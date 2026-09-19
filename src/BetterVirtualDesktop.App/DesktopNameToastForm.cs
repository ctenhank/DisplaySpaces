using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BetterVirtualDesktop.App.Localization;

namespace BetterVirtualDesktop.App;

internal enum DesktopToastCorner
{
    TopRight,
    TopLeft
}

/// <summary>A passive, click-through indicator for one display's current desktop.</summary>
internal sealed class DesktopNameToastForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly Color Canvas = Color.FromArgb(24, 31, 44);
    private static readonly Color Accent = Color.FromArgb(142, 186, 255);
    private static readonly Color Muted = Color.FromArgb(179, 190, 210);
    private Font? _nameFont;
    private Font? _captionFont;
    private int _fontDpi;
    private string _fontFamily = "";
    private string _caption = "";
    private Screen? _screen;
    private DesktopToastCorner _corner;
    private bool _positioning;
    private int _contentVersion;
    private int _positionedContentVersion = -1;
    private Rectangle _lastArea;
    private Rectangle _lastBounds;
    private string? _lastDisplayId;
    private DesktopToastCorner _lastCorner;
    private int _lastDpi;
    private Size _regionSize;
    private int _regionDpi;

    public string DesktopName { get; private set; } = "";
    public string DisplayId { get; private set; } = "";

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle = (parameters.ExStyle & ~WsExAppWindow) |
                WsExNoActivate | WsExToolWindow | WsExTransparent | WsExLayered;
            return parameters;
        }
    }

    public DesktopNameToastForm()
    {
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        DoubleBuffered = true;
        BackColor = Canvas;
        ForeColor = Color.White;
        AccessibleRole = AccessibleRole.Alert;
    }

    public void ShowName(Screen screen, string displayName, string desktopName,
        int position, int count, DesktopToastCorner corner)
    {
        if (IsDisposed) return;
        DisplayId = screen.DeviceName;
        DesktopName = desktopName;
        _caption = UiText.Get("Toast.Caption", displayName, position, count);
        _contentVersion++;
        Text = $"{desktopName} · BetterVirtualDesktop";
        AccessibleName = UiText.Get("Toast.Accessible", desktopName, displayName, count, position);
        Reposition(screen, corner);
        // Set opacity only after creating the HWND on its destination display.
        Opacity = 0.96;
        Invalidate();
        if (!Visible) Show();
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public void Reposition(Screen screen, DesktopToastCorner corner)
    {
        if (IsDisposed) return;
        _screen = screen;
        _corner = corner;
        if (_positioning) return;
        _positioning = true;
        try
        {
            var area = screen.WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) area = screen.Bounds;
            if (IsHandleCreated && _positionedContentVersion == _contentVersion &&
                _lastDisplayId == screen.DeviceName && _lastArea == area && _lastCorner == corner &&
                _lastDpi == DeviceDpi && _lastBounds == Bounds) return;

            if (!IsHandleCreated)
            {
                // The initial HWND must already lie on the destination monitor:
                // otherwise DeviceDpi still describes the primary display.
                Bounds = CornerBounds(area, Math.Min(300, area.Width), Math.Min(90, area.Height), 20, corner);
                _ = Handle;
            }
            else if (!string.Equals(Screen.FromHandle(Handle).DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                // Moving the HWND first delivers WM_DPICHANGED synchronously.
                // Both this provisional move and the final placement preserve focus.
                var provisional = CornerBounds(area, Math.Min(Width, area.Width), Math.Min(Height, area.Height), S(20), corner);
                Place(provisional);
            }

            EnsureFonts();
            using var graphics = CreateGraphics();
            const TextFormatFlags measurementFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int nameWidth = TextRenderer.MeasureText(graphics, DesktopName, _nameFont!,
                new Size(int.MaxValue, S(36)), measurementFlags).Width;
            int captionWidth = TextRenderer.MeasureText(graphics, _caption, _captionFont!,
                new Size(int.MaxValue, S(24)), measurementFlags).Width;
            int width = Math.Clamp(Math.Max(nameWidth, captionWidth) + S(56), S(300), S(420));
            Place(CornerBounds(area, width, S(90), S(20), corner));
            _positionedContentVersion = _contentVersion;
            _lastDisplayId = screen.DeviceName;
            _lastArea = area;
            _lastCorner = corner;
            _lastDpi = DeviceDpi;
            _lastBounds = Bounds;
            UpdateRoundedRegion();
            Invalidate();
        }
        finally
        {
            _positioning = false;
        }
    }

    public void Dismiss()
    {
        if (!IsDisposed) Hide();
    }

    private static Rectangle CornerBounds(Rectangle area, int width, int height, int margin, DesktopToastCorner corner)
    {
        int horizontalMargin = Math.Min(margin, Math.Max(0, (area.Width - 1) / 2));
        int verticalMargin = Math.Min(margin, Math.Max(0, (area.Height - 1) / 2));
        width = Math.Clamp(width, 1, Math.Max(1, area.Width - horizontalMargin * 2));
        height = Math.Clamp(height, 1, Math.Max(1, area.Height - verticalMargin * 2));
        int left = corner == DesktopToastCorner.TopLeft
            ? area.Left + horizontalMargin
            : area.Right - horizontalMargin - width;
        return new Rectangle(left, area.Top + verticalMargin, width, height);
    }

    private void Place(Rectangle bounds)
    {
        if (Bounds != bounds) SetWindowPos(Handle, HwndTopmost,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpNoOwnerZOrder);
    }

    private int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96.0));

    private void EnsureFonts()
    {
        if (_fontDpi == DeviceDpi && _fontFamily == UiText.FontFamily && _nameFont is not null && _captionFont is not null) return;
        _nameFont?.Dispose();
        _captionFont?.Dispose();
        _fontFamily = UiText.FontFamily;
        _nameFont = new Font(_fontFamily, 20f * DeviceDpi / 96f, FontStyle.Bold, GraphicsUnit.Pixel);
        _captionFont = new Font(_fontFamily, 12f * DeviceDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontDpi = DeviceDpi;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureFonts();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (ClientSize.Width > 1 && ClientSize.Height > 1)
        {
            using var outline = RoundedRectangle(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1), S(14));
            using var border = new Pen(Color.FromArgb(65, 79, 102));
            e.Graphics.DrawPath(border, outline);
        }
        using var accent = new SolidBrush(Accent);
        using var marker = RoundedRectangle(new RectangleF(S(18), S(23), S(4), S(43)), S(2));
        e.Graphics.FillPath(accent, marker);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int textWidth = Math.Max(1, ClientSize.Width - S(54));
        TextRenderer.DrawText(e.Graphics, DesktopName, _nameFont!,
            new Rectangle(S(34), S(15), textWidth, S(34)), ForeColor, flags);
        TextRenderer.DrawText(e.Graphics, _caption, _captionFont!,
            new Rectangle(S(34), S(50), textWidth, S(24)), Muted, flags);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        EnsureFonts();
        if (_screen is not null && !_positioning) Reposition(_screen, _corner);
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        if (_regionSize == ClientSize && _regionDpi == DeviceDpi) return;
        using var path = RoundedRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), S(14));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
        _regionSize = ClientSize;
        _regionDpi = DeviceDpi;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            message.Result = (nint)3; // MA_NOACTIVATE
            return;
        }
        if (message.Msg == 0x0084) // WM_NCHITTEST
        {
            message.Result = (nint)(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont?.Dispose();
            _captionFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
