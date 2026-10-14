using Microsoft.Win32;

namespace BetterVirtualDesktop.App.Services;

internal sealed class StartupRegistration
{
    public const string ValueName = "BetterVirtualDesktop";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registrySubKey;

    public StartupRegistration(string? registrySubKey = null)
    {
        _registrySubKey = registrySubKey ?? RunKeyPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(_registrySubKey);
    }

    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public bool IsEnabledFor(string executablePath) =>
        string.Equals(ReadCommand(), BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled)
        {
            string command = BuildCommand(executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(_registrySubKey, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)
            || executablePath.IndexOfAny(['"', '\r', '\n']) >= 0
            || executablePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires an absolute Windows executable path.", nameof(executablePath));

        // Registering the dotnet host alone would omit the application assembly and fail at sign-in.
        if (Path.GetFileName(executablePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires the application's executable, not the dotnet host.", nameof(executablePath));

        string command = $"\"{executablePath}\" --startup";
        // Windows Run values allow at most 260 characters for the complete command line.
        if (command.Length > 260)
            throw new ArgumentException("The startup command cannot exceed 260 characters.", nameof(executablePath));
        return command;
    }
}
