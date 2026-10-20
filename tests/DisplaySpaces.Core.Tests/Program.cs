using System.Text.Json;
using DisplaySpaces.Core;

var tests = new (string Name, Action Run)[]
{
    ("Switching one display leaves the other display visible", IndependentDisplays),
    ("Window rediscovery and same-display movement preserve inactive assignments", Rediscovery),
    ("Disconnect migrates every orphaned window to the primary active desktop", Disconnect),
    ("Topology updates preserve desktops and use a fallback when no display is primary", TopologyUpdates),
    ("Removing active and inactive desktops migrates windows correctly", RemoveDesktops),
    ("Window movement respects display ownership and active destination", MoveWindows),
    ("Cross-display group movement is atomic and preserves active desktops", MoveWindowGroups),
    ("Relative switching wraps safely for every signed offset", RelativeSwitches),
    ("Bounded switching stops at both ends and moves between interior desktops", BoundedRelativeSwitches),
    ("Bounded switching safely clamps extreme offsets", BoundedExtremeOffsets),
    ("Bounded switching preserves other displays, assignments, and singleton desktops", BoundedSwitchIsolation),
    ("Independent card and relative switches never change peer displays", ExplicitIndependentSwitching),
    ("Linked card switching matches ordinal positions and preserves existing desktops", LinkedCardSwitching),
    ("Linked switching creates only the desktops needed for the selected position", LinkedDesktopCreation),
    ("Linked relative switching follows its source and the longest display list", LinkedRelativeAnchor),
    ("Linked relative wrapping and bounds handle every signed offset", LinkedRelativeOffsets),
    ("Linked relative no-ops leave divergent displays completely unchanged", LinkedRelativeNoOps),
    ("Linked switching changes visibility without moving windows", LinkedWindowAssignments),
    ("Invalid linked switching requests are atomic", LinkedInvalidRequests),
    ("Invalid mutations leave all state unchanged", InvalidMutations),
    ("Snapshots remain stable after later mutations", DetachedSnapshots),
    ("Forget removes closed windows and tolerates repeated removal", ForgetWindows),
    ("Desktop names stay stable and explicit names are trimmed", DesktopNames),
    ("Reordering desktop 2 before desktop 1 preserves content and visible windows", ReorderAdjacentDesktops),
    ("Reordering inserts at the final index in either direction", ReorderNonadjacentDesktops),
    ("Reordered navigation follows the list while keeping peer displays independent", ReorderNavigation),
    ("Linked switching uses reordered positions without implicitly reordering peers", ReorderLinkedNavigation),
    ("Invalid and unchanged desktop reorder requests leave state untouched", ReorderInvalidAndNoOp),
    ("Desktop reordering leaves previous snapshots and generated names stable", ReorderSnapshotsAndNames),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static WorkspaceCatalog NewCatalog()
{
    var catalog = new WorkspaceCatalog();
    catalog.SynchronizeDisplays([new("left", "Left", true), new("right", "Right")]);
    return catalog;
}

static DisplaySnapshot Display(WorkspaceCatalog catalog, string id) => catalog.Snapshot().Single(d => d.Id == id);

static WorkspaceCatalog ThreeDisplayCatalog()
{
    var catalog = new WorkspaceCatalog();
    catalog.SynchronizeDisplays([new("left", "Left", true), new("right", "Right"), new("third", "Third")]);
    return catalog;
}

static int ActiveIndex(DisplaySnapshot display) =>
    display.Desktops.ToList().FindIndex(d => d.Id == display.ActiveDesktopId);

static void AllAtIndex(WorkspaceCatalog catalog, int expected)
{
    foreach (var display in catalog.Snapshot())
        Equal(expected, ActiveIndex(display));
}

static void IndependentDisplays()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    catalog.TrackWindow(102, "right");
    var originalRight = Display(catalog, "right").ActiveDesktopId;
    var secondLeft = catalog.AddDesktop("left");
    catalog.SwitchToDesktop("left", secondLeft);
    False(catalog.IsWindowVisible(101));
    True(catalog.IsWindowVisible(102));
    Equal(originalRight, Display(catalog, "right").ActiveDesktopId);
    catalog.TrackWindow(103, "left");
    Equal(secondLeft, catalog.GetAssignment(103)!.DesktopId);
    catalog.SwitchRelative("left", -1);
    True(catalog.IsWindowVisible(101));
    False(catalog.IsWindowVisible(103));
    True(catalog.IsWindowVisible(102));
}

static void Rediscovery()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    var original = catalog.GetAssignment(101);
    catalog.SwitchToDesktop("left", catalog.AddDesktop("left"));
    catalog.TrackWindow(101, "right");
    catalog.AssignToDisplay(101, "left");
    Equal(original, catalog.GetAssignment(101));
    False(catalog.IsWindowVisible(101));
}

static void Disconnect()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "right");
    catalog.SwitchToDesktop("right", catalog.AddDesktop("right"));
    catalog.TrackWindow(102, "right");
    catalog.TrackWindow(103, "left");
    var oldLeft = catalog.GetAssignment(103);
    var newLeft = catalog.AddDesktop("left");
    catalog.SwitchToDesktop("left", newLeft);
    catalog.SynchronizeDisplays([new("extra", "Extra"), new("left", "Left renamed", true)]);
    Equal(2, catalog.Snapshot().Count);
    foreach (var window in new long[] { 101, 102 })
    {
        Equal(new WindowAssignment(window, "left", newLeft), catalog.GetAssignment(window));
        True(catalog.IsWindowVisible(window));
    }
    Equal(oldLeft, catalog.GetAssignment(103));
    False(catalog.IsWindowVisible(103));
    Equal("Left renamed", Display(catalog, "left").Name);
}

static void TopologyUpdates()
{
    var catalog = NewCatalog();
    var secondRight = catalog.AddDesktop("right", "Research");
    catalog.SwitchToDesktop("right", secondRight);
    catalog.TrackWindow(101, "left");
    catalog.SynchronizeDisplays([new("right", "Portrait"), new("third", "Third")]);
    Equal(secondRight, Display(catalog, "right").ActiveDesktopId);
    Equal(2, Display(catalog, "right").Desktops.Count);
    Equal("Portrait", Display(catalog, "right").Name);
    Equal(new WindowAssignment(101, "right", secondRight), catalog.GetAssignment(101));
    Equal(1, Display(catalog, "third").Desktops.Count);
    catalog.SynchronizeDisplays([new("replacement", "Replacement", true)]);
    Equal("replacement", catalog.GetAssignment(101)!.DisplayId);
    True(catalog.IsWindowVisible(101));
}

static void RemoveDesktops()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left");
    catalog.TrackWindow(101, "left");
    catalog.SwitchToDesktop("left", second);
    catalog.TrackWindow(102, "left");
    catalog.SwitchToDesktop("left", third);
    catalog.TrackWindow(103, "left");
    catalog.RemoveDesktop("left", second);
    Equal(first, catalog.GetAssignment(102)!.DesktopId);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.RemoveDesktop("left", first);
    Equal(third, catalog.GetAssignment(101)!.DesktopId);
    Equal(third, catalog.GetAssignment(102)!.DesktopId);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    var fourth = catalog.AddDesktop("left");
    catalog.SwitchToDesktop("left", fourth);
    catalog.TrackWindow(104, "left");
    catalog.RemoveDesktop("left", fourth);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    Equal(third, catalog.GetAssignment(104)!.DesktopId);
    Equal(4, Display(catalog, "left").Desktops.Single().WindowIds.Count);
    Throws<InvalidOperationException>(() => catalog.RemoveDesktop("left", third));
    Equal(1, Display(catalog, "left").Desktops.Count);

    var rightFirst = Display(catalog, "right").ActiveDesktopId;
    var rightSecond = catalog.AddDesktop("right");
    catalog.RemoveDesktop("right", rightFirst);
    Equal(rightSecond, Display(catalog, "right").ActiveDesktopId);
}

static void MoveWindows()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    var secondLeft = catalog.AddDesktop("left");
    var secondRight = catalog.AddDesktop("right");
    catalog.MoveWindowToDesktop(101, secondLeft);
    False(catalog.IsWindowVisible(101));
    catalog.SwitchToDesktop("right", secondRight);
    catalog.AssignToDisplay(101, "right");
    Equal(new WindowAssignment(101, "right", secondRight), catalog.GetAssignment(101));
    True(catalog.IsWindowVisible(101));
    Throws<ArgumentException>(() => catalog.MoveWindowToDesktop(101, secondLeft));
    Equal(new WindowAssignment(101, "right", secondRight), catalog.GetAssignment(101));
}

static void RelativeSwitches()
{
    var catalog = NewCatalog();
    var desktops = new[]
    {
        Display(catalog, "left").ActiveDesktopId,
        catalog.AddDesktop("left"),
        catalog.AddDesktop("left")
    };
    catalog.SwitchRelative("left", -1);
    Equal(desktops[2], Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1);
    Equal(desktops[0], Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", int.MinValue);
    Equal(desktops[1], Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", int.MaxValue);
    Equal(desktops[2], Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 0);
    Equal(desktops[2], Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("right", int.MinValue);
    Equal(Display(catalog, "right").Desktops.Single().Id, Display(catalog, "right").ActiveDesktopId);
}

static void BoundedRelativeSwitches()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left");

    catalog.SwitchRelative("left", -1, wrapAround: false);
    Equal(first, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1, wrapAround: false);
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 0, wrapAround: false);
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1, wrapAround: false);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1, wrapAround: false);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", -1, wrapAround: false);
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", -1, wrapAround: false);
    Equal(first, Display(catalog, "left").ActiveDesktopId);

    // Changing the option takes effect on the next operation in either direction.
    catalog.SwitchRelative("left", -1, wrapAround: true);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1, wrapAround: false);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", 1);
    Equal(first, Display(catalog, "left").ActiveDesktopId);
}

static void BoundedExtremeOffsets()
{
    var catalog = NewCatalog();
    var desktops = new[]
    {
        Display(catalog, "left").ActiveDesktopId,
        catalog.AddDesktop("left"),
        catalog.AddDesktop("left")
    };
    foreach (var start in desktops)
    {
        foreach (int offset in new[] { int.MinValue, -10 })
        {
            catalog.SwitchToDesktop("left", start);
            catalog.SwitchRelative("left", offset, wrapAround: false);
            Equal(desktops[0], Display(catalog, "left").ActiveDesktopId);
        }
        foreach (int offset in new[] { int.MaxValue, 10 })
        {
            catalog.SwitchToDesktop("left", start);
            catalog.SwitchRelative("left", offset, wrapAround: false);
            Equal(desktops[2], Display(catalog, "left").ActiveDesktopId);
        }
    }
}

static void BoundedSwitchIsolation()
{
    var catalog = NewCatalog();
    var right = Display(catalog, "right").ActiveDesktopId;
    catalog.TrackWindow(101, "left");
    catalog.TrackWindow(102, "right");
    var originalLeftAssignment = catalog.GetAssignment(101);
    var rightBefore = JsonSerializer.Serialize(Display(catalog, "right"));
    var secondLeft = catalog.AddDesktop("left");
    catalog.SwitchRelative("left", -1, wrapAround: false);
    True(catalog.IsWindowVisible(101));
    catalog.SwitchRelative("left", 1, wrapAround: false);
    Equal(secondLeft, Display(catalog, "left").ActiveDesktopId);
    False(catalog.IsWindowVisible(101));
    catalog.SwitchRelative("left", int.MaxValue, wrapAround: false);
    Equal(secondLeft, Display(catalog, "left").ActiveDesktopId);
    Equal(originalLeftAssignment, catalog.GetAssignment(101));
    Equal(rightBefore, JsonSerializer.Serialize(Display(catalog, "right")));
    True(catalog.IsWindowVisible(102));

    foreach (int offset in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
    {
        catalog.SwitchRelative("right", offset, wrapAround: false);
        Equal(right, Display(catalog, "right").ActiveDesktopId);
        Equal(secondLeft, Display(catalog, "left").ActiveDesktopId);
    }

    var empty = new WorkspaceCatalog();
    Throws<ArgumentException>(() => empty.SwitchRelative("missing", 1, wrapAround: false));
    Equal(0, empty.Snapshot().Count);
}

static void ExplicitIndependentSwitching()
{
    var catalog = ThreeDisplayCatalog();
    var leftSecond = catalog.AddDesktop("left");
    var rightSecond = catalog.AddDesktop("right");
    catalog.SwitchToDesktop("right", rightSecond);
    var peers = JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left"));

    catalog.SwitchToDesktop("left", leftSecond);
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));
    catalog.SwitchRelative("left", -1);
    Equal(0, ActiveIndex(Display(catalog, "left")));
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));
    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Independent);
    catalog.SwitchRelative("left", 1, mode: DisplaySwitchingMode.Independent);
    Equal(0, ActiveIndex(Display(catalog, "left")));
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));

    // Choosing Linked for one request does not alter the default for subsequent requests.
    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Linked);
    peers = JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left"));
    catalog.SwitchRelative("left", -1);
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));
}

static void LinkedCardSwitching()
{
    var catalog = ThreeDisplayCatalog();
    var leftSecond = catalog.AddDesktop("left", "Work");
    for (int index = 2; index <= 4; index++)
        catalog.AddDesktop("right", $"Right {index}");
    var before = catalog.Snapshot();

    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 1);
    Equal(2, Display(catalog, "left").Desktops.Count);
    Equal(4, Display(catalog, "right").Desktops.Count);
    Equal(2, Display(catalog, "third").Desktops.Count);
    foreach (var previous in before)
    {
        var existing = Display(catalog, previous.Id).Desktops.Take(previous.Desktops.Count);
        Equal(JsonSerializer.Serialize(previous.Desktops), JsonSerializer.Serialize(existing));
    }

    // Clicking an already-active source card still explicitly brings peers into alignment.
    catalog.SwitchToDesktop("right", Display(catalog, "right").Desktops[3].Id);
    catalog.SwitchToDesktop("third", Display(catalog, "third").Desktops[0].Id);
    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 1);
    var synchronized = JsonSerializer.Serialize(catalog.Snapshot());
    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Linked);
    Equal(synchronized, JsonSerializer.Serialize(catalog.Snapshot()));
}

static void LinkedDesktopCreation()
{
    var catalog = ThreeDisplayCatalog();
    var leftSecond = catalog.AddDesktop("left");
    catalog.AddDesktop("left");
    var rightFirst = Display(catalog, "right").ActiveDesktopId;
    catalog.RenameDesktop("right", rightFirst, "Desktop 2");
    for (int index = 2; index <= 5; index++) catalog.AddDesktop("third");

    catalog.SwitchToDesktop("left", leftSecond, DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 1);
    Equal(3, Display(catalog, "left").Desktops.Count);
    var right = Display(catalog, "right");
    Equal(2, right.Desktops.Count);
    Equal("Desktop 2", right.Desktops[0].Name);
    Equal("Desktop 3", right.Desktops[1].Name);
    Equal<int?>(3, right.Desktops[1].DefaultNumber);
    Equal(0, right.Desktops[1].WindowIds.Count);
    Equal(5, Display(catalog, "third").Desktops.Count);

    // A later target grows the short list just far enough, preserving stable names and IDs.
    var rightBefore = right.Desktops;
    catalog.SwitchToDesktop("third", Display(catalog, "third").Desktops[3].Id, DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 3);
    Equal(4, Display(catalog, "left").Desktops.Count);
    Equal(4, Display(catalog, "right").Desktops.Count);
    Equal(5, Display(catalog, "third").Desktops.Count);
    Equal(JsonSerializer.Serialize(rightBefore),
        JsonSerializer.Serialize(Display(catalog, "right").Desktops.Take(2)));
}

static void LinkedRelativeAnchor()
{
    var catalog = ThreeDisplayCatalog();
    for (int index = 2; index <= 4; index++) catalog.AddDesktop("right");
    catalog.AddDesktop("third");
    catalog.SwitchToDesktop("right", Display(catalog, "right").Desktops[2].Id);

    // The one-desktop source can advance because another display already has more desktops.
    catalog.SwitchRelative("left", 1, wrapAround: false, mode: DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 1);
    Equal(2, Display(catalog, "left").Desktops.Count);
    Equal(4, Display(catalog, "right").Desktops.Count);
    Equal(2, Display(catalog, "third").Desktops.Count);

    // An independently diverged source, rather than the primary display, anchors the next step.
    catalog.SwitchToDesktop("right", Display(catalog, "right").Desktops[3].Id);
    catalog.SwitchRelative("right", -1, wrapAround: false, mode: DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 2);
    Equal(3, Display(catalog, "left").Desktops.Count);
    Equal(3, Display(catalog, "third").Desktops.Count);

    catalog.SwitchRelative("third", 1, wrapAround: false, mode: DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 3);
    catalog.SwitchRelative("left", 1, mode: DisplaySwitchingMode.Linked);
    AllAtIndex(catalog, 0);
}

static void LinkedRelativeOffsets()
{
    foreach (bool wrap in new[] { false, true })
    foreach (int start in new[] { 0, 1, 2 })
    foreach (int offset in new[] { int.MinValue, -10, -3, -1, 0, 1, 3, 10, int.MaxValue })
    {
        var catalog = ThreeDisplayCatalog();
        catalog.AddDesktop("right");
        catalog.AddDesktop("right");
        catalog.SwitchToDesktop("right", Display(catalog, "right").Desktops[start].Id);
        string before = JsonSerializer.Serialize(catalog.Snapshot());
        long requested = (long)start + offset;
        int expected = wrap
            ? (int)((requested % 3 + 3) % 3)
            : (int)Math.Clamp(requested, 0, 2);
        catalog.SwitchRelative("right", offset, wrap, DisplaySwitchingMode.Linked);
        if (expected == start)
            Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
        else
        {
            AllAtIndex(catalog, expected);
            Equal(expected + 1, Display(catalog, "left").Desktops.Count);
            Equal(3, Display(catalog, "right").Desktops.Count);
            Equal(expected + 1, Display(catalog, "third").Desktops.Count);
        }
    }
}

static void LinkedRelativeNoOps()
{
    var catalog = ThreeDisplayCatalog();
    catalog.AddDesktop("right");
    var last = catalog.AddDesktop("right");
    catalog.SwitchToDesktop("right", last);
    var before = JsonSerializer.Serialize(catalog.Snapshot());
    foreach (int offset in new[] { int.MinValue, -1, 0 })
    {
        catalog.SwitchRelative("left", offset, wrapAround: false, mode: DisplaySwitchingMode.Linked);
        Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    }
    foreach (int offset in new[] { 0, 1, int.MaxValue })
    {
        catalog.SwitchRelative("right", offset, wrapAround: false, mode: DisplaySwitchingMode.Linked);
        Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    }
    catalog.SwitchRelative("right", 0, mode: DisplaySwitchingMode.Linked);
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    catalog.SwitchRelative("right", 3, mode: DisplaySwitchingMode.Linked);
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));

    var singleton = ThreeDisplayCatalog();
    before = JsonSerializer.Serialize(singleton.Snapshot());
    foreach (bool wrap in new[] { false, true })
    foreach (int offset in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
    {
        singleton.SwitchRelative("third", offset, wrap, DisplaySwitchingMode.Linked);
        Equal(before, JsonSerializer.Serialize(singleton.Snapshot()));
    }
}

static void LinkedWindowAssignments()
{
    var catalog = ThreeDisplayCatalog();
    long id = 100;
    foreach (string display in new[] { "left", "right", "third" })
    {
        catalog.TrackWindow(++id, display);
        var second = catalog.AddDesktop(display);
        catalog.TrackWindow(++id, display);
        catalog.MoveWindowToDesktop(id, second);
    }
    var assignments = Enumerable.Range(101, 6).Select(i => catalog.GetAssignment(i)).ToArray();
    var contents = catalog.Snapshot().ToDictionary(d => d.Id, d => JsonSerializer.Serialize(d.Desktops));
    catalog.SwitchToDesktop("third", Display(catalog, "third").Desktops[1].Id, DisplaySwitchingMode.Linked);
    for (int index = 0; index < assignments.Length; index++)
    {
        var assignment = assignments[index]!;
        Equal(assignment, catalog.GetAssignment(assignment.WindowId));
        Equal(index % 2 == 1, catalog.IsWindowVisible(assignment.WindowId));
    }
    catalog.SwitchRelative("right", -1, mode: DisplaySwitchingMode.Linked);
    for (int index = 0; index < assignments.Length; index++)
    {
        var assignment = assignments[index]!;
        Equal(assignment, catalog.GetAssignment(assignment.WindowId));
        Equal(index % 2 == 0, catalog.IsWindowVisible(assignment.WindowId));
    }
    foreach (var display in catalog.Snapshot())
        Equal(contents[display.Id], JsonSerializer.Serialize(display.Desktops));
}

static void LinkedInvalidRequests()
{
    var catalog = ThreeDisplayCatalog();
    var target = catalog.AddDesktop("left");
    catalog.AddDesktop("left");
    catalog.TrackWindow(101, "right");
    var before = JsonSerializer.Serialize(catalog.Snapshot());
    Action[] invalid =
    [
        () => catalog.SwitchToDesktop("missing", target, DisplaySwitchingMode.Linked),
        () => catalog.SwitchToDesktop("right", target, DisplaySwitchingMode.Linked),
        () => catalog.SwitchToDesktop("left", Guid.Empty, DisplaySwitchingMode.Linked),
        () => catalog.SwitchToDesktop("left", target, (DisplaySwitchingMode)99),
        () => catalog.SwitchRelative("missing", 1, mode: DisplaySwitchingMode.Linked),
        () => catalog.SwitchRelative("", 0, mode: DisplaySwitchingMode.Linked),
        () => catalog.SwitchRelative("left", 1, mode: (DisplaySwitchingMode)99),
        () => catalog.SwitchRelative("left", 0, mode: (DisplaySwitchingMode)(-1)),
    ];
    foreach (var operation in invalid)
    {
        Throws<ArgumentException>(operation);
        Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    }
    var empty = new WorkspaceCatalog();
    Throws<ArgumentException>(() => empty.SwitchRelative("missing", 1, mode: DisplaySwitchingMode.Linked));
    Throws<ArgumentException>(() => empty.SwitchToDesktop("missing", Guid.NewGuid(), DisplaySwitchingMode.Linked));
    Equal(0, empty.Snapshot().Count);
}

static void MoveWindowGroups()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    catalog.TrackWindow(102, "left");
    catalog.TrackWindow(103, "right");
    var active = catalog.Snapshot().ToDictionary(d => d.Id, d => d.ActiveDesktopId);
    var target = catalog.AddDesktop("right");
    catalog.MoveWindowsToDisplay([101, 102, 101], "right", target);
    Equal(new WindowAssignment(101, "right", target), catalog.GetAssignment(101));
    Equal(new WindowAssignment(102, "right", target), catalog.GetAssignment(102));
    False(catalog.IsWindowVisible(101));
    True(catalog.IsWindowVisible(103));
    foreach (var display in catalog.Snapshot()) Equal(active[display.Id], display.ActiveDesktopId);
    string before = JsonSerializer.Serialize(catalog.Snapshot());
    Throws<ArgumentException>(() => catalog.MoveWindowsToDisplay([101, 999], "left", active["left"]));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    Throws<ArgumentException>(() => catalog.MoveWindowsToDisplay([101, 102], "left", target));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    Throws<ArgumentException>(() => catalog.MoveWindowsToDisplay([101, 0], "right", target));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
}

static void InvalidMutations()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    var leftFirst = Display(catalog, "left").ActiveDesktopId;
    var rightFirst = Display(catalog, "right").ActiveDesktopId;
    var before = JsonSerializer.Serialize(catalog.Snapshot());
    Action[] invalidOperations =
    [
        () => catalog.SynchronizeDisplays([]),
        () => catalog.SynchronizeDisplays(null!),
        () => catalog.SynchronizeDisplays([new("new", "New"), new("new", "Duplicate")]),
        () => catalog.SynchronizeDisplays([new("left", "Changed"), new("", "Invalid")]),
        () => catalog.SynchronizeDisplays([new("left", "Changed"), null!]),
        () => catalog.SynchronizeDisplays([new("left", "Changed"), new("new", " ")]),
        () => catalog.SynchronizeDisplays(ThrowingTopology()),
        () => catalog.AddDesktop("missing"),
        () => catalog.AddDesktop("left", " "),
        () => catalog.RenameDesktop("left", leftFirst, ""),
        () => catalog.RenameDesktop("left", rightFirst, "Cross display"),
        () => catalog.SwitchToDesktop("left", rightFirst),
        () => catalog.SwitchToDesktop("left", Guid.Empty),
        () => catalog.SwitchRelative(" ", 1),
        () => catalog.RemoveDesktop("left", rightFirst),
        () => catalog.TrackWindow(0, "left"),
        () => catalog.TrackWindow(102, "missing"),
        () => catalog.AssignToDisplay(101, "missing"),
        () => catalog.AssignToDisplay(999, "right"),
        () => catalog.MoveWindowToDesktop(101, rightFirst),
        () => catalog.MoveWindowToDesktop(999, leftFirst),
        () => catalog.ForgetWindow(0),
    ];
    foreach (var action in invalidOperations)
    {
        Throws<ArgumentException>(action);
        Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    }
    Throws<InvalidOperationException>(() => catalog.RemoveDesktop("left", leftFirst));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
}

static IEnumerable<DisplayDescriptor> ThrowingTopology()
{
    yield return new("left", "Changed");
    throw new ArgumentException("Topology provider failed.");
}

static void DetachedSnapshots()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(101, "left");
    var snapshot = catalog.Snapshot();
    var serialized = JsonSerializer.Serialize(snapshot);
    catalog.RenameDesktop("left", snapshot[0].ActiveDesktopId, "New name");
    catalog.AddDesktop("left");
    catalog.ForgetWindow(101);
    Equal(serialized, JsonSerializer.Serialize(snapshot));
    Throws<NotSupportedException>(() => ((IList<DisplaySnapshot>)snapshot).Clear());
    Throws<NotSupportedException>(() => ((IList<DesktopSnapshot>)snapshot[0].Desktops).Clear());
    Throws<NotSupportedException>(() => ((IList<long>)snapshot[0].Desktops[0].WindowIds).Clear());
}

static void ForgetWindows()
{
    var catalog = NewCatalog();
    catalog.TrackWindow(-1, "left");
    True(catalog.IsWindowVisible(-1));
    catalog.ForgetWindow(-1);
    catalog.ForgetWindow(-1);
    Equal<WindowAssignment?>(null, catalog.GetAssignment(-1));
    False(catalog.IsWindowVisible(-1));
    Equal(0, Display(catalog, "left").Desktops.Single().WindowIds.Count);
}

static void DesktopNames()
{
    var catalog = NewCatalog();
    var initial = Display(catalog, "left").Desktops.Single();
    var first = initial.Id;
    Equal<int?>(1, initial.DefaultNumber);
    catalog.RenameDesktop("left", first, "  Work  ");
    var custom = catalog.AddDesktop("left", "  Desktop 2  ");
    var automatic = catalog.AddDesktop("left");
    Equal("Work", Display(catalog, "left").Desktops.Single(d => d.Id == first).Name);
    Equal<int?>(null, Display(catalog, "left").Desktops.Single(d => d.Id == first).DefaultNumber);
    Equal("Desktop 2", Display(catalog, "left").Desktops.Single(d => d.Id == custom).Name);
    Equal<int?>(null, Display(catalog, "left").Desktops.Single(d => d.Id == custom).DefaultNumber);
    Equal("Desktop 3", Display(catalog, "left").Desktops.Single(d => d.Id == automatic).Name);
    Equal<int?>(3, Display(catalog, "left").Desktops.Single(d => d.Id == automatic).DefaultNumber);
    Equal<int?>(1, initial.DefaultNumber); // Detached snapshots retain their original generated-name metadata.
    catalog.RemoveDesktop("left", custom);
    var later = catalog.AddDesktop("left");
    Equal("Desktop 4", Display(catalog, "left").Desktops.Single(d => d.Id == later).Name);
    catalog.RenameDesktop("left", automatic, "Desktop 3");
    Equal<int?>(null, Display(catalog, "left").Desktops.Single(d => d.Id == automatic).DefaultNumber);
}

static void ReorderAdjacentDesktops()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left", "Research");
    catalog.TrackWindow(101, "left");
    catalog.TrackWindow(102, "left");
    catalog.MoveWindowToDesktop(102, second);
    catalog.TrackWindow(103, "left");
    catalog.MoveWindowToDesktop(103, third);
    catalog.TrackWindow(104, "right");
    catalog.SwitchToDesktop("left", second);
    var before = catalog.Snapshot();
    var desktops = before[0].Desktops.ToDictionary(d => d.Id, d => JsonSerializer.Serialize(d));
    var assignments = new long[] { 101, 102, 103, 104 }.ToDictionary(id => id, catalog.GetAssignment);
    var visibility = assignments.Keys.ToDictionary(id => id, catalog.IsWindowVisible);

    True(catalog.ReorderDesktop("left", second, 0));

    Equal($"{second},{first},{third}", string.Join(",", Display(catalog, "left").Desktops.Select(d => d.Id)));
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    foreach (var desktop in Display(catalog, "left").Desktops)
        Equal(desktops[desktop.Id], JsonSerializer.Serialize(desktop));
    foreach (var assignment in assignments)
    {
        Equal(assignment.Value, catalog.GetAssignment(assignment.Key));
        Equal(visibility[assignment.Key], catalog.IsWindowVisible(assignment.Key));
    }
    Equal(JsonSerializer.Serialize(before[1]), JsonSerializer.Serialize(Display(catalog, "right")));
}

static void ReorderNonadjacentDesktops()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left");
    var fourth = catalog.AddDesktop("left");
    True(catalog.ReorderDesktop("left", first, 3));
    Equal($"{second},{third},{fourth},{first}", string.Join(",", Display(catalog, "left").Desktops.Select(d => d.Id)));
    Equal(first, Display(catalog, "left").ActiveDesktopId);

    True(catalog.ReorderDesktop("left", fourth, 0));
    Equal($"{fourth},{second},{third},{first}", string.Join(",", Display(catalog, "left").Desktops.Select(d => d.Id)));
    True(catalog.ReorderDesktop("left", fourth, 2));
    Equal($"{second},{third},{fourth},{first}", string.Join(",", Display(catalog, "left").Desktops.Select(d => d.Id)));
}

static void ReorderNavigation()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left");
    catalog.SwitchToDesktop("right", catalog.AddDesktop("right"));
    var peer = JsonSerializer.Serialize(Display(catalog, "right"));
    catalog.ReorderDesktop("left", second, 0);

    catalog.SwitchRelative("left", -1, wrapAround: false);
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", -1, wrapAround: false);
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", -1);
    Equal(third, Display(catalog, "left").ActiveDesktopId);
    catalog.SwitchRelative("left", -1);
    Equal(first, Display(catalog, "left").ActiveDesktopId);
    Equal(peer, JsonSerializer.Serialize(Display(catalog, "right")));
}

static void ReorderLinkedNavigation()
{
    var catalog = ThreeDisplayCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    catalog.AddDesktop("left");
    catalog.SwitchToDesktop("left", second, DisplaySwitchingMode.Linked);
    var peers = JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left"));

    True(catalog.ReorderDesktop("left", second, 0));
    Equal(second, Display(catalog, "left").ActiveDesktopId);
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));

    // The following linked request uses the new ordinal, without copying the source order.
    catalog.SwitchRelative("left", 1, mode: DisplaySwitchingMode.Linked);
    Equal(first, Display(catalog, "left").ActiveDesktopId);
    AllAtIndex(catalog, 1);
    Equal(peers, JsonSerializer.Serialize(catalog.Snapshot().Where(d => d.Id != "left")));
}

static void ReorderInvalidAndNoOp()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var peer = Display(catalog, "right").ActiveDesktopId;
    catalog.TrackWindow(101, "left");
    var before = JsonSerializer.Serialize(catalog.Snapshot());
    Action[] invalid =
    [
        () => catalog.ReorderDesktop("missing", first, 0),
        () => catalog.ReorderDesktop(null!, first, 0),
        () => catalog.ReorderDesktop(" ", first, 0),
        () => catalog.ReorderDesktop("left", peer, 0),
        () => catalog.ReorderDesktop("left", Guid.Empty, 0),
        () => catalog.ReorderDesktop("left", Guid.NewGuid(), 0),
        () => catalog.ReorderDesktop("left", first, -1),
        () => catalog.ReorderDesktop("left", second, 2),
        () => catalog.ReorderDesktop("left", first, int.MinValue),
        () => catalog.ReorderDesktop("left", second, int.MaxValue),
    ];
    foreach (var operation in invalid)
    {
        Throws<ArgumentException>(operation);
        Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    }
    False(catalog.ReorderDesktop("left", first, 0));
    False(catalog.ReorderDesktop("left", second, 1));
    False(catalog.ReorderDesktop("right", peer, 0));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
    catalog.RemoveDesktop("left", second);
    before = JsonSerializer.Serialize(catalog.Snapshot());
    Throws<ArgumentException>(() => catalog.ReorderDesktop("left", second, 0));
    Equal(before, JsonSerializer.Serialize(catalog.Snapshot()));
}

static void ReorderSnapshotsAndNames()
{
    var catalog = NewCatalog();
    var first = Display(catalog, "left").ActiveDesktopId;
    var second = catalog.AddDesktop("left");
    var third = catalog.AddDesktop("left");
    var custom = catalog.AddDesktop("left", "Desktop 4");
    var snapshot = catalog.Snapshot();
    var serialized = JsonSerializer.Serialize(snapshot);

    catalog.ReorderDesktop("left", second, 0);
    catalog.ReorderDesktop("left", custom, 1);
    var reordered = Display(catalog, "left");
    Equal("Desktop 2", reordered.Desktops[0].Name);
    Equal<int?>(2, reordered.Desktops[0].DefaultNumber);
    Equal("Desktop 4", reordered.Desktops[1].Name);
    Equal<int?>(null, reordered.Desktops[1].DefaultNumber);
    Equal(first, reordered.Desktops[2].Id);
    Equal(third, reordered.Desktops[3].Id);

    var next = catalog.AddDesktop("left");
    var generated = Display(catalog, "left").Desktops.Single(d => d.Id == next);
    Equal("Desktop 5", generated.Name);
    Equal<int?>(5, generated.DefaultNumber);
    Equal(serialized, JsonSerializer.Serialize(snapshot));
}

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected <{expected}>; actual <{actual}>.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
