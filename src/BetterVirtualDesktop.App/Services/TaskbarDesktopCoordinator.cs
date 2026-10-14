using BetterVirtualDesktop.App.Native;

namespace BetterVirtualDesktop.App.Services;

/// <summary>Keeps hidden real windows selectable in their native taskbar group and follows exact selections.</summary>
internal sealed class TaskbarDesktopCoordinator : IDisposable
{
    private readonly DesktopController _controller;
    private readonly Control _dispatcher;
    private readonly Func<bool> _suspended;
    private readonly TaskbarWindowRegistry _taskbar = new();
    private readonly ForegroundWindowHook _foreground;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly Dictionary<long, NativeWindowInfo> _registered = new();
    private bool _pendingSync;
    private bool _disposed;
    private long _activationSequence;
    public event Action? DesktopFollowed;
    internal int RegisteredWindowCount => _registered.Count;
    internal bool IsRegistered(long handle) => _registered.ContainsKey(handle);

    public TaskbarDesktopCoordinator(DesktopController controller, Control dispatcher,
        Func<bool>? suspended = null, uint eventProcessId = 0)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _suspended = suspended ?? (() => false);
        try { _foreground = new ForegroundWindowHook(eventProcessId); }
        catch { _taskbar.Dispose(); _timer.Dispose(); throw; }
        _foreground.Activated += OnForeground;
        _foreground.Hidden += OnNativeHidden;
        _controller.Changed += QueueSync;
        _controller.WindowHideRequested += OnWindowHidden;
        _taskbar.Recreated += OnTaskbarRecreated;
        _timer.Tick += (_, _) => Synchronize();
        _timer.Start();
        QueueSync();
    }

    private void OnForeground(long handle, uint eventTime)
    {
        long sequence = ++_activationSequence;
        if (_disposed || _suspended() || !_controller.Windows.TryGetValue(handle, out var window)) return;
        long generation = _controller.VisibilityGeneration;
        // Leave the native callback before journal I/O or applying a desktop switch.
        Post(() =>
        {
            if (sequence != _activationSequence || _suspended()) return;
            if (_controller.FollowForegroundWindow(window, eventTime, generation)) DesktopFollowed?.Invoke();
        });
    }

    private void OnTaskbarRecreated()
    {
        _registered.Clear();
        QueueSync();
    }

    private void OnWindowHidden(long handle)
    {
        // An application may show itself in the background and be hidden again. That hide
        // removes its taskbar entry even if neither state was observed by our polling timer.
        _registered.Remove(handle);
        QueueSync();
    }

    private void OnNativeHidden(long handle, uint eventTime)
    {
        if (_disposed || !_registered.ContainsKey(handle)) return;
        Post(() =>
        {
            // Observe actual hide completion too: a show followed by hide can both finish
            // between polling ticks and remove a taskbar button registered in that interval.
            if (!NativeWindows.IsVisible(handle)) OnWindowHidden(handle);
        });
    }

    private void QueueSync()
    {
        if (_disposed || _pendingSync) return;
        _pendingSync = true;
        Post(() => { _pendingSync = false; Synchronize(); });
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.IsDisposed || !_dispatcher.IsHandleCreated) return;
        try { _dispatcher.BeginInvoke(() => { if (!_disposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    internal void Synchronize()
    {
        if (_disposed) return;
        foreach (var window in _controller.Windows.Values) _controller.ObserveHiddenWindow(window.Handle);
        var candidates = _controller.Paused ? [] : _controller.Windows.Values
            .Where(window => _controller.OwnsHiddenWindow(window.Handle)
                && !_controller.HasPendingHide(window.Handle)
                && !_controller.Catalog.IsWindowVisible(window.Handle)
                && !NativeWindows.IsVisible(window.Handle)
                && NativeWindows.IsOnCurrentVirtualDesktop(window.Handle)
                && TaskbarWindowRegistry.CanAppearOnTaskbar(window.Handle)
                && NativeWindows.IsSameWindow(window.Handle, window.ProcessId, window.ProcessStartTicks, window.ClassName))
            .ToDictionary(window => window.Handle);
        foreach (var (handle, window) in _registered.ToArray())
        {
            if (candidates.TryGetValue(handle, out var candidate) && SameIdentity(window, candidate)) continue;
            // A visible window has returned to normal shell ownership. DeleteTab would remove
            // its real taskbar button. A recycled handle belongs to someone else; never touch it.
            if (!NativeWindows.Exists(handle) || (NativeWindows.IsSameWindow(handle, window.ProcessId, window.ProcessStartTicks, window.ClassName)
                && !NativeWindows.IsVisible(handle))) _taskbar.TryDelete(handle);
            _registered.Remove(handle);
        }
        foreach (var (handle, window) in candidates)
            if (!_registered.ContainsKey(handle) && _taskbar.TryAdd(handle)) _registered[handle] = window;
    }

    private static bool SameIdentity(NativeWindowInfo left, NativeWindowInfo right) =>
        left.ProcessId == right.ProcessId && left.ProcessStartTicks == right.ProcessStartTicks && left.ClassName == right.ClassName;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= QueueSync;
        _controller.WindowHideRequested -= OnWindowHidden;
        _timer.Dispose();
        _foreground.Activated -= OnForeground;
        _foreground.Hidden -= OnNativeHidden;
        _foreground.Dispose();
        _taskbar.Recreated -= OnTaskbarRecreated;
        foreach (var (handle, window) in _registered)
            if (!NativeWindows.Exists(handle) || (NativeWindows.IsSameWindow(handle, window.ProcessId, window.ProcessStartTicks, window.ClassName)
                && !NativeWindows.IsVisible(handle))) _taskbar.TryDelete(handle);
        _registered.Clear();
        _taskbar.Dispose();
    }
}
