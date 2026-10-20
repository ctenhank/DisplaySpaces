using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Shortcuts;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>Uses isolated settings, empty catalogs, and only a dedicated fixture subprocess's windows.</summary>
internal static class DisplaySwitchingSelfTests
{
    private sealed record FixtureInfo(int ProcessId, long[] Handles);

    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "display-switching-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string previousLanguage = UiText.SelectedLanguageCode;
        string token = Guid.NewGuid().ToString("N");
        string settingsPath = Path.Combine(output, $"display-switching-settings-{token}.json");
        var journal = new RecoveryJournal(Path.Combine(output, $"display-switching-empty-{token}.json"));
        try
        {
            CheckPreferences(settingsPath, results);
            UiText.SetLanguage("en", persist: false);
            var controller = new DesktopController(journal, include: _ => false);
            Check(controller.Windows.Count == 0, "switching settings tests exclude all external windows", results);
            CheckController(controller, results);
            CheckMenu(controller, results);
            CheckBoundaryLabels(controller, results);
            CheckPhysicalDisplays(output, results);
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
        Check(DisplaySwitchingPreferences.Load(path) == DisplaySwitchingMode.Independent,
            "missing switching preferences default to independent displays", results);
        File.WriteAllText(path, """{"language":"ja","unrelated":{"keep":17}}""");
        ShortcutPreferences.Save(ShortcutProfile.WindowsStyle, path);
        DesktopNavigationPreferences.Save(false, path);
        var names = new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft);
        DesktopNamePreferences.Save(names, path);
        foreach (var mode in Enum.GetValues<DisplaySwitchingMode>())
        {
            DisplaySwitchingPreferences.Save(mode, path);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(DisplaySwitchingPreferences.Load(path) == mode
                && saved["displaySwitchingMode"]!.GetValue<string>() == mode.ToString()
                && saved["language"]!.GetValue<string>() == "ja"
                && saved["unrelated"]!["keep"]!.GetValue<int>() == 17
                && !DesktopNavigationPreferences.Load(path)
                && DesktopNamePreferences.Load(path) == names
                && ShortcutPreferences.Load(path)[DesktopCommand.ToggleOverview]
                    == ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview],
                $"switching preference survives reload without replacing other settings: {mode}", results);
        }
        DisplaySwitchingPreferences.Save(DisplaySwitchingMode.Linked, path);
        UiText.WriteLanguagePreference(path, "zh-Hant");
        ShortcutPreferences.Save(ShortcutProfile.Defaults, path);
        DesktopNavigationPreferences.Save(true, path);
        DesktopNamePreferences.Save(new(DesktopNameDisplayMode.Off), path);
        Check(DisplaySwitchingPreferences.Load(path) == DisplaySwitchingMode.Linked
            && UiText.ReadLanguagePreference(path) == "zh-Hant",
            "language, shortcut, wrap and name saves preserve the linked switching preference", results);
        foreach (string malformed in new[]
        {
            "{broken", "[]", "null", """{"unrelated":true}""",
            """{"displaySwitchingMode":"unknown"}""", """{"displaySwitchingMode":"1"}""",
            """{"displaySwitchingMode":"linked"}""", """{"displaySwitchingMode":1}""",
            """{"displaySwitchingMode":null}""", """{"displaySwitchingMode":true}"""
        })
        {
            File.WriteAllText(path, malformed);
            Check(DisplaySwitchingPreferences.Load(path) == DisplaySwitchingMode.Independent,
                $"invalid switching settings recover the independent default: {malformed}", results);
        }
        DisplaySwitchingPreferences.Save(DisplaySwitchingMode.Linked, path);
        Check(DisplaySwitchingPreferences.Load(path) == DisplaySwitchingMode.Linked,
            "switching preferences can be saved after malformed configuration", results);
        string before = File.ReadAllText(path);
        bool rejected = false;
        try { DisplaySwitchingPreferences.Save((DisplaySwitchingMode)999, path); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && File.ReadAllText(path) == before,
            "invalid switching modes cannot overwrite a valid settings file", results);
    }

    private static void CheckController(DesktopController controller, List<string> results)
    {
        const string primary = "switching-test-primary";
        const string secondary = "switching-test-secondary";
        controller.Catalog.SynchronizeDisplays([
            new DisplayDescriptor(primary, "Primary fixture", true),
            new DisplayDescriptor(secondary, "Secondary fixture")
        ]);
        var first = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
        Guid second = controller.Catalog.AddDesktop(primary);
        Check(controller.SwitchingMode == DisplaySwitchingMode.Independent,
            "new controllers default to independent switching", results);
        controller.Switch(primary, second);
        Check(Active(primary) == second && Active(secondary) == first[secondary]
            && Display(secondary).Desktops.Count == 1,
            "default direct switching changes only the selected display", results);
        long generation = controller.VisibilityGeneration;
        int changes = 0;
        controller.Changed += OnChanged;
        try
        {
            string before = State(controller);
            controller.SwitchingMode = DisplaySwitchingMode.Linked;
            Check(State(controller) == before && changes == 0 && controller.VisibilityGeneration == generation,
                "changing the switching preference does not immediately switch or create desktops", results);
            controller.Switch(primary, second);
            Check(Display(secondary).Desktops.Count == 2 && AtIndex(1) && changes == 1
                && controller.VisibilityGeneration == generation + 1,
                "linked selection of the current source desktop synchronizes another display and publishes one change", results);
            controller.Switch(primary, first[primary]);
            Check(AtIndex(0), "linked direct switching selects the same ordinal on every display", results);
            Guid third = controller.Catalog.AddDesktop(secondary);
            controller.SwitchRelative(primary, 1);
            Check(AtIndex(1), "linked relative switching advances both display lists", results);
            controller.SwitchRelative(primary, 1);
            Check(AtIndex(2) && Display(primary).Desktops.Count == 3 && Active(secondary) == third,
                "linked relative switching reaches the longest display list by adding a missing empty desktop", results);
            controller.SwitchRelative(primary, 1);
            Check(AtIndex(0), "linked wrapping returns every display from the last desktop to the first", results);
            controller.WrapDesktopSwitching = false;
            controller.Catalog.SwitchToDesktop(secondary, third);
            before = State(controller);
            generation = controller.VisibilityGeneration;
            int beforeChanges = changes;
            var boundaries = new List<string>();
            controller.SwitchBoundaryReached += OnBoundary;
            try
            {
                controller.SwitchRelative(primary, -1);
                Check(State(controller) == before && changes == beforeChanges
                    && controller.VisibilityGeneration == generation && boundaries.SequenceEqual(new[] { primary }),
                    "bounded linked navigation at the source's first desktop does not synchronize other displays", results);
                controller.SwitchRelative(primary, 0);
                Check(State(controller) == before && boundaries.Count == 1 && changes == beforeChanges,
                    "zero linked navigation neither synchronizes displays nor reports a boundary", results);
            }
            finally { controller.SwitchBoundaryReached -= OnBoundary; }
            controller.Switch(primary, Display(primary).Desktops[2].Id);
            before = State(controller);
            generation = controller.VisibilityGeneration;
            controller.SwitchRelative(primary, 1);
            Check(State(controller) == before && controller.VisibilityGeneration == generation,
                "bounded linked navigation stops at the last desktop on every display", results);
            controller.SwitchingMode = DisplaySwitchingMode.Independent;
            controller.SwitchRelative(primary, -1);
            Check(Active(primary) == second && Active(secondary) == third,
                "returning to independent mode restores single-display relative switching", results);
            controller.AddDesktop(primary);
            Check(Display(primary).Desktops.Count == 4 && Display(secondary).Desktops.Count == 3
                && Active(secondary) == third,
                "independent desktop creation leaves the other display's list and selection unchanged", results);
            controller.SwitchingMode = DisplaySwitchingMode.Linked;
            controller.AddDesktop(primary);
            Check(AtIndex(4) && controller.Catalog.Snapshot().All(d => d.Desktops.Count == 5),
                "linked create-and-switch selects the new ordinal and creates missing empty desktops", results);
            controller.RemoveDesktop(primary, Active(primary));
            Check(AtIndex(3) && Display(primary).Desktops.Count == 4 && Display(secondary).Desktops.Count == 5,
                "removing a current desktop in linked mode follows its replacement without deleting other display desktops", results);
            before = State(controller);
            bool rejected = false;
            try { controller.SwitchingMode = (DisplaySwitchingMode)999; }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && controller.SwitchingMode == DisplaySwitchingMode.Linked && State(controller) == before,
                "invalid controller switching modes preserve the current mode and workspace", results);

            void OnBoundary(string display) => boundaries.Add(display);
        }
        finally { controller.Changed -= OnChanged; }

        void OnChanged() => changes++;
        DisplaySnapshot Display(string id) => controller.Catalog.Snapshot().Single(d => d.Id == id);
        Guid Active(string id) => Display(id).ActiveDesktopId;
        bool AtIndex(int index) => controller.Catalog.Snapshot().All(d =>
            d.Desktops.Count > index && d.ActiveDesktopId == d.Desktops[index].Id);
    }

    private static void CheckMenu(DesktopController controller, List<string> results)
    {
        controller.SwitchingMode = DisplaySwitchingMode.Independent;
        bool failSave = true;
        var saves = new List<DisplaySwitchingMode>();
        var errors = new List<Exception>();
        using var menu = new DisplaySwitchingMenuItem(controller, mode =>
        {
            if (failSave) throw new IOException("Simulated switching settings write failure.");
            saves.Add(mode);
        }, errors.Add);
        var independent = menu.DropDownItems.OfType<ToolStripMenuItem>()
            .Single(item => item.Name == "Tray.SwitchingIndependent");
        var linked = menu.DropDownItems.OfType<ToolStripMenuItem>()
            .Single(item => item.Name == "Tray.SwitchingLinked");
        string before = State(controller);
        Check(independent.Checked && !linked.Checked,
            "switching menu initially reflects the independent controller mode", results);
        linked.PerformClick();
        Check(independent.Checked && !linked.Checked && controller.SwitchingMode == DisplaySwitchingMode.Independent
            && errors.Count == 1 && saves.Count == 0 && State(controller) == before,
            "failed switching settings saves preserve the runtime mode, checkmarks and desktop selection", results);
        failSave = false;
        linked.PerformClick();
        Check(!independent.Checked && linked.Checked && controller.SwitchingMode == DisplaySwitchingMode.Linked
            && saves.SequenceEqual(new[] { DisplaySwitchingMode.Linked }) && State(controller) == before,
            "choosing linked switching saves and applies the preference without switching immediately", results);
        independent.PerformClick();
        Check(independent.Checked && !linked.Checked && controller.SwitchingMode == DisplaySwitchingMode.Independent
            && saves.SequenceEqual(new[] { DisplaySwitchingMode.Linked, DisplaySwitchingMode.Independent })
            && State(controller) == before,
            "choosing independent switching restores its checkmark and persists the preference", results);
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            using var localized = new DisplaySwitchingMenuItem(controller, _ => { }, _ => { });
            Check(new[] { "Tray.SwitchingIndependent", "Tray.SwitchingLinked" }.All(key =>
                UiText.HasTranslation(language.Code, key)
                && localized.DropDownItems.OfType<ToolStripMenuItem>()
                    .Any(item => item.Name == key && item.Text == UiText.Get(key))),
                $"switching choices are translated: {language.Code}", results);
        }
        UiText.SetLanguage("en", persist: false);
    }

    private static void CheckPhysicalDisplays(string output, List<string> results)
    {
        if (Screen.AllScreens.Length < 2)
        {
            results.Add("SKIP native independent/linked window visibility regression requires two physical displays");
            return;
        }
        using var fixture = new FixtureSession(output);
        var controller = fixture.Controller;
        var assignments = fixture.Handles.ToDictionary(h => h, h => controller.Catalog.GetAssignment(h)!);
        string primary = assignments[fixture.Handles[0]].DisplayId;
        long[] primaryHandles = fixture.Handles.Where(h => assignments[h].DisplayId == primary).ToArray();
        long[] otherHandles = fixture.Handles.Where(h => assignments[h].DisplayId != primary).ToArray();
        Check(primaryHandles.Length >= 3 && otherHandles.Length >= 1
            && controller.Windows.Values.All(w => w.ProcessId == fixture.ProcessId),
            "native switching regression includes only fixture owner/dialog windows and another physical display", results);
        var first = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
        var second = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => controller.Catalog.AddDesktop(d.Id));
        var otherVisibility = otherHandles.ToDictionary(h => h, NativeWindows.IsVisible);
        Check(controller.SwitchingMode == DisplaySwitchingMode.Independent && fixture.Handles.All(NativeWindows.IsVisible),
            "physical switching fixture starts visible in independent mode", results);
        controller.Switch(primary, second[primary]);
        WaitUntil(() => primaryHandles.All(h => !NativeWindows.IsVisible(h)), "independent primary fixture hide");
        Check(OtherDisplaysUnchanged() && AssignmentsUnchanged()
            && otherHandles.All(h => NativeWindows.IsVisible(h) == otherVisibility[h]),
            "direct independent switch leaves other monitor desktop IDs, assignments and native window visibility unchanged", results);
        controller.SwitchRelative(primary, -1);
        WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "independent fixture restoration");
        Check(OtherDisplaysUnchanged() && AssignmentsUnchanged(),
            "relative independent return restores its own fixture group without switching the other display", results);
        controller.SwitchRelative(primary, 1);
        WaitUntil(() => primaryHandles.All(h => !NativeWindows.IsVisible(h)), "independent relative fixture hide");
        Check(OtherDisplaysUnchanged() && otherHandles.All(NativeWindows.IsVisible),
            "relative independent next command keeps other physical display windows visible", results);

        string before = State(controller);
        controller.SwitchingMode = DisplaySwitchingMode.Linked;
        Check(State(controller) == before && otherHandles.All(NativeWindows.IsVisible),
            "enabling linked mode does not hide or switch another monitor immediately", results);
        controller.Switch(primary, second[primary]);
        WaitUntil(() => fixture.Handles.All(h => !NativeWindows.IsVisible(h)), "linked fixture hide on both displays");
        Check(controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == second[d.Id]) && AssignmentsUnchanged(),
            "linked direct selection hides both fixture groups and selects the matching ordinal on every monitor", results);
        controller.SwitchRelative(primary, -1);
        WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "linked fixture restoration on both displays");
        Check(controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == first[d.Id]) && AssignmentsUnchanged(),
            "linked relative return restores both native fixture groups without moving their assignments", results);
        controller.SwitchRelative(primary, 1);
        WaitUntil(() => fixture.Handles.All(h => !NativeWindows.IsVisible(h)), "linked relative fixture hide");
        controller.SwitchingMode = DisplaySwitchingMode.Independent;
        controller.SwitchRelative(primary, -1);
        WaitUntil(() => primaryHandles.All(NativeWindows.IsVisible), "return to independent primary restoration");
        Check(otherHandles.All(h => !NativeWindows.IsVisible(h))
            && controller.Catalog.Snapshot().Where(d => d.Id != primary).All(d => d.ActiveDesktopId == second[d.Id]),
            "switching back to independent mode leaves the other monitor on its own selected desktop", results);
        controller.RestoreAllAndPause();
        WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "all fixture windows restored after switching tests");
        Check(fixture.Handles.All(NativeWindows.IsVisible),
            "native switching regression restores every fixture window before cleanup", results);

        bool OtherDisplaysUnchanged() => controller.Catalog.Snapshot()
            .Where(d => d.Id != primary).All(d => d.ActiveDesktopId == first[d.Id]);
        bool AssignmentsUnchanged() => assignments.All(pair => controller.Catalog.GetAssignment(pair.Key) == pair.Value);
    }

    private static void CheckBoundaryLabels(DesktopController controller, List<string> results)
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2)
        {
            results.Add("SKIP unequal-display desktop-name boundary rendering requires two physical displays");
            return;
        }
        controller.Catalog.SynchronizeDisplays(screens.Select((screen, index) =>
            new DisplayDescriptor(screen.DeviceName, $"Display {index + 1}", screen.Primary)));
        string primary = screens.First(s => s.Primary).DeviceName;
        string secondary = screens.First(s => !s.Primary).DeviceName;
        Guid current = controller.Catalog.AddDesktop(primary);
        controller.Catalog.AddDesktop(secondary);
        controller.Catalog.AddDesktop(secondary);
        controller.Catalog.SwitchToDesktop(primary, current);
        controller.SwitchingMode = DisplaySwitchingMode.Linked;
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        using var names = new DesktopNameToastCoordinator(controller, dispatcher, new(DesktopNameDisplayMode.Always));
        PumpFor(70);
        var form = names.VisibleForms.Single(item => item.DisplayId == primary);
        var display = controller.Catalog.Snapshot().Single(item => item.Id == primary);
        Check(form.BoundaryText.Length == 0 && form.AccessibleName ==
            UiText.Get("Toast.Accessible", form.DesktopName, UiText.DisplayName(display), 3, 2),
            "linked desktop names use the longest display list and do not label local desktop 2 of global 3 as last", results);
        controller.SwitchingMode = DisplaySwitchingMode.Independent;
        controller.Refresh(); // The empty include filter still excludes every external window.
        PumpFor(70);
        Check(form.BoundaryText == UiText.Get("Toast.LastDesktop"),
            "switching back to independent mode restores the local last-desktop name hint", results);
    }

    private sealed class FixtureSession : IDisposable
    {
        private readonly string _manifest;
        private Process? _process;
        private RecoveryJournal? _journal;
        private DesktopController? _controller;
        private FixtureInfo? _info;
        public DesktopController Controller => _controller!;
        public int ProcessId => _info!.ProcessId;
        public long[] Handles => _info!.Handles;

        public FixtureSession(string output)
        {
            string token = Guid.NewGuid().ToString("N");
            _manifest = Path.Combine(output, $"display-switching-fixture-{token}.json");
            try
            {
                _process = ProcessLauncher.Start("--fixture", _manifest);
                WaitUntil(() => File.Exists(_manifest) && new FileInfo(_manifest).Length > 0, "switching fixture startup");
                _info = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(_manifest))
                    ?? throw new InvalidOperationException("Switching fixture manifest is empty.");
                if (_info.ProcessId != _process.Id || Handles.Length < 4
                    || Handles.Any(h => h == 0) || Handles.Distinct().Count() != Handles.Length)
                    throw new InvalidOperationException("Switching fixture process/manifest identity mismatch.");
                WaitUntil(() => NativeWindows.EnumerateWindows().Count(w =>
                    w.ProcessId == ProcessId && Handles.Contains(w.Handle)) == Handles.Length, "switching fixture enumeration");
                var windows = NativeWindows.EnumerateWindows().Where(w =>
                    w.ProcessId == ProcessId && Handles.Contains(w.Handle)).ToDictionary(w => w.Handle);
                long started = _process.StartTime.ToUniversalTime().Ticks;
                // The helper's hidden startup flag can hide its first form. Reveal only a
                // manifest-listed, identity-validated fixture window, without activating it.
                foreach (long handle in Handles)
                {
                    var window = windows[handle];
                    if (window.ProcessStartTicks != started
                        || !NativeWindows.IsSameWindow(handle, ProcessId, started, window.ClassName))
                        throw new InvalidOperationException("Refusing to operate on a non-fixture window.");
                    if (!NativeWindows.IsVisible(handle))
                        NativeWindows.Show(handle, window.IsMinimized, window.IsMaximized);
                }
                WaitUntil(() => Handles.All(NativeWindows.IsVisible), "switching fixture initial visibility");
                _journal = new RecoveryJournal(Path.Combine(output, $"display-switching-recovery-{token}.json"));
                _controller = new DesktopController(_journal, window =>
                    window.ProcessId == ProcessId && Handles.Contains(window.Handle));
                if (!Handles.All(Controller.Windows.ContainsKey))
                    throw new InvalidOperationException("Switching fixture did not discover all windows on the current native desktop.");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                _controller?.RestoreAllAndPause();
                if (_journal is not null)
                {
                    var deadline = Stopwatch.StartNew();
                    while (_journal.Entries.Count > 0 && deadline.ElapsedMilliseconds < 1500)
                    {
                        _journal.RestoreAll();
                        PumpFor(40);
                        foreach (var entry in _journal.Entries.ToArray()) _journal.ConfirmRestored(entry.Handle);
                    }
                }
            }
            finally
            {
                try
                {
                    if (_process is not null)
                    {
                        try
                        {
                            if (!_process.HasExited)
                            {
                                _process.Kill();
                                _process.WaitForExit(3000);
                            }
                        }
                        finally { _process.Dispose(); _process = null; }
                    }
                }
                finally
                {
                    _journal?.PruneClosed();
                    _journal?.DeleteIfEmpty();
                    foreach (string path in new[] { _manifest, _manifest + ".tmp" })
                        if (File.Exists(path)) File.Delete(path);
                }
            }
        }
    }

    private static string State(DesktopController controller) => string.Join("|",
        controller.Catalog.Snapshot().Select(d => $"{d.Id}:{d.ActiveDesktopId}:{string.Join(',', d.Desktops.Select(s => s.Id))}"));

    private static void Check(bool condition, string description, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(description);
        results.Add("PASS " + description);
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException(description);
            PumpFor(15);
        }
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
}
