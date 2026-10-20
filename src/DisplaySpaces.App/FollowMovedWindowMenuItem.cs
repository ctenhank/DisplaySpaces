using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Services;

namespace DisplaySpaces.App;

internal sealed class FollowMovedWindowMenuItem : ToolStripMenuItem
{
    public FollowMovedWindowMenuItem(DesktopController controller,
        Action<bool> save, Action<Exception> reportError) : base(UiText.Get("Tray.FollowMovedWindow"))
    {
        Name = "Tray.FollowMovedWindow";
        ToolTipText = UiText.Get("Tray.FollowMovedWindowHint");
        Checked = controller.FollowMovedWindow;
        Click += (_, _) =>
        {
            bool follow = !controller.FollowMovedWindow;
            try { save(follow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportError(error);
                return;
            }
            controller.FollowMovedWindow = follow;
            Checked = follow;
        };
    }
}
