using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterVirtualDesktop.App;

internal static class WindowMovePreferences
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");

    public static bool Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("followMovedWindow", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return false; }
    }

    public static void Save(bool follow, string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["followMovedWindow"] = follow;
        string temporary = Path.Combine(directory, $".window-move-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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
