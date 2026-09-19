using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterVirtualDesktop.App.Localization;
using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App;

/// <summary>Local resource checks and standalone overlay renders; never switches desktops or moves real windows.</summary>
internal static class LocalizationSelfTests
{
    public static void Run(Screen screen, DisplaySnapshot fixtureDisplay,
        IReadOnlyDictionary<long, NativeWindowInfo> fixtureWindows, string outputDirectory, List<string> results)
    {
        string previousLanguage = UiText.SelectedLanguageCode;
        try
        {
            CheckResources(results);
            CheckPreferences(outputDirectory, results);
            CheckNames(results);
            RenderFeedback(screen, fixtureDisplay, fixtureWindows, outputDirectory, results);
        }
        finally { UiText.SetLanguage(previousLanguage, persist: false); }
    }

    private static void CheckResources(List<string> results)
    {
        string[] expected = ["ko", "en", "zh-Hans", "zh-Hant", "ja"];
        Require(UiText.SupportedLanguages.Select(language => language.Code).Order().SequenceEqual(expected.Order()),
            "The supported language inventory must contain Korean, English, Simplified/Traditional Chinese, and Japanese.");
        var keys = UiText.ResourceKeys.ToArray();
        Require(keys.Length > 0, "The resource inventory is empty.");
        UiText.SetLanguage("en", persist: false);
        var placeholders = keys.ToDictionary(key => key, key => PlaceholderIndexes(UiText.Get(key)));
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            foreach (string key in keys)
            {
                Require(UiText.HasTranslation(language.Code, key), $"Missing {language.Code} resource: {key}.");
                Require(PlaceholderIndexes(UiText.Get(key)).SequenceEqual(placeholders[key]),
                    $"Formatting arguments differ in {language.Code} resource: {key}.");
                string text = UiText.Get(key, Enumerable.Range(1, 10).Cast<object>().ToArray());
                Require(!string.IsNullOrWhiteSpace(text), $"Empty {language.Code} resource: {key}.");
            }
        }
        results.Add($"PASS all {keys.Length} UI resources exist and format successfully in every supported language");

        var cases = new Dictionary<string, string>
        {
            ["ko-KR"] = "ko", ["en-GB"] = "en", ["ja-JP"] = "ja",
            ["zh"] = "zh-Hans", ["zh-CN"] = "zh-Hans", ["zh-SG"] = "zh-Hans", ["zh-Hans"] = "zh-Hans",
            ["zh-TW"] = "zh-Hant", ["zh-HK"] = "zh-Hant", ["zh-MO"] = "zh-Hant", ["zh-Hant"] = "zh-Hant",
            ["de-DE"] = "en", [""] = "en"
        };
        foreach (var item in cases)
            Require(UiText.ResolveLanguage(item.Key) == item.Value, $"Unexpected locale fallback for {item.Key}.");
        results.Add("PASS system locale selection distinguishes Chinese scripts and falls back to English for unsupported cultures");
    }

    private static string[] PlaceholderIndexes(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")
        .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();

    private static void CheckPreferences(string outputDirectory, List<string> results)
    {
        string path = Path.Combine(outputDirectory, $"language-settings-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"fixtureToken\":42}");
            foreach (string code in UiText.SupportedLanguages.Select(language => language.Code).Append("system"))
            {
                UiText.WriteLanguagePreference(path, code);
                Require(UiText.ReadLanguagePreference(path) == code, $"Language preference failed to roundtrip: {code}.");
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Require(json.RootElement.GetProperty("fixtureToken").GetInt32() == 42, "Saving a language removed another setting.");
            }
            File.WriteAllText(path, "{\"language\":\"unsupported-fixture-language\"}");
            Require(UiText.ReadLanguagePreference(path) == "system", "An unsupported saved language did not fall back to system.");
            File.WriteAllText(path, "{ invalid json");
            Require(UiText.ReadLanguagePreference(path) == "system", "A corrupt language preference did not fall back to system.");
            results.Add("PASS language preferences roundtrip without modifying unrelated settings and recover from malformed JSON");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckNames(List<string> results)
    {
        var catalog = new WorkspaceCatalog();
        catalog.SynchronizeDisplays([new("localization-fixture", "Fixture")]);
        Guid automaticId = catalog.AddDesktop("localization-fixture");
        Guid literalId = catalog.AddDesktop("localization-fixture", "Desktop 7");
        Guid renamedId = catalog.AddDesktop("localization-fixture");
        catalog.RenameDesktop("localization-fixture", renamedId, "Desktop 9 · 작업 / 工作 / 作業");
        var desktops = catalog.Snapshot().Single().Desktops;
        var automatic = desktops.Single(desktop => desktop.Id == automaticId);
        var literal = desktops.Single(desktop => desktop.Id == literalId);
        var renamed = desktops.Single(desktop => desktop.Id == renamedId);
        Require(desktops[0].DefaultNumber == 1 && automatic.DefaultNumber == 2
            && literal.DefaultNumber is null && renamed.DefaultNumber is null,
            "Default desktop numbering must be explicit metadata, cleared by user naming.");
        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            Require(UiText.DesktopName(literal) == literal.Name && UiText.DesktopName(renamed) == renamed.Name,
                $"User desktop names were translated in {language.Code}.");
            string automaticName = UiText.DesktopName(automatic);
            Require(!string.IsNullOrWhiteSpace(automaticName) && automaticName.Contains('2'), "The default desktop number was lost.");
            if (language.Code != "en") Require(automaticName != automatic.Name, $"A default desktop name was not localized for {language.Code}.");
        }
        results.Add("PASS default desktop names localize while explicit and renamed multilingual desktop names remain unchanged");
    }

    private static void RenderFeedback(Screen screen, DisplaySnapshot fixtureDisplay,
        IReadOnlyDictionary<long, NativeWindowInfo> fixtureWindows, string outputDirectory, List<string> results)
    {
        Guid created = Guid.NewGuid();
        // An off-page destination proves creation reveals the new card, rather than leaving
        // the pointer's old plus-card as the only visible indication of the completed action.
        var desktops = new List<DesktopSnapshot> { new(fixtureDisplay.ActiveDesktopId, "Desktop 1", [], 1) };
        for (int number = 2; number <= 11; number++) desktops.Add(new(Guid.NewGuid(), $"Desktop {number}", [], number));
        desktops.Add(new(created, "Desktop 12", fixtureDisplay.Desktops.SelectMany(desktop => desktop.WindowIds).ToArray(), 12));
        var display = fixtureDisplay with { Desktops = desktops.AsReadOnly() };
        using var form = new DisplayOverlayForm(screen, display);
        int switchRequests = 0;
        (string DisplayId, Guid DesktopId, long? WindowHandle)? requestedDestination = null;
        form.SelectionRequested += (displayId, desktopId, handle) =>
        {
            switchRequests++;
            requestedDestination = (displayId, desktopId, handle);
        };
        form.UpdateSnapshot(screen, display, fixtureWindows);
        _ = form.Handle;
        var regularSize = new Size(Math.Min(screen.Bounds.Width, (int)Math.Round(1280 * form.DeviceDpi / 96d)),
            Math.Min(screen.Bounds.Height, (int)Math.Round(760 * form.DeviceDpi / 96d)));
        form.ClientSize = regularSize;
        long foreground = NativeWindows.ForegroundWindow;
        form.Show();
        PumpFor(100);
        Require(NativeWindows.ForegroundWindow == foreground, "Showing a nonactivating feedback fixture changed foreground focus.");

        foreach (var language in UiText.SupportedLanguages)
        {
            UiText.SetLanguage(language.Code, persist: false);
            form.ApplyLanguage();
            form.ClientSize = regularSize;
            form.ShowCreationFeedback(created);
            PumpFor(70);
            form.Refresh();
            Require(form.HasCreationFeedback && form.SelectedDesktopId == created
                && display.ActiveDesktopId == fixtureDisplay.ActiveDesktopId && switchRequests == 0,
                $"Creation feedback switched a desktop instead of selecting its preview in {language.Code}.");
            Require(form.CreationFeedbackTitle?.Contains(UiText.DesktopName(desktops[^1]), StringComparison.Ordinal) == true,
                $"Creation feedback omitted the localized destination name in {language.Code}.");
            Require(form.ClientRectangle.Contains(form.CreationFeedbackBounds)
                && form.ClientRectangle.Contains(form.DesktopCardBounds(created)),
                $"Creation feedback or the newly created desktop card escaped the viewport in {language.Code}.");
            Require(NativeWindows.ForegroundWindow == foreground, "Creation feedback activated a window.");
            Save(form, Path.Combine(outputDirectory, $"overlay-language-{language.Code}.png"));

            form.ClientSize = new Size(914, 512);
            form.Refresh();
            Require(form.CreationFeedbackBounds.Width > 0 && form.CreationFeedbackBounds.Height > 0
                && form.ClientRectangle.Contains(form.CreationFeedbackBounds)
                && !form.CreationFeedbackBounds.IntersectsWith(form.DesktopRailBounds),
                $"Creation feedback does not fit the compact overlay in {language.Code}.");
            Save(form, Path.Combine(outputDirectory, $"overlay-language-{language.Code}-compact.png"));
        }
        results.Add($"PASS creation feedback renders in five languages at {regularSize.Width}x{regularSize.Height} ({form.DeviceDpi} DPI) and compact 914x512, reveals its destination, and preserves desktop state and focus");

        // Language and ordinary catalog refreshes must preserve the existing notification's
        // expiry, rather than extending it on every timer tick.
        form.ShowCreationFeedback(created);
        var elapsed = Stopwatch.StartNew();
        PumpFor(1100);
        UiText.SetLanguage("en", persist: false);
        form.ApplyLanguage();
        Require(form.HasCreationFeedback
            && form.CreationFeedbackTitle?.Contains(UiText.DesktopName(desktops[^1]), StringComparison.Ordinal) == true,
            "Changing language lost the active creation message or kept its old desktop label.");
        while (elapsed.Elapsed < DisplayOverlayForm.CreationFeedbackDuration + TimeSpan.FromMilliseconds(450))
        {
            form.UpdateSnapshot(screen, display, fixtureWindows);
            form.Refresh();
            PumpFor(90);
        }
        Require(!form.HasCreationFeedback && switchRequests == 0 && NativeWindows.ForegroundWindow == foreground,
            "Polling renewed creation feedback or changed focus/desktop selection.");
        form.UpdateSnapshot(screen, display with { ActiveDesktopId = created }, fixtureWindows);
        form.Refresh();
        Require(!form.HasCreationFeedback, "A real desktop switch was incorrectly reported as desktop creation.");
        results.Add("PASS creation feedback expires despite language/catalog refreshes and actual desktop switching does not trigger creation feedback");

        form.UpdateSnapshot(screen, display, fixtureWindows);
        form.ClientSize = regularSize;
        form.ShowCreationFeedback(created);
        form.Refresh();
        Rectangle openButton = form.CreationOpenButtonBounds;
        Require(openButton.Width > 0 && openButton.Height > 0 && form.ClientRectangle.Contains(openButton),
            "The creation feedback open button was not rendered inside the overlay.");
        var point = new Point(openButton.Left + openButton.Width / 2, openButton.Top + openButton.Height / 2);
        foreach (string method in new[] { "OnMouseDown", "OnMouseUp" })
        {
            var callback = typeof(DisplayOverlayForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(DisplayOverlayForm).Name, method);
            callback.Invoke(form, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        }
        Require(switchRequests == 1 && requestedDestination is { } requested
            && requested.DisplayId == display.Id && requested.DesktopId == created && requested.WindowHandle is null,
            "Clicking the creation feedback open button did not request exactly one switch to its destination.");
        results.Add("PASS creation feedback open button requests exactly one switch to the new desktop through mouse down/up handling");
        form.Shutdown();
    }

    private static void Save(DisplayOverlayForm form, string path)
    {
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(path, ImageFormat.Png);
    }

    private static void PumpFor(int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
