using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DisplaySpaces.App.Native;

/// <summary>
/// Memory-only, bounded snapshots for hidden/minimized windows and GDI rendering.
/// Request, TryGet, Changed and Dispose belong to the creating UI thread. Borrowed
/// bitmaps remain valid until the next UI callback; callers must not dispose them.
/// PrintWindow can block in another process, so exactly one background thread owns
/// capture. A stuck app can stall snapshots, but never the UI or create more workers.
/// </summary>
internal sealed class WindowPreviewCache : IDisposable
{
    private const int MaximumRequests = 64;
    private const int MaximumEntries = 48;
    private const long MaximumCachedPixels = 12_000_000;
    private const long MaximumCapturePixels = 16_000_000;
    private readonly object _sync = new();
    private readonly Control _dispatcher = new();
    private readonly Queue<NativeWindowInfo> _requests = [];
    private readonly HashSet<long> _queued = [];
    private readonly Queue<(NativeWindowInfo Window, Bitmap Image)> _completed = [];
    private readonly Dictionary<long, NativeWindowInfo> _known = [];
    private readonly Dictionary<long, Entry> _images = [];
    private readonly Dictionary<long, long> _lastRequested = [];
    private Thread? _worker;
    private bool _disposed;
    private long _cachedPixels;
    private long _completedPixels;

    public event Action? Changed;
    public int Count => _images.Count;

    public WindowPreviewCache() => _ = _dispatcher.Handle;

    /// <summary>Pass the complete tracked-window snapshot, including hidden windows.</summary>
    public void Request(IEnumerable<NativeWindowInfo> windows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = windows.ToDictionary(window => window.Handle);
        foreach (var handle in _images.Keys.Where(handle => !current.TryGetValue(handle, out var window)
                     || !SameIdentity(_images[handle].Window, window)).ToArray()) Remove(handle);
        foreach (var handle in _lastRequested.Keys.Where(handle => !current.ContainsKey(handle)).ToArray())
            _lastRequested.Remove(handle);
        _known.Clear();
        foreach (var window in current.Values) _known.Add(window.Handle, window);

        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var window in current.Values)
            {
                if (!window.IsVisible || window.IsMinimized || window.IsCloaked || window.ProcessStartTicks <= 0
                    || _queued.Contains(window.Handle) || _requests.Count >= MaximumRequests) continue;
                if (_lastRequested.TryGetValue(window.Handle, out var last)
                    && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1)) continue;
                _lastRequested[window.Handle] = now;
                _requests.Enqueue(window);
                _queued.Add(window.Handle);
            }
            if (_requests.Count > 0 && _worker is null)
            {
                _worker = new Thread(CaptureLoop) { IsBackground = true, Name = "Window preview snapshots" };
                _worker.Start();
            }
            Monitor.Pulse(_sync);
        }
    }

    public Bitmap? TryGet(long handle)
    {
        if (_disposed || !_images.TryGetValue(handle, out var entry)) return null;
        entry.LastUsed = Stopwatch.GetTimestamp();
        return entry.Image;
    }

    private void CaptureLoop()
    {
        while (true)
        {
            NativeWindowInfo window;
            lock (_sync)
            {
                while (!_disposed && _requests.Count == 0) Monitor.Wait(_sync);
                if (_disposed) return;
                window = _requests.Dequeue();
            }
            Bitmap? image = null;
            try { image = Capture(window); }
            catch (Exception exception) when (exception is ArgumentException or ExternalException
                                               or InvalidOperationException or OutOfMemoryException)
            { /* A missing preview is represented honestly by the overlay placeholder. */ }

            lock (_sync)
            {
                _queued.Remove(window.Handle);
                if (_disposed) { image?.Dispose(); return; }
                if (image is null) continue;
                // Bound pending images too, even if the UI cannot drain callbacks.
                _completed.Enqueue((window, image));
                _completedPixels += (long)image.Width * image.Height;
                while (_completed.Count > MaximumRequests || _completedPixels > MaximumCachedPixels)
                {
                    var removed = _completed.Dequeue().Image;
                    _completedPixels -= (long)removed.Width * removed.Height;
                    removed.Dispose();
                }
            }
            try { _dispatcher.BeginInvoke(DrainCompleted); }
            catch (InvalidOperationException) { /* Dispose drains any queued images. */ }
        }
    }

    private void DrainCompleted()
    {
        if (_disposed) return;
        var changed = false;
        while (true)
        {
            (NativeWindowInfo Window, Bitmap Image) completed;
            lock (_sync)
            {
                if (_completed.Count == 0) break;
                completed = _completed.Dequeue();
                _completedPixels -= (long)completed.Image.Width * completed.Image.Height;
            }
            if (!_known.TryGetValue(completed.Window.Handle, out var current)
                || !SameIdentity(completed.Window, current)) { completed.Image.Dispose(); continue; }
            Remove(completed.Window.Handle);
            _images.Add(completed.Window.Handle, new Entry(completed.Window, completed.Image));
            _cachedPixels += (long)completed.Image.Width * completed.Image.Height;
            changed = true;
        }
        while (_images.Count > MaximumEntries || _cachedPixels > MaximumCachedPixels)
            Remove(_images.MinBy(pair => pair.Value.LastUsed).Key);
        if (changed) Changed?.Invoke();
    }

    private static Bitmap? Capture(NativeWindowInfo window)
    {
        var handle = new nint(window.Handle);
        if (!NativeWindows.IsSameWindow(window.Handle, window.ProcessId, window.ProcessStartTicks, window.ClassName)
            || !NativeWindows.IsVisible(window.Handle) || NativeWindows.IsMinimized(window.Handle)
            || IsHungAppWindow(handle) || DwmGetWindowAttribute(handle, 14, out uint cloaked, sizeof(uint)) < 0
            || cloaked != 0 || !GetClientRect(handle, out var bounds)) return null;
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || width * height > MaximumCapturePixels) return null;
        if (GetWindowDisplayAffinity(handle, out var affinity) && affinity != 0) return null;

        using var original = new Bitmap((int)width, (int)height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(original))
        {
            graphics.Clear(Color.Black);
            var dc = graphics.GetHdc();
            try
            {
                // PW_CLIENTONLY avoids duplicating the overlay's title bar;
                // PW_RENDERFULLCONTENT asks supported accelerated apps to render too.
                // This call is intentionally confined to the one background worker.
                if (!PrintWindow(handle, dc, 0x1 | 0x2)) return null;
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        if (!NativeWindows.IsSameWindow(window.Handle, window.ProcessId, window.ProcessStartTicks, window.ClassName)
            || IsBlack(original)) return null;
        var scale = Math.Min(1d, Math.Min(960d / width, 600d / height));
        var result = new Bitmap(Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)), PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, new Rectangle(Point.Empty, result.Size));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool IsBlack(Bitmap image)
    {
        // Protected or unsupported capture often reports success with a black image.
        // Do not present that as a working preview. Legitimate dark content with any
        // nonblack sample still remains eligible.
        for (var y = 1; y <= 5; y++)
        for (var x = 1; x <= 7; x++)
        {
            var color = image.GetPixel((image.Width - 1) * x / 8, (image.Height - 1) * y / 6);
            if (color.R > 4 || color.G > 4 || color.B > 4) return false;
        }
        return true;
    }

    private static bool SameIdentity(NativeWindowInfo a, NativeWindowInfo b) =>
        a.Handle == b.Handle && a.ProcessId == b.ProcessId && a.ProcessStartTicks == b.ProcessStartTicks
        && a.ClassName == b.ClassName;

    private void Remove(long handle)
    {
        if (!_images.Remove(handle, out var entry)) return;
        _cachedPixels -= (long)entry.Image.Width * entry.Image.Height;
        entry.Image.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _requests.Clear();
            _queued.Clear();
            while (_completed.Count > 0) _completed.Dequeue().Image.Dispose();
            _completedPixels = 0;
            Monitor.Pulse(_sync);
        }
        _dispatcher.Dispose();
        foreach (var entry in _images.Values) entry.Image.Dispose();
        _images.Clear();
        _known.Clear();
        _lastRequested.Clear();
        // Never join a worker that may be inside another application's PrintWindow.
        // Its sole in-flight bitmap is disposed when the native call returns.
    }

    private sealed class Entry(NativeWindowInfo window, Bitmap image)
    {
        public NativeWindowInfo Window { get; } = window;
        public Bitmap Image { get; } = image;
        public long LastUsed { get; set; } = Stopwatch.GetTimestamp();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint handle, nint destination, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out uint value, int size);
}
