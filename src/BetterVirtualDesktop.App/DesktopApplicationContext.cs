using System.Diagnostics;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Shortcuts;

namespace BetterVirtualDesktop.App;

internal sealed class DesktopApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly RecoveryJournal _journal;
    private readonly Process _guardian;
    private readonly DesktopController _controller;
    private readonly OverviewForm _overview;
    private readonly OverlayCoordinator _overlays;
    private readonly DesktopNameToastCoordinator _desktopNames;
    private readonly TaskbarDesktopCoordinator _taskbarDesktops;
    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _keyboard;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _stopping;
    private bool _disposed;
    private bool _handlingError;
    private bool _overviewUpdatePending;
    private bool _languageUpdatePending;
    private Font? _trayFont;
    private ShortcutProfile _shortcuts = ShortcutProfile.Defaults;
    private ShortcutSettingsForm? _shortcutSettings;

    public DesktopApplicationContext(bool showOnStartup = true)
    {
        try
        {
            _ = _dispatcher.Handle;
            _shortcuts = ShortcutPreferences.Load();
            RecoveryJournal.RecoverOrphans();
            _journal = new RecoveryJournal();
            _guardian = ProcessLauncher.StartGuardian(_journal.FilePath);
            _controller = new DesktopController(_journal, canHide: () => !_guardian.HasExited)
            {
                WrapDesktopSwitching = DesktopNavigationPreferences.Load(),
                SwitchingMode = DisplaySwitchingPreferences.Load(),
                FollowMovedWindow = WindowMovePreferences.Load()
            };
            _overview = new OverviewForm(_controller, () => _shortcuts);
            _overview.ShortcutSettingsRequested += OpenShortcutSettings;
            _overlays = new OverlayCoordinator(_controller, _dispatcher);
            _overlays.ShortcutSettingsRequested += OpenShortcutSettings;
            _desktopNames = new DesktopNameToastCoordinator(_controller, _dispatcher, DesktopNamePreferences.Load());
            _overlays.VisibilityChanged += UpdateDesktopNameVisibility;
            _overview.VisibleChanged += (_, _) => UpdateDesktopNameVisibility();
            _taskbarDesktops = new TaskbarDesktopCoordinator(_controller, _dispatcher,
                () => _stopping || _shortcutSettings is not null);
            _taskbarDesktops.DesktopFollowed += () =>
            {
                _overlays.Dismiss(false);
                _overview.Hide();
            };
            _controller.Changed += () =>
            {
                // Rebuild after the current click handler completes; do not dispose its control in-place.
                if (!_overviewUpdatePending && !_overview.IsDisposed && (_overview.Visible || _overlays.Visible) && !_stopping)
                {
                    _overviewUpdatePending = true;
                    _dispatcher.BeginInvoke(() =>
                    {
                        _overviewUpdatePending = false;
                        if (!_stopping)
                        {
                            if (!_overview.IsDisposed && _overview.Visible) _overview.UpdateContent();
                            _overlays.UpdateContent();
                        }
                    });
                }
            };
            _tray = new NotifyIcon { Icon = AppIcon.Tray, Text = "BetterVirtualDesktop", Visible = true };
            RebuildTrayMenu();
            _tray.DoubleClick += (_, _) => OpenOverlays();
            UiText.Changed += OnLanguageChanged;
            _keyboard = new KeyboardHook(_shortcuts);
            _keyboard.Command += (command, generation) =>
            {
                if (_stopping || _dispatcher.IsDisposed) return;
                try { _dispatcher.BeginInvoke(() =>
                {
                    // A queued press from before the settings dialog must not run after it closes.
                    if (!_stopping && generation == _keyboard.Generation) Execute(command);
                }); }
                catch (InvalidOperationException) { }
            };
            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (_, _) =>
            {
                if (_stopping) return;
                try
                {
                    if (_guardian.HasExited && !_controller.Paused)
                    {
                        _controller.RestoreAllAndPause();
                        _tray.ShowBalloonTip(6000, UiText.Get("Guardian.StoppedTitle"), UiText.Get("Guardian.StoppedBody"), ToolTipIcon.Warning);
                    }
                    _controller.Refresh();
                }
                catch (Exception ex) { HandleError(ex); }
            };
            _timer.Start();
            Application.ThreadException += OnThreadException;
            if (showOnStartup) OpenOverlays();
        }
        catch
        {
            // A failed constructor is never placed into Program's using statement.
            // Restore and dispose anything initialized before the failure here.
            _stopping = true;
            Application.ThreadException -= OnThreadException;
            UiText.Changed -= OnLanguageChanged;
            BestEffort(() => _journal?.RestoreAll());
            BestEffort(() => _timer?.Dispose());
            BestEffort(() => _keyboard?.Dispose());
            BestEffort(() => _overlays?.Dispose());
            BestEffort(() => _desktopNames?.Dispose());
            BestEffort(() => _taskbarDesktops?.Dispose());
            BestEffort(() => _tray?.ContextMenuStrip?.Dispose());
            BestEffort(() => _tray?.Dispose());
            BestEffort(() => _trayFont?.Dispose());
            BestEffort(() => _overview?.Dispose());
            BestEffort(_dispatcher.Dispose);
            BestEffort(() => _guardian?.Dispose());
            throw;
        }
    }

    private void OnThreadException(object sender, ThreadExceptionEventArgs args) => HandleError(args.Exception);

    private void HandleError(Exception error)
    {
        AppLog.Write(error);
        if (_handlingError || _stopping) return;
        _handlingError = true;
        try
        {
            BestEffort(_controller.RestoreAllAndPause);
            BestEffort(() => _overlays.Dismiss(false));
            BestEffort(() => _tray.ShowBalloonTip(6000, UiText.Get("Error.PausedTitle"), UiText.Get("Error.PausedBody"), ToolTipIcon.Warning));
        }
        finally { _handlingError = false; }
    }

    private static void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception error) { AppLog.Write(error); }
    }

    private void RebuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        var nextFont = new Font(UiText.FontFamily, 9);
        menu.Font = nextFont;
        string overlayShortcut = ShortcutText.Format(_shortcuts[DesktopCommand.ToggleOverview]);
        string trayText = $"BetterVirtualDesktop · {overlayShortcut}";
        _tray.Text = trayText[..Math.Min(63, trayText.Length)];
        menu.Items.Add(UiText.Get("Tray.Overlays", overlayShortcut), null, (_, _) => OpenOverlays());
        menu.Items.Add(UiText.Get("Tray.Management"), null, (_, _) => OpenOverview());
        menu.Items.Add(UiText.Get("Shortcuts.Open"), null, (_, _) => OpenShortcutSettings());
        var startup = new StartupMenuItem(new StartupRegistration(), Environment.ProcessPath, error =>
        {
            AppLog.Write(error);
            _tray.ShowBalloonTip(6000, "BetterVirtualDesktop", UiText.Get("Startup.AutoRunFailed", error.Message), ToolTipIcon.Error);
        });
        menu.Items.Add(startup);
        menu.Opening += (_, _) => startup.RefreshStatus();
        menu.Items.Add(new DisplaySwitchingMenuItem(_controller, mode => DisplaySwitchingPreferences.Save(mode), error =>
        {
            AppLog.Write(error);
            _tray.ShowBalloonTip(6000, "BetterVirtualDesktop", UiText.Get("DisplaySwitching.SaveFailed", error.Message), ToolTipIcon.Error);
        }));
        menu.Items.Add(new WrapDesktopSwitchingMenuItem(_controller, wrap => DesktopNavigationPreferences.Save(wrap), error =>
        {
            AppLog.Write(error);
            _tray.ShowBalloonTip(6000, "BetterVirtualDesktop", UiText.Get("Navigation.SaveFailed", error.Message), ToolTipIcon.Error);
        }));
        menu.Items.Add(new FollowMovedWindowMenuItem(_controller, follow => WindowMovePreferences.Save(follow), error =>
        {
            AppLog.Write(error);
            _tray.ShowBalloonTip(6000, "BetterVirtualDesktop", UiText.Get("WindowMove.SaveFailed", error.Message), ToolTipIcon.Error);
        }));
        menu.Items.Add(new DesktopNameMenuItem(_desktopNames, settings => DesktopNamePreferences.Save(settings), error =>
        {
            AppLog.Write(error);
            _tray.ShowBalloonTip(6000, "BetterVirtualDesktop", UiText.Get("DesktopName.SaveFailed", error.Message), ToolTipIcon.Error);
        }));
        var languageMenu = new ToolStripMenuItem(UiText.Get("Tray.Language"));
        var system = new ToolStripMenuItem(UiText.Get("Tray.SystemLanguage")) { Checked = UiText.SelectedLanguageCode == "system" };
        system.Click += (_, _) => SelectLanguage("system");
        languageMenu.DropDownItems.Add(system);
        languageMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var language in UiText.SupportedLanguages)
        {
            var item = new ToolStripMenuItem(language.NativeName) { Checked = UiText.SelectedLanguageCode == language.Code };
            item.Click += (_, _) => SelectLanguage(language.Code);
            languageMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(languageMenu);
        menu.Items.Add(UiText.Get("Tray.PauseResume"), null, (_, _) => _controller.TogglePause());
        menu.Items.Add(UiText.Get("Tray.RestorePause"), null, (_, _) => _controller.RestoreAllAndPause());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.Get("Tray.Exit"), null, (_, _) => ExitThread());
        var previous = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        previous?.Close();
        previous?.Dispose();
        _trayFont?.Dispose();
        _trayFont = nextFont;
    }

    private static void SelectLanguage(string code)
    {
        try { UiText.SetLanguage(code, persist: true); }
        catch (Exception error)
        {
            AppLog.Write(error);
            MessageBox.Show(UiText.Get("LanguageSaveFailed", error.Message), "BetterVirtualDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateDesktopNameVisibility()
    {
        if (!_stopping) _desktopNames.Suspended = _overlays.Visible || _overview.Visible;
    }

    private void OnLanguageChanged()
    {
        if (_stopping || _dispatcher.IsDisposed || _languageUpdatePending) return;
        _languageUpdatePending = true;
        // Leave the menu's click handler before disposing its old localized controls.
        _dispatcher.BeginInvoke(() =>
        {
            _languageUpdatePending = false;
            if (_stopping) return;
            RebuildTrayMenu();
            _overview.ApplyLanguage();
            _overlays.ApplyLanguage();
            // No discovery or visibility operation: language changes only repaint this app's UI.
        });
    }

    private void Execute(DesktopCommand command)
    {
        if (_stopping || _shortcutSettings is not null) return;
        try
        {
            _controller.Refresh();
            string display = _controller.PointerDisplay;
            switch (command)
            {
                case DesktopCommand.ToggleOverview:
                    _overview.Hide();
                    _overlays.Toggle();
                    break;
                case DesktopCommand.PreviousDesktop: _controller.SwitchRelative(display, -1); break;
                case DesktopCommand.NextDesktop: _controller.SwitchRelative(display, 1); break;
                case DesktopCommand.MoveWindowPrevious: _controller.MoveForegroundRelative(-1); break;
                case DesktopCommand.MoveWindowNext: _controller.MoveForegroundRelative(1); break;
                case DesktopCommand.AddDesktop: _controller.AddDesktop(display); break;
                case DesktopCommand.RemoveDesktop:
                    var state = _controller.Catalog.Snapshot().First(d => d.Id == display);
                    _controller.RemoveDesktop(display, state.ActiveDesktopId);
                    break;
                case DesktopCommand.RestoreAll: _controller.RestoreAllAndPause(); break;
            }
        }
        catch (Exception error) { HandleError(error); }
    }

    private void OpenOverview()
    {
        if (_shortcutSettings is { IsDisposed: false } settings) { settings.Activate(); return; }
        _overlays.Dismiss(false);
        _controller.Refresh();
        _overview.OpenOnPointerDisplay();
    }

    private void OpenShortcutSettings()
    {
        if (_stopping) return;
        if (_shortcutSettings is { IsDisposed: false } existing) { existing.Activate(); return; }
        bool reopenOverlay = _overlays.Visible;
        bool reopenOverview = _overview.Visible;
        _overlays.Dismiss(false);
        _overview.Hide();
        _keyboard.SetSuspended(true);
        try
        {
            using var dialog = new ShortcutSettingsForm(_shortcuts, SaveShortcuts);
            _shortcutSettings = dialog;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(area.Left + Math.Max(0, (area.Width - dialog.Width) / 2),
                area.Top + Math.Max(0, (area.Height - dialog.Height) / 2));
            dialog.ShowDialog();
        }
        finally
        {
            _shortcutSettings = null;
            if (!_stopping)
            {
                _keyboard.SetSuspended(false);
                if (reopenOverlay) OpenOverlays();
                else if (reopenOverview) OpenOverview();
            }
        }
    }

    private string? SaveShortcuts(ShortcutProfile profile)
    {
        if (profile.Validate() is { } issue) return ShortcutText.ErrorText(issue);
        try { ShortcutPreferences.Save(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(error);
            return UiText.Get("Shortcuts.SaveFailed", error.Message);
        }
        _keyboard.ApplyProfile(profile);
        _shortcuts = profile;
        // Rebuild after the dialog/menu click has finished, just like language changes.
        _dispatcher.BeginInvoke(() =>
        {
            if (_stopping) return;
            RebuildTrayMenu();
            _overview.ApplyLanguage();
        });
        return null;
    }

    private void OpenOverlays()
    {
        if (_shortcutSettings is { IsDisposed: false } settings) { settings.Activate(); return; }
        _overview.Hide();
        _overlays.Show();
    }

    protected override void ExitThreadCore()
    {
        if (_stopping) return;
        _stopping = true;
        UiText.Changed -= OnLanguageChanged;
        BestEffort(() => _shortcutSettings?.Close());
        BestEffort(_timer.Stop);
        BestEffort(_controller.RestoreAllAndPause);
        BestEffort(_taskbarDesktops.Dispose);
        BestEffort(_journal.DeleteIfEmpty);
        BestEffort(_keyboard.Dispose);
        BestEffort(_overlays.Dispose);
        BestEffort(_desktopNames.Dispose);
        BestEffort(_overview.Shutdown);
        BestEffort(() => _tray.Visible = false);
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_stopping) ExitThreadCore();
            Application.ThreadException -= OnThreadException;
            UiText.Changed -= OnLanguageChanged;
            BestEffort(_timer.Dispose);
            BestEffort(_keyboard.Dispose);
            BestEffort(_overlays.Dispose);
            BestEffort(_desktopNames.Dispose);
            BestEffort(_taskbarDesktops.Dispose);
            BestEffort(() => _tray.ContextMenuStrip?.Dispose());
            BestEffort(_tray.Dispose);
            BestEffort(() => _trayFont?.Dispose());
            BestEffort(_overview.Dispose);
            BestEffort(_dispatcher.Dispose);
            BestEffort(_guardian.Dispose);
        }
        base.Dispose(disposing);
    }
}
