using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Localization;

namespace BetterVirtualDesktop.App;

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
        form.Controls.Add(new Label { Text = "BetterVirtualDesktop\n창 복구 검증용 임시 창입니다.", Dock = DockStyle.Fill,
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
            File.WriteAllText(Path.Combine(output, "smoke-results.txt"), "RUNNING\n");
            File.WriteAllText(Path.Combine(output, "test-stage.txt"), "Native keyboard state tests");
            foreach (var name in NativeSelfTests.Run()) results.Add("PASS " + name);
            File.WriteAllText(Path.Combine(output, "test-stage.txt"), "Shortcut preference tests");
            ShortcutPreferenceSelfTests.Run(output, results);
            File.WriteAllText(Path.Combine(output, "test-stage.txt"), "Shortcut settings UI tests");
            ShortcutSettingsSelfTests.Run(output, results);
            File.WriteAllText(Path.Combine(output, "test-stage.txt"), "Desktop fixture tests");
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

            RunCrossDisplayMoves(controller, journal, fixture, initial.DisplayId, initial.DesktopId, results);
            RunMoveToNewDesktop(controller, journal, fixture, initial.DisplayId, initial.DesktopId, results);

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
                // Shortcut saves rebuild the chrome even when the language is unchanged.
                overview.ApplyLanguage();
                overview.ApplyLanguage();
                overview.PerformLayout();
                using var bitmap = new Bitmap(overview.Width, overview.Height);
                overview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, overview.Size));
                bitmap.Save(Path.Combine(output, "overview.png"), ImageFormat.Png);
                string previousLanguage = UiText.SelectedLanguageCode;
                try
                {
                    overview.Size = overview.MinimumSize;
                    foreach (var language in UiText.SupportedLanguages)
                    {
                        UiText.SetLanguage(language.Code, persist: false);
                        overview.ApplyLanguage();
                        PumpFor(50);
                        var actions = overview.Controls.OfType<Panel>()
                            .SelectMany(panel => panel.Controls.OfType<FlowLayoutPanel>())
                            .Single(panel => panel.Name == "FooterActions");
                        Assert(actions.Controls.Count == 4 && actions.Controls.Cast<Control>()
                            .All(button => actions.ClientRectangle.Contains(button.Bounds)),
                            $"all management actions remain accessible at minimum size: {language.Code}", results);
                        if (language.Code == "en")
                        {
                            using var compact = new Bitmap(overview.Width, overview.Height);
                            overview.DrawToBitmap(compact, new Rectangle(Point.Empty, overview.Size));
                            compact.Save(Path.Combine(output, "overview-compact.png"), ImageFormat.Png);
                        }
                    }
                }
                finally { UiText.SetLanguage(previousLanguage, persist: false); }
                overview.Hide();
            }
            results.Add("PASS overview render to overview.png");
            OverlayInteractionTests.Run(Screen.AllScreens.Single(s => s.DeviceName == initial.DisplayId),
                controller.Catalog.Snapshot().Single(d => d.Id == initial.DisplayId), controller.Windows, output, results);
            LocalizationSelfTests.Run(Screen.AllScreens.Single(s => s.DeviceName == initial.DisplayId),
                controller.Catalog.Snapshot().Single(d => d.Id == initial.DisplayId), controller.Windows, output, results);

            using (var dispatcher = new Control())
            {
                _ = dispatcher.Handle;
                using var overlays = new OverlayCoordinator(controller, dispatcher);
                NativeWindows.Activate(root);
                overlays.Show();
                PumpFor(160);
                WaitUntil(() => overlays.Forms.Single(f => f.DisplayId == initial.DisplayId).HasCachedPreview(root), "fixture preview snapshot");
                Assert(overlays.Visible && overlays.Forms.Count == Screen.AllScreens.Length,
                    "one simultaneously visible overlay per display", results);
                foreach (var form in overlays.Forms)
                {
                    var screen = Screen.AllScreens.Single(s => s.DeviceName == form.DisplayId);
                    var display = controller.Catalog.Snapshot().Single(d => d.Id == form.DisplayId);
                    Assert(form.Visible && form.Bounds == screen.Bounds && form.TopMost && !form.ShowInTaskbar
                        && form.FormBorderStyle == FormBorderStyle.None,
                        $"borderless overlay matches monitor coordinates: {form.DisplayId}", results);
                    Assert(form.DesktopIds.SequenceEqual(display.Desktops.Select(d => d.Id))
                        && form.DisplayWindowIds.Order().SequenceEqual(display.Desktops.SelectMany(d => d.WindowIds).Order()),
                        $"overlay includes only its own monitor desktops and windows: {form.DisplayId}", results);
                    form.Refresh();
                    Assert(form.DesktopRailBounds.Top > form.ClientSize.Height / 2
                        && form.WindowTileBounds.All(b => b.Bottom < form.DesktopRailBounds.Top),
                        $"task view keeps previews above the bottom desktop strip: {form.DisplayId}", results);
                    Assert(form.LivePreviewCount > 0, $"visible fixture windows have DWM live thumbnails: {form.DisplayId}", results);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, screen.Primary ? "overlay-primary.png" : $"overlay-{Array.IndexOf(Screen.AllScreens, screen) + 1}.png"), ImageFormat.Png);
                }
                var unchanged = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
                var primaryOverlay = overlays.Forms.Single(f => f.DisplayId == initial.DisplayId);
                foreach (var targetOverlay in new[] { primaryOverlay, overlays.Forms.FirstOrDefault(f => f.DisplayId != initial.DisplayId) }.OfType<DisplayOverlayForm>())
                {
                    var before = controller.Catalog.Snapshot().Single(d => d.Id == targetOverlay.DisplayId);
                    DispatchDrop(targetOverlay, primaryOverlay.CreateDragData(root), null);
                    PumpFor(140);
                    var after = controller.Catalog.Snapshot().Single(d => d.Id == targetOverlay.DisplayId);
                    var created = after.Desktops.Single(d => !before.Desktops.Any(previous => previous.Id == d.Id));
                    WaitUntil(() => !NativeWindows.IsVisible(root) && !NativeWindows.IsVisible(dialog)
                        && NativeWindows.GetDisplayId(root) == targetOverlay.DisplayId, "new-desktop drop owner group placement");
                    controller.Refresh(); overlays.UpdateContent(); PumpFor(100);
                    Assert(after.Desktops.Count == before.Desktops.Count + 1 && overlays.Visible
                        && targetOverlay.SelectedDesktopId == created.Id
                        && targetOverlay.HasCreationFeedback
                        && controller.Catalog.GetAssignment(root)?.DesktopId == created.Id
                        && controller.Catalog.GetAssignment(dialog)?.DesktopId == created.Id
                        && controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == unchanged[d.Id]),
                        $"new desktop card drop creates one desktop, moves the owner group and previews it without switching: {targetOverlay.DisplayId}", results);
                    if (targetOverlay == primaryOverlay)
                    {
                        targetOverlay.Refresh();
                        using var movedImage = new Bitmap(targetOverlay.Width, targetOverlay.Height);
                        targetOverlay.DrawToBitmap(movedImage, new Rectangle(Point.Empty, targetOverlay.Size));
                        movedImage.Save(Path.Combine(output, "overlay-new-desktop.png"), ImageFormat.Png);
                    }
                    targetOverlay.RequestMove(root, initial.DisplayId, initial.DesktopId);
                    PumpFor(120);
                    WaitUntil(() => NativeWindows.IsVisible(root) && NativeWindows.IsVisible(dialog)
                        && NativeWindows.GetDisplayId(root) == initial.DisplayId, "new-desktop UI fixture restoration");
                    controller.Refresh();
                    controller.RemoveDesktop(targetOverlay.DisplayId, created.Id);
                    overlays.UpdateContent(); PumpFor(100);
                }
                if (overlays.Forms.Count > 1)
                {
                    var other = overlays.Forms.First(f => f.DisplayId != initial.DisplayId);
                    var destination = controller.Catalog.Snapshot().Single(d => d.Id == other.DisplayId);
                    var drag = primaryOverlay.CreateDragData(root);
                    DispatchDrop(other, drag, destination.ActiveDesktopId);
                    PumpFor(120);
                    WaitUntil(() => NativeWindows.GetDisplayId(root) == destination.Id && NativeWindows.GetDisplayId(dialog) == destination.Id,
                        "overlay drag physically moves owner group to another display");
                    controller.Refresh(); overlays.UpdateContent(); PumpFor(120);
                    Assert(overlays.Visible && controller.Catalog.GetAssignment(root)?.DisplayId == destination.Id
                        && controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == unchanged[d.Id]),
                        "drag receiver moves to the destination display without closing overlays or switching desktops", results);
                    other.RequestMove(root, initial.DisplayId, initial.DesktopId);
                    PumpFor(120);
                    WaitUntil(() => NativeWindows.GetDisplayId(root) == initial.DisplayId && NativeWindows.GetDisplayId(dialog) == initial.DisplayId,
                        "overlay move menu route returns owner group");
                    controller.Refresh(); overlays.UpdateContent(); PumpFor(120);
                    Assert(controller.Catalog.GetAssignment(root)?.DesktopId == initial.DesktopId,
                        "overlay move command shares the cross-display destination route", results);
                    other.RequestKeyboardFocus();
                    PumpFor(100);
                    Assert(other.ContainsFocus && overlays.Visible, "pointer-enter focus follows the other monitor without dismissing overlays", results);
                    primaryOverlay.RequestKeyboardFocus();
                    PumpFor(100);
                }
                var originalBounds = primaryOverlay.Bounds;
                primaryOverlay.Size = new Size((int)(914 * primaryOverlay.DeviceDpi / 96.0), (int)(512 * primaryOverlay.DeviceDpi / 96.0));
                primaryOverlay.Refresh();
                Assert(primaryOverlay.WindowTileBounds.Count > 0 && primaryOverlay.WindowTileBounds.All(b => primaryOverlay.ClientRectangle.Contains(b)),
                    "short high-DPI overlay keeps window tiles inside the viewport", results);
                using (var compact = new Bitmap(primaryOverlay.Width, primaryOverlay.Height))
                {
                    primaryOverlay.DrawToBitmap(compact, new Rectangle(Point.Empty, primaryOverlay.Size));
                    compact.Save(Path.Combine(output, "overlay-compact.png"), ImageFormat.Png);
                }
                primaryOverlay.Bounds = originalBounds;
                primaryOverlay.MoveSelection(1);
                Assert(controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == unchanged[d.Id]),
                    "keyboard candidate navigation does not switch desktops", results);
                primaryOverlay.CancelOverlay();
                PumpFor(100);
                Assert(!overlays.Visible && overlays.Forms.Count == 0, "Escape-style dismissal closes every overlay", results);
                overlays.Show();
                PumpFor(120);
                var retired = overlays.Forms.Single(f => f.DisplayId == initial.DisplayId);
                var staleDrag = retired.CreateDragData(root);
                retired.ChooseDesktop(second); // Queue a click, then close/reopen before that click executes.
                overlays.Dismiss(false);
                overlays.Show();
                PumpFor(120);
                Assert(overlays.Visible && controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == unchanged[d.Id]),
                    "queued selection from an old overlay cannot affect a reopened session", results);
                DispatchDrop(overlays.Forms.Single(f => f.DisplayId == initial.DisplayId), staleDrag, second);
                PumpFor(80);
                Assert(controller.Catalog.GetAssignment(root)?.DesktopId == initial.DesktopId,
                    "drag payload from an old overlay session is rejected", results);
                int countBeforeStaleCreate = controller.Catalog.Snapshot().Sum(d => d.Desktops.Count);
                DispatchDrop(overlays.Forms.Single(f => f.DisplayId == initial.DisplayId), staleDrag, null);
                PumpFor(80);
                Assert(controller.Catalog.Snapshot().Sum(d => d.Desktops.Count) == countBeforeStaleCreate,
                    "old overlay drag cannot create a new desktop", results);
                overlays.Forms.Single(f => f.DisplayId == initial.DisplayId).ChooseDesktop(second);
                PumpFor(140);
                Assert(!overlays.Visible && overlays.Forms.Count == 0, "desktop selection dismisses all overlays", results);
                Assert(controller.Catalog.Snapshot().Single(d => d.Id == initial.DisplayId).ActiveDesktopId == second
                    && controller.Catalog.Snapshot().Where(d => d.Id != initial.DisplayId).All(d => d.ActiveDesktopId == unchanged[d.Id]),
                    "overlay selection switches exactly one monitor", results);
                WaitUntil(() => primaryHandles.All(h => !NativeWindows.IsVisible(h)), "overlay-selected desktop visibility");
                Assert(secondaryHandles.All(NativeWindows.IsVisible), "overlay switch leaves other monitor windows visible", results);
                overlays.Show();
                PumpFor(120);
                var hiddenPreview = overlays.Forms.Single(f => f.DisplayId == initial.DisplayId);
                hiddenPreview.MoveSelection(-1);
                hiddenPreview.Refresh();
                Assert(hiddenPreview.HasCachedPreview(root) && hiddenPreview.LivePreviewCount == 0
                    && primaryHandles.All(h => !NativeWindows.IsVisible(h)),
                    "inactive desktop preview uses cached content without revealing hidden windows", results);
                using (var cachedImage = new Bitmap(hiddenPreview.Width, hiddenPreview.Height))
                {
                    hiddenPreview.DrawToBitmap(cachedImage, new Rectangle(Point.Empty, hiddenPreview.Size));
                    cachedImage.Save(Path.Combine(output, "overlay-hidden-desktop.png"), ImageFormat.Png);
                }
                overlays.Dismiss(false);
                controller.Switch(initial.DisplayId, initial.DesktopId);
                WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "post-overlay fixture restoration");
                PumpFor(150); controller.Refresh();
                overlays.Toggle();
                overlays.Toggle();
                PumpFor(100);
                Assert(!overlays.Visible && overlays.Forms.Count == 0, "repeated overlay shortcut toggles all screens closed", results);
            }

            using (var dispatcher = new Control())
            using (var focusProbe = new Form { Text = "BetterVirtualDesktop · focus test", Size = new Size(260, 100), ShowInTaskbar = false })
            {
                _ = dispatcher.Handle;
                using var desktopNames = new DesktopNameToastCoordinator(controller, dispatcher);
                focusProbe.Show();
                focusProbe.Activate();
                PumpFor(80);
                long foregroundBefore = NativeWindows.ForegroundWindow;
                controller.Switch(initial.DisplayId, second);
                PumpFor(120);
                var toast = desktopNames.VisibleForms.Single();
                Assert(toast.DisplayId == initial.DisplayId && NativeWindows.ForegroundWindow == foregroundBefore,
                    "desktop name notification appears only on switched monitor without taking focus", results);
                var workArea = Screen.AllScreens.Single(s => s.DeviceName == initial.DisplayId).WorkingArea;
                Assert(workArea.Contains(toast.Bounds), "desktop name notification stays within monitor work area", results);
                int rightPosition = toast.Left;
                desktopNames.Corner = DesktopToastCorner.TopLeft;
                Assert(toast.Left < rightPosition && workArea.Contains(toast.Bounds), "notification corner can change to top-left", results);
                controller.Switch(initial.DisplayId, initial.DesktopId);
                controller.RenameDesktop(initial.DisplayId, initial.DesktopId, "개발 작업 · 현재 데스크톱");
                PumpFor(100);
                toast = desktopNames.VisibleForms.Single();
                Assert(toast.DesktopName == "개발 작업 · 현재 데스크톱", "rapid switching and renaming show the latest desktop name", results);
                toast.Refresh();
                using (var bitmap = new Bitmap(toast.Width, toast.Height))
                {
                    toast.DrawToBitmap(bitmap, new Rectangle(Point.Empty, toast.Size));
                    bitmap.Save(Path.Combine(output, "desktop-name-toast.png"), ImageFormat.Png);
                }
                PumpFor(1000);
                controller.Refresh();
                PumpFor(1000);
                Assert(desktopNames.VisibleForms.Count == 0, "notification expires even while window discovery refreshes", results);
                focusProbe.Hide();
            }

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
            File.WriteAllText(Path.Combine(output, "test-stage.txt"), "Completed");
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

    private static void RunCrossDisplayMoves(DesktopController controller, RecoveryJournal journal, FixtureInfo fixture,
        string sourceDisplayId, Guid sourceDesktopId, List<string> results)
    {
        if (Screen.AllScreens.Length < 2)
        {
            results.Add("SKIP cross-display fixture movement requires two physical displays");
            return;
        }

        long root = fixture.Handles[0];
        long minimized = fixture.Handles[1];
        long dialog = fixture.Handles[2];
        long[] group = [root, dialog];
        var destination = controller.Catalog.Snapshot().First(d => d.Id != sourceDisplayId);
        var originalActive = controller.Catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
        var unrelatedAssignments = fixture.Handles.Skip(3).ToDictionary(h => h, h => controller.Catalog.GetAssignment(h));

        bool Assigned(long handle, string displayId, Guid desktopId) =>
            controller.Catalog.GetAssignment(handle) is { } assignment
                && assignment.DisplayId == displayId && assignment.DesktopId == desktopId;
        bool ActiveDesktopsUnchanged() => controller.Catalog.Snapshot().All(d => originalActive[d.Id] == d.ActiveDesktopId);
        void RefreshSettled()
        {
            PumpFor(180);
            controller.Refresh();
            PumpFor(180);
            controller.Refresh();
        }

        var originalAssignments = fixture.Handles.ToDictionary(h => h, h => controller.Catalog.GetAssignment(h));
        var originalBounds = NativeWindows.EnumerateWindows().Where(w => group.Contains(w.Handle))
            .ToDictionary(w => w.Handle, w => w.Bounds);
        Guid removedDesktop = controller.Catalog.AddDesktop(destination.Id, "삭제된 이동 대상 검사");
        controller.RemoveDesktop(destination.Id, removedDesktop);
        Assert(!controller.MoveWindowToDisplay(root, "missing-fixture-display", destination.ActiveDesktopId)
            && !controller.MoveWindowToDisplay(root, destination.Id, removedDesktop)
            && !controller.MoveWindowToDisplay(long.MaxValue, destination.Id, destination.ActiveDesktopId),
            "cross-display move rejects missing monitors, removed desktops, and untracked windows", results);
        RefreshSettled();
        Assert(originalAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value)
            && group.All(h => NativeWindows.GetDisplayId(h) == sourceDisplayId && NativeWindows.IsVisible(h))
            && NativeWindows.EnumerateWindows().Where(w => originalBounds.ContainsKey(w.Handle))
                .All(w => w.Bounds == originalBounds[w.Handle])
            && ActiveDesktopsUnchanged() && journal.Entries.Count == 0,
            "invalid cross-display requests leave fixture positions, assignments, and desktop state unchanged", results);

        // Start from the child dialog: selecting any member must move its complete owner group.
        Assert(controller.MoveWindowToDisplay(dialog, destination.Id, destination.ActiveDesktopId),
            "cross-display move accepts an owned dialog", results);
        WaitUntil(() => group.All(h => NativeWindows.GetDisplayId(h) == destination.Id && NativeWindows.IsVisible(h)),
            "owner group reaches destination monitor");
        RefreshSettled();
        Assert(group.All(h => Assigned(h, destination.Id, destination.ActiveDesktopId)),
            "owner and owned dialog retain destination assignment after discovery refresh", results);
        Assert(ActiveDesktopsUnchanged(), "cross-display movement preserves every monitor's active desktop", results);
        Assert(NativeWindows.IsVisible(minimized) && NativeWindows.IsMinimized(minimized)
            && Assigned(minimized, sourceDisplayId, sourceDesktopId)
            && unrelatedAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value && NativeWindows.IsVisible(item.Key)),
            "cross-display group movement leaves unrelated fixture windows unchanged", results);

        Assert(controller.MoveWindowToDisplay(root, sourceDisplayId, sourceDesktopId),
            "owner group can return to its source monitor", results);
        WaitUntil(() => group.All(h => NativeWindows.GetDisplayId(h) == sourceDisplayId && NativeWindows.IsVisible(h)),
            "owner group returns to source monitor");
        RefreshSettled();

        Assert(SetFixtureShowState(fixture, root, 3), "fixture root accepts maximized-state setup", results);
        WaitUntil(() => NativeWindows.IsMaximized(root), "fixture root maximizes");
        controller.Refresh();
        Assert(controller.MoveWindowToDisplay(root, destination.Id, destination.ActiveDesktopId),
            "cross-display move accepts a maximized owner group", results);
        WaitUntil(() => group.All(h => NativeWindows.GetDisplayId(h) == destination.Id && NativeWindows.IsVisible(h))
            && NativeWindows.IsMaximized(root), "maximized owner group reaches destination monitor");
        RefreshSettled();
        Assert(NativeWindows.IsMaximized(root) && group.All(h => Assigned(h, destination.Id, destination.ActiveDesktopId))
            && ActiveDesktopsUnchanged(),
            "maximized owner keeps its state and destination assignment after refresh", results);
        Assert(controller.MoveWindowToDisplay(root, sourceDisplayId, sourceDesktopId),
            "maximized owner group can return to its source monitor", results);
        WaitUntil(() => group.All(h => NativeWindows.GetDisplayId(h) == sourceDisplayId && NativeWindows.IsVisible(h))
            && NativeWindows.IsMaximized(root), "maximized owner group returns to source monitor");
        Assert(SetFixtureShowState(fixture, root, 9), "fixture root accepts normal-state restoration", results);
        WaitUntil(() => !NativeWindows.IsMaximized(root) && NativeWindows.IsVisible(root)
            && NativeWindows.GetDisplayId(root) == sourceDisplayId, "fixture root restores on its original monitor");
        RefreshSettled();

        // Add an inactive target without switching either display as part of fixture setup.
        Guid inactive = controller.Catalog.AddDesktop(destination.Id, "모니터 이동 검사");
        Assert(controller.MoveWindowToDisplay(root, destination.Id, inactive),
            "cross-display move accepts an inactive destination desktop", results);
        WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "inactive destination hides owner group");
        RefreshSettled();
        Assert(group.All(h => Assigned(h, destination.Id, inactive)) && ActiveDesktopsUnchanged(),
            "inactive destination assignment survives refresh without switching either monitor", results);

        Assert(controller.MoveWindowToDisplay(minimized, destination.Id, inactive),
            "cross-display move accepts a minimized fixture window", results);
        WaitUntil(() => !NativeWindows.IsVisible(minimized), "inactive destination hides minimized fixture");
        RefreshSettled();
        Assert(Assigned(minimized, destination.Id, inactive) && ActiveDesktopsUnchanged(),
            "minimized window retains inactive destination assignment after refresh", results);

        controller.Switch(destination.Id, inactive);
        WaitUntil(() => fixture.Handles.Take(3).All(NativeWindows.IsVisible), "destination switch restores moved fixture windows");
        RefreshSettled();
        Assert(group.All(h => NativeWindows.GetDisplayId(h) == destination.Id)
            && fixture.Handles.Take(3).All(h => Assigned(h, destination.Id, inactive)),
            "destination switch reveals the moved owner group on its physical monitor", results);
        Assert(NativeWindows.IsMinimized(minimized), "cross-display move and hide/show preserve minimized state", results);
        Assert(controller.Catalog.Snapshot().Single(d => d.Id == sourceDisplayId).ActiveDesktopId == sourceDesktopId,
            "revealing moved windows leaves source monitor desktop unchanged", results);

        Assert(controller.MoveWindowToDisplay(root, sourceDisplayId, sourceDesktopId)
            && controller.MoveWindowToDisplay(minimized, sourceDisplayId, sourceDesktopId),
            "cross-display fixtures return to their original desktops", results);
        WaitUntil(() => group.All(h => NativeWindows.GetDisplayId(h) == sourceDisplayId && NativeWindows.IsVisible(h))
            && NativeWindows.IsVisible(minimized), "cross-display fixture return");
        controller.Switch(destination.Id, destination.ActiveDesktopId);
        controller.RemoveDesktop(destination.Id, inactive);
        WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible), "cross-display fixture cleanup");
        RefreshSettled();
        Assert(fixture.Handles.Take(3).All(h => Assigned(h, sourceDisplayId, sourceDesktopId))
            && unrelatedAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value)
            && ActiveDesktopsUnchanged() && NativeWindows.IsMinimized(minimized) && journal.Entries.Count == 0,
            "cross-display test restores original fixture assignments, desktops, and recovery state", results);
    }

    private static void RunMoveToNewDesktop(DesktopController controller, RecoveryJournal journal, FixtureInfo fixture,
        string sourceDisplayId, Guid sourceDesktopId, List<string> results)
    {
        long root = fixture.Handles[0];
        long minimized = fixture.Handles[1];
        long dialog = fixture.Handles[2];
        long[] group = [root, dialog];
        var initial = controller.Catalog.Snapshot();
        var originalActive = initial.ToDictionary(d => d.Id, d => d.ActiveDesktopId);
        var originalCounts = initial.ToDictionary(d => d.Id, d => d.Desktops.Count);
        var originalAssignments = fixture.Handles.ToDictionary(h => h, h => controller.Catalog.GetAssignment(h));

        bool Assigned(long handle, string displayId, Guid desktopId) =>
            controller.Catalog.GetAssignment(handle) is { } assignment
                && assignment.DisplayId == displayId && assignment.DesktopId == desktopId;
        bool ActiveDesktopsUnchanged() => controller.Catalog.Snapshot().All(d => d.ActiveDesktopId == originalActive[d.Id]);
        bool OriginalDesktopCounts() => controller.Catalog.Snapshot().All(d => d.Desktops.Count == originalCounts[d.Id]);
        void RefreshSettled()
        {
            PumpFor(180);
            controller.Refresh();
            PumpFor(180);
            controller.Refresh();
        }
        void ReturnToSource(long handle, Guid created, string displayId)
        {
            if (!controller.MoveWindowToDisplay(handle, sourceDisplayId, sourceDesktopId))
                throw new InvalidOperationException("New-desktop fixture could not return to its original desktop.");
            WaitUntil(() => fixture.Handles.All(NativeWindows.IsVisible)
                && group.All(h => NativeWindows.GetDisplayId(h) == sourceDisplayId), "new-desktop fixture return");
            RefreshSettled();
            controller.RemoveDesktop(displayId, created);
            RefreshSettled();
        }

        Assert(controller.MoveWindowToNewDesktop(long.MaxValue, sourceDisplayId) is null
            && controller.MoveWindowToNewDesktop(dialog, "missing-fixture-display") is null
            && OriginalDesktopCounts() && ActiveDesktopsUnchanged()
            && originalAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value)
            && journal.Entries.Count == 0,
            "new-desktop move rejects invalid windows and displays without creating empty desktops", results);

        // Corrupt only this fixture's visible-window marker to exercise recovery preflight failure.
        if (!journal.PrepareHide(controller.Windows[root]))
            throw new InvalidOperationException("Could not prepare the fixture recovery-failure probe.");
        NativeWindows.ClearRecoveryMarker(root);
        try
        {
            Assert(controller.MoveWindowToNewDesktop(root, sourceDisplayId) is null
                && OriginalDesktopCounts() && ActiveDesktopsUnchanged() && fixture.Handles.All(NativeWindows.IsVisible)
                && originalAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value),
                "failed new-desktop recovery preflight leaves no empty desktop and keeps the complete group visible", results);
        }
        finally { journal.PruneClosed(); }

        Guid sameDisplay = controller.MoveWindowToNewDesktop(dialog, sourceDisplayId)
            ?? throw new InvalidOperationException("New-desktop move rejected the fixture dialog.");
        WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h)), "new desktop hides selected dialog and owner");
        RefreshSettled();
        Assert(group.All(h => Assigned(h, sourceDisplayId, sameDisplay))
            && controller.Catalog.Snapshot().Single(d => d.Id == sourceDisplayId).Desktops.Count == originalCounts[sourceDisplayId] + 1
            && ActiveDesktopsUnchanged() && NativeWindows.IsVisible(minimized) && NativeWindows.IsMinimized(minimized)
            && fixture.Handles.Skip(3).All(NativeWindows.IsVisible) && journal.Entries.Count == group.Length,
            "new-desktop move from an owned dialog creates one inactive desktop and hides its complete owner group", results);
        ReturnToSource(root, sameDisplay, sourceDisplayId);
        Assert(OriginalDesktopCounts() && ActiveDesktopsUnchanged()
            && originalAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value)
            && journal.Entries.Count == 0,
            "same-display new-desktop owner group returns without leaving recovery entries or desktops", results);

        Guid minimizedDesktop = controller.MoveWindowToNewDesktop(minimized, sourceDisplayId)
            ?? throw new InvalidOperationException("New-desktop move rejected the minimized fixture.");
        WaitUntil(() => !NativeWindows.IsVisible(minimized), "new desktop hides minimized fixture");
        RefreshSettled();
        Assert(Assigned(minimized, sourceDisplayId, minimizedDesktop) && ActiveDesktopsUnchanged()
            && group.All(NativeWindows.IsVisible) && journal.Entries.Count == 1,
            "new-desktop move hides a minimized window without changing active desktops or unrelated windows", results);
        ReturnToSource(minimized, minimizedDesktop, sourceDisplayId);
        Assert(NativeWindows.IsMinimized(minimized) && OriginalDesktopCounts() && ActiveDesktopsUnchanged()
            && Assigned(minimized, sourceDisplayId, sourceDesktopId) && journal.Entries.Count == 0,
            "returning from a newly created desktop preserves the minimized state", results);

        var destination = initial.FirstOrDefault(d => d.Id != sourceDisplayId);
        if (destination is null)
        {
            results.Add("SKIP cross-display new-desktop fixture movement requires two physical displays");
            return;
        }
        Guid crossDisplay = controller.MoveWindowToNewDesktop(dialog, destination.Id)
            ?? throw new InvalidOperationException("New-desktop move rejected the destination display.");
        WaitUntil(() => group.All(h => !NativeWindows.IsVisible(h) && NativeWindows.GetDisplayId(h) == destination.Id),
            "new desktop physically moves and hides owner group on destination monitor");
        RefreshSettled();
        Assert(group.All(h => Assigned(h, destination.Id, crossDisplay)) && ActiveDesktopsUnchanged()
            && controller.Catalog.Snapshot().Single(d => d.Id == destination.Id).Desktops.Count == originalCounts[destination.Id] + 1
            && fixture.Handles.Where(h => !group.Contains(h)).All(NativeWindows.IsVisible),
            "cross-display new-desktop move creates one inactive target and preserves every monitor's active desktop", results);
        ReturnToSource(root, crossDisplay, destination.Id);
        Assert(OriginalDesktopCounts() && ActiveDesktopsUnchanged() && NativeWindows.IsMinimized(minimized)
            && originalAssignments.All(item => controller.Catalog.GetAssignment(item.Key) == item.Value)
            && journal.Entries.Count == 0,
            "new-desktop move tests restore every fixture assignment, desktop count, and recovery entry", results);
    }

    private static bool SetFixtureShowState(FixtureInfo fixture, long handle, int command)
    {
        // This test-only helper can never act on a window outside the dedicated fixture process.
        if (!fixture.Handles.Contains(handle) || !NativeWindows.EnumerateWindows()
            .Any(w => w.Handle == handle && w.ProcessId == fixture.ProcessId)) return false;
        return ShowWindowAsync(new nint(handle), command);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint handle, int command);

    private static void Assert(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS " + name);
    }

    private static void DispatchDrop(DisplayOverlayForm target, IDataObject data, Guid? desktop)
    {
        target.Refresh();
        var bounds = desktop is Guid id ? target.DesktopCardBounds(id) : target.NewDesktopCardBounds;
        var point = target.PointToScreen(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        var args = new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
        typeof(DisplayOverlayForm).GetMethod("OnDragEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, [args]);
        typeof(DisplayOverlayForm).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, [args]);
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
