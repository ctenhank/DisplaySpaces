using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App.Native;

/// <summary>Deterministic shortcut and geometry checks. Never installs a hook or manipulates live windows.</summary>
public static class NativeSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        Check("display move preserves owner offsets and handles negative monitor origins", () =>
        {
            Rectangle[] windows = [new(120, 160, 600, 400), new(220, 210, 250, 140)];
            var result = WindowMoveGeometry.MapGroup(windows, new(0, 40, 1920, 1040), new(-1920, -200, 1920, 1040));
            Require(result[0].Size == windows[0].Size && result[1].Size == windows[1].Size, "A fitting window was resized.");
            Require(result[1].Left - result[0].Left == 100 && result[1].Top - result[0].Top == 50, "Owner/dialog offsets changed.");
            Require(result[0] == new Rectangle(-1800, -80, 600, 400), "Monitor-relative placement changed.");
        });
        Check("oversized groups fit smaller monitors while retaining relative layout", () =>
        {
            var area = new Rectangle(2500, 100, 800, 600);
            var result = WindowMoveGeometry.MapGroup([new(0, 0, 1600, 1000), new(400, 200, 600, 400)],
                new(0, 0, 1920, 1080), area);
            Require(result.All(area.Contains), "A mapped window escaped the target work area.");
            Require(result[1].Left - result[0].Left == 200 && result[1].Top - result[0].Top == 100,
                "Group scaling changed relative layout.");
        });
        Check("window placement conversion respects top and left taskbars and tool windows", () =>
        {
            var monitor = new Rectangle(-1920, -100, 1920, 1080);
            var work = new Rectangle(-1880, -60, 1880, 1040);
            var rect = new Rectangle(-1700, 40, 800, 600);
            var placement = WindowMoveGeometry.ScreenToPlacement(rect, monitor, work, false);
            Require(placement.Location == new Point(-1740, 0), "Taskbar offset was not subtracted.");
            Require(WindowMoveGeometry.PlacementToScreen(placement, monitor, work, false) == rect, "Placement did not roundtrip.");
            Require(WindowMoveGeometry.ScreenToPlacement(rect, monitor, work, true) == rect, "Tool window must use screen coordinates.");
        });
        Check("all supported shortcuts", () =>
        {
            AssertCommand([0x5B, 0xA4], 0x09, DesktopCommand.ToggleOverview);
            AssertCommand([0xA5, 0x5C], 0x09, DesktopCommand.ToggleOverview);
            AssertCommand([0x12, 0x5B], 0x09, DesktopCommand.ToggleOverview);
            AssertCommand([0xA2, 0x5B], 0x25, DesktopCommand.PreviousDesktop);
            AssertCommand([0xA3, 0x5C], 0x27, DesktopCommand.NextDesktop);
            AssertCommand([0xA2, 0x5B, 0xA0], 0x25, DesktopCommand.MoveWindowPrevious);
            AssertCommand([0xA3, 0x5C, 0xA1], 0x27, DesktopCommand.MoveWindowNext);
            AssertCommand([0xA2, 0x5B], 0x44, DesktopCommand.AddDesktop);
            AssertCommand([0xA2, 0x5B], 0x73, DesktopCommand.RemoveDesktop);
            AssertCommand([0xA2, 0x5B, 0xA0], 0x7B, DesktopCommand.RestoreAll);
        });
        Check("Windows task view and app switching remain native", () =>
        {
            AssertPassThrough([0x5B], 0x09);
            AssertPassThrough([0x5C], 0x09);
            AssertPassThrough([0xA4], 0x09);
            AssertPassThrough([0xA5], 0x09);
            AssertPassThrough([0xA2, 0xA4], 0x09);
            AssertPassThrough([0xA3, 0xA5], 0x09);
            AssertPassThrough([0xA4, 0xA0], 0x09);
        });
        Check("unrelated shortcuts pass through", () =>
        {
            AssertPassThrough([0x5B], 0x44);
            AssertPassThrough([0xA2], 0x25);
            AssertPassThrough([0x5B, 0xA0], 0x09);
            AssertPassThrough([0x5B, 0xA2], 0x09);
            AssertPassThrough([0x5B, 0xA4, 0xA0], 0x09);
            AssertPassThrough([0x5C, 0xA5, 0xA3], 0x09);
            AssertPassThrough([0x5B, 0xA2, 0xA0], 0x44);
            AssertPassThrough([0x5B, 0xA2], 0x7B);
        });
        Check("Alt excludes every desktop management shortcut", () =>
        {
            foreach (var alt in new[] { 0x12, 0xA4, 0xA5 })
            {
                foreach (var key in new[] { 0x25, 0x27, 0x44, 0x73 })
                    AssertPassThrough([0x5B, 0xA2, alt], key);
                foreach (var key in new[] { 0x25, 0x27, 0x7B })
                    AssertPassThrough([0x5C, 0xA3, 0xA1, alt], key);
            }
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
        Check("overview autorepeat stays consumed after modifiers release", () =>
        {
            var state = Seed(0x5B, 0xA4);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Overview press missing.");
            state.Process(0xA4, false);
            state.Process(0x5B, false);
            for (var count = 0; count < 8; count++)
            {
                var repeat = state.Process(0x09, true);
                Require(repeat.Suppress && repeat.Command is null && !repeat.MaskStartMenu, "Consumed Tab repeated or leaked.");
            }
            Require(state.Process(0x09, false).Suppress, "Consumed Tab release leaked.");
            Require(state.Process(0x09, true) == default, "Released modifiers remained stuck.");
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
        Check("overview allows every modifier and Tab release order", () =>
        {
            int[][] releaseOrders =
            [
                [0x09, 0x5B, 0xA4], [0x09, 0xA4, 0x5B],
                [0x5B, 0x09, 0xA4], [0x5B, 0xA4, 0x09],
                [0xA4, 0x09, 0x5B], [0xA4, 0x5B, 0x09]
            ];
            foreach (var releaseOrder in releaseOrders)
            {
                var state = Seed(0x5B, 0xA4);
                var press = state.Process(0x09, true);
                Require(press.Suppress && press.Command == DesktopCommand.ToggleOverview && press.MaskStartMenu,
                    "Overview must consume Tab and mask menu activation before modifiers release.");
                foreach (var key in releaseOrder)
                {
                    var release = state.Process(key, false);
                    Require(release.Suppress == (key == 0x09) && release.Command is null && !release.MaskStartMenu,
                        "A modifier release was consumed or a Tab release leaked.");
                }
                Require(state.Process(0x09, true) == default, "A released modifier remained stuck.");
                state.Process(0x09, false);
                state.Process(0x5B, true);
                Require(state.Process(0x09, true) == default, "Native Win+Tab was captured after overview dismissal.");
                state.Process(0x09, false);
                state.Process(0xA4, true);
                Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview,
                    "Overview could not be opened again after releases.");
            }
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
            var state = Seed(0x5B, 0x5C, 0xA4);
            state.Process(0x5B, false);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "One Windows release cleared both keys.");
        });
        Check("both Alt keys track independently", () =>
        {
            var state = Seed(0x5B, 0xA4, 0xA5);
            Require(!state.Process(0xA4, false).Suppress, "Left Alt release was swallowed.");
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "One Alt release cleared both keys.");
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
        Check("native Win+Tab repeat does not become overview when Alt is pressed", () =>
        {
            var state = Seed(0x5B);
            Require(state.Process(0x09, true) == default, "Native Win+Tab was captured.");
            state.Process(0xA4, true);
            Require(state.Process(0x09, true) == default, "Native held Tab was captured through autorepeat.");
            Require(state.Process(0x09, false) == default, "Native Tab release was swallowed.");
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Fresh overview press was ignored.");
        });
        Check("default Start and Alt menu masking applies only to Win+Alt+Tab", () =>
        {
            Require(Seed(0x5B, 0xA4).Process(0x09, true).MaskStartMenu, "Overview must mask Windows and Alt taps.");
            Require(!Seed(0x5B).Process(0x09, true).MaskStartMenu, "Native Win+Tab must not be masked.");
            Require(!Seed(0x5B, 0xA2).Process(0x25, true).MaskStartMenu, "Held Control must not be injected/released.");
            Require(!Seed(0x5B).Process(0x44, true).MaskStartMenu, "An unrelated shortcut was masked.");
        });
        Check("invalid virtual key input passes through", () =>
        {
            var state = new KeyboardShortcutState();
            Require(state.Process(-1, true) == default && state.Process(256, true) == default, "Invalid key was captured.");
        });
        Check("Windows-style Win+Tab consumes exactly one command in either release order", () =>
        {
            foreach (int win in new[] { 0x5B, 0x5C })
            foreach (bool releaseModifierFirst in new[] { false, true })
            {
                var state = SeedProfile(ShortcutProfile.WindowsStyle, win);
                var first = state.Process(0x09, true);
                Require(first.Suppress && first.Command == DesktopCommand.ToggleOverview && first.MaskStartMenu,
                    "Windows-style Win+Tab did not consume the command and mask Start.");
                Require(state.Process(0x09, true) is { Suppress: true, Command: null, MaskStartMenu: false },
                    "A Windows-style repeat invoked another command.");
                if (releaseModifierFirst) Require(state.Process(win, false) == default, "Win release was swallowed.");
                Require(state.Process(0x09, false) is { Suppress: true, Command: null }, "Tab release leaked.");
                if (!releaseModifierFirst) Require(state.Process(win, false) == default, "Win release was swallowed.");
                Require(state.Process(0x09, true) == default, "Win stayed held after release.");
                AssertPassThrough(ShortcutProfile.WindowsStyle, [win, 0xA4], 0x09);
            }
        });
        Check("disabled overview bindings leave both native overview gestures untouched", () =>
        {
            var disabled = ShortcutProfile.WindowsStyle.With(DesktopCommand.ToggleOverview, null);
            AssertPassThrough(disabled, [0x5B], 0x09);
            AssertPassThrough(disabled, [0x5C, 0xA4], 0x09);
            Require(SeedProfile(disabled, 0x5B, 0xA2).Process(0x27, true).Command == DesktopCommand.NextDesktop,
                "Disabling overview changed another binding.");
        });
        Check("custom shortcuts without Win require the exact modifier set", () =>
        {
            var custom = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview,
                new(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x77));
            Require(SeedProfile(custom, 0xA2, 0xA0).Process(0x77, true)
                    is { Suppress: true, Command: DesktopCommand.ToggleOverview, MaskStartMenu: false },
                "Custom Ctrl+Shift+F8 did not match without Win.");
            foreach (int[] keys in new int[][] { [], [0xA2], [0xA0], [0xA2, 0xA0, 0xA4], [0xA2, 0xA0, 0x5B] })
                AssertPassThrough(custom, keys, 0x77);
            AssertPassThrough(custom, [0x5B, 0xA4], 0x09);
        });
        Check("generic and sided modifier keys match custom bindings", () =>
        {
            var custom = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview,
                new(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x77));
            foreach (int control in new[] { 0x11, 0xA2, 0xA3 })
            foreach (int shift in new[] { 0x10, 0xA0, 0xA1 })
            {
                var state = SeedProfile(custom, control, shift);
                Require(state.Process(0x77, true).Command == DesktopCommand.ToggleOverview, "Modifier aliases did not match.");
                Require(state.Process(control, false) == default && state.Process(shift, false) == default,
                    "A modifier release was swallowed.");
                Require(state.Process(0x77, false).Suppress && state.Process(0x77, true) == default,
                    "Alias release left a modifier stuck.");
            }
            var altOnly = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview, new(ShortcutModifiers.Alt, 0x77));
            foreach (int alt in new[] { 0x12, 0xA4, 0xA5 })
                Require(SeedProfile(altOnly, alt).Process(0x77, true)
                        is { Suppress: true, Command: DesktopCommand.ToggleOverview, MaskStartMenu: true },
                    "Alt-only shortcuts must mask native menu activation.");
        });
        Check("two physical Control and Shift keys retain their other held side", () =>
        {
            var custom = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview,
                new(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x77));
            var state = SeedProfile(custom, 0xA2, 0xA3, 0xA0, 0xA1);
            Require(state.Process(0xA2, false) == default && state.Process(0xA0, false) == default,
                "Left modifier releases were swallowed.");
            Require(state.Process(0x77, true).Command == DesktopCommand.ToggleOverview, "One side cleared its other held modifier.");
            state.Process(0x77, false);
            state.Process(0xA3, false);
            Require(state.Process(0x77, true) == default, "Control remained held after both sides were released.");
        });
        Check("profile changes retain consumed keys through all old modifier releases", () =>
        {
            int[][] releases = [[0x09, 0x5B, 0xA4], [0x5B, 0xA4, 0x09], [0xA4, 0x09, 0x5B]];
            foreach (var order in releases)
            {
                var state = Seed(0x5B, 0xA4);
                Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Original binding failed.");
                state.ApplyProfile(ShortcutProfile.WindowsStyle.With(DesktopCommand.ToggleOverview, null));
                Require(state.Process(0x09, true) is { Suppress: true, Command: null, MaskStartMenu: false },
                    "Disabling a binding leaked its existing repeat.");
                foreach (int key in order)
                    Require(state.Process(key, false).Suppress == (key == 0x09), "Profile change corrupted release ownership.");
                state.Process(0x5B, true);
                Require(state.Process(0x09, true) == default, "Disabled overview intercepted a fresh native chord.");
            }
        });
        Check("an already native key stays native when a profile enables its chord", () =>
        {
            var state = Seed(0x5B);
            Require(state.Process(0x09, true) == default, "Default Win+Tab was not native.");
            state.ApplyProfile(ShortcutProfile.WindowsStyle);
            Require(state.Process(0x09, true) == default && state.Process(0x09, false) == default,
                "Updating a profile captured an already-held native Tab.");
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "A fresh Tab did not use the new profile.");
        });
        Check("rebinding a consumed chord cannot dispatch its new action until a fresh press", () =>
        {
            var binding = new ShortcutBinding(ShortcutModifiers.Control, 0x77);
            var original = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview, binding);
            var state = SeedProfile(original, 0xA2);
            Require(state.Process(0x77, true).Command == DesktopCommand.ToggleOverview, "Original custom action failed.");
            state.ApplyProfile(original.With(DesktopCommand.ToggleOverview, null).With(DesktopCommand.AddDesktop, binding));
            Require(state.Process(0x77, true) is { Suppress: true, Command: null }, "Repeat invoked the newly mapped action.");
            Require(state.Process(0x77, false).Suppress, "Old action lost ownership of its release.");
            Require(state.Process(0x77, true).Command == DesktopCommand.AddDesktop, "Fresh press did not dispatch the new action.");
        });
        Check("suspension passes new keys and resume waits for a fresh command-key press", () =>
        {
            var state = SeedProfile(ShortcutProfile.WindowsStyle, 0x5B);
            state.SetSuspended(true);
            Require(state.Process(0x09, true) == default, "A new suspended key was captured.");
            state.SetSuspended(false);
            Require(state.Process(0x09, true) == default && state.Process(0x09, false) == default,
                "Resume captured a key first pressed while suspended.");
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Fresh resumed key failed.");
        });
        Check("suspension and profile changes still drain old consumed repeats and releases", () =>
        {
            var state = Seed(0x5B, 0xA4);
            state.Process(0x09, true);
            state.SetSuspended(true);
            state.ApplyProfile(ShortcutProfile.WindowsStyle);
            Require(state.Process(0x5B, false) == default && state.Process(0xA4, false) == default,
                "Suspension swallowed modifier releases.");
            Require(state.Process(0x09, true) is { Suppress: true, Command: null, MaskStartMenu: false }
                && state.Process(0x09, false).Suppress && state.Process(0x09, false) == default,
                "Suspension did not drain the old consumed key exactly once.");
            state.Process(0x5B, true);
            state.SetSuspended(false);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Resume did not use the changed profile.");
        });
        Check("invalid profile updates preserve the previous mapping and held-key ownership", () =>
        {
            var state = Seed(0x5B, 0xA2);
            Require(state.Process(0x27, true).Command == DesktopCommand.NextDesktop, "Original shortcut failed.");
            var duplicate = ShortcutProfile.Defaults.With(DesktopCommand.AddDesktop,
                ShortcutProfile.Defaults.Bindings[DesktopCommand.NextDesktop]);
            bool rejected = false;
            try { state.ApplyProfile(duplicate); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "An ambiguous duplicate binding was accepted.");
            Require(state.Process(0x27, true) is { Suppress: true, Command: null } && state.Process(0x27, false).Suppress,
                "Rejected profile update altered a consumed key.");
            Require(state.Process(0x27, true).Command == DesktopCommand.NextDesktop, "Rejected profile replaced the valid mapping.");
        });
        Check("ordinary keys seeded at startup are not treated as fresh shortcut presses", () =>
        {
            var state = new KeyboardShortcutState(ShortcutProfile.WindowsStyle);
            state.SeedHeldKey(0x5B, true);
            state.SeedHeldKey(0x09, true);
            Require(state.Process(0x09, true) == default && state.Process(0x09, false) == default,
                "A startup-held Tab was captured before its first release.");
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Fresh startup Tab was ignored.");
        });
        Check("custom modifier release orders mask only Win or Alt gestures without held Control", () =>
        {
            (ShortcutModifiers Modifiers, int[] Keys, bool Mask)[] gestures =
            [
                (ShortcutModifiers.Control, [0xA2], false),
                (ShortcutModifiers.Alt, [0xA5], true),
                (ShortcutModifiers.Win, [0x5C], true),
                (ShortcutModifiers.Win | ShortcutModifiers.Alt, [0x5B, 0xA4], true),
                (ShortcutModifiers.Control | ShortcutModifiers.Alt, [0xA3, 0xA5], false),
                (ShortcutModifiers.Win | ShortcutModifiers.Control | ShortcutModifiers.Alt, [0x5C, 0xA2, 0xA4], false)
            ];
            foreach (var (modifiers, keys, mask) in gestures)
            {
                var profile = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview, new(modifiers, 0x77));
                foreach (var releaseOrder in Permutations(keys.Append(0x77).ToArray()))
                {
                    var state = SeedProfile(profile, keys);
                    Require(state.Process(0x77, true) == new ShortcutDecision(true, DesktopCommand.ToggleOverview, mask),
                        "A custom chord injected a Control mask while Control was held or omitted a necessary menu mask.");
                    bool commandReleased = false;
                    foreach (int key in releaseOrder)
                    {
                        if (!commandReleased)
                            Require(state.Process(0x77, true) is { Suppress: true, Command: null, MaskStartMenu: false },
                                "Release ordering repeated a command or a menu mask.");
                        Require(state.Process(key, false) == new ShortcutDecision(key == 0x77, null, false),
                            "A custom modifier release was swallowed or a consumed key release leaked.");
                        commandReleased |= key == 0x77;
                    }
                    Require(state.Process(0x77, true) == default, "A custom chord left a modifier held after all releases.");
                }
            }
        });
        Check("settings and suspension generations invalidate already queued commands", () =>
        {
            var state = Seed(0x5B, 0xA4);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview, "Original command missing.");
            long queuedGeneration = state.Generation;
            state.SetSuspended(true);
            long suspendedGeneration = state.Generation;
            Require(suspendedGeneration != queuedGeneration, "Opening settings retained a queued command's generation.");
            state.SetSuspended(true);
            Require(state.Generation == suspendedGeneration, "An unchanged suspension state invalidated current callbacks.");
            state.ApplyProfile(ShortcutProfile.WindowsStyle);
            Require(state.Generation != suspendedGeneration && state.Generation != queuedGeneration,
                "Applying a profile reused a generation held by an old callback.");
            long appliedGeneration = state.Generation;
            state.SetSuspended(false);
            Require(state.Generation != appliedGeneration && state.Generation != queuedGeneration,
                "Closing settings revived an old queued command.");
            long resumedGeneration = state.Generation;
            Require(state.Process(0x09, true) is { Suppress: true, Command: null } && state.Process(0x09, false).Suppress,
                "Generation changes discarded ownership of the consumed Tab.");
            state.Process(0xA4, false);
            Require(state.Process(0x09, true).Command == DesktopCommand.ToggleOverview && state.Generation == resumedGeneration,
                "A fresh command did not carry the current generation.");
        });
        Check("failed profile changes and ordinary key transitions preserve command generation", () =>
        {
            var state = Seed(0x5B, 0xA2);
            long original = state.Generation;
            state.Process(0x27, true);
            state.Process(0x27, false);
            state.SetSuspended(false);
            Require(state.Generation == original, "Ordinary keys or unchanged suspension invalidated queued commands.");
            var duplicate = ShortcutProfile.Defaults.With(DesktopCommand.AddDesktop,
                ShortcutProfile.Defaults.Bindings[DesktopCommand.NextDesktop]);
            try { state.ApplyProfile(duplicate); }
            catch (ArgumentException) { }
            Require(state.Generation == original, "A rejected profile invalidated commands from the still-active settings.");
            state.ApplyProfile(ShortcutProfile.Defaults);
            Require(state.Generation != original, "Successful reapplication failed to invalidate old queued commands.");
        });
        return passed;

        void Check(string name, Action action)
        {
            try { action(); passed.Add($"Native: {name}"); }
            catch (Exception exception) { throw new InvalidOperationException($"Native shortcut test failed: {name}", exception); }
        }
    }

    private static KeyboardShortcutState Seed(params int[] keys)
        => SeedProfile(ShortcutProfile.Defaults, keys);

    private static KeyboardShortcutState SeedProfile(ShortcutProfile profile, params int[] keys)
    {
        var state = new KeyboardShortcutState(profile);
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
        => AssertPassThrough(ShortcutProfile.Defaults, modifiers, key);

    private static void AssertPassThrough(ShortcutProfile profile, int[] modifiers, int key)
    {
        var state = SeedProfile(profile, modifiers);
        Require(state.Process(key, true) == default, "An unrelated key combination was consumed or masked.");
        Require(state.Process(key, true) == default, "An unrelated repeat was consumed or masked.");
        Require(state.Process(key, false) == default, "An unrelated key release was consumed or masked.");
        foreach (var modifier in modifiers.Reverse())
            Require(state.Process(modifier, false) == default, "An unrelated modifier release was swallowed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 0) { yield return []; yield break; }
        for (int index = 0; index < values.Length; index++)
            foreach (var remainder in Permutations(values.Where((_, position) => position != index).ToArray()))
                yield return [values[index], .. remainder];
    }
}
