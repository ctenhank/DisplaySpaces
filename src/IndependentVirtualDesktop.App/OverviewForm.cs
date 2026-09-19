using IndependentVirtualDesktop.App.Services;
using IndependentVirtualDesktop.Core;

namespace IndependentVirtualDesktop.App;

internal sealed class OverviewForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(17, 21, 30);
    private static readonly Color Surface = Color.FromArgb(28, 34, 47);
    private static readonly Color Tile = Color.FromArgb(38, 46, 62);
    private static readonly Color Accent = Color.FromArgb(139, 183, 255);
    private static readonly Color Muted = Color.FromArgb(161, 174, 196);
    private readonly DesktopController _controller;
    private readonly Panel _body;
    private readonly Label _status;
    private readonly ToolTip _tips = new() { AutoPopDelay = 10000, InitialDelay = 400, ReshowDelay = 100 };
    private string _signature = "";
    private bool _exiting;

    public OverviewForm(DesktopController controller)
    {
        _controller = controller;
        Text = "Independent Spaces · 모니터별 작업 보기";
        Icon = SystemIcons.Application;
        BackColor = Canvas;
        ForeColor = Color.White;
        Font = new Font("맑은 고딕", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(740, 540);
        Size = new Size(1180, 800);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        FormClosing += (_, e) => { if (!_exiting) { e.Cancel = true; Hide(); } };

        var header = new Panel { Dock = DockStyle.Top, Height = 135, Padding = new Padding(28, 22, 28, 12) };
        header.Controls.Add(new Label { Text = "마우스가 있는 모니터만 전환합니다.  ·  Win + Ctrl + ← / →", Dock = DockStyle.Bottom,
            Height = 30, ForeColor = Muted, AutoEllipsis = true });
        header.Controls.Add(new Label { Text = "Independent Spaces", Font = new Font("Segoe UI", 25, FontStyle.Bold),
            AutoSize = false, Dock = DockStyle.Top, Height = 52 });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(28, 10, 28, 16) };
        _status = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = Accent, AutoEllipsis = true };
        var footerActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
        footerActions.Controls.Add(Button("일시 정지 / 재개", (_, _) => { _controller.TogglePause(); UpdateContent(); }, 185));
        footerActions.Controls.Add(Button("모든 창 복구", (_, _) => { _controller.RestoreAllAndPause(); UpdateContent(); }, 132));
        footerActions.Controls.Add(Button("돌아가기 · Esc", (_, _) => Hide(), 185));
        footer.Controls.Add(_status);
        footer.Controls.Add(footerActions);

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 0, 28, 0) };
        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);
        _body.Resize += (_, _) => LayoutRows();
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
            string.Join(";", d.Desktops.Select(s => $"{s.Id}/{s.Name}/{string.Join(',', s.WindowIds.Select(h => $"{h}-{WindowTitle(h)}"))}"))))
            + _controller.Paused + _controller.UnmanageableCount;
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
        _status.Text = _controller.Paused ? "일시 정지됨 · 모든 창 표시 · 재개하면 데스크톱 구성이 적용됩니다."
            : $"{displays.Count}개 디스플레이  ·  {count}개 창  ·  클릭: 열기  /  우클릭: 이동";
        if (_controller.UnmanageableCount > 0) _status.Text += $"  ·  제어 불가 {_controller.UnmanageableCount}개";
    }

    private static bool HasOpenMenu(Control control) =>
        control.ContextMenuStrip is { Visible: true } || control.Controls.Cast<Control>().Any(HasOpenMenu);

    private Control CreateDisplayRow(DisplaySnapshot display)
    {
        var row = new Panel { Height = 338, BackColor = Surface, Margin = new Padding(0, 0, 0, 18) };
        var heading = new Panel { Dock = DockStyle.Top, Height = 55, Padding = new Padding(16, 10, 12, 4) };
        heading.Controls.Add(new Label { Text = display.Name, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
        var add = Button("+ 데스크톱 추가", (_, _) => _controller.AddDesktop(display.Id), 205);
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
        var card = new Panel { Width = 290, Height = 243, Margin = new Padding(4, 0, 10, 0),
            BackColor = Tile, Padding = new Padding(12) };
        card.Paint += (_, e) => { using var pen = new Pen(active ? Accent : Color.FromArgb(60, 70, 90), active ? 2 : 1);
            e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3); };
        var title = Button((active ? "●  " : "○  ") + desktop.Name, (_, _) => _controller.Switch(display.Id, desktop.Id), 263);
        title.AutoSize = false;
        title.MinimumSize = Size.Empty;
        title.AutoEllipsis = true;
        _tips.SetToolTip(title, desktop.Name + " · 우클릭: 이름 변경 / 삭제");
        title.Dock = DockStyle.Top;
        title.Height = 39;
        title.ForeColor = active ? Accent : Color.White;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.AccessibleName = $"{display.Name}, {desktop.Name}" + (active ? ", 현재 데스크톱" : ", 전환");
        var menu = new ContextMenuStrip();
        menu.Items.Add("이름 변경", null, (_, _) => Rename(display.Id, desktop));
        var remove = menu.Items.Add("데스크톱 삭제 · 창은 인접 데스크톱으로 이동", null,
            (_, _) => _controller.RemoveDesktop(display.Id, desktop.Id));
        remove.Enabled = display.Desktops.Count > 1;
        title.ContextMenuStrip = menu;
        title.Disposed += (_, _) => menu.Dispose();
        var hint = new Label { Text = $"{desktop.WindowIds.Count}개 창" + (active ? "  ·  현재 표시 중" : ""), Dock = DockStyle.Bottom,
            Height = 25, ForeColor = Muted, Font = new Font(Font.FontFamily, 9), TextAlign = ContentAlignment.BottomLeft };
        var windows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        if (desktop.WindowIds.Count == 0)
            windows.Controls.Add(new Label { Text = "비어 있는 데스크톱\n여기에서 새 창을 열어 보세요.",
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
            var windowMenu = new ContextMenuStrip();
            foreach (var target in display.Desktops)
            {
                var item = windowMenu.Items.Add($"{target.Name}(으)로 이동", null, (_, _) => _controller.MoveWindow(handle, target.Id));
                item.Enabled = target.Id != desktop.Id;
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
        ? (string.IsNullOrWhiteSpace(info.Title) ? "대화상자" : info.Title) : "종료된 창";

    private void LayoutRows()
    {
        int top = _body.AutoScrollPosition.Y;
        foreach (Control row in _body.Controls)
        {
            row.SetBounds(28, top, Math.Max(600, _body.ClientSize.Width - 74), row.Height);
            top += row.Height + 18;
        }
    }

    private void Rename(string displayId, DesktopSnapshot desktop)
    {
        using var dialog = new Form { Text = "데스크톱 이름 변경", Size = new Size(380, 170), BackColor = Surface,
            ForeColor = Color.White, Font = Font, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var input = new TextBox { Text = desktop.Name, Left = 18, Top = 20, Width = 325, MaxLength = 60 };
        var save = Button("저장", (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = DialogResult.OK; }, 100);
        save.Location = new Point(242, 65);
        dialog.Controls.AddRange([input, save]);
        dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) == DialogResult.OK) _controller.RenameDesktop(displayId, desktop.Id, input.Text.Trim());
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
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
