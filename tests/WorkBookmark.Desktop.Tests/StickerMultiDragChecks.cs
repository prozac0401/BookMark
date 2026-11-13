using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerMultiDragChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var first = Item(1); var second = Item(2); var peer = Item(3);
        var folder = Item(4) with { Target = new(TargetKind.BookmarkFolder, ""), FolderExpanded = true };
        var child = Item(5) with { ParentId = folder.Id };
        using var fixture = new Fixture(first, second, peer, folder, child);
        Form a = fixture.View(first.Id), b = fixture.View(second.Id), p = fixture.View(peer.Id), f = fixture.View(folder.Id), c = fixture.View(child.Id);
        var area = Screen.FromControl(a).WorkingArea;
        a.Location = new Point(area.Left + 40, area.Top + 60);
        b.Location = new Point(area.Left + 40, area.Top + 130);
        p.Location = new Point(area.Right - p.Width - 30, area.Top + 40);
        f.Location = new Point(area.Right - f.Width - 30, area.Bottom - 200);
        fixture.Flush();
        fixture.Select(a, Keys.None); fixture.Select(b, Keys.Control);
        Rectangle beforeA = a.Bounds, beforeB = b.Bounds, beforePeer = p.Bounds;
        Point pointer = new(a.Left + 100, b.Bottom + 80);
        object preview = fixture.Preview(a, pointer);
        assert(Get<bool>(preview, "Valid") && Get<bool>(preview, "IsRootMove") &&
            Get<IEnumerable>(preview, "Moves").Cast<object>().Count() == 2,
            "MD01 multiple roots preview their individual placements");
        int writes = fixture.Writes;
        assert(fixture.Commit(a, preview, pointer) && fixture.Writes == writes + 1 && fixture.LastWrite.Length == 2,
            "MD02 one group drag saves all root positions in one batch");
        assert(b.Location - (Size)a.Location == beforeB.Location - (Size)beforeA.Location && p.Bounds == beforePeer,
            "MD03 group movement preserves relative spacing and leaves unselected roots unchanged");

        beforeA = a.Bounds; beforeB = b.Bounds;
        pointer = new(a.Left + 80, b.Bottom + 80);
        preview = fixture.Preview(a, pointer);
        fixture.FailWrites = true;
        assert(!fixture.Commit(a, preview, pointer) && a.Bounds == beforeA && b.Bounds == beforeB,
            "MD04 failed batch placement leaves every selected root at its committed position");
        fixture.FailWrites = false;

        pointer = new(f.Left + f.Width / 2, f.Top + f.Height / 2);
        Call(fixture.Manager, "BringTreeToFront", folder.Id);
        preview = fixture.Preview(a, pointer);
        var request = Get<BookmarkDropRequest?>(preview, "Request");
        assert(request is { Kind: BookmarkDropKind.MoveInto } && request.TargetId == folder.Id &&
            request.SourceIds.ToHashSet().SetEquals([first.Id, second.Id]),
            "MD05 dropping a selection on a folder carries all selected identities");

        fixture.Select(c, Keys.Control);
        preview = fixture.Preview(a, new(area.Left + area.Width / 2, area.Top + area.Height / 2));
        request = Get<BookmarkDropRequest?>(preview, "Request");
        assert(request is { Kind: BookmarkDropKind.Detach, RootLayouts.Count: 3 } &&
            request.SourceIds.ToHashSet().SetEquals([first.Id, second.Id, child.Id]),
            "MD06 mixed root and child selection detaches and places the whole selection atomically");
        assert(!Get<bool>(fixture.Preview(a, pointer, Keys.Alt), "Valid"),
            "MD07 Alt cancels a selection containing embedded children");

        fixture.Select(f, Keys.None); fixture.Select(c, Keys.Control); fixture.Select(a, Keys.Control);
        preview = fixture.Preview(c, new(area.Left + area.Width / 2, area.Top + area.Height / 2));
        var moves = Get<IEnumerable>(preview, "Moves").Cast<object>().Select(move => Get<Guid>(move, "BookmarkId")).ToHashSet();
        assert(moves.SetEquals([folder.Id, first.Id]),
            "MD08 dragging a selected descendant moves its selected ancestor once");

        fixture.Select(a, Keys.None); fixture.Select(b, Keys.Control);
        pointer = new(a.Left + 40, b.Bottom + 60);
        preview = fixture.Preview(a, pointer);
        fixture.Select(b, Keys.Control);
        writes = fixture.Writes;
        assert(!fixture.Commit(a, preview, pointer) && fixture.Writes == writes,
            "MD09 a changed selection invalidates a stale preview before persistence");

        fixture.Select(f, Keys.None); fixture.Select(c, Keys.Control);
        pointer = new(area.Left + area.Width / 2, area.Top + area.Height / 2);
        preview = fixture.Preview(c, pointer);
        assert(fixture.Commit(c, preview, pointer) && fixture.LastWrite is [{ BookmarkId: var movedId }] && movedId == folder.Id,
            "MD10 a parent and child selection commits only the ancestor when dragged from its child");

        fixture.Select(a, Keys.None); fixture.Select(c, Keys.Control);
        fixture.StartSyntheticDrag(a, [first.Id, child.Id]);
        writes = fixture.Writes;
        Call(fixture.Manager, "ApplyFolderChanges", new[] { folder with { FolderExpanded = false } }, null);
        assert(ManagerType.GetField("_drag", Members)!.GetValue(fixture.Manager) is null && fixture.Writes == writes && !c.Visible,
            "MD11 hiding a selected peer cancels the frozen drag without moving the remaining selection");
        assert(!Get<bool>(fixture.Preview(c, pointer), "Valid"),
            "MD12 a hidden card cannot start or commit a detached move");
    }

    private sealed class Fixture : IDisposable
    {
        internal object Manager { get; }
        internal int Writes;
        internal StickerLayout[] LastWrite = [];
        internal bool FailWrites;
        internal Fixture(params Bookmark[] items)
        {
            Func<Task<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>> load = () => Task.FromResult(((IReadOnlyList<Bookmark>)items, (IReadOnlyList<StickerLayout>)Array.Empty<StickerLayout>()));
            Action<IReadOnlyList<StickerLayout>> save = layouts =>
            {
                if (FailWrites) throw new IOException("isolated multi-drag failure");
                LastWrite = layouts.ToArray(); Interlocked.Increment(ref Writes);
            };
            Manager = ManagerType.GetConstructors(Members).Single().Invoke([load, save, (Action<string>)(_ => { })]);
            ManagerType.GetProperty("SnapEnabled", Members)!.SetValue(Manager, false);
            Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!);
        }
        internal Form View(Guid id) => Get<IEnumerable>(Manager, "Forms").Cast<Form>().Single(form => Get<Bookmark>(form, "Bookmark").Id == id);
        internal void Select(Form form, Keys keys) => Call(Manager, "SelectBookmark", form, keys, false);
        internal void Flush() => Pump((Task)Call(Manager, "SavePendingAsync")!);
        internal void StartSyntheticDrag(Form source, Guid[] ids)
        {
            var ghostType = ManagerType.Assembly.GetType("WorkBookmark.App.UI.StickerDragPreview")!;
            var ghost = ghostType.GetConstructors(Members).Single().Invoke([]);
            var dragType = ManagerType.GetNestedType("DragSession", BindingFlags.NonPublic)!;
            var drag = dragType.GetConstructors(Members).Single().Invoke([source, source.Bounds, Size.Empty, ghost, ids]);
            ManagerType.GetField("_drag", Members)!.SetValue(Manager, drag);
        }
        internal object Preview(Form form, Point cursor, Keys keys = Keys.None) => Call(Manager, "EvaluateDrop", form, cursor, Size.Empty, keys)!;
        internal bool Commit(Form form, object preview, Point cursor)
        {
            var task = (Task<bool>)Call(Manager, "CommitDropPreviewAsync", form, preview, cursor, Size.Empty, Keys.None)!;
            Pump(task); return task.Result;
        }
        public void Dispose() => ((IDisposable)Manager).Dispose();
    }
    private static Bookmark Item(long sequence) => new(Guid.NewGuid(), new(TargetKind.File, @"C:\multi-drag\example.txt"),
        "example.txt", "선택 이동 " + sequence, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Members)!.Invoke(value, args);
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(20)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Multi-drag test timed out.");
        task.GetAwaiter().GetResult();
    }
}
