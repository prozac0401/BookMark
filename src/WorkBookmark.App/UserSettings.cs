using System.Text.Json;
using System.Windows.Forms;

namespace WorkBookmark.App;

public sealed record Hotkey(uint Modifiers, int VirtualKey)
{
    public static Hotkey CaptureDefault => new(3, (int)Keys.B);
    public static Hotkey RecentDefault => new(3, (int)Keys.J);
    public bool IsValid => (Modifiers & ~15u) == 0 && (Modifiers & 3u) != 0 && VirtualKey is >= 0x30 and <= 0xFE && VirtualKey is not (int)Keys.ControlKey and not (int)Keys.ShiftKey and not (int)Keys.Menu;
    public override string ToString() => string.Join("+", new[] { (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null, (Modifiers & 4) != 0 ? "Shift" : null, (Modifiers & 8) != 0 ? "Win" : null, ((Keys)VirtualKey).ToString() }.Where(s => s is not null));
}

public sealed record UserSettings(int Version, Hotkey CaptureHotkey, Hotkey RecentHotkey, bool StartWithWindows, bool IntroShown)
{
    public static UserSettings Default => new(1, Hotkey.CaptureDefault, Hotkey.RecentDefault, false, false);
    public static UserSettings Load(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return Default;
        var value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
        if (value is null || value.Version != 1 || value.CaptureHotkey is null || value.RecentHotkey is null || !value.CaptureHotkey.IsValid || !value.RecentHotkey.IsValid || value.CaptureHotkey == value.RecentHotkey)
            throw new InvalidDataException("설정 파일을 읽을 수 없습니다. 원본은 유지됩니다.");
        return value;
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
