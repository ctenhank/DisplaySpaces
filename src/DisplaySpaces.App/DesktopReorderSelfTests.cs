using System.Drawing.Imaging;
using System.Reflection;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

/// <summary>Hidden overlay input fixtures; no real input, window thumbnails, hooks, or user settings.</summary>
internal static class DesktopReorderSelfTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long FixtureWindow = 59801;

    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "desktop-reorder-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        Directory.CreateDirectory(output);
        string originalLanguage = UiText.SelectedLanguageCode;
        try
        {
            UiText.SetLanguage("en", persist: false);
            DesktopReorderControllerTests.Run(output, results);
            InsertionAndIdentity(output, results);
            RejectedDestinations(results);
            SessionAndSnapshotValidation(results);
            Cancellation(results);
            MenuAndClicks(results);
            WindowDropCompatibility(results);
        }
        finally { UiText.SetLanguage(originalLanguage, persist: false); }
    }

    private static void InsertionAndIdentity(string output, List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        var original = fixture.Display;
        Guid first = original.Desktops[0].Id, second = original.Desktops[1].Id, third = original.Desktops[2].Id;
        var payload = form.CreateDesktopDragData(second);
        Point beforeFirst = Half(form.DesktopCardBounds(first), after: false);
        Mouse(form, "OnMouseDown", beforeFirst, MouseButtons.Left);
        var enter = Drag(form, "OnDragEnter", payload, beforeFirst);
        Check(enter.Effect == DragDropEffects.Move && form.PressedTargetId is null
            && !form.ReorderInsertionBounds.IsEmpty && form.ReorderInsertionBounds.Left < form.DesktopCardBounds(first).Left,
            "desktop drag accepts insertion before the first card, clears its pending click, and displays an insertion marker", results);
        fixture.Render(Path.Combine(output, "desktop-reorder-drag.png"));
        var drop = Drag(form, "OnDragDrop", payload, beforeFirst);
        Check(drop.Effect == DragDropEffects.Move && fixture.Reorders.SequenceEqual([(original.Id, second, 0)])
            && form.ReorderInsertionBounds.IsEmpty && fixture.Selections.Count == 0 && fixture.Adds == 0,
            "dropping desktop 2 before desktop 1 requests one reorder at final index zero without switching or creating a desktop", results);
        Drag(form, "OnDragDrop", payload, beforeFirst);
        Mouse(form, "OnMouseUp", beforeFirst, MouseButtons.Left);
        Check(fixture.Reorders.Count == 1 && fixture.Selections.Count == 0,
            "consumed reorder payload and subsequent mouse release cannot repeat the action or select a desktop", results);
        fixture.Update(original with { Desktops = [original.Desktops[1], original.Desktops[0], original.Desktops[2]] });
        Mouse(form, "OnMouseMove", beforeFirst);
        fixture.Render(Path.Combine(output, "desktop-reorder-after.png"));
        Check(form.DesktopIds.SequenceEqual([second, first, third]) && form.SelectedDesktopId == first
            && fixture.Display.ActiveDesktopId == first && fixture.Display.Desktops[0] == original.Desktops[1]
            && fixture.Display.Desktops[1] == original.Desktops[0] && fixture.Selections.Count == 0,
            "reordered snapshot preserves GUIDs, names, default numbers, window membership, active desktop, and preview despite pointer jitter", results);

        fixture.Update(original);
        fixture.Reorders.Clear();
        payload = form.CreateDesktopDragData(first);
        Point afterThird = Half(form.DesktopCardBounds(third), after: true);
        Drag(form, "OnDragEnter", payload, afterThird);
        drop = Drag(form, "OnDragDrop", payload, afterThird);
        Check(drop.Effect == DragDropEffects.Move && fixture.Reorders.SequenceEqual([(original.Id, first, 2)]),
            "moving the first desktop after the last card emits the final index after removing its old position", results);
    }

    private static void RejectedDestinations(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        Guid first = fixture.Display.Desktops[0].Id, second = fixture.Display.Desktops[1].Id;
        Point[] invalidPoints =
        [
            Half(form.DesktopCardBounds(first), false),
            Half(form.DesktopCardBounds(first), true),
            Half(form.DesktopCardBounds(second), false),
            Center(form.NewDesktopCardBounds),
            Center(form.WindowTileBounds.Single()),
            new(1, 1)
        ];
        foreach (var point in invalidPoints)
        {
            var payload = form.CreateDesktopDragData(first);
            var enter = Drag(form, "OnDragEnter", payload, point);
            var drop = Drag(form, "OnDragDrop", payload, point);
            Check(enter.Effect == DragDropEffects.None && drop.Effect == DragDropEffects.None
                && form.ReorderInsertionBounds.IsEmpty && fixture.Reorders.Count == 0
                && fixture.Moves.Count == 0 && fixture.NewMoves.Count == 0 && fixture.Adds == 0,
                $"desktop reorder rejects unchanged position or non-desktop destination at ({point.X},{point.Y}) without a window move or creation", results);
        }
        var moveOnly = form.CreateDesktopDragData(second);
        var copy = Drag(form, "OnDragEnter", moveOnly, Half(form.DesktopCardBounds(first), false), DragDropEffects.Copy);
        Check(copy.Effect == DragDropEffects.None && form.ReorderInsertionBounds.IsEmpty,
            "desktop reorder refuses a drag that does not permit the Move effect", results);
    }

    private static void SessionAndSnapshotValidation(List<string> results)
    {
        Guid windowSession = Guid.NewGuid();
        using var source = new Fixture(session: windowSession);
        using var otherDisplay = new Fixture("reorder-other", session: windowSession);
        var form = source.Form;
        Guid first = source.Display.Desktops[0].Id, second = source.Display.Desktops[1].Id;
        Point target = Half(form.DesktopCardBounds(first), false);
        var payload = form.CreateDesktopDragData(second);
        var foreign = Drag(otherDisplay.Form, "OnDragDrop", payload,
            Half(otherDisplay.Form.DesktopCardBounds(otherDisplay.Display.Desktops[0].Id), false));
        Check(foreign.Effect == DragDropEffects.None && otherDisplay.Reorders.Count == 0
            && otherDisplay.Moves.Count == 0 && otherDisplay.NewMoves.Count == 0,
            "desktop cards cannot move to another display even when both overlays share their window-drag session", results);
        using var otherOpening = new Fixture(session: windowSession);
        otherOpening.Update(source.Display);
        var foreignOpening = Drag(otherOpening.Form, "OnDragDrop", payload,
            Half(otherOpening.Form.DesktopCardBounds(first), false));
        Check(foreignOpening.Effect == DragDropEffects.None && otherOpening.Reorders.Count == 0,
            "another overlay instance cannot accept a desktop payload even for the same display and desktop IDs", results);

        var replacedPayload = form.CreateDesktopDragData(second);
        form.CreateDesktopDragData(second);
        var replaced = Drag(form, "OnDragOver", replacedPayload, target);
        Check(replaced.Effect == DragDropEffects.None && form.ReorderInsertionBounds.IsEmpty,
            "starting a new desktop drag invalidates the previous drag token", results);
        foreach (var invalid in new IDataObject[]
        {
            new DataObject(), new DataObject(DataFormats.UnicodeText, "unrelated text"),
            new DataObject(DataFormats.UnicodeText, $"DisplaySpaces.Desktop/{Guid.NewGuid():D}/{Guid.NewGuid():D}/{second:D}")
        })
        {
            var rejected = Drag(form, "OnDragDrop", invalid, target);
            Check(rejected.Effect == DragDropEffects.None && source.Reorders.Count == 0,
                "empty, unrelated, or forged desktop payload is rejected", results);
        }

        payload = form.CreateDesktopDragData(second);
        source.Update(source.Display with { Name = "Metadata refresh" });
        var metadata = Drag(form, "OnDragOver", payload, Half(form.DesktopCardBounds(first), false));
        Check(metadata.Effect == DragDropEffects.Move && !form.ReorderInsertionBounds.IsEmpty,
            "compatible display metadata refresh retains an in-progress desktop drag", results);
        source.Update(source.Display with { Desktops = source.Display.Desktops.Reverse().ToArray() });
        var staleOrder = Drag(form, "OnDragDrop", payload, Half(form.DesktopCardBounds(first), false));
        Check(staleOrder.Effect == DragDropEffects.None && form.ReorderInsertionBounds.IsEmpty && source.Reorders.Count == 0,
            "a changed desktop order invalidates the old payload and removes its insertion marker", results);
        payload = form.CreateDesktopDragData(second);
        source.Update(source.Display with { Desktops = source.Display.Desktops.Where(d => d.Id != second).ToArray() });
        var removed = Drag(form, "OnDragDrop", payload, Half(form.DesktopCardBounds(first), false));
        Check(removed.Effect == DragDropEffects.None && source.Reorders.Count == 0 && form.PressedTargetId is null,
            "removing the source desktop cancels its drag without targeting a replacement card", results);
    }

    private static void Cancellation(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        Guid first = fixture.Display.Desktops[0].Id, second = fixture.Display.Desktops[1].Id;
        Point target = Half(form.DesktopCardBounds(first), false);
        var payload = form.CreateDesktopDragData(second);
        Drag(form, "OnDragEnter", payload, target);
        Invoke(form, "OnDragLeave", EventArgs.Empty);
        Check(form.ReorderInsertionBounds.IsEmpty && fixture.Reorders.Count == 0,
            "leaving the drop surface clears its insertion indicator without moving desktops", results);
        var reenter = Drag(form, "OnDragEnter", payload, target);
        Check(reenter.Effect == DragDropEffects.Move && !form.ReorderInsertionBounds.IsEmpty,
            "a desktop dragged outside the overlay can return to its source display", results);
        var cancellation = new QueryContinueDragEventArgs(0, true, DragAction.Continue);
        Invoke(form, "OnQueryContinueDrag", cancellation);
        Drag(form, "OnDragDrop", payload, target);
        Mouse(form, "OnMouseUp", target, MouseButtons.Left);
        Check(cancellation.Action == DragAction.Cancel && form.ReorderInsertionBounds.IsEmpty
            && form.PressedTargetId is null && fixture.Reorders.Count == 0 && fixture.Selections.Count == 0,
            "Escape cancels the desktop drag, stale release, and insertion feedback", results);

        payload = form.CreateDesktopDragData(second);
        Drag(form, "OnDragEnter", payload, target);
        form.CancelOverlay();
        Drag(form, "OnDragDrop", payload, target);
        Check(fixture.Dismisses == 1 && fixture.Reorders.Count == 0 && form.ReorderInsertionBounds.IsEmpty,
            "closing the overlay invalidates pending desktop payloads and clears drag feedback", results);
    }

    private static void MenuAndClicks(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        var desktops = fixture.Display.Desktops;
        using var first = form.BuildDesktopMenu(desktops[0].Id);
        using var middle = form.BuildDesktopMenu(desktops[1].Id);
        using var last = form.BuildDesktopMenu(desktops[2].Id);
        Check(!first.Items["Reorder.MoveLeft"]!.Enabled && first.Items["Reorder.MoveRight"]!.Enabled
            && middle.Items["Reorder.MoveLeft"]!.Enabled && middle.Items["Reorder.MoveRight"]!.Enabled
            && last.Items["Reorder.MoveLeft"]!.Enabled && !last.Items["Reorder.MoveRight"]!.Enabled,
            "desktop menus offer Move left/right with first and last boundaries disabled", results);
        middle.Items["Reorder.MoveLeft"]!.PerformClick();
        middle.Items["Reorder.MoveRight"]!.PerformClick();
        Check(fixture.Reorders.SequenceEqual([(fixture.Display.Id, desktops[1].Id, 0), (fixture.Display.Id, desktops[1].Id, 2)])
            && fixture.Selections.Count == 0 && form.SelectedDesktopId == desktops[0].Id,
            "menu reorder actions emit the exact desktop and adjacent index without changing the current preview", results);
        int count = fixture.Reorders.Count;
        form.RequestReorderDesktop(Guid.NewGuid(), 0);
        form.RequestReorderDesktop(desktops[0].Id, -1);
        form.RequestReorderDesktop(desktops[0].Id, desktops.Count);
        form.RequestReorderDesktop(desktops[0].Id, 0);
        Check(fixture.Reorders.Count == count, "invalid and unchanged menu reorder requests have no side effects", results);

        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            using var localized = form.BuildDesktopMenu(desktops[1].Id);
            Check(localized.Items["Reorder.MoveLeft"]!.Text == UiText.Get("Reorder.MoveLeft")
                && localized.Items["Reorder.MoveRight"]!.Text == UiText.Get("Reorder.MoveRight")
                && UiText.Get("Reorder.Hint") != "Reorder.Hint",
                $"desktop reorder commands and drag hint are localized for {language.Code}", results);
        }
        UiText.SetLanguage("en", persist: false);
        Point point = Center(form.DesktopCardBounds(desktops[1].Id));
        Mouse(form, "OnMouseDown", point, MouseButtons.Left);
        Check(form.PressedTargetId == $"desktop:{desktops[1].Id}",
            "a desktop card still renders its pressed state before crossing the drag threshold", results);
        Mouse(form, "OnMouseUp", point, MouseButtons.Left);
        Check(fixture.Selections.SequenceEqual([(fixture.Display.Id, desktops[1].Id, (long?)null)]) && fixture.Reorders.Count == count,
            "a normal desktop click still selects once without reordering", results);
        Point window = Center(form.WindowTileBounds.Single());
        Mouse(form, "OnMouseDown", window, MouseButtons.Left);
        Mouse(form, "OnMouseUp", window, MouseButtons.Left);
        Check(fixture.Selections.Last() == (fixture.Display.Id, desktops[0].Id, (long?)FixtureWindow),
            "a normal window-card click still selects its window after desktop reorder commands", results);

        fixture.Update(fixture.Display with { Desktops = [desktops[0]] });
        using var single = form.BuildDesktopMenu(desktops[0].Id);
        Check(!single.Items["Reorder.MoveLeft"]!.Enabled && !single.Items["Reorder.MoveRight"]!.Enabled,
            "both reorder directions are disabled for a single desktop", results);
    }

    private static void WindowDropCompatibility(List<string> results)
    {
        Guid session = Guid.NewGuid();
        using var source = new Fixture(session: session);
        using var destination = new Fixture("window-destination", session: session);
        var form = destination.Form;
        Guid target = destination.Display.Desktops[1].Id;
        var payload = source.Form.CreateDragData(FixtureWindow);
        var existing = Drag(form, "OnDragDrop", payload, Center(form.DesktopCardBounds(target)));
        Check(existing.Effect == DragDropEffects.Move
            && destination.Moves.SequenceEqual([(FixtureWindow, destination.Display.Id, target)])
            && destination.Reorders.Count == 0 && destination.Selections.Count == 0,
            "the separate window drag format still moves windows across displays without reordering desktops", results);
        var created = Drag(form, "OnDragDrop", payload, Center(form.NewDesktopCardBounds));
        Check(created.Effect == DragDropEffects.Move
            && destination.NewMoves.SequenceEqual([(FixtureWindow, destination.Display.Id)])
            && destination.Reorders.Count == 0 && destination.Adds == 0,
            "window drag-to-new-desktop retains its creation-and-move request without being interpreted as desktop reorder", results);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Screen _screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        private readonly IReadOnlyDictionary<long, NativeWindowInfo> _windows = new Dictionary<long, NativeWindowInfo>();
        public DisplaySnapshot Display { get; private set; }
        public DisplayOverlayForm Form { get; }
        public List<(string Display, Guid Desktop, int Index)> Reorders { get; } = [];
        public List<(string Display, Guid Desktop, long? Window)> Selections { get; } = [];
        public List<(long Window, string Display, Guid Desktop)> Moves { get; } = [];
        public List<(long Window, string Display)> NewMoves { get; } = [];
        public int Adds { get; private set; }
        public int Dismisses { get; private set; }

        public Fixture(string id = "reorder-fixture", Guid? session = null)
        {
            DesktopSnapshot[] desktops =
            [
                new(Guid.NewGuid(), "Desktop 1", [FixtureWindow], 1),
                new(Guid.NewGuid(), "Desktop 2", [], 2),
                new(Guid.NewGuid(), "Research", [])
            ];
            Display = new(id, "Reorder fixture", true, desktops[0].Id, desktops);
            Form = new DisplayOverlayForm(_screen, Display, dragSession: session);
            Form.ReorderRequested += (display, desktop, index) => Reorders.Add((display, desktop, index));
            Form.SelectionRequested += (display, desktop, window) => Selections.Add((display, desktop, window));
            Form.MoveToDisplayRequested += (window, display, desktop) => Moves.Add((window, display, desktop));
            Form.MoveToNewDesktopRequested += (window, display) => NewMoves.Add((window, display));
            Form.AddRequested += _ => Adds++;
            Form.DismissRequested += () => Dismisses++;
            _ = Form.Handle;
            Update(Display);
        }

        public void Update(DisplaySnapshot display)
        {
            Display = display;
            Form.UpdateSnapshot(_screen, display, _windows);
            Form.ClientSize = new Size(1280 * Form.DeviceDpi / 96, 760 * Form.DeviceDpi / 96);
            Render();
        }

        public void Render(string? path = null)
        {
            using var bitmap = new Bitmap(Form.Width, Form.Height);
            Form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, Form.Size));
            if (path is not null) bitmap.Save(path, ImageFormat.Png);
            if (Form.Visible || Form.LivePreviewCount != 0)
                throw new InvalidOperationException("A desktop reorder fixture became visible or registered a native thumbnail.");
        }

        public void Dispose() => Form.Dispose();
    }

    private static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static Point Half(Rectangle bounds, bool after) =>
        new(bounds.Left + bounds.Width * (after ? 3 : 1) / 4, bounds.Top + bounds.Height / 2);
    private static void Mouse(DisplayOverlayForm form, string method, Point point, MouseButtons button = MouseButtons.None) =>
        Invoke(form, method, new MouseEventArgs(button, button == MouseButtons.None ? 0 : 1, point.X, point.Y, 0));
    private static DragEventArgs Drag(DisplayOverlayForm form, string method, IDataObject data, Point point,
        DragDropEffects allowed = DragDropEffects.Move)
    {
        Point screen = form.PointToScreen(point);
        var args = new DragEventArgs(data, 0, screen.X, screen.Y, allowed, DragDropEffects.None);
        Invoke(form, method, args);
        return args;
    }
    private static void Invoke(DisplayOverlayForm form, string method, params object[] arguments) =>
        (typeof(DisplayOverlayForm).GetMethod(method, PrivateInstance)
            ?? throw new MissingMethodException(typeof(DisplayOverlayForm).Name, method)).Invoke(form, arguments);
    private static void Check(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS " + name);
    }
}
