using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Services;

namespace DisplaySpaces.App;

/// <summary>Reflects Windows registration directly; opening the menu never enables startup.</summary>
internal sealed class StartupMenuItem : ToolStripMenuItem
{
    private readonly StartupRegistration _registration;
    private readonly string? _executablePath;
    private readonly Action<Exception> _onError;

    public StartupMenuItem(StartupRegistration registration, string? executablePath, Action<Exception> onError)
    {
        _registration = registration;
        _executablePath = executablePath;
        _onError = onError;
        CheckOnClick = false;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        Text = UiText.Get("Tray.StartWithWindows");
        try
        {
            string expected = StartupRegistration.BuildCommand(ExecutablePath());
            string? registered = _registration.ReadCommand();
            Checked = string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase);
            Enabled = true;
            ToolTipText = UiText.Get("Startup.AutoRunHint");
            if (registered is not null && !Checked)
                ToolTipText = UiText.Get("Startup.AutoRunDifferentPath") + "\n" + ToolTipText;
        }
        catch (Exception error) when (IsRegistrationError(error))
        {
            CheckState = CheckState.Indeterminate;
            Enabled = false;
            ToolTipText = UiText.Get("Startup.AutoRunUnavailable") + "\n" + error.Message;
        }
    }

    protected override void OnClick(EventArgs e)
    {
        // Re-read before toggling, including changes made while the menu was open.
        RefreshStatus();
        if (!Enabled) return;
        try
        {
            _registration.SetEnabled(!Checked, ExecutablePath());
            RefreshStatus();
        }
        catch (Exception error) when (IsRegistrationError(error))
        {
            RefreshStatus();
            _onError(error);
        }
        base.OnClick(e);
    }

    private string ExecutablePath() => _executablePath
        ?? throw new InvalidOperationException(UiText.Get("Startup.NoExecutable"));

    private static bool IsRegistrationError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or InvalidOperationException;
}
