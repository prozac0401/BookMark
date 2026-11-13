using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class BookmarkSelectionChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-selection-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("SEL01 Selection roots preserve tree order and keep selected descendants with their parent", SelectionRoots);
            Run("SEL02 Moving a selection across folders is one command and one exact undo", MoveAndUndo);
            Run("SEL03 Selection insertion uses source-removed gaps and preserves no-op history", Insertion);
            Run("SEL04 Grouping a selection creates one folder at the target slot", Grouping);
            Run("SEL05 Detach stores all root placements atomically and persists across restart", Detach);
            Run("SEL06 Moving existing roots changes only placements and undoes in one step", RootPlacement);
            Run("SEL07 Invalid, stale and descendant destinations reject the complete selection", InvalidSelection);
            Run("SEL08 Commit failure rolls back all selected memberships, positions and undo history", AtomicFailure);
            Run("SEL09 Independent selections from different folders move together without removing source folders", MultipleParents);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"BOOKMARK SELECTION RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;

        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        void Run(string title, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + title); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + title + ": " + error); }
        }

        void SelectionRoots()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var parent = repo.CreateFolder("parent");
            var child = repo.CreateFolder("child", parent.Id);
            var grandchild = repo.UpsertCapture(Target("grandchild")).Bookmark;
            repo.SetParent(grandchild.Id, child.Id);
            var peer = repo.UpsertCapture(Target("peer")).Bookmark;
            repo.SetParent(peer.Id, parent.Id);
            var selected = new[] { peer.Id, grandchild.Id, child.Id };
            Check(BookmarkTree.SelectionRoots(repo.ListActive(), selected).SequenceEqual(new[] { child.Id, peer.Id }), "Selected child folder includes its grandchild exactly once, before its later sibling.");
            Check(BookmarkTree.SelectionRoots(repo.ListActive(), selected.Append(parent.Id)).SequenceEqual(new[] { parent.Id }), "Selected ancestor collapses all selected descendants into one move.");
        }

        void MoveAndUndo()
        {
            string path = Db();
            Bookmark source, folder, child, destination, existing;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                source = setup.CreateFolder("source");
                folder = setup.CreateFolder("selected folder", source.Id);
                child = setup.UpsertCapture(Target("child")).Bookmark;
                setup.SetParent(child.Id, folder.Id);
                existing = setup.UpsertCapture(Target("existing")).Bookmark;
                destination = setup.CreateFolder("destination");
                setup.SetParent(existing.Id, destination.Id);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = repo.ListActive().ToArray();
            repo.ApplyDrop(new(child.Id, BookmarkDropKind.MoveInto, destination.Id, BookmarkIds: [folder.Id, source.Id]));
            Check(Children(repo, destination.Id).SequenceEqual(new[] { existing.Id, source.Id }), "Only the topmost selected root is appended to the destination.");
            Check(repo.Get(folder.Id)!.ParentId == source.Id && repo.Get(child.Id)!.ParentId == folder.Id, "Internal parent relationships survive selection normalization.");
            Check(original.All(item => repo.Get(item.Id)!.CaptureSequence == item.CaptureSequence), "Selection movement preserves capture metadata.");
            repo.Undo();
            Check(repo.ListActive().SequenceEqual(original) && !repo.CanUndo, "One undo restores the complete selected tree and destination order.");
        }

        void Insertion()
        {
            string path = Db();
            Guid folder;
            Bookmark[] items;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                folder = setup.CreateFolder("ordered").Id;
                items = new[] { "a", "b", "c", "d", "e" }.Select(name => setup.UpsertCapture(Target(name)).Bookmark).ToArray();
                foreach (var item in items) setup.SetParent(item.Id, folder);
            }
            using var repo = new SqliteBookmarkRepository(path);
            repo.ApplyDrop(new(items[2].Id, BookmarkDropKind.InsertBefore, folder, items[1].Id, BookmarkIds: [items[1].Id]));
            Check(Children(repo, folder).SequenceEqual(items.Select(item => item.Id)) && !repo.CanUndo, "A contiguous selection dropped at its current gap adds no undo entry.");
            repo.ApplyDrop(new(items[3].Id, BookmarkDropKind.InsertBefore, folder, items[0].Id, BookmarkIds: [items[1].Id]));
            Check(Children(repo, folder).SequenceEqual(new[] { items[1].Id, items[3].Id, items[0].Id, items[2].Id, items[4].Id }), "Noncontiguous source rows move as one block in tree order, independent of click order.");
            repo.Undo();
            Check(Children(repo, folder).SequenceEqual(items.Select(item => item.Id)) && !repo.CanUndo, "Reorder undo restores original positions with no extra entry.");
            repo.ApplyDrop(new(items[1].Id, BookmarkDropKind.MoveInto, folder, BookmarkIds: [items[3].Id]));
            Check(Children(repo, folder).SequenceEqual(new[] { items[0].Id, items[2].Id, items[4].Id, items[1].Id, items[3].Id }), "Folder-center drop appends the entire ordered selection.");
        }

        void Grouping()
        {
            string path = Db();
            Guid parent;
            Bookmark[] items;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                parent = setup.CreateFolder("parent").Id;
                items = new[] { "a", "b", "target", "d", "e" }.Select(name => setup.UpsertCapture(Target(name)).Bookmark).ToArray();
                foreach (var item in items) setup.SetParent(item.Id, parent);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = repo.ListActive().ToArray();
            var commit = repo.ApplyDrop(new(items[3].Id, BookmarkDropKind.Group, items[2].Id, BookmarkIds: [items[0].Id]));
            var group = commit.CreatedFolder!;
            Check(group.FolderExpanded && group.ParentId == parent, "Exactly one opened group is created under the target parent.");
            Check(Children(repo, parent).SequenceEqual(new[] { items[1].Id, group.Id, items[4].Id }), "The new group replaces the target after selected rows are removed.");
            Check(Children(repo, group.Id).SequenceEqual(new[] { items[2].Id, items[0].Id, items[3].Id }), "Group contents are target first, then selection in tree order.");
            repo.Undo();
            Check(repo.Get(group.Id) is null && repo.ListActive().SequenceEqual(original) && !repo.CanUndo, "One undo removes the generated group and restores all sources.");
        }

        void Detach()
        {
            string path = Db();
            Bookmark a, b;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                var parent = setup.CreateFolder("parent");
                a = setup.UpsertCapture(Target("a")).Bookmark;
                b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SetParent(a.Id, parent.Id); setup.SetParent(b.Id, parent.Id);
            }
            Bookmark[] original;
            var positions = new[] { Layout(a.Id, 110), Layout(b.Id, 420) };
            using (var repo = new SqliteBookmarkRepository(path))
            {
                original = repo.ListActive().ToArray();
                repo.ApplyDrop(new(b.Id, BookmarkDropKind.Detach, BookmarkIds: [a.Id], RootLayouts: positions));
                Check(repo.Get(a.Id)!.ParentId is null && repo.Get(b.Id)!.ParentId is null, "Both sources become roots together.");
                Check(positions.All(layout => repo.GetStickerLayouts().Contains(layout)), "All detached anchors are saved in the membership transaction.");
                repo.Undo();
                Check(repo.ListActive().SequenceEqual(original) && repo.GetStickerLayouts().Count == 0 && !repo.CanUndo, "One undo restores membership and removes both newly created layouts.");
                repo.ApplyDrop(new(b.Id, BookmarkDropKind.Detach, BookmarkIds: [a.Id], RootLayouts: positions));
            }
            using var reopened = new SqliteBookmarkRepository(path);
            Check(reopened.Get(a.Id)!.ParentId is null && reopened.Get(b.Id)!.ParentId is null && positions.All(layout => reopened.GetStickerLayouts().Contains(layout)), "The complete move and placements survive reopening the repository.");
        }

        void RootPlacement()
        {
            string path = Db();
            Bookmark a, b;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                a = setup.UpsertCapture(Target("a")).Bookmark;
                b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SaveStickerLayout(Layout(a.Id, 100)); setup.SaveStickerLayout(Layout(b.Id, 400));
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = repo.ListActive().ToArray(); var layouts = repo.GetStickerLayouts().ToArray();
            repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [b.Id]));
            Check(!repo.CanUndo && repo.ListActive().SequenceEqual(original), "Repeated detach of existing roots preserves root order and history.");
            var commit = repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [b.Id], RootLayouts: layouts.Select(layout => layout with { Left = layout.Left + 50 }).ToArray()));
            Check(commit.ChangedItems.Count == 0 && repo.ListActive().SequenceEqual(original), "Desktop placement alone changes no bookmark rows.");
            Check(repo.GetStickerLayouts().All(layout => layout.Left == layouts.Single(old => old.BookmarkId == layout.BookmarkId).Left + 50), "All selected roots preserve their relative horizontal distance.");
            repo.Undo();
            Check(repo.GetStickerLayouts().SequenceEqual(layouts) && !repo.CanUndo, "One undo restores every root position.");
        }

        void InvalidSelection()
        {
            string path = Db();
            Bookmark parent, nested, a, b;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                parent = setup.CreateFolder("parent"); nested = setup.CreateFolder("nested", parent.Id);
                a = setup.UpsertCapture(Target("a")).Bookmark; b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SetParent(a.Id, nested.Id); setup.SoftDelete(b.Id);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = repo.ListActive().ToArray();
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.MoveInto, parent.Id, BookmarkIds: [b.Id])), ResultCode.TargetUnavailable);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [Guid.NewGuid()])), ResultCode.TargetUnavailable);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.MoveInto, nested.Id, BookmarkIds: [parent.Id])), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(nested.Id, BookmarkDropKind.Group, a.Id, BookmarkIds: [parent.Id])), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [parent.Id], RootLayouts: [Layout(a.Id, 100)])), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [nested.Id], RootLayouts: [Layout(nested.Id, 100), Layout(nested.Id, 200)])), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [parent.Id], RootLayouts: [null!])), ResultCode.InvalidRequest);
            Check(repo.ListActive().SequenceEqual(original) && repo.GetStickerLayouts().Count == 0 && !repo.CanUndo, "Every rejected selection preserves complete state and history.");
        }

        void AtomicFailure()
        {
            string path = Db();
            Bookmark a, b, destination;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                var parent = setup.CreateFolder("parent"); destination = setup.CreateFolder("destination");
                a = setup.UpsertCapture(Target("a")).Bookmark; b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SetParent(a.Id, parent.Id); setup.SetParent(b.Id, parent.Id);
            }
            bool fail = true;
            using var repo = new SqliteBookmarkRepository(path, operation => { if (fail && operation == "folder-drop") throw new IOException(); });
            var original = repo.ListActive().ToArray();
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.MoveInto, destination.Id, BookmarkIds: [b.Id])), ResultCode.PersistenceFailed);
            Fails(() => repo.ApplyDrop(new(a.Id, BookmarkDropKind.Detach, BookmarkIds: [b.Id], RootLayouts: [Layout(a.Id, 100), Layout(b.Id, 500)])), ResultCode.PersistenceFailed);
            Check(repo.ListActive().SequenceEqual(original) && repo.GetStickerLayouts().Count == 0 && !repo.CanUndo, "Failure after all writes rolls back every membership and position without history.");
            fail = false;
            repo.ApplyDrop(new(a.Id, BookmarkDropKind.MoveInto, destination.Id, BookmarkIds: [b.Id]));
            repo.Undo();
            Check(repo.ListActive().SequenceEqual(original) && !repo.CanUndo, "Retry creates exactly one reversible command.");
        }

        void MultipleParents()
        {
            string path = Db();
            Bookmark left, right, a, b, destination;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                left = setup.CreateFolder("left"); right = setup.CreateFolder("right");
                destination = setup.CreateFolder("destination");
                a = setup.UpsertCapture(Target("a")).Bookmark; b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SetParent(a.Id, left.Id); setup.SetParent(b.Id, right.Id);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = repo.ListActive().ToArray();
            repo.ApplyDrop(new(a.Id, BookmarkDropKind.MoveInto, destination.Id, BookmarkIds: [b.Id]));
            Check(Children(repo, destination.Id).SequenceEqual(new[] { b.Id, a.Id }), "Selections retain the displayed order of their different source trees.");
            Check(repo.Get(left.Id) is { DeletedAtUtc: null } && repo.Get(right.Id) is { DeletedAtUtc: null } &&
                Children(repo, left.Id).Length == 0 && Children(repo, right.Id).Length == 0, "Empty source folders are retained.");
            repo.Undo();
            Check(repo.ListActive().SequenceEqual(original) && !repo.CanUndo, "One undo restores both independent parent memberships.");
        }
    }

    private static Guid[] Children(IBookmarkRepository repo, Guid parent) => BookmarkTree.Flatten(repo.ListActive())
        .Where(row => row.Bookmark.ParentId == parent).Select(row => row.Bookmark.Id).ToArray();
    private static CapturedTarget Target(string name) => new(TargetKind.File, @"C:\synthetic\selection\" + name + ".txt");
    private static StickerLayout Layout(Guid id, int left) => new(id, "test-monitor", left, 200, 260, 36);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code)
    {
        try { action(); } catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
