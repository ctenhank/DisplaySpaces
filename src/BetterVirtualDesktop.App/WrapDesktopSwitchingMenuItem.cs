using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Services;

namespace BetterVirtualDesktop.App;

internal sealed class WrapDesktopSwitchingMenuItem : ToolStripMenuItem
{
    public WrapDesktopSwitchingMenuItem(DesktopController controller,
        Action<bool> save, Action<Exception> reportError) : base(UiText.Get("Tray.WrapDesktopSwitching"))
    {
        Name = "Tray.WrapDesktopSwitching";
        ToolTipText = UiText.Get("Tray.WrapDesktopSwitchingHint");
        Checked = controller.WrapDesktopSwitching;
        Click += (_, _) =>
        {
            bool wrapAround = !controller.WrapDesktopSwitching;
            try { save(wrapAround); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportError(error);
                return;
            }
            controller.WrapDesktopSwitching = wrapAround;
            Checked = wrapAround;
        };
    }
}
