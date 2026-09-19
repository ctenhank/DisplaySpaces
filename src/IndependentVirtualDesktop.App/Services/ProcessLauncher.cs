using System.Diagnostics;
using System.Reflection;

namespace IndependentVirtualDesktop.App.Services;

internal static class ProcessLauncher
{
    public static Process Start(params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 찾을 수 없습니다.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("보조 프로세스를 시작할 수 없습니다.");
    }

    public static Process StartGuardian(string journalPath)
    {
        string eventName = @"Local\IndependentVirtualDesktop.Guardian." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var owner = Process.GetCurrentProcess();
        var guardian = Start("--watch", owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString(), journalPath, eventName);
        if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
        {
            guardian.Dispose();
            throw new InvalidOperationException("창 복구 감시 프로세스를 시작하지 못했습니다.");
        }
        return guardian;
    }

    public static void Watch(int pid, long start, string path, string readyName)
    {
        using var ready = EventWaitHandle.OpenExisting(readyName);
        ready.Set();
        // Parent exit can race with Process.StartTime/WaitForExit. OwnerAlive also
        // handles access failures conservatively, so this watchdog does not die after
        // announcing readiness or recover while an uninspectable owner is still alive.
        while (RecoveryJournal.OwnerAlive(pid, start)) Thread.Sleep(250);
        RecoveryJournal.RecoverFile(path);
    }
}
