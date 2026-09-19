using BetterVirtualDesktop.App.Services;
using BetterVirtualDesktop.App.Localization;

namespace BetterVirtualDesktop.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            // Fixture/test runs must neither depend on nor replace the user's language preference.
            if (args.Length > 0 && args[0] is "--self-test" or "--fixture" or "--crash-fixture")
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
            if (args is ["--fixture", var manifest]) return SmokeTests.RunFixture(manifest);
            if (args is ["--crash-fixture", var journal, var manifestPath]) return SmokeTests.RunCrashFixture(journal, manifestPath);
            // Keep the legacy identity so old and renamed builds cannot manage windows concurrently.
            using var mutex = new Mutex(true, @"Local\IndependentVirtualDesktop.Singleton", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show(UiText.Get("Startup.AlreadyRunning"), "BetterVirtualDesktop");
                return 0;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            using var context = new DesktopApplicationContext();
            Application.Run(context);
            GC.KeepAlive(mutex);
            return 0;
        }
        catch (Exception error)
        {
            AppLog.Write(error);
            // Background recovery/test helpers must fail back to their caller rather
            // than waiting indefinitely in a modal message box.
            if (args.Length == 0) MessageBox.Show(error.Message, "BetterVirtualDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
