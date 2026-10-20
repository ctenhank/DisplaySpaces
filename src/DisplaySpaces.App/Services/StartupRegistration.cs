using Microsoft.Win32;

namespace DisplaySpaces.App.Services;

internal sealed class StartupRegistration
{
    public const string ValueName = "DisplaySpaces";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] LegacyValueNames = ["BetterVirtualDesktop", "IndependentVirtualDesktop"];
    private readonly string _registrySubKey;

    public StartupRegistration(string? registrySubKey = null)
    {
        _registrySubKey = registrySubKey ?? RunKeyPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(_registrySubKey);
    }

    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey);
        if (key is null) return null;
        object? current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is not null) return current as string;
        // Reading a renamed installation must not enable it or alter Windows' startup approval.
        foreach (string legacyName in LegacyValueNames)
            if (ReadOwnedLegacyCommand(key, legacyName) is string command) return command;
        return null;
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
            RemoveOwnedLegacyValues(key);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            if (key is not null) RemoveOwnedLegacyValues(key);
        }
    }

    private static void RemoveOwnedLegacyValues(RegistryKey key)
    {
        // Only an explicit user change migrates startup. Do not remove arbitrary values
        // that happen to use a former product name, or touch another app's approval state.
        foreach (string legacyName in LegacyValueNames)
            if (ReadOwnedLegacyCommand(key, legacyName) is not null)
                key.DeleteValue(legacyName, throwOnMissingValue: false);
    }

    private static string? ReadOwnedLegacyCommand(RegistryKey key, string legacyName)
    {
        if (key.GetValue(legacyName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command
            || !command.StartsWith('"')) return null;
        int closingQuote = command.IndexOf('"', 1);
        if (closingQuote <= 1) return null;
        string executablePath = command[1..closingQuote];
        if (!Path.GetFileName(executablePath).Equals(legacyName + ".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return command.Equals(BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase) ? command : null;
        }
        catch (ArgumentException) { return null; }
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
