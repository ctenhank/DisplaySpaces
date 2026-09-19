using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace IndependentVirtualDesktop.App.Native;

public sealed record NativeWindowInfo(
    long Handle,
    long RootOwner,
    int ProcessId,
    long ProcessStartTicks,
    string ClassName,
    string Title,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized,
    bool IsCloaked,
    Rectangle Bounds);

/// <summary>Documented Windows APIs only. Visibility operations are asynchronous.</summary>
public static class NativeWindows
{
    private const string RecoveryProperty = "IndependentVirtualDesktop.Recovery";
    private const long WsChild = 0x40000000;
    private const long WsExToolWindow = 0x80;
    private const long WsExAppWindow = 0x40000;
    private const long WsExNoActivate = 0x08000000;
    private const uint GwOwner = 4;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint DwmwaCloaked = 14;
    private const int SwHide = 0;
    private const int SwShowMinNoActive = 7;
    private const int SwShowNa = 8;
    private const int SwRestore = 9;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost",
        "MsgrIMEWindowClass", "SysShadow", "IME", "tooltips_class32", "#32768",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "Windows.UI.Composition.DesktopWindowContentBridge"
    };

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "Widgets", "WidgetService", "LockApp"
    };

    // A COM object must not be shared across the UI and recovery threads/apartments.
    [ThreadStatic] private static IVirtualDesktopManager? virtualDesktopManager;

    public static long ForegroundWindow => GetForegroundWindow().ToInt64();

    /// <summary>
    /// Includes eligible hidden windows so callers can recognize their own hidden windows.
    /// Discover NEW windows only when visible and on the current native virtual desktop.
    /// A zero ProcessStartTicks means identity could not be proven: never hide that window.
    /// </summary>
    public static IReadOnlyList<NativeWindowInfo> EnumerateWindows()
    {
        var windows = new List<(NativeWindowInfo Window, bool RequiresRoot)>();
        var processes = new Dictionary<int, (long Started, bool IsShell)>();
        var selfPid = Environment.ProcessId;
        EnumWindowsCallback callback = (handle, _) =>
        {
            if (!IsWindow(handle)) return true;
            GetWindowThreadProcessId(handle, out var rawPid);
            if (rawPid is 0 or > int.MaxValue || rawPid == selfPid) return true;
            var pid = (int)rawPid;
            var className = ReadClassName(handle);
            if (className.Length == 0 || ShellClasses.Contains(className)) return true;

            var style = GetWindowStyle(handle, -16);
            var extendedStyle = GetWindowStyle(handle, -20);
            if ((style & WsChild) != 0) return true;
            var root = ReadRootOwner(handle);
            var owned = root != nint.Zero && root != handle;
            var auxiliary = (extendedStyle & WsExNoActivate) != 0
                || ((extendedStyle & WsExToolWindow) != 0 && (extendedStyle & WsExAppWindow) == 0);
            if (auxiliary && !owned) return true;

            if (!processes.TryGetValue(pid, out var process))
            {
                process = ReadProcessIdentity(pid);
                processes.Add(pid, process);
            }
            if (process.IsShell) return true;

            var title = ReadTitle(handle);
            if (title.Length == 0 && !owned) return true;
            var rectangle = ReadBounds(handle);
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return true;
            // A failed DWM query is treated as cloaked, keeping discovery conservative.
            var cloakStatus = DwmGetWindowAttribute(handle, DwmwaCloaked, out uint cloaked, sizeof(uint));
            windows.Add((new NativeWindowInfo(handle.ToInt64(), (root == nint.Zero ? handle : root).ToInt64(),
                pid, process.Started, className, title, IsWindowVisible(handle), IsIconic(handle),
                IsZoomed(handle), cloakStatus < 0 || cloaked != 0, rectangle), auxiliary || title.Length == 0));
            return true;
        };
        if (!EnumWindows(callback, nint.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Keep owned dialogs and floating tools with their normal app window. Leaving them
        // behind would leak them onto another desktop or let implicit owner hiding go unjournaled.
        var roots = windows.Where(item => !item.RequiresRoot)
            .SelectMany(item => new[] { item.Window.Handle, item.Window.RootOwner }).ToHashSet();
        return windows.Where(item => !item.RequiresRoot || roots.Contains(item.Window.RootOwner))
            .Select(item => item.Window).ToArray();
    }

    /// <summary>Returns false on COM/query failure; never guesses that a foreign desktop is current.</summary>
    public static bool IsOnCurrentVirtualDesktop(long handle)
    {
        if (handle == 0 || !IsWindow(new nint(handle))) return false;
        try
        {
            virtualDesktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
            return virtualDesktopManager.IsWindowOnCurrentVirtualDesktop(new nint(handle), out var current) >= 0 && current;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
        {
            // Explorer restart, an unavailable shell, or a disconnected COM proxy must not
            // cause windows on another native desktop to be adopted by this application.
            virtualDesktopManager = null;
            return false;
        }
    }

    /// <summary>Identity is intentionally unprovable when process start time was inaccessible.</summary>
    public static bool IsSameWindow(long handle, int processId, long processStartTicks, string className)
    {
        if (handle == 0 || processId <= 0 || processStartTicks <= 0 || string.IsNullOrEmpty(className)) return false;
        var nativeHandle = new nint(handle);
        if (!IsWindow(nativeHandle)) return false;
        GetWindowThreadProcessId(nativeHandle, out var actualPid);
        return actualPid == processId && ReadClassName(nativeHandle) == className
            && ReadProcessIdentity(processId).Started == processStartTicks;
    }

    public static bool SetRecoveryMarker(long handle, long token)
    {
        if (handle == 0 || token <= 0 || (nint.Size == 4 && token > int.MaxValue)) return false;
        return SetProp(new nint(handle), RecoveryProperty, new nint(token));
    }

    public static long GetRecoveryMarker(long handle) => GetProp(new nint(handle), RecoveryProperty).ToInt64();

    public static void ClearRecoveryMarker(long handle) => RemoveProp(new nint(handle), RecoveryProperty);

    /// <summary>True means the request was enqueued; callers must confirm visibility later.</summary>
    public static bool Hide(long handle) => handle != 0 && ShowWindowAsync(new nint(handle), SwHide);

    /// <summary>
    /// SW_HIDE retains normal/maximized placement. SW_SHOWNA reveals that placement without
    /// stealing focus; SW_SHOWMINNOACTIVE keeps originally minimized windows minimized.
    /// </summary>
    public static bool Show(long handle, bool minimized, bool maximized)
    {
        _ = maximized; // Deliberately preserve placement instead of re-maximizing and activating.
        return handle != 0 && ShowWindowAsync(new nint(handle), minimized ? SwShowMinNoActive : SwShowNa);
    }

    public static bool IsVisible(long handle) => handle != 0 && IsWindowVisible(new nint(handle));
    public static bool IsMinimized(long handle) => handle != 0 && IsIconic(new nint(handle));
    public static bool IsMaximized(long handle) => handle != 0 && IsZoomed(new nint(handle));

    public static void Activate(long handle)
    {
        var nativeHandle = new nint(handle);
        if (!IsWindow(nativeHandle) || !IsOnCurrentVirtualDesktop(handle)) return;
        if (IsIconic(nativeHandle)) ShowWindowAsync(nativeHandle, SwRestore);
        // Foreground activation is subject to Windows' focus-stealing prevention rules.
        SetForegroundWindow(nativeHandle);
    }

    public static string GetDisplayId(long handle) => Screen.FromHandle(new nint(handle)).DeviceName;

    private static (long Started, bool IsShell) ReadProcessIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var shell = ShellProcesses.Contains(process.ProcessName);
            return (process.StartTime.ToUniversalTime().Ticks, shell);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return (0, false);
        }
    }

    private static string ReadTitle(nint handle)
    {
        var buffer = new StringBuilder(Math.Clamp(GetWindowTextLength(handle) + 1, 256, 4096));
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadClassName(nint handle)
    {
        var buffer = new StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static nint ReadRootOwner(nint handle)
    {
        // GA_ROOTOWNER follows GetParent semantics, which omit owners of ordinary
        // WS_OVERLAPPED windows (including WinForms owned forms). GW_OWNER works for
        // both overlapped windows and WS_POPUP dialogs. EnumWindows already limits
        // this traversal to top-level windows. Bound it in case ownership changes.
        var current = handle;
        var visited = new HashSet<nint> { handle };
        for (var depth = 0; depth < 64; depth++)
        {
            var owner = GetWindow(current, GwOwner);
            if (owner == nint.Zero || !visited.Add(owner)) return current;
            current = owner;
        }
        return current;
    }

    private static Rectangle ReadBounds(nint handle)
    {
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) < 0
            && !GetWindowRect(handle, out bounds)) return Rectangle.Empty;
        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static long GetWindowStyle(nint handle, int index) => nint.Size == 8
        ? GetWindowLongPtr(handle, index).ToInt64() : GetWindowLong(handle, index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManager { }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint window, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(nint window, [In] ref Guid desktopId);
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out NativeRect value, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint handle, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint handle, string name, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint handle, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint handle, string name);
}
