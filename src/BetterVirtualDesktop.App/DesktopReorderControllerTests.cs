using System.Text.Json;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>Reorders isolated logical desktops and hidden management controls without managing real windows.</summary>
internal static class DesktopReorderControllerTests
{
    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string language = UiText.SelectedLanguageCode;
        var journal = new RecoveryJournal(Path.Combine(output, $"reorder-controller-{Guid.NewGuid():N}.json"));
        int visibilityChecks = 0;
        try
        {
            UiText.SetLanguage("en", persist: false);
            var controller = new DesktopController(journal, include: _ => false,
                canHide: () => { visibilityChecks++; return true; });
            Check(controller.Windows.Count == 0 && journal.Entries.Count == 0,
                "reorder controller checks exclude all external windows", results);
            CheckController(controller, () => visibilityChecks, results);
            CheckManagementMenus(controller, () => visibilityChecks, results);
            Check(journal.Entries.Count == 0,
                "reordering never adds a native window to the recovery journal", results);
        }
        finally
        {
            UiText.SetLanguage(language, persist: false);
            journal.DeleteIfEmpty();
        }
    }

    private static void CheckController(DesktopController controller, Func<int> visibilityChecks, List<string> results)
    {
        const string primary = "reorder-controller-primary";
        const string secondary = "reorder-controller-secondary";
        controller.Catalog.SynchronizeDisplays([
            new DisplayDescriptor(primary, "Reorder primary", true),
            new DisplayDescriptor(secondary, "Reorder secondary")
        ]);
        Guid first = Display(primary).ActiveDesktopId;
        Guid second = controller.Catalog.AddDesktop(primary);
        Guid third = controller.Catalog.AddDesktop(primary);
        Guid peerFirst = Display(secondary).ActiveDesktopId;
        Guid peerSecond = controller.Catalog.AddDesktop(secondary, "Peer research");
        Guid peerThird = controller.Catalog.AddDesktop(secondary, "Peer communication");
        controller.Catalog.RenameDesktop(primary, third, "Research");
        controller.Catalog.SwitchToDesktop(secondary, peerSecond);
        // Logical IDs only: these handles are never inserted into the native controller's window dictionary.
        controller.Catalog.TrackWindow(71001, primary);
        controller.Catalog.TrackWindow(71002, primary);
        controller.Catalog.MoveWindowToDesktop(71002, second);
        controller.Catalog.TrackWindow(71003, secondary);
        long[] handles = [71001, 71002, 71003];
        var assignments = handles.Select(controller.Catalog.GetAssignment).ToArray();
        var names = Display(primary).Desktops.ToDictionary(d => d.Id, d => (d.Name, d.DefaultNumber));
        string peerBefore = JsonSerializer.Serialize(Display(secondary));
        long generation = controller.VisibilityGeneration;
        int initialVisibilityChecks = visibilityChecks();
        int changed = 0;
        int hideRequests = 0;
        controller.Changed += OnChanged;
        controller.WindowHideRequested += OnHide;
        try
        {
            Check(controller.ReorderDesktop(primary, second, 0)
                && Order(primary).SequenceEqual(new[] { second, first, third }),
                "moving desktop 2 to the first position inserts it before desktop 1", results);
            Check(Display(primary).ActiveDesktopId == first && changed == 1
                && controller.VisibilityGeneration == generation && visibilityChecks() == initialVisibilityChecks
                && hideRequests == 0,
                "reordering publishes one change while keeping the active desktop and native visibility unchanged", results);
            Check(Display(primary).Desktops.All(d => names[d.Id] == (d.Name, d.DefaultNumber))
                && handles.Select(controller.Catalog.GetAssignment).SequenceEqual(assignments)
                && UiText.DesktopName(Display(primary).Desktops[0]) == UiText.Get("DesktopDefault", 2)
                && UiText.DesktopName(Display(primary).Desktops[2]) == "Research",
                "generated numbers, custom names, desktop IDs and window assignments stay attached to their desktops", results);
            Check(JsonSerializer.Serialize(Display(secondary)) == peerBefore,
                "reordering one display leaves the other display's complete desktop state unchanged", results);

            foreach (var request in new (string Display, Guid Desktop, int Index, string Description)[]
            {
                (primary, second, 0, "same position"),
                (primary, second, -1, "negative position"),
                (primary, second, 3, "position beyond the last desktop"),
                (primary, second, int.MinValue, "minimum integer position"),
                (primary, second, int.MaxValue, "maximum integer position"),
                ("unknown-reorder-display", second, 0, "missing display"),
                (primary, Guid.NewGuid(), 0, "missing desktop"),
                (primary, peerFirst, 0, "desktop belonging to another display")
            })
            {
                string before = State(controller);
                Check(!controller.ReorderDesktop(request.Display, request.Desktop, request.Index)
                    && State(controller) == before && changed == 1
                    && controller.VisibilityGeneration == generation && visibilityChecks() == initialVisibilityChecks,
                    $"invalid or unchanged reorder is harmless: {request.Description}", results);
            }

            Check(controller.ReorderDesktop(primary, second, 2)
                && Order(primary).SequenceEqual(new[] { first, third, second }) && changed == 2,
                "moving from the first position to the last uses the final destination index", results);
            controller.SwitchingMode = DisplaySwitchingMode.Linked;
            Check(controller.ReorderDesktop(primary, first, 2)
                && Order(primary).SequenceEqual(new[] { third, second, first })
                && Display(primary).ActiveDesktopId == first
                && JsonSerializer.Serialize(Display(secondary)) == peerBefore
                && changed == 3 && controller.VisibilityGeneration == generation
                && visibilityChecks() == initialVisibilityChecks && hideRequests == 0,
                "linked switching mode still reorders only the selected display without synchronizing active desktops", results);
            Check(handles.Select(controller.Catalog.GetAssignment).SequenceEqual(assignments),
                "reordering an active desktop does not redistribute its or another display's windows", results);

            controller.SwitchingMode = DisplaySwitchingMode.Independent;
            controller.WrapDesktopSwitching = false;
            controller.SwitchRelative(primary, -1);
            Check(Display(primary).ActiveDesktopId == second && Display(secondary).ActiveDesktopId == peerSecond,
                "previous-desktop navigation follows the reordered list rather than generated desktop numbers", results);
            controller.SwitchRelative(primary, -1);
            Check(Display(primary).ActiveDesktopId == third,
                "reordered custom-named desktops participate in relative navigation", results);
            generation = controller.VisibilityGeneration;
            int beforeChanges = changed;
            string? boundaryDisplay = null;
            controller.SwitchBoundaryReached += OnBoundary;
            try
            {
                controller.SwitchRelative(primary, -1);
                Check(Display(primary).ActiveDesktopId == third && boundaryDisplay == primary
                    && controller.VisibilityGeneration == generation && changed == beforeChanges,
                    "bounded navigation treats the new first position as the boundary", results);
            }
            finally { controller.SwitchBoundaryReached -= OnBoundary; }
            controller.SwitchingMode = DisplaySwitchingMode.Linked;
            controller.SwitchRelative(primary, 1);
            Check(Display(primary).ActiveDesktopId == second && Display(secondary).ActiveDesktopId == peerSecond,
                "linked navigation selects matching positions after one display is reordered", results);
            controller.SwitchRelative(primary, 1);
            Check(Display(primary).ActiveDesktopId == first && Display(secondary).ActiveDesktopId == peerThird,
                "linked navigation reaches the reordered last position without matching desktop names", results);

            void OnBoundary(string id) => boundaryDisplay = id;
        }
        finally
        {
            controller.Changed -= OnChanged;
            controller.WindowHideRequested -= OnHide;
        }

        void OnChanged() => changed++;
        void OnHide(long _) => hideRequests++;
        DisplaySnapshot Display(string id) => controller.Catalog.Snapshot().Single(d => d.Id == id);
        IEnumerable<Guid> Order(string id) => Display(id).Desktops.Select(d => d.Id);
    }

    private static void CheckManagementMenus(DesktopController controller, Func<int> visibilityChecks, List<string> results)
    {
        const string displayId = "reorder-management";
        controller.Catalog.SynchronizeDisplays([new DisplayDescriptor(displayId, "Management test", true)]);
        Guid first = controller.Catalog.Snapshot().Single().ActiveDesktopId;
        controller.Catalog.RenameDesktop(displayId, first, "Planning");
        Guid second = controller.Catalog.AddDesktop(displayId, "Research");
        Guid third = controller.Catalog.AddDesktop(displayId, "Communication");
        using var form = new OverviewForm(controller);
        form.UpdateContent(true);
        Check(!Menu(first, "Reorder.MoveLeft").Enabled && Menu(first, "Reorder.MoveRight").Enabled
            && Menu(second, "Reorder.MoveLeft").Enabled && Menu(second, "Reorder.MoveRight").Enabled
            && Menu(third, "Reorder.MoveLeft").Enabled && !Menu(third, "Reorder.MoveRight").Enabled,
            "management desktop menus disable movement beyond either end of the list", results);
        long generation = controller.VisibilityGeneration;
        int beforeVisibilityChecks = visibilityChecks();
        Menu(second, "Reorder.MoveLeft").PerformClick();
        form.UpdateContent(true);
        Check(Order().SequenceEqual(new[] { second, first, third })
            && controller.Catalog.Snapshot().Single().ActiveDesktopId == first
            && !Menu(second, "Reorder.MoveLeft").Enabled && Menu(second, "Reorder.MoveRight").Enabled,
            "management move-left action reorders the selected desktop and refreshes boundary availability", results);
        Menu(second, "Reorder.MoveRight").PerformClick();
        form.UpdateContent(true);
        Check(Order().SequenceEqual(new[] { first, second, third })
            && controller.VisibilityGeneration == generation && visibilityChecks() == beforeVisibilityChecks
            && !form.Visible,
            "management move-right action restores order without showing the form or switching desktops", results);
        var staleMoveLeft = Menu(second, "Reorder.MoveLeft");
        controller.ReorderDesktop(displayId, second, 2);
        staleMoveLeft.PerformClick();
        form.UpdateContent(true);
        Check(Order().SequenceEqual(new[] { first, second, third })
            && controller.VisibilityGeneration == generation && visibilityChecks() == beforeVisibilityChecks,
            "an existing management menu moves relative to the desktop's current position after another reorder", results);
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            form.ApplyLanguage();
            string[] keys = ["Reorder.MoveLeft", "Reorder.MoveRight", "Reorder.Hint", "Reorder.Succeeded"];
            Check(keys.All(key => UiText.HasTranslation(language.Code, key))
                && Menu(second, "Reorder.MoveLeft").Text == UiText.Get("Reorder.MoveLeft")
                && Menu(second, "Reorder.MoveRight").Text == UiText.Get("Reorder.MoveRight"),
                $"reorder instructions and management actions are translated: {language.Code}", results);
        }

        IEnumerable<Guid> Order() => controller.Catalog.Snapshot().Single().Desktops.Select(d => d.Id);
        ToolStripMenuItem Menu(Guid desktopId, string name)
        {
            var display = controller.Catalog.Snapshot().Single();
            var desktop = display.Desktops.Single(d => d.Id == desktopId);
            string accessibleName = UiText.Get("Overview.DesktopAccessible", UiText.DisplayName(display),
                UiText.DesktopName(desktop), UiText.Get(display.ActiveDesktopId == desktopId ? "Overview.Active" : "Overview.Switch"));
            return Descendants(form).Single(control => control.AccessibleName == accessibleName)
                .ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == name);
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string State(DesktopController controller) => JsonSerializer.Serialize(controller.Catalog.Snapshot());

    private static void Check(bool condition, string description, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(description);
        results.Add("PASS " + description);
    }
}
