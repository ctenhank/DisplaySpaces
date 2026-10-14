using System.Drawing.Imaging;
using System.Reflection;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>Hidden overlay fixtures and synthetic input only; never moves windows, installs hooks, or moves the pointer.</summary>
internal static class MoveBehaviorSelfTests
{
    // There is deliberately no native metadata for this fixture-only ID, so rendering
    // cannot register a thumbnail for, or otherwise interact with, a real window.
    private const long FixtureWindow = 47101;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(output, results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "move-results.txt"), results);
        return exitCode;
    }

    public static void Run(string output, List<string> results)
    {
        string originalLanguage = UiText.SelectedLanguageCode;
        Directory.CreateDirectory(output);
        try
        {
            UiText.SetLanguage("en", persist: false);
            ExistingDesktopDrop(results);
            ExplicitNavigation(results);
            InvalidDragDoesNotFreeze(results);
            MenuMoveRequests(results);
            NewDesktopDrop(output, results, previewInactive: false);
            NewDesktopDrop(output, results, previewInactive: true);
        }
        finally { UiText.SetLanguage(originalLanguage, persist: false); }
    }

    private static void ExistingDesktopDrop(List<string> results)
    {
        Guid session = Guid.NewGuid();
        using var source = new Fixture("move-source", session: session);
        using var fixture = new Fixture("move-receiver", session: session);
        var form = fixture.Form;
        Point target = Center(form.DesktopCardBounds(fixture.Target));
        IDataObject payload = source.Form.CreateDragData(FixtureWindow);

        Mouse(form, "OnMouseDown", target, MouseButtons.Left);
        Require(form.PressedTargetId is not null, "The drag receiver fixture did not establish its pending press.");
        var enter = Drag(form, "OnDragEnter", payload, target);
        Require(enter.Effect == DragDropEffects.Move && form.PressedTargetId is null,
            "A valid drag entry must accept the move and clear a pending desktop press.");
        Mouse(form, "OnMouseUp", target, MouseButtons.Left);
        HoverJitter(form, target);
        fixture.RequirePreserved(fixture.Source, "valid drag entry");

        var drop = Drag(form, "OnDragDrop", payload, target);
        Require(drop.Effect == DragDropEffects.Move && fixture.Moves.Count == 1
            && fixture.Moves[0] == (FixtureWindow, fixture.Display.Id, fixture.Target),
            "Dropping on an existing desktop must request exactly one move to that desktop.");
        HoverJitter(form, target);
        fixture.RequirePreserved(fixture.Source, "existing desktop drop and pointer jitter");

        fixture.Update(fixture.Display with
        {
            Desktops = fixture.Display.Desktops.Select(desktop => desktop with
            {
                WindowIds = desktop.Id == fixture.Target ? [FixtureWindow] : []
            }).ToArray()
        });
        HoverJitter(form, Center(form.DesktopCardBounds(fixture.Target)));
        fixture.RequirePreserved(fixture.Source, "the moved-window snapshot refresh");
        Require(!source.Form.Visible && !form.Visible && source.Form.LivePreviewCount == 0 && form.LivePreviewCount == 0,
            "Move fixtures must stay hidden and must not register live previews.");
        results.Add("PASS existing-desktop drops request the correct move once and preserve preview/active desktop through receiver entry, release, pointer jitter, and snapshot refresh");
    }

    private static void ExplicitNavigation(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        Point target = Center(form.DesktopCardBounds(fixture.Target));
        form.PreservePreviewAfterMove();
        HoverJitter(form, target);
        fixture.RequirePreserved(fixture.Source, "stationary pointer after a move");
        Key(form, Keys.Enter);
        Require(fixture.Selections.Count == 1 && fixture.Selections[0] == (fixture.Display.Id, fixture.Source, (long?)null),
            "Enter after a move must still request the original preview desktop.");
        fixture.Selections.Clear();

        Click(form, target);
        Require(fixture.Selections.Count == 1 && fixture.Selections[0] == (fixture.Display.Id, fixture.Target, (long?)null),
            "An explicit click must request the destination exactly once while post-move hover is held.");
        fixture.Selections.Clear();

        Mouse(form, "OnMouseMove", new Point(form.DesktopRailBounds.Left + 5, form.DesktopRailBounds.Top - 10));
        Mouse(form, "OnMouseMove", target);
        Require(form.SelectedDesktopId == fixture.Target && fixture.Selections.Count == 0,
            "Leaving the desktop rail and returning must restore normal hover previews without switching desktops.");
        form.PreviewDesktop(fixture.Source);
        form.PreservePreviewAfterMove();
        Invoke(form, "OnMouseLeave", EventArgs.Empty);
        Mouse(form, "OnMouseMove", target);
        Require(form.SelectedDesktopId == fixture.Target,
            "Leaving the overlay must also restore normal hover previews on return.");

        form.PreviewDesktop(fixture.Source);
        form.PreservePreviewAfterMove();
        Key(form, Keys.Right);
        Key(form, Keys.Enter);
        Require(form.SelectedDesktopId == fixture.Target && fixture.Selections.Count == 1
            && fixture.Selections[0] == (fixture.Display.Id, fixture.Target, (long?)null)
            && Read<DisplaySnapshot>(form, "_display").ActiveDesktopId == fixture.Source,
            "Explicit keyboard navigation must remain available while post-move hover is held.");
        results.Add("PASS moves keep Enter on the original preview while explicit clicks/keyboard navigation work, and leaving the rail or overlay restores hover previews");
    }

    private static void InvalidDragDoesNotFreeze(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        Point target = Center(form.DesktopCardBounds(fixture.Target));
        IDataObject[] invalidPayloads =
        [
            new DataObject(),
            new DataObject(DataFormats.UnicodeText, $"BetterVirtualDesktop.Window/{Guid.NewGuid():D}/{FixtureWindow}"),
            new DataObject(DataFormats.UnicodeText, "unrelated drag contents")
        ];
        foreach (var payload in invalidPayloads)
        {
            form.PreviewDesktop(fixture.Source);
            fixture.Render();
            Mouse(form, "OnMouseDown", target, MouseButtons.Left);
            string? pressed = form.PressedTargetId;
            var enter = Drag(form, "OnDragEnter", payload, target);
            Require(enter.Effect == DragDropEffects.None && pressed is not null && form.PressedTargetId == pressed,
                "An invalid drag must neither accept a move nor clear a deliberate desktop press.");
            Mouse(form, "OnMouseUp", target, MouseButtons.Left);
            Require(fixture.Selections.Count == 1 && fixture.Selections[0].Desktop == fixture.Target,
                "An invalid drag must not suppress an otherwise valid desktop click.");
            fixture.Selections.Clear();
            Drag(form, "OnDragDrop", payload, target);
            Mouse(form, "OnMouseMove", target);
            Require(form.SelectedDesktopId == fixture.Target && fixture.Moves.Count == 0 && fixture.NewMoves.Count == 0,
                "Invalid drag payloads must leave ordinary hover preview behavior intact and request no move.");
        }
        results.Add("PASS empty, unrelated, and foreign-session drag payloads neither move windows nor freeze ordinary clicking/hover previews");
    }

    private static void MenuMoveRequests(List<string> results)
    {
        using var fixture = new Fixture();
        var form = fixture.Form;
        Point target = Center(form.DesktopCardBounds(fixture.Target));
        Mouse(form, "OnMouseDown", target, MouseButtons.Left);
        form.RequestMove(FixtureWindow, "other-fixture-display", fixture.Target);
        Require(form.PressedTargetId is null, "A menu move request must clear an outstanding press.");
        Mouse(form, "OnMouseUp", target, MouseButtons.Left);
        HoverJitter(form, target);
        fixture.RequirePreserved(fixture.Source, "a menu move to an existing desktop");
        Require(fixture.Moves.Count == 1 && fixture.Moves[0] == (FixtureWindow, "other-fixture-display", fixture.Target),
            "The menu move path must preserve its requested display and desktop.");

        form.RequestMoveToNewDesktop(FixtureWindow, "other-fixture-display");
        HoverJitter(form, target);
        fixture.RequirePreserved(fixture.Source, "a menu move to a new desktop");
        Require(fixture.NewMoves.Count == 1 && fixture.NewMoves[0] == (FixtureWindow, "other-fixture-display"),
            "The menu new-desktop path must issue exactly one move request for the selected display.");
        results.Add("PASS both context-menu move request paths preserve the current preview, suppress stale releases, and retain the exact destination without selecting it");
    }

    private static void NewDesktopDrop(string output, List<string> results, bool previewInactive)
    {
        using var fixture = new Fixture(desktopCount: 12);
        var form = fixture.Form;
        Guid selected = previewInactive ? fixture.Target : fixture.Source;
        form.PreviewDesktop(selected);
        fixture.Render();
        Rectangle sourceCard = form.DesktopCardBounds(selected);
        int firstDesktop = Read<int>(form, "_firstDesktop");
        Point plus = Center(form.NewDesktopCardBounds);
        var payload = form.CreateDragData(FixtureWindow);
        Mouse(form, "OnMouseDown", plus, MouseButtons.Left);
        Drag(form, "OnDragEnter", payload, plus);
        Drag(form, "OnDragDrop", payload, plus);
        Mouse(form, "OnMouseUp", plus, MouseButtons.Left);
        Require(fixture.NewMoves.Count == 1 && fixture.NewMoves[0] == (FixtureWindow, fixture.Display.Id)
            && fixture.Moves.Count == 0 && fixture.Adds == 0,
            "Dropping on the plus card must request one move to a new desktop without also adding or selecting a desktop.");

        Guid created = Guid.NewGuid();
        var createdDesktop = new DesktopSnapshot(created, "Created destination", [FixtureWindow]);
        fixture.Update(fixture.Display with
        {
            Desktops = fixture.Display.Desktops.Select(desktop => desktop with { WindowIds = [] })
                .Append(createdDesktop).ToArray()
        });
        form.ShowCreationFeedback(created, windowMoved: true);
        fixture.Render();
        fixture.RequirePreserved(selected, "new-desktop creation feedback");
        Require(form.HasCreationFeedback && form.CreationFeedbackTitle?.Contains(createdDesktop.Name, StringComparison.Ordinal) == true
            && form.CreationFeedbackBounds.Height >= 100 * form.DeviceDpi / 96
            && form.ClientRectangle.Contains(form.CreationFeedbackBounds)
            && !form.CreationFeedbackBounds.IntersectsWith(form.DesktopRailBounds)
            && !form.CreationOpenButtonBounds.IsEmpty,
            "Move feedback must prominently name the created destination and retain its explicit Open action.");
        Require(Read<int>(form, "_firstDesktop") == firstDesktop && form.DesktopCardBounds(selected) == sourceCard,
            "An off-page move destination must not scroll the rail away from the existing preview.");

        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            form.ApplyLanguage();
            fixture.Update(fixture.Display with { Name = "Move fixture " + language.Code });
            fixture.Update(fixture.Display);
            HoverJitter(form, Center(form.DesktopCardBounds(previewInactive ? fixture.Source : fixture.Target)));
            fixture.RequirePreserved(selected, "language and repeated snapshot updates");
            Require(form.CreationFeedbackTitle?.Contains(createdDesktop.Name, StringComparison.Ordinal) == true,
                "Language or snapshot changes lost the destination named in move feedback.");
        }
        fixture.Render(Path.Combine(output, previewInactive ? "move-created-inactive-preview.png" : "move-created-active-preview.png"));
        Key(form, Keys.Enter);
        Require(fixture.Selections.Count == 1 && fixture.Selections[0] == (fixture.Display.Id, selected, (long?)null),
            "Enter during move-to-new feedback must still open the original preview.");
        fixture.Selections.Clear();
        Click(form, Center(form.CreationOpenButtonBounds));
        Require(fixture.Selections.Count == 1 && fixture.Selections[0] == (fixture.Display.Id, created, (long?)null),
            "The feedback Open button must explicitly request the created destination exactly once.");
        Require(form.SelectedDesktopId == selected && Read<DisplaySnapshot>(form, "_display").ActiveDesktopId == fixture.Source,
            "Emitting the explicit Open request must not independently mutate the overlay snapshot.");
        results.Add($"PASS move-to-new feedback preserves the {(previewInactive ? "nonactive" : "active")} preview and rail across all languages/snapshot updates, names its off-page destination, keeps Enter on the source, and opens the destination only on an explicit click");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Screen _screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        private readonly IReadOnlyDictionary<long, NativeWindowInfo> _windows = new Dictionary<long, NativeWindowInfo>();
        public DisplayOverlayForm Form { get; }
        public DisplaySnapshot Display { get; private set; }
        public Guid Source => Display.Desktops[0].Id;
        public Guid Target => Display.Desktops[1].Id;
        public List<(string Display, Guid Desktop, long? Window)> Selections { get; } = [];
        public List<(long Window, string Display, Guid Desktop)> Moves { get; } = [];
        public List<(long Window, string Display)> NewMoves { get; } = [];
        public int Adds { get; private set; }

        public Fixture(string id = "move-fixture", int desktopCount = 3, Guid? session = null)
        {
            var desktops = Enumerable.Range(1, desktopCount)
                .Select(number => new DesktopSnapshot(Guid.NewGuid(), $"Desktop {number}", number == 1 ? [FixtureWindow] : [], number)).ToArray();
            Display = new(id, "Move fixture", true, desktops[0].Id, desktops);
            Form = new DisplayOverlayForm(_screen, Display, dragSession: session);
            Form.SelectionRequested += (display, desktop, window) => Selections.Add((display, desktop, window));
            Form.MoveToDisplayRequested += (window, display, desktop) => Moves.Add((window, display, desktop));
            Form.MoveToNewDesktopRequested += (window, display) => NewMoves.Add((window, display));
            Form.AddRequested += _ => Adds++;
            _ = Form.Handle;
            Update(Display);
        }

        public void Update(DisplaySnapshot display)
        {
            Display = display;
            Form.UpdateSnapshot(_screen, Display, _windows);
            Form.ClientSize = new Size(1280 * Form.DeviceDpi / 96, 760 * Form.DeviceDpi / 96);
            Render();
        }

        public void Render(string? path = null)
        {
            using var bitmap = new Bitmap(Form.Width, Form.Height);
            Form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, Form.Size));
            if (path is not null) bitmap.Save(path, ImageFormat.Png);
            Require(!Form.Visible && Form.LivePreviewCount == 0, "A move fixture became visible or registered a native window preview.");
        }

        public void RequirePreserved(Guid selected, string operation) => Require(
            Form.SelectedDesktopId == selected && Read<DisplaySnapshot>(Form, "_display").ActiveDesktopId == Source && Selections.Count == 0,
            $"The {operation} changed the preview, active desktop, or emitted a desktop-selection request.");

        public void Dispose() => Form.Dispose();
    }

    private static void HoverJitter(DisplayOverlayForm form, Point point)
    {
        foreach (int offset in new[] { 0, 0, 1, -1, 0 })
            Mouse(form, "OnMouseMove", new Point(point.X + offset, point.Y));
    }

    private static void Click(DisplayOverlayForm form, Point point)
    {
        Mouse(form, "OnMouseDown", point, MouseButtons.Left);
        Mouse(form, "OnMouseUp", point, MouseButtons.Left);
    }

    private static void Mouse(DisplayOverlayForm form, string method, Point point, MouseButtons button = MouseButtons.None) =>
        Invoke(form, method, new MouseEventArgs(button, button == MouseButtons.None ? 0 : 1, point.X, point.Y, 0));

    private static DragEventArgs Drag(DisplayOverlayForm form, string method, IDataObject data, Point point)
    {
        Point screen = form.PointToScreen(point);
        var args = new DragEventArgs(data, 0, screen.X, screen.Y, DragDropEffects.Move, DragDropEffects.None);
        Invoke(form, method, args);
        return args;
    }

    private static void Key(DisplayOverlayForm form, Keys key) => Invoke(form, "ProcessCmdKey", new Message(), key);
    private static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static T Read<T>(DisplayOverlayForm form, string field) => (T)(typeof(DisplayOverlayForm).GetField(field, PrivateInstance)?.GetValue(form)
        ?? throw new MissingFieldException(typeof(DisplayOverlayForm).Name, field));
    private static void Invoke(DisplayOverlayForm form, string method, params object[] arguments) =>
        (typeof(DisplayOverlayForm).GetMethod(method, PrivateInstance)
            ?? throw new MissingMethodException(typeof(DisplayOverlayForm).Name, method)).Invoke(form, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
