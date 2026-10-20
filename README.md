# DisplaySpaces

<img src="src/DisplaySpaces.App/Assets/DisplaySpaces.png" width="96" alt="DisplaySpaces icon" />

**Independent virtual desktops for every monitor on Windows 11.** Switch workspaces on one monitor while leaving the others unchanged, or choose linked switching to move all monitors together.

## Features

- **Desktops per monitor:** Create, switch, rename, and delete desktops on each display.
- **Desktop ordering:** Drag desktop cards or move them left and right from a context menu. Names and windows stay with their desktops.
- **Independent or linked switching:** Choose whether shortcuts and desktop selections affect one monitor or all monitors.
- **Task View overlay:** Browse desktop cards and window previews on every monitor.
- **Window movement:** Move windows between desktops and displays using drag-and-drop, context menus, or shortcuts. Moving a window keeps your current view by default; previous/next move shortcuts can optionally follow it.
- **Taskbar switching:** Select an app icon or window preview to open that window's desktop. Multiple windows from the same app are handled individually, using your selected display switching mode.
- **Clear feedback:** Creation and movement notifications, highlighted cards, and configurable desktop name indicators.
- **Five languages:** English, Korean, Simplified Chinese, Traditional Chinese, and Japanese. Language changes apply immediately and are saved.
- **Custom shortcuts:** Change or disable individual key bindings, use a Windows shortcuts preset, and save your preferences.
- **Optional wraparound:** Stop at the first and last desktops or loop to the opposite end.
- **Optional startup:** Run in the background when you sign in to Windows.
- **Unified settings:** Configure language, startup, desktop switching, name indicators, and shortcuts from one settings window.
- **Window recovery:** Restore hidden windows when exiting, with a separate recovery process for unexpected shutdowns.

## Download and run

Download `DisplaySpaces-1.0.0-win-x64.zip` from [GitHub Releases](https://github.com/ctenhank/DisplaySpaces/releases/latest), extract the entire archive, and run `DisplaySpaces.exe`.

Version **1.0.0** requires **Windows 11 x64**. The release includes the .NET runtime; you do not need to install the SDK or runtime separately. Keep the extracted files together and reuse the same folder when updating.

For a stable installation path, run this command from the extracted folder.
Before installing or updating, exit any running copy through **Exit · Restore all windows** in its tray menu.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -PublishDirectory . -Launch
```

This installs the app under `%LOCALAPPDATA%\Programs\DisplaySpaces` and starts it in the background. The execution-policy option applies only to this PowerShell process.

**Upgrading from BetterVirtualDesktop:** Exit the old app through **Exit · Restore all windows**, then install DisplaySpaces using the command above. If you use automatic startup, enable **Start with Windows** again under **Settings → General** in DisplaySpaces to register the new executable location. Your preferences remain in `%LOCALAPPDATA%\IndependentVirtualDesktop` and are reused automatically.

## Default shortcuts

| Action | Shortcut |
| --- | --- |
| Show / hide the overlay on all monitors | `Win + Alt + Tab` |
| Previous / next desktop, using the monitor under the pointer | `Win + Ctrl + ← / →` |
| Create and open a desktop | `Win + Ctrl + D` |
| Delete the current desktop | `Win + Ctrl + F4` |
| Move the current window to the previous / next desktop | `Win + Ctrl + Shift + ← / →` |
| Restore all windows and pause | `Win + Ctrl + Shift + F12` |

In the overlay, use `← / →` to preview desktops, `Enter` to switch, and `Esc` to close. By default, `Win + Tab` continues to open Windows Task View.

Open **Settings… → Shortcuts → Keyboard shortcuts…** from the system tray menu to change or disable bindings. The overlay and desktop management window also offer **Keyboard shortcuts…** directly. Choose the **Windows shortcuts** preset and save to assign `Win + Tab` to this app's overlay while it is running. Saved changes apply immediately and persist between runs. Duplicate bindings and unsupported system shortcuts are reported before saving.

## Settings

Right-click the DisplaySpaces system tray icon and choose **Settings…**. Options are grouped into four tabs:

| Tab | Options |
| --- | --- |
| **General** | Language and **Start with Windows** |
| **Desktops** | **Display switching mode**, **Wrap desktop switching**, and **Follow window to its desktop** |
| **Appearance** | **Show desktop names** and **Desktop name position** |
| **Shortcuts** | Open **Keyboard shortcuts…** to edit bindings and choose presets |

Changes in Settings are saved and applied immediately. The separate shortcut editor uses **Save changes** and **Cancel**. DisplaySpaces shortcuts and taskbar desktop switching pause while Settings is open; close Settings to use them again. Desktop name indicators are also hidden while Settings is open.

## Using desktops

**Reorder desktops:** Drag a desktop card at the bottom of the overlay and drop it at the insertion marker. You can also choose **Move left** or **Move right** from the card's right-click or `…` menu, or by right-clicking a desktop heading in the management window. Cards stop at the first and last positions.

Names belong to desktops, not positions. Moving Desktop 2 to the front of `Desktop 1 · 2 · 3` produces `Desktop 2 · 1 · 3`. Its windows stay with it, and your active desktop stays open. Names such as **Work**, **Personal**, and **Games** remain easy to recognize after reordering. Previous/next navigation and first/last indicators follow the new order.

**Choose a display switching mode:** In **Settings → Desktops**, set **Display switching mode** to **Independent** (default) or **Linked**. Independent switching affects the monitor under the pointer for shortcuts, or the monitor whose desktop card you click. Linked switching opens the same desktop position on every monitor, creating empty desktops where needed. For example, selecting the third card opens each monitor's third desktop. Each monitor keeps its own names and window assignments.

The mode takes effect on the next switch and is saved between runs. In Linked mode, previous/next navigation uses the largest desktop count across all monitors. Taskbar selection and creating and opening a desktop also follow the selected mode. Reordering changes only the selected monitor's order; the next linked switch uses the updated positions.

**Follow a moved window:** Enable **Follow window to its desktop** in **Settings → Desktops** to open the destination when using previous/next window movement shortcuts, including custom bindings. This option is off by default and is saved. Independent mode switches the window's monitor; Linked mode switches every monitor to the destination position. Dragging a window, choosing a destination from its context menu, or dropping it on **New desktop** keeps your current view.

**Stop at the ends:** Turn off **Wrap desktop switching** in **Settings → Desktops** to stop previous/next desktop shortcuts at the first and last desktops. With two desktops, pressing `Win + Ctrl + ←` on Desktop 1 stays on Desktop 1, and pressing `Win + Ctrl + →` on Desktop 2 stays on Desktop 2. Wraparound is on by default; your choice is saved.

**Show desktop names:** In **Settings → Appearance**, set **Show desktop names** to **Off**, **When switching**, or **Always**. The default, **When switching**, displays the name on the affected monitor for about 1.8 seconds. **Always** shows each monitor's current desktop name continuously. Choose **Top right** or **Top left** under **Desktop name position**; this choice is disabled when name display is Off. These preferences are saved. Indicators temporarily hide while an overlay, the management window, Settings, or the shortcut editor is open, or while the app is paused.

Name indicators also identify the **first** and **last** desktops, or both when there is only one desktop. With wraparound off, attempting to switch past an end still displays the current name and position.

**Select a taskbar window:** For apps with several windows, such as Chrome, choose the desired window from the taskbar's window list. DisplaySpaces follows the window Windows actually selects. Depending on the app, a hidden window's taskbar preview may show its last image or an icon.

**Start with Windows:** Enable this option in **Settings → General** to run in the background at sign-in without opening the overlay. It is off by default. If you move the app, run it from its new location and enable the option again to update the registered path. Windows **Settings → Apps → Startup** can disable startup separately.

## Build and install from source

Building requires Windows 11 x64 and the .NET 10 SDK.

```powershell
.\scripts\build.ps1 -Install
& "$env:LOCALAPPDATA\Programs\DisplaySpaces\DisplaySpaces.exe"
```

The installer uses `%LOCALAPPDATA%\Programs\DisplaySpaces`. Reuse this path for updates: running executables from a different folder for each version can leave separate entries in Windows' **Other system tray icons** settings. Before updating, select **Exit · Restore all windows** in the tray menu. The installer refuses to replace a running installation and restores the previous installation if file replacement fails.

To install an existing published build and launch it in the background:

```powershell
.\scripts\install.ps1 -PublishDirectory .\artifacts\publish -Launch
```

If startup was enabled at an earlier location, enable **Start with Windows** again under **Settings → General** in the installed app to update its path. Existing language, shortcut, and display preferences are preserved.

To create a portable publish folder without installing, run `.\scripts\build.ps1 -Publish`. It includes the .NET runtime, so you can copy the entire folder to another compatible PC.

Run the tests with:

```powershell
.\scripts\test.ps1
```

## Development environment

- **Development and validation:** Windows 11 x64 with multiple monitors
- **Language and UI:** C#, .NET 10, Windows Forms
- **Window management and previews:** Windows APIs (Win32), DWM
- **Tools:** Codex desktop app, PowerShell, .NET CLI; validated with SDK `10.0.400`
- **AI assistance:** Designed, implemented, tested, and documented with help from **ChatGPT Astra Ultra**. The app icon was created with ImageGen.

## Limitations

DisplaySpaces manages window visibility separately from Windows' built-in virtual desktops. Desktop layouts and window assignments last only for the current app session. Elevated apps and some specialized windows may have limited support.

See the [implementation notes](docs/architecture.md) for details about behavior and validation.
