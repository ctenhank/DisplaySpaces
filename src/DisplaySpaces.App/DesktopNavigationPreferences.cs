using System.Text.Json;
using System.Text.Json.Nodes;

namespace DisplaySpaces.App;

internal static class DesktopNavigationPreferences
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");

    public static bool Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("wrapDesktopSwitching", out var value)
                || value.ValueKind != JsonValueKind.False;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return true; }
    }

    public static void Save(bool wrapAround, string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["wrapDesktopSwitching"] = wrapAround;
        string temporary = Path.Combine(directory, $".navigation-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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
