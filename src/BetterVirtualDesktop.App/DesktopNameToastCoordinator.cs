using System.Diagnostics;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>Reports active desktop changes, regardless of whether they came from a shortcut or UI.</summary>
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
    internal static readonly TimeSpan VisibleDuration = TimeSpan.FromMilliseconds(1800);
    public IReadOnlyList<DesktopNameToastForm> VisibleForms => _toasts.Values.Select(t => t.Form).Where(f => f.Visible).ToArray();

    public DesktopToastCorner Corner
    {
        get => _corner;
        set
        {
            _corner = value;
            foreach (var screen in Screen.AllScreens)
                if (_toasts.TryGetValue(screen.DeviceName, out var toast)) toast.Form.Reposition(screen, value);
        }
    }

    public DesktopNameToastCoordinator(DesktopController controller, Control dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _active = ReadActive(controller.Catalog.Snapshot());
        _controller.Changed += OnChanged;
        UiText.Changed += OnLanguageChanged;
        _timer.Tick += (_, _) => Tick();
    }

    private static Dictionary<string, ActiveDesktop> ReadActive(IReadOnlyList<DisplaySnapshot> displays) =>
        displays.ToDictionary(d => d.Id, d =>
        {
            int index = d.Desktops.ToList().FindIndex(s => s.Id == d.ActiveDesktopId);
            return new ActiveDesktop(d.ActiveDesktopId, UiText.DesktopName(d.Desktops[index]), UiText.DisplayName(d), index + 1, d.Desktops.Count);
        });

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        _active = ReadActive(_controller.Catalog.Snapshot());
        // Update only existing notifications and retain their original expiration times.
        foreach (var screen in Screen.AllScreens)
            if (_toasts.TryGetValue(screen.DeviceName, out var toast) && toast.Form.Visible
                && _active.TryGetValue(screen.DeviceName, out var desktop)) Show(toast.Form, screen, desktop);
    }

    private void OnChanged()
    {
        if (_disposed) return;
        var next = ReadActive(_controller.Catalog.Snapshot());
        foreach (var id in _toasts.Keys.Where(id => !next.ContainsKey(id)).ToArray())
        { _toasts[id].Form.Dispose(); _toasts.Remove(id); _pending.Remove(id); }
        foreach (var (id, desktop) in next)
        {
            if (_active.TryGetValue(id, out var previous) && previous.Id != desktop.Id)
                _pending.Add(id);
            else if (_toasts.TryGetValue(id, out var toast) && toast.Form.Visible && previous != desktop)
            {
                // Renaming an active desktop updates its visible label without extending its lifetime.
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == id);
                if (screen is not null) Show(toast.Form, screen, desktop);
            }
        }
        _active = next;
        if (_controller.Paused) { DismissAll(); return; }
        if (_pending.Count == 0 || _posted) return;
        _posted = true;
        // Overlay selection closes its full-screen forms before this notification appears.
        _dispatcher.BeginInvoke(() =>
        {
            _posted = false;
            if (_disposed || _controller.Paused) return;
            var screens = Screen.AllScreens.ToDictionary(s => s.DeviceName);
            foreach (var id in _pending)
            {
                if (!screens.TryGetValue(id, out var screen) || !_active.TryGetValue(id, out var desktop)) continue;
                var form = _toasts.TryGetValue(id, out var existing) ? existing.Form : new DesktopNameToastForm();
                // Register ownership before showing, so disposal still reaches a partially initialized form.
                _toasts[id] = new Toast(form, Stopwatch.GetTimestamp() + (long)(VisibleDuration.TotalSeconds * Stopwatch.Frequency));
                Show(form, screen, desktop);
            }
            _pending.Clear();
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
            if (now >= toast.ExpiresAt) toast.Form.Dismiss();
            else
            {
                toast.Form.Reposition(screen, Corner);
                double remainingMs = (toast.ExpiresAt - now) * 1000.0 / Stopwatch.Frequency;
                toast.Form.Opacity = 0.96 * Math.Min(1, remainingMs / 200);
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
        UiText.Changed -= OnLanguageChanged;
        _timer.Dispose();
        foreach (var toast in _toasts.Values) toast.Form.Dispose();
        _toasts.Clear();
        _pending.Clear();
    }
}
