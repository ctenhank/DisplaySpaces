using System.Collections.ObjectModel;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;

namespace BetterVirtualDesktop.App.Shortcuts;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Win = 1,
    Control = 2,
    Alt = 4,
    Shift = 8
}

public sealed record ShortcutBinding(ShortcutModifiers Modifiers, int VirtualKey);

public enum ShortcutValidationIssueKind { InvalidKey, NeedModifier, Reserved, Duplicate }

public sealed record ShortcutValidationIssue(ShortcutValidationIssueKind Kind, DesktopCommand Command,
    DesktopCommand? OtherCommand = null);

/// <summary>An immutable complete mapping. Null bindings deliberately disable individual commands.</summary>
public sealed class ShortcutProfile
{
    private const ShortcutModifiers AllModifiers = ShortcutModifiers.Win | ShortcutModifiers.Control |
        ShortcutModifiers.Alt | ShortcutModifiers.Shift;
    private static readonly DesktopCommand[] Commands = Enum.GetValues<DesktopCommand>();
    public static IReadOnlyList<int> AllowedKeys { get; } = Array.AsReadOnly(
        Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x70, 12))
            .Concat(new[] { 0x25, 0x26, 0x27, 0x28, 0x09, 0x20, 0x0D, 0x1B, 0x08, 0x24, 0x23, 0x21, 0x22, 0x2D, 0x2E })
            .ToArray());
    private static readonly HashSet<int> AllowedKeySet = AllowedKeys.ToHashSet();
    public static ShortcutProfile Defaults { get; } = CreateDefaults();
    public static ShortcutProfile WindowsStyle { get; } = Defaults.With(DesktopCommand.ToggleOverview,
        new ShortcutBinding(ShortcutModifiers.Win, 0x09));

    public IReadOnlyDictionary<DesktopCommand, ShortcutBinding?> Bindings { get; }
    public ShortcutBinding? this[DesktopCommand command] => Bindings[command];

    public ShortcutProfile(IReadOnlyDictionary<DesktopCommand, ShortcutBinding?> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count != Commands.Length || Commands.Any(command => !bindings.ContainsKey(command)))
            throw new ArgumentException("A shortcut profile must contain exactly every supported command.", nameof(bindings));
        Bindings = new ReadOnlyDictionary<DesktopCommand, ShortcutBinding?>(
            Commands.ToDictionary(command => command, command => bindings[command]));
    }

    public ShortcutProfile With(DesktopCommand command, ShortcutBinding? binding)
    {
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        var updated = Bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        updated[command] = binding;
        return new ShortcutProfile(updated);
    }

    /// <summary>
    /// Rejects duplicates and selected protected/system combinations. Other apps and
    /// Windows features may claim additional shortcuts; this is not exhaustive conflict detection.
    /// </summary>
    public ShortcutValidationIssue? Validate()
    {
        var assigned = new Dictionary<ShortcutBinding, DesktopCommand>();
        foreach (var command in Commands)
        {
            var binding = Bindings[command];
            if (binding is null) continue;
            var modifiers = binding.Modifiers;
            if (!AllowedKeySet.Contains(binding.VirtualKey) || (modifiers & ~AllModifiers) != 0)
                return new(ShortcutValidationIssueKind.InvalidKey, command);
            if ((modifiers & (ShortcutModifiers.Win | ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0)
                return new(ShortcutValidationIssueKind.NeedModifier, command);
            if (IsReserved(binding)) return new(ShortcutValidationIssueKind.Reserved, command);
            if (assigned.TryGetValue(binding, out var previous))
                return new(ShortcutValidationIssueKind.Duplicate, command, previous);
            assigned.Add(binding, command);
        }
        return null;
    }

    private static bool IsReserved(ShortcutBinding binding)
    {
        bool win = binding.Modifiers.HasFlag(ShortcutModifiers.Win);
        bool control = binding.Modifiers.HasFlag(ShortcutModifiers.Control);
        bool alt = binding.Modifiers.HasFlag(ShortcutModifiers.Alt);
        bool shift = binding.Modifiers.HasFlag(ShortcutModifiers.Shift);
        return (win && binding.VirtualKey == 0x4C) // Win+L locks Windows regardless of extra modifiers.
            || (control && alt && binding.VirtualKey == 0x2E) // Secure attention sequence.
            || (!win && alt && binding.VirtualKey is 0x09 or 0x1B or 0x73)
            || (!win && control && binding.VirtualKey == 0x1B)
            || (win && control && shift && binding.VirtualKey == 0x42); // Graphics driver recovery.
    }

    private static ShortcutProfile CreateDefaults()
    {
        const ShortcutModifiers desktop = ShortcutModifiers.Win | ShortcutModifiers.Control;
        const ShortcutModifiers move = desktop | ShortcutModifiers.Shift;
        return new ShortcutProfile(new Dictionary<DesktopCommand, ShortcutBinding?>
        {
            [DesktopCommand.ToggleOverview] = new(ShortcutModifiers.Win | ShortcutModifiers.Alt, 0x09),
            [DesktopCommand.PreviousDesktop] = new(desktop, 0x25),
            [DesktopCommand.NextDesktop] = new(desktop, 0x27),
            [DesktopCommand.MoveWindowPrevious] = new(move, 0x25),
            [DesktopCommand.MoveWindowNext] = new(move, 0x27),
            [DesktopCommand.AddDesktop] = new(desktop, 0x44),
            [DesktopCommand.RemoveDesktop] = new(desktop, 0x73),
            [DesktopCommand.RestoreAll] = new(move, 0x7B)
        });
    }
}

public static class ShortcutText
{
    public static string Format(ShortcutBinding? binding)
    {
        if (binding is null) return UiText.Get("Shortcuts.Disabled");
        var parts = new List<string>(5);
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(binding.VirtualKey));
        return string.Join('+', parts);
    }

    public static string KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x7B => $"F{virtualKey - 0x70 + 1}",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0x09 => "Tab", 0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x08 => "Backspace",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown", 0x2D => "Insert", 0x2E => "Delete",
        _ => $"0x{virtualKey:X2}"
    };

    public static string CommandName(DesktopCommand command) => UiText.Get("Shortcuts.Command." + command);

    public static string ErrorText(ShortcutValidationIssue issue) => UiText.Get("Shortcuts.Validation." + issue.Kind,
        CommandName(issue.Command), issue.OtherCommand is DesktopCommand other ? CommandName(other) : "");
}
