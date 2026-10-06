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
            IntroShown = true,
            DisplayMode = BookmarkDisplayMode.List,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            previous.Version, previous.CaptureHotkey, previous.RecentHotkey,
            previous.StartWithWindows, previous.IntroShown
        }));
        var legacyWithoutMode = UserSettings.Load(directory);
        assert(legacyWithoutMode == (previous with { DisplayMode = BookmarkDisplayMode.Stickers, StickerPresentationVersion = 0 }),
            "SET01 old settings without display mode retain other values and await the sticker presentation upgrade");
        assert(UserSettings.Default.DisplayMode == BookmarkDisplayMode.Stickers && UserSettings.Default.StickerPresentationVersion == 0,
            "SET10 new settings default to stickers and apply presentation once at startup");
        var legacyList = previous with { StickerPresentationVersion = 0 };
        legacyList.Save(directory);
        assert(UserSettings.Load(directory) == legacyList,
            "SET11 an explicit legacy List choice is loaded without marking the startup upgrade complete");
        int resets = 0, saves = 0;
        var upgraded = Upgrade(legacyList, () => { resets++; return Task.CompletedTask; },
            value => { saves++; value.Save(directory); return Task.CompletedTask; }).GetAwaiter().GetResult();
        assert(upgraded == (previous with { DisplayMode = BookmarkDisplayMode.Stickers }) && UserSettings.Load(directory) == upgraded && resets == 1 && saves == 1,
            "SET12 the one-time upgrade changes legacy List to Stickers while preserving hotkeys and startup preferences");
        var chosenList = upgraded with { DisplayMode = BookmarkDisplayMode.List };
        var retained = Upgrade(chosenList, () => throw new Exception("Unexpected repeated layout reset"),
            _ => throw new Exception("Unexpected repeated settings save")).GetAwaiter().GetResult();
        assert(retained == chosenList,
            "SET13 a List choice made after the upgrade is retained without another reset");

        foreach (int version in new[] { 1, 2 })
        {
            int legacyResets = 0, legacySaves = 0;
            var legacyChoice = previous with { StickerPresentationVersion = version };
            var currentChoice = Upgrade(legacyChoice, () => { legacyResets++; return Task.CompletedTask; },
                value => { legacySaves++; value.Save(directory); return Task.CompletedTask; }).GetAwaiter().GetResult();
            assert(currentChoice == previous && legacyResets == 1 && legacySaves == 1 &&
                UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.List,
                $"SET15 one-line presentation upgrades version {version} without resetting the user's List choice");
        }

        var stickers = previous with { DisplayMode = BookmarkDisplayMode.Stickers };
        stickers.Save(directory);
        assert(UserSettings.Load(directory) == stickers,
            "SET02 sticker display choice survives saving and reloading");
        assert(UserSettings.Default.StickerSnapEnabled && UserSettings.Load(directory).StickerSnapEnabled,
            "SET16 magnetic alignment defaults on and survives saving and reloading");
        var unsnappedStickers = stickers with { StickerSnapEnabled = false };
        unsnappedStickers.Save(directory);
        assert(UserSettings.Load(directory) == unsnappedStickers,
            "SET17 disabling magnetic alignment survives saving and reloading");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            previous.Version, previous.CaptureHotkey, previous.RecentHotkey,
            previous.StartWithWindows, previous.IntroShown, previous.DisplayMode,
            previous.StickerPresentationVersion
        }));
        assert(UserSettings.Load(directory) == previous && legacyWithoutMode.StickerSnapEnabled,
            "SET18 older settings without magnetic alignment enable it while preserving saved preferences");
        string invalid = JsonSerializer.Serialize(stickers with { DisplayMode = (BookmarkDisplayMode)99 });
        File.WriteAllText(path, invalid);
        bool rejected = false;
        try { UserSettings.Load(directory); }
        catch (InvalidDataException) { rejected = true; }
        assert(rejected && File.ReadAllText(path) == invalid,
            "SET03 unknown display mode is rejected without rewriting settings");

        string invalidVersion = JsonSerializer.Serialize(stickers with { StickerPresentationVersion = -1 });
        File.WriteAllText(path, invalidVersion);
        rejected = false;
        try { UserSettings.Load(directory); }
        catch (InvalidDataException) { rejected = true; }
        assert(rejected && File.ReadAllText(path) == invalidVersion,
            "SET14 invalid presentation version is rejected without rewriting the original settings");

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
            var snap = Field<CheckBox>(form, "_stickerSnap");
            var save = Field<Button>(form, "_apply");
            assert(list.Checked && !sticker.Checked && snap.Checked && !snap.Enabled,
                "SET04 settings shows the saved List choice and retains its inactive magnetic alignment preference");
            sticker.Checked = true;
            assert(sticker.Checked && !list.Checked && snap.Enabled,
                "SET05 display choices remain mutually exclusive and sticker mode enables magnetic alignment");
            snap.Checked = false;
            list.Checked = true;
            sticker.Checked = true;
            assert(!snap.Checked && snap.Enabled,
                "SET19 switching display modes retains the selected magnetic alignment preference");
            save.PerformClick();
            Application.DoEvents();
            assert(!form.IsDisposed && save.Enabled && sticker.Checked && !snap.Checked &&
                Field<Label>(form, "_status").Text == "테스트: 설정 저장 실패" &&
                applied == unsnappedStickers,
                "SET06 failed apply retains selected display and magnetic alignment preferences for retry");
            fail = false;
            save.PerformClick();
            Application.DoEvents();
            assert(form.IsDisposed && applied == unsnappedStickers,
                "SET07 successful apply closes settings and includes display and magnetic alignment preferences");
        }

        applied = null;
        using (var form = (Form)Activator.CreateInstance(formType, unsnappedStickers, true, true,
            directory, apply, (Action)(() => { }), (Action)(() => { }))!)
        {
            form.Show();
            Application.DoEvents();
            assert(Field<RadioButton>(form, "_stickerDisplay").Checked &&
                !Field<CheckBox>(form, "_stickerSnap").Checked,
                "SET08 settings shows saved Stickers and disabled magnetic alignment choices");
            Field<CheckBox>(form, "_stickerSnap").Checked = true;
            Field<RadioButton>(form, "_listDisplay").Checked = true;
            form.Close();
            assert(applied is null,
                "SET09 closing settings discards unapplied display and magnetic alignment changes");
        }
    }

    private static Task<UserSettings> Upgrade(UserSettings settings, Func<Task> reset, Func<UserSettings, Task> save) =>
        (Task<UserSettings>)typeof(UserSettings).Assembly.GetType("WorkBookmark.App.StickerPresentationUpgrade")!
            .GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [settings, reset, save])!;

    private static T Field<T>(object value, string name) where T : class =>
        (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
}
