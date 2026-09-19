namespace BetterVirtualDesktop.App.Services;

internal static class AppLog
{
    public static void Write(Exception exception)
    {
        try
        {
            // Keep diagnostics alongside the legacy settings and recovery state.
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndependentVirtualDesktop");
            Directory.CreateDirectory(path);
            File.AppendAllText(Path.Combine(path, "app.log"), $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
