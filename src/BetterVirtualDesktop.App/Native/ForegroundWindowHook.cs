using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterVirtualDesktop.App.Services;

namespace BetterVirtualDesktop.App.Native;

/// <summary>
/// Observes foreground and hidden HWNDs on the installing message-loop thread, without
/// injecting into other processes. Construct and dispose on that same thread. Handlers should
/// only enqueue work, because Windows can reenter an out-of-context event callback.
/// </summary>
internal sealed class ForegroundWindowHook : IDisposable
{
    private const uint EventSystemForeground = 3;
    private const uint EventObjectHide = 0x8003;
    private const uint WinEventOutOfContext = 0;
    private const uint WinEventSkipOwnProcess = 2;
    private readonly uint _ownerThread = GetCurrentThreadId();
    private readonly WinEventProcedure _callback;
    private GCHandle _callbackRoot;
    private nint _hook;
    private nint _hideHook;
    private bool _disposed;

    public event Action<long, uint>? Activated;
    public event Action<long, uint>? Hidden;

    /// <param name="processId">Zero observes all other processes; a fixture PID limits test observation.</param>
    public ForegroundWindowHook(uint processId = 0)
    {
        _callback = OnWinEvent;
        _callbackRoot = GCHandle.Alloc(_callback);
        try
        {
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero,
                _callback, processId, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_hook == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe foreground window changes.");
            _hideHook = SetWinEventHook(EventObjectHide, EventObjectHide, nint.Zero,
                _callback, processId, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_hideHook == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe hidden window changes.");
        }
        catch
        {
            // The first hook may already be live when the second installation fails.
            _disposed = true;
            ReleaseHooks();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("The foreground hook must be removed on the thread that installed it.");
        _disposed = true;
        Activated = null;
        Hidden = null;
        ReleaseHooks();
    }

    private void ReleaseHooks()
    {
        // Always attempt both removals; a failure in one must not leave the other live.
        bool foregroundRemoved = TryRemove(ref _hook, "foreground");
        bool hideRemoved = TryRemove(ref _hideHook, "hidden");
        // Keep the shared delegate rooted if either hook could still call it. The
        // disposed callback ignores events; Windows removes hooks on thread exit.
        if (foregroundRemoved && hideRemoved && _callbackRoot.IsAllocated) _callbackRoot.Free();
        GC.KeepAlive(_callback);
    }

    private static bool TryRemove(ref nint hook, string name)
    {
        if (hook == nint.Zero) return true;
        if (!UnhookWinEvent(hook))
        {
            AppLog.Write(new Win32Exception(Marshal.GetLastWin32Error(), $"Could not remove the {name} window hook."));
            return false;
        }
        hook = nint.Zero;
        return true;
    }

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_disposed || window == nint.Zero) return;
        try
        {
            if (eventType == EventSystemForeground) Activated?.Invoke(window.ToInt64(), eventTime);
            else if (eventType == EventObjectHide && objectId == 0 && childId == 0)
                Hidden?.Invoke(window.ToInt64(), eventTime);
        }
        catch (Exception exception)
        {
            // Never allow managed exceptions to cross the native callback boundary.
            AppLog.Write(exception);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(nint hook, uint eventType, nint window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint eventMin,
        uint eventMax, nint module, WinEventProcedure callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
}
