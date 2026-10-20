using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Services;
using DisplaySpaces.App.Shortcuts;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

/// <summary>Isolated settings and dedicated fixture windows only; no input, activation, or global hooks.</summary>
internal static class WindowMoveFollowSelfTests
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
        File.WriteAllLines(Path.Combine(output, "window-move-follow-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string language = UiText.SelectedLanguageCode;
        string token = Guid.NewGuid().ToString("N");
        string settings = Path.Combine(output, $"window-move-follow-settings-{token}.json");
        var journal = new RecoveryJournal(Path.Combine(output, $"window-move-follow-empty-{token}.json"));
        try
        {
            CheckPreferences(settings, results);
            UiText.SetLanguage("en", persist: false);
            var empty = new DesktopController(journal, include: _ => false);
            Check(empty.Windows.Count == 0, "follow-option menu tests exclude every external window", results);
            CheckMenu(empty, results);
            CheckNativeMoves(output, results);
        }
        finally
        {
            UiText.SetLanguage(language, persist: false);
            journal.DeleteIfEmpty();
            if (File.Exists(settings)) File.Delete(settings);
        }
    }

    private static void CheckPreferences(string path, List<string> results)
    {
        Check(!WindowMovePreferences.Load(path), "missing move-follow settings default to staying on the current desktop", results);
        File.WriteAllText(path, """{"language":"ja","unrelated":{"keep":17}}""");
        ShortcutPreferences.Save(ShortcutProfile.WindowsStyle, path);
        DesktopNavigationPreferences.Save(false, path);
        DisplaySwitchingPreferences.Save(DisplaySwitchingMode.Linked, path);
        var names = new DesktopNameSettings(DesktopNameDisplayMode.Always, DesktopToastCorner.TopLeft);
        DesktopNamePreferences.Save(names, path);
        foreach (bool follow in new[] { true, false })
        {
            WindowMovePreferences.Save(follow, path);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(WindowMovePreferences.Load(path) == follow
                && saved["followMovedWindow"]!.GetValue<bool>() == follow
                && saved["language"]!.GetValue<string>() == "ja"
                && saved["unrelated"]!["keep"]!.GetValue<int>() == 17
                && !DesktopNavigationPreferences.Load(path)
                && DisplaySwitchingPreferences.Load(path) == DisplaySwitchingMode.Linked
                && DesktopNamePreferences.Load(path) == names
                && ShortcutPreferences.Load(path)[DesktopCommand.ToggleOverview]
                    == ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview],
                $"move-follow value reloads without replacing other settings: {follow}", results);
        }
        WindowMovePreferences.Save(true, path);
        UiText.WriteLanguagePreference(path, "zh-Hant");
        ShortcutPreferences.Save(ShortcutProfile.Defaults, path);
        DesktopNavigationPreferences.Save(true, path);
        DisplaySwitchingPreferences.Save(DisplaySwitchingMode.Independent, path);
        DesktopNamePreferences.Save(new(DesktopNameDisplayMode.Off), path);
        Check(WindowMovePreferences.Load(path) && UiText.ReadLanguagePreference(path) == "zh-Hant",
            "other settings writers preserve the enabled move-follow option", results);
        foreach (string malformed in new[]
        {
            "{broken", "[]", "null", """{"unrelated":true}""",
            """{"followMovedWindow":"true"}""", """{"followMovedWindow":1}""",
            """{"followMovedWindow":null}""", """{"followMovedWindow":{}}"""
        })
        {
            File.WriteAllText(path, malformed);
            Check(!WindowMovePreferences.Load(path), $"invalid move-follow settings fall back to off: {malformed}", results);
        }
        WindowMovePreferences.Save(true, path);
        Check(WindowMovePreferences.Load(path), "move-follow preferences can be saved after malformed configuration", results);
    }

    private static void CheckMenu(DesktopController controller, List<string> results)
    {
        bool failSave = true;
        var saves = new List<bool>();
        var errors = new List<Exception>();
        using var menu = new FollowMovedWindowMenuItem(controller, follow =>
        {
            if (failSave) throw new IOException("Simulated move-follow settings write failure.");
            saves.Add(follow);
        }, errors.Add);
        string before = State(controller);
        long generation = controller.VisibilityGeneration;
        int changes = 0;
        controller.Changed += OnChanged;
        try
        {
            Check(!controller.FollowMovedWindow && !menu.Checked && menu.Name == "Tray.FollowMovedWindow",
                "new controllers and the move-follow checkbox default to off", results);
            menu.PerformClick();
            Check(!controller.FollowMovedWindow && !menu.Checked && errors.Count == 1 && saves.Count == 0,
                "failed move-follow save preserves both runtime option and checkbox", results);
            failSave = false;
            menu.PerformClick();
            Check(controller.FollowMovedWindow && menu.Checked && saves.SequenceEqual(new[] { true }),
                "enabling move-follow saves and applies the checkbox", results);
            menu.PerformClick();
            Check(!controller.FollowMovedWindow && !menu.Checked && saves.SequenceEqual(new[] { true, false }),
                "disabling move-follow saves and restores stay-on-current behavior", results);
            Check(State(controller) == before && controller.VisibilityGeneration == generation && changes == 0,
                "changing the move-follow option does not move windows, switch desktops or publish visibility changes", results);
            foreach (var language in UiText.SupportedLanguages)
            {
                UiText.SetLanguage(language.Code, persist: false);
                using var localized = new FollowMovedWindowMenuItem(controller, _ => { }, _ => { });
                Check(UiText.HasTranslation(language.Code, "Tray.FollowMovedWindow")
                    && localized.Text == UiText.Get("Tray.FollowMovedWindow"),
                    $"move-follow checkbox is translated: {language.Code}", results);
            }
        }
        finally
        {
            controller.Changed -= OnChanged;
            UiText.SetLanguage("en", persist: false);
        }
        void OnChanged() => changes++;
    }

    private static void CheckNativeMoves(string output, List<string> results)
    {
        using var fixture = new FixtureSession(output);
        var controller = fixture.Controller;
        long root = fixture.Handles[0], dialog = fixture.Handles[2];
        long[] group = [root, dialog];
        var original = fixture.Handles.ToDictionary(h => h, h => controller.Catalog.GetAssignment(h)!);
        string source = original[root].DisplayId;
        Guid first = original[root].DesktopId;
        var firstDesktops = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
        long[] others = fixture.Handles.Where(h => !group.Contains(h)).ToArray();
        Check(controller.Windows.Values.All(w => w.ProcessId == fixture.ProcessId)
            && controller.Windows[dialog].RootOwner == root && fixture.Handles.All(NativeWindows.IsVisible),
            "native move-follow tests contain only visible dedicated fixture windows, including an owner/dialog group", results);
        string before = State(controller);
        Check(!controller.MoveWindowRelative(root, 1) && State(controller) == before,
            "a one-desktop relative move is a no-op", results);
        Guid second = controller.Catalog.AddDesktop(source);
        int changes = 0;
        var hides = new List<long>();
        controller.Changed += OnChanged;
        controller.WindowHideRequested += OnHide;
        try
        {
            before = State(controller);
            long generation = controller.VisibilityGeneration;
            Check(!controller.MoveWindowRelative(root, 0)
                && !controller.MoveWindowRelative(0, 1)
                && !controller.MoveWindowRelative(long.MaxValue, -1)
                && !controller.MoveWindowRelative(root, 2)
                && State(controller) == before && changes == 0 && controller.VisibilityGeneration == generation,
                "zero, invalid/untracked HWND and full-cycle relative moves cannot switch or mutate desktops", results);

            Check(controller.MoveWindowRelative(dialog, 1), "default relative move accepts an owned dialog", results);
            WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "default move hides fixture owner/dialog");
            Check(AllActiveFirst() && GroupAssigned(second) && OthersAssigned() && others.All(NativeWindows.IsVisible)
                && changes == 1 && controller.VisibilityGeneration == generation + 1,
                "default-off relative move moves the entire owner group, stays on all current desktops and publishes one change", results);
            ReturnToFirst();
            controller.FollowMovedWindow = true;
            BeginObservation();
            Check(controller.MoveWindowRelative(dialog, 1), "enabled relative move accepts the selected dialog", results);
            WaitUntil(() => others.Where(h => original[h].DisplayId == source).All(h => !NativeWindows.IsVisible(h)),
                "follow hides other windows left on the source desktop");
            Check(GroupAssigned(second) && Active(source) == second && OthersAssigned()
                && OtherDisplaysAtFirst() && group.All(NativeWindows.IsVisible)
                && others.Where(h => original[h].DisplayId != source).All(NativeWindows.IsVisible)
                && !hides.Any(group.Contains) && changes == 1 && controller.VisibilityGeneration == generation + 1,
                "independent follow switches only its display in one batch without ever hiding the moved owner/dialog", results);
            BeginObservation();
            Check(controller.MoveWindowRelative(root, -1), "enabled previous-desktop move is accepted", results);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "previous follow restores source windows");
            Check(AllActiveFirst() && GroupAssigned(first) && !hides.Any(group.Contains) && changes == 1,
                "following the previous desktop restores source windows without a transient moved-group hide", results);

            controller.WrapDesktopSwitching = false;
            BeginObservation();
            Check(controller.MoveWindowRelative(root, -1) && Active(source) == second && GroupAssigned(second),
                "relative window movement retains its existing wrapping behavior when desktop navigation is bounded", results);
            Check(controller.MoveWindowRelative(root, int.MaxValue) && Active(source) == first && GroupAssigned(first)
                && !controller.MoveWindowRelative(root, int.MinValue),
                "extreme relative offsets are handled without integer overflow or spurious full-cycle switching", results);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "wrapping regression restoration");
            Settle();

            controller.SwitchingMode = DisplaySwitchingMode.Linked;
            BeginObservation();
            Check(controller.MoveWindowRelative(dialog, 1), "linked move-follow accepts the owner group", results);
            WaitUntil(() => others.All(h => !NativeWindows.IsVisible(h)), "linked follow hides inactive fixture windows");
            Check(controller.Catalog.Snapshot().All(d => d.Desktops.Count >= 2 && d.ActiveDesktopId == d.Desktops[1].Id)
                && GroupAssigned(second) && OthersAssigned() && group.All(NativeWindows.IsVisible)
                && !hides.Any(group.Contains) && changes == 1 && controller.VisibilityGeneration == generation + 1,
                "linked follow switches every display to the destination ordinal while preserving unrelated window assignments", results);
            Check(controller.MoveWindowRelative(root, -1), "linked previous move returns the fixture group", results);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "linked previous restoration");
            Check(AllActiveFirst() && GroupAssigned(first), "linked previous follow returns every display to its first desktop", results);
            Settle();

            BeginObservation();
            Check(controller.MoveWindowToDisplay(dialog, source, second), "explicit destination move is accepted with follow enabled", results);
            WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "explicit move hides fixture owner/dialog");
            Check(AllActiveFirst() && GroupAssigned(second) && changes == 1,
                "explicit drag/menu destination moves stay on the current desktops even when relative follow is enabled", results);
            ReturnToFirst();
            BeginObservation();
            Guid created = controller.MoveWindowToNewDesktop(dialog, source)
                ?? throw new InvalidOperationException("New-desktop fixture move was rejected.");
            WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "new-desktop explicit move hides fixture owner/dialog");
            Check(AllActiveFirst() && GroupAssigned(created) && changes == 1,
                "explicit move to a newly created desktop does not follow or synchronize display selections", results);
            ReturnToFirst();
            controller.Catalog.RemoveDesktop(source, created);
            controller.MoveWindow(root, second);
            WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "explicit same-display move hides owner/dialog");
            Check(AllActiveFirst() && GroupAssigned(second),
                "the explicit same-display window move API also ignores the relative follow preference", results);
            ReturnToFirst();

            controller.TogglePause();
            BeginObservation();
            Check(controller.MoveWindowRelative(root, 1) && controller.Paused && GroupAssigned(second)
                && AllActiveFirst() && fixture.Handles.All(NativeWindows.IsVisible) && hides.Count == 0 && changes == 1,
                "paused relative movement updates assignments without following or hiding windows", results);
            ReturnToFirst();
            controller.TogglePause();
            fixture.CanHide = false;
            BeginObservation();
            Check(controller.MoveWindowRelative(root, 1) && controller.Paused && GroupAssigned(second)
                && AllActiveFirst() && fixture.Handles.All(NativeWindows.IsVisible) && hides.Count == 0 && changes == 1,
                "guardian-unavailable relative movement suppresses follow and keeps all fixture windows visible", results);
            fixture.CanHide = true;
            ReturnToFirst();
            controller.TogglePause();

            var otherDisplay = controller.Catalog.Snapshot().FirstOrDefault(d => d.Id != source);
            if (otherDisplay is not null)
            {
                Check(controller.MoveWindowToDisplay(dialog, otherDisplay.Id, otherDisplay.ActiveDesktopId),
                    "cross-display fixture setup accepts an explicit move without following", results);
                before = State(controller);
                var pending = group.ToDictionary(h => h, h => controller.Catalog.GetAssignment(h));
                BeginObservation();
                Check(!controller.MoveWindowRelative(dialog, 1) && State(controller) == before && changes == 0
                    && controller.VisibilityGeneration == generation
                    && pending.All(pair => controller.Catalog.GetAssignment(pair.Key) == pair.Value),
                    "relative follow cannot race an outstanding cross-display placement", results);
            }
            else results.Add("SKIP pending cross-display placement guard requires a second physical display");
            controller.RestoreAllAndPause();
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "move-follow fixture cleanup restoration");
            Check(fixture.Handles.All(NativeWindows.IsVisible), "every dedicated fixture window is restored before cleanup", results);

            void BeginObservation()
            {
                changes = 0;
                hides.Clear();
                generation = controller.VisibilityGeneration;
            }
            void ReturnToFirst()
            {
                if (!controller.MoveWindowToDisplay(root, source, first))
                    throw new InvalidOperationException("Fixture group could not return to its original desktop.");
                WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "original fixture group restoration");
                Settle();
            }
            void Settle()
            {
                PumpFor(110);
                controller.Refresh(); // Its predicate excludes all windows outside the validated fixture process.
            }
        }
        finally
        {
            controller.Changed -= OnChanged;
            controller.WindowHideRequested -= OnHide;
        }

        Guid Active(string id) => controller.Catalog.Snapshot().Single(d => d.Id == id).ActiveDesktopId;
        bool AllActiveFirst() => controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == firstDesktops[d.Id]);
        bool OtherDisplaysAtFirst() => controller.Catalog.Snapshot().Where(d => d.Id != source)
            .All(d => d.ActiveDesktopId == firstDesktops[d.Id]);
        bool GroupAssigned(Guid desktop) => group.All(h => controller.Catalog.GetAssignment(h) is { } a
            && a.DisplayId == source && a.DesktopId == desktop);
        bool OthersAssigned() => others.All(h => controller.Catalog.GetAssignment(h) == original[h]);
        void OnChanged() => changes++;
        void OnHide(long handle) => hides.Add(handle);
    }

    private sealed class FixtureSession : IDisposable
    {
        private readonly string _manifest;
        private Process? _process;
        private RecoveryJournal? _journal;
        private DesktopController? _controller;
        private FixtureInfo? _info;
        public bool CanHide { get; set; } = true;
        public DesktopController Controller => _controller!;
        public int ProcessId => _info!.ProcessId;
        public long[] Handles => _info!.Handles;

        public FixtureSession(string output)
        {
            string token = Guid.NewGuid().ToString("N");
            _manifest = Path.Combine(output, $"window-move-follow-fixture-{token}.json");
            try
            {
                _process = ProcessLauncher.Start("--fixture", _manifest);
                WaitUntil(() => File.Exists(_manifest) && new FileInfo(_manifest).Length > 0, "move-follow fixture startup");
                _info = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(_manifest))
                    ?? throw new InvalidOperationException("Move-follow fixture manifest is empty.");
                if (_info.ProcessId != _process.Id || Handles.Length < 3
                    || Handles.Any(h => h == 0) || Handles.Distinct().Count() != Handles.Length)
                    throw new InvalidOperationException("Move-follow fixture process/manifest identity mismatch.");
                WaitUntil(() => NativeWindows.EnumerateWindows().Count(w =>
                    w.ProcessId == ProcessId && Handles.Contains(w.Handle)) == Handles.Length, "move-follow fixture enumeration");
                var windows = NativeWindows.EnumerateWindows().Where(w =>
                    w.ProcessId == ProcessId && Handles.Contains(w.Handle)).ToDictionary(w => w.Handle);
                long started = _process.StartTime.ToUniversalTime().Ticks;
                foreach (long handle in Handles)
                {
                    var window = windows[handle];
                    if (window.ProcessStartTicks != started
                        || !NativeWindows.IsSameWindow(handle, ProcessId, started, window.ClassName))
                        throw new InvalidOperationException("Refusing to operate on a non-fixture window.");
                    // STARTUPINFO can hide the helper's first form. Only reveal a validated
                    // helper window, preserving its show state and never requesting activation.
                    if (!NativeWindows.IsVisible(handle))
                        NativeWindows.Show(handle, window.IsMinimized, window.IsMaximized);
                }
                WaitUntil(() => Handles.All(NativeWindows.IsVisible), "move-follow fixture initial visibility");
                _journal = new RecoveryJournal(Path.Combine(output, $"window-move-follow-recovery-{token}.json"));
                _controller = new DesktopController(_journal, window =>
                    window.ProcessId == ProcessId && Handles.Contains(window.Handle), () => CanHide);
                if (!Handles.All(Controller.Windows.ContainsKey))
                    throw new InvalidOperationException("Move-follow fixture did not discover every window on the current native desktop.");
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
