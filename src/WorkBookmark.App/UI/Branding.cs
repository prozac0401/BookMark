using System.Drawing;

namespace WorkBookmark.App.UI;

/// <summary>Creates owned icons from the artwork embedded in the executable.</summary>
internal static class Branding
{
    public static Icon CreateApplicationIcon() => CreateIcon("WorkBookmark.ico", new Size(32, 32));

    public static Icon CreateTrayIcon() => CreateIcon("WorkBookmark.Tray.ico", SystemInformation.SmallIconSize);

    private static Icon CreateIcon(string name, Size size)
    {
        using Stream stream = typeof(Branding).Assembly.GetManifestResourceStream($"WorkBookmark.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing embedded brand icon: {name}");
        using var icon = new Icon(stream, size);
        return (Icon)icon.Clone();
    }
}
