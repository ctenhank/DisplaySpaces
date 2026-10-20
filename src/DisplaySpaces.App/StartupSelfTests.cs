using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Services;
using Microsoft.Win32;

namespace DisplaySpaces.App;

/// <summary>Uses a disposable non-startup HKCU key; never registers any app to run at login.</summary>
internal static class StartupSelfTests
{
    public static int RunStandalone(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        int exitCode = 0;
        try { Run(results); }
        catch (Exception error) { results.Add("FAIL " + error); exitCode = 1; }
        File.WriteAllLines(Path.Combine(output, "startup-results.txt"), results);
        return exitCode;
    }

    public static void Run(List<string> results)
    {
        const string executable = @"C:\테스트 앱\DisplaySpaces.exe";
        const string oldExecutable = @"C:\Previous Version\DisplaySpaces.exe";
        string testKey = @"Software\DisplaySpaces\SelfTests\Startup\" + Guid.NewGuid().ToString("N");
        string previousLanguage = UiText.SelectedLanguageCode;
        var startup = new StartupRegistration(testKey);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            results.Add("PASS " + message);
        }
        static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
        try
        {
            Check(StartupRegistration.BuildCommand(executable) == "\"" + executable + "\" --startup",
                "startup command quotes Unicode and spaced executable paths and requests background launch");
            Check(new[] { "", "DisplaySpaces.exe", @"C:relative.exe", @"C:\app.dll", @"C:\dotnet.exe",
                "C:\\bad\"path.exe", "C:\\bad\npath.exe" }.All(path => Rejected(() => StartupRegistration.BuildCommand(path))),
                "startup rejects relative paths, host executables, non-executables and command injection characters");
            string longest = @"C:\" + new string('a', 241) + ".exe";
            Check(StartupRegistration.BuildCommand(longest).Length == 260
                && Rejected(() => StartupRegistration.BuildCommand(@"C:\" + new string('a', 242) + ".exe")),
                "startup validates Windows Run's complete command-line length before registration");
            startup.SetEnabled(false, "");
            using (var missing = Registry.CurrentUser.OpenSubKey(testKey))
                Check(missing is null && startup.ReadCommand() is null && !startup.IsEnabledFor(executable),
                    "reading or disabling startup in an absent key creates no registration");

            CheckLegacyMigration(startup, testKey, executable, Check);
            using (var key = Registry.CurrentUser.CreateSubKey(testKey)) key.SetValue("FixtureOtherApp", 42);
            var errors = new List<Exception>();
            using var menu = new StartupMenuItem(startup, executable, errors.Add);
            Check(!menu.Checked && menu.Enabled && startup.ReadCommand() is null,
                "creating the startup menu leaves automatic startup off");
            menu.PerformClick();
            using (var key = Registry.CurrentUser.OpenSubKey(testKey)!)
                Check(menu.Checked && startup.IsEnabledFor(executable)
                    && key.GetValueKind(StartupRegistration.ValueName) == RegistryValueKind.String,
                    "startup menu enables a quoted REG_SZ registration only after an explicit click");
            Check(new StartupRegistration(testKey).IsEnabledFor(executable.ToUpperInvariant()),
                "startup registration survives a new service instance and compares Windows paths without case sensitivity");
            string? saved = startup.ReadCommand();
            Check(Rejected(() => startup.SetEnabled(true, "relative.exe")) && startup.ReadCommand() == saved,
                "invalid startup registration leaves the previous value unchanged");
            menu.PerformClick();
            using (var key = Registry.CurrentUser.OpenSubKey(testKey)!)
                Check(!menu.Checked && startup.ReadCommand() is null && (int)key.GetValue("FixtureOtherApp")! == 42,
                    "disabling startup removes only this app's entry and preserves other applications");

            startup.SetEnabled(true, oldExecutable);
            menu.RefreshStatus();
            Check(!menu.Checked && menu.Enabled && menu.ToolTipText?.Contains(UiText.Get("Startup.AutoRunDifferentPath")) == true,
                "startup menu identifies registration of a different installation without rewriting it on read");
            menu.PerformClick();
            Check(menu.Checked && startup.IsEnabledFor(executable),
                "enabling startup from a moved application updates its registered executable path");
            startup.SetEnabled(false, executable);
            menu.RefreshStatus();
            startup.SetEnabled(true, executable);
            menu.PerformClick();
            Check(!menu.Checked && startup.ReadCommand() is null,
                "startup toggle rereads external changes before deciding whether to enable or disable");
            using (var key = Registry.CurrentUser.OpenSubKey(testKey, writable: true)!)
                key.SetValue(StartupRegistration.ValueName, 99, RegistryValueKind.DWord);
            Check(startup.ReadCommand() is null, "non-string startup registry values are handled without a cast failure");
            menu.PerformClick();
            Check(menu.Checked && startup.IsEnabledFor(executable), "startup menu can replace malformed registration with a valid command");
            using (var invalid = new StartupMenuItem(startup, @"C:\dotnet.exe", errors.Add))
                Check(!invalid.Enabled && invalid.CheckState == CheckState.Indeterminate && startup.IsEnabledFor(executable),
                    "unsupported application hosts cannot accidentally replace an existing startup registration");
            foreach (var language in UiText.SupportedLanguages)
            {
                UiText.SetLanguage(language.Code, persist: false);
                menu.RefreshStatus();
                Check(menu.Checked && menu.Text == UiText.Get("Tray.StartWithWindows")
                    && menu.ToolTipText == UiText.Get("Startup.AutoRunHint"),
                    $"startup menu follows the selected UI language without changing registration: {language.Code}");
            }
            startup.SetEnabled(false, "");
            Check(startup.ReadCommand() is null && errors.Count == 0,
                "startup registration can be removed even when an executable path is no longer available");
        }
        finally
        {
            UiText.SetLanguage(previousLanguage, persist: false);
            // The GUID key above is the entire test boundary; never delete Run or an ancestor key.
            Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
        }
    }

    private static void CheckLegacyMigration(StartupRegistration startup, string testKey, string executable,
        Action<bool, string> check)
    {
        const string previousName = "BetterVirtualDesktop";
        const string originalName = "IndependentVirtualDesktop";
        string previousCommand = StartupRegistration.BuildCommand(@"C:\Previous Version\BetterVirtualDesktop.exe");
        string originalCommand = StartupRegistration.BuildCommand(@"C:\Original Version\IndependentVirtualDesktop.exe");
        using var key = Registry.CurrentUser.CreateSubKey(testKey);
        key.SetValue(previousName, previousCommand);
        check(startup.ReadCommand() == previousCommand && !startup.IsEnabledFor(executable)
            && key.GetValue(StartupRegistration.ValueName) is null && (string?)key.GetValue(previousName) == previousCommand,
            "reading a recognized legacy registration shows its location without migrating or enabling startup");
        var errors = new List<Exception>();
        using var menu = new StartupMenuItem(startup, executable, errors.Add);
        check(!menu.Checked && menu.ToolTipText?.Contains(UiText.Get("Startup.AutoRunDifferentPath")) == true
            && key.GetValue(StartupRegistration.ValueName) is null,
            "startup menu identifies the former product registration without changing it");
        key.SetValue(originalName, originalCommand);
        bool rejected = false;
        try { startup.SetEnabled(true, "relative.exe"); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && (string?)key.GetValue(previousName) == previousCommand
            && (string?)key.GetValue(originalName) == originalCommand && key.GetValue(StartupRegistration.ValueName) is null,
            "invalid replacement executable leaves both legacy registrations unchanged");
        menu.PerformClick();
        check(menu.Checked && startup.IsEnabledFor(executable) && key.GetValue(previousName) is null
            && key.GetValue(originalName) is null && errors.Count == 0,
            "explicit startup enable registers DisplaySpaces and removes both recognized legacy app commands");
        key.SetValue(previousName, previousCommand);
        check(startup.ReadCommand() == StartupRegistration.BuildCommand(executable),
            "current DisplaySpaces registration takes precedence over a legacy registration");
        startup.SetEnabled(false, "");
        check(key.GetValue(StartupRegistration.ValueName) is null && key.GetValue(previousName) is null,
            "explicit startup disable removes a recognized legacy registration even without an executable path");

        object[] unrecognized =
        [
            StartupRegistration.BuildCommand(@"C:\Other App\OtherApp.exe"),
            previousCommand + " --extra",
            @"C:\Previous Version\BetterVirtualDesktop.exe --startup",
            "\"BetterVirtualDesktop.exe\" --startup",
            "\"C:\\bad\npath\\BetterVirtualDesktop.exe\" --startup",
            42
        ];
        foreach (object command in unrecognized)
        {
            key.SetValue(previousName, command);
            check(startup.ReadCommand() is null,
                $"legacy ownership validation rejects an unrecognized startup value ({Array.IndexOf(unrecognized, command) + 1})");
            startup.SetEnabled(true, executable);
            check(Equals(key.GetValue(previousName), command),
                $"enabling startup preserves an unrecognized former-name value ({Array.IndexOf(unrecognized, command) + 1})");
            startup.SetEnabled(false, "");
            check(Equals(key.GetValue(previousName), command),
                $"disabling startup preserves an unrecognized former-name value ({Array.IndexOf(unrecognized, command) + 1})");
        }
        key.SetValue(previousName, previousCommand.ToUpperInvariant());
        startup.SetEnabled(true, executable);
        check(key.GetValue(previousName) is null && startup.IsEnabledFor(executable),
            "legacy startup ownership accepts Windows paths without case sensitivity");
        startup.SetEnabled(false, "");
    }
}
