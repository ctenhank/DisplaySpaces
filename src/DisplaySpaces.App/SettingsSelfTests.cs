using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Shortcuts;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

/// <summary>Exercises settings against an isolated JSON file and fake startup state; never touches Run or user windows.</summary>
internal static class SettingsSelfTests
{
    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "settings-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string originalLanguage = UiText.SelectedLanguageCode;
        string path = Path.Combine(output, $"settings-fixture-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"language":"en","unrelated":{"keep":23}}""");
            ShortcutPreferences.Save(ShortcutProfile.WindowsStyle, path);
            UiText.SetLanguage("en", persist: false);
            var fixture = new SettingsFixture(path);
            CheckChanges(fixture, results);
            CheckLanguagesAndLayout(fixture, output, results);
            Check(JsonNode.Parse(File.ReadAllText(path))!["unrelated"]!["keep"]!.GetValue<int>() == 23
                && ShortcutPreferences.Load(path)[DesktopCommand.ToggleOverview]
                    == ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview],
                "unified settings preserve unrelated JSON values and the existing shortcut profile", results);
        }
        finally
        {
            UiText.SetLanguage(originalLanguage, persist: false);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckChanges(SettingsFixture fixture, List<string> results)
    {
        string initial = File.ReadAllText(fixture.Path);
        using var form = new SettingsForm(fixture.Bindings);
        form.Show();
        Pump();
        Check(fixture.Callbacks == 0 && File.ReadAllText(fixture.Path) == initial,
            "opening unified settings reads existing preferences without writing or enabling startup", results);
        form.RefreshSettings();
        form.RefreshSettings();
        form.ApplyLanguage();
        Check(fixture.Callbacks == 0 && File.ReadAllText(fixture.Path) == initial,
            "refreshing and translating settings never invoke change callbacks", results);

        Check(form.ChangeStartup(true) && fixture.StartupEnabled && fixture.Callbacks == 1,
            "startup changes reach the injected registration callback exactly once", results);
        Check(form.ChangeStartup(false) && !fixture.StartupEnabled && fixture.Callbacks == 2,
            "startup can be disabled without modifying the user Run registry", results);
        Check(form.ChangeSwitchingMode(DisplaySwitchingMode.Linked)
            && DisplaySwitchingPreferences.Load(fixture.Path) == DisplaySwitchingMode.Linked,
            "the settings display mode persists through the existing preference service", results);
        Check(form.ChangeWrap(false) && !DesktopNavigationPreferences.Load(fixture.Path),
            "bounded desktop switching persists through the existing navigation preference service", results);
        Check(form.ChangeFollow(true) && WindowMovePreferences.Load(fixture.Path),
            "follow moved window persists through the existing window move preference service", results);
        Check(form.ChangeDesktopNameMode(DesktopNameDisplayMode.Always)
            && form.ChangeDesktopNameCorner(DesktopToastCorner.TopLeft)
            && DesktopNamePreferences.Load(fixture.Path)
                == new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft),
            "desktop name mode and corner persist together through the existing preference service", results);

        int before = fixture.Callbacks;
        CheckBox follow = Named<CheckBox>(form, "FollowMovedWindow");
        follow.Checked = false;
        Check(fixture.Callbacks == before + 1 && !WindowMovePreferences.Load(fixture.Path),
            "changing the actual follow checkbox invokes persistence once without refresh recursion", results);
        before = fixture.Callbacks;
        Check(form.ChangeLanguage("ja") && fixture.Callbacks == before + 1
            && UiText.ReadLanguagePreference(fixture.Path) == "ja" && UiText.CurrentLanguageCode == "ja",
            "language changes persist and refresh the open form without recursive writes", results);

        before = fixture.Callbacks;
        DesktopNavigationPreferences.Save(true, fixture.Path);
        form.RefreshSettings();
        Check(fixture.Callbacks == before && Named<CheckBox>(form, "WrapDesktopSwitching").Checked,
            "refresh reflects externally updated preferences without writing them back", results);
        Check(form.ChangeDesktopNameMode(DesktopNameDisplayMode.Off)
            && !Named<ComboBox>(form, "DesktopNameCorner").Enabled,
            "desktop name position is disabled while name display is off", results);
        Check(form.ChangeDesktopNameMode(DesktopNameDisplayMode.Always)
            && Named<ComboBox>(form, "DesktopNameCorner").Enabled
            && DesktopNamePreferences.Load(fixture.Path).Corner == DesktopToastCorner.TopLeft,
            "reenabling desktop names retains the saved corner and restores its control", results);

        fixture.FailureKey = "switching";
        before = fixture.Callbacks;
        Named<ComboBox>(form, "SwitchingMode").SelectedIndex = 0;
        Check(fixture.Callbacks == before + 1 && form.ErrorMessage.Contains("fixture save failure")
            && DisplaySwitchingPreferences.Load(fixture.Path) == DisplaySwitchingMode.Linked
            && Named<ComboBox>(form, "SwitchingMode").SelectedIndex == 1,
            "a failed setting save reports an inline error and restores the last persisted selection", results);
        fixture.FailureKey = null;
        Check(form.ChangeSwitchingMode(DisplaySwitchingMode.Independent)
            && form.ErrorMessage.Length == 0
            && DisplaySwitchingPreferences.Load(fixture.Path) == DisplaySwitchingMode.Independent,
            "retrying a failed setting save succeeds and clears the inline error", results);

        fixture.ThrowKey = "startup";
        Check(!form.ChangeStartup(true) && !fixture.StartupEnabled
            && form.ErrorMessage.Contains("fixture callback failure"),
            "callback exceptions remain inline and leave the prior startup state intact", results);
        fixture.ThrowKey = null;
        fixture.StartupAvailable = false;
        form.RefreshSettings();
        before = fixture.Callbacks;
        Check(!Named<CheckBox>(form, "StartWithWindows").Enabled
            && !form.ChangeStartup(true) && fixture.Callbacks == before,
            "unavailable startup registration cannot invoke a write callback", results);
        fixture.StartupAvailable = true;
        form.RefreshSettings();

        before = fixture.Callbacks;
        form.OpenShortcutEditor();
        Check(fixture.ShortcutOpens == 1 && fixture.Callbacks == before,
            "the integrated shortcut entry opens its editor without changing preferences", results);
        form.Hide();
        form.Show();
        form.RefreshSettings();
        Check(!form.IsDisposed && fixture.Callbacks == before,
            "showing an existing settings window again preserves state without saving", results);
        form.Close();
        Check(form.IsDisposed, "closing settings disposes its controls and language subscription", results);
        UiText.SetLanguage("en", persist: false);
        using var reopened = new SettingsForm(fixture.Bindings);
        reopened.Show();
        Pump();
        Check(fixture.Callbacks == before && Named<CheckBox>(reopened, "WrapDesktopSwitching").Checked
            && !Named<CheckBox>(reopened, "FollowMovedWindow").Checked,
            "reopening settings reloads persisted options without reapplying them", results);
        reopened.Close();
    }

    private static void CheckLanguagesAndLayout(SettingsFixture fixture, string output, List<string> results)
    {
        using var form = new SettingsForm(fixture.Bindings);
        form.Show();
        Pump();
        var tabs = Descendants(form).OfType<TabControl>().Single();
        Check(tabs.TabPages.Count == 4, "settings group controls into four navigable categories", results);
        foreach (var language in UiText.SupportedLanguages)
        {
            int before = fixture.Callbacks;
            Check(form.ChangeLanguage(language.Code)
                && fixture.Callbacks == before + 1,
                $"language selection applies once without callback recursion: {language.Code}", results);
            Check(UiText.ResourceKeys.Where(key => key.StartsWith("Settings.", StringComparison.Ordinal))
                .All(key => UiText.HasTranslation(language.Code, key)),
                $"every unified settings resource has a translation: {language.Code}", results);
            Check(form.Text.Contains("DisplaySpaces") && !form.Text.Contains("Settings.Title"),
                $"the settings window title is localized and branded: {language.Code}", results);

            form.FitToWorkingArea(Screen.PrimaryScreen!.WorkingArea);
            form.Size = new Size(Math.Min(1000, Screen.PrimaryScreen.WorkingArea.Width),
                Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height));
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                Pump();
                Check(!string.IsNullOrWhiteSpace(page.Text) && InteractiveControlsFit(page),
                    $"settings controls fit their category at normal size: {language.Code}, {page.Name}", results);
                Render(form, Path.Combine(output, $"settings-{language.Code}-{page.Name}.png"));
            }
            tabs.SelectedIndex = 0;
            Render(form, Path.Combine(output, $"settings-{language.Code}.png"));

            Rectangle compactArea = new(Screen.PrimaryScreen.WorkingArea.Location, new Size(800, 600));
            form.FitToWorkingArea(compactArea);
            Pump();
            Check(compactArea.Contains(form.Bounds),
                $"settings remain within a compact working area at the current monitor DPI: {language.Code}", results);
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                Pump();
                Check(InteractiveControlsFit(page),
                    $"settings controls remain reachable at compact size: {language.Code}, {page.Name}", results);
                Render(form, Path.Combine(output, $"settings-{language.Code}-{page.Name}-compact.png"));
            }
            tabs.SelectedIndex = 0;
            Render(form, Path.Combine(output, $"settings-{language.Code}-compact.png"));
        }
        form.Close();
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, searchAllChildren: true).OfType<T>().Single();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static bool InteractiveControlsFit(Control root) =>
        Descendants(root).Where(control => control is ComboBox or CheckBox or Button).All(control =>
            control.Width > 0 && control.Height > 0 && control.Parent is { } parent
                && parent.DisplayRectangle.Contains(control.Bounds));

    private static void Render(Form form, string path)
    {
        form.PerformLayout();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(path, ImageFormat.Png);
    }

    private static void Check(bool condition, string message, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(message);
        results.Add("PASS " + message);
    }

    private static void Pump()
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 70)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private sealed class SettingsFixture
    {
        public string Path { get; }
        public bool StartupEnabled { get; private set; }
        public bool StartupAvailable { get; set; } = true;
        public int Callbacks { get; private set; }
        public int ShortcutOpens { get; private set; }
        public string? FailureKey { get; set; }
        public string? ThrowKey { get; set; }
        public SettingsBindings Bindings { get; }

        public SettingsFixture(string path)
        {
            Path = path;
            Bindings = new SettingsBindings(
                Read,
                code => Apply("language", () =>
                {
                    UiText.WriteLanguagePreference(Path, code);
                    UiText.SetLanguage(code, persist: false);
                }),
                enabled => Apply("startup", () => StartupEnabled = enabled),
                mode => Apply("switching", () => DisplaySwitchingPreferences.Save(mode, Path)),
                wrap => Apply("wrap", () => DesktopNavigationPreferences.Save(wrap, Path)),
                follow => Apply("follow", () => WindowMovePreferences.Save(follow, Path)),
                names => Apply("names", () => DesktopNamePreferences.Save(names, Path)),
                () => ShortcutOpens++);
        }

        private SettingsSnapshot Read() => new(
            UiText.ReadLanguagePreference(Path), new(StartupEnabled, StartupAvailable),
            DisplaySwitchingPreferences.Load(Path), DesktopNavigationPreferences.Load(Path),
            WindowMovePreferences.Load(Path), DesktopNamePreferences.Load(Path));

        private string? Apply(string key, Action action)
        {
            Callbacks++;
            if (ThrowKey == key) throw new IOException("fixture callback failure");
            if (FailureKey == key) return "fixture save failure";
            action();
            return null;
        }
    }
}
