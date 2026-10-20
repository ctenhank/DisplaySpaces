using System.Diagnostics;
using System.Text.Json;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Services;

namespace DisplaySpaces.App;

/// <summary>Uses only a dedicated fixture process, its windows, and an isolated recovery journal.</summary>
internal static class TaskbarActivationSelfTests
{
    private sealed record FixtureInfo(int ProcessId, long[] Handles);
    private sealed class ActivationHarnessForm : Form { }

    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "taskbar-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        using var harness = new ActivationHarnessForm
        {
            Text = "DisplaySpaces taskbar activation test",
            ClientSize = new Size(420, 150),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        harness.Controls.Add(new Label
        {
            Text = "Activation tests / temporary fixture\nClick this test window to begin.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        });
        FocusHarness(harness, output, "Exact-window routing");
        ExactWindowRouting(output, results);
        FocusHarness(harness, output, "Native-event routing");
        NativeEventRouting(output, results);
    }

    private static void FocusHarness(Form harness, string output, string stage)
    {
        harness.Show();
        // Helpers launch hidden; explicitly reveal only this temporary test-owned form.
        NativeWindows.Show(harness.Handle.ToInt64(), minimized: false, maximized: false);
        harness.Activate();
        File.WriteAllText(Path.Combine(output, "taskbar-harness.json"), JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, Handle = harness.Handle.ToInt64(), Stage = stage,
            Title = harness.Text, State = "waiting-for-test-window-focus"
        }));
        WaitUntil(() => NativeWindows.ForegroundWindow == harness.Handle.ToInt64(),
            "click the temporary taskbar activation test window to grant normal foreground permission", 60_000);
        PumpFor(250);
        if (NativeWindows.ForegroundWindow != harness.Handle.ToInt64())
            throw new InvalidOperationException("The temporary test harness lost foreground during focus settling.");
    }

    private static void ExactWindowRouting(string output, List<string> results)
    {
        using var fixture = new FixtureSession(output);
        var controller = fixture.Controller;
        var initial = controller.Catalog.Snapshot().ToDictionary(display => display.Id, display => display.ActiveDesktopId);
        Guid rootDesktop = controller.Catalog.AddDesktop(fixture.DisplayId);
        Guid secondDesktop = controller.Catalog.AddDesktop(fixture.DisplayId);
        long beforeMove = controller.VisibilityGeneration;
        uint beforeMoveTime = FreshTime(controller);
        controller.MoveWindow(fixture.Root, rootDesktop);
        controller.MoveWindow(fixture.Second, secondDesktop);
        WaitUntil(() => !NativeWindows.IsVisible(fixture.Root) && !NativeWindows.IsVisible(fixture.Dialog)
            && !NativeWindows.IsVisible(fixture.Second), "fixture windows move to two inactive desktops");
        Assert(ActiveDesktops(controller).All(pair => pair.Value == initial[pair.Key])
            && controller.Catalog.GetAssignment(fixture.Dialog)?.DesktopId == rootDesktop,
            "moving same-process windows to separate inactive desktops does not switch either display and keeps the owner/dialog group together", results);

        NativeWindowInfo root = controller.Windows[fixture.Root];
        NativeWindowInfo second = controller.Windows[fixture.Second];
        NativeWindowInfo dialog = controller.Windows[fixture.Dialog];
        Assert(root.ProcessId == second.ProcessId && root.RootOwner != second.RootOwner,
            "taskbar fixture represents unrelated windows in the same app process", results);

        fixture.Select(fixture.Second);
        Assert(!controller.FollowForegroundWindow(second, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == fixture.InitialDesktop,
            "foreground events while a window's asynchronous hide is unobserved cannot reverse the move", results);
        controller.Refresh();
        WaitUntil(() => !NativeWindows.IsVisible(fixture.Second), "pending-hide fixture returned to its inactive desktop");
        fixture.ObserveHiddenWindows();

        fixture.Select(fixture.Root);
        Assert(!controller.FollowForegroundWindow(root, FreshTime(controller), beforeMove)
            && !controller.FollowForegroundWindow(root, beforeMoveTime, controller.VisibilityGeneration)
            && !controller.FollowForegroundWindow(root, controller.LastVisibilityChangeTime, controller.VisibilityGeneration)
            && fixture.ActiveDesktop == fixture.InitialDesktop,
            "queued foreground events with an old generation or a timestamp at/before the last move cannot undo the move", results);
        Assert(!controller.FollowForegroundWindow(root with { ProcessStartTicks = root.ProcessStartTicks + 1 },
                FreshTime(controller), controller.VisibilityGeneration)
            && !controller.FollowForegroundWindow(root with { ClassName = root.ClassName + ".recycled" },
                FreshTime(controller), controller.VisibilityGeneration)
            && !controller.FollowForegroundWindow(root with { ProcessId = Environment.ProcessId },
                FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == fixture.InitialDesktop,
            "stale or recycled window identities are rejected even when their handle is foreground", results);
        Assert(!controller.FollowForegroundWindow(root with { Handle = 0 }, FreshTime(controller), controller.VisibilityGeneration),
            "unknown foreground-window identities do not select a desktop", results);

        Assert(controller.FollowForegroundWindow(root, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == rootDesktop,
            "selecting one app window follows that exact window's assigned desktop", results);
        WaitUntil(() => NativeWindows.IsVisible(fixture.Root) && NativeWindows.IsVisible(fixture.Dialog)
            && !NativeWindows.IsVisible(fixture.Second), "selected owner group restored without the other same-process window");
        AssertOtherDisplays(controller, initial, fixture.DisplayId, results);

        fixture.Select(fixture.Second);
        Assert(controller.FollowForegroundWindow(second, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == secondDesktop,
            "selecting another window from the same process follows its own desktop rather than the first process match", results);
        WaitUntil(() => NativeWindows.IsVisible(fixture.Second) && !NativeWindows.IsVisible(fixture.Root)
            && !NativeWindows.IsVisible(fixture.Dialog), "second same-process window desktop selected");
        Assert(!NativeWindows.IsMinimized(fixture.Second),
            "a selected formerly minimized window stays restored after its desktop opens", results);
        Assert(!controller.FollowForegroundWindow(second, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == secondDesktop,
            "selecting a window on the current desktop does not perform a redundant switch", results);

        fixture.ShowWithoutActivation(fixture.Root);
        Assert(NativeWindows.ForegroundWindow == fixture.Second
            && !controller.FollowForegroundWindow(root, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == secondDesktop,
            "a background window becoming visible cannot steal the desktop without becoming the exact foreground window", results);
        controller.Refresh();
        WaitUntil(() => !NativeWindows.IsVisible(fixture.Root), "unsolicited background window is hidden again");
        fixture.ObserveHiddenWindows();

        fixture.Select(fixture.Dialog);
        Assert(controller.FollowForegroundWindow(dialog, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == rootDesktop
            && controller.Catalog.GetAssignment(fixture.Root)?.DesktopId == rootDesktop,
            "foreground owned dialogs follow their shared owner desktop", results);

        controller.Switch(fixture.DisplayId, fixture.InitialDesktop);
        controller.RestoreAllAndPause();
        WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "paused fixture windows restored");
        fixture.Select(fixture.Second);
        Assert(!controller.FollowForegroundWindow(second, FreshTime(controller), controller.VisibilityGeneration)
            && fixture.ActiveDesktop == fixture.InitialDesktop,
            "paused desktop management ignores foreground activation", results);

        var targetDisplay = controller.Catalog.Snapshot().FirstOrDefault(display => display.Id != fixture.DisplayId);
        if (targetDisplay is not null)
        {
            controller.TogglePause();
            Guid movedDesktop = controller.Catalog.AddDesktop(targetDisplay.Id);
            var activeBeforeTransfer = ActiveDesktops(controller);
            Assert(controller.MoveWindowToDisplay(fixture.Root, targetDisplay.Id, movedDesktop),
                "cross-display taskbar fixture move is accepted", results);
            WaitUntil(() => !NativeWindows.IsVisible(fixture.Root), "cross-display fixture hide completes");
            fixture.ObserveHiddenWindows();
            fixture.Select(fixture.Root);
            Assert(!controller.FollowForegroundWindow(root, FreshTime(controller), controller.VisibilityGeneration)
                && ActiveDesktops(controller).All(pair => pair.Value == activeBeforeTransfer[pair.Key]),
                "foreground notifications during an unreconciled cross-display move cannot open its destination", results);
        }
    }

    private static void NativeEventRouting(string output, List<string> results)
    {
        using var fixture = new FixtureSession(output);
        var controller = fixture.Controller;
        Guid rootDesktop = controller.Catalog.AddDesktop(fixture.DisplayId);
        Guid secondDesktop = controller.Catalog.AddDesktop(fixture.DisplayId);
        controller.MoveWindow(fixture.Root, rootDesktop);
        controller.MoveWindow(fixture.Second, secondDesktop);
        WaitUntil(() => !NativeWindows.IsVisible(fixture.Root) && !NativeWindows.IsVisible(fixture.Second),
            "native taskbar fixture starts with two hidden app windows");
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        bool suspended = false;
        using var coordinator = new TaskbarDesktopCoordinator(controller, dispatcher, () => suspended,
            (uint)fixture.ProcessId);
        WaitUntil(() => coordinator.IsRegistered(fixture.Root) && coordinator.IsRegistered(fixture.Second),
            "hidden fixture windows receive taskbar registrations");
        Assert(coordinator.RegisteredWindowCount == 2 && !coordinator.IsRegistered(fixture.Dialog),
            "hidden unrelated app windows retain separate taskbar entries while owned dialogs do not create duplicates", results);

        _ = FreshTime(controller);
        // Exercise the real foreground hook after native restore + activation. Calling
        // SetForegroundWindow on a hidden HWND alone does not reproduce the shell's
        // restore behavior, and is not an automated taskbar-click test.
        fixture.Select(fixture.Root);
        WaitUntil(() => fixture.ActiveDesktop == rootDesktop, "native foreground hook follows selected fixture root");
        WaitUntil(() => !coordinator.IsRegistered(fixture.Root) && coordinator.IsRegistered(fixture.Second),
            "taskbar registrations settle after following the root window");
        Assert(!coordinator.IsRegistered(fixture.Root) && coordinator.IsRegistered(fixture.Second),
            "programmatic fixture restoration/activation emits a native foreground event that opens its desktop and releases its taskbar registration", results);

        _ = FreshTime(controller);
        fixture.Select(fixture.Second);
        WaitUntil(() => fixture.ActiveDesktop == secondDesktop, "native foreground hook follows exact second fixture window");
        WaitUntil(() => coordinator.IsRegistered(fixture.Root) && !coordinator.IsRegistered(fixture.Second),
            "taskbar registrations settle after following the second window");
        Assert(coordinator.IsRegistered(fixture.Root) && !coordinator.IsRegistered(fixture.Second),
            "native foreground events distinguish multiple windows of the same app", results);

        controller.Switch(fixture.DisplayId, fixture.InitialDesktop);
        WaitUntil(() => !NativeWindows.IsVisible(fixture.Root) && !NativeWindows.IsVisible(fixture.Second),
            "suspension fixture windows hidden");
        suspended = true;
        _ = FreshTime(controller);
        fixture.Select(fixture.Second);
        PumpFor(180);
        Assert(fixture.ActiveDesktop == fixture.InitialDesktop,
            "overlay/settings suspension prevents taskbar activation from changing the desktop", results);

        suspended = false;
        coordinator.Dispose();
        Assert(coordinator.RegisteredWindowCount == 0,
            "disposing taskbar integration releases every temporary registration", results);
        _ = FreshTime(controller);
        fixture.Select(fixture.Root);
        PumpFor(180);
        Assert(fixture.ActiveDesktop == fixture.InitialDesktop,
            "disposed foreground hooks no longer follow fixture activations", results);
    }

    /// <summary>Bounded opt-in shell-click fixture. A stop file ends it without touching any other app.</summary>
    public static int RunInteractive(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string statusPath = Path.Combine(output, "taskbar-interactive.json");
        string stopPath = Path.Combine(output, "taskbar-interactive.stop");
        var history = new List<object>();
        long sequence = 0;
        try
        {
            using var fixture = new FixtureSession(output);
            Guid rootDesktop = fixture.Controller.Catalog.AddDesktop(fixture.DisplayId, "Taskbar fixture A");
            Guid secondDesktop = fixture.Controller.Catalog.AddDesktop(fixture.DisplayId, "Taskbar fixture B");
            fixture.Controller.MoveWindow(fixture.Root, rootDesktop);
            fixture.Controller.MoveWindow(fixture.Second, secondDesktop);
            WaitUntil(() => !NativeWindows.IsVisible(fixture.Root) && !NativeWindows.IsVisible(fixture.Second),
                "interactive fixture windows hidden");
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            using var coordinator = new TaskbarDesktopCoordinator(fixture.Controller, dispatcher,
                eventProcessId: (uint)fixture.ProcessId);
            using var diagnosticHook = new ForegroundWindowHook((uint)fixture.ProcessId);
            void Record(string kind, long handle, uint eventTime)
            {
                // Hook callbacks collect fixture-only state in memory; the outer loop writes files.
                history.Add(new
                {
                    Sequence = ++sequence, Kind = kind, EventHandle = handle, EventTime = eventTime,
                    CurrentForeground = NativeWindows.ForegroundWindow,
                    Assignment = handle == 0 ? null : fixture.Controller.Catalog.GetAssignment(handle),
                    fixture.Controller.VisibilityGeneration, fixture.Controller.LastVisibilityChangeTime,
                    Journaled = fixture.Controller.OwnsHiddenWindow(handle),
                    Registered = coordinator.IsRegistered(handle),
                    ActiveDesktops = ActiveDesktops(fixture.Controller)
                });
                if (history.Count > 256) history.RemoveAt(0);
            }
            void ForegroundObserved(long handle, uint eventTime) => Record("foreground", handle, eventTime);
            void ControllerChanged() => Record("controller-changed", NativeWindows.ForegroundWindow,
                unchecked((uint)Environment.TickCount));
            diagnosticHook.Activated += ForegroundObserved;
            fixture.Controller.Changed += ControllerChanged;
            void WriteStatus(string state)
            {
                File.WriteAllText(statusPath + ".tmp", JsonSerializer.Serialize(new
                {
                    State = state, fixture.ProcessId, fixture.Handles, fixture.DisplayId,
                    fixture.InitialDesktop, RootDesktop = rootDesktop, SecondDesktop = secondDesktop,
                    fixture.ActiveDesktop, NativeWindows.ForegroundWindow,
                    RegisteredWindows = fixture.Handles.Where(coordinator.IsRegistered).ToArray(),
                    Events = history.ToArray(),
                    Windows = fixture.Controller.Windows.Values.Select(window => new
                    {
                        window.Handle, window.Title, window.RootOwner,
                        Visible = NativeWindows.IsVisible(window.Handle),
                        Assignment = fixture.Controller.Catalog.GetAssignment(window.Handle)
                    }).ToArray()
                }));
                File.Move(statusPath + ".tmp", statusPath, true);
            }
            try
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromMinutes(5) && !File.Exists(stopPath))
                {
                    WriteStatus("running");
                    PumpFor(500);
                }
                WriteStatus("completed");
            }
            finally
            {
                diagnosticHook.Activated -= ForegroundObserved;
                fixture.Controller.Changed -= ControllerChanged;
            }
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(statusPath, JsonSerializer.Serialize(new
            {
                State = "failed", Error = error.ToString(), Events = history.ToArray()
            }));
            return 1;
        }
    }

    private sealed class FixtureSession : IDisposable
    {
        private Process? _process;
        private RecoveryJournal? _journal;
        private DesktopController? _controller;
        private FixtureInfo? _info;
        public DesktopController Controller => _controller!;
        public int ProcessId => _info!.ProcessId;
        public long[] Handles => _info!.Handles;
        public long Root => Handles[0];
        public long Second => Handles[1];
        public long Dialog => Handles[2];
        public string DisplayId { get; private set; } = "";
        public Guid InitialDesktop { get; private set; }
        public Guid ActiveDesktop => Controller.Catalog.Snapshot().Single(display => display.Id == DisplayId).ActiveDesktopId;

        public FixtureSession(string output)
        {
            try
            {
                string session = Guid.NewGuid().ToString("N");
                string manifest = Path.Combine(output, $"taskbar-fixture-{session}.json");
                _process = ProcessLauncher.Start("--fixture", manifest);
                WaitUntil(() => File.Exists(manifest) && new FileInfo(manifest).Length > 0, "taskbar fixture startup");
                _info = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(manifest))
                    ?? throw new InvalidOperationException("Taskbar fixture manifest is empty.");
                if (_info.ProcessId != _process.Id || _info.Handles.Length < 3)
                    throw new InvalidOperationException("Taskbar fixture process/manifest identity mismatch.");
                WaitUntil(() => NativeWindows.EnumerateWindows().Count(window => window.ProcessId == ProcessId) >= Handles.Length,
                    "taskbar fixture enumeration");
                _journal = new RecoveryJournal(Path.Combine(output, $"taskbar-recovery-{session}.json"));
                _controller = new DesktopController(_journal, window => window.ProcessId == ProcessId);
                var assignment = Controller.Catalog.GetAssignment(Root)
                    ?? throw new InvalidOperationException("Taskbar fixture is not on the current native desktop.");
                DisplayId = assignment.DisplayId;
                InitialDesktop = assignment.DesktopId;
                if (!Handles.All(Controller.Windows.ContainsKey))
                    throw new InvalidOperationException("Taskbar fixture did not discover all owned windows.");
            }
            catch { Dispose(); throw; }
        }

        public void ShowWithoutActivation(long handle)
        {
            Validate(handle);
            NativeWindows.Show(handle, minimized: false, maximized: false);
            WaitUntil(() => NativeWindows.IsVisible(handle), "fixture background show");
        }

        public void ObserveHiddenWindows()
        {
            WaitUntil(() =>
            {
                foreach (long handle in Handles) Controller.ObserveHiddenWindow(handle);
                return Handles.All(handle => !Controller.HasPendingHide(handle));
            }, "fixture hide completion is observed after asynchronous requests settle");
            uint observedAt = unchecked((uint)Environment.TickCount);
            WaitUntil(() => unchecked((uint)Environment.TickCount) != observedAt,
                "foreground event follows the completed-hide observation");
        }

        public void Select(long handle)
        {
            Validate(handle);
            if (handle == Dialog) ShowWithoutActivation(Root);
            ShowWithoutActivation(handle);
            NativeWindows.Activate(handle);
            if (NativeWindows.ForegroundWindow != handle)
            {
                PumpFor(80);
                long foreground = NativeWindows.ForegroundWindow;
                if (foreground != handle && IsTestOwnedForeground(foreground))
                {
                    Validate(handle);
                    // A single retry can settle test-owned focus transitions. Never retry over
                    // an unrelated app, including one the user focused during this short wait.
                    if (NativeWindows.ForegroundWindow == foreground) NativeWindows.Activate(handle);
                }
            }
            WaitUntil(() => NativeWindows.ForegroundWindow == handle,
                $"foreground activation of fixture {handle}; Windows may have denied foreground permission");
            WaitUntil(() => !NativeWindows.IsMinimized(handle), "selected fixture finishes its asynchronous restore");
        }

        private bool IsTestOwnedForeground(long handle)
        {
            if (Form.FromHandle(new nint(handle)) is ActivationHarnessForm { IsDisposed: false }) return true;
            return Handles.Contains(handle) && Controller.Windows.TryGetValue(handle, out var window)
                && window.ProcessId == ProcessId
                && NativeWindows.IsSameWindow(handle, ProcessId, window.ProcessStartTicks, window.ClassName);
        }

        private void Validate(long handle)
        {
            if (!Handles.Contains(handle) || !Controller.Windows.TryGetValue(handle, out var window)
                || window.ProcessId != ProcessId
                || !NativeWindows.IsSameWindow(handle, ProcessId, window.ProcessStartTicks, window.ClassName))
                throw new InvalidOperationException("Refusing to operate on a non-fixture window.");
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
                if (_process is not null)
                {
                    try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(3000); } }
                    finally { _process.Dispose(); _process = null; }
                }
                _journal?.PruneClosed();
                _journal?.DeleteIfEmpty();
            }
        }
    }

    private static Dictionary<string, Guid> ActiveDesktops(DesktopController controller) =>
        controller.Catalog.Snapshot().ToDictionary(display => display.Id, display => display.ActiveDesktopId);

    private static void AssertOtherDisplays(DesktopController controller, Dictionary<string, Guid> before,
        string targetDisplay, List<string> results)
    {
        if (before.Count < 2) { results.Add("SKIP taskbar activation independence requires two physical displays"); return; }
        Assert(ActiveDesktops(controller).Where(pair => pair.Key != targetDisplay).All(pair => before[pair.Key] == pair.Value),
            "following a taskbar window changes only that window's physical display", results);
    }

    private static uint FreshTime(DesktopController controller)
    {
        WaitUntil(() => unchecked((int)(unchecked((uint)Environment.TickCount) - controller.LastVisibilityChangeTime)) > 0,
            "fresh foreground event clock tick");
        return unchecked((uint)Environment.TickCount);
    }

    private static void Assert(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS " + name);
    }

    private static void WaitUntil(Func<bool> condition, string name, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException(name + $" (foreground={NativeWindows.ForegroundWindow})");
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    private static void PumpFor(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
    }
}
