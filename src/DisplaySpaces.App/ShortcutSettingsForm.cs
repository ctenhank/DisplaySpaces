using System.ComponentModel;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App;

/// <summary>Edits a private draft; only a successful Save calls the supplied apply callback.</summary>
internal sealed class ShortcutSettingsForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(20, 24, 33);
    private static readonly Color Surface = Color.FromArgb(29, 35, 47);
    private static readonly Color Alternate = Color.FromArgb(34, 41, 54);
    private static readonly Color Muted = Color.FromArgb(182, 194, 212);
    private static readonly Color Accent = Color.FromArgb(151, 204, 248);
    private readonly Func<ShortcutProfile, string?> _apply;
    private readonly List<Font> _ownedFonts = [];
    private readonly Dictionary<DesktopCommand, ShortcutRow> _rows = [];
    private readonly Dictionary<string, Label> _columnLabels = [];
    private readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _description = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _note = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 12, 0, 5) };
    private readonly Label _validation = new()
    {
        AutoSize = true, ForeColor = Color.FromArgb(255, 169, 155),
        Margin = new Padding(0, 3, 0, 3), MinimumSize = new Size(0, 42)
    };
    private readonly Button _defaults = MakeButton();
    private readonly Button _windowsPreset = MakeButton();
    private readonly Button _save = MakeButton();
    private readonly Button _cancel = MakeButton();
    private readonly ToolTip _tips = new() { InitialDelay = 500, AutoPopDelay = 10000 };
    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(22), Margin = Padding.Empty
    };
    private Font _uiFont;
    private Font _headingFont;
    private bool _loading;
    private bool _saving;
    private bool _sizingText;
    private int _textWidth = -1;
    private string? _saveFailure;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ShortcutProfile DraftProfile
    {
        get => new(_rows.ToDictionary(pair => pair.Key, pair => pair.Value.Binding));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _loading = true;
            try
            {
                foreach (var pair in _rows) pair.Value.SetBinding(value[pair.Key]);
                _saveFailure = null;
            }
            finally { _loading = false; }
            UpdateValidation();
        }
    }

    internal string ValidationMessage => _validation.Text;
    internal bool CanSave => _save.Enabled;

    public ShortcutSettingsForm(ShortcutProfile initial, Func<ShortcutProfile, string?> apply)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(apply);
        // Keep the complete 96-DPI design together until every control has been added.
        // Otherwise an early Font/layout change can consume the initial DPI scale before
        // the later form size, row heights, and modifier controls are assigned.
        SuspendLayout();
        _apply = apply;
        _uiFont = new Font(UiText.FontFamily, 11);
        _headingFont = new Font(UiText.FontFamily, 20, FontStyle.Bold);
        _ownedFonts.AddRange([_uiFont, _headingFont]);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = _uiFont;
        _title.Font = _headingFont;
        BackColor = Canvas;
        ForeColor = Color.White;
        Icon = AppIcon.Window;
        MinimumSize = new Size(760, 600);
        Size = new Size(1040, 860);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;

        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++)
            _layout.RowStyles.Add(new RowStyle(row == 3 ? SizeType.Percent : SizeType.AutoSize, row == 3 ? 100 : 0));
        _layout.Controls.Add(_title, 0, 0);
        _layout.Controls.Add(_description, 0, 1);
        var presets = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 12)
        };
        presets.Controls.Add(_defaults);
        presets.Controls.Add(_windowsPreset);
        _defaults.Click += (_, _) => LoadDefaults();
        _windowsPreset.Click += (_, _) => LoadWindowsPreset();
        _layout.Controls.Add(presets, 0, 2);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Surface, Margin = Padding.Empty };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 38 + Enum.GetValues<DesktopCommand>().Length * 64 + 8,
            ColumnCount = 4, RowCount = Enum.GetValues<DesktopCommand>().Length + 1,
            Margin = Padding.Empty, Padding = new Padding(8, 0, 8, 8), BackColor = Surface
        };
        foreach (float width in new[] { 28f, 36f, 14f, 22f }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        string[] headers = ["Shortcuts.Action", "Shortcuts.Modifiers", "Shortcuts.Key", "Shortcuts.Preview"];
        for (int column = 0; column < headers.Length; column++)
        {
            var header = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = new Padding(8, 0, 5, 0) };
            _columnLabels.Add(headers[column], header);
            table.Controls.Add(header, column, 0);
        }
        int index = 1;
        foreach (var command in Enum.GetValues<DesktopCommand>())
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            var row = new ShortcutRow(command, DraftChanged) { Name = { BackColor = index % 2 == 0 ? Alternate : Surface } };
            _rows.Add(command, row);
            table.Controls.Add(row.Name, 0, index);
            table.Controls.Add(row.Modifiers, 1, index);
            table.Controls.Add(row.Key, 2, index);
            table.Controls.Add(row.Preview, 3, index);
            index++;
        }
        scroll.Controls.Add(table);
        _layout.Controls.Add(scroll, 0, 3);
        _layout.Controls.Add(_note, 0, 4);
        _layout.Controls.Add(_validation, 0, 5);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0, 8, 0, 0)
        };
        _save.BackColor = Color.FromArgb(48, 87, 123);
        _save.Click += (_, _) => AttemptSave();
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(_save);
        footer.Controls.Add(_cancel);
        _layout.Controls.Add(footer, 0, 6);
        AcceptButton = _save;
        CancelButton = _cancel;
        Controls.Add(_layout);
        _layout.SizeChanged += (_, _) => ConstrainTextWidth();
        DraftProfile = initial;
        ApplyLanguage();
        UiText.Changed += ApplyLanguage;
        ResumeLayout(false);
        PerformAutoScale();
        ConstrainTextWidth();
        PerformLayout();
    }

    internal void LoadDefaults() => DraftProfile = ShortcutProfile.Defaults;
    internal void LoadWindowsPreset() => DraftProfile = ShortcutProfile.WindowsStyle;

    internal bool AttemptSave()
    {
        if (_saving || IsDisposed || Disposing || DialogResult == DialogResult.OK) return false;
        var draft = DraftProfile;
        if (draft.Validate() is not null) { UpdateValidation(); return false; }
        _saving = true;
        UpdateValidation();
        try
        {
            string? error = _apply(draft);
            if (error is not null)
            {
                _saveFailure = error;
                return false;
            }
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
        finally
        {
            _saving = false;
            if (!IsDisposed && !Disposing) UpdateValidation();
        }
    }

    private void DraftChanged(ShortcutRow row)
    {
        if (_loading) return;
        row.ClearUnknownModifiers();
        _saveFailure = null;
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        if (_rows.Count == 0 || IsDisposed) return;
        foreach (var row in _rows.Values)
        {
            // Spaces keep long four-modifier combinations readable on two lines at the minimum width.
            row.Preview.Text = ShortcutText.Format(row.Binding).Replace("+", " + ");
            row.Preview.AccessibleName = row.Name.Text + ": " + row.Preview.Text;
            row.SetModifierAvailability();
            _tips.SetToolTip(row.Preview, row.Preview.Text);
        }
        var issue = DraftProfile.Validate();
        _validation.Text = issue is null ? _saveFailure ?? "" : ShortcutText.ErrorText(issue);
        _save.Enabled = !_saving && issue is null;
        ConstrainTextWidth();
    }

    private void ApplyLanguage()
    {
        if (IsDisposed || Disposing) return;
        SuspendLayout();
        _loading = true;
        try
        {
            _uiFont = new Font(UiText.FontFamily, 11);
            _headingFont = new Font(UiText.FontFamily, 20, FontStyle.Bold);
            // WinForms can ignore an equal Font assignment and keep the earlier instance.
            // Retain every assigned font until its controls have been disposed.
            _ownedFonts.AddRange([_uiFont, _headingFont]);
            Font = _uiFont;
            _title.Font = _headingFont;
            Text = "DisplaySpaces · " + UiText.Get("Shortcuts.Title");
            _title.Text = UiText.Get("Shortcuts.Title");
            _description.Text = UiText.Get("Shortcuts.Description");
            _note.Text = UiText.Get("Shortcuts.Note");
            _defaults.Text = UiText.Get("Shortcuts.DefaultPreset");
            _windowsPreset.Text = UiText.Get("Shortcuts.WindowsPreset");
            _save.Text = UiText.Get("Shortcuts.Save");
            _cancel.Text = UiText.Get("Shortcuts.Cancel");
            _tips.SetToolTip(_defaults, UiText.Get("Shortcuts.PresetHint"));
            _tips.SetToolTip(_windowsPreset, UiText.Get("Shortcuts.PresetHint"));
            foreach (var item in _columnLabels) item.Value.Text = UiText.Get(item.Key);
            foreach (var row in _rows.Values)
            {
                row.Name.Text = ShortcutText.CommandName(row.Command);
                row.Key.AccessibleName = row.Name.Text + " · " + UiText.Get("Shortcuts.Key");
                row.RefreshKeyNames();
            }
        }
        finally
        {
            _loading = false;
            ResumeLayout(true);
        }
        UpdateValidation();
    }

    private void ConstrainTextWidth()
    {
        if (_sizingText) return;
        int width = Math.Max(100, _layout.ClientSize.Width - _layout.Padding.Horizontal);
        if (_textWidth == width) return;
        _textWidth = width;
        _sizingText = true;
        try
        {
            foreach (var label in new[] { _description, _note, _validation })
                if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
        }
        finally { _sizingText = false; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UiText.Changed -= ApplyLanguage;
            _tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in _ownedFonts) font.Dispose();
            _ownedFonts.Clear();
        }
    }

    private static Button MakeButton() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(128, 40),
        Padding = new Padding(14, 6, 14, 6), Margin = new Padding(0, 0, 10, 0),
        FlatStyle = FlatStyle.Flat, BackColor = Alternate, ForeColor = Color.White,
        FlatAppearance = { BorderColor = Color.FromArgb(69, 84, 103) }, UseVisualStyleBackColor = false
    };

    private sealed class ShortcutRow
    {
        private readonly Dictionary<ShortcutModifiers, CheckBox> _modifiers = [];
        private ShortcutModifiers _unknownModifiers;
        public DesktopCommand Command { get; }
        public Label Name { get; } = new()
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 2, 8, 2)
        };
        public FlowLayoutPanel Modifiers { get; } = new()
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(4, 0, 0, 0), Padding = new Padding(0, 15, 0, 0)
        };
        public ComboBox Key { get; } = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Alternate, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 8, 0), DropDownHeight = 320, IntegralHeight = false
        };
        public Label Preview { get; } = new()
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Accent, Margin = new Padding(8, 2, 5, 2)
        };

        public ShortcutBinding? Binding => Key.SelectedItem is KeyChoice { Value: int key }
            ? new(_modifiers.Where(pair => pair.Value.Checked).Aggregate(_unknownModifiers, (flags, pair) => flags | pair.Key), key)
            : null;

        public ShortcutRow(DesktopCommand command, Action<ShortcutRow> changed)
        {
            Command = command;
            foreach (var (modifier, text) in new[]
            {
                (ShortcutModifiers.Win, "Win"), (ShortcutModifiers.Control, "Ctrl"),
                (ShortcutModifiers.Alt, "Alt"), (ShortcutModifiers.Shift, "Shift")
            })
            {
                var checkbox = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 3) };
                _modifiers.Add(modifier, checkbox);
                Modifiers.Controls.Add(checkbox);
                checkbox.CheckedChanged += (_, _) => changed(this);
            }
            RefreshKeyNames();
            Key.SelectedIndexChanged += (_, _) => changed(this);
        }

        public void SetBinding(ShortcutBinding? binding)
        {
            _unknownModifiers = (binding?.Modifiers ?? ShortcutModifiers.None)
                & ~(ShortcutModifiers.Win | ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift);
            foreach (var pair in _modifiers) pair.Value.Checked = binding?.Modifiers.HasFlag(pair.Key) == true;
            int? virtualKey = binding?.VirtualKey;
            if (!Key.Items.Cast<KeyChoice>().Any(choice => choice.Value == virtualKey)) Key.Items.Add(new KeyChoice(virtualKey));
            Key.SelectedIndex = Key.Items.Cast<KeyChoice>().ToList().FindIndex(choice => choice.Value == virtualKey);
        }

        public void ClearUnknownModifiers() => _unknownModifiers = ShortcutModifiers.None;

        public void SetModifierAvailability()
        {
            bool enabled = Key.SelectedItem is KeyChoice { Value: not null };
            foreach (var checkbox in _modifiers.Values) checkbox.Enabled = enabled;
        }

        public void RefreshKeyNames()
        {
            int? selected = (Key.SelectedItem as KeyChoice)?.Value;
            Key.BeginUpdate();
            try
            {
                Key.Items.Clear();
                Key.Items.Add(new KeyChoice(null));
                foreach (int key in ShortcutProfile.AllowedKeys) Key.Items.Add(new KeyChoice(key));
                if (selected.HasValue && !ShortcutProfile.AllowedKeys.Contains(selected.Value)) Key.Items.Add(new KeyChoice(selected));
                Key.SelectedIndex = Key.Items.Cast<KeyChoice>().ToList().FindIndex(choice => choice.Value == selected);
            }
            finally { Key.EndUpdate(); }
        }
    }

    private sealed record KeyChoice(int? Value)
    {
        public override string ToString() => Value.HasValue ? ShortcutText.KeyName(Value.Value) : UiText.Get("Shortcuts.Disabled");
    }
}
