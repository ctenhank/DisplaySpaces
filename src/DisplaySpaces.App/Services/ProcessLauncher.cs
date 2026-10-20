using System.Diagnostics;
using System.Reflection;
using DisplaySpaces.App.Localization;

namespace DisplaySpaces.App.Services;

internal static class ProcessLauncher
{
    public static Process Start(params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException(UiText.Get("Startup.NoExecutable"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException(UiText.Get("Startup.HelperFailed"));
    }

    public static Process StartGuardian(string journalPath)
    {
        // Keep the watchdog readiness protocol independent of the product's display name.
        string eventName = @"Local\IndependentVirtualDesktop.Guardian." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var owner = Process.GetCurrentProcess();
        var guardian = Start("--watch", owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString(), journalPath, eventName);
        if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
        {
            guardian.Dispose();
            throw new InvalidOperationException(UiText.Get("Startup.GuardianFailed"));
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
