using System.Diagnostics;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Services;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App;

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
    private SettingsForm? _settings;

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
                () => _stopping || _shortcutSettings is not null || _settings is not null);
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
            _tray = new NotifyIcon { Icon = AppIcon.Tray, Text = "DisplaySpaces", Visible = true };
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
        string trayText = $"DisplaySpaces · {overlayShortcut}";
        _tray.Text = trayText[..Math.Min(63, trayText.Length)];
        menu.Items.Add(UiText.Get("Tray.Overlays", overlayShortcut), null, (_, _) => OpenOverlays());
        menu.Items.Add(UiText.Get("Tray.Management"), null, (_, _) => OpenOverview());
        menu.Items.Add(UiText.Get("Settings.Open"), null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
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

    private static string? SaveSetting(string errorKey, Action saveAndApply)
    {
        try
        {
            saveAndApply();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write(error);
            return UiText.Get(errorKey, error.Message);
        }
    }

    private void UpdateDesktopNameVisibility()
    {
        if (!_stopping) _desktopNames.Suspended = _overlays.Visible || _overview.Visible
            || _settings is not null || _shortcutSettings is not null;
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
        if (_stopping || _shortcutSettings is not null || _settings is not null) return;
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
        if (_settings is { IsDisposed: false } general) { ActivateSettings(general); return; }
        _overlays.Dismiss(false);
        _controller.Refresh();
        _overview.OpenOnPointerDisplay();
    }

    private static void ActivateSettings(SettingsForm settings)
    {
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    private void OpenSettings()
    {
        if (_stopping) return;
        if (_shortcutSettings is { IsDisposed: false } shortcuts) { shortcuts.Activate(); return; }
        if (_settings is { IsDisposed: false } existing) { ActivateSettings(existing); return; }
        _overlays.Dismiss(false);
        _overview.Hide();
        var startup = new StartupRegistration();
        string ExecutablePath() => Environment.ProcessPath ?? throw new InvalidOperationException(UiText.Get("Startup.NoExecutable"));
        StartupSettingsState ReadStartup()
        {
            try
            {
                string expected = StartupRegistration.BuildCommand(ExecutablePath());
                string? registered = startup.ReadCommand();
                bool enabled = string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase);
                string hint = UiText.Get("Startup.AutoRunHint");
                if (registered is not null && !enabled) hint = UiText.Get("Startup.AutoRunDifferentPath") + "\n\n" + hint;
                return new(enabled, Hint: hint);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException or InvalidOperationException)
            {
                return new(false, false, UiText.Get("Startup.AutoRunUnavailable") + "\n" + error.Message);
            }
        }
        var bindings = new SettingsBindings(
            () => new(UiText.SelectedLanguageCode, ReadStartup(), _controller.SwitchingMode,
                _controller.WrapDesktopSwitching, _controller.FollowMovedWindow, new(_desktopNames.Mode, _desktopNames.Corner)),
            language => SaveSetting("LanguageSaveFailed", () => UiText.SetLanguage(language, persist: true)),
            enabled => SaveSetting("Startup.AutoRunFailed", () => startup.SetEnabled(enabled, ExecutablePath())),
            mode => SaveSetting("DisplaySwitching.SaveFailed", () =>
            {
                DisplaySwitchingPreferences.Save(mode);
                _controller.SwitchingMode = mode;
            }),
            wrap => SaveSetting("Navigation.SaveFailed", () =>
            {
                DesktopNavigationPreferences.Save(wrap);
                _controller.WrapDesktopSwitching = wrap;
            }),
            follow => SaveSetting("WindowMove.SaveFailed", () =>
            {
                WindowMovePreferences.Save(follow);
                _controller.FollowMovedWindow = follow;
            }),
            names => SaveSetting("DesktopName.SaveFailed", () =>
            {
                DesktopNamePreferences.Save(names);
                _desktopNames.Corner = names.Corner;
                _desktopNames.Mode = names.Mode;
            }),
            OpenShortcutSettings);
        var form = new SettingsForm(bindings);
        _settings = form;
        _keyboard.SetSuspended(true);
        UpdateDesktopNameVisibility();
        form.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_settings, form)) return;
            _settings = null;
            if (_stopping) return;
            _keyboard.SetSuspended(_shortcutSettings is not null);
            UpdateDesktopNameVisibility();
        };
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.Location = new Point(area.Left + Math.Max(0, (area.Width - form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - form.Height) / 2));
        try { form.Show(); }
        catch
        {
            _settings = null;
            form.Dispose();
            _keyboard.SetSuspended(_shortcutSettings is not null);
            UpdateDesktopNameVisibility();
            throw;
        }
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
            UpdateDesktopNameVisibility();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(area.Left + Math.Max(0, (area.Width - dialog.Width) / 2),
                area.Top + Math.Max(0, (area.Height - dialog.Height) / 2));
            if (_settings is { IsDisposed: false } owner) dialog.ShowDialog(owner);
            else dialog.ShowDialog();
        }
        finally
        {
            _shortcutSettings = null;
            if (!_stopping)
            {
                _keyboard.SetSuspended(_settings is not null);
                UpdateDesktopNameVisibility();
                if (_settings is null)
                {
                    if (reopenOverlay) OpenOverlays();
                    else if (reopenOverview) OpenOverview();
                }
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
        if (_settings is { IsDisposed: false } general) { ActivateSettings(general); return; }
        _overview.Hide();
        _overlays.Show();
    }

    protected override void ExitThreadCore()
    {
        if (_stopping) return;
        _stopping = true;
        UiText.Changed -= OnLanguageChanged;
        BestEffort(() => _shortcutSettings?.Close());
        BestEffort(() => _settings?.Close());
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
            BestEffort(() => _settings?.Dispose());
            BestEffort(_dispatcher.Dispose);
            BestEffort(_guardian.Dispose);
        }
        base.Dispose(disposing);
    }
}
