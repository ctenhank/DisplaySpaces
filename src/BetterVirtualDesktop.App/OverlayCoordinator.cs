using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Services;

namespace BetterVirtualDesktop.App;

/// <summary>Coordinates overlay lifetime and routes a selection to exactly one display.</summary>
internal sealed class OverlayCoordinator : IDisposable
{
    private readonly DesktopController _controller;
    private readonly Control _dispatcher;
    private readonly Dictionary<string, DisplayOverlayForm> _forms = new();
    private readonly WindowPreviewCache _previews = new();
    private Guid _dragSession = Guid.NewGuid();
    private readonly System.Windows.Forms.Timer _activation = new() { Interval = 70 };
    private long _previousForeground;
    private long _pendingActivation;
    private int _activationAttempts;
    private bool _visible;
    private bool _updating;
    private bool _disposed;
    private long _generation;
    private NativeWindowInfo? _pendingWindow;
    public bool Visible => _visible;
    public IReadOnlyList<DisplayOverlayForm> Forms => _forms.Values.ToArray();
    public event Action? ShortcutSettingsRequested;
    public event Action? VisibilityChanged;

    public OverlayCoordinator(DesktopController controller, Control dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _controller.Changed += CapturePreviews;
        _previews.Changed += RefreshPreviews;
        CapturePreviews();
        _activation.Tick += (_, _) =>
        {
            if (++_activationAttempts >= 8 || NativeWindows.IsVisible(_pendingActivation))
            {
                _activation.Stop();
                if (_pendingWindow is { } window && NativeWindows.IsVisible(_pendingActivation)
                    && NativeWindows.IsSameWindow(window.Handle, window.ProcessId, window.ProcessStartTicks, window.ClassName))
                    NativeWindows.Activate(_pendingActivation);
            }
        };
    }

    public void Toggle() { if (_visible) Dismiss(); else Show(); }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_visible) return;
        _activation.Stop();
        _previousForeground = NativeWindows.ForegroundWindow;
        _controller.Refresh();
        _generation++;
        _dragSession = Guid.NewGuid();
        _visible = true;
        VisibilityChanged?.Invoke();
        UpdateContent();
        var screen = Screen.FromPoint(Cursor.Position);
        if (_forms.TryGetValue(screen.DeviceName, out var focused)) focused.Activate();
    }

    public void UpdateContent()
    {
        if (!_visible || _disposed) return;
        _updating = true;
        try
        {
            var screens = Screen.AllScreens.ToDictionary(s => s.DeviceName);
            var displays = _controller.Catalog.Snapshot().ToDictionary(d => d.Id);
            foreach (var id in _forms.Keys.Where(id => !screens.ContainsKey(id) || !displays.ContainsKey(id)).ToArray())
            { var removed = _forms[id]; _forms.Remove(id); removed.Shutdown(); removed.Dispose(); }
            foreach (var screen in screens.Values)
            {
                if (!displays.TryGetValue(screen.DeviceName, out var display)) continue;
                if (!_forms.TryGetValue(screen.DeviceName, out var form))
                {
                    form = new DisplayOverlayForm(screen, display, _previews, _dragSession);
                    var source = form;
                    form.DismissRequested += () => Post(source, () => Dismiss());
                    form.ShortcutSettingsRequested += () => Post(source, () => ShortcutSettingsRequested?.Invoke());
                    form.SelectionRequested += (id, desktop, handle) => Post(source, () => Select(id, desktop, handle));
                    form.AddRequested += id => Post(source, () =>
                    {
                        _controller.AddDesktop(id);
                        UpdateContent();
                        if (_forms.TryGetValue(id, out var destination))
                            destination.ShowCreationFeedback(_controller.Catalog.Snapshot().Single(d => d.Id == id).ActiveDesktopId, false);
                    });
                    form.RemoveRequested += (id, desktop) => Post(source, () => { _controller.RemoveDesktop(id, desktop); UpdateContent(); });
                    form.RenameRequested += (id, desktop, name) => Post(source, () => { _controller.RenameDesktop(id, desktop, name); UpdateContent(); });
                    form.ReorderRequested += (id, desktop, index) => Post(source, () =>
                    {
                        if (!_controller.ReorderDesktop(id, desktop, index)) return;
                        UpdateContent();
                        source.ShowStatus(UiText.Get("Reorder.Succeeded"));
                    });
                    form.MoveToDisplayRequested += (handle, id, desktop) => Post(source, () =>
                    {
                        PreserveMovePreviews();
                        bool moved = _controller.MoveWindowToDisplay(handle, id, desktop);
                        source.ShowStatus(UiText.Get(moved ? "MoveSucceeded" : "MoveFailed"));
                        UpdateContent();
                    });
                    form.MoveToNewDesktopRequested += (handle, id) => Post(source, () =>
                    {
                        PreserveMovePreviews();
                        var desktop = _controller.MoveWindowToNewDesktop(handle, id);
                        UpdateContent();
                        if (desktop is Guid created && _forms.TryGetValue(id, out var destination))
                        {
                            destination.ShowCreationFeedback(created);
                        }
                        else source.ShowStatus(UiText.Get("NewDesktopMoveFailed"));
                    });
                    form.KeyboardFocusRequested += () => Post(source, () =>
                    {
                        if (!_forms.Values.Any(f => f.HasOpenTransientUi)) source.Activate();
                    });
                    form.DragEnded += () => Post(source, () =>
                    {
                        if (!_forms.Values.Any(f => f.HasInteractionFocus)) Dismiss(false);
                    });
                    form.Deactivate += (_, _) =>
                    {
                        if (!_updating) Post(source, () =>
                        {
                            if (_visible && !_forms.Values.Any(f => f.HasInteractionFocus)) Dismiss(false);
                        });
                    };
                    _forms.Add(screen.DeviceName, form);
                    form.UpdateSnapshot(screen, display, _controller.Windows, displays.Values.ToArray());
                    form.Show();
                }
                else form.UpdateSnapshot(screen, display, _controller.Windows, displays.Values.ToArray());
            }
            if (_forms.Count == 0)
            {
                _visible = false;
                VisibilityChanged?.Invoke();
            }
        }
        catch { Dismiss(false); throw; }
        finally { _updating = false; }
    }

    public void ApplyLanguage()
    {
        if (_disposed) return;
        foreach (var form in _forms.Values) form.ApplyLanguage();
        UpdateContent();
    }

    private void PreserveMovePreviews()
    {
        foreach (var overlay in _forms.Values) overlay.PreservePreviewAfterMove();
    }

    private void CapturePreviews()
    {
        if (!_disposed) _previews.Request(_controller.Windows.Values);
    }

    private void RefreshPreviews()
    {
        if (!_disposed) foreach (var form in _forms.Values) form.Invalidate();
    }

    private void Select(string displayId, Guid desktopId, long? handle)
    {
        if (!_visible) return;
        var display = _controller.Catalog.Snapshot().FirstOrDefault(d => d.Id == displayId);
        var desktop = display?.Desktops.FirstOrDefault(d => d.Id == desktopId);
        if (desktop is null) return;
        if (handle.HasValue && !desktop.WindowIds.Contains(handle.Value)) return;
        _controller.Switch(displayId, desktopId);
        long target = handle ?? (desktop.WindowIds.Contains(_previousForeground) ? _previousForeground :
            desktop.WindowIds.FirstOrDefault(h => _controller.Windows.TryGetValue(h, out var w) && !w.IsMinimized));
        Dismiss(false);
        if (target != 0)
        {
            _pendingActivation = target;
            _pendingWindow = _controller.Windows.GetValueOrDefault(target);
            _activationAttempts = 0;
            _activation.Start();
        }
    }

    public void Dismiss(bool restoreFocus = true)
    {
        bool wasVisible = _visible;
        _visible = false;
        _generation++;
        _updating = true;
        try
        {
            foreach (var form in _forms.Values) { form.Shutdown(); form.Dispose(); }
            _forms.Clear();
        }
        finally { _updating = false; }
        if (wasVisible) VisibilityChanged?.Invoke();
        if (restoreFocus && _previousForeground != 0 && NativeWindows.IsVisible(_previousForeground))
            NativeWindows.Activate(_previousForeground);
    }

    private void Post(DisplayOverlayForm source, Action action)
    {
        if (_disposed || _dispatcher.IsDisposed) return;
        long generation = _generation;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _visible && generation == _generation && !source.IsDisposed
                && _forms.TryGetValue(source.DisplayId, out var current) && ReferenceEquals(source, current)) action();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= CapturePreviews;
        _previews.Changed -= RefreshPreviews;
        _activation.Dispose();
        Dismiss(false);
        _previews.Dispose();
    }
}
