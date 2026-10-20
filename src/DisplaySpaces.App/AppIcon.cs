namespace DisplaySpaces.App;

/// <summary>
/// Process-owned icons shared by the application's windows and notification area.
/// Consumers borrow these instances and must not dispose them when a window closes.
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "DisplaySpaces.App.Assets.DisplaySpaces.ico";
    private static readonly Lazy<Icon> WindowIcon = new(() => Load(SystemInformation.IconSize));
    private static readonly Lazy<Icon> TrayIcon = new(() => Load(SystemInformation.SmallIconSize));

    public static Icon Window => WindowIcon.Value;
    public static Icon Tray => TrayIcon.Value;

    private static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded DisplaySpaces icon is missing.");
        using var source = new Icon(stream, size);
        // Clone while the stream is alive. The shared result owns its native icon
        // and retains the ICO data, including the other available frame sizes.
        return (Icon)source.Clone();
    }
}
