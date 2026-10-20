using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Shortcuts;

namespace BetterVirtualDesktop.App;

/// <summary>Uses isolated preferences and an empty window catalog; never hides or activates user windows.</summary>
internal static class DesktopNameSelfTests
{
    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "desktop-name-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string previousLanguage = UiText.SelectedLanguageCode;
        string token = Guid.NewGuid().ToString("N");
        string settingsPath = Path.Combine(output, $"desktop-name-settings-{token}.json");
        var journal = new RecoveryJournal(Path.Combine(output, $"desktop-name-journal-{token}.json"));
        try
        {
            CheckPreferences(settingsPath, results);
            UiText.SetLanguage("en", persist: false);
            var controller = new DesktopController(journal, include: _ => false);
            Check(controller.Windows.Count == 0, "desktop name tests exclude every external window", results);
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            CheckModes(controller, dispatcher, results);
            CheckMenu(controller, dispatcher, output, results);
            DesktopNavigationSelfTests.Run(output, results);
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
        var defaults = new DesktopNameSettings();
        Check(DesktopNamePreferences.Load(path) == defaults
            && defaults.Mode == DesktopNameDisplayMode.OnSwitch && defaults.Corner == DesktopToastCorner.TopRight,
            "missing desktop name preferences preserve the existing switch-only, top-right defaults", results);
        File.WriteAllText(path, """{"language":"ja","unrelated":{"keep":17}}""");
        ShortcutPreferences.Save(ShortcutProfile.WindowsStyle, path);
        foreach (var mode in Enum.GetValues<DesktopNameDisplayMode>())
            foreach (var corner in Enum.GetValues<DesktopToastCorner>())
            {
                var expected = new DesktopNameSettings(mode, corner);
                DesktopNamePreferences.Save(expected, path);
                Check(DesktopNamePreferences.Load(path) == expected,
                    $"desktop name preferences survive reload: {mode}, {corner}", results);
            }
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Check(saved["language"]!.GetValue<string>() == "ja" && saved["unrelated"]!["keep"]!.GetValue<int>() == 17
            && ShortcutPreferences.Load(path)[DesktopCommand.ToggleOverview]
                == ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview],
            "saving desktop name options preserves language, shortcuts and unrelated preferences", results);
        var persisted = DesktopNamePreferences.Load(path);
        UiText.WriteLanguagePreference(path, "zh-Hant");
        ShortcutPreferences.Save(ShortcutProfile.Defaults, path);
        Check(DesktopNamePreferences.Load(path) == persisted && UiText.ReadLanguagePreference(path) == "zh-Hant",
            "saving language and shortcuts preserves desktop name mode and position", results);
        foreach (string malformed in new[] { "{broken", "[]", "null", """{"unrelated":true}""" })
        {
            File.WriteAllText(path, malformed);
            Check(DesktopNamePreferences.Load(path) == defaults,
                $"invalid or absent desktop name configuration recovers defaults: {malformed}", results);
        }
        DesktopNamePreferences.Save(new(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft), path);
        Check(DesktopNamePreferences.Load(path) == new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft),
            "desktop name preferences can be saved after malformed configuration", results);
        File.WriteAllText(path, """{"desktopName":{"mode":"unknown","corner":"TopLeft"}}""");
        Check(DesktopNamePreferences.Load(path) == new DesktopNameSettings(DesktopNameDisplayMode.OnSwitch, DesktopToastCorner.TopLeft),
            "an unknown name display mode defaults without discarding a valid corner", results);
        File.WriteAllText(path, """{"desktopName":{"mode":"Always","corner":900}}""");
        Check(DesktopNamePreferences.Load(path) == new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopRight),
            "an invalid name display corner defaults without discarding a valid mode", results);
        File.WriteAllText(path, """{"desktopName":{"mode":"Always"}}""");
        Check(DesktopNamePreferences.Load(path) == new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopRight),
            "missing position defaults independently from an existing name display mode", results);
    }

    private static void CheckModes(DesktopController controller, Control dispatcher, List<string> results)
    {
        string display = Screen.PrimaryScreen!.DeviceName;
        using (var startup = new DesktopNameToastCoordinator(controller, dispatcher, new(DesktopNameDisplayMode.Always)))
        {
            PumpFor(60);
            Check(startup.VisibleForms.Count == Screen.AllScreens.Length,
                "loading always mode creates labels on startup without a desktop switch", results);
        }
        using var names = new DesktopNameToastCoordinator(controller, dispatcher);
        Check(names.VisibleForms.Count == 0, "switch-only names remain hidden before any desktop switch", results);
        controller.AddDesktop(display);
        names.Mode = DesktopNameDisplayMode.Off;
        PumpFor(100);
        Check(names.VisibleForms.Count == 0,
            "turning name display off cancels a previously queued desktop switch notification", results);
        controller.AddDesktop(display);
        PumpFor(80);
        Check(names.VisibleForms.Count == 0, "off mode stays hidden through subsequent desktop changes", results);

        long foreground = NativeWindows.ForegroundWindow;
        names.Mode = DesktopNameDisplayMode.Always;
        PumpFor(80);
        var visible = names.VisibleForms.ToArray();
        Check(visible.Length == Screen.AllScreens.Length && visible.All(form => !form.ShowInTaskbar)
            && NativeWindows.ForegroundWindow == foreground,
            "always mode immediately shows one passive label per monitor without taking focus", results);
        Check(visible.All(form => Screen.AllScreens.Single(screen => screen.DeviceName == form.DisplayId).WorkingArea.Contains(form.Bounds)),
            "persistent desktop names remain within each display's work area", results);
        PumpFor((int)DesktopNameToastCoordinator.VisibleDuration.TotalMilliseconds + 250);
        controller.Refresh();
        PumpFor(80);
        Check(names.VisibleForms.Count == visible.Length && visible.All(form => names.VisibleForms.Contains(form)),
            "always mode survives the notification lifetime and refresh without replacing its forms", results);
        names.Suspended = true;
        PumpFor(60);
        Check(names.VisibleForms.Count == 0, "showing app overview UI suspends persistent labels", results);
        names.Suspended = false;
        PumpFor(60);
        Check(names.VisibleForms.Count == Screen.AllScreens.Length,
            "closing app overview UI restores persistent labels without another desktop switch", results);

        names.Corner = DesktopToastCorner.TopLeft;
        int left = names.VisibleForms.Single(form => form.DisplayId == display).Left;
        names.Corner = DesktopToastCorner.TopRight;
        Check(names.VisibleForms.Single(form => form.DisplayId == display).Left > left,
            "changing persistent name position immediately repositions the visible label", results);
        var active = controller.Catalog.Snapshot().Single(item => item.Id == display).ActiveDesktopId;
        controller.RenameDesktop(display, active, "개발 작업 · Desktop name fixture");
        PumpFor(60);
        Check(names.VisibleForms.Single(form => form.DisplayId == display).DesktopName == "개발 작업 · Desktop name fixture",
            "renaming an active desktop updates its persistent name", results);
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            PumpFor(30);
            Check(names.VisibleForms.All(form =>
            {
                var snapshot = controller.Catalog.Snapshot().Single(item => item.Id == form.DisplayId);
                return form.DesktopName == UiText.DesktopName(snapshot.Desktops.Single(item => item.Id == snapshot.ActiveDesktopId));
            }), $"persistent desktop names follow language changes and preserve custom names: {language.Code}", results);
        }
        controller.TogglePause();
        PumpFor(60);
        Check(names.VisibleForms.Count == 0, "pausing desktop management hides persistent names", results);
        controller.AddDesktop(display);
        PumpFor(60);
        Check(names.VisibleForms.Count == 0, "desktop changes while paused do not reveal name labels", results);
        controller.TogglePause();
        PumpFor(80);
        Check(names.VisibleForms.Count == Screen.AllScreens.Length,
            "resuming management restores persistent names without requiring another switch", results);

        names.Mode = DesktopNameDisplayMode.OnSwitch;
        PumpFor(60);
        Check(names.VisibleForms.Count == 0, "returning to switch-only mode clears persistent labels", results);
        names.Suspended = true;
        names.Suspended = false;
        PumpFor(60);
        Check(names.VisibleForms.Count == 0, "closing an overview without a switch does not create a switch-only notification", results);
        names.Suspended = true;
        controller.AddDesktop(display);
        PumpFor(80);
        Check(names.VisibleForms.Count == 0, "a desktop switch during overview stays queued while labels are suspended", results);
        names.Suspended = false;
        PumpFor(80);
        Check(names.VisibleForms.Count == 1 && names.VisibleForms.Single().DisplayId == display,
            "closing overview shows the pending notification only on the switched display", results);
        PumpFor((int)DesktopNameToastCoordinator.VisibleDuration.TotalMilliseconds + 250);
        Check(names.VisibleForms.Count == 0, "switch-only notifications still expire", results);
        names.Mode = DesktopNameDisplayMode.Always;
        PumpFor(60);
        names.Mode = DesktopNameDisplayMode.Off;
        Check(names.VisibleForms.Count == 0, "turning always mode off immediately removes every name label", results);

        var disposed = new DesktopNameToastCoordinator(controller, dispatcher);
        controller.AddDesktop(display);
        disposed.Dispose();
        PumpFor(80);
        Check(disposed.VisibleForms.Count == 0, "disposing the coordinator safely cancels its queued notification", results);
        UiText.SetLanguage("en", persist: false);
    }

    private static void CheckMenu(DesktopController controller, Control dispatcher, string output, List<string> results)
    {
        using var names = new DesktopNameToastCoordinator(controller, dispatcher, new(DesktopNameDisplayMode.Off));
        var errors = new List<Exception>();
        var saves = new List<DesktopNameSettings>();
        bool failSave = true;
        using var menu = new DesktopNameMenuItem(names, settings =>
        {
            if (failSave) throw new IOException("Simulated desktop-name settings write failure.");
            saves.Add(settings);
        }, errors.Add);
        ToolStripMenuItem Item(string key) => menu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == UiText.Get(key));
        var off = Item("Tray.NameOff");
        var onSwitch = Item("Tray.NameOnSwitch");
        var always = Item("Tray.NameAlways");
        always.PerformClick();
        PumpFor(50);
        Check(names.Mode == DesktopNameDisplayMode.Off && off.Checked && !always.Checked
            && names.VisibleForms.Count == 0 && saves.Count == 0 && errors.Count == 1,
            "a failed menu save reports the error and preserves the prior mode, checkmarks and visible state", results);
        failSave = false;
        always.PerformClick();
        PumpFor(60);
        Check(names.Mode == DesktopNameDisplayMode.Always && always.Checked && !off.Checked && !onSwitch.Checked
            && saves.Count == 1 && saves[0].Mode == DesktopNameDisplayMode.Always,
            "selecting always in the menu saves and applies an exclusive mode", results);
        var position = Item("Tray.NamePosition");
        position.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == UiText.Get("Tray.TopLeft")).PerformClick();
        Check(names.Corner == DesktopToastCorner.TopLeft && saves.Count == 2
            && saves[1] == new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft),
            "the position submenu saves both the selected position and current display mode", results);
        onSwitch.PerformClick();
        off.PerformClick();
        Check(names.Mode == DesktopNameDisplayMode.Off && off.Checked && !onSwitch.Checked && !always.Checked,
            "all desktop name menu modes can be selected and remain mutually exclusive", results);

        // Rendering an unopened dropdown never takes focus or sends native pointer/keyboard input.
        var dropdown = menu.DropDown;
        dropdown.Size = dropdown.GetPreferredSize(Size.Empty);
        dropdown.PerformLayout();
        using var bitmap = new Bitmap(Math.Max(1, dropdown.Width), Math.Max(1, dropdown.Height));
        dropdown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(output, "desktop-name-options.png"), ImageFormat.Png);
        Check(!dropdown.Visible, "desktop name options preview renders without opening the live tray menu", results);
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
