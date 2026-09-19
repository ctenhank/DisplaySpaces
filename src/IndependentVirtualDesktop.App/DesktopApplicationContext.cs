using System.Diagnostics;
using IndependentVirtualDesktop.App.Native;
using IndependentVirtualDesktop.App.Services;

namespace IndependentVirtualDesktop.App;

internal sealed class DesktopApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly RecoveryJournal _journal;
    private readonly Process _guardian;
    private readonly DesktopController _controller;
    private readonly OverviewForm _overview;
    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _keyboard;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _stopping;
    private bool _disposed;
    private bool _handlingError;
    private bool _overviewUpdatePending;

    public DesktopApplicationContext()
    {
        try
        {
            _ = _dispatcher.Handle;
            RecoveryJournal.RecoverOrphans();
            _journal = new RecoveryJournal();
            _guardian = ProcessLauncher.StartGuardian(_journal.FilePath);
            _controller = new DesktopController(_journal, canHide: () => !_guardian.HasExited);
            _overview = new OverviewForm(_controller);
            _controller.Changed += () =>
            {
                // Rebuild after the current click handler completes; do not dispose its control in-place.
                if (!_overviewUpdatePending && !_overview.IsDisposed && _overview.Visible && !_stopping)
                {
                    _overviewUpdatePending = true;
                    _dispatcher.BeginInvoke(() =>
                    {
                        _overviewUpdatePending = false;
                        if (!_stopping && !_overview.IsDisposed && _overview.Visible) _overview.UpdateContent();
                    });
                }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("작업 보기 열기 (Win+Tab)", null, (_, _) => OpenOverview());
            menu.Items.Add("일시 정지 / 다시 시작", null, (_, _) => _controller.TogglePause());
            menu.Items.Add("모든 창 복구 후 일시 정지", null, (_, _) => _controller.RestoreAllAndPause());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료 · 모든 창 복구", null, (_, _) => ExitThread());
            _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Independent Spaces · Win+Tab", ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (_, _) => OpenOverview();
            _keyboard = new KeyboardHook();
            _keyboard.Command += command =>
            {
                if (_stopping || _dispatcher.IsDisposed) return;
                try { _dispatcher.BeginInvoke(() => Execute(command)); }
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
                        _tray.ShowBalloonTip(6000, "창 복구 감시 중단", "모든 창을 복구하고 일시 정지했습니다. 앱을 다시 시작해 주세요.", ToolTipIcon.Warning);
                    }
                    _controller.Refresh();
                }
                catch (Exception ex) { HandleError(ex); }
            };
            _timer.Start();
            Application.ThreadException += OnThreadException;
            OpenOverview();
        }
        catch
        {
            // A failed constructor is never placed into Program's using statement.
            // Restore and dispose anything initialized before the failure here.
            _stopping = true;
            Application.ThreadException -= OnThreadException;
            BestEffort(() => _journal?.RestoreAll());
            BestEffort(() => _timer?.Dispose());
            BestEffort(() => _keyboard?.Dispose());
            BestEffort(() => _tray?.ContextMenuStrip?.Dispose());
            BestEffort(() => _tray?.Dispose());
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
            BestEffort(() => _tray.ShowBalloonTip(6000, "작업 전환 일시 정지", "오류가 발생하여 창 복구를 요청했습니다. 앱 로그에서 내용을 확인할 수 있습니다.", ToolTipIcon.Warning));
        }
        finally { _handlingError = false; }
    }

    private static void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception error) { AppLog.Write(error); }
    }

    private void Execute(DesktopCommand command)
    {
        if (_stopping) return;
        try
        {
            _controller.Refresh();
            string display = _controller.PointerDisplay;
            switch (command)
            {
                case DesktopCommand.ToggleOverview:
                    if (_overview.Visible) _overview.Hide(); else OpenOverview();
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
        _controller.Refresh();
        _overview.OpenOnPointerDisplay();
    }

    protected override void ExitThreadCore()
    {
        if (_stopping) return;
        _stopping = true;
        BestEffort(_timer.Stop);
        BestEffort(_controller.RestoreAllAndPause);
        BestEffort(_journal.DeleteIfEmpty);
        BestEffort(_keyboard.Dispose);
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
            BestEffort(_timer.Dispose);
            BestEffort(_keyboard.Dispose);
            BestEffort(() => _tray.ContextMenuStrip?.Dispose());
            BestEffort(_tray.Dispose);
            BestEffort(_overview.Dispose);
            BestEffort(_dispatcher.Dispose);
            BestEffort(_guardian.Dispose);
        }
        base.Dispose(disposing);
    }
}
