namespace IndependentVirtualDesktop.App.Native;

/// <summary>Deterministic shortcut checks. Never installs a hook or manipulates live windows.</summary>
public static class NativeSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        Check("all supported shortcuts", () =>
        {
            AssertCommand([0x5B], 0x09, DesktopCommand.ToggleOverview);
            AssertCommand([0xA2, 0x5B], 0x25, DesktopCommand.PreviousDesktop);
            AssertCommand([0xA3, 0x5C], 0x27, DesktopCommand.NextDesktop);
            AssertCommand([0xA2, 0x5B, 0xA0], 0x25, DesktopCommand.MoveWindowPrevious);
            AssertCommand([0xA3, 0x5C, 0xA1], 0x27, DesktopCommand.MoveWindowNext);
            AssertCommand([0xA2, 0x5B], 0x44, DesktopCommand.AddDesktop);
            AssertCommand([0xA2, 0x5B], 0x73, DesktopCommand.RemoveDesktop);
            AssertCommand([0xA2, 0x5B, 0xA0], 0x7B, DesktopCommand.RestoreAll);
        });
        Check("unrelated shortcuts pass through", () =>
        {
            AssertPassThrough([0x5B], 0x44);
            AssertPassThrough([0xA2], 0x25);
            AssertPassThrough([0x5B, 0xA0], 0x09);
            AssertPassThrough([0x5B, 0xA2], 0x09);
            AssertPassThrough([0x5B, 0xA2, 0xA4], 0x25);
            AssertPassThrough([0x5B, 0xA2, 0xA0], 0x44);
            AssertPassThrough([0x5B, 0xA2], 0x7B);
        });
        Check("consumed autorepeat cannot switch repeatedly", () =>
        {
            var state = Seed(0xA2, 0x5B);
            Require(state.Process(0x27, true).Command == DesktopCommand.NextDesktop, "First key down missing.");
            for (var count = 0; count < 8; count++)
            {
                var repeat = state.Process(0x27, true);
                Require(repeat.Suppress && repeat.Command is null, "A repeat leaked or triggered a command.");
            }
            Require(state.Process(0x27, false).Suppress, "Matching key up leaked.");
            Require(state.Process(0x27, true).Command == DesktopCommand.NextDesktop, "New press was not recognized.");
        });
        Check("release order cannot leak consumed keys or stick modifiers", () =>
        {
            var state = Seed(0xA2, 0x5B);
            state.Process(0x25, true);
            Require(!state.Process(0x5B, false).Suppress, "Windows release was consumed.");
            Require(!state.Process(0xA2, false).Suppress, "Control release was consumed.");
            Require(state.Process(0x25, false).Suppress, "Command key release leaked after modifier release.");
            Require(!state.Process(0x25, true).Suppress, "Released modifiers remained stuck.");
        });
        Check("held modifiers at hook startup", () =>
        {
            var state = new KeyboardShortcutState();
            state.SeedHeldKey(0xA3, true);
            state.SeedHeldKey(0x5C, true);
            Require(state.Process(0x25, true).Command == DesktopCommand.PreviousDesktop, "Startup modifiers were ignored.");
        });
        Check("both Windows keys track independently", () =>
        {
            var state = Seed(0x5B, 0x5C);
            state.Process(0x5B, false);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "One Windows release cleared both keys.");
        });
        Check("preexisting ordinary key repeat is not captured", () =>
        {
            var state = new KeyboardShortcutState();
            state.Process(0x25, true);
            state.Process(0xA2, true);
            state.Process(0x5B, true);
            Require(!state.Process(0x25, true).Suppress, "Ordinary held key became a shortcut through autorepeat.");
            Require(!state.Process(0x25, false).Suppress, "Uncaptured key up was swallowed.");
        });
        Check("Start menu masking applies only to bare Win+Tab", () =>
        {
            Require(Seed(0x5B).Process(0x09, true).MaskStartMenu, "Bare Win+Tab must mask the Windows tap.");
            Require(!Seed(0x5B, 0xA2).Process(0x25, true).MaskStartMenu, "Held Control must not be injected/released.");
            Require(!Seed(0x5B).Process(0x44, true).MaskStartMenu, "An unrelated shortcut was masked.");
        });
        Check("invalid virtual key input passes through", () =>
        {
            var state = new KeyboardShortcutState();
            Require(state.Process(-1, true) == default && state.Process(256, true) == default, "Invalid key was captured.");
        });
        return passed;

        void Check(string name, Action action)
        {
            try { action(); passed.Add($"Native: {name}"); }
            catch (Exception exception) { throw new InvalidOperationException($"Native shortcut test failed: {name}", exception); }
        }
    }

    private static KeyboardShortcutState Seed(params int[] keys)
    {
        var state = new KeyboardShortcutState();
        foreach (var key in keys)
            Require(!state.Process(key, true).Suppress, "A modifier was swallowed.");
        return state;
    }

    private static void AssertCommand(int[] modifiers, int key, DesktopCommand expected)
    {
        var state = Seed(modifiers);
        var decision = state.Process(key, true);
        Require(decision.Suppress && decision.Command == expected, $"Expected {expected}.");
        Require(state.Process(key, false).Suppress, "Matching key up was not consumed.");
    }

    private static void AssertPassThrough(int[] modifiers, int key)
    {
        var decision = Seed(modifiers).Process(key, true);
        Require(!decision.Suppress && decision.Command is null, "An unrelated key combination was consumed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
