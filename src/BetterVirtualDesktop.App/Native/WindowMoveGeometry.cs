namespace BetterVirtualDesktop.App.Native;

/// <summary>Screen-coordinate geometry; WINDOWPLACEMENT conversion stays at the native boundary.</summary>
internal static class WindowMoveGeometry
{
    public static Rectangle[] MapGroup(IReadOnlyList<Rectangle> windows, Rectangle sourceArea, Rectangle targetArea)
    {
        if (windows.Count == 0) return [];
        if (sourceArea.Width <= 0 || sourceArea.Height <= 0 || targetArea.Width <= 0 || targetArea.Height <= 0
            || windows.Any(r => r.Width <= 0 || r.Height <= 0))
            throw new ArgumentException("Window and working-area bounds must have a positive size.");
        var group = windows.Aggregate(Rectangle.Union);
        double scale = Math.Min(1, Math.Min((double)targetArea.Width / group.Width, (double)targetArea.Height / group.Height));
        int groupWidth = Math.Max(1, (int)Math.Round(group.Width * scale));
        int groupHeight = Math.Max(1, (int)Math.Round(group.Height * scale));
        double relativeX = Math.Clamp((double)(group.Left - sourceArea.Left) / Math.Max(1, sourceArea.Width - group.Width), 0, 1);
        double relativeY = Math.Clamp((double)(group.Top - sourceArea.Top) / Math.Max(1, sourceArea.Height - group.Height), 0, 1);
        int left = targetArea.Left + (int)Math.Round(relativeX * Math.Max(0, targetArea.Width - groupWidth));
        int top = targetArea.Top + (int)Math.Round(relativeY * Math.Max(0, targetArea.Height - groupHeight));
        return windows.Select(rect =>
        {
            int x = left + (int)Math.Round((rect.Left - group.Left) * scale);
            int y = top + (int)Math.Round((rect.Top - group.Top) * scale);
            int right = left + (int)Math.Round((rect.Right - group.Left) * scale);
            int bottom = top + (int)Math.Round((rect.Bottom - group.Top) * scale);
            return Rectangle.FromLTRB(x, y, Math.Max(x + 1, right), Math.Max(y + 1, bottom));
        }).ToArray();
    }

    public static Rectangle PlacementToScreen(Rectangle rect, Rectangle monitorArea, Rectangle workArea, bool toolWindow)
    {
        if (!toolWindow) rect.Offset(workArea.Left - monitorArea.Left, workArea.Top - monitorArea.Top);
        return rect;
    }

    public static Rectangle ScreenToPlacement(Rectangle rect, Rectangle monitorArea, Rectangle workArea, bool toolWindow)
    {
        if (!toolWindow) rect.Offset(monitorArea.Left - workArea.Left, monitorArea.Top - workArea.Top);
        return rect;
    }
}
