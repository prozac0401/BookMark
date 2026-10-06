using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerArrangeNoteChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly = typeof(BookmarkApplicationContext).Assembly;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        // Observe both exits before reporting failures, so a failing cancel check
        // does not conceal the corresponding successful-save geometry.
        List<(bool Passed, string Name)> results = [];
        foreach (var (mode, collapsed, save) in new[]
        {
            ("Grid", false, false), ("Horizontal", true, false),
            ("Vertical", false, true), ("Grid", true, true)
        })
            CheckExit(Path.Combine(directory, $"{mode}-{collapsed}-{save}"), mode, collapsed, save,
                assert, (passed, name) => results.Add((passed, name)));
        foreach (var result in results) assert(result.Passed, result.Name);
    }

    private static void CheckExit(string directory, string mode, bool collapsed, bool save,
        Action<bool, string> assert, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "peer.txt")));
        var bookmark = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "editing.txt"))).Bookmark;
        const string originalNote = "정렬 전 저장된 메모", draft = "실패 후 정렬해도 보존할 초안";
        repository.UpdateNote(bookmark.Id, originalNote);
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = () =>
            Task.FromResult((repository.ListActive(), repository.GetStickerLayouts()));
        Action<IReadOnlyList<StickerLayout>> persist = layouts =>
        {
            foreach (var layout in layouts) repository.SaveStickerLayout(layout);
        };
        object manager = AppAssembly.GetType("WorkBookmark.App.StickerManager")!.GetConstructors(Private).Single()
            .Invoke([load, persist, (Action<string>)(message => throw new InvalidOperationException(message))]);
        using var lifetime = (IDisposable)manager;
        Pump(Call(manager, "SetEnabledAsync", true, false));
        Form form = Forms(manager).Single(value => BookmarkFor(value).Id == bookmark.Id);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.ClientSize = new Size(Px(form, 300), Px(form, 80));
        Invoke(form, "OnResizeEnd", EventArgs.Empty);
        Size expandedSize = Property<Size>(form, "ExpandedSize");
        Invoke(form, "ApplyPresentation", collapsed, true);
        form.Location = new Point(area.Left + Px(form, 60), area.Bottom - form.Height - 2);
        Invoke(form, "OnResizeEnd", EventArgs.Empty);
        Rectangle original = form.Bounds;
        int attempts = 0;
        bool fail = true;
        Func<string, Task> writeNote = text =>
        {
            attempts++;
            if (fail) return Task.FromException(new IOException("Injected arrangement note save failure"));
            repository.UpdateNote(bookmark.Id, text);
            return Task.CompletedTask;
        };
        Invoke(manager, "EditNote", repository.Get(bookmark.Id)!, writeNote);
        var editor = Field<TextBox>(form, "_noteEditor");
        editor.Text = draft;
        Rectangle editing = form.Bounds;
        string label = $"{mode}/{(collapsed ? "collapsed" : "expanded")}/{(save ? "save" : "Esc")}";
        assert(Editing(form) && editing.Top < original.Top && editing.Height > original.Height &&
            area.Contains(editing) && Placement(form).Location == original.Location,
            $"SAN01 {label} bottom-edge editing expands upward without changing normal placement");
        Pump(Call(form, "SaveNoteAsync"));
        assert(attempts == 1 && Editing(form) && !editor.ReadOnly && editor.Text == draft &&
            form.Bounds == editing && repository.Get(bookmark.Id)!.Note == originalNote &&
            Field<Label>(form, "_noteStatus").Text.Contains("저장하지 못했습니다", StringComparison.Ordinal),
            $"SAN02 {label} a failed save preserves the draft, editing bounds and stored note");

        var ordered = Forms(manager).OrderByDescending(value => BookmarkFor(value).CaptureSequence).ToArray();
        var modeType = AppAssembly.GetType("WorkBookmark.App.StickerArrangementMode")!;
        object modeValue = Enum.Parse(modeType, mode);
        var expected = (IReadOnlyList<Rectangle>)AppAssembly.GetType("WorkBookmark.App.StickerArrangement")!
            .GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [ordered.Select(value => value.Size).ToArray(), area, ordered[0].DeviceDpi, modeValue])!;
        Point destination = expected[Array.IndexOf(ordered, form)].Location;
        Pump(Call(manager, "ArrangeAsync", modeValue));
        Flush(manager);
        check(ordered.Select(value => value.Bounds).SequenceEqual(expected) && Editing(form) &&
            editor.Text == draft && !editor.ReadOnly && attempts == 1 && form.Size == editing.Size,
            $"SAN03 {label} arrangement preserves the failed draft and editing size without forcing another save");
        Rectangle placementAfterArrange = Placement(form);
        var layoutAfterArrange = repository.GetStickerLayouts().Single(value => value.BookmarkId == bookmark.Id);
        check(placementAfterArrange.Location == destination &&
            StoredPlacementMatches(layoutAfterArrange, form, destination, expandedSize, collapsed),
            $"SAN04 {label} arranged normal placement and stored coordinates use the new destination without the old clamp offset");

        if (save)
        {
            fail = false;
            Command(form, Keys.Enter);
            PumpUntil(() => !Editing(form));
        }
        else Command(form, Keys.Escape);
        Flush(manager);
        var finalLayout = repository.GetStickerLayouts().Single(value => value.BookmarkId == bookmark.Id);
        check(!Editing(form) && form.Visible && form.Location == destination && form.Size == original.Size &&
            Property<Size>(form, "ExpandedSize") == expandedSize && Property<bool>(form, "IsCollapsed") == collapsed,
            $"SAN05 {label} exiting editing retains the arranged location and restores the original size and collapse state");
        check(StoredPlacementMatches(finalLayout, form, destination, expandedSize, collapsed) && finalLayout == layoutAfterArrange,
            $"SAN06 {label} persisted placement remains the arranged destination after editing exits");
        check(attempts == (save ? 2 : 1) && repository.Get(bookmark.Id)!.Note == (save ? draft : originalNote) &&
            BookmarkFor(form).Note == (save ? draft : originalNote) && (save || editor.Text.Length == 0),
            $"SAN07 {label} Enter saves the retained draft while Esc cancels it without writing or hiding");
        Console.WriteLine($"SAN geometry {label}: old={original.Location}, edit={editing.Location}, " +
            $"arranged={destination}, placement={placementAfterArrange.Location}, final={form.Location}, " +
            $"stored=({finalLayout.Left},{finalLayout.Top}), dpi={form.DeviceDpi}");
    }

    private static bool StoredPlacementMatches(StickerLayout layout, Form form, Point destination, Size expanded, bool collapsed)
    {
        var screen = Screen.FromPoint(destination);
        float scale = 96F / Math.Max(96, form.DeviceDpi);
        return layout.MonitorDevice == screen.DeviceName &&
            layout.Left == (int)Math.Round((destination.X - screen.WorkingArea.Left) * scale) &&
            layout.Top == (int)Math.Round((destination.Y - screen.WorkingArea.Top) * scale) &&
            layout.Width == (int)Math.Round(expanded.Width * scale) && layout.Height == (int)Math.Round(expanded.Height * scale) &&
            layout.IsCollapsed == collapsed;
    }

    private static int Px(Form form, int value) => (int)Math.Round(value * form.DeviceDpi / 96F);
    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => Property<Bookmark>(form, "Bookmark");
    private static bool Editing(Form form) => Property<bool>(form, "IsEditingNote");
    private static Rectangle Placement(Form form) => Property<Rectangle>(form, "PlacementBounds");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static Task Call(object value, string name, params object[] arguments) => (Task)Invoke(value, name, arguments)!;
    private static void Command(Form form, Keys keys)
    {
        object[] arguments = [Message.Create(form.Handle, 0x0100, IntPtr.Zero, IntPtr.Zero), keys];
        Invoke(form, "ProcessCmdKey", arguments);
    }
    private static void Flush(object manager)
    {
        PumpUntil(() => Field<Task?>(manager, "_writeTask") is null);
        Pump(Call(manager, "SavePendingAsync"));
    }
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!ready()) throw new TimeoutException("Sticker arrangement during note editing timed out.");
    }
}
