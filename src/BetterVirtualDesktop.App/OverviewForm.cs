using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.Core;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Shortcuts;

namespace BetterVirtualDesktop.App;

internal sealed class OverviewForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(17, 21, 30);
    private static readonly Color Surface = Color.FromArgb(28, 34, 47);
    private static readonly Color Tile = Color.FromArgb(38, 46, 62);
    private static readonly Color Accent = Color.FromArgb(139, 183, 255);
    private static readonly Color Muted = Color.FromArgb(161, 174, 196);
    private readonly DesktopController _controller;
    private readonly Func<ShortcutProfile> _shortcuts;
    public event Action? ShortcutSettingsRequested;
    private Panel _body = null!;
    private Label _status = null!;
    private Font? _uiFont;
    private readonly ToolTip _tips = new() { AutoPopDelay = 10000, InitialDelay = 400, ReshowDelay = 100 };
    private string _signature = "";
    private bool _exiting;

    public OverviewForm(DesktopController controller, Func<ShortcutProfile>? shortcuts = null)
    {
        _controller = controller;
        _shortcuts = shortcuts ?? (() => ShortcutProfile.Defaults);
        Icon = AppIcon.Window;
        BackColor = Canvas;
        ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(740, 540);
        Size = new Size(1180, 800);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        FormClosing += (_, e) => { if (!_exiting) { e.Cancel = true; Hide(); } };

        BuildChrome();
    }

    private void BuildChrome()
    {
        Text = UiText.Get("Overview.Title");
        var nextFont = new Font(UiText.FontFamily, 10);
        if (_uiFont is not null && _uiFont.Equals(nextFont)) nextFont.Dispose();
        else
        {
            var previousFont = _uiFont;
            _uiFont = nextFont;
            Font = _uiFont;
            previousFont?.Dispose();
        }

        var header = new Panel { Dock = DockStyle.Top, Height = 135, Padding = new Padding(28, 22, 28, 12) };
        header.Controls.Add(new Label { Text = UiText.Get("Overview.Hint",
            ShortcutText.Format(_shortcuts()[DesktopCommand.PreviousDesktop]),
            ShortcutText.Format(_shortcuts()[DesktopCommand.NextDesktop])), Dock = DockStyle.Bottom,
            Height = 30, ForeColor = Muted, AutoEllipsis = true });
        header.Controls.Add(new Label { Text = "BetterVirtualDesktop", Font = new Font(UiText.FontFamily, 25, FontStyle.Bold),
            AutoSize = false, Dock = DockStyle.Top, Height = 52 });

        var footer = new Panel { Name = "OverviewFooter", Dock = DockStyle.Bottom, Height = 100,
            Padding = new Padding(28, 10, 28, 16) };
        _status = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = Accent, AutoEllipsis = true };
        var footerActions = new FlowLayoutPanel { Name = "FooterActions", Dock = DockStyle.Bottom,
            Height = 38, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        footerActions.Controls.Add(Button(UiText.Get("Overview.PauseResume"), (_, _) => { _controller.TogglePause(); UpdateContent(); }, 185));
        footerActions.Controls.Add(Button(UiText.Get("Overview.Restore"), (_, _) => { _controller.RestoreAllAndPause(); UpdateContent(); }, 132));
        footerActions.Controls.Add(Button(UiText.Get("Shortcuts.Open"), (_, _) => ShortcutSettingsRequested?.Invoke(), 150));
        footerActions.Controls.Add(Button(UiText.Get("Overview.Back"), (_, _) => Hide(), 185));
        foreach (Control action in footerActions.Controls) action.Margin = new Padding(0, 0, 8, 8);
        footer.Controls.Add(_status);
        footer.Controls.Add(footerActions);

        bool arrangingFooter = false;
        void LayoutFooter()
        {
            if (arrangingFooter || footerActions.ClientSize.Width <= 0) return;
            arrangingFooter = true;
            try
            {
                // Constrain the preferred size to the actual available width so localized
                // buttons can wrap without covering the status or being clipped below it.
                int height = footerActions.GetPreferredSize(new Size(footerActions.ClientSize.Width, 0)).Height;
                if (footerActions.Height != height) footerActions.Height = height;
                int footerHeight = footer.Padding.Vertical + _status.Height + footerActions.Margin.Top + height;
                if (footer.Height != footerHeight) footer.Height = footerHeight;
            }
            finally { arrangingFooter = false; }
        }
        footerActions.SizeChanged += (_, _) => LayoutFooter();
        footerActions.Layout += (_, _) => LayoutFooter();

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 0, 28, 0) };
        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);
        _body.Resize += (_, _) => LayoutRows();
        LayoutFooter();
    }

    public void ApplyLanguage()
    {
        if (IsDisposed || Disposing) return;
        var scroll = _body.AutoScrollPosition;
        SuspendLayout();
        try
        {
            foreach (Control control in Controls.Cast<Control>().ToArray()) control.Dispose();
            _signature = "";
            BuildChrome();
            UpdateContent(true);
            _body.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        }
        finally { ResumeLayout(true); }
    }

    public void OpenOnPointerDisplay()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1180, area.Width - 40), Math.Min(840, area.Height - 40));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        UpdateContent(true);
        Show();
        Activate();
    }

    public void UpdateContent(bool force = false)
    {
        if (IsDisposed || Disposing) return;
        // Menus and ShowDialog run nested message loops: a timer tick must not dispose
        // the clicked item or the owner of an active rename dialog beneath that loop.
        if (!force && (OwnedForms.Any(form => form.Visible) || HasOpenMenu(_body))) return;
        var displays = _controller.Catalog.Snapshot();
        string signature = string.Join("|", displays.Select(d => $"{d.Id}:{d.ActiveDesktopId}:" +
            string.Join(";", d.Desktops.Select(s => $"{s.Id}/{s.Name}/{s.DefaultNumber}/{string.Join(',', s.WindowIds.Select(h => $"{h}-{WindowTitle(h)}"))}"))))
            + _controller.Paused + _controller.UnmanageableCount + UiText.CurrentLanguageCode;
        if (!force && signature == _signature) return;
        var scroll = _body.AutoScrollPosition;
        _body.SuspendLayout();
        try
        {
            foreach (Control control in _body.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (var display in displays) _body.Controls.Add(CreateDisplayRow(display));
            LayoutRows();
            _signature = signature;
        }
        finally { _body.ResumeLayout(); }
        _body.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        int count = displays.Sum(d => d.Desktops.Sum(s => s.WindowIds.Count));
        _status.Text = _controller.Paused ? UiText.Get("Overview.Paused") : UiText.Get("Overview.Status", displays.Count, count);
        if (_controller.UnmanageableCount > 0) _status.Text += UiText.Get("Overview.Unmanageable", _controller.UnmanageableCount);
    }

    private static bool HasOpenMenu(Control control) =>
        control.ContextMenuStrip is { Visible: true } || control.Controls.Cast<Control>().Any(HasOpenMenu);

    private Control CreateDisplayRow(DisplaySnapshot display)
    {
        var row = new Panel { Height = 338, BackColor = Surface, Margin = new Padding(0, 0, 0, 18) };
        var heading = new Panel { Dock = DockStyle.Top, Height = 55, Padding = new Padding(16, 10, 12, 4) };
        heading.Controls.Add(new Label { Text = UiText.DisplayName(display), Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
        var add = Button(UiText.Get("Overview.Add"), (_, _) => _controller.AddDesktop(display.Id), 205);
        add.Dock = DockStyle.Right;
        heading.Controls.Add(add);
        var desktops = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false,
            Padding = new Padding(12, 8, 12, 10), FlowDirection = FlowDirection.LeftToRight };
        foreach (var desktop in display.Desktops) desktops.Controls.Add(CreateDesktopCard(display, desktop));
        row.Controls.Add(desktops);
        row.Controls.Add(heading);
        return row;
    }

    private Control CreateDesktopCard(DisplaySnapshot display, DesktopSnapshot desktop)
    {
        bool active = display.ActiveDesktopId == desktop.Id;
        string desktopName = UiText.DesktopName(desktop);
        var card = new Panel { Width = 290, Height = 243, Margin = new Padding(4, 0, 10, 0),
            BackColor = Tile, Padding = new Padding(12) };
        card.Paint += (_, e) => { using var pen = new Pen(active ? Accent : Color.FromArgb(60, 70, 90), active ? 2 : 1);
            e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3); };
        var title = Button((active ? "●  " : "○  ") + desktopName, (_, _) => _controller.Switch(display.Id, desktop.Id), 263);
        title.AutoSize = false;
        title.MinimumSize = Size.Empty;
        title.AutoEllipsis = true;
        _tips.SetToolTip(title, UiText.Get("Overview.DesktopHint", desktopName));
        title.Dock = DockStyle.Top;
        title.Height = 39;
        title.ForeColor = active ? Accent : Color.White;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.AccessibleName = UiText.Get("Overview.DesktopAccessible", UiText.DisplayName(display), desktopName,
            UiText.Get(active ? "Overview.Active" : "Overview.Switch"));
        var menu = new ContextMenuStrip { Font = Font };
        menu.Items.Add(UiText.Get("Rename"), null, (_, _) => Rename(display.Id, desktop));
        menu.Items.Add(new ToolStripSeparator());
        var moveLeft = menu.Items.Add(UiText.Get("Reorder.MoveLeft"), null, (_, _) => ReorderDesktop(display.Id, desktop.Id, -1));
        moveLeft.Name = "Reorder.MoveLeft";
        var moveRight = menu.Items.Add(UiText.Get("Reorder.MoveRight"), null, (_, _) => ReorderDesktop(display.Id, desktop.Id, 1));
        moveRight.Name = "Reorder.MoveRight";
        void UpdateReorderActions()
        {
            var current = _controller.Catalog.Snapshot().FirstOrDefault(d => d.Id == display.Id);
            int index = current?.Desktops.ToList().FindIndex(d => d.Id == desktop.Id) ?? -1;
            moveLeft.Enabled = index > 0;
            moveRight.Enabled = index >= 0 && index < current!.Desktops.Count - 1;
        }
        UpdateReorderActions();
        menu.Opening += (_, _) => UpdateReorderActions();
        menu.Items.Add(new ToolStripSeparator());
        var remove = menu.Items.Add(UiText.Get("RemoveDesktop"), null,
            (_, _) => _controller.RemoveDesktop(display.Id, desktop.Id));
        remove.Enabled = display.Desktops.Count > 1;
        title.ContextMenuStrip = menu;
        title.Disposed += (_, _) => menu.Dispose();
        var hint = new Label { Text = UiText.Get("Overview.WindowCount", desktop.WindowIds.Count) + (active ? UiText.Get("Overview.VisibleSuffix") : ""), Dock = DockStyle.Bottom,
            Height = 25, ForeColor = Muted, Font = new Font(Font.FontFamily, 9), TextAlign = ContentAlignment.BottomLeft };
        var windows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        if (desktop.WindowIds.Count == 0)
            windows.Controls.Add(new Label { Text = UiText.Get("Overview.Empty"),
                Size = new Size(240, 76), ForeColor = Muted, Padding = new Padding(6, 12, 0, 0) });
        foreach (long handle in desktop.WindowIds)
        {
            var windowButton = Button(WindowTitle(handle), (_, _) => { Hide(); _controller.ActivateWindow(handle); }, 238);
            windowButton.AutoSize = false;
            windowButton.MinimumSize = Size.Empty;
            _tips.SetToolTip(windowButton, WindowTitle(handle));
            windowButton.Height = 45;
            windowButton.TextAlign = ContentAlignment.MiddleLeft;
            windowButton.AutoEllipsis = true;
            windowButton.BackColor = Surface;
            windowButton.Margin = new Padding(0, 0, 0, 5);
            var windowMenu = new ContextMenuStrip { Font = Font };
            foreach (var targetDisplay in _controller.Catalog.Snapshot())
            {
                var group = new ToolStripMenuItem(UiText.DisplayName(targetDisplay));
                foreach (var target in targetDisplay.Desktops)
                {
                    var item = group.DropDownItems.Add(UiText.DesktopName(target) + (target.Id == targetDisplay.ActiveDesktopId ? UiText.Get("CurrentSuffix") : ""), null,
                        (_, _) =>
                        {
                            if (!_controller.MoveWindowToDisplay(handle, targetDisplay.Id, target.Id))
                                _status.Text = UiText.Get("MoveFailed");
                        });
                    item.Enabled = target.Id != desktop.Id;
                }
                group.DropDownItems.Add(new ToolStripSeparator());
                group.DropDownItems.Add(UiText.Get("MoveNewDesktop"), null, (_, _) =>
                {
                    if (_controller.MoveWindowToNewDesktop(handle, targetDisplay.Id) is null)
                        _status.Text = UiText.Get("NewDesktopMoveFailed");
                });
                windowMenu.Items.Add(group);
            }
            windowButton.ContextMenuStrip = windowMenu;
            windowButton.Disposed += (_, _) => windowMenu.Dispose();
            windows.Controls.Add(windowButton);
        }
        card.Controls.Add(windows);
        card.Controls.Add(hint);
        card.Controls.Add(title);
        return card;
    }

    private string WindowTitle(long handle) => _controller.Windows.TryGetValue(handle, out var info)
        ? (string.IsNullOrWhiteSpace(info.Title) ? UiText.Get("Dialog") : info.Title) : UiText.Get("Overview.ClosedWindow");

    private void LayoutRows()
    {
        int top = _body.AutoScrollPosition.Y;
        foreach (Control row in _body.Controls)
        {
            row.SetBounds(28, top, Math.Max(600, _body.ClientSize.Width - 74), row.Height);
            top += row.Height + 18;
        }
    }

    private void ReorderDesktop(string displayId, Guid desktopId, int offset)
    {
        var display = _controller.Catalog.Snapshot().FirstOrDefault(d => d.Id == displayId);
        int index = display?.Desktops.ToList().FindIndex(d => d.Id == desktopId) ?? -1;
        if (index >= 0) _controller.ReorderDesktop(displayId, desktopId, index + offset);
    }

    private void Rename(string displayId, DesktopSnapshot desktop)
    {
        Font dialogFont = new(UiText.FontFamily, Font.Size);
        using var dialog = new Form { Text = UiText.Get("Rename"), Size = new Size(380, 170), BackColor = Surface,
            ForeColor = Color.White, Font = dialogFont, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        string originalName = UiText.DesktopName(desktop);
        var input = new TextBox { Text = originalName, Left = 18, Top = 20, Width = 325, MaxLength = 60 };
        var save = Button(UiText.Get("Save"), (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = DialogResult.OK; }, 100);
        save.Location = new Point(242, 65);
        dialog.Controls.AddRange([input, save]);
        dialog.AcceptButton = save;
        void UpdateLanguage()
        {
            dialog.Text = UiText.Get("Rename");
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
            if (dialog.ShowDialog(this) == DialogResult.OK && input.Text.Trim() != originalName)
                _controller.RenameDesktop(displayId, desktop.Id, input.Text.Trim());
        }
        finally { UiText.Changed -= UpdateLanguage; dialogFont.Dispose(); }
    }

    private static Button Button(string text, EventHandler click, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = Tile, ForeColor = Color.White, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false, TabStop = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 34) };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 78, 101);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 66, 90);
        button.Click += click;
        return button;
    }

    public void Shutdown() { _exiting = true; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); _uiFont?.Dispose(); }
        base.Dispose(disposing);
    }
}
