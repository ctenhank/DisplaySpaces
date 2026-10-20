using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App.Native;

public enum DesktopCommand
{
    ToggleOverview,
    PreviousDesktop,
    NextDesktop,
    MoveWindowPrevious,
    MoveWindowNext,
    AddDesktop,
    RemoveDesktop,
    RestoreAll
}

/// <summary>
/// Runs the low-level hook on its own message-loop thread. Command events are serialized
/// on a worker thread; consumers must marshal to their UI thread before touching controls.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private readonly HookProcedure callback;
    private readonly Thread thread;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<(DesktopCommand Command, long Generation)> commands = new();
    private readonly KeyboardShortcutState state;
    private readonly object stateSync = new();
    private nint hook;
    private uint threadId;
    private int disposed;
    private int dispatching;

    public event Action<DesktopCommand, long>? Command;

    /// <summary>Lets UI consumers reject commands posted before a settings or suspension change.</summary>
    public long Generation
    {
        get { lock (stateSync) return state.Generation; }
    }

    public KeyboardHook(ShortcutProfile? profile = null)
    {
        state = new KeyboardShortcutState(profile);
        callback = HandleKeyboard;
        thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "DisplaySpaces keyboard hook" };
        thread.Start();
        try
        {
            ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Changes mappings without resetting keys that are already held or consumed.</summary>
    public void ApplyProfile(ShortcutProfile profile)
    {
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            state.ApplyProfile(profile);
        }
    }

    /// <summary>Suspends new matches while continuing to drain previously consumed key releases.</summary>
    public void SetSuspended(bool suspended)
    {
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            state.SetSuspended(suspended);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var installedHook = Interlocked.Exchange(ref hook, nint.Zero);
        if (installedHook != nint.Zero) UnhookWindowsHookEx(installedHook);
        if (threadId != 0) PostThreadMessage(threadId, WmQuit, nuint.Zero, nint.Zero);
        if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
        while (commands.TryDequeue(out _)) { }
    }

    private void RunMessageLoop()
    {
        try
        {
            threadId = GetCurrentThreadId();
            // Ensure the thread owns a message queue before Dispose can post WM_QUIT.
            PeekMessage(out _, nint.Zero, 0, 0, 0);
            lock (stateSync)
            {
                // Seed ordinary keys too: a key held before installation must not turn
                // its first observed autorepeat into a new command. Generic modifier
                // aliases are not seeded because the hook releases their left/right keys.
                for (int key = 0; key < 256; key++)
                    if (key is not (0x10 or 0x11 or 0x12))
                        state.SeedHeldKey(key, (GetAsyncKeyState(key) & 0x8000) != 0);
            }
            if (Volatile.Read(ref disposed) != 0) return;
            hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
            if (hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
            ready.TrySetResult(true);
            while (Volatile.Read(ref disposed) == 0)
            {
                var result = GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            Debug.WriteLine($"Keyboard hook stopped: {exception}");
        }
        finally
        {
            var installedHook = Interlocked.Exchange(ref hook, nint.Zero);
            if (installedHook != nint.Zero) UnhookWindowsHookEx(installedHook);
            ready.TrySetCanceled();
        }
    }

    private nint HandleKeyboard(int code, nuint message, nint data)
    {
        if (code < 0 || Volatile.Read(ref disposed) != 0) return CallNextHookEx(nint.Zero, code, message, data);
        var eventType = (uint)message;
        if (eventType is not (WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp))
            return CallNextHookEx(nint.Zero, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardEvent>(data);
        // Ignore injected keystrokes, including our own Start/Alt-menu mask. Modifier state
        // is tracked from physical transitions because async key state is stale in this hook.
        if ((key.Flags & LlkhfInjected) != 0) return CallNextHookEx(nint.Zero, code, message, data);
        ShortcutDecision decision;
        long generation;
        lock (stateSync)
        {
            decision = state.Process((int)key.VirtualKey, eventType is WmKeyDown or WmSysKeyDown);
            generation = state.Generation;
        }
        if (decision.MaskStartMenu) MaskStartMenu();
        if (decision.Command is { } command)
        {
            commands.Enqueue((command, generation));
            if (Interlocked.CompareExchange(ref dispatching, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => DrainCommands());
        }
        return decision.Suppress ? new nint(1) : CallNextHookEx(nint.Zero, code, message, data);
    }

    private void DrainCommands()
    {
        do
        {
            while (commands.TryDequeue(out var pending))
            {
                if (Volatile.Read(ref disposed) != 0 || pending.Generation != Generation) continue;
                // Consumers recheck this generation after marshaling to the UI thread:
                // a settings change can occur after this check or after event publication.
                try { Command?.Invoke(pending.Command, pending.Generation); }
                catch (Exception exception) { Debug.WriteLine($"Desktop command callback failed: {exception}"); }
            }
            Interlocked.Exchange(ref dispatching, 0);
        }
        while (!commands.IsEmpty && Interlocked.CompareExchange(ref dispatching, 1, 0) == 0);
    }

    private static void MaskStartMenu()
    {
        // Win/Alt down have already reached Windows. Consuming their command key
        // must not leave either looking like a bare tap when released. A Control
        // press/release masks Start and Alt-menu activation, including Alt-only
        // bindings. Never inject/release Control while it is physically held.
        var input = new[]
        {
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0xA2 } } },
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0xA2, Flags = 2 } } }
        };
        SendInput((uint)input.Length, input, Marshal.SizeOf<NativeInput>());
    }

    private delegate nint HookProcedure(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public NativePoint Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}

/// <summary>Pure key-transition matcher, independently testable without a native hook.</summary>
public sealed class KeyboardShortcutState
{
    private readonly bool[] held = new bool[256];
    private readonly bool[] consumed = new bool[256];
    private ShortcutProfile profile;
    private bool suspended;
    public long Generation { get; private set; }
    public static IReadOnlyList<int> ModifierKeys { get; } = Array.AsReadOnly(new[] { 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 });

    public KeyboardShortcutState(ShortcutProfile? profile = null)
    {
        this.profile = ShortcutProfile.Defaults;
        ApplyProfile(profile ?? ShortcutProfile.Defaults);
    }

    public void ApplyProfile(ShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Validate() is { } issue)
            throw new ArgumentException($"Invalid shortcut profile: {issue.Kind} ({issue.Command}).", nameof(profile));
        // Preserve physical transitions and consumed keys. The new mapping becomes
        // eligible only on a fresh command-key press, never on an existing repeat.
        this.profile = profile;
        Generation = unchecked(Generation + 1);
    }

    public void SetSuspended(bool value)
    {
        if (suspended == value) return;
        suspended = value;
        Generation = unchecked(Generation + 1);
    }

    public void SeedHeldKey(int virtualKey, bool isHeld)
    {
        if ((uint)virtualKey < held.Length) held[virtualKey] = isHeld;
    }

    public ShortcutDecision Process(int virtualKey, bool isDown)
    {
        if ((uint)virtualKey >= held.Length) return default;
        var wasHeld = held[virtualKey];
        held[virtualKey] = isDown;
        if (!isDown)
        {
            var suppressRelease = consumed[virtualKey];
            consumed[virtualKey] = false;
            return new ShortcutDecision(suppressRelease, null, false);
        }
        if (consumed[virtualKey]) return new ShortcutDecision(true, null, false);
        if (wasHeld || suspended) return default;

        var win = held[0x5B] || held[0x5C];
        var control = held[0x11] || held[0xA2] || held[0xA3];
        var shift = held[0x10] || held[0xA0] || held[0xA1];
        var alt = held[0x12] || held[0xA4] || held[0xA5];
        var modifiers = (win ? ShortcutModifiers.Win : ShortcutModifiers.None)
            | (control ? ShortcutModifiers.Control : ShortcutModifiers.None)
            | (alt ? ShortcutModifiers.Alt : ShortcutModifiers.None)
            | (shift ? ShortcutModifiers.Shift : ShortcutModifiers.None);
        foreach (var (command, binding) in profile.Bindings)
        {
            if (binding is null || binding.VirtualKey != virtualKey || binding.Modifiers != modifiers) continue;
            consumed[virtualKey] = true;
            return new ShortcutDecision(true, command, (win || alt) && !control);
        }
        return default;
    }
}

public readonly record struct ShortcutDecision(bool Suppress, DesktopCommand? Command, bool MaskStartMenu);
