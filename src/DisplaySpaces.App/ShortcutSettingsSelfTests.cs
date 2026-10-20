using System.Diagnostics;
using System.Drawing.Imaging;
using DisplaySpaces.App.Localization;
using DisplaySpaces.App.Native;
using DisplaySpaces.App.Shortcuts;

namespace DisplaySpaces.App;

/// <summary>Standalone settings UI with in-memory callbacks; never installs a hook or writes user preferences.</summary>
internal static class ShortcutSettingsSelfTests
{
    public static void Run(string output, List<string> results)
    {
        string originalLanguage = UiText.SelectedLanguageCode;
        static bool Same(ShortcutProfile a, ShortcutProfile b) =>
            Enum.GetValues<DesktopCommand>().All(command => a[command] == b[command]);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            results.Add("PASS " + message);
        }
        try
        {
            UiText.SetLanguage("ko", persist: false);
            int saves = 0;
            ShortcutProfile? applied = null;
            using (var form = new ShortcutSettingsForm(ShortcutProfile.Defaults, profile => { saves++; applied = profile; return null; }))
            {
                form.Show();
                Pump();
                form.LoadWindowsPreset();
                Check(saves == 0 && Same(form.DraftProfile, ShortcutProfile.WindowsStyle),
                    "Windows shortcut preset edits a draft without applying global shortcuts");
                form.LoadDefaults();
                Check(saves == 0 && Same(form.DraftProfile, ShortcutProfile.Defaults),
                    "restoring default shortcuts remains a draft until Save");
                form.DraftProfile = ShortcutProfile.Defaults.With(DesktopCommand.ToggleOverview,
                    ShortcutProfile.Defaults[DesktopCommand.PreviousDesktop]);
                Check(!form.CanSave && form.ValidationMessage.Length > 0 && !form.AttemptSave() && saves == 0,
                    "duplicate shortcuts show an inline error and cannot be saved");
                var edited = ShortcutProfile.WindowsStyle.With(DesktopCommand.AddDesktop,
                    new ShortcutBinding(ShortcutModifiers.Control | ShortcutModifiers.Alt, (int)Keys.N))
                    .With(DesktopCommand.MoveWindowNext, null);
                form.DraftProfile = edited;
                UiText.SetLanguage("ja", persist: false);
                Check(form.CanSave && Same(form.DraftProfile, edited) && form.ValidationMessage.Length == 0,
                    "language changes preserve unsaved custom and disabled shortcut choices");
                Check(form.AttemptSave() && saves == 1 && applied is not null && Same(applied, edited)
                    && form.DialogResult == DialogResult.OK,
                    "Save applies the selected shortcut profile exactly once and closes successfully");
            }
            using (var form = new ShortcutSettingsForm(ShortcutProfile.Defaults, _ => { saves++; return null; }))
            {
                form.Show();
                form.LoadWindowsPreset();
                form.CancelButton!.PerformClick();
                Check(saves == 1 && form.DialogResult == DialogResult.Cancel && !form.Visible,
                    "Cancel discards shortcut preset changes without applying them");
            }
            using (var form = new ShortcutSettingsForm(ShortcutProfile.Defaults, _ => "fixture save failure"))
            {
                form.Show();
                form.LoadWindowsPreset();
                Check(!form.AttemptSave() && form.Visible && form.DialogResult != DialogResult.OK
                    && form.ValidationMessage == "fixture save failure" && form.CanSave,
                    "failed shortcut persistence leaves the draft open with an inline error and retry enabled");
                form.LoadDefaults();
                Check(form.ValidationMessage.Length == 0,
                    "editing the shortcut draft clears a previous persistence error");
                form.Close();
            }
            foreach (var language in UiText.SupportedLanguages)
            {
                UiText.SetLanguage(language.Code, persist: false);
                using var form = new ShortcutSettingsForm(ShortcutProfile.WindowsStyle, _ => null);
                form.Show();
                Pump();
                Check(form.CanSave && form.Text.Contains(UiText.Get("Shortcuts.Title")),
                    $"shortcut settings are translated and accept the Windows preset: {language.Code}");
                Render(form, Path.Combine(output, $"shortcuts-{language.Code}.png"));
                Check(ModifierControlsFit(form), $"all shortcut modifiers fit their editor rows: {language.Code}");
                form.Size = form.MinimumSize;
                Pump();
                Render(form, Path.Combine(output, $"shortcuts-{language.Code}-compact.png"));
                Check(ModifierControlsFit(form), $"all shortcut modifiers remain available at minimum size: {language.Code}");
                form.Close();
            }
            results.Add("PASS shortcut settings rendered in all five languages at normal and minimum sizes");
        }
        finally { UiText.SetLanguage(originalLanguage, persist: false); }
    }

    private static void Render(Form form, string path)
    {
        form.PerformLayout();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(path, ImageFormat.Png);
    }

    private static bool ModifierControlsFit(Control control)
    {
        foreach (Control child in control.Controls)
        {
            if (child is CheckBox && !control.ClientRectangle.Contains(child.Bounds)) return false;
            if (!ModifierControlsFit(child)) return false;
        }
        return true;
    }

    private static void Pump()
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 100)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
