using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Shortcuts;

namespace BetterVirtualDesktop.App;

/// <summary>Exercises desktop boundaries using an empty window catalog and isolated settings.</summary>
internal static class DesktopNavigationSelfTests
{
    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string previousLanguage = UiText.SelectedLanguageCode;
        string token = Guid.NewGuid().ToString("N");
        string settingsPath = Path.Combine(output, $"navigation-settings-{token}.json");
        var journal = new RecoveryJournal(Path.Combine(output, $"navigation-journal-{token}.json"));
        try
        {
            CheckPreferences(settingsPath, results);
            UiText.SetLanguage("en", persist: false);
            var controller = new DesktopController(journal, include: _ => false);
            Check(controller.Windows.Count == 0, "desktop navigation tests exclude all external windows", results);
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            CheckNavigation(controller, results);
            CheckMenu(controller, results);
            ResetToSingleDesktop(controller);
            CheckNotifications(controller, dispatcher, results);
            CheckBoundaryLabels(output, results);
        }
        finally
        {
            UiText.SetLanguage(previousLanguage, persist: false);
            journal.DeleteIfEmpty();
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static void CheckPreferences(string path, List<string> results)
    {
        Check(DesktopNavigationPreferences.Load(path),
            "missing navigation preferences preserve the existing wrap-around default", results);
        File.WriteAllText(path, """{"language":"ja","unrelated":{"keep":17}}""");
        ShortcutPreferences.Save(ShortcutProfile.WindowsStyle, path);
        var names = new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft);
        DesktopNamePreferences.Save(names, path);
        foreach (bool wrapAround in new[] { false, true })
        {
            DesktopNavigationPreferences.Save(wrapAround, path);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(DesktopNavigationPreferences.Load(path) == wrapAround
                && saved["wrapDesktopSwitching"]!.GetValue<bool>() == wrapAround
                && saved["language"]!.GetValue<string>() == "ja"
                && saved["unrelated"]!["keep"]!.GetValue<int>() == 17
                && DesktopNamePreferences.Load(path) == names
                && ShortcutPreferences.Load(path)[DesktopCommand.ToggleOverview]
                    == ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview],
                $"saving navigation mode preserves existing preferences: wrap={wrapAround}", results);
        }
        DesktopNavigationPreferences.Save(false, path);
        UiText.WriteLanguagePreference(path, "zh-Hant");
        ShortcutPreferences.Save(ShortcutProfile.Defaults, path);
        DesktopNamePreferences.Save(new(DesktopNameDisplayMode.Off), path);
        Check(!DesktopNavigationPreferences.Load(path)
            && UiText.ReadLanguagePreference(path) == "zh-Hant",
            "saving language, shortcuts and desktop-name settings preserves disabled wrapping", results);
        foreach (string malformed in new[]
        {
            "{broken", "[]", "null", """{"unrelated":true}""",
            """{"wrapDesktopSwitching":"false"}""", """{"wrapDesktopSwitching":0}""",
            """{"wrapDesktopSwitching":null}"""
        })
        {
            File.WriteAllText(path, malformed);
            Check(DesktopNavigationPreferences.Load(path),
                $"invalid navigation settings recover the default: {malformed}", results);
        }
        DesktopNavigationPreferences.Save(false, path);
        Check(!DesktopNavigationPreferences.Load(path),
            "navigation preferences can be saved after malformed configuration", results);
    }

    private static void CheckNavigation(DesktopController controller, List<string> results)
    {
        string display = Screen.PrimaryScreen!.DeviceName;
        var first = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        Check(controller.WrapDesktopSwitching, "new controllers preserve wrapping by default", results);
        controller.AddDesktop(display);
        var last = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        controller.SwitchRelative(display, 1);
        Check(Current() == first, "default next-desktop navigation wraps last to first", results);
        controller.SwitchRelative(display, -1);
        Check(Current() == last, "default previous-desktop navigation wraps first to last", results);

        controller.WrapDesktopSwitching = false;
        int changes = 0;
        var boundaries = new List<string>();
        void Changed() => changes++;
        void Boundary(string id) => boundaries.Add(id);
        controller.Changed += Changed;
        controller.SwitchBoundaryReached += Boundary;
        try
        {
            long generation = controller.VisibilityGeneration;
            var others = controller.Catalog.Snapshot().Where(item => item.Id != display)
                .ToDictionary(item => item.Id, item => item.ActiveDesktopId);
            controller.SwitchRelative(display, 1);
            Check(Current() == last && changes == 0 && controller.VisibilityGeneration == generation
                && boundaries.SequenceEqual(new[] { display }),
                "blocked last-desktop navigation signals the boundary without a visibility change", results);
            controller.SwitchRelative(display, -1);
            Check(Current() == first && changes == 1 && controller.VisibilityGeneration == generation + 1,
                "bounded navigation still switches to an adjacent desktop", results);
            generation = controller.VisibilityGeneration;
            controller.SwitchRelative(display, -1);
            Check(Current() == first && changes == 1 && controller.VisibilityGeneration == generation
                && boundaries.SequenceEqual(new[] { display, display }),
                "blocked first-desktop navigation signals the boundary without a visibility change", results);
            controller.SwitchRelative(display, 0);
            Check(changes == 1 && boundaries.Count == 2 && controller.VisibilityGeneration == generation
                && controller.Catalog.Snapshot().Where(item => item.Id != display)
                    .All(item => others[item.Id] == item.ActiveDesktopId),
                "zero navigation is silent and relative switching leaves other displays unchanged", results);
        }
        finally
        {
            controller.Changed -= Changed;
            controller.SwitchBoundaryReached -= Boundary;
        }

        Guid Current() => controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
    }

    private static void CheckMenu(DesktopController controller, List<string> results)
    {
        controller.WrapDesktopSwitching = true;
        bool failSave = true;
        var saved = new List<bool>();
        var errors = new List<Exception>();
        using var menu = new WrapDesktopSwitchingMenuItem(controller, wrap =>
        {
            if (failSave) throw new IOException("Simulated navigation settings write failure.");
            saved.Add(wrap);
        }, errors.Add);
        Check(menu.Name == "Tray.WrapDesktopSwitching" && menu.Checked,
            "navigation menu initially reflects the controller's wrap setting", results);
        menu.PerformClick();
        Check(menu.Checked && controller.WrapDesktopSwitching && saved.Count == 0 && errors.Count == 1,
            "a failed navigation save preserves the runtime mode and checkmark", results);
        failSave = false;
        menu.PerformClick();
        Check(!menu.Checked && !controller.WrapDesktopSwitching && saved.SequenceEqual(new[] { false }),
            "unchecking wrap-around saves and applies bounded desktop switching", results);
        menu.PerformClick();
        Check(menu.Checked && controller.WrapDesktopSwitching && saved.SequenceEqual(new[] { false, true }),
            "checking wrap-around restores the original switching behavior", results);
    }

    private static void CheckNotifications(DesktopController controller, Control dispatcher, List<string> results)
    {
        string display = Screen.PrimaryScreen!.DeviceName;
        controller.WrapDesktopSwitching = false;
        using (var names = new DesktopNameToastCoordinator(controller, dispatcher))
        {
            long generation = controller.VisibilityGeneration;
            controller.SwitchRelative(display, -1);
            PumpFor(70);
            Check(names.VisibleForms.Count == 1 && names.VisibleForms[0].DisplayId == display
                && names.VisibleForms[0].BoundaryText == UiText.Get("Toast.OnlyDesktop")
                && controller.VisibilityGeneration == generation,
                "switch-only mode shows the single-desktop boundary without a desktop-change event", results);
            names.Mode = DesktopNameDisplayMode.Off;
            controller.SwitchRelative(display, 1);
            PumpFor(50);
            Check(names.VisibleForms.Count == 0, "off mode suppresses blocked-edge notifications", results);
            names.Mode = DesktopNameDisplayMode.OnSwitch;
            controller.TogglePause();
            controller.SwitchRelative(display, -1);
            PumpFor(50);
            controller.TogglePause();
            PumpFor(50);
            Check(names.VisibleForms.Count == 0,
                "paused boundary input remains silent and is not replayed on resume", results);
            names.Suspended = true;
            controller.SwitchRelative(display, 1);
            PumpFor(50);
            Check(names.VisibleForms.Count == 0, "an open overview suspends blocked-edge notifications", results);
            names.Suspended = false;
            PumpFor(70);
            Check(names.VisibleForms.Count == 1 && names.VisibleForms[0].BoundaryText == UiText.Get("Toast.OnlyDesktop"),
                "closing the overview shows the queued boundary notification on its own display", results);
            names.Suspended = true;
            controller.SwitchRelative(display, -1);
            names.Mode = DesktopNameDisplayMode.Off;
            names.Suspended = false;
            PumpFor(50);
            Check(names.VisibleForms.Count == 0,
                "turning names off cancels a queued boundary notification", results);
        }

        using var always = new DesktopNameToastCoordinator(controller, dispatcher, new(DesktopNameDisplayMode.Always));
        PumpFor(70);
        var form = always.VisibleForms.Single(item => item.DisplayId == display);
        Check(form.BoundaryText == UiText.Get("Toast.OnlyDesktop"),
            "always mode identifies the sole desktop before any navigation", results);
        var first = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        controller.AddDesktop(display);
        PumpFor(60);
        var second = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        Check(form.BoundaryText == UiText.Get("Toast.LastDesktop"),
            "adding and switching to a second desktop updates the persistent last-desktop label", results);
        controller.AddDesktop(display);
        var third = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        controller.Switch(display, second);
        PumpFor(60);
        Check(form.BoundaryText.Length == 0,
            "the persistent boundary label clears when the active desktop becomes interior", results);
        controller.RemoveDesktop(display, third);
        PumpFor(60);
        Check(form.BoundaryText == UiText.Get("Toast.LastDesktop"),
            "removing an inactive desktop updates the boundary without switching the active desktop", results);
        controller.Switch(display, first);
        PumpFor(60);
        Check(form.BoundaryText == UiText.Get("Toast.FirstDesktop"),
            "switching to the first desktop updates its persistent boundary label", results);
        controller.RemoveDesktop(display, second);
        PumpFor(60);
        Check(form.BoundaryText == UiText.Get("Toast.OnlyDesktop")
            && always.VisibleForms.Contains(form),
            "removing the remaining inactive desktop updates the existing label to the single-desktop state", results);
    }

    private static void CheckBoundaryLabels(string output, List<string> results)
    {
        var screen = Screen.PrimaryScreen!;
        long foreground = NativeWindows.ForegroundWindow;
        using var form = new DesktopNameToastForm();
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            foreach (var sample in new[]
            {
                (Position: 1, Count: 3, Key: "Toast.FirstDesktop", File: "first"),
                (Position: 3, Count: 3, Key: "Toast.LastDesktop", File: "last"),
                (Position: 1, Count: 1, Key: "Toast.OnlyDesktop", File: "only")
            })
            {
                string desktop = UiText.Get("DesktopDefault", sample.Position);
                form.ShowName(screen, UiText.Get("DisplayDefault", 1), desktop,
                    sample.Position, sample.Count, DesktopToastCorner.TopRight);
                string expected = UiText.Get(sample.Key);
                string accessible = $"{form.AccessibleName} {form.AccessibleDescription}";
                Check(UiText.HasTranslation(language.Code, sample.Key) && expected != sample.Key
                    && form.BoundaryText == expected && accessible.Contains(expected, StringComparison.Ordinal)
                    && accessible.Contains(desktop, StringComparison.Ordinal),
                    $"desktop boundary is localized and accessible: {language.Code}, {sample.File}", results);
                if (language.Code is "ko" or "en")
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, $"desktop-boundary-{language.Code}-{sample.File}.png"), ImageFormat.Png);
                }
            }
            form.ShowName(screen, UiText.Get("DisplayDefault", 1), UiText.Get("DesktopDefault", 2),
                2, 3, DesktopToastCorner.TopRight);
            string interiorAccessible = $"{form.AccessibleName} {form.AccessibleDescription}";
            Check(form.BoundaryText.Length == 0
                && !interiorAccessible.Contains(UiText.Get("Toast.FirstDesktop"), StringComparison.Ordinal)
                && !interiorAccessible.Contains(UiText.Get("Toast.LastDesktop"), StringComparison.Ordinal)
                && !interiorAccessible.Contains(UiText.Get("Toast.OnlyDesktop"), StringComparison.Ordinal),
                $"interior desktops clear boundary text and accessible boundary hints: {language.Code}", results);
        }
        Check(NativeWindows.ForegroundWindow == foreground && !form.ShowInTaskbar
            && screen.WorkingArea.Contains(form.Bounds),
            "boundary labels remain passive, absent from the taskbar and within their display", results);
    }

    private static void ResetToSingleDesktop(DesktopController controller)
    {
        foreach (var display in controller.Catalog.Snapshot())
            foreach (var desktop in display.Desktops.Skip(1))
                controller.RemoveDesktop(display.Id, desktop.Id);
    }

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            Thread.Sleep(10);
        } while (watch.ElapsedMilliseconds < milliseconds);
    }

    private static void Check(bool condition, string description, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(description);
        results.Add("PASS " + description);
    }
}
