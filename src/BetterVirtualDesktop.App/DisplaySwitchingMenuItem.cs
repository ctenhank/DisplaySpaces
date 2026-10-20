using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

internal sealed class DisplaySwitchingMenuItem : ToolStripMenuItem
{
    public DisplaySwitchingMenuItem(DesktopController controller,
        Action<DisplaySwitchingMode> save, Action<Exception> reportError)
        : base(UiText.Get("Tray.DisplaySwitching"))
    {
        Name = "Tray.DisplaySwitching";
        var choices = new Dictionary<DisplaySwitchingMode, ToolStripMenuItem>();
        foreach (var mode in Enum.GetValues<DisplaySwitchingMode>())
        {
            string key = "Tray.Switching" + mode;
            var item = new ToolStripMenuItem(UiText.Get(key))
            {
                Name = key,
                ToolTipText = UiText.Get(key + "Hint")
            };
            item.Click += (_, _) =>
            {
                try { save(mode); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    reportError(error);
                    return;
                }
                controller.SwitchingMode = mode;
                RefreshChecks();
            };
            choices.Add(mode, item);
            DropDownItems.Add(item);
        }
        DropDownOpening += (_, _) => RefreshChecks();
        RefreshChecks();

        void RefreshChecks()
        {
            foreach (var (mode, item) in choices) item.Checked = controller.SwitchingMode == mode;
        }
    }
}
