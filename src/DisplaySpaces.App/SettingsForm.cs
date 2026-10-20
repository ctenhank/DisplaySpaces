using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Services;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

internal sealed record StartupSettingsState(bool Enabled, bool Available = true, string? Hint = null);

internal sealed record SettingsSnapshot(
    string LanguageCode,
    StartupSettingsState Startup,
    DisplaySwitchingMode SwitchingMode,
    bool WrapDesktopSwitching,
    bool FollowMovedWindow,
    DesktopNameSettings DesktopNames);

/// <summary>Uses the existing application setters; the form never owns a second preference store.</summary>
internal sealed record SettingsBindings(
    Func<SettingsSnapshot> Read,
    Func<string, string?> Language,
    Func<bool, string?> StartWithWindows,
    Func<DisplaySwitchingMode, string?> SwitchingMode,
    Func<bool, string?> WrapDesktopSwitching,
    Func<bool, string?> FollowMovedWindow,
    Func<DesktopNameSettings, string?> DesktopNames,
    Action OpenShortcuts);

internal sealed class SettingsForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(20, 24, 33);
    private static readonly Color Surface = Color.FromArgb(29, 35, 47);
    private static readonly Color Muted = Color.FromArgb(182, 194, 212);
    private readonly SettingsBindings _bindings;
    private readonly TabControl _categories = new() { Dock = DockStyle.Fill, Name = "SettingsCategories" };
    private readonly Dictionary<Control, string> _localized = [];
    private readonly List<Label> _wrappingLabels = [];
    private readonly ComboBox _language = Choice("Language");
    private readonly CheckBox _startup = Toggle("StartWithWindows");
    private readonly ComboBox _switching = Choice("SwitchingMode");
    private readonly CheckBox _wrap = Toggle("WrapDesktopSwitching");
    private readonly CheckBox _follow = Toggle("FollowMovedWindow");
    private readonly ComboBox _nameMode = Choice("DesktopNameMode");
    private readonly ComboBox _nameCorner = Choice("DesktopNameCorner");
    private readonly Label _startupHint = new();
    private readonly Label _switchingHint = new();
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(255, 169, 155), Name = "SettingsError", Visible = false };
    private readonly Button _editShortcuts = Button("EditShortcuts");
    private readonly Button _close = Button("CloseSettings");
    private readonly List<Font> _fonts = [];
    private bool _loading;
    private bool _applying;
    private bool _sizing;
    private bool _fitting;

    internal string ErrorMessage => _error.Text;

    public SettingsForm(SettingsBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Canvas;
        ForeColor = Color.White;
        Icon = AppIcon.Window;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(620, 460);
        Size = new Size(840, 670);
        ShowInTaskbar = true;
        MinimizeBox = false;
        MaximizeBox = true;
        Name = "DisplaySpacesSettings";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 16), Name = "SettingsHeading" };
        Localize(heading, "Settings.Heading");
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_categories, 0, 1);
        _error.Margin = new Padding(0, 10, 0, 0);
        _wrappingLabels.Add(_error);
        layout.Controls.Add(_error, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var applied = Description("Settings.AppliedImmediately");
        applied.Anchor = AnchorStyles.Left;
        footer.Controls.Add(applied, 0, 0);
        Localize(_close, "Settings.Close");
        _close.DialogResult = DialogResult.Cancel;
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_close, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        CancelButton = _close;
        Controls.Add(layout);

        var general = AddPage("General");
        Add(general, Label("Language"));
        Add(general, _language);
        Add(general, Description("Settings.LanguageHint"));
        Localize(_startup, "Tray.StartWithWindows");
        Add(general, _startup);
        PrepareDescription(_startupHint);
        Add(general, _startupHint);

        var desktops = AddPage("Desktops");
        Add(desktops, Label("Tray.DisplaySwitching"));
        Add(desktops, _switching);
        PrepareDescription(_switchingHint);
        Add(desktops, _switchingHint);
        Localize(_wrap, "Tray.WrapDesktopSwitching");
        Add(desktops, _wrap);
        Add(desktops, Description("Tray.WrapDesktopSwitchingHint"));
        Localize(_follow, "Tray.FollowMovedWindow");
        Add(desktops, _follow);
        Add(desktops, Description("Tray.FollowMovedWindowHint"));

        var appearance = AddPage("Appearance");
        Add(appearance, Label("Tray.NameDisplay"));
        Add(appearance, _nameMode);
        Add(appearance, Description("Settings.DesktopNamesHint"));
        Add(appearance, Label("Tray.NamePosition"));
        Add(appearance, _nameCorner);

        var shortcuts = AddPage("Shortcuts");
        Add(shortcuts, Description("Settings.ShortcutsHint"));
        Localize(_editShortcuts, "Shortcuts.Open");
        Add(shortcuts, _editShortcuts);
        Add(shortcuts, Description("Settings.ShortcutsPaused"));

        _language.SelectedIndexChanged += (_, _) => { if (!_loading && _language.SelectedItem is ChoiceItem item) ChangeLanguage((string)item.Value); };
        _startup.CheckedChanged += (_, _) => { if (!_loading) ChangeStartup(_startup.Checked); };
        _switching.SelectedIndexChanged += (_, _) => { if (!_loading && _switching.SelectedItem is ChoiceItem item) ChangeSwitchingMode((DisplaySwitchingMode)item.Value); };
        _wrap.CheckedChanged += (_, _) => { if (!_loading) ChangeWrap(_wrap.Checked); };
        _follow.CheckedChanged += (_, _) => { if (!_loading) ChangeFollow(_follow.Checked); };
        _nameMode.SelectedIndexChanged += (_, _) => { if (!_loading && _nameMode.SelectedItem is ChoiceItem item) ChangeDesktopNameMode((DesktopNameDisplayMode)item.Value); };
        _nameCorner.SelectedIndexChanged += (_, _) => { if (!_loading && _nameCorner.SelectedItem is ChoiceItem item) ChangeDesktopNameCorner((DesktopToastCorner)item.Value); };
        _editShortcuts.Click += (_, _) => OpenShortcutEditor();
        _categories.SelectedIndexChanged += (_, _) => ConstrainText();
        SizeChanged += (_, _) => ConstrainText();
        Activated += (_, _) => RefreshSettings();
        ApplyLanguage();
        UiText.Changed += ApplyLanguage;
        ResumeLayout(false);
        PerformAutoScale();
        ConstrainText();
        PerformLayout();
    }

    internal void RefreshSettings()
    {
        if (IsDisposed || Disposing) return;
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var current = _bindings.Read();
            Select(_language, current.LanguageCode);
            _startup.Enabled = current.Startup.Available;
            _startup.Checked = current.Startup.Enabled;
            _startupHint.Text = current.Startup.Hint ?? UiText.Get("Startup.AutoRunHint");
            _startup.AccessibleDescription = _startupHint.Text;
            Select(_switching, current.SwitchingMode);
            _switchingHint.Text = UiText.Get("Tray.Switching" + current.SwitchingMode + "Hint");
            _switching.AccessibleDescription = _switchingHint.Text;
            _wrap.Checked = current.WrapDesktopSwitching;
            _follow.Checked = current.FollowMovedWindow;
            Select(_nameMode, current.DesktopNames.Mode);
            Select(_nameCorner, current.DesktopNames.Corner);
            _nameCorner.Enabled = current.DesktopNames.Mode != DesktopNameDisplayMode.Off;
        }
        finally { _loading = wasLoading; }
        ConstrainText();
    }

    internal bool ChangeLanguage(string code) => code == _bindings.Read().LanguageCode || Apply(() => _bindings.Language(code));
    internal bool ChangeStartup(bool enabled) =>
        _bindings.Read().Startup.Available && Apply(() => _bindings.StartWithWindows(enabled));
    internal bool ChangeSwitchingMode(DisplaySwitchingMode mode) => Apply(() => _bindings.SwitchingMode(mode));
    internal bool ChangeWrap(bool enabled) => Apply(() => _bindings.WrapDesktopSwitching(enabled));
    internal bool ChangeFollow(bool enabled) => Apply(() => _bindings.FollowMovedWindow(enabled));
    internal bool ChangeDesktopNameMode(DesktopNameDisplayMode mode) =>
        Apply(() => _bindings.DesktopNames(_bindings.Read().DesktopNames with { Mode = mode }));
    internal bool ChangeDesktopNameCorner(DesktopToastCorner corner) =>
        Apply(() => _bindings.DesktopNames(_bindings.Read().DesktopNames with { Corner = corner }));
    internal void OpenShortcutEditor() => _bindings.OpenShortcuts();

    private bool Apply(Func<string?> change)
    {
        if (_loading || _applying || IsDisposed || Disposing) return false;
        _applying = true;
        try
        {
            string? error = change();
            _error.Text = error ?? "";
            _error.Visible = error is not null;
            return error is null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or InvalidOperationException)
        {
            AppLog.Write(error);
            _error.Text = UiText.Get("Settings.ChangeFailed", error.Message);
            _error.Visible = true;
            return false;
        }
        finally
        {
            _applying = false;
            RefreshSettings();
        }
    }

    internal void ApplyLanguage()
    {
        if (IsDisposed || Disposing) return;
        bool wasLoading = _loading;
        _loading = true;
        SuspendLayout();
        try
        {
            Text = UiText.Get("Settings.Title");
            foreach (var (control, key) in _localized)
            {
                control.Text = UiText.Get(key);
                control.AccessibleName = control.Text;
            }
            var nextFont = new Font(UiText.FontFamily, 11);
            var headingFont = new Font(UiText.FontFamily, 20, FontStyle.Bold);
            // Equal Font assignments can leave the previous instance on a control.
            // Keep assigned fonts alive until the controls have been disposed.
            _fonts.AddRange([nextFont, headingFont]);
            Font = nextFont;
            _localized.Keys.First(control => control.Name == "SettingsHeading").Font = headingFont;
            SetChoices(_language, [new("system", UiText.Get("SystemLanguage")), .. UiText.SupportedLanguages.Select(language => new ChoiceItem(language.Code, language.NativeName))]);
            SetChoices(_switching, Enum.GetValues<DisplaySwitchingMode>().Select(mode => new ChoiceItem(mode, UiText.Get("Tray.Switching" + mode))));
            SetChoices(_nameMode, Enum.GetValues<DesktopNameDisplayMode>().Select(mode => new ChoiceItem(mode, UiText.Get("Tray.Name" + mode))));
            SetChoices(_nameCorner, Enum.GetValues<DesktopToastCorner>().Select(corner => new ChoiceItem(corner, UiText.Get("Tray." + corner))));
            _language.AccessibleName = UiText.Get("Language");
            _nameMode.AccessibleName = UiText.Get("Tray.NameDisplay");
            _nameCorner.AccessibleName = UiText.Get("Tray.NamePosition");
            _switching.AccessibleName = UiText.Get("Tray.DisplaySwitching");
            _wrap.AccessibleDescription = UiText.Get("Tray.WrapDesktopSwitchingHint");
            _follow.AccessibleDescription = UiText.Get("Tray.FollowMovedWindowHint");
            RefreshSettings();
        }
        finally
        {
            _loading = wasLoading;
            ResumeLayout(true);
        }
        ConstrainText();
    }

    internal void FitToWorkingArea(Rectangle area)
    {
        if (_fitting || area.Width <= 0 || area.Height <= 0) return;
        _fitting = true;
        try
        {
            int scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
            MinimumSize = new Size(Math.Min(scale(620), area.Width), Math.Min(scale(460), area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        }
        finally { _fitting = false; }
    }

    protected override void OnShown(EventArgs e)
    {
        FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        base.OnShown(e);
        ConstrainText();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        ConstrainText();
    }

    private TableLayoutPanel AddPage(string category)
    {
        var page = new TabPage { Name = category, BackColor = Surface, ForeColor = Color.White, UseVisualStyleBackColor = false };
        Localize(page, "Settings." + category);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content);
        scroll.SizeChanged += (_, _) => ConstrainText();
        page.Controls.Add(scroll);
        _categories.TabPages.Add(page);
        return content;
    }

    private static void Add(TableLayoutPanel panel, Control control)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.TabIndex = row;
        panel.Controls.Add(control, 0, row);
    }

    private Label Label(string key)
    {
        var label = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 8) };
        Localize(label, key);
        _wrappingLabels.Add(label);
        return label;
    }

    private Label Description(string key)
    {
        var label = new Label();
        PrepareDescription(label);
        Localize(label, key);
        return label;
    }

    private void PrepareDescription(Label label)
    {
        label.AutoSize = true;
        label.ForeColor = Muted;
        label.Margin = new Padding(0, 6, 0, 20);
        _wrappingLabels.Add(label);
    }

    private void Localize(Control control, string key) => _localized.Add(control, key);

    private void ConstrainText()
    {
        if (_sizing || IsDisposed || Disposing) return;
        _sizing = true;
        try
        {
            foreach (var label in _wrappingLabels)
            {
                if (label.Parent is not Control parent) continue;
                int width = Math.Max(80, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal - 20);
                if (parent is TableLayoutPanel table && table.ColumnCount > 1)
                    width = Math.Max(80, width - _close.Width - _close.Margin.Horizontal);
                if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
            }
            foreach (var checkbox in new[] { _startup, _wrap, _follow })
            {
                if (checkbox.Parent is not Control parent) continue;
                int width = Math.Max(80, parent.ClientSize.Width - checkbox.Margin.Horizontal - 20);
                if (checkbox.MaximumSize.Width != width) checkbox.MaximumSize = new Size(width, 0);
            }
        }
        finally { _sizing = false; }
    }

    private static ComboBox Choice(string name) => new()
    {
        Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
        Margin = new Padding(0, 0, 18, 4), BackColor = Canvas, ForeColor = Color.White, IntegralHeight = true
    };

    private static CheckBox Toggle(string name) => new()
    {
        Name = name, AutoSize = true, Margin = new Padding(0, 12, 0, 4), UseVisualStyleBackColor = false
    };

    private static Button Button(string name) => new()
    {
        Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 8, 16, 8),
        BackColor = Color.FromArgb(48, 87, 123), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 8, 0, 0), UseVisualStyleBackColor = false
    };

    private static void SetChoices(ComboBox control, IEnumerable<ChoiceItem> choices)
    {
        control.BeginUpdate();
        try
        {
            control.Items.Clear();
            control.Items.AddRange(choices.Cast<object>().ToArray());
        }
        finally { control.EndUpdate(); }
    }

    private static void Select(ComboBox control, object value)
    {
        control.SelectedItem = control.Items.Cast<ChoiceItem>().FirstOrDefault(item => Equals(item.Value, value));
    }

    private sealed record ChoiceItem(object Value, string Label)
    {
        public override string ToString() => Label;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UiText.Changed -= ApplyLanguage;
        }
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in _fonts) font.Dispose();
            _fonts.Clear();
        }
    }
}
