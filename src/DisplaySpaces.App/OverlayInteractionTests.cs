using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using DisplaySpaces.App.Native;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

/// <summary>Exercises overlay input without moving the pointer or subscribing a window controller.</summary>
internal static class OverlayInteractionTests
{
    public static void Run(Screen screen, DisplaySnapshot display,
        IReadOnlyDictionary<long, NativeWindowInfo> windows, string output, List<string> results)
    {
        var fixture = display.Desktops.SelectMany(d => d.WindowIds).Distinct().Where(windows.ContainsKey)
            .Select(handle => windows[handle]).FirstOrDefault(w => !w.IsMinimized)
            ?? throw new InvalidOperationException("Pointer interaction tests need a fixture window on this display.");
        Guid active = display.ActiveDesktopId;
        Guid candidate = Guid.NewGuid();
        var snapshot = display with
        {
            Desktops = [new(active, "현재 데스크톱", [fixture.Handle]), new(candidate, "다른 데스크톱", [])]
        };
        IReadOnlyDictionary<long, NativeWindowInfo> fixtureWindows = new Dictionary<long, NativeWindowInfo>
        {
            [fixture.Handle] = fixture
        };
        using var form = new DisplayOverlayForm(screen, snapshot);
        int windowClicks = 0, desktopClicks = 0, addClicks = 0, dismissClicks = 0;
        form.SelectionRequested += (_, _, handle) => { if (handle.HasValue) windowClicks++; else desktopClicks++; };
        form.AddRequested += _ => addClicks++;
        form.DismissRequested += () => dismissClicks++;
        var captures = new List<(string Category, string State, Bitmap Image)>();

        int Actions() => windowClicks + desktopClicks + addClicks + dismissClicks;
        void Reset()
        {
            Leave(form);
            Mouse(form, "OnMouseUp", MouseButtons.Left, new Point(1, 1));
            PumpFor(180);
            form.UpdateSnapshot(screen, snapshot, fixtureWindows);
            form.PreviewDesktop(active);
            using var rendered = Render(form);
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            results.Add("PASS " + name);
        }
        Rectangle WindowBounds() => form.WindowTileBounds.Single();
        Rectangle DesktopBounds() => form.DesktopCardBounds(active);
        Rectangle NewBounds() => form.NewDesktopCardBounds;

        try
        {
            Reset();
            foreach (var category in new[]
            {
                (Name: "window", Bounds: (Func<Rectangle>)WindowBounds, Count: (Func<int>)(() => windowClicks)),
                (Name: "desktop", Bounds: (Func<Rectangle>)DesktopBounds, Count: (Func<int>)(() => desktopClicks)),
                (Name: "new desktop", Bounds: (Func<Rectangle>)NewBounds, Count: (Func<int>)(() => addClicks))
            })
            {
                Reset();
                Rectangle bounds = category.Bounds();
                Point point = Center(bounds);
                int actionsBefore = Actions(), categoryBefore = category.Count();
                var normal = Crop(form, bounds);
                captures.Add((category.Name, "Normal", normal));
                Mouse(form, "OnMouseMove", MouseButtons.None, point);
                PumpFor(180);
                var hover = Crop(form, bounds);
                captures.Add((category.Name, "Hover", hover));
                Check(form.HoveredTargetId is not null && Actions() == actionsBefore,
                    $"{category.Name} hover identifies the target without invoking an action");
                Check(DifferentPixels(normal, hover) >= 8, $"{category.Name} hover visibly changes the rendered target");

                Mouse(form, "OnMouseDown", MouseButtons.Left, point);
                string? pressedId = form.PressedTargetId;
                var pressed = Crop(form, bounds);
                captures.Add((category.Name, "Pressed", pressed));
                Check(pressedId is not null && pressedId == form.HoveredTargetId,
                    $"{category.Name} mouse down records the hovered target");
                Check(DifferentPixels(hover, pressed) >= 8, $"{category.Name} pressed state differs visibly from hover");

                // A discovery update changes metadata but retains the identity under the pointer.
                var changedWindows = new Dictionary<long, NativeWindowInfo>
                {
                    [fixture.Handle] = fixture with { Title = fixture.Title + " · metadata refresh" }
                };
                form.UpdateSnapshot(screen, snapshot, changedWindows);
                using (Render(form)) { }
                Check(form.PressedTargetId == pressedId,
                    $"{category.Name} press survives repaint and a compatible snapshot update");
                Mouse(form, "OnMouseUp", MouseButtons.Left, point);
                using (Render(form)) { }
                Check(form.PressedTargetId is null && category.Count() == categoryBefore + 1 && Actions() == actionsBefore + 1,
                    $"{category.Name} same-target release invokes its action exactly once");
                Mouse(form, "OnMouseUp", MouseButtons.Left, point);
                Check(Actions() == actionsBefore + 1, $"{category.Name} repeated mouse-up cannot repeat an action");

                Reset();
                bounds = category.Bounds();
                point = Center(bounds);
                actionsBefore = Actions();
                Point background = new(1, 1);
                Mouse(form, "OnMouseDown", MouseButtons.Left, background);
                Mouse(form, "OnMouseUp", MouseButtons.Left, point);
                Check(Actions() == actionsBefore, $"{category.Name} release cannot activate a target pressed elsewhere");
                Mouse(form, "OnMouseDown", MouseButtons.Left, point);
                Mouse(form, "OnMouseUp", MouseButtons.Left, background);
                Check(Actions() == actionsBefore, $"{category.Name} release outside cancels the pending action");

                Reset();
                bounds = category.Bounds();
                point = Center(bounds);
                Mouse(form, "OnMouseMove", MouseButtons.None, point);
                Mouse(form, "OnMouseDown", MouseButtons.Left, point);
                Leave(form);
                PumpFor(180);
                using var afterLeave = Crop(form, bounds);
                Check(form.HoveredTargetId is null && form.PressedTargetId is null && DifferentPixels(normal, afterLeave) == 0,
                    $"{category.Name} pointer leave clears hover and press rendering");
            }

            Reset();
            int beforeHover = Actions();
            Mouse(form, "OnMouseMove", MouseButtons.None, Center(form.DesktopCardBounds(candidate)));
            using (Render(form)) { }
            Check(form.SelectedDesktopId == candidate && snapshot.ActiveDesktopId == active && Actions() == beforeHover,
                "hover previews another desktop without requesting an actual switch");

            Reset();
            Mouse(form, "OnMouseMove", MouseButtons.None, Center(DesktopBounds()));
            form.MoveSelection(1);
            using (Render(form)) { }
            Check(form.SelectedDesktopId == candidate,
                "stationary pointer repaint preserves the keyboard-selected desktop");

            Reset();
            using (var captureProbe = new Control())
            {
                _ = captureProbe.Handle;
                form.Show();
                Point point = Center(WindowBounds());
                Mouse(form, "OnMouseMove", MouseButtons.None, point);
                Mouse(form, "OnMouseDown", MouseButtons.Left, point);
                Check(form.Capture && form.PressedTargetId is not null,
                    "visible overlay captures a pressed pointer");
                int beforeCaptureLoss = Actions();
                captureProbe.Capture = true; // Another test-owned HWND delivers real WM_CAPTURECHANGED.
                Check(!form.Capture && form.PressedTargetId is null,
                    "native mouse capture loss clears the pending press");
                captureProbe.Capture = false;
                Mouse(form, "OnMouseUp", MouseButtons.Left, point);
                Check(Actions() == beforeCaptureLoss,
                    "release after native capture loss cannot activate the former target");
                form.Hide();
            }

            Reset();
            Point removedWindowPoint = Center(WindowBounds());
            Mouse(form, "OnMouseMove", MouseButtons.None, removedWindowPoint);
            Mouse(form, "OnMouseDown", MouseButtons.Left, removedWindowPoint);
            int beforeRemoval = Actions();
            var noWindow = snapshot with
            {
                Desktops = snapshot.Desktops.Select(d => d.Id == active ? d with { WindowIds = Array.Empty<long>() } : d).ToArray()
            };
            form.UpdateSnapshot(screen, noWindow, fixtureWindows);
            using (Render(form)) { }
            Check(form.PressedTargetId is null, "removing a pressed window cancels its pending click");
            Mouse(form, "OnMouseUp", MouseButtons.Left, removedWindowPoint);
            Check(Actions() == beforeRemoval, "removed window cannot activate on a later release");

            Reset();
            Point removedDesktopPoint = Center(form.DesktopCardBounds(candidate));
            Mouse(form, "OnMouseMove", MouseButtons.None, removedDesktopPoint);
            using (Render(form)) { }
            Mouse(form, "OnMouseDown", MouseButtons.Left, removedDesktopPoint);
            beforeRemoval = Actions();
            var noCandidate = snapshot with { Desktops = snapshot.Desktops.Where(d => d.Id != candidate).ToArray() };
            form.UpdateSnapshot(screen, noCandidate, fixtureWindows);
            using (Render(form)) { }
            Check(form.PressedTargetId is null, "removing a pressed desktop cancels its pending click");
            Mouse(form, "OnMouseUp", MouseButtons.Left, removedDesktopPoint);
            Check(Actions() == beforeRemoval, "removed desktop release cannot trigger a replacement card");

            SaveContactSheet(captures, Path.Combine(output, "overlay-pointer-states.png"));
            results.Add("PASS pointer-state contact sheet saved to overlay-pointer-states.png");
        }
        finally
        {
            foreach (var capture in captures) capture.Image.Dispose();
        }
    }

    private static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private static void Mouse(DisplayOverlayForm form, string method, MouseButtons button, Point point) =>
        Invoke(form, method, new MouseEventArgs(button, method == "OnMouseMove" ? 0 : 1, point.X, point.Y, 0));

    private static void Leave(DisplayOverlayForm form) => Invoke(form, "OnMouseLeave", EventArgs.Empty);

    private static void Invoke(DisplayOverlayForm form, string method, EventArgs args)
    {
        var callback = typeof(DisplayOverlayForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(DisplayOverlayForm).Name, method);
        callback.Invoke(form, [args]);
    }

    private static Bitmap Render(DisplayOverlayForm form)
    {
        var image = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        try
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static Bitmap Crop(DisplayOverlayForm form, Rectangle bounds)
    {
        using var image = Render(form);
        return image.Clone(bounds, PixelFormat.Format32bppArgb);
    }

    private static int DifferentPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return int.MaxValue;
        int difference = 0;
        for (int y = 0; y < first.Height; y += 2)
        for (int x = 0; x < first.Width; x += 2)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) difference++;
        return difference;
    }

    private static void PumpFor(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static void SaveContactSheet(IReadOnlyList<(string Category, string State, Bitmap Image)> captures, string path)
    {
        const int cellWidth = 360, cellHeight = 240, padding = 16, titleHeight = 28;
        using var sheet = new Bitmap(cellWidth * 3, cellHeight * 3);
        using var graphics = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 11);
        graphics.Clear(Color.FromArgb(38, 38, 41));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            int left = i % 3 * cellWidth, top = i / 3 * cellHeight;
            graphics.DrawString($"{capture.Category} / {capture.State}", font, Brushes.White, left + padding, top + padding);
            double scale = Math.Min((cellWidth - padding * 2d) / capture.Image.Width,
                (cellHeight - titleHeight - padding * 2d) / capture.Image.Height);
            int width = Math.Max(1, (int)(capture.Image.Width * scale)), height = Math.Max(1, (int)(capture.Image.Height * scale));
            graphics.DrawImage(capture.Image, new Rectangle(left + (cellWidth - width) / 2,
                top + titleHeight + (cellHeight - titleHeight - height) / 2, width, height));
        }
        sheet.Save(path, ImageFormat.Png);
    }
}
