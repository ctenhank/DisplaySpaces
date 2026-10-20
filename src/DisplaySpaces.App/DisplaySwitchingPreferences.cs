using System.Text.Json;
using System.Text.Json.Nodes;
using DisplaySpaces.Core;

namespace DisplaySpaces.App;

internal static class DisplaySwitchingPreferences
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");

    public static DisplaySwitchingMode Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return DisplaySwitchingMode.Independent;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("displaySwitchingMode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && Enum.GetNames<DisplaySwitchingMode>().Contains(mode.GetString())
                && Enum.TryParse<DisplaySwitchingMode>(mode.GetString(), out var parsed)
                    ? parsed : DisplaySwitchingMode.Independent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return DisplaySwitchingMode.Independent; }
    }

    public static void Save(DisplaySwitchingMode mode, string? path = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        path = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["displaySwitchingMode"] = mode.ToString();
        string temporary = Path.Combine(directory, $".display-switching-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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
