using System.Text.Json;
using BetterVirtualDesktop.Core;

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
    ("Invalid mutations leave all state unchanged", InvalidMutations),
    ("Snapshots remain stable after later mutations", DetachedSnapshots),
    ("Forget removes closed windows and tolerates repeated removal", ForgetWindows),
    ("Desktop names stay stable and explicit names are trimmed", DesktopNames),
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
