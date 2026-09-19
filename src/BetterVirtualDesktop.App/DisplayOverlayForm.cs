using System.Drawing.Drawing2D;
using System.Diagnostics;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>A task view for one screen: large window previews above a desktop strip.</summary>
internal sealed class DisplayOverlayForm : Form
{
    private static readonly Color Surface = Color.FromArgb(20, 20, 22);
    private static readonly Color Muted = Color.FromArgb(190, 192, 198);
    private static readonly Color Accent = Color.FromArgb(91, 196, 247);
    private readonly Dictionary<long, NativeWindowInfo> _windows = new();
    private readonly List<HitTarget> _targets = new();
    private readonly List<Rectangle> _windowTiles = new();
    private readonly ToolTip _tips = new() { InitialDelay = 650, AutoPopDelay = 6000 };
    private Font _titleFont = new(UiText.FontFamily, 12);
    private Font _smallFont = new(UiText.FontFamily, 11);
    private Font _headerFont = new(UiText.FontFamily, 16, FontStyle.Bold);
    private Font _messageFont = new(UiText.FontFamily, 23, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 16 };
    private readonly Dictionary<string, float> _hoverLevels = new();
    private readonly WindowPreviewCache? _previews;
    private DwmThumbnailSession? _live;
    private ContextMenuStrip? _menu;
    private DisplaySnapshot _display;
    private IReadOnlyList<DisplaySnapshot> _displays;
    private Guid _selected;
    private Guid? _dropDesktop;
    private bool _dropNewDesktop;
    private int _firstDesktop;
    private int _windowOffset;
    private int _desktopSlots = 1;
    private const int WindowSlots = 12;
    private Rectangle _gallery;
    private Rectangle _rail;
    private string _signature = "";
    private string _tip = "";
    private string _status = "";
    private bool _closing;
    private bool _dragging;
    private bool _suppressMouseUp;
    private string? _hoveredTargetId;
    private string? _pressedTargetId;
    private MouseButtons _pressedButton;
    private bool _pressedBackground;
    private bool _releasingCapture;
    private bool _pointerInside;
    private Point _pointerLocation;
    private bool _layoutDirty = true;
    private Point _mouseDown;
    private long? _pressedWindow;
    private readonly Guid _dragSession;
    private Guid? _creationDesktop;
    private long _creationStarted;
    private bool _creationMovedWindow;
    internal static readonly TimeSpan CreationFeedbackDuration = TimeSpan.FromMilliseconds(3200);

    private sealed record HitTarget(string Id, Rectangle Bounds, Action Invoke, Action? Context, string Hint,
        Guid? DesktopId = null, long? WindowHandle = null, bool CreatesDesktop = false);
    private sealed record WindowDrag(long Handle);

    public string DisplayId => _display.Id;
    public Guid SelectedDesktopId => _selected;
    public IReadOnlyList<Guid> DesktopIds => _display.Desktops.Select(d => d.Id).ToArray();
    public IReadOnlyList<long> DisplayWindowIds => _display.Desktops.SelectMany(d => d.WindowIds).ToArray();
    public IReadOnlyList<Rectangle> WindowTileBounds => _windowTiles;
    public Rectangle DesktopRailBounds => _rail;
    internal Rectangle DesktopCardBounds(Guid desktop) => _targets.First(t => t.DesktopId == desktop).Bounds;
    internal Rectangle NewDesktopCardBounds => _targets.First(t => t.CreatesDesktop).Bounds;
    internal string? HoveredTargetId => _hoveredTargetId;
    internal string? PressedTargetId => _pressedButton == MouseButtons.Left && _pressedTargetId == _hoveredTargetId ? _pressedTargetId : null;
    internal bool HasCreationFeedback => _creationDesktop.HasValue && Stopwatch.GetElapsedTime(_creationStarted) < CreationFeedbackDuration;
    internal Rectangle CreationFeedbackBounds { get; private set; }
    internal Rectangle CreationOpenButtonBounds => _targets.FirstOrDefault(t => t.Id == $"feedback:open:{_creationDesktop}")?.Bounds ?? Rectangle.Empty;
    internal string? CreationFeedbackTitle => HasCreationFeedback && _display.Desktops.FirstOrDefault(d => d.Id == _creationDesktop) is { } desktop
        ? UiText.Get("NewDesktopCreatedTitle", UiText.DesktopName(desktop)) : null;
    public int LivePreviewCount => _live?.RegistrationCount ?? 0;
    internal bool HasCachedPreview(long handle) => _previews?.TryGet(handle) is not null;
    public bool HasOpenTransientUi => _dragging || _menu is { Visible: true } || OwnedForms.Any(f => f.Visible);
    public bool HasInteractionFocus => ContainsFocus || HasOpenTransientUi;
    public event Action? KeyboardFocusRequested;
    public event Action? DragEnded;
    public event Action? DismissRequested;
    public event Action? ShortcutSettingsRequested;
    public event Action<string, Guid, long?>? SelectionRequested;
    public event Action<string>? AddRequested;
    public event Action<string, Guid>? RemoveRequested;
    public event Action<string, Guid, string>? RenameRequested;
    public event Action<long, string, Guid>? MoveToDisplayRequested;
    public event Action<long, string>? MoveToNewDesktopRequested;
    protected override bool ShowWithoutActivation => true;

    public DisplayOverlayForm(Screen screen, DisplaySnapshot display, WindowPreviewCache? previews = null, Guid? dragSession = null)
    {
        Icon = AppIcon.Window;
        _display = display;
        _displays = [display];
        _selected = display.ActiveDesktopId;
        _previews = previews;
        _dragSession = dragSession ?? Guid.NewGuid();
        Text = $"BetterVirtualDesktop · {UiText.DisplayName(display)}";
        AccessibleName = UiText.Get("OverlayAccessible", UiText.DisplayName(display));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(UiText.FontFamily, 12);
        BackColor = Color.FromArgb(52, 52, 54);
        ForeColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = true;
        AllowDrop = true;
        Bounds = screen.Bounds;
        FormClosing += (_, e) => { if (!_closing) { e.Cancel = true; DismissRequested?.Invoke(); } };
        Resize += (_, _) => InvalidateLayout();
        DpiChanged += (_, _) => InvalidateLayout();
        _feedbackTimer.Tick += (_, _) => AnimateFeedback();
    }

    public void UpdateSnapshot(Screen screen, DisplaySnapshot display, IReadOnlyDictionary<long, NativeWindowInfo> windows,
        IReadOnlyList<DisplaySnapshot>? displays = null)
    {
        _displays = displays ?? [display];
        string signature = $"{UiText.CurrentLanguageCode}/{display.Name}/{display.ActiveDesktopId}/" + string.Join("|", display.Desktops.Select(d =>
            $"{d.Id}:{d.Name}:{d.DefaultNumber}:" + string.Join(',', d.WindowIds.Select(h => windows.TryGetValue(h, out var w)
                ? $"{h}-{w.Title}-{w.Bounds}-{w.IsVisible}-{w.IsMinimized}" : h.ToString()))));
        if (Bounds != screen.Bounds) Bounds = screen.Bounds;
        if (_signature == signature) return;
        _signature = signature;
        if (_display.ActiveDesktopId != display.ActiveDesktopId || !display.Desktops.Any(d => d.Id == _selected))
        { _selected = display.ActiveDesktopId; _windowOffset = 0; }
        _display = display;
        if (_creationDesktop.HasValue && !display.Desktops.Any(d => d.Id == _creationDesktop)) _creationDesktop = null;
        _windows.Clear();
        foreach (var handle in DisplayWindowIds)
            if (windows.TryGetValue(handle, out var window)) _windows.Add(handle, window);
        KeepSelectionVisible();
        InvalidateLayout();
    }

    private int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96.0));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(57, 57, 59), Color.FromArgb(43, 43, 46), 85f);
        g.FillRectangle(background, ClientRectangle);
        _targets.Clear();
        _windowTiles.Clear();
        int margin = Math.Min(S(28), ClientSize.Width / 16);
        var selected = _display.Desktops.First(d => d.Id == _selected);
        var active = _display.Desktops.First(d => d.Id == _display.ActiveDesktopId);
        int headingWidth = Math.Max(1, ClientSize.Width - 2 * margin - S(398));
        DrawText(g, UiText.Get("PreviewHeader", UiText.DisplayName(_display), UiText.DesktopName(selected)), _headerFont, Color.White,
            new Rectangle(margin, S(13), headingWidth, S(35)));
        bool compactFeedback = HasCreationFeedback && ClientSize.Height < S(450);
        if (!compactFeedback)
            DrawText(g, selected.Id == active.Id ? UiText.Get("ActiveDesktop") : UiText.Get("CurrentDesktopHint", UiText.DesktopName(active)),
                _smallFont, selected.Id == active.Id ? Accent : Muted, new Rectangle(margin, S(51), ClientSize.Width - 2 * margin, S(28)));
        ActionButton(g, "shortcuts", new Rectangle(ClientSize.Width - margin - S(382), S(15), S(164), S(36)),
            UiText.Get("Shortcuts.Open"), () => ShortcutSettingsRequested?.Invoke());
        ActionButton(g, "language", new Rectangle(ClientSize.Width - margin - S(208), S(15), S(102), S(36)),
            UiText.Get("Language"), OpenLanguageMenu);
        ActionButton(g, "close", new Rectangle(ClientSize.Width - margin - S(96), S(15), S(96), S(36)), UiText.Get("Close"), CancelOverlay);

        int railHeight = Math.Min(S(176), Math.Max(S(ClientSize.Height < S(360) ? 88 : 120), ClientSize.Height / 5));
        _rail = new Rectangle(S(7), ClientSize.Height - railHeight - S(10), Math.Max(1, ClientSize.Width - S(14)), railHeight);
        CreationFeedbackBounds = HasCreationFeedback ? CalculateFeedbackBounds(margin) : Rectangle.Empty;
        int galleryTop = HasCreationFeedback ? CreationFeedbackBounds.Bottom + S(16) : S(98);
        _gallery = Rectangle.FromLTRB(margin, galleryTop, ClientSize.Width - margin, _rail.Top - S(compactFeedback ? 12 : 48));
        if (_gallery.Height >= S(70)) DrawGallery(g, selected);
        else _live?.Clear();
        DrawDesktopRail(g);
        if (HasCreationFeedback) DrawCreationFeedback(g);
        string help = _status.Length > 0 ? _status : UiText.Get("OverlayHelp");
        if (selected.WindowIds.Count > WindowSlots) help += UiText.Get("MoreWindows", _windowOffset + 1, Math.Min(_windowOffset + WindowSlots, selected.WindowIds.Count));
        if (!compactFeedback)
            DrawText(g, help, _smallFont, Muted, new Rectangle(margin, _rail.Top - S(35), ClientSize.Width - margin * 2, S(26)), true);
        _layoutDirty = false;
        if (_pressedTargetId is not null && !_targets.Any(t => t.Id == _pressedTargetId)) ClearPress();
        SetHovered(_pointerInside && !_dragging ? _targets.LastOrDefault(t => t.Bounds.Contains(_pointerLocation)) : null);
    }

    private Rectangle CalculateFeedbackBounds(int margin)
    {
        int width = Math.Min(S(820), ClientSize.Width - margin * 2);
        bool compact = ClientSize.Height < S(450);
        int top = S(compact ? 64 : 92);
        int available = Math.Max(1, _rail.Top - S(compact ? 12 : 48) - top);
        int height = Math.Min(S(ClientSize.Height < S(650) ? 136 : 152), available);
        return new Rectangle((ClientSize.Width - width) / 2, top, width, height);
    }

    private void DrawCreationFeedback(Graphics g)
    {
        if (_creationDesktop is not Guid id || _display.Desktops.FirstOrDefault(d => d.Id == id) is not { } desktop) return;
        var bounds = CreationFeedbackBounds;
        double elapsed = Stopwatch.GetElapsedTime(_creationStarted).TotalMilliseconds;
        bounds.Offset(0, -(int)(S(14) * Math.Pow(1 - Math.Clamp(elapsed / 180, 0, 1), 3)));
        FillRounded(g, bounds, Color.FromArgb(27, 49, 63), S(12));
        using var line = new Pen(Accent, S(2));
        g.DrawLine(line, bounds.Left + S(18), bounds.Top + S(16), bounds.Left + S(18), bounds.Bottom - S(16));
        int textLeft = bounds.Left + S(38), textWidth = Math.Max(1, bounds.Width - S(60));
        bool compact = bounds.Height < S(128) || bounds.Width < S(680);
        if (!compact)
            DrawText(g, "✓  " + UiText.Get(_creationMovedWindow ? "WindowMovedNotice" : "DesktopCreatedNotice"), _titleFont, Accent,
                new Rectangle(textLeft, bounds.Top + S(10), textWidth, S(28)));
        DrawText(g, UiText.Get("NewDesktopCreatedTitle", UiText.DesktopName(desktop)), compact ? _headerFont : _messageFont, Color.White,
            new Rectangle(textLeft, bounds.Top + S(compact ? 8 : 40), textWidth, S(compact ? 38 : 46)));
        bool preview = desktop.Id != _display.ActiveDesktopId;
        int actionWidth = preview ? Math.Min(TextRenderer.MeasureText(UiText.Get("OpenDesktop"), _smallFont).Width + S(24), bounds.Width * 3 / 5) : 0;
        var hint = new Rectangle(textLeft, bounds.Bottom - S(45), Math.Max(1, textWidth - actionWidth - S(12)), S(34));
        DrawText(g, UiText.Get(preview ? compact ? "Previewing" : "NewDesktopPreviewHint" : "ActiveDesktop"), _smallFont, Color.FromArgb(214, 228, 236), hint);
        if (preview)
        {
            var button = new Rectangle(bounds.Right - actionWidth - S(20), bounds.Bottom - S(48), actionWidth, S(36));
            string key = $"feedback:open:{id}";
            DrawInteractiveFrame(g, button, key, Color.FromArgb(46, 79, 98), S(5));
            DrawText(g, UiText.Get("OpenDesktop"), _smallFont, Color.White, button, true);
            _targets.Add(new(key, button, () => ChooseDesktop(id), null, UiText.Get("OpenDesktop")));
        }
    }

    public void ShowCreationFeedback(Guid desktopId, bool windowMoved = true)
    {
        if (!_display.Desktops.Any(d => d.Id == desktopId)) return;
        PreviewDesktop(desktopId);
        _creationDesktop = desktopId;
        _creationMovedWindow = windowMoved;
        _creationStarted = Stopwatch.GetTimestamp();
        _status = "";
        _feedbackTimer.Start();
        InvalidateLayout();
    }

    public void ApplyLanguage()
    {
        if (IsDisposed || Disposing) return;
        _menu?.Close();
        _titleFont.Dispose(); _smallFont.Dispose(); _headerFont.Dispose(); _messageFont.Dispose();
        _titleFont = new Font(UiText.FontFamily, 12);
        _smallFont = new Font(UiText.FontFamily, 11);
        _headerFont = new Font(UiText.FontFamily, 16, FontStyle.Bold);
        _messageFont = new Font(UiText.FontFamily, 23, FontStyle.Bold);
        var previousFont = Font;
        Font = new Font(UiText.FontFamily, 12);
        previousFont.Dispose();
        Text = $"BetterVirtualDesktop · {UiText.DisplayName(_display)}";
        AccessibleName = UiText.Get("OverlayAccessible", UiText.DisplayName(_display));
        _status = "";
        _tip = "";
        ClearPress();
        InvalidateLayout();
    }

    private void OpenLanguageMenu()
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip { Font = Font };
        void AddLanguage(string code, string label)
        {
            var item = new ToolStripMenuItem(label) { Checked = UiText.SelectedLanguageCode == code };
            item.Click += (_, _) =>
            {
                try { UiText.SetLanguage(code, persist: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { AppLog.Write(error); ShowStatus(UiText.Get("Error.LanguageSave")); }
            };
            _menu.Items.Add(item);
        }
        AddLanguage("system", UiText.Get("SystemLanguage"));
        _menu.Items.Add(new ToolStripSeparator());
        foreach (var language in UiText.SupportedLanguages) AddLanguage(language.Code, language.NativeName);
        _menu.Show(Cursor.Position);
    }

    private void DrawGallery(Graphics g, DesktopSnapshot desktop)
    {
        _windowOffset = Math.Clamp(_windowOffset, 0, Math.Max(0, desktop.WindowIds.Count - WindowSlots));
        var handles = desktop.WindowIds.Skip(_windowOffset).Take(WindowSlots).ToArray();
        var live = new List<(long Handle, Rectangle Bounds)>();
        if (handles.Length == 0)
            DrawText(g, UiText.Get("NoWindows"), _titleFont, Muted, _gallery, true);
        else
        {
            int gap = S(18), titleHeight = S(32);
            // Pick the grid whose smallest window preview is largest; preserve each window's aspect ratio.
            int columns = 1;
            double best = -1;
            for (int candidate = 1; candidate <= Math.Min(4, handles.Length); candidate++)
            {
                int rows = (int)Math.Ceiling(handles.Length / (double)candidate);
                double cellWidth = Math.Max(1, (_gallery.Width - gap * (candidate - 1)) / (double)candidate);
                double cellHeight = Math.Max(1, (_gallery.Height - gap * (rows - 1)) / (double)rows - titleHeight);
                double score = handles.Min(h =>
                {
                    double ratio = Aspect(h);
                    double w = Math.Min(S(620), Math.Min(cellWidth, cellHeight * ratio));
                    return w * w / ratio;
                });
                if (score >= best - 0.1) { best = score; columns = candidate; }
            }
            int rowCount = (int)Math.Ceiling(handles.Length / (double)columns);
            int cw = Math.Max(1, (_gallery.Width - gap * (columns - 1)) / columns);
            int ch = Math.Max(1, (_gallery.Height - gap * (rowCount - 1)) / rowCount);
            var sizes = handles.Select(h =>
            {
                double ratio = Aspect(h);
                int w = Math.Max(1, (int)Math.Min(S(620), Math.Min(cw, Math.Max(1, ch - titleHeight) * ratio)));
                return new Size(w, Math.Max(1, (int)(w / ratio)) + titleHeight);
            }).ToArray();
            var heights = Enumerable.Range(0, rowCount).Select(row => sizes.Skip(row * columns).Take(columns).Max(s => s.Height)).ToArray();
            int y = _gallery.Top + Math.Max(0, (_gallery.Height - heights.Sum() - gap * (rowCount - 1)) / 2);
            for (int row = 0; row < rowCount; row++)
            {
                int start = row * columns, count = Math.Min(columns, handles.Length - start);
                int totalWidth = sizes.Skip(start).Take(count).Sum(s => s.Width) + (count - 1) * gap;
                int x = _gallery.Left + Math.Max(0, (_gallery.Width - totalWidth) / 2);
                for (int i = start; i < start + count; i++)
                {
                    var tile = new Rectangle(x, y, sizes[i].Width, sizes[i].Height);
                    DrawWindow(g, desktop, handles[i], tile, live);
                    x += sizes[i].Width + gap;
                }
                y += heights[row] + gap;
            }
        }
        if (IsHandleCreated && !IsDisposed)
        {
            _live ??= new DwmThumbnailSession(Handle);
            _live.Update(live);
        }
    }

    private double Aspect(long handle) => _windows.TryGetValue(handle, out var info)
        ? Math.Clamp(info.Bounds.Width / (double)Math.Max(1, info.Bounds.Height), 0.45, 2.8) : 1.6;

    private void DrawWindow(Graphics g, DesktopSnapshot desktop, long handle, Rectangle bounds, List<(long Handle, Rectangle Bounds)> live)
    {
        _windowTiles.Add(bounds);
        string key = $"window:{desktop.Id}:{handle}";
        bool pressed = PressedTargetId == key;
        DrawInteractiveFrame(g, bounds, key, Surface, S(6));
        string title = _windows.TryGetValue(handle, out var info) && !string.IsNullOrWhiteSpace(info.Title) ? info.Title : UiText.Get("Dialog");
        // Keep feedback outside the compositor thumbnail, which paints over GDI content.
        int inset = S(pressed ? 6 : 4);
        var content = Rectangle.FromLTRB(bounds.Left + inset, bounds.Top + S(32) + (pressed ? S(1) : 0), bounds.Right - inset, bounds.Bottom - inset);
        DrawText(g, title, _titleFont, pressed ? Accent : Color.White,
            new Rectangle(bounds.X + S(12), bounds.Y + (pressed ? S(1) : 0), Math.Max(1, bounds.Width - S(24)), S(32)));
        DrawPreview(g, handle, content, true);
        if (info is { IsVisible: true, IsMinimized: false, IsCloaked: false }) live.Add((handle, content));
        _targets.Add(new(key, bounds, () => SelectionRequested?.Invoke(DisplayId, desktop.Id, handle),
            () => WindowMenu(handle, desktop.Id), UiText.Get("WindowHint", title), WindowHandle: handle));
    }

    private void DrawPreview(Graphics g, long handle, Rectangle bounds, bool labelFallback)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;
        using var fill = new SolidBrush(Color.FromArgb(42, 43, 47));
        g.FillRectangle(fill, bounds);
        var bitmap = _previews?.TryGet(handle);
        if (bitmap is not null)
        {
            double scale = Math.Min(bounds.Width / (double)bitmap.Width, bounds.Height / (double)bitmap.Height);
            int width = Math.Max(1, (int)(bitmap.Width * scale)), height = Math.Max(1, (int)(bitmap.Height * scale));
            g.DrawImage(bitmap, new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
        }
        else if (labelFallback)
        {
            string text = UiText.Get(_windows.TryGetValue(handle, out var info) && info.IsMinimized ? "MinimizedWindow" : "PreviewUnavailable");
            DrawText(g, text, _smallFont, Muted, bounds, true);
        }
    }

    private void DrawDesktopRail(Graphics g)
    {
        FillRounded(g, _rail, Color.FromArgb(18, 18, 20), S(6));
        int gap = S(12), arrow = S(34), cardHeight = _rail.Height - S(16);
        int cardWidth = Math.Min(S(216), Math.Max(S(110), (int)((cardHeight - S(30)) * 1.78)));
        int available = Math.Max(1, _rail.Width - S(32) - arrow * 2);
        _desktopSlots = Math.Max(1, Math.Min(_display.Desktops.Count, (available - cardWidth - gap) / (cardWidth + gap)));
        KeepSelectionVisible();
        int visible = Math.Min(_desktopSlots, _display.Desktops.Count - _firstDesktop);
        int totalWidth = (visible + 1) * cardWidth + visible * gap;
        int left = _rail.Left + Math.Max(arrow + S(8), (_rail.Width - totalWidth) / 2);
        if (_display.Desktops.Count > _desktopSlots)
        {
            ActionButton(g, "previous", new Rectangle(_rail.Left + S(6), _rail.Top + cardHeight / 2, arrow, S(34)), "‹", () => MoveSelection(-1));
            ActionButton(g, "next", new Rectangle(_rail.Right - arrow - S(6), _rail.Top + cardHeight / 2, arrow, S(34)), "›", () => MoveSelection(1));
        }
        for (int i = 0; i < visible; i++)
            DrawDesktop(g, _display.Desktops[_firstDesktop + i], new Rectangle(left + i * (cardWidth + gap), _rail.Top + S(8), cardWidth, cardHeight));
        var add = new Rectangle(left + visible * (cardWidth + gap), _rail.Top + S(8), cardWidth, cardHeight);
        const string addKey = "desktop:new";
        bool pressed = PressedTargetId == addKey;
        DrawInteractiveFrame(g, add, addKey, Color.FromArgb(24, 24, 27), S(5), drop: _dropNewDesktop);
        DrawText(g, UiText.Get("NewDesktop"), _smallFont, _hoveredTargetId == addKey || pressed ? Color.White : Muted,
            new Rectangle(add.Left + S(8), add.Top + (pressed ? S(1) : 0), add.Width - S(16), S(26)));
        var plus = Rectangle.FromLTRB(add.Left + S(8), add.Top + S(30), add.Right - S(8), add.Bottom - S(8));
        if (pressed) plus.Inflate(-S(2), -S(2));
        FillRounded(g, plus, pressed ? Color.FromArgb(36, 64, 80) : Mix(Color.FromArgb(55, 55, 58), Color.FromArgb(69, 84, 96), HoverLevel(addKey)), S(4));
        DrawText(g, _dropNewDesktop ? UiText.Get("DropToMove") : "+", _titleFont, _dropNewDesktop || pressed ? Accent : Color.White, plus, true);
        _targets.Add(new(addKey, add, () => AddRequested?.Invoke(DisplayId), null,
            UiText.Get("NewDesktopHint"), CreatesDesktop: true));
    }

    private void DrawDesktop(Graphics g, DesktopSnapshot desktop, Rectangle bounds)
    {
        bool selected = desktop.Id == _selected, active = desktop.Id == _display.ActiveDesktopId;
        string key = $"desktop:{desktop.Id}";
        bool pressed = PressedTargetId == key;
        DrawInteractiveFrame(g, bounds, key, selected ? Color.FromArgb(38, 41, 47) : Color.FromArgb(24, 24, 27), S(5), selected, _dropDesktop == desktop.Id);
        DrawText(g, UiText.DesktopName(desktop), _smallFont, pressed ? Accent : Color.White,
            new Rectangle(bounds.Left + S(8), bounds.Top + (pressed ? S(1) : 0), bounds.Width - S(35), S(26)));
        var miniature = Rectangle.FromLTRB(bounds.Left + S(8), bounds.Top + S(30), bounds.Right - S(8), bounds.Bottom - S(10));
        if (pressed) miniature.Inflate(-S(2), -S(2));
        FillRounded(g, miniature, Color.FromArgb(57, 61, 68), S(3));
        foreach (var handle in desktop.WindowIds.Reverse().Where(_windows.ContainsKey).Take(10))
        {
            var window = _windows[handle];
            if (window.IsMinimized) continue;
            float scaleX = miniature.Width / (float)Math.Max(1, Bounds.Width), scaleY = miniature.Height / (float)Math.Max(1, Bounds.Height);
            var rect = Rectangle.Intersect(miniature, new Rectangle(miniature.Left + (int)((window.Bounds.Left - Bounds.Left) * scaleX),
                miniature.Top + (int)((window.Bounds.Top - Bounds.Top) * scaleY), Math.Max(1, (int)(window.Bounds.Width * scaleX)), Math.Max(1, (int)(window.Bounds.Height * scaleY))));
            DrawPreview(g, handle, rect, false);
        }
        if (desktop.WindowIds.Count == 0) DrawText(g, UiText.Get("EmptyDesktop"), _smallFont, Muted, miniature, true);
        if (active)
        {
            using var accent = new Pen(Accent, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(accent, bounds.Left + bounds.Width / 2 - S(12), bounds.Bottom - S(3), bounds.Left + bounds.Width / 2 + S(12), bounds.Bottom - S(3));
        }
        _targets.Add(new(key, bounds, () => ChooseDesktop(desktop.Id), () => DesktopMenu(desktop),
            UiText.Get("DesktopHint", UiText.DesktopName(desktop), desktop.WindowIds.Count), DesktopId: desktop.Id));
        var menu = new Rectangle(bounds.Right - S(28), bounds.Top, S(24), S(26));
        string menuKey = $"desktop-menu:{desktop.Id}";
        DrawInteractiveFrame(g, menu, menuKey, Color.Transparent, S(4), quiet: true);
        DrawText(g, "···", _smallFont, Muted, menu, true);
        _targets.Add(new(menuKey, menu, () => DesktopMenu(desktop), () => DesktopMenu(desktop), UiText.Get("DesktopMenu"), DesktopId: desktop.Id));
        if (HasCreationFeedback && desktop.Id == _creationDesktop)
        {
            double elapsed = Stopwatch.GetElapsedTime(_creationStarted).TotalSeconds;
            float pulse = (float)(0.5 + 0.5 * Math.Sin(elapsed * Math.PI * 3));
            using var ring = new Pen(Color.FromArgb(150 + (int)(105 * pulse), Accent), S(3));
            g.DrawRectangle(ring, bounds.Left + S(2), bounds.Top + S(2), bounds.Width - S(4), bounds.Height - S(4));
        }
    }

    private static void FillRounded(Graphics g, Rectangle bounds, Color color, int radius)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;
        float d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private void ActionButton(Graphics g, string id, Rectangle bounds, string text, Action action)
    {
        string key = $"action:{id}";
        DrawInteractiveFrame(g, bounds, key, Color.Transparent, S(5), quiet: true);
        DrawText(g, text, _smallFont, _hoveredTargetId == key ? Color.White : Muted, bounds, true);
        _targets.Add(new(key, bounds, action, null, text));
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds, bool centered = false) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | (centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));

    private float HoverLevel(string id) => _hoverLevels.GetValueOrDefault(id);

    private static Color Mix(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.A + (to.A - from.A) * amount), (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));

    private void DrawInteractiveFrame(Graphics g, Rectangle bounds, string key, Color normal, int radius,
        bool selected = false, bool drop = false, bool quiet = false)
    {
        float hover = HoverLevel(key);
        bool pressed = PressedTargetId == key;
        FillRounded(g, bounds, pressed ? Color.FromArgb(33, 62, 80) : Mix(normal, Color.FromArgb(57, 70, 84), hover), radius);
        if (quiet && hover <= 0 && !pressed) return;
        if (bounds.Width < 3 || bounds.Height < 3) return;
        var outline = new RectangleF(bounds.X + S(1), bounds.Y + S(1), bounds.Width - S(2), bounds.Height - S(2));
        float diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(outline.Width, outline.Height)));
        using var path = new GraphicsPath();
        path.AddArc(outline.Left, outline.Top, diameter, diameter, 180, 90);
        path.AddArc(outline.Right - diameter, outline.Top, diameter, diameter, 270, 90);
        path.AddArc(outline.Right - diameter, outline.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(outline.Left, outline.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        Color idle = selected ? Color.FromArgb(123, 139, 158) : Color.FromArgb(62, 64, 70);
        if (hover > 0 && !pressed)
        {
            using var glow = new Pen(Color.FromArgb((int)(45 * hover), Accent), S(4));
            g.DrawPath(glow, path);
        }
        using var edge = new Pen(pressed || drop ? Accent : Mix(idle, Color.FromArgb(164, 211, 238), hover),
            pressed || drop ? S(2) : Math.Max(1, DeviceDpi / 96f));
        g.DrawPath(edge, path);
    }

    private void InvalidateLayout() { _layoutDirty = true; Invalidate(); }

    private HitTarget? HitTest(Point location)
    {
        if (_layoutDirty && IsHandleCreated && !IsDisposed) Refresh();
        return _targets.LastOrDefault(t => t.Bounds.Contains(location));
    }

    private void SetHovered(HitTarget? hit)
    {
        string? id = hit?.Id;
        if (_hoveredTargetId != id)
        {
            _hoveredTargetId = id;
            if (id is not null) _hoverLevels.TryAdd(id, 0);
            if (!_closing && !Disposing && !IsDisposed) _feedbackTimer.Start();
            Invalidate();
        }
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        if (_tip != (hit?.Hint ?? "")) { _tip = hit?.Hint ?? ""; _tips.SetToolTip(this, _tip); }
    }

    private void AnimateFeedback()
    {
        bool animating = false;
        if (_creationDesktop.HasValue)
        {
            if (!HasCreationFeedback) { _creationDesktop = null; InvalidateLayout(); }
            else animating = true;
        }
        foreach (var key in _hoverLevels.Keys.ToArray())
        {
            float goal = key == _hoveredTargetId ? 1 : 0;
            float current = _hoverLevels[key];
            float next = current < goal ? Math.Min(goal, current + 0.16f) : Math.Max(goal, current - 0.16f);
            if (next == 0 && goal == 0) _hoverLevels.Remove(key);
            else _hoverLevels[key] = next;
            animating |= next != goal;
        }
        if (!animating) _feedbackTimer.Stop();
        Invalidate();
    }

    private void ClearPress()
    {
        _pressedTargetId = null;
        _pressedBackground = false;
        _pressedButton = MouseButtons.None;
        _pressedWindow = null;
        _releasingCapture = true;
        try { if (Capture) Capture = false; }
        finally { _releasingCapture = false; }
        if (!IsDisposed) Invalidate();
    }

    public void MoveSelection(int delta)
    {
        int index = _display.Desktops.ToList().FindIndex(d => d.Id == _selected);
        int next = (int)(((long)index + delta) % _display.Desktops.Count + _display.Desktops.Count) % _display.Desktops.Count;
        PreviewDesktop(_display.Desktops[next].Id);
    }

    public void PreviewDesktop(Guid id)
    {
        if (_selected == id || !_display.Desktops.Any(d => d.Id == id)) return;
        if (_creationDesktop.HasValue && _creationDesktop != id) _creationDesktop = null;
        _selected = id;
        _windowOffset = 0;
        KeepSelectionVisible();
        _live?.Clear();
        InvalidateLayout();
    }

    private void KeepSelectionVisible()
    {
        int index = _display.Desktops.ToList().FindIndex(d => d.Id == _selected);
        if (index < _firstDesktop) _firstDesktop = index;
        if (index >= _firstDesktop + _desktopSlots) _firstDesktop = index - _desktopSlots + 1;
        _firstDesktop = Math.Clamp(_firstDesktop, 0, Math.Max(0, _display.Desktops.Count - _desktopSlots));
    }

    public void ChooseDesktop(Guid desktopId)
    {
        if (_display.Desktops.Any(d => d.Id == desktopId)) SelectionRequested?.Invoke(DisplayId, desktopId, null);
    }

    public void RequestMove(long handle, string displayId, Guid desktopId) => MoveToDisplayRequested?.Invoke(handle, displayId, desktopId);
    public void RequestMoveToNewDesktop(long handle, string displayId) => MoveToNewDesktopRequested?.Invoke(handle, displayId);
    public void ShowStatus(string message) { _status = message; Invalidate(); }
    public void CancelOverlay() => DismissRequested?.Invoke();
    public void RequestKeyboardFocus() => KeyboardFocusRequested?.Invoke();
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); RequestKeyboardFocus(); }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: CancelOverlay(); return true;
            case Keys.Left: MoveSelection(-1); return true;
            case Keys.Right: MoveSelection(1); return true;
            case Keys.Enter: ChooseDesktop(_selected); return true;
            case Keys.Home: MoveSelection(-_display.Desktops.ToList().FindIndex(d => d.Id == _selected)); return true;
            case Keys.End: MoveSelection(_display.Desktops.Count - 1 - _display.Desktops.ToList().FindIndex(d => d.Id == _selected)); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        _suppressMouseUp = false;
        _mouseDown = e.Location;
        _pointerInside = ClientRectangle.Contains(e.Location);
        _pointerLocation = e.Location;
        var hit = HitTest(e.Location);
        SetHovered(hit);
        _pressedTargetId = hit?.Id;
        _pressedButton = e.Button;
        _pressedBackground = hit is null && _pointerInside && !_rail.Contains(e.Location);
        _pressedWindow = e.Button == MouseButtons.Left ? hit?.WindowHandle : null;
        if (Visible) Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointerInside = ClientRectangle.Contains(e.Location);
        _pointerLocation = e.Location;
        if (_pressedWindow is long handle && e.Button == MouseButtons.Left && !_dragging &&
            (Math.Abs(e.X - _mouseDown.X) > SystemInformation.DragSize.Width / 2 || Math.Abs(e.Y - _mouseDown.Y) > SystemInformation.DragSize.Height / 2))
        {
            _suppressMouseUp = true;
            _dragging = true;
            ClearPress();
            SetHovered(null);
            try { DoDragDrop(CreateDragData(handle), DragDropEffects.Move); }
            finally
            {
                _dragging = false;
                _dropDesktop = null;
                _dropNewDesktop = false;
                if (!IsDisposed)
                {
                    _pointerLocation = PointToClient(Cursor.Position);
                    _pointerInside = ClientRectangle.Contains(_pointerLocation);
                    SetHovered(_pointerInside ? HitTest(_pointerLocation) : null);
                    Invalidate();
                    DragEnded?.Invoke();
                }
            }
            return;
        }
        var hit = _pointerInside ? HitTest(e.Location) : null;
        SetHovered(hit);
        if (hit?.DesktopId is Guid id && _pressedButton == MouseButtons.None && e.Button == MouseButtons.None && !_dragging) PreviewDesktop(id);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pointerInside = ClientRectangle.Contains(e.Location);
        _pointerLocation = e.Location;
        var hit = _pointerInside ? HitTest(e.Location) : null;
        string? pressed = _pressedTargetId;
        bool background = _pressedBackground;
        bool matchingButton = e.Button == _pressedButton;
        ClearPress();
        SetHovered(hit);
        if (_suppressMouseUp) { _suppressMouseUp = false; return; }
        if (!matchingButton || _dragging) return;
        if (pressed is not null && hit?.Id == pressed)
        {
            if (e.Button == MouseButtons.Right) hit.Context?.Invoke();
            else if (e.Button == MouseButtons.Left) hit.Invoke();
        }
        else if (e.Button == MouseButtons.Left && background && hit is null && _pointerInside && !_rail.Contains(e.Location)) DismissRequested?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerInside = false;
        SetHovered(null);
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && !_dragging && !_releasingCapture) ClearPress();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        if (!_dragging)
        {
            ClearPress();
            _pointerInside = false;
            SetHovered(null);
        }
        base.OnDeactivate(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) return;
        ClearPress();
        if (_rail.Contains(e.Location)) MoveSelection(e.Delta < 0 ? 1 : -1);
        else { _windowOffset += e.Delta < 0 ? WindowSlots : -WindowSlots; _live?.Clear(); InvalidateLayout(); }
    }

    internal IDataObject CreateDragData(long handle)
    {
        // Unicode text crosses the OLE boundary without serializing managed objects.
        // A per-opening nonce restricts drops to this app's current overlay session.
        return new DataObject(DataFormats.UnicodeText, $"BetterVirtualDesktop.Window/{_dragSession:D}/{handle}");
    }
    private WindowDrag? ReadDrag(IDataObject? data)
    {
        string prefix = $"BetterVirtualDesktop.Window/{_dragSession:D}/";
        return data?.GetData(DataFormats.UnicodeText, false) is string text && text.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(prefix.Length), out long handle) && handle != 0 ? new WindowDrag(handle) : null;
    }
    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); UpdateDrag(e); }
    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); UpdateDrag(e); }
    private void UpdateDrag(DragEventArgs e)
    {
        var point = PointToClient(new Point(e.X, e.Y));
        var hit = HitTest(point);
        var target = hit?.DesktopId;
        bool create = hit?.CreatesDesktop == true;
        if (target is null && !create && _gallery.Contains(point)) target = _selected;
        bool valid = ReadDrag(e.Data) is not null && (target.HasValue || create);
        e.Effect = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (_dropDesktop != (valid ? target : null) || _dropNewDesktop != (valid && create))
        {
            _dropDesktop = valid ? target : null;
            _dropNewDesktop = valid && create;
            Invalidate();
        }
    }
    protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); _dropDesktop = null; _dropNewDesktop = false; Invalidate(); }
    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        UpdateDrag(e);
        var payload = ReadDrag(e.Data);
        if (payload is not null)
        {
            if (_dropNewDesktop) RequestMoveToNewDesktop(payload.Handle, DisplayId);
            else if (_dropDesktop is Guid target) RequestMove(payload.Handle, DisplayId, target);
        }
        _dropDesktop = null;
        _dropNewDesktop = false;
        Invalidate();
    }

    private void DesktopMenu(DesktopSnapshot desktop)
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip();
        _menu.Items.Add(UiText.Get("Rename"), null, (_, _) => Rename(desktop));
        var remove = _menu.Items.Add(UiText.Get("RemoveDesktop"), null, (_, _) => RemoveRequested?.Invoke(DisplayId, desktop.Id));
        remove.Enabled = _display.Desktops.Count > 1;
        _menu.Show(Cursor.Position);
    }

    private void WindowMenu(long handle, Guid current)
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip();
        foreach (var display in _displays)
        {
            var group = new ToolStripMenuItem(UiText.DisplayName(display) + (display.Id == DisplayId ? UiText.Get("ThisDisplaySuffix") : ""));
            foreach (var desktop in display.Desktops)
            {
                var item = group.DropDownItems.Add(UiText.DesktopName(desktop) + (desktop.Id == display.ActiveDesktopId ? UiText.Get("CurrentSuffix") : ""), null,
                    (_, _) => RequestMove(handle, display.Id, desktop.Id));
                item.Enabled = desktop.Id != current;
            }
            group.DropDownItems.Add(new ToolStripSeparator());
            group.DropDownItems.Add(UiText.Get("MoveNewDesktop"), null, (_, _) => RequestMoveToNewDesktop(handle, display.Id));
            _menu.Items.Add(group);
        }
        _menu.Show(Cursor.Position);
    }

    private void Rename(DesktopSnapshot desktop)
    {
        Font dialogFont = new(UiText.FontFamily, Font.Size);
        using var dialog = new Form { Text = UiText.Get("DesktopNameTitle"), ClientSize = new Size(S(330), S(115)),
            Font = dialogFont, BackColor = Surface, ForeColor = Color.White, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, TopMost = true };
        string originalName = UiText.DesktopName(desktop);
        var input = new TextBox { Text = originalName, Left = S(15), Top = S(18), Width = S(300), MaxLength = 60 };
        var save = new Button { Text = UiText.Get("Save"), Left = S(220), Top = S(65), Size = new Size(S(95), S(32)), DialogResult = DialogResult.OK };
        dialog.Controls.AddRange([input, save]);
        dialog.AcceptButton = save;
        void UpdateLanguage()
        {
            dialog.Text = UiText.Get("DesktopNameTitle");
            save.Text = UiText.Get("Save");
            var previous = dialogFont;
            dialogFont = new Font(UiText.FontFamily, Font.Size);
            dialog.Font = dialogFont;
            previous.Dispose();
            if (input.Text == originalName) input.Text = UiText.DesktopName(desktop);
            originalName = UiText.DesktopName(desktop);
        }
        UiText.Changed += UpdateLanguage;
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) && input.Text.Trim() != originalName)
                RenameRequested?.Invoke(DisplayId, desktop.Id, input.Text.Trim());
        }
        finally { UiText.Changed -= UpdateLanguage; dialogFont.Dispose(); }
    }

    public void Shutdown() { _live?.Clear(); _closing = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _feedbackTimer.Dispose(); _live?.Dispose(); _menu?.Dispose(); _tips.Dispose(); _titleFont.Dispose(); _smallFont.Dispose(); _headerFont.Dispose(); _messageFont.Dispose(); }
        base.Dispose(disposing);
    }
}
