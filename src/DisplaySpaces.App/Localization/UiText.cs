using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DisplaySpaces.Core;

namespace DisplaySpaces.App.Localization;

internal sealed record LanguageOption(string Code, string NativeName);

/// <summary>UI-thread language selection; only generated desktop labels are translated.</summary>
internal static class UiText
{
    private static readonly string OsCultureName = CultureInfo.CurrentUICulture.Name;
    private static string _systemCultureName = OsCultureName;
    // Retain the existing preference file instead of migrating or resetting user settings.
    private static string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "settings.json");
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = Array.AsReadOnly<LanguageOption>(
        [new("ko", "한국어"), new("en", "English"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"), new("ja", "日本語")]);
    public static string SelectedLanguageCode { get; private set; } = "system";
    public static string CurrentLanguageCode { get; private set; } = ResolveLanguage(OsCultureName);
    public static event Action? Changed;
    public static string FontFamily => CurrentLanguageCode switch
    {
        "ko" => "Malgun Gothic",
        "ja" => "Yu Gothic UI",
        "zh-Hans" => "Microsoft YaHei UI",
        "zh-Hant" => "Microsoft JhengHei UI",
        _ => "Segoe UI"
    };

    public static void Initialize(string? settingsPath = null, string? systemCultureName = null)
    {
        // The legacy directory is shared with older builds and their recovery journals.
        _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IndependentVirtualDesktop", "settings.json");
        _systemCultureName = systemCultureName ?? OsCultureName;
        SetLanguage(ReadLanguagePreference(_settingsPath));
    }

    public static void SetLanguage(string code, bool persist = false)
    {
        string selection = NormalizeSelection(code) ?? throw new ArgumentException("Unsupported UI language.", nameof(code));
        string resolved = selection == "system" ? ResolveLanguage(_systemCultureName) : selection;
        if (persist) WriteLanguagePreference(_settingsPath, selection);
        bool changed = SelectedLanguageCode != selection || CurrentLanguageCode != resolved;
        SelectedLanguageCode = selection;
        CurrentLanguageCode = resolved;
        if (changed) Changed?.Invoke();
    }

    public static string Get(string key, params object[] args)
    {
        var text = UiTextResources.Languages[CurrentLanguageCode].GetValueOrDefault(key)
            ?? UiTextResources.Languages["en"].GetValueOrDefault(key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.GetCultureInfo(CurrentLanguageCode), text, args);
    }

    public static string DesktopName(DesktopSnapshot desktop) => desktop.DefaultNumber is int number
        ? Get("DesktopDefault", number) : desktop.Name;

    public static string DisplayName(DisplaySnapshot display)
    {
        var screens = Screen.AllScreens;
        int index = Array.FindIndex(screens, screen => screen.DeviceName == display.Id);
        return index < 0 ? display.Name : Get("DisplayDefault", index + 1)
            + (display.IsPrimary ? Get("PrimaryDisplaySuffix") : "");
    }

    internal static IReadOnlyCollection<string> ResourceKeys => UiTextResources.Languages["en"].Keys;
    internal static bool HasTranslation(string code, string key) =>
        UiTextResources.Languages.TryGetValue(code, out var language) && language.ContainsKey(key);

    internal static string ResolveLanguage(string cultureName)
    {
        string name = cultureName.Replace('_', '-').ToLowerInvariant();
        if (name == "ko" || name.StartsWith("ko-", StringComparison.Ordinal)) return "ko";
        if (name == "ja" || name.StartsWith("ja-", StringComparison.Ordinal)) return "ja";
        if (name == "zh" || name.StartsWith("zh-", StringComparison.Ordinal))
        {
            var parts = name.Split('-');
            if (parts.Contains("hant")) return "zh-Hant";
            if (parts.Contains("hans")) return "zh-Hans";
            return parts.Any(p => p is "tw" or "hk" or "mo") ? "zh-Hant" : "zh-Hans";
        }
        return "en";
    }

    private static string? NormalizeSelection(string? code)
    {
        if (string.Equals(code, "system", StringComparison.OrdinalIgnoreCase)) return "system";
        return SupportedLanguages.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;
    }

    internal static string ReadLanguagePreference(string path)
    {
        try
        {
            if (!File.Exists(path)) return "system";
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("language", out var language)
                || language.ValueKind != JsonValueKind.String) return "system";
            return NormalizeSelection(language.GetString()) ?? "system";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return "system"; }
    }

    internal static void WriteLanguagePreference(string path, string code)
    {
        string selection = NormalizeSelection(code) ?? throw new ArgumentException("Unsupported UI language.", nameof(code));
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        JsonObject settings;
        try { settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings["language"] = selection;
        string temporary = Path.Combine(directory, $".settings-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
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
