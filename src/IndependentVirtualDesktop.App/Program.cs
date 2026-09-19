using IndependentVirtualDesktop.App.Services;

namespace IndependentVirtualDesktop.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
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
            using var mutex = new Mutex(true, @"Local\IndependentVirtualDesktop.Singleton", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("이미 실행 중입니다. Win+Tab 또는 알림 영역 아이콘에서 작업 보기를 여세요.", "Independent Spaces");
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
            if (args.Length == 0) MessageBox.Show(error.Message, "Independent Spaces", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
