using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using IndependentVirtualDesktop.App.Native;
using IndependentVirtualDesktop.App.Services;

namespace IndependentVirtualDesktop.App;

/// <summary>Exercises only windows belonging to a dedicated fixture subprocess. Never installs keyboard hooks.</summary>
internal static class SmokeTests
{
    private sealed record FixtureInfo(int ProcessId, long[] Handles);
    private sealed class FixtureWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    public static int RunFixture(string manifest)
    {
        using var root = CreateFixture("테스트 메모 · 첫 번째 작업", 30);
        using var minimized = CreateFixture("테스트 문서 · 최소화 상태 유지", 80);
        using var dialog = CreateFixture("테스트 대화상자 · 소유 창과 함께 이동", 130);
        dialog.ShowInTaskbar = false;
        using var otherDisplay = Screen.AllScreens.Length > 1 ? CreateFixture("테스트 보조 모니터 · 독립 전환 확인", 50) : null;
        if (otherDisplay is not null)
        {
            var area = Screen.AllScreens.First(s => !s.Primary).WorkingArea;
            otherDisplay.Location = new Point(area.Left + 50, area.Top + 50);
            otherDisplay.Show();
        }
        root.Show();
        minimized.Show();
        minimized.WindowState = FormWindowState.Minimized;
        dialog.Show(root);
        var handles = new List<long> { root.Handle.ToInt64(), minimized.Handle.ToInt64(), dialog.Handle.ToInt64() };
        if (otherDisplay is not null) handles.Add(otherDisplay.Handle.ToInt64());
        File.WriteAllText(manifest + ".tmp", JsonSerializer.Serialize(new FixtureInfo(Environment.ProcessId, handles.ToArray())));
        File.Move(manifest + ".tmp", manifest, true);
        Application.Run(root);
        return 0;
    }

    private static FixtureWindow CreateFixture(string title, int offset)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var form = new FixtureWindow { Text = title, Size = new Size(350, 170), StartPosition = FormStartPosition.Manual,
            Location = new Point(area.Left + offset, area.Top + offset), BackColor = Color.FromArgb(28, 34, 47),
            ForeColor = Color.White, Font = new Font("맑은 고딕", 11) };
        form.Controls.Add(new Label { Text = "Independent Spaces\n창 복구 검증용 임시 창입니다.", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter });
        return form;
    }

    public static int RunCrashFixture(string journalPath, string manifest)
    {
        var fixture = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(manifest))!;
        var window = NativeWindows.EnumerateWindows().First(w => w.Handle == fixture.Handles[1] && w.ProcessId == fixture.ProcessId);
        var journal = new RecoveryJournal(journalPath);
        using var guardian = ProcessLauncher.StartGuardian(journalPath);
        if (!journal.PrepareHide(window)) return 2;
        NativeWindows.Hide(window.Handle);
        WaitUntil(() => !NativeWindows.IsVisible(window.Handle), "crash fixture hidden");
        File.WriteAllText(journalPath + ".ready", "ready");
        Thread.Sleep(Timeout.Infinite); // The test parent terminates this process to exercise the independent guardian.
        return 0;
    }

    public static int Run(string? outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory ?? Path.Combine(AppContext.BaseDirectory, "test-results"));
        Directory.CreateDirectory(output);
        var results = new List<string>();
        Process? fixtureProcess = null;
        Process? crashProcess = null;
        RecoveryJournal? journal = null;
        string session = Guid.NewGuid().ToString("N");
        string manifest = Path.Combine(output, $"fixture-{session}.json");
        string journalPath = Path.Combine(output, $"recovery-{session}.json");
        string crashJournal = Path.Combine(output, $"crash-{session}.json");
        try
        {
            foreach (var name in NativeSelfTests.Run()) results.Add("PASS " + name);
            fixtureProcess = ProcessLauncher.Start("--fixture", manifest);
            WaitUntil(() => File.Exists(manifest) && new FileInfo(manifest).Length > 0, "fixture startup");
            var fixture = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(manifest))!;
            WaitUntil(() => NativeWindows.EnumerateWindows().Count(w => w.ProcessId == fixture.ProcessId) >= 3, "fixture enumeration");
            journal = new RecoveryJournal(journalPath);
            var controller = new DesktopController(journal, window => window.ProcessId == fixture.ProcessId);
            Assert(controller.Windows.Count >= 3, "fixture-only window discovery including owned dialog", results);
            long root = fixture.Handles[0];
            long minimized = fixture.Handles[1];
            long dialog = fixture.Handles[2];
            long[] primaryHandles = fixture.Handles.Take(3).ToArray();
            long[] secondaryHandles = fixture.Handles.Skip(3).ToArray();
            Assert(controller.Windows[dialog].RootOwner == root, "native owner chain includes overlapped owned windows", results);
            var initial = controller.Catalog.GetAssignment(root) ?? throw new InvalidOperationException("Fixture not on current native desktop.");
            controller.AddDesktop(initial.DisplayId);
            var second = controller.Catalog.Snapshot().First(d => d.Id == initial.DisplayId).ActiveDesktopId;
            WaitUntil(() => primaryHandles.All(h => !NativeWindows.IsVisible(h)), "target-display fixture windows hidden");
            Assert(secondaryHandles.All(NativeWindows.IsVisible), "other physical display windows remain visible", results);
            Assert(journal.Entries.Count == 3, "write-ahead recovery entries include owned dialogs", results);
            Assert(controller.Catalog.Snapshot().Where(d => d.Id != initial.DisplayId).All(d => d.Desktops.Count == 1),
                "switching one display leaves other display states unchanged", results);

            controller.Switch(initial.DisplayId, initial.DesktopId);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "fixture restoration");
            PumpFor(150);
            controller.Refresh();
            Assert(NativeWindows.IsMinimized(minimized), "minimized window state preserved", results);
            Assert(journal.Entries.Count == 0, "restored windows clear recovery markers and journal", results);

            controller.MoveWindow(root, second);
            WaitUntil(() => !NativeWindows.IsVisible(root) && !NativeWindows.IsVisible(dialog), "owner group moved");
            Assert(NativeWindows.IsVisible(minimized), "moving owner/dialog does not hide unrelated window", results);
            Assert(controller.Catalog.GetAssignment(dialog)?.DesktopId == second, "owned dialog follows owner assignment", results);

            controller.Switch(initial.DisplayId, second);
            WaitUntil(() => NativeWindows.IsVisible(root) && !NativeWindows.IsVisible(minimized), "switch moved group");
            controller.RemoveDesktop(initial.DisplayId, second);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "desktop deletion restores migrated windows");
            PumpFor(150); controller.Refresh();
            Assert(controller.Catalog.Snapshot().First(d => d.Id == initial.DisplayId).Desktops.Count == 1,
                "desktop deletion preserves windows on adjacent desktop", results);

            // Stress asynchronous show/hide ordering without waiting between each switch.
            controller.AddDesktop(initial.DisplayId);
            second = controller.Catalog.Snapshot().First(d => d.Id == initial.DisplayId).ActiveDesktopId;
            for (int i = 0; i < 6; i++)
            {
                controller.Switch(initial.DisplayId, initial.DesktopId);
                controller.Switch(initial.DisplayId, second);
            }
            WaitUntil(() => primaryHandles.All(h => !NativeWindows.IsVisible(h)), "rapid-switch final visibility");
            controller.RestoreAllAndPause();
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "emergency restoration");
            PumpFor(150); controller.Refresh(); PumpFor(150); controller.Refresh();
            Assert(controller.Paused && journal.Entries.Count == 0, "emergency recovery and pause", results);

            controller.TogglePause();
            controller.Switch(initial.DisplayId, initial.DesktopId);
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "overview fixture restoration");
            PumpFor(150); controller.Refresh();
            using (var overview = new OverviewForm(controller))
            {
                overview.Size = new Size(1180, 840);
                overview.UpdateContent(true);
                overview.Show();
                PumpFor(120);
                overview.PerformLayout();
                using var bitmap = new Bitmap(overview.Width, overview.Height);
                overview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, overview.Size));
                bitmap.Save(Path.Combine(output, "overview.png"), ImageFormat.Png);
                overview.Hide();
            }
            results.Add("PASS overview render to overview.png");

            controller.RestoreAllAndPause();
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "pre-crash-test restoration");
            PumpFor(150); controller.Refresh();
            crashProcess = ProcessLauncher.Start("--crash-fixture", crashJournal, manifest);
            WaitUntil(() => File.Exists(crashJournal + ".ready"), "guardian readiness", 8000);
            Assert(!NativeWindows.IsVisible(minimized), "crash fixture hid only a fixture window", results);
            crashProcess.Kill();
            crashProcess.WaitForExit(3000);
            WaitUntil(() => NativeWindows.IsVisible(minimized), "guardian restored after manager termination", 8000);
            WaitUntil(() => !File.Exists(crashJournal), "guardian journal cleanup", 5000);
            Assert(NativeWindows.IsMinimized(minimized), "guardian preserves minimized state after forced termination", results);
            Assert(NativeWindows.GetRecoveryMarker(minimized) == 0, "guardian clears recovery marker", results);
            results.Add($"PASS real Windows integration ({Screen.AllScreens.Length} physical display(s)); no live keyboard hook installed");
            File.WriteAllLines(Path.Combine(output, "smoke-results.txt"), results);
            return 0;
        }
        catch (Exception error)
        {
            results.Add("FAIL " + error);
            if (fixtureProcess is not null)
                foreach (var window in NativeWindows.EnumerateWindows().Where(w => w.ProcessId == fixtureProcess.Id))
                    results.Add($"DIAGNOSTIC {window} CurrentDesktop={NativeWindows.IsOnCurrentVirtualDesktop(window.Handle)} Marker={NativeWindows.GetRecoveryMarker(window.Handle)}");
            if (journal is not null) results.Add("DIAGNOSTIC journal " + JsonSerializer.Serialize(journal.Entries));
            File.WriteAllLines(Path.Combine(output, "smoke-results.txt"), results);
            return 1;
        }
        finally
        {
            journal?.RestoreAll();
            PumpFor(150);
            if (crashProcess is not null) { if (!crashProcess.HasExited) crashProcess.Kill(); crashProcess.Dispose(); }
            if (fixtureProcess is not null) { if (!fixtureProcess.HasExited) fixtureProcess.Kill(); fixtureProcess.WaitForExit(3000); fixtureProcess.Dispose(); }
            journal?.PruneClosed();
            journal?.DeleteIfEmpty();
            foreach (string path in new[] { manifest, crashJournal + ".ready" })
                if (File.Exists(path)) File.Delete(path);
        }
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
            if (timer.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException(name);
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    private static void PumpFor(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
    }
}
