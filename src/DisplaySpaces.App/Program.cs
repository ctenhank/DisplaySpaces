using DisplaySpaces.App.Services;
using DisplaySpaces.App.Localization;

namespace DisplaySpaces.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            // Fixture/test runs must neither depend on nor replace the user's language preference.
            if (args.Length > 0 && args[0] is "--self-test" or "--self-test-startup" or "--self-test-moves" or "--self-test-taskbar" or "--self-test-desktop-names" or "--self-test-display-switching" or "--self-test-move-follow" or "--self-test-desktop-reorder" or "--self-test-settings" or "--taskbar-fixture" or "--fixture" or "--crash-fixture")
            {
                // A UI callback failure must fail automation rather than open a modal error dialog.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                UiText.SetLanguage("ko");
            }
            else UiText.Initialize();
            if (args is ["--watch", var pid, var started, var path, var ready])
            {
                ProcessLauncher.Watch(int.Parse(pid), long.Parse(started), path, ready);
                return 0;
            }
            if (args is ["--recover"])
            {
                RecoveryJournal.RecoverOrphans();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--self-test") return SmokeTests.Run(args.Skip(1).FirstOrDefault());
            if (args is ["--self-test-startup", var outputDirectory]) return StartupSelfTests.RunStandalone(outputDirectory);
            if (args is ["--self-test-moves", var moveOutput]) return MoveBehaviorSelfTests.RunStandalone(moveOutput);
            if (args is ["--self-test-taskbar", var taskbarOutput]) return TaskbarActivationSelfTests.RunStandalone(taskbarOutput);
            if (args is ["--self-test-desktop-names", var nameOutput]) return DesktopNameSelfTests.RunStandalone(nameOutput);
            if (args is ["--self-test-display-switching", var switchingOutput]) return DisplaySwitchingSelfTests.RunStandalone(switchingOutput);
            if (args is ["--self-test-move-follow", var moveFollowOutput]) return WindowMoveFollowSelfTests.RunStandalone(moveFollowOutput);
            if (args is ["--self-test-desktop-reorder", var reorderOutput]) return DesktopReorderSelfTests.RunStandalone(reorderOutput);
            if (args is ["--self-test-settings", var settingsOutput]) return SettingsSelfTests.RunStandalone(settingsOutput);
            if (args is ["--taskbar-fixture", var taskbarFixtureOutput]) return TaskbarActivationSelfTests.RunInteractive(taskbarFixtureOutput);
            if (args is ["--fixture", var manifest]) return SmokeTests.RunFixture(manifest);
            if (args is ["--crash-fixture", var journal, var manifestPath]) return SmokeTests.RunCrashFixture(journal, manifestPath);
            bool startupLaunch = args is ["--startup"];
            // Keep the legacy identity so old and renamed builds cannot manage windows concurrently.
            using var mutex = new Mutex(true, @"Local\IndependentVirtualDesktop.Singleton", out bool isFirst);
            if (!isFirst)
            {
                if (!startupLaunch) MessageBox.Show(UiText.Get("Startup.AlreadyRunning"), "DisplaySpaces");
                return 0;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            using var context = new DesktopApplicationContext(showOnStartup: !startupLaunch);
            Application.Run(context);
            GC.KeepAlive(mutex);
            return 0;
        }
        catch (Exception error)
        {
            AppLog.Write(error);
            // Background recovery/test helpers must fail back to their caller rather
            // than waiting indefinitely in a modal message box.
            if (args.Length == 0) MessageBox.Show(error.Message, "DisplaySpaces", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
