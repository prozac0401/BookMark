using System.Reflection;
using System.Text.Json;
using WorkBookmark.App;

namespace WorkBookmark.Desktop.Tests;

internal static class SettingsDisplayChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        var previous = UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F19),
            RecentHotkey = new Hotkey(7, (int)Keys.F20),
            StartWithWindows = true,
            IntroShown = true
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            previous.Version, previous.CaptureHotkey, previous.RecentHotkey,
            previous.StartWithWindows, previous.IntroShown
        }));
        assert(UserSettings.Load(directory) == previous,
            "SET01 existing settings without display mode retain values and open as List");

        var stickers = previous with { DisplayMode = BookmarkDisplayMode.Stickers };
        stickers.Save(directory);
        assert(UserSettings.Load(directory) == stickers,
            "SET02 sticker display choice survives saving and reloading");
        string invalid = JsonSerializer.Serialize(stickers with { DisplayMode = (BookmarkDisplayMode)99 });
        File.WriteAllText(path, invalid);
        bool rejected = false;
        try { UserSettings.Load(directory); }
        catch (InvalidDataException) { rejected = true; }
        assert(rejected && File.ReadAllText(path) == invalid,
            "SET03 unknown display mode is rejected without rewriting settings");

        Type formType = typeof(UserSettings).Assembly.GetType("WorkBookmark.App.UI.SettingsForm")!;
        UserSettings? applied = null;
        bool fail = true;
        Func<UserSettings, Task<string?>> apply = candidate =>
        {
            applied = candidate;
            return Task.FromResult<string?>(fail ? "테스트: 설정 저장 실패" : null);
        };
        using (var form = (Form)Activator.CreateInstance(formType, previous, true, true,
            directory, apply, (Action)(() => { }), (Action)(() => { }))!)
        {
            form.Show();
            Application.DoEvents();
            var list = Field<RadioButton>(form, "_listDisplay");
            var sticker = Field<RadioButton>(form, "_stickerDisplay");
            var save = Field<Button>(form, "_apply");
            assert(list.Checked && !sticker.Checked,
                "SET04 settings shows the saved List choice");
            sticker.Checked = true;
            assert(sticker.Checked && !list.Checked,
                "SET05 display choices remain mutually exclusive");
            save.PerformClick();
            Application.DoEvents();
            assert(!form.IsDisposed && save.Enabled && sticker.Checked &&
                Field<Label>(form, "_status").Text == "테스트: 설정 저장 실패" &&
                applied == stickers,
                "SET06 failed apply retains selected mode and existing settings for retry");
            fail = false;
            save.PerformClick();
            Application.DoEvents();
            assert(form.IsDisposed && applied == stickers,
                "SET07 successful apply closes settings and includes the selected mode");
        }

        applied = null;
        using (var form = (Form)Activator.CreateInstance(formType, stickers, true, true,
            directory, apply, (Action)(() => { }), (Action)(() => { }))!)
        {
            form.Show();
            Application.DoEvents();
            assert(Field<RadioButton>(form, "_stickerDisplay").Checked,
                "SET08 settings shows the saved Stickers choice");
            Field<RadioButton>(form, "_listDisplay").Checked = true;
            form.Close();
            assert(applied is null,
                "SET09 closing settings discards unapplied display changes");
        }
    }

    private static T Field<T>(object value, string name) where T : class =>
        (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
}
