using System.Text.Json;
using System.Text.Json.Nodes;
using DisplaySpaces.App.Native;

namespace DisplaySpaces.App.Shortcuts;

/// <summary>UI-thread read/merge/write preferences sharing the existing language settings file.</summary>
public static class ShortcutPreferences
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");

    public static ShortcutProfile Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return ShortcutProfile.Defaults;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keyboardShortcuts", out var shortcuts)
                || shortcuts.ValueKind != JsonValueKind.Object) return ShortcutProfile.Defaults;
            var bindings = new Dictionary<DesktopCommand, ShortcutBinding?>();
            foreach (var property in shortcuts.EnumerateObject())
            {
                if (!Enum.GetNames<DesktopCommand>().Contains(property.Name)
                    || !Enum.TryParse<DesktopCommand>(property.Name, out var command)
                    || bindings.ContainsKey(command)) return ShortcutProfile.Defaults;
                if (property.Value.ValueKind == JsonValueKind.Null) bindings.Add(command, null);
                else
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) return ShortcutProfile.Defaults;
                    var fields = property.Value.EnumerateObject().Select(field => field.Name).ToArray();
                    if (fields.Length != 2 || fields.Distinct().Count() != 2
                        || !property.Value.TryGetProperty("modifiers", out var modifiers)
                        || modifiers.ValueKind != JsonValueKind.Number || !modifiers.TryGetInt32(out var modifierValue)
                        || !property.Value.TryGetProperty("virtualKey", out var key)
                        || key.ValueKind != JsonValueKind.Number || !key.TryGetInt32(out var virtualKey))
                        return ShortcutProfile.Defaults;
                    bindings.Add(command, new ShortcutBinding((ShortcutModifiers)modifierValue, virtualKey));
                }
            }
            var profile = new ShortcutProfile(bindings);
            return profile.Validate() is null ? profile : ShortcutProfile.Defaults;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return ShortcutProfile.Defaults; }
    }

    public static void Save(ShortcutProfile profile, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Validate() is not null) throw new ArgumentException("Cannot save an invalid shortcut profile.", nameof(profile));
        path = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        var shortcuts = new JsonObject();
        foreach (var (command, binding) in profile.Bindings)
            shortcuts[command.ToString()] = binding is null ? null : new JsonObject
            {
                ["modifiers"] = (int)binding.Modifiers,
                ["virtualKey"] = binding.VirtualKey
            };
        settings["keyboardShortcuts"] = shortcuts;
        string temporary = Path.Combine(directory, $".shortcuts-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
