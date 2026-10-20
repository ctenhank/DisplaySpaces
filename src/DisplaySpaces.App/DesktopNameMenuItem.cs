using DisplaySpaces.App.Localization;

namespace DisplaySpaces.App;

internal sealed class DesktopNameMenuItem : ToolStripMenuItem
{
    public DesktopNameMenuItem(DesktopNameToastCoordinator coordinator,
        Action<DesktopNameSettings> save, Action<Exception> reportError)
        : base(UiText.Get("Tray.NameDisplay"))
    {
        Name = "Tray.NameDisplay";
        var modes = new Dictionary<DesktopNameDisplayMode, ToolStripMenuItem>();
        var corners = new Dictionary<DesktopToastCorner, ToolStripMenuItem>();
        foreach (var mode in Enum.GetValues<DesktopNameDisplayMode>())
        {
            string key = "Tray.Name" + mode;
            var item = new ToolStripMenuItem(UiText.Get(key)) { Name = key };
            item.Click += (_, _) => Apply(new(mode, coordinator.Corner));
            modes.Add(mode, item);
            DropDownItems.Add(item);
        }
        DropDownItems.Add(new ToolStripSeparator());
        var position = new ToolStripMenuItem(UiText.Get("Tray.NamePosition")) { Name = "Tray.NamePosition" };
        foreach (var corner in Enum.GetValues<DesktopToastCorner>())
        {
            string key = "Tray." + corner;
            var item = new ToolStripMenuItem(UiText.Get(key)) { Name = key };
            item.Click += (_, _) => Apply(new(coordinator.Mode, corner));
            corners.Add(corner, item);
            position.DropDownItems.Add(item);
        }
        DropDownItems.Add(position);
        DropDownOpening += (_, _) => RefreshChecks();
        RefreshChecks();

        void RefreshChecks()
        {
            foreach (var (mode, item) in modes) item.Checked = coordinator.Mode == mode;
            foreach (var (corner, item) in corners) item.Checked = coordinator.Corner == corner;
        }

        void Apply(DesktopNameSettings settings)
        {
            try { save(settings); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportError(error);
                return;
            }
            coordinator.Corner = settings.Corner;
            coordinator.Mode = settings.Mode;
            RefreshChecks();
        }
    }
}
