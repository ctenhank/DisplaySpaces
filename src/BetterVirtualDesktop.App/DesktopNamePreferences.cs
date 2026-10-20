using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterVirtualDesktop.App;

internal enum DesktopNameDisplayMode
{
    Off,
    OnSwitch,
    Always
}

internal sealed record DesktopNameSettings(
    DesktopNameDisplayMode Mode = DesktopNameDisplayMode.OnSwitch,
    DesktopToastCorner Corner = DesktopToastCorner.TopRight);

/// <summary>Shares the existing UI-thread settings file with language and shortcut preferences.</summary>
internal static class DesktopNamePreferences
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");

    public static DesktopNameSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("desktopName", out var settings)
                || settings.ValueKind != JsonValueKind.Object) return new();
            return new(ReadEnum(settings, "mode", DesktopNameDisplayMode.OnSwitch),
                ReadEnum(settings, "corner", DesktopToastCorner.TopRight));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }

    private static T ReadEnum<T>(JsonElement settings, string key, T fallback) where T : struct, Enum =>
        settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && Enum.GetNames<T>().Contains(value.GetString())
        && Enum.TryParse<T>(value.GetString(), out var parsed) ? parsed : fallback;

    public static void Save(DesktopNameSettings preferences, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.Mode) || !Enum.IsDefined(preferences.Corner))
            throw new ArgumentException("Invalid desktop name preferences.", nameof(preferences));
        path = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["desktopName"] = new JsonObject
        {
            ["mode"] = preferences.Mode.ToString(),
            ["corner"] = preferences.Corner.ToString()
        };
        string temporary = Path.Combine(directory, $".desktop-name-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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
