using System.Diagnostics;
using DisplaySpaces.App.Services;
using DisplaySpaces.App.Localization;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

/// <summary>Shows each display's active desktop name according to the selected display mode.</summary>
internal sealed class DesktopNameToastCoordinator : IDisposable
{
    private sealed record ActiveDesktop(Guid Id, string Name, string DisplayName, int Position, int Count);
    private sealed record Toast(DesktopNameToastForm Form, long ExpiresAt);
    private readonly DesktopController _controller;
    private readonly Control _dispatcher;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly Dictionary<string, Toast> _toasts = new();
    private readonly HashSet<string> _pending = new();
    private Dictionary<string, ActiveDesktop> _active;
    private bool _posted;
    private bool _disposed;
    private DesktopToastCorner _corner;
    private DesktopNameDisplayMode _mode;
    private bool _suspended;
    internal static readonly TimeSpan VisibleDuration = TimeSpan.FromMilliseconds(1800);
    public IReadOnlyList<DesktopNameToastForm> VisibleForms => _toasts.Values.Select(t => t.Form).Where(f => f.Visible).ToArray();

    public DesktopNameDisplayMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_mode == value || _disposed) return;
            _mode = value;
            DismissAll();
            QueueAlways();
        }
    }

    // The overview already displays desktop names. Hide passive labels so its controls remain clear.
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value || _disposed) return;
            _suspended = value;
            if (value) DismissAll();
            else
            {
                QueueAlways();
                QueuePending();
            }
        }
    }

    public DesktopToastCorner Corner
    {
        get => _corner;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_disposed) return;
            _corner = value;
            foreach (var screen in Screen.AllScreens)
                if (_toasts.TryGetValue(screen.DeviceName, out var toast)) toast.Form.Reposition(screen, value);
        }
    }

    public DesktopNameToastCoordinator(DesktopController controller, Control dispatcher, DesktopNameSettings? settings = null)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        settings ??= new();
        _mode = settings.Mode;
        _corner = settings.Corner;
        _active = ReadActive(controller.Catalog.Snapshot());
        _controller.Changed += OnChanged;
        _controller.SwitchBoundaryReached += OnSwitchBoundaryReached;
        UiText.Changed += OnLanguageChanged;
        _timer.Tick += (_, _) => Tick();
        QueueAlways();
    }

    private Dictionary<string, ActiveDesktop> ReadActive(IReadOnlyList<DisplaySnapshot> displays)
    {
        int linkedCount = _controller.SwitchingMode == DisplaySwitchingMode.Linked && displays.Count > 0
            ? displays.Max(d => d.Desktops.Count) : 0;
        return displays.ToDictionary(d => d.Id, d =>
        {
            int index = d.Desktops.ToList().FindIndex(s => s.Id == d.ActiveDesktopId);
            return new ActiveDesktop(d.ActiveDesktopId, UiText.DesktopName(d.Desktops[index]), UiText.DisplayName(d),
                index + 1, linkedCount > 0 ? linkedCount : d.Desktops.Count);
        });
    }

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        _active = ReadActive(_controller.Catalog.Snapshot());
        // Boundary captions also need translating when the desktop itself has a custom name.
        foreach (var screen in Screen.AllScreens)
            if (_toasts.TryGetValue(screen.DeviceName, out var toast) && toast.Form.Visible
                && _active.TryGetValue(screen.DeviceName, out var desktop)) Show(toast.Form, screen, desktop);
    }

    private void OnSwitchBoundaryReached(string displayId)
    {
        if (_disposed || _controller.Paused || Mode == DesktopNameDisplayMode.Off) return;
        _active = ReadActive(_controller.Catalog.Snapshot());
        if (!_active.ContainsKey(displayId)) return;
        _pending.Add(displayId);
        QueuePending();
    }

    private void OnChanged()
    {
        if (_disposed) return;
        var next = ReadActive(_controller.Catalog.Snapshot());
        foreach (var id in _toasts.Keys.Where(id => !next.ContainsKey(id)).ToArray())
        { _toasts[id].Form.Dispose(); _toasts.Remove(id); _pending.Remove(id); }
        _pending.RemoveWhere(id => !next.ContainsKey(id));
        var previousActive = _active;
        _active = next;
        if (_controller.Paused || Mode == DesktopNameDisplayMode.Off) { DismissAll(); return; }
        foreach (var (id, desktop) in next)
        {
            previousActive.TryGetValue(id, out var previous);
            if ((previous is not null && previous.Id != desktop.Id)
                || (Mode == DesktopNameDisplayMode.Always
                    && (!_toasts.TryGetValue(id, out var current) || !current.Form.Visible)))
                _pending.Add(id);
            else if (_toasts.TryGetValue(id, out var toast) && toast.Form.Visible && previous != desktop)
            {
                // Renaming an active desktop updates its visible label without extending its lifetime.
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == id);
                if (screen is not null) Show(toast.Form, screen, desktop);
            }
        }
        QueuePending();
    }

    private void QueueAlways()
    {
        if (_disposed || _controller.Paused || Mode != DesktopNameDisplayMode.Always) return;
        _pending.UnionWith(_active.Keys);
        QueuePending();
    }

    private void QueuePending()
    {
        if (_disposed || _controller.Paused || Suspended || Mode == DesktopNameDisplayMode.Off
            || _pending.Count == 0 || _posted || _dispatcher.IsDisposed) return;
        _posted = true;
        // Overlay selection closes its full-screen forms before this notification appears.
        _dispatcher.BeginInvoke(() =>
        {
            _posted = false;
            if (_disposed || _controller.Paused || Suspended || Mode == DesktopNameDisplayMode.Off) return;
            var screens = Screen.AllScreens.ToDictionary(s => s.DeviceName);
            foreach (var id in _pending)
            {
                if (!screens.TryGetValue(id, out var screen) || !_active.TryGetValue(id, out var desktop)) continue;
                var form = _toasts.TryGetValue(id, out var existing) ? existing.Form : new DesktopNameToastForm();
                // Register ownership before showing, so disposal still reaches a partially initialized form.
                _toasts[id] = new Toast(form, Mode == DesktopNameDisplayMode.Always ? long.MaxValue
                    : Stopwatch.GetTimestamp() + (long)(VisibleDuration.TotalSeconds * Stopwatch.Frequency));
                Show(form, screen, desktop);
            }
            _pending.Clear();
            _timer.Interval = Mode == DesktopNameDisplayMode.Always ? 250 : 50;
            _timer.Start();
        });
    }

    private void Show(DesktopNameToastForm form, Screen screen, ActiveDesktop desktop) =>
        form.ShowName(screen, desktop.DisplayName, desktop.Name, desktop.Position, desktop.Count, Corner);

    private void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        var screens = Screen.AllScreens.ToDictionary(s => s.DeviceName);
        foreach (var (id, toast) in _toasts.ToArray())
        {
            if (!screens.TryGetValue(id, out var screen))
            { toast.Form.Dispose(); _toasts.Remove(id); continue; }
            if (!toast.Form.Visible) continue;
            if (Mode != DesktopNameDisplayMode.Always && now >= toast.ExpiresAt) toast.Form.Dismiss();
            else
            {
                toast.Form.Reposition(screen, Corner);
                if (Mode != DesktopNameDisplayMode.Always)
                {
                    double remainingMs = (toast.ExpiresAt - now) * 1000.0 / Stopwatch.Frequency;
                    toast.Form.Opacity = 0.96 * Math.Min(1, remainingMs / 200);
                }
            }
        }
        if (_toasts.Values.All(t => !t.Form.Visible)) _timer.Stop();
    }

    public void DismissAll()
    {
        _pending.Clear();
        foreach (var toast in _toasts.Values) toast.Form.Dismiss();
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= OnChanged;
        _controller.SwitchBoundaryReached -= OnSwitchBoundaryReached;
        UiText.Changed -= OnLanguageChanged;
        _timer.Dispose();
        foreach (var toast in _toasts.Values) toast.Form.Dispose();
        _toasts.Clear();
        _pending.Clear();
    }
}
