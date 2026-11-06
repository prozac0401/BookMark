using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class BookmarkCommandChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("CMD01 Version 8 migration backs up and preserves sibling projection, raw layouts and capture history", Migration);
            Run("CMD02 Failed version 9 migration preserves the source and permits retry", MigrationFailure);
            Run("CMD03 Reorder, append and detach preserve history and use source-removed insertion positions", Ordering);
            Run("CMD04 Group replaces its target slot, opens, and preserves empty source folders", Grouping);
            Run("CMD05 Session undo unwinds content and grouping while preserving view/resume changes", UndoSequence);
            Run("CMD06 Layout batches and all completed content commands undo atomically", UndoCommands);
            Run("CMD07 Failed drop, batch and undo do not create partial state or lose retry history", AtomicFailure);
            Run("CMD08 Undo preserves untouched newer fields and rejects conflicting new identities", UndoConflict);
            Run("CMD09 No-op and invalid commands leave both state and undo history unchanged", NoOp);
            Run("CMD10 Visible orphan roots accept grouping and repeated detach does not change root order", OrphanRoots);
            Run("CMD11 Explicit name/recent sort is one reversible command and recapture preserves manual order", Sorting);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"BOOKMARK COMMAND RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;

        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        void Run(string title, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + title); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + title + ": " + error); }
        }

        void Migration()
        {
            string path = Db();
            Bookmark[] original;
            StickerLayout[] layouts;
            using (var repo = new SqliteBookmarkRepository(path))
            {
                var folder = repo.CreateFolder("folder");
                var a = repo.UpsertCapture(Target("a")).Bookmark;
                var b = repo.UpsertCapture(Target("b")).Bookmark;
                repo.SetParent(a.Id, folder.Id);
                repo.SetParent(b.Id, folder.Id);
                repo.SaveStickerLayout(Layout(a.Id) with { ParentId = folder.Id, Left = -235, Top = 834 });
                repo.SaveStickerLayout(Layout(folder.Id));
                repo.SetFolderExpanded(folder.Id, true);
                original = repo.ListActive().Select(item => item with { SiblingOrder = null }).ToArray();
                layouts = repo.GetStickerLayouts().ToArray();
            }
            Downgrade(path);
            using (var db = Open(path)) Execute(db, "CREATE TABLE retained_extension(value TEXT); INSERT INTO retained_extension VALUES('untouched');");
            using var migrated = new SqliteBookmarkRepository(path);
            Check(BookmarkTree.Flatten(migrated.ListActive()).Select(row => row.Bookmark.Id).SequenceEqual(BookmarkTree.Flatten(original).Select(row => row.Bookmark.Id)), "Migration retains the exact prior sibling projection.");
            Check(migrated.ListActive().SequenceEqual(original.Select(item => item with { SiblingOrder = -item.CaptureSequence })), "Only sibling order is added to bookmarks.");
            Check(migrated.GetStickerLayouts().SequenceEqual(layouts), "Old custom child coordinates remain intact as legacy layout data.");
            using (var db = Open(path)) Check(Scalar(db, "PRAGMA user_version") == 9 && Scalar(db, "SELECT count(*) FROM retained_extension") == 1, "Version and unrelated content.");
            string backup = Directory.GetFiles(root, Path.GetFileName(path) + ".pre-migration-*.bak").Single();
            using var old = Open(backup);
            Check(Scalar(old, "PRAGMA user_version") == 8 && Scalar(old, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='sibling_order'") == 0, "The backup is the exact v8 schema.");
            Check(!migrated.CanUndo, "Migration never populates session history.");
        }

        void MigrationFailure()
        {
            string path = Db();
            using (var repo = new SqliteBookmarkRepository(path)) repo.UpsertCapture(Target("migration"));
            Downgrade(path);
            Fails(() => { using var ignored = new SqliteBookmarkRepository(path, operation => { if (operation == "migration") throw new IOException(); }); }, ResultCode.PersistenceFailed);
            using (var db = Open(path)) Check(Scalar(db, "PRAGMA user_version") == 8 && Scalar(db, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='sibling_order'") == 0, "Failure rolls back the additive migration.");
            using var retry = new SqliteBookmarkRepository(path);
            Check(retry.ListActive().Count == 1, "Retry preserves the bookmark.");
        }

        void Ordering()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var folder = repo.CreateFolder("folder");
            var items = new[] { "a", "b", "c" }.Select(name => repo.UpsertCapture(Target(name)).Bookmark).ToArray();
            foreach (var item in items) repo.ApplyDrop(new(item.Id, BookmarkDropKind.MoveInto, folder.Id));
            Check(Children(repo, folder.Id).SequenceEqual(items.Select(item => item.Id)), "Folder-center append follows move order.");
            repo.ApplyDrop(new(items[2].Id, BookmarkDropKind.InsertBefore, folder.Id, items[0].Id));
            Check(Children(repo, folder.Id).SequenceEqual(new[] { items[2].Id, items[0].Id, items[1].Id }), "Insert before uses the source-removed sequence.");
            repo.ApplyDrop(new(items[2].Id, BookmarkDropKind.MoveInto, folder.Id));
            Check(Children(repo, folder.Id).SequenceEqual(items.Select(item => item.Id)), "Dropping into current parent appends.");
            repo.ApplyDrop(new(items[1].Id, BookmarkDropKind.Detach, RootLayout: Layout(items[1].Id) with { Left = 440 }));
            Check(repo.Get(items[1].Id)!.ParentId is null && repo.GetStickerLayouts().Single().Left == 440, "Detach atomically stores the new root anchor.");
            Check(items.All(item => repo.Get(item.Id)!.CaptureSequence == item.CaptureSequence), "Manual order never changes capture history.");
        }

        void Grouping()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var parent = repo.CreateFolder("parent");
            var items = new[] { "a", "b", "c", "d" }.Select(name => repo.UpsertCapture(Target(name)).Bookmark).ToArray();
            foreach (var item in items) repo.ApplyDrop(new(item.Id, BookmarkDropKind.MoveInto, parent.Id));
            var commit = repo.ApplyDrop(new(items[0].Id, BookmarkDropKind.Group, items[2].Id));
            var group = commit.CreatedFolder!;
            Check(group.FolderExpanded && group.ParentId == parent.Id, "A new group opens in its target's parent.");
            Check(Children(repo, parent.Id).SequenceEqual(new[] { items[1].Id, group.Id, items[3].Id }), "Target slot is inherited after removing source.");
            Check(Children(repo, group.Id).SequenceEqual(new[] { items[2].Id, items[0].Id }), "Group content is target then source.");
            repo.ApplyDrop(new(items[1].Id, BookmarkDropKind.Detach));
            repo.ApplyDrop(new(items[3].Id, BookmarkDropKind.Detach));
            repo.ApplyDrop(new(group.Id, BookmarkDropKind.Detach));
            Check(repo.Get(parent.Id) is { DeletedAtUtc: null } && Children(repo, parent.Id).Length == 0, "Empty source folders remain.");
        }

        void UndoSequence()
        {
            string path = Db();
            Bookmark a, b;
            StickerLayout[] originalLayouts;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                a = setup.UpsertCapture(Target("a")).Bookmark;
                b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SaveStickerLayout(Layout(a.Id)); setup.SaveStickerLayout(Layout(b.Id) with { Left = 600 });
                originalLayouts = setup.GetStickerLayouts().ToArray();
            }
            using var repo = new SqliteBookmarkRepository(path);
            var group = repo.ApplyDrop(new(b.Id, BookmarkDropKind.Group, a.Id, RootLayout: Layout(a.Id) with { Left = 350 })).CreatedFolder!;
            repo.SetFolderExpanded(group.Id, false);
            repo.RecordResume(a.Id, ResultCode.OpenRequested);
            repo.RenameFolder(group.Id, "renamed");
            repo.UpdateNote(a.Id, "new note");
            repo.SetColor(group.Id, BookmarkColor.Pink);
            repo.Undo(); Check(repo.Get(group.Id)!.Color == BookmarkColor.Default, "Color is the most recent command.");
            repo.Undo(); Check(repo.Get(a.Id)!.Note == "", "Saved note undo follows color.");
            repo.Undo(); Check(repo.Get(group.Id)!.DisplayName == "새 폴더" && !repo.Get(group.Id)!.FolderExpanded, "Rename undo preserves later view state.");
            var undo = repo.Undo();
            Check(undo.ChangedIds.Contains(group.Id) && repo.Get(group.Id) is null, "Group undo reports and removes the generated identity.");
            Check(repo.Get(a.Id) == a with { LastResumeAtUtc = repo.Get(a.Id)!.LastResumeAtUtc, LastResumeResult = ResultCode.OpenRequested } && repo.Get(b.Id) == b, "Original memberships/order and newer automatic resume metadata survive.");
            Check(repo.GetStickerLayouts().SequenceEqual(originalLayouts) && !repo.CanUndo, "Original root anchors restored with one group undo.");
        }

        void UndoCommands()
        {
            string path = Db();
            Bookmark item;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                item = setup.UpsertCapture(Target("layout")).Bookmark;
                setup.SaveStickerLayout(Layout(item.Id));
            }
            using (var repo = new SqliteBookmarkRepository(path))
            {
                repo.SaveStickerLayouts([Layout(item.Id) with { Left = 400, Width = 450 }]);
                repo.SaveStickerLayout(Layout(item.Id) with { Left = 400, Width = 450, IsCollapsed = true });
                repo.Undo();
                Check(repo.GetStickerLayouts().Single() == Layout(item.Id) with { IsCollapsed = true }, "Batch undo preserves view-only changes.");
                repo.SoftDelete(item.Id); repo.Restore(item.Id); repo.Undo();
                Check(repo.Get(item.Id)!.DeletedAtUtc is not null, "Explicit restore is undoable.");
                repo.Undo(); Check(repo.Get(item.Id)!.DeletedAtUtc is null, "Delete is undoable.");
                var before = repo.Get(item.Id)!;
                repo.UpsertCapture(item.Target); repo.Undo();
                Check(repo.Get(item.Id) == before, "Recapture restores previous capture fields.");
                var added = repo.UpsertCapture(Target("new")).Bookmark;
                repo.SaveStickerLayout(Layout(added.Id));
                repo.Undo(); Check(repo.Get(added.Id) is null && repo.GetStickerLayouts().All(layout => layout.BookmarkId != added.Id), "Capture undo removes its automatic layout too.");
                Check(!repo.CanUndo, "One undo entry per user command.");
                repo.UpdateNote(item.Id, "persistent");
            }
            using var reopened = new SqliteBookmarkRepository(path);
            Check(!reopened.CanUndo && reopened.Get(item.Id)!.Note == "persistent", "History is session-only; committed data persists.");
        }

        void AtomicFailure()
        {
            string path = Db();
            Bookmark a, b;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                a = setup.UpsertCapture(Target("a")).Bookmark; b = setup.UpsertCapture(Target("b")).Bookmark;
                setup.SaveStickerLayout(Layout(a.Id)); setup.SaveStickerLayout(Layout(b.Id));
            }
            string? failing = "folder-drop";
            using var repo = new SqliteBookmarkRepository(path, operation => { if (operation == failing) throw new IOException(); });
            var original = repo.ListActive().ToArray(); var layouts = repo.GetStickerLayouts().ToArray();
            Fails(() => repo.ApplyDrop(new(b.Id, BookmarkDropKind.Group, a.Id, RootLayout: Layout(a.Id) with { Left = 800 })), ResultCode.PersistenceFailed);
            Check(repo.ListActive().SequenceEqual(original) && repo.GetStickerLayouts().SequenceEqual(layouts) && !repo.CanUndo, "Failed group rolls back structure/order/layout/history.");
            failing = "sticker-layouts";
            Fails(() => repo.SaveStickerLayouts(layouts.Select(layout => layout with { Left = 900 }).ToArray()), ResultCode.PersistenceFailed);
            Check(repo.GetStickerLayouts().SequenceEqual(layouts) && !repo.CanUndo, "Failed batch rolls back all members.");
            failing = null; repo.UpdateNote(a.Id, "saved"); failing = "undo";
            Fails(() => repo.Undo(), ResultCode.PersistenceFailed);
            Check(repo.Get(a.Id)!.Note == "saved" && repo.CanUndo, "Failed Undo retains both state and retry history.");
            failing = null; repo.Undo(); Check(repo.Get(a.Id)!.Note == "" && !repo.CanUndo, "Retry undoes exactly once.");
        }

        void UndoConflict()
        {
            string path = Db();
            Bookmark a, b;
            using (var setup = new SqliteBookmarkRepository(path))
            { a = setup.UpsertCapture(Target("a")).Bookmark; b = setup.UpsertCapture(Target("b")).Bookmark; }
            using var repo = new SqliteBookmarkRepository(path);
            repo.ApplyDrop(new(b.Id, BookmarkDropKind.Group, a.Id));
            using (var external = new SqliteBookmarkRepository(path)) external.UpdateNote(a.Id, "newer unrelated note");
            repo.Undo();
            Check(repo.Get(a.Id)!.Note == "newer unrelated note" && repo.Get(a.Id)!.ParentId is null, "Inverse patches do not overwrite untouched newer fields.");
            var group = repo.ApplyDrop(new(b.Id, BookmarkDropKind.Group, a.Id)).CreatedFolder!;
            using (var external = new SqliteBookmarkRepository(path)) external.RenameFolder(group.Id, "external change");
            Fails(() => repo.Undo(), ResultCode.InvalidRequest);
            Check(!repo.CanUndo && repo.Get(group.Id)!.DisplayName == "external change" && repo.Get(a.Id)!.ParentId == group.Id, "Conflicting identity mutation prevents deletion and invalidates session history.");
        }

        void NoOp()
        {
            string path = Db();
            Guid folderId, childId;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                folderId = setup.CreateFolder("parent").Id;
                childId = setup.UpsertCapture(Target("child")).Bookmark.Id;
                setup.SetParent(childId, folderId);
            }
            using var repo = new SqliteBookmarkRepository(path);
            repo.ApplyDrop(new(childId, BookmarkDropKind.MoveInto, folderId));
            repo.ApplyDrop(new(childId, BookmarkDropKind.InsertBefore, folderId, childId));
            repo.UpdateNote(childId, ""); repo.RenameFolder(folderId, "parent"); repo.SetColor(childId, BookmarkColor.Default);
            repo.Restore(childId); repo.SaveStickerLayouts([]);
            Check(!repo.CanUndo, "No-op commands do not hide the previous undo target.");
            var before = repo.ListActive().ToArray();
            Fails(() => repo.ApplyDrop(new(folderId, BookmarkDropKind.MoveInto, folderId)), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(childId, BookmarkDropKind.Group, childId)), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(childId, BookmarkDropKind.InsertBefore, folderId, Guid.NewGuid())), ResultCode.InvalidRequest);
            Fails(() => repo.ApplyDrop(new(childId, BookmarkDropKind.MoveInto, folderId, RootLayout: Layout(childId))), ResultCode.InvalidRequest);
            Check(repo.ListActive().SequenceEqual(before) && !repo.CanUndo, "Invalid commands change neither state nor history.");
        }

        void OrphanRoots()
        {
            string path = Db();
            Guid childId, sourceId;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                var parent = setup.CreateFolder("deleted parent");
                childId = setup.UpsertCapture(Target("orphan")).Bookmark.Id;
                sourceId = setup.UpsertCapture(Target("source")).Bookmark.Id;
                setup.SetParent(childId, parent.Id);
                setup.SoftDelete(parent.Id);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var rootsBefore = repo.ListActive().ToArray();
            repo.ApplyDrop(new(sourceId, BookmarkDropKind.Detach));
            Check(!repo.CanUndo && repo.ListActive().SequenceEqual(rootsBefore), "An existing root detach is a true no-op.");
            var group = repo.ApplyDrop(new(sourceId, BookmarkDropKind.Group, childId, RootLayout: Layout(childId))).CreatedFolder!;
            Check(group.ParentId is null && Children(repo, group.Id).SequenceEqual(new[] { childId, sourceId }), "Children of unavailable parents group at their visible root level.");
            repo.Undo();
            Check(repo.ListActive().SequenceEqual(rootsBefore), "Undo restores retained deleted-parent links exactly.");
        }

        void Sorting()
        {
            string path = Db();
            Guid parentId;
            Bookmark[] items;
            using (var setup = new SqliteBookmarkRepository(path))
            {
                parentId = setup.CreateFolder("sort parent").Id;
                items = new[] { "charlie", "alpha", "bravo" }.Select(name => setup.UpsertCapture(Target(name)).Bookmark).ToArray();
                foreach (var item in items) setup.SetParent(item.Id, parentId);
            }
            using var repo = new SqliteBookmarkRepository(path);
            var original = Children(repo, parentId);
            repo.SortChildren(parentId, BookmarkSortMode.Name);
            Check(Children(repo, parentId).SequenceEqual(new[] { items[1].Id, items[2].Id, items[0].Id }), "Name sort uses the saved bookmark names.");
            repo.Undo();
            Check(Children(repo, parentId).SequenceEqual(original) && !repo.CanUndo, "One Undo restores the exact previous order.");
            repo.UpsertCapture(items[0].Target);
            Check(Children(repo, parentId).SequenceEqual(original), "Recapture never silently replaces manual folder order.");
            repo.SortChildren(parentId, BookmarkSortMode.Recent);
            Check(Children(repo, parentId).SequenceEqual(new[] { items[0].Id, items[2].Id, items[1].Id }), "Recent sort explicitly uses capture sequence.");
            long recaptured = repo.Get(items[0].Id)!.CaptureSequence;
            repo.Undo();
            Check(Children(repo, parentId).SequenceEqual(original) && repo.Get(items[0].Id)!.CaptureSequence == recaptured, "Sort undo changes order only.");
            repo.Undo();
            Check(!repo.CanUndo && repo.Get(items[0].Id)!.CaptureSequence == items[0].CaptureSequence, "The earlier recapture remains its own command.");
            Fails(() => repo.SortChildren(items[0].Id, BookmarkSortMode.Name), ResultCode.InvalidRequest);
            Fails(() => repo.SortChildren(parentId, (BookmarkSortMode)99), ResultCode.InvalidRequest);
        }
    }

    private static Guid[] Children(IBookmarkRepository repo, Guid parent) => BookmarkTree.Flatten(repo.ListActive())
        .Where(row => row.Bookmark.ParentId == parent).Select(row => row.Bookmark.Id).ToArray();
    private static CapturedTarget Target(string name) => new(TargetKind.File, @"C:\synthetic\commands\" + name + ".txt");
    private static StickerLayout Layout(Guid id) => new(id, "test-monitor", 100, 200, 260, 36);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code)
    {
        try { action(); } catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    private static long Scalar(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void Downgrade(string path)
    {
        using var db = Open(path);
        Execute(db, "DROP INDEX bookmark_sibling_order; ALTER TABLE bookmarks DROP COLUMN sibling_order; PRAGMA user_version=8;");
    }
}
