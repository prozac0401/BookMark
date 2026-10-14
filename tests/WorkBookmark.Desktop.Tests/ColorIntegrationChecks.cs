using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class ColorIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
            IntroShown = true, DisplayMode = BookmarkDisplayMode.List,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        }).Save(directory);
        bool reject = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), operation =>
        {
            if (operation == "color" && reject) throw new IOException("Injected color commit failure");
        });
        var parent = repository.CreateFolder("부모 폴더");
        repository.SetFolderExpanded(parent.Id, true);
        var child = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "자식.txt"))).Bookmark;
        repository.SetParent(child.Id, parent.Id);
        repository.UpdateNote(child.Id, "색상 변경 전 메모");
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        Application.DoEvents();
        object manager = Field<object>(context, "_stickers");
        var recent = Field<Form>(context, "_recent");
        Pump((Task)Invoke(manager, "SetEnabledAsync", true, false)!);
        Pump((Task)recent.GetType().GetMethod("OpenAsync")!.Invoke(recent, null)!);
        var items = Field<ListBox>(recent, "_items");
        Form Sticker() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!)
            .Cast<Form>().Single(form => Current(form).Id == child.Id);
        var before = repository.Get(child.Id)!;
        var bounds = Sticker().Bounds;
        Field<Action<Bookmark, BookmarkColor>>(recent, "ColorChangeRequested")(before, BookmarkColor.Blue);
        PumpUntil(() => repository.Get(child.Id)!.Color == BookmarkColor.Blue && Current(Sticker()).Color == BookmarkColor.Blue && Listed().Color == BookmarkColor.Blue);
        assert(repository.Get(child.Id) == before with { Color = BookmarkColor.Blue } && Sticker().Bounds == bounds,
            "CI01 list color commits to both views without changing hierarchy, notes, history or geometry");
        assert(repository.Get(parent.Id)!.Color == BookmarkColor.Default,
            "CI02 a child color does not overwrite its parent's independent color");

        Field<Action<Bookmark, BookmarkColor>>(Sticker(), "ColorChangeRequested")(repository.Get(child.Id)!, BookmarkColor.Pink);
        PumpUntil(() => repository.Get(child.Id)!.Color == BookmarkColor.Pink && Listed().Color == BookmarkColor.Pink && Current(Sticker()).Color == BookmarkColor.Pink);
        assert(repository.Get(child.Id)!.ParentId == parent.Id, "CI03 sticker color events use the same authoritative application route");

        reject = true;
        Pump((Task)Invoke(context, "ChangeColorAsync", repository.Get(child.Id)!, BookmarkColor.Green)!);
        reject = false;
        assert(repository.Get(child.Id)!.Color == BookmarkColor.Pink && Listed().Color == BookmarkColor.Pink && Current(Sticker()).Color == BookmarkColor.Pink,
            "CI04 a rejected color write keeps the committed color in storage and both views");
        assert(Field<Label>(recent, "_status").Text.Contains("색상을 저장하지 못했습니다", StringComparison.Ordinal),
            "CI05 color failure leaves a retry message");
        Invoke(manager, "HideAll");
        Pump((Task)Invoke(context, "ChangeColorAsync", repository.Get(child.Id)!, BookmarkColor.Default)!);
        assert(repository.Get(child.Id)!.Color == BookmarkColor.Default && Current(Sticker()).Color == BookmarkColor.Default && !Sticker().Visible,
            "CI06 restoring default color does not reveal hidden stickers");
        Bookmark Listed() => items.Items.Cast<Bookmark>().Single(value => value.Id == child.Id);
    }
    private static Bookmark Current(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!done()) throw new TimeoutException("Color integration did not complete.");
    }
}
