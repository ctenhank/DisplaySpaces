# Architecture

The product and executable are named `DisplaySpaces`, previously BetterVirtualDesktop. Internal compatibility identifiers, including the local storage directory, window recovery properties, and singleton mutex, retain `IndependentVirtualDesktop`. This preserves language preferences and recovery records from earlier versions and prevents old and new versions from running at the same time.

## Installation and notification area history

The stable installation path is `%LOCALAPPDATA%\Programs\DisplaySpaces\DisplaySpaces.exe`. `scripts\build.ps1 -Install` creates a self-contained build and installs it through `scripts\install.ps1`. The installer validates the application payload, runtime notices, and installation ownership marker. It stages and replaces the installation under the same parent directory and restores the previous installation if replacement fails. It refuses unrelated directories, reparse points, incomplete payloads, and an installation that is running. `-Launch` also refuses to start another application or recovery watcher instance.

The installer does not modify user preferences or the startup registry entry. If startup was registered from an older location, update it from the installed application. Keeping one executable path avoids accumulating notification area entries for different build directories. Microsoft documents that a notification icon registered by GUID is also associated with the executable's full path: [NotificationIcon sample](https://learn.microsoft.com/en-us/windows/win32/shell/samples-notificationicon).

When upgrading from BetterVirtualDesktop, exit the old app through **Exit · Restore all windows**, install DisplaySpaces at its new stable path, and launch it. If automatic startup is desired, enable **Start with Windows** again under **Settings → General** in DisplaySpaces to register the new executable location. Existing preferences continue to use `%LOCALAPPDATA%\IndependentVirtualDesktop`; the upgrade does not require moving or resetting that folder.

An investigation on October 20, 2026 found one application process and one recovery watcher, but ten historical entries in `HKCU\Control Panel\NotifyIconSettings`: five for the previous BetterVirtualDesktop paths and five for IndependentVirtualDesktop paths. These application-specific entries were backed up to `artifacts\tray-maintenance\notify-icon-history-backup.json` and removed. Starting the stable installation left one current entry and no legacy entries. Other applications' entries were untouched. The old process exited through its normal message loop and restored its two hidden windows. Startup registration and a Start menu shortcut were updated; the settings file's SHA-256 remained unchanged.

Legacy-only `publish`, `overlay-publish`, `new-desktop-publish`, `branded-publish`, `taskview-publish`, and `localized-publish` output directories, plus 137 legacy-named build output files, were removed. The local removal manifest is `artifacts\tray-maintenance\removed-legacy-files.json`. Historical artifact paths below describe previous validation runs, not recommended installation locations. Compatibility settings and recovery data remain in place.

Run installer checks with `scripts\test-installer.ps1`. Its 22 checks use temporary workspace files and simulated processes, covering initial installation, updates at the same path, incomplete payload rejection, unrelated directory protection, current and legacy running process checks, rollback, and preference preservation. The previous BetterVirtualDesktop installation was also checked against the assembly hash of the validated `desktop-reorder-publish` build.

## Workspace model

`DisplaySpaces.Core` is a .NET 10 library without Windows API dependencies. `WorkspaceCatalog` owns each display's desktop list and active desktop ID, together with window assignments. A window is logically visible when its assigned desktop matches its display's active desktop.

| Model | Contents |
| --- | --- |
| `DisplayDescriptor` | Display ID, name, and primary display flag reported by the OS |
| `WindowAssignment` | Window handle, display ID, and desktop ID |
| `DesktopSnapshot` | Desktop ID, name, and assigned window IDs |
| `DisplaySnapshot` | Display information, active desktop ID, and desktop list |

Desktop IDs are `Guid` values; window IDs are `long` values corresponding to `IntPtr.ToInt64()`. Zero window IDs are rejected, while negative nonzero values are accepted to preserve signed pointer representations. Snapshots are read-only data detached from the internal collections. Catalog access is serialized by a lock.

Automatically assigned names carry `DefaultNumber` metadata and are formatted in the current UI language. User-defined names are stored verbatim. A name the user explicitly sets to `Desktop 1`, for example, is not translated when the language changes.

`ReorderDesktop(displayId, desktopId, targetIndex)` inserts an existing desktop at its final zero-based position and shifts intervening entries. Adjacent swaps and moves across several positions use the same path. After validating the display, desktop, and index, it changes only the list order. GUIDs, names, default name numbers, window assignments, and the active ID remain unchanged. Moving to the same position is a no-op. Invalid requests throw in the catalog and return `false` through the UI controller. The controller emits one `Changed` event only when the order changes; it does not update window visibility or the switching generation.

Reordering affects only the specified display, even in Linked mode. Subsequent previous/next navigation, linked position matching, relative window moves, and first/last notifications use the new order. Moving `Desktop 2` to the first position leaves its name as `Desktop 2`, with a displayed position of `1/N`. Like the rest of the workspace configuration, order lasts for the current application session.

Display synchronization validates the entire input before applying it. Invalid input, such as an empty list or duplicate IDs, leaves the current state intact. Displays that remain connected retain their desktops; a newly connected display starts with one desktop. Windows from a disconnected display move to the remaining primary display's active desktop, or the first display in the input if none is primary.

`TrackWindow` registers newly discovered windows without changing the assignments of known windows. `AssignToDisplay` handles windows the user moves manually between monitors. Explicit display and desktop moves use `MoveWindowsToDisplay`, validating the whole window group before updating its assignments together. These moves do not change any display's active desktop. Deleting a desktop moves its windows to the previous desktop, or the next desktop when deleting the first. Deleting a display's only desktop is rejected.

`SwitchRelative` wraps in either direction by default. With `wrapAround: false`, it stops at the first and last desktops. Both paths use `long` intermediate arithmetic to support the full `int` offset range. The **Wrap desktop switching** setting applies to previous/next desktop shortcuts and is enabled by default. `DesktopNavigationPreferences` persists it as `wrapDesktopSwitching` in `settings.json`, preserving other settings. If relative switching produces no change, the controller leaves visibility and the switching generation untouched. An attempt to move past an end with wrapping disabled emits `SwitchBoundaryReached` to request a name notification only.

`DisplaySwitchingMode` supports `Independent` (the default) and `Linked`. Both `SwitchToDesktop` and `SwitchRelative` accept the mode. The controller's `SwitchingMode` is shared by shortcut, overlay, management window, and taskbar activation paths. Independent mode changes only the selected display's active ID. Linked mode aligns every display with the selected desktop's position on the reference display, creating empty desktops where necessary. Existing names, window assignments, and additional desktops on other displays are preserved.

Linked relative navigation uses the largest desktop count across all displays and the active position on the display under the pointer. A zero move or a move blocked at an end does not align other displays or create desktops. Explicitly clicking the already active card can align other displays with that position. Linked changes occur under the catalog lock, followed by one visibility update and one `Changed` event for all displays. Deleting an active desktop aligns other displays with the resulting active position, without deleting their desktops.

The **Display switching mode → Independent / Linked** setting uses `DisplaySwitchingPreferences` to persist `displaySwitchingMode` in `settings.json`. Missing or invalid settings fall back to Independent. Changing the mode itself does not alter window placement or active desktops. A selection is not applied if saving fails.

## Windows window management

`DisplaySpaces.App` is a .NET 10 Windows Forms application. It manages application windows as follows:

1. A 500 ms timer in `DesktopApplicationContext` calls `DesktopController.Refresh`.
2. `Screen.AllScreens` synchronizes displays, while `NativeWindows.EnumerateWindows` collects normal application windows and their owned dialogs.
3. New windows are registered only when visible and on the current native Windows virtual desktop. The application's own windows, shell windows, and standalone tool windows are excluded.
4. Monitor changes and ownership relationships are applied. Owned dialogs and auxiliary windows follow their owner's workspace.
5. The catalog's logical visibility state is applied through `ShowWindowAsync` show/hide requests.

The public `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop` API is used only to check whether a window is on the current native desktop. DisplaySpaces does not create or switch native Windows virtual desktops or modify Explorer's Task View. Its desktops are separate workspaces implemented through window assignments and visibility.

A window is identified by its handle, process ID, process start time, and window class. Before hiding a window, the application assigns a random recovery property through `SetProp`. If process information cannot be read or `SetProp` is denied, the window is not hidden. If any part of an owned window group cannot be recorded safely, the entire group remains visible. Full control of elevated windows from an application running without elevation is therefore not assumed.

`ShowWindowAsync` submits a request; its return value alone does not establish successful recovery. Recovery records remain until a subsequent visibility check confirms the result. Originally minimized windows recover in their minimized state. Normal and maximized windows use a show operation that retains their stored placement. Window activation remains subject to Windows foreground activation rules.

`DesktopController.MoveWindowToDisplay` validates the destination display, desktop, and window identity before moving the full owner/dialog group. An inactive destination requires recovery records to be prepared first. `NativeWindows.MoveGroupToDisplay` transforms the normal placement returned by `GetWindowPlacement` into the destination work area and submits asynchronous, nonactivating `SetWindowPlacement` requests. Maximized windows also have their current maximized bounds moved to the destination monitor. The code preserves minimized/maximized state and relative group placement, scaling the group down together when needed to fit a smaller work area. It distinguishes normal windows' work area coordinates from tool windows' screen coordinates.

A successful move API return means the request was accepted. Pending moves are tracked briefly until arrival is observed so `Refresh` does not misinterpret old coordinates as the user moving the window back. Missing destinations and stale window identities are rejected. If some native requests fail, the controller attempts to restore already submitted placements. Acceptance cannot guarantee completion by every external application, which may close or stop responding.

**Follow window to its desktop** is an optional behavior for shortcuts that move a window to the previous or next desktop. It is off by default and stored as `followMovedWindow` by `WindowMovePreferences`. `MoveForegroundRelative` reads the foreground HWND and passes it to `MoveWindowRelative`, which calculates the destination from the owner's actual assignment. Relative window moves retain their wrapping behavior and use `long` offset arithmetic. Requests with a single desktop, zero offset, a full-cycle offset, invalid window, or pending monitor move are rejected as ineffective.

When following is enabled, and the application is neither paused nor in a recovery protection failure state, the validated group assignment and desktop selection for `SwitchingMode` are updated together before one visibility update and one `Changed` event. The moved group remains visible on the final active desktop, without temporary hiding or forced activation. Owners and their dialogs move together. Explicit destination moves and moves to newly created desktops do not switch desktops, even when this option is enabled.

## Selecting windows from the taskbar

`TaskbarDesktopCoordinator` uses the public `ITaskbarList.AddTab` API to re-register normal windows that the application has hidden on inactive desktops and for which it holds recovery records. It uses the original HWND rather than proxy windows and does not change other applications' styles or AppUserModelIDs. Windows retains its app grouping and individual window selection behavior. Owned dialogs and standalone tool windows are not added as duplicate buttons.

Registration waits at least 80 ms after a hide request and requires confirmation that the window is hidden. Already hidden windows do not receive repeated `SW_HIDE` requests, except when an earlier asynchronous show request must be followed by a hide request to ensure the final requested state. `EVENT_OBJECT_HIDE` also repairs registrations lost through show/hide transitions between polls; this event never switches desktops. `TaskbarCreated` restores registrations after Explorer recreates the taskbar.

`ForegroundWindowHook` uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)` to receive the actual selected window and event time. It does not infer a destination from a process name or choose a process's first window. On the UI thread, it validates the current foreground HWND, process ID, start time, class, native Windows desktop, and recovery record. It then opens the window's assigned desktop using the selected display switching mode. Independent mode switches only that monitor. Multiple Chrome windows sharing one process each follow their own HWND assignment. A minimized window restored by the user is not minimized again.

Show and notification events alone never trigger a switch. Generation and time checks reject delayed events from before a move, switch, or pause. Focus changes during an asynchronous hide, or before hide completion was confirmed, are also ignored. This prevents Windows' automatic focus changes during hiding or moving from switching back to the previous desktop. Following is suspended while paused or while Settings or the shortcut editor is open.

Once a window is restored to normal visible operation, taskbar ownership returns to the shell without deleting its normal button through `DeleteTab`. Manual registrations for closed hidden windows are removed, while live HWNDs whose identity has changed are left untouched. Hooks and temporary registrations are cleaned up with recovery during shutdown. The contents of hidden window previews and the window chosen by a single click on a grouped icon are controlled by Windows and the original application. Live taskbar thumbnails are not guaranteed.

API references: [ITaskbarList.AddTab](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist-addtab), [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), and [Windows taskbar](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar).

## Task View and input

`MoveWindowToNewDesktop` validates the destination monitor and window group, creates an inactive desktop, and uses the existing move path. It prepares recovery records for the entire group even for a move within one monitor. If the move is rejected immediately, it removes only the empty inactive desktop created by that call. Context menus and drops onto the new desktop card share this path. Successful moves preserve every monitor's active desktop and overlay preview selection.

After creation, `ShowCreationFeedback` displays a large confirmation on the destination overlay for approximately 3.2 seconds and highlights the new card's border when visible. The message names the new desktop without changing the preview selection or card scroll position. The user can explicitly choose **Open desktop** to switch to an inactive desktop. `Enter` continues to open the currently previewed desktop. The top title identifies the preview selection, with the actual active desktop shown separately beneath it. A UI timer drives the feedback animation; dismissing it does not affect catalog state or window assignments.

On a move request, `PreservePreviewAfterMove` cancels pending clicks and consumes the mouse release immediately after the drop. Hover does not change the preview until the pointer leaves the desktop list, preventing a pointer resting on the destination card, or small pointer movements, from following the moved window. Explicit card clicks and arrow key selection remain available. Cross-monitor moves preserve the preview selection on each screen.

Dragging a desktop card draws an insertion indicator between cards on the same display. Card reordering uses a distinct data format, per-form session, and per-drag token from window dragging. It also verifies that the desktop ID order still matches the order at drag start. Cross-display data, other sessions, completed drags, and changed lists are rejected. A drop submits the final insertion position once and preserves the preview and active IDs. Cancellation, list changes, and form disposal clear the insertion indicator and pressed state. Card context menus and the `…` menu offer **Move left / Move right**, disabling directions at their respective ends.

The main Task View consists of one borderless, topmost overlay per connected display. Each covers its entire screen, showing previews of the selected desktop's windows in the center and that display's desktop cards along the bottom. Window layouts preserve aspect ratios, while desktop cards compose scaled cached window images at their corresponding positions. The active desktop and preview selection are separate: hovering a card or using arrow keys changes the preview without switching desktops.

Normal application startup, `Win + Alt + Tab`, double-clicking the notification area icon, and the overlay menu open all overlays. Clicking a desktop card or pressing `Enter` switches according to the configured display switching mode, then closes every overlay. Clicking a window preview switches to its desktop, requests window activation, and closes every overlay. `Esc` or the Task View shortcut again closes all overlays. Keyboard focus follows the overlay entered by the pointer. Overlays also support creating, renaming, and deleting desktops. Delayed selection events from a closed overlay are rejected by a later overlay session.

Window previews can be dragged to desktop cards on the same or another display. Dropping in the central window area targets the desktop previewed by that overlay. Drag data includes the current overlay session to reject old data. `OverlayCoordinator` passes the display, desktop, and window information to the controller. Successful moves leave the overlays open and preserve each monitor's active desktop. Window context menus list desktops under their destination displays and use the same move path.

`OverviewForm` remains available through **Manage desktops** in the notification area. It opens as one window on the monitor under the pointer, with a row per display, cards for its desktops, and window titles. A desktop title's context menu supports rename, move left/right, and delete. A window title's menu selects a destination display and desktop. Reorder commands recalculate the current position when invoked, so changes made after the menu opened do not make the command act on an outdated neighbor.

`KeyboardHook` runs a low-level keyboard hook on a separate message-loop thread. `KeyboardShortcutState` interprets physical key press/release state and emits commands to the UI thread. Injected input is excluded from shortcut matching. The hook callback does not enumerate windows or write recovery files.

The default profile passes `Win + Tab` to Windows and opens this application's Task View with `Win + Alt + Tab`. `Win + Ctrl + ← / →`, `Win + Ctrl + D`, and `Win + Ctrl + F4` switch, create, and remove desktops relative to the monitor under the pointer. `Win + Ctrl + Shift + ← / →` moves the foreground window from its current assignment, following it only if **Follow window to its desktop** is enabled. `Win + Ctrl + Shift + F12` requests recovery of all windows and pauses window management. Selecting a window in Task View also opens its desktop, subject to Independent/Linked mode.

Shortcut settings are accessible directly from the overlays and management window, and through **Settings… → Shortcuts → Keyboard shortcuts…** from the notification area. Each command can use a regular key with Win/Ctrl/Alt/Shift, or be disabled. The Windows shortcuts preset assigns `Win + Tab` to this application's Task View. Changes remain a draft until saving succeeds, then apply immediately to the hook. Duplicate combinations, unmodified regular keys, and certain protected system combinations are rejected. This does not detect every possible conflict with other applications.

`ShortcutProfile` is immutable, and the UI and hook threads synchronize input state changes. Keys already consumed by the application continue to have their repeats and releases handled across profile changes, preserving matched input pairs. Opening settings suspends new shortcut commands; the new mapping takes effect on fresh key presses after settings close. Pending commands retain the profile generation at matching time so old input cannot execute after settings close. The editor uses modifier checkboxes and a key list rather than recording keyboard input.

Pause changes only the window visibility policy. The keyboard hook and configured shortcuts remain active until the application exits. Assigning `Win + Tab` to the application opens its own Task View while running. Closing an overlay or the management window hides that UI while the application continues running in the notification area.

## Unified settings

The notification area menu keeps the overlay, desktop management, **Settings…**, pause/resume, restore-and-pause, and exit actions. Preference controls live in one `SettingsForm`, organized into **General**, **Desktops**, **Appearance**, and **Shortcuts** tabs. General contains language and startup; Desktops contains display switching mode, wraparound, and following moved windows; Appearance contains desktop name display mode and corner. The Shortcuts tab opens the existing editor, retaining its **Save changes** and **Cancel** workflow.

The settings window is modeless and has one instance per application. Reopening it activates the existing window and restores it if minimized. Opening it dismisses overlays and hides desktop management. New shortcut commands and taskbar-triggered desktop switching are suspended until it closes; desktop name notifications are also temporarily hidden. Opening the nested shortcut editor keeps this suspension in place until both windows close.

`SettingsBindings` delegates reads and writes to the existing preference stores and controller setters; it does not introduce a second settings file. Each change saves and applies immediately. Save errors appear inline, and the controls refresh from the retained application state. Language changes update the window in place. The desktop name corner selector is disabled when name display is Off. Native controls, wrapping text, scrolling categories, and work-area fitting support keyboard navigation, localized text, and smaller displays.

## Localization and preferences

`ShortcutPreferences` reads and writes `keyboardShortcuts` in the existing `settings.json`, preserving language and other fields. Saves replace the original with a temporary file. Invalid values or incomplete mappings fall back to the full default profile. Save failures leave current shortcuts unchanged and display an error in the settings window. Shortcut names, validation messages, and settings UI support all five languages.

`UiText` and `UiTextResources` provide strings and fonts for Korean (`ko`), English (`en`), Simplified Chinese (`zh-Hans`), Traditional Chinese (`zh-Hant`), and Japanese (`ja`). The default follows the Windows display language, with English as the fallback for unsupported languages. Task View's language menu and **Settings → General → Language** use native language names and include a system-language option. The selection is saved locally to `%LOCALAPPDATA%\IndependentVirtualDesktop\settings.json`, separately from the workspace configuration that lasts only for the application session.

Language changes redraw tray menus, Settings, the management window, and open overlays. Visible desktop name notifications update their content without extending their expiration time. Language changes do not rediscover the catalog or invoke window hiding or movement, so active desktops and window assignments remain intact. `DesktopName` formats only automatic names; `DisplayName` formats physical display labels. User-defined desktop names and external window titles remain verbatim. Test and fixture runs explicitly select Korean without reading or overwriting the user's saved preferences.

## Start with Windows

The **Start with Windows** setting is off by default and registers the current executable when enabled. `StartupRegistration` uses the `DisplaySpaces` value under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It stores a `REG_SZ` command in the form `"<absolute executable path>" --startup`. Spaces and non-ASCII characters in paths are preserved; host executables, relative paths, and overlong commands are rejected. Execution occurs after sign-in and may be delayed by Windows, as described in the [Run key documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

When no current registration exists, startup status can recognize the earlier `BetterVirtualDesktop` and `IndependentVirtualDesktop` values. Recognition requires a string containing an exact quoted absolute executable path, the matching legacy executable filename, and `--startup`, with the same path and length validation. Explicitly enabling startup writes the current `DisplaySpaces` command and removes recognized legacy entries; explicitly disabling it removes the current value and recognized legacy entries. Foreign or malformed values under the earlier names, other applications' entries, and the Run key itself are preserved.

`--startup` prepares the tray, shortcuts, and recovery watcher without opening an overlay. If an instance is already running, it exits silently. A normal direct launch opens the overlay. Registration state is read from the registry when Settings opens or regains focus, rather than duplicated in JSON. An entry pointing elsewhere is reported and updated to the current path when the user enables startup again. Launching the app, opening Settings or menus, or changing language never modifies registration.

The checked **Start with Windows** box under **Settings → General** indicates registration of the current path. Windows **Settings → Apps → Startup** may independently block execution; the application does not override that setting. `StartupSelfTests` use temporary GUID-specific test keys instead of the real Run key to check registration, removal, external changes, path updates, and localized controls, then clean up those keys. Automated validation does not include a real sign-out or restart.

## Window previews and pointer interaction

`DisplayOverlayForm` identifies input targets by window handle, desktop ID, and button kind, not by callback objects recreated during painting. An action occurs only if press and release target IDs match. Window list rearrangement or timer refreshes therefore cannot redirect a click to another item. Pointer exit clears hover and pressed rendering. Loss of focus, mouse capture, or the target itself cancels pending clicks. Starting a drag clears pressed state, and the subsequent mouse release is not treated as a click.

Hover emphasis transitions over approximately 0.12 seconds on a 16 ms timer, which stops when the state is stable. Pressed feedback is immediate. Margins and title areas remain outside DWM's window content so borders and press feedback remain visible around live previews. Repainting checks the current pointer against the new layout only to update emphasis; preview selection changes occur only through actual pointer movement or keyboard input.

`DwmThumbnailSession` uses `DwmRegisterThumbnail` and `DwmUpdateThumbnailProperties` for visible, non-minimized windows. DWM composites the source window's client area directly into the overlay's top-level HWND. Registrations are removed when the gallery changes or the overlay closes. This composition is not included in GDI `DrawToBitmap` output. The application neither modifies native Windows Task View nor injects code into other applications.

`WindowPreviewCache` captures visible normal windows through `PrintWindow` on a separate worker thread. Hidden or minimized windows reuse a previously captured image; they are never shown merely to obtain a preview. Images are discarded if the process ID, start time, or window class changes, or the window disappears. Protected content, unresponsive windows, unsupported captures, and black frames are not cached. When no usable image exists, the title and an explanatory message remain visible.

Images are stored only in memory. The retained cache is limited to 48 images and 12 million pixels total, with another 12 million pixels allowed for completed images awaiting processing. Each cached image is scaled to at most 960×600, and a capture source is limited to 16 million pixels. Requests for the same window are spaced at least one second apart. Because `PrintWindow` can stall inside another process, there is only one capture worker, and neither the UI nor shutdown waits for it. One application may delay all snapshot updates, but cannot block the UI thread or cause unbounded worker creation.

Hidden window previews show the last captured frame, not live content. Native Windows Task View transition animations, per-monitor wallpapers, and independent taskbars are outside the implementation's scope.

## Desktop name notifications

`DesktopNameToastCoordinator` compares each display's active desktop ID when the controller reports changes. The **Show desktop names** setting offers Off, When switching, and Always. The default When switching mode shows `DesktopNameToastForm` on the affected display for approximately 1.8 seconds, fading out over the last 0.2 seconds. Rapid switching updates the latest name; window list refreshes do not extend the duration. This mode does not notify at startup or when reselecting the same desktop.

Always mode also displays the active name at startup, after adding a monitor, and after resuming. Names and language changes update the contents. Notifications hide while Task View, the management window, Settings, or the shortcut editor is open to avoid covering controls, then return when those windows close. Switching to Off cancels pending notification requests as well.

First and last desktops include an additional boundary message in the accent color. A single desktop is identified as both first and last; middle desktops omit the line. These messages are localized in all five languages, included in accessible names, and recalculated after desktop creation or deletion. A blocked navigation attempt with wrapping disabled shows the message for approximately 1.8 seconds even in When switching mode. Off and paused states suppress it. Requests during Task View are handled after it closes.

In Linked mode, notification counts and the last position use the largest desktop count across monitors. A monitor with a shorter local list therefore does not report the last desktop while navigation can still advance to another linked position.

Notifications appear at the top right of the monitor's work area by default. **Desktop name position** also offers the top left. `DesktopNamePreferences` saves the mode and position in the existing `desktopName` section of `settings.json`, preserving language and shortcuts. If saving fails, the prior selection remains and an error is shown. `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, transparent layered-window styles, and nonactivating display keep notifications out of the taskbar, preserve keyboard focus, and let clicks pass through. Placement accounts for per-monitor DPI and negative screen coordinates. Notifications are disposed on pause, exit, or monitor removal.

## Shutdown and recovery

`RecoveryJournal` writes recovery information before a window is hidden. It writes a temporary file, flushes it to disk, and replaces the original. A window whose recovery record cannot be committed is not hidden. This journal is separate from persistence of the workspace configuration.

`ProcessLauncher` starts a recovery watcher using the same executable's `--watch` mode. The watcher identifies the application by process ID and start time, detects its exit, and attempts recovery from its journal. The main application also checks orphaned journals from previous runs at startup. Normal exit, a user recovery request, and runtime error handling all request recovery. If the watcher exits first, the main application restores windows and pauses.

Recovery requires matching process identity and window properties, protecting against applying stale operations to closed windows or reused handles. `--recover` does not take journals owned by another live instance. Records for windows that could not be recovered remain available for a later attempt.

Desktop counts, names, order, and window assignments exist only in memory. Restarting the application starts each monitor with one desktop.

## Validation

`DisplaySpaces.Core.Tests` is a console test runner without external test packages. It checks monitor independence, reassignment after disconnects, desktop deletion and reordering, window movement, wrapping and bounded navigation, atomic input validation, and snapshot isolation. Failure returns a nonzero exit code.

The application's `--self-test <output directory>` creates dedicated test windows to exercise Windows APIs and render overlays and the management window. It does not install the real keyboard hook or use existing user windows as test targets. With `artifacts\tests` as output, results are written to `smoke-results.txt`; overlays to `overlay-primary.png`, `overlay-2.png`, `overlay-compact.png`, and `overlay-hidden-desktop.png`; and the management window to `overview.png`. An interactive Windows desktop session is required because test windows appear briefly.

Focused test commands use isolated settings, controllers, forms, or fixture processes:

| Command | Coverage |
| --- | --- |
| `scripts\test.ps1 -SettingsOnly` | Unified settings persistence, read-only opening and refresh, failed-save rollback, unavailable startup controls, shortcut editor entry, reopen/disposal behavior, and all four tabs in five languages at normal and compact sizes |
| `scripts\test.ps1 -DesktopNamesOnly` | Display modes, pending notification cancellation, pause/resume, preference persistence and save failure, wrapping, boundaries, localized boundary messages, and desktop count changes |
| `scripts\test.ps1 -DisplaySwitchingOnly` | Preference persistence and rollback, Independent/Linked behavior, unequal desktop counts, boundary notifications after mode changes, and real fixture-window visibility and assignments across two monitors |
| `scripts\test.ps1 -MoveFollowOnly` | Follow preference and menus, Independent/Linked moves, owner/dialog groups, no temporary hiding of followed groups, one change event, paused and recovery-failure behavior, and explicit moves that preserve the current desktop |
| `scripts\test.ps1 -DesktopReorderOnly` | Controller and menu commands, overlay drag receivers, identity and assignment preservation, one change event, navigation in the new order, boundaries, stale/cross-display data, cancellation, and separation from window dragging |
| `scripts\test.ps1 -MovesOnly` | Existing/new desktop moves, post-drop pointer and release handling, preserved active desktops, previews and scroll positions after snapshot/language updates, explicit card navigation, and creation feedback's Open desktop action |
| `scripts\test.ps1 -StartupOnly` | Startup registration through temporary registry keys without modifying real startup configuration |
| `scripts\test.ps1 -TaskbarOnly` | Individual windows of one process, owner dialogs, minimized windows, another monitor, paused/stale events, pending hides and moves, real taskbar registration, and fixture-process foreground events |
| `scripts\test-installer.ps1` | Installation, update, rejection, rollback, and preservation checks using temporary payloads and simulated processes |

Settings checks use an isolated JSON file, injected startup state, and dedicated forms. They do not change the real Run registry or user windows. Focused results go to `settings-results.txt`; `settings-<language>-<tab>.png` and corresponding `-compact.png` files record each tab's rendering.

Desktop name integration checks also verify display-specific notifications, preserved focus, work-area placement, top-left positioning, rapid switching, renaming, and expiration despite refreshes. Display switching tests invoke direct-selection and relative-navigation controller paths against separate `--fixture` process windows. They do not automate actual user key presses or mouse clicks. Physical multi-monitor checks report `SKIP` on a single-monitor system.

Move-follow checks pass fixture HWNDs to the internal relative move path without manipulating the user's foreground window or global input. Reordering uses controllers without real user windows and hidden forms. `desktop-reorder-drag.png` and `desktop-reorder-after.png` record the insertion indicator and final layout. These checks do not drive the full OLE drag loop using real mouse input. Drag destinations are currently visible cards; left/right menu commands can move a desktop beyond the visible area.

Taskbar tests may require activation of a temporary **DisplaySpaces taskbar activation test** window to satisfy Windows foreground permissions. Automated selection calls the fixture windows' show/activation APIs rather than clicking the real taskbar. A manual user check confirmed that selecting a test window opened it. Actual thumbnail clicks across multiple Chrome profiles and other applications, plus Explorer restarts, remain separate real-world checks.

Earlier integration runs validated one overlay per physical monitor, exact screen bounds, monitor-specific cards and windows, focus following pointer entry, browsing previews without switching, closing all overlays with `Esc` or the toggle, rejecting delayed events from an earlier session, and Independent mode leaving other monitors' windows unchanged. Cross-monitor fixture tests covered owned dialog groups, preserved active desktops and assignments after refresh, hiding on inactive destinations and recovery after switching, minimized/maximized state, return moves, invalid destinations, and watcher recovery after forced termination.

Preview checks verified DWM registration and cached image creation on each screen, including that browsing an inactive desktop leaves the original windows hidden. Injecting drag events into receivers verified cross-monitor moves while preserving overlays and active desktops and rejecting previous-session data. Shortcut logic checks covered default `Win + Alt + Tab` matching, native `Win + Tab` pass-through, the Windows preset, custom combinations, profile changes while keys are held, pause/resume, release ordering, settings persistence, language preservation, editor save/cancel/failure paths, duplicate errors, and minimum-size rendering in all five languages (`shortcuts-*.png`).

The following records describe completed development validation runs. Counts from earlier versions are not claims that the current full integration suite was rerun.

| Build or output directory | Recorded validation |
| --- | --- |
| `artifacts\desktop-reorder-publish` | Release build with no warnings or errors; 29 core checks, 70 reorder checks, and 6 move-preview preservation groups. The published build also passed all 70 reorder checks, with insertion and final-order images reviewed. Results: `artifacts\desktop-reorder-publish-tests\desktop-reorder-results.txt`. No real user windows or mouse input were manipulated; the full application suite was not rerun for this change. |
| `artifacts\move-follow-publish` | 23 core checks, 48 move-follow checks, and 6 move-preview groups. The published build passed all 48 follow checks using fixture windows and owned dialogs on two monitors, with no skips. Results: `artifacts\move-follow-publish-tests\window-move-follow-results.txt`. |
| `artifacts\display-switching-publish` | 23 core checks, 106 desktop name/boundary checks, and 54 display switching checks. The published build also passed 54 switching checks. Results: `artifacts\display-switching-publish-tests\display-switching-results.txt`. |
| `artifacts\bounded-desktops-publish` | Korean/English first, last, and single-desktop rendering images: `artifacts\bounded-desktops-publish-tests\desktop-boundary-*.png`. |
| `artifacts\taskbar-publish` | 12 core checks and 260 application self-tests, including 21 startup checks, 6 move-preview groups, and 22 taskbar/individual-window activation checks. Full results were written to `artifacts\tests\smoke-results.txt`; the published build passed its 22 taskbar checks at `artifacts\taskbar-publish-tests\taskbar-results.txt`. |
| `artifacts\move-stay-publish-tests` | All 6 move-preview preservation groups passed in the published build. Results: `move-results.txt`. |

An earlier report that all screens switched together prompted checks of the default controller path, executable path, and saved shortcuts. The default code switched only the selected monitor, and previous/next shortcuts were enabled. The reported input sequence's cause was not reproduced or established. Direct controller checks therefore do not establish the cause or resolution of that specific report.

Manual verification is still needed for a complete mouse drag across monitor boundaries, real global shortcut input and its interaction with native Windows Task View, movement and capture across diverse external applications, protected content, and physical monitor disconnect/reconnect. Automated coverage includes pure logic, preference storage, UI, and real Windows API integration, but does not generate global keyboard input. Localized rendering images are stored under `artifacts\tests` and icon inspection images under `artifacts\icon-check`. Running installations are not overwritten.

## Application icon and runtime notices

`Assets/DisplaySpaces.ico` is included both as a Windows executable resource (`ApplicationIcon`) and as a managed `EmbeddedResource`. `AppIcon` loads shared window and tray sizes from the embedded resource once, without depending on the working directory or external image files. Shared icons live for the process lifetime even when consuming forms or the tray icon close. The ICO contains ten sizes from 16 to 256 pixels with an alpha channel.

`build.ps1 -Publish` runs `copy-runtime-notices.ps1` after publishing. It reads the included .NET runtime version and copies the corresponding NuGet package's license and third-party notices, unmodified, into the published `licenses` directory. These runtime notices are separate from the application's own license choice.
