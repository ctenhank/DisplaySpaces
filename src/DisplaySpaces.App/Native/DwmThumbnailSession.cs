using System.Runtime.InteropServices;

namespace DisplaySpaces.App.Native;

/// <summary>
/// Owns documented DWM thumbnail registrations for one top-level overlay window.
/// The compositor draws these previews directly; DrawToBitmap cannot capture them.
/// Use only on the overlay's UI thread and dispose before its HWND is destroyed.
/// </summary>
internal sealed class DwmThumbnailSession(nint destination) : IDisposable
{
    private readonly Dictionary<(long Handle, int Occurrence), nint> _thumbnails = [];
    private bool _disposed;

    public int RegistrationCount => _thumbnails.Count;

    public void Update(IEnumerable<(long Handle, Rectangle Bounds)> previews)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var seen = new HashSet<(long Handle, int Occurrence)>();
        var occurrences = new Dictionary<long, int>();
        foreach (var (handle, bounds) in previews)
        {
            if (handle == 0 || bounds.Width <= 0 || bounds.Height <= 0
                || !NativeWindows.IsVisible(handle) || NativeWindows.IsMinimized(handle)) continue;
            var occurrence = occurrences.GetValueOrDefault(handle);
            occurrences[handle] = occurrence + 1;
            var key = (handle, occurrence);
            if (!_thumbnails.TryGetValue(key, out var thumbnail))
            {
                if (DwmRegisterThumbnail(destination, new nint(handle), out thumbnail) < 0) continue;
                _thumbnails.Add(key, thumbnail);
            }

            var fitted = bounds;
            if (GetClientRect(new nint(handle), out var client) && client.Right > client.Left && client.Bottom > client.Top)
            {
                var sourceWidth = (long)client.Right - client.Left;
                var sourceHeight = (long)client.Bottom - client.Top;
                var scale = Math.Min((double)bounds.Width / sourceWidth, (double)bounds.Height / sourceHeight);
                var width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, bounds.Width);
                var height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, bounds.Height);
                fitted = new Rectangle(bounds.X + (bounds.Width - width) / 2,
                    bounds.Y + (bounds.Height - height) / 2, width, height);
            }
            var properties = new ThumbnailProperties
            {
                Flags = 0x1 | 0x4 | 0x8 | 0x10, // destination, opacity, visible, client-area flag
                Destination = new NativeRect(fitted),
                Opacity = byte.MaxValue,
                Visible = true,
                SourceClientAreaOnly = true
            };
            if (DwmUpdateThumbnailProperties(thumbnail, ref properties) >= 0) seen.Add(key);
        }

        foreach (var key in _thumbnails.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            DwmUnregisterThumbnail(_thumbnails[key]);
            _thumbnails.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (var thumbnail in _thumbnails.Values) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(Rectangle bounds)
    {
        public int Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
