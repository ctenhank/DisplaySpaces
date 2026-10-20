using System.Text.Json.Nodes;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App;

/// <summary>Profile and temporary-file checks only; never installs a hook or accesses real user settings.</summary>
internal static class ShortcutPreferenceSelfTests
{
    public static void Run(string output, List<string> results)
    {
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            results.Add("PASS " + description);
        }
        static bool Equivalent(ShortcutProfile first, ShortcutProfile second) =>
            Enum.GetValues<DesktopCommand>().All(command => first[command] == second[command]);
        static ShortcutProfile Disabled() => new(Enum.GetValues<DesktopCommand>()
            .ToDictionary(command => command, _ => (ShortcutBinding?)null));
        static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }

        const ShortcutModifiers desktop = ShortcutModifiers.Win | ShortcutModifiers.Control;
        const ShortcutModifiers move = desktop | ShortcutModifiers.Shift;
        var expected = new Dictionary<DesktopCommand, ShortcutBinding?>
        {
            [DesktopCommand.ToggleOverview] = new(ShortcutModifiers.Win | ShortcutModifiers.Alt, 0x09),
            [DesktopCommand.PreviousDesktop] = new(desktop, 0x25),
            [DesktopCommand.NextDesktop] = new(desktop, 0x27),
            [DesktopCommand.MoveWindowPrevious] = new(move, 0x25),
            [DesktopCommand.MoveWindowNext] = new(move, 0x27),
            [DesktopCommand.AddDesktop] = new(desktop, 0x44),
            [DesktopCommand.RemoveDesktop] = new(desktop, 0x73),
            [DesktopCommand.RestoreAll] = new(move, 0x7B)
        };
        Check(ShortcutProfile.Defaults.Validate() is null && Equivalent(ShortcutProfile.Defaults, new ShortcutProfile(expected)),
            "default shortcut profile retains all eight existing shortcuts");
        Check(ShortcutProfile.WindowsStyle.Validate() is null
            && ShortcutProfile.WindowsStyle[DesktopCommand.ToggleOverview] == new ShortcutBinding(ShortcutModifiers.Win, 0x09)
            && Enum.GetValues<DesktopCommand>().Where(command => command != DesktopCommand.ToggleOverview)
                .All(command => ShortcutProfile.WindowsStyle[command] == ShortcutProfile.Defaults[command]),
            "Windows-style profile changes only overview to Win+Tab");

        var copied = new ShortcutProfile(expected);
        expected[DesktopCommand.ToggleOverview] = null;
        Check(copied[DesktopCommand.ToggleOverview] == ShortcutProfile.Defaults[DesktopCommand.ToggleOverview]
            && Throws<NotSupportedException>(() => ((IDictionary<DesktopCommand, ShortcutBinding?>)copied.Bindings)
                [DesktopCommand.ToggleOverview] = null), "shortcut profiles defensively copy inputs and expose read-only bindings");
        var missing = ShortcutProfile.Defaults.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        missing.Remove(DesktopCommand.RestoreAll);
        Check(Throws<ArgumentException>(() => new ShortcutProfile(missing)), "incomplete shortcut profiles are rejected");
        missing[(DesktopCommand)999] = null;
        Check(Throws<ArgumentException>(() => new ShortcutProfile(missing)), "unknown commands cannot replace required shortcut entries");

        var custom = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview,
            new ShortcutBinding(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x79));
        Check(custom.Validate() is null && ShortcutProfile.Defaults[DesktopCommand.ToggleOverview] == copied[DesktopCommand.ToggleOverview],
            "custom non-Win shortcuts are supported without mutating defaults");
        var disabled = Disabled();
        Check(disabled.Validate() is null && disabled.Bindings.Values.All(binding => binding is null),
            "every shortcut including RestoreAll can be explicitly disabled");
        Check(ShortcutProfile.AllowedKeys.Count == 63 && ShortcutProfile.AllowedKeys.Distinct().Count() == 63
            && Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x70, 12))
                .All(ShortcutProfile.AllowedKeys.Contains), "shortcut key inventory includes letters, digits, F1-F12 and navigation keys");
        foreach (var binding in new[]
        {
            new ShortcutBinding(ShortcutModifiers.Win, 0), new ShortcutBinding(ShortcutModifiers.Control, 0x10),
            new ShortcutBinding(ShortcutModifiers.Win, 0x7C), new ShortcutBinding((ShortcutModifiers)32, 0x41)
        })
            Check(disabled.With(DesktopCommand.ToggleOverview, binding).Validate()?.Kind == ShortcutValidationIssueKind.InvalidKey,
                $"invalid shortcut key or modifiers rejected ({(int)binding.Modifiers}, {binding.VirtualKey})");
        foreach (var modifiers in new[] { ShortcutModifiers.None, ShortcutModifiers.Shift })
            Check(disabled.With(DesktopCommand.ToggleOverview, new(modifiers, 0x41)).Validate()?.Kind == ShortcutValidationIssueKind.NeedModifier,
                $"shortcut requires Win, Ctrl or Alt ({modifiers})");

        ShortcutBinding[] reserved =
        [
            new(ShortcutModifiers.Win, 0x4C), new(ShortcutModifiers.Win | ShortcutModifiers.Alt | ShortcutModifiers.Shift, 0x4C),
            new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x2E), new(desktop | ShortcutModifiers.Alt | ShortcutModifiers.Shift, 0x2E),
            new(ShortcutModifiers.Alt, 0x09), new(ShortcutModifiers.Alt | ShortcutModifiers.Shift, 0x09),
            new(ShortcutModifiers.Alt, 0x1B), new(ShortcutModifiers.Alt | ShortcutModifiers.Control, 0x73),
            new(ShortcutModifiers.Control, 0x1B), new(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x1B),
            new(move, 0x42), new(move | ShortcutModifiers.Alt, 0x42)
        ];
        Check(reserved.All(binding => disabled.With(DesktopCommand.ToggleOverview, binding).Validate()?.Kind
                == ShortcutValidationIssueKind.Reserved), "selected protected Windows shortcuts and modifier variants are rejected");
        var duplicate = ShortcutProfile.Defaults.With(DesktopCommand.PreviousDesktop, ShortcutProfile.Defaults[DesktopCommand.NextDesktop]);
        Check(duplicate.Validate() is { Kind: ShortcutValidationIssueKind.Duplicate, Command: DesktopCommand.NextDesktop,
                OtherCommand: DesktopCommand.PreviousDesktop }, "duplicate validation identifies both conflicting commands");
        Check(ShortcutText.Format(new(ShortcutModifiers.Win | ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift, 0x71))
                == "Win+Ctrl+Alt+Shift+F2" && ShortcutText.Format(null) == UiText.Get("Shortcuts.Disabled"),
            "shortcut labels use consistent modifier ordering and localized disabled text");

        Directory.CreateDirectory(output);
        string path = Path.Combine(output, $"shortcut-preferences-{Guid.NewGuid():N}.json");
        try
        {
            Check(Equivalent(ShortcutPreferences.Load(path), ShortcutProfile.Defaults), "missing shortcut settings use default profile");
            File.WriteAllText(path, "{\"language\":\"ja\",\"unrelated\":{\"keep\":17}}");
            foreach (var profile in new[] { ShortcutProfile.Defaults, ShortcutProfile.WindowsStyle, custom, disabled })
            {
                ShortcutPreferences.Save(profile, path);
                Check(Equivalent(ShortcutPreferences.Load(path), profile),
                    $"shortcut profile roundtrips through temporary settings ({ShortcutText.Format(profile[DesktopCommand.ToggleOverview])})");
            }
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(saved["language"]!.GetValue<string>() == "ja" && saved["unrelated"]!["keep"]!.GetValue<int>() == 17,
                "saving shortcuts preserves language and unrelated preferences");
            UiText.WriteLanguagePreference(path, "zh-Hant");
            Check(Equivalent(ShortcutPreferences.Load(path), disabled) && UiText.ReadLanguagePreference(path) == "zh-Hant",
                "saving language preserves disabled shortcut preferences");
            string beforeInvalidSave = File.ReadAllText(path);
            Check(Throws<ArgumentException>(() => ShortcutPreferences.Save(duplicate, path))
                && File.ReadAllText(path) == beforeInvalidSave, "invalid shortcut saves leave existing preferences unchanged");

            ShortcutPreferences.Save(custom, path);
            var valid = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var malformedProfiles = new List<JsonNode?> { null, JsonValue.Create("invalid"), new JsonObject() };
            JsonObject Mutated(Action<JsonObject> mutate)
            {
                var shortcuts = valid["keyboardShortcuts"]!.DeepClone().AsObject();
                mutate(shortcuts);
                return shortcuts;
            }
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["UnknownCommand"] = null));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts.Remove("RestoreAll")));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["ToggleOverview"]!["virtualKey"] = 0x7C));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["ToggleOverview"]!["modifiers"] = 32));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["ToggleOverview"]!["virtualKey"] = "Tab"));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["ToggleOverview"]!["extra"] = true));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["ToggleOverview"] = new JsonObject
                { ["modifiers"] = (int)ShortcutModifiers.Win, ["virtualKey"] = 0x4C }));
            malformedProfiles.Add(Mutated(shortcuts => shortcuts["PreviousDesktop"] = shortcuts["NextDesktop"]!.DeepClone()));
            bool allFallback = true;
            foreach (var malformed in malformedProfiles)
            {
                var document = new JsonObject { ["language"] = "ko", ["keyboardShortcuts"] = malformed };
                File.WriteAllText(path, document.ToJsonString());
                allFallback &= Equivalent(ShortcutPreferences.Load(path), ShortcutProfile.Defaults);
            }
            Check(allFallback, "malformed, partial, reserved, duplicate and unknown shortcut profiles fall back as a whole");
            foreach (string invalidJson in new[] { "{broken", "[]", "null", "{\"keyboardShortcuts\":{\"ToggleOverview\":null,\"ToggleOverview\":null}}" })
            {
                File.WriteAllText(path, invalidJson);
                allFallback &= Equivalent(ShortcutPreferences.Load(path), ShortcutProfile.Defaults);
            }
            Check(allFallback, "invalid JSON roots and repeated shortcut command names fall back safely");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
