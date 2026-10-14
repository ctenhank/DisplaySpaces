using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterVirtualDesktop.App.Services;

namespace BetterVirtualDesktop.App.Native;

/// <summary>
/// Adds existing HWNDs to the shell taskbar without changing their styles or visibility.
/// Create, use, and dispose this object on the same STA thread with a Windows message loop.
/// The caller owns membership: it must delete manually added entries when no longer needed.
/// </summary>
internal sealed class TaskbarWindowRegistry : IDisposable
{
    private const long WsChild = 0x40000000;
    private const long WsExToolWindow = 0x80;
    private const long WsExAppWindow = 0x40000;
    private const long WsExNoActivate = 0x08000000;
    private const uint GwOwner = 4;
    private readonly uint _ownerThread = GetCurrentThreadId();
    private readonly BroadcastWindow _notifications;
    private ITaskbarList? _taskbar;
    private bool _disposed;
    private bool _failureLogged;

    public event Action? Recreated;

    public TaskbarWindowRegistry()
    {
        uint message = RegisterWindowMessage("TaskbarCreated");
        if (message == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the taskbar restart notification.");
        _notifications = new BroadcastWindow(message, TaskbarRecreated);
    }

    public bool TryAdd(long handle)
    {
        VerifyThread();
        if (_disposed || handle == 0 || !IsWindow(new nint(handle))) return false;
        return TryChange(handle, add: true);
    }

    public bool TryDelete(long handle)
    {
        VerifyThread();
        // DeleteTab is also required after an added window is destroyed, so do not
        // reject an HWND merely because IsWindow now returns false.
        if (_disposed || handle == 0) return false;
        return TryChange(handle, add: false);
    }

    /// <summary>Conservative taskbar eligibility; intentionally accepts hidden windows.</summary>
    public static bool CanAppearOnTaskbar(long handle)
    {
        var window = new nint(handle);
        if (window == nint.Zero || !IsWindow(window)) return false;
        long style = ReadStyle(window, -16);
        long extended = ReadStyle(window, -20);
        if ((style & WsChild) != 0 || (extended & (WsExToolWindow | WsExNoActivate)) != 0) return false;
        return (extended & WsExAppWindow) != 0 || GetWindow(window, GwOwner) == nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyThread();
        _disposed = true;
        Recreated = null;
        _notifications.DestroyHandle();
        ReleaseTaskbar();
    }

    private bool TryChange(long handle, bool add)
    {
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList)new TaskbarList();
                Marshal.ThrowExceptionForHR(_taskbar.HrInit());
            }
            int result = add ? _taskbar.AddTab(new nint(handle)) : _taskbar.DeleteTab(new nint(handle));
            Marshal.ThrowExceptionForHR(result);
            _failureLogged = false;
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
        {
            // A disconnected Explorer proxy cannot be reused. Retry with a fresh
            // object on the next request; avoid a log entry on every polling tick.
            ReleaseTaskbar();
            LogFailure(exception);
            return false;
        }
    }

    private void TaskbarRecreated()
    {
        if (_disposed) return;
        ReleaseTaskbar();
        try { Recreated?.Invoke(); }
        catch (Exception exception) { LogFailure(exception); }
    }

    private void ReleaseTaskbar()
    {
        var previous = _taskbar;
        _taskbar = null;
        if (previous is null) return;
        try { Marshal.FinalReleaseComObject(previous); }
        catch (Exception exception) when (exception is COMException or InvalidComObjectException)
        {
            LogFailure(exception);
        }
    }

    private void LogFailure(Exception exception)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        AppLog.Write(exception);
    }

    private void VerifyThread()
    {
        if (GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("Taskbar registration must run on its owning UI thread.");
    }

    private static long ReadStyle(nint window, int index) => nint.Size == 8
        ? GetWindowLongPtr(window, index).ToInt64() : GetWindowLong(window, index);

    private sealed class BroadcastWindow : NativeWindow
    {
        private readonly uint _message;
        private readonly Action _recreated;

        public BroadcastWindow(uint message, Action recreated)
        {
            _message = message;
            _recreated = recreated;
            // Broadcasts are not delivered to HWND_MESSAGE windows. This unshown
            // top-level window receives TaskbarCreated without a taskbar button.
            CreateHandle(new CreateParams
            {
                Caption = "BetterVirtualDesktop taskbar notifications",
                Parent = nint.Zero,
                Style = 0,
                ExStyle = (int)(WsExToolWindow | WsExNoActivate)
            });
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == _message) _recreated();
            base.WndProc(ref message);
        }
    }

    // ITaskbarList's complete vtable, in its documented order after IUnknown.
    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint window);
        [PreserveSig] int DeleteTab(nint window);
        [PreserveSig] int ActivateTab(nint window);
        [PreserveSig] int SetActiveAlt(nint window);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
}
