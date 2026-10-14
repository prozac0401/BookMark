using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class BookmarkFolderChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-folder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("F-S01 Linkless folders persist names, nested membership, expansion and independent colors", Lifecycle);
            Run("F-S02 Ordinary bookmark drops atomically create a closed folder at the target parent", Grouping);
            Run("F-S03 Folder drops preserve expansion, subtree, snapshots and capture history", FolderMoves);
            Run("F-S04 Cycle, unavailable-target and folder-only operation validation preserves data", Validation);
            Run("F-S05 Failed grouping, folder create, rename and expansion roll back every affected row", Rollback);
            Run("F-S06 Version 7 parent graphs become nested virtual folders without historical/layout loss", () => Migration(false));
            Run("F-S07 Failed version 7 folder migration preserves database, backup and successful retry", () => Migration(true));
            Run("F-C01 Folder target and name policy cannot enter capture, resume or worker launch", Policy);
            Run("F-S08 Incomplete schemas and externally corrupted folder metadata are rejected without repair", Corruption);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"BOOKMARK FOLDER RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        void Run(string title, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + title); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + title + ": " + error); }
        }

        void Lifecycle()
        {
            string database = Db();
            Bookmark folder, nested, link;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                folder = repo.CreateFolder("  계획  ");
                nested = repo.CreateFolder("자료", folder.Id);
                link = repo.UpsertCapture(Target("link")).Bookmark;
                repo.SetParent(link.Id, nested.Id);
                Check(folder.IsFolder && folder.DisplayName == "계획" && folder.Target == new CapturedTarget(TargetKind.BookmarkFolder, "")
                    && folder.NormalizedPath == folder.Id.ToString() && !folder.FolderExpanded && folder.Color == BookmarkColor.Default,
                    "Folders are closed, explicit linkless identities with trimmed names.");
                repo.RenameFolder(folder.Id, "프로젝트 계획");
                repo.SetFolderExpanded(folder.Id, true);
                repo.SetColor(folder.Id, BookmarkColor.Blue);
                folder = folder with { DisplayName = "프로젝트 계획", FolderExpanded = true, Color = BookmarkColor.Blue };
                Check(repo.Get(folder.Id) == folder && repo.Get(nested.Id) == nested && repo.Get(link.Id) == link with { ParentId = nested.Id },
                    "Folder name/expansion/color updates change no descendant fields or capture metadata.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(folder.Id) == folder && reopened.Get(nested.Id) == nested, "Folder state and names survive restart.");
            reopened.SoftDelete(folder.Id);
            Check(BookmarkTree.Flatten(reopened.ListActive()).Single(row => row.Bookmark.Id == nested.Id).Depth == 0,
                "An active nested folder stays visible when its parent is deleted.");
            reopened.Restore(folder.Id);
            Check(reopened.Get(folder.Id) == folder && BookmarkTree.Flatten(reopened.ListActive()).Select(row => row.Depth).SequenceEqual([0, 1, 2]),
                "Restore recovers the same folder, expansion, color and nesting.");
            reopened.SetFolderExpanded(folder.Id, false);
            Check(BookmarkTree.Flatten(reopened.ListActive(), new HashSet<Guid> { folder.Id }).Count == 1, "An explicit collapsed folder hides its complete descendant tree.");
        }

        void Grouping()
        {
            string database = Db();
            using var repo = new SqliteBookmarkRepository(database);
            var outer = repo.CreateFolder("상위");
            var a = repo.UpsertCapture(Target("A")).Bookmark;
            var b = repo.UpsertCapture(Target("B")).Bookmark;
            repo.SetParent(a.Id, outer.Id);
            repo.UpdateNote(a.Id, "보존할 대상 메모");
            repo.SetColor(a.Id, BookmarkColor.Pink);
            repo.RecordResume(a.Id, ResultCode.OpenRequested);
            a = repo.Get(a.Id)!;
            var layout = new StickerLayout(a.Id, "display", 110, 220, 300, 150, true, true, outer.Id);
            repo.SaveStickerLayout(layout);
            var commit = repo.DropOnto(b.Id, a.Id);
            var folder = commit.CreatedFolder!;
            Check(folder.IsFolder && folder.DisplayName == "새 폴더" && folder.ParentId == outer.Id && !folder.FolderExpanded
                && folder.CaptureSequence == b.CaptureSequence + 1 && commit.ChangedItems.Select(item => item.Id).ToHashSet().SetEquals([folder.Id, a.Id, b.Id]),
                "Dropping B onto ordinary A creates exactly one closed folder beside A's previous siblings.");
            Check(repo.Get(a.Id) == a with { ParentId = folder.Id } && repo.Get(b.Id) == b with { ParentId = folder.Id }
                && repo.Get(outer.Id) == outer && repo.GetStickerLayouts().Single(item => item.BookmarkId == a.Id) == layout,
                "Grouping changes only the membership of A/B and preserves their complete history, colors and raw geometry.");
            Check(repo.GetStickerLayouts().Single(item => item.BookmarkId == folder.Id) == layout with { BookmarkId = folder.Id, ParentId = folder.ParentId },
                "The newly grouped folder atomically inherits A's saved anchor with its own topology snapshot.");
            var sibling = repo.UpsertCapture(Target("C")).Bookmark;
            repo.SetParent(sibling.Id, folder.Id);
            var second = repo.DropOnto(sibling.Id, a.Id);
            Check(second.CreatedFolder!.ParentId == folder.Id && repo.Get(sibling.Id)!.ParentId == second.CreatedFolder.Id,
                "Two links already in the same folder can form a nested folder.");
            repo.Dispose();
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.GetStickerLayouts().Single(item => item.BookmarkId == folder.Id) == layout with { BookmarkId = folder.Id, ParentId = outer.Id }
                && reopened.GetStickerLayouts().Single(item => item.BookmarkId == second.CreatedFolder.Id) == layout with { BookmarkId = second.CreatedFolder.Id, ParentId = folder.Id }
                && reopened.GetStickerLayouts().Single(item => item.BookmarkId == a.Id) == layout,
                "Exiting after list-only grouping retains each new folder anchor and A's exact original geometry for the first sticker reveal.");
        }

        void FolderMoves()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var source = repo.CreateFolder("작업 묶음");
            var nested = repo.CreateFolder("세부", source.Id);
            var leaf = repo.UpsertCapture(Target("leaf")).Bookmark;
            repo.SetParent(leaf.Id, nested.Id);
            var destination = repo.CreateFolder("목적지");
            repo.SetFolderExpanded(destination.Id, true);
            destination = repo.Get(destination.Id)!;
            var result = repo.DropOnto(source.Id, destination.Id);
            Check(result.CreatedFolder is null && repo.Get(source.Id) == source with { ParentId = destination.Id }
                && repo.Get(destination.Id) == destination && repo.Get(nested.Id) == nested && repo.Get(leaf.Id) == leaf with { ParentId = nested.Id },
                "Moving a folder preserves descendant relationships and the destination's explicit expanded state.");
            var before = repo.ListActive().ToArray();
            Check(!BookmarkTree.CanDropOnto(before, source.Id, destination.Id) && repo.DropOnto(source.Id, destination.Id).ChangedItems.Count == 0
                && repo.ListActive().SequenceEqual(before), "Dropping into the same parent is a no-op without another folder or sequence change.");
            repo.SetParent(source.Id, null);
            Check(repo.Get(source.Id) == source && repo.Get(nested.Id) == nested, "Detaching a folder preserves the entire subtree.");
            var linkTarget = repo.UpsertCapture(Target("target")).Bookmark;
            var grouped = repo.DropOnto(source.Id, linkTarget.Id);
            Check(grouped.CreatedFolder is { IsFolder: true } && repo.Get(nested.Id)!.ParentId == source.Id,
                "A folder dropped onto a normal bookmark is nested intact alongside that bookmark in a new folder.");
        }

        void Validation()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var parent = repo.CreateFolder("parent");
            var middle = repo.CreateFolder("middle", parent.Id);
            var child = repo.CreateFolder("child", middle.Id);
            var leaf = repo.UpsertCapture(Target("leaf")).Bookmark;
            repo.SetParent(leaf.Id, child.Id);
            Fails(() => repo.DropOnto(parent.Id, leaf.Id), ResultCode.InvalidRequest);
            Fails(() => repo.DropOnto(parent.Id, child.Id), ResultCode.InvalidRequest);
            Fails(() => repo.DropOnto(child.Id, child.Id), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(child.Id, leaf.Id), ResultCode.InvalidRequest);
            Fails(() => repo.CreateFolder("bad parent", leaf.Id), ResultCode.InvalidRequest);
            Fails(() => repo.RenameFolder(leaf.Id, "not a folder"), ResultCode.InvalidRequest);
            Fails(() => repo.SetFolderExpanded(leaf.Id, true), ResultCode.InvalidRequest);
            repo.SoftDelete(middle.Id);
            var before = repo.ListActive().Concat(repo.ListDeleted()).ToArray();
            Fails(() => repo.DropOnto(parent.Id, child.Id), ResultCode.InvalidRequest);
            Fails(() => repo.DropOnto(leaf.Id, middle.Id), ResultCode.TargetUnavailable);
            Fails(() => repo.DropOnto(middle.Id, parent.Id), ResultCode.TargetUnavailable);
            Fails(() => repo.DropOnto(leaf.Id, Guid.NewGuid()), ResultCode.TargetUnavailable);
            Check(!BookmarkTree.CanDropOnto(before, parent.Id, child.Id) && repo.ListActive().Concat(repo.ListDeleted()).SequenceEqual(before),
                "Cycle validation follows deleted ancestors, and every rejected drop preserves all committed rows.");
        }

        void Rollback()
        {
            string? failure = null;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (operation == failure) throw new IOException("Injected folder mutation failure"); });
            var folder = repo.CreateFolder("folder");
            var a = repo.UpsertCapture(Target("A")).Bookmark;
            var b = repo.UpsertCapture(Target("B")).Bookmark;
            var targetLayout = new StickerLayout(a.Id, "rollback-display", 50, 80, 320, 180);
            repo.SaveStickerLayout(targetLayout);
            var before = repo.ListActive().ToArray();
            failure = "folder-drop";
            Fails(() => repo.DropOnto(b.Id, a.Id), ResultCode.PersistenceFailed);
            Fails(() => repo.DropOnto(b.Id, folder.Id), ResultCode.PersistenceFailed);
            failure = "folder-create";
            Fails(() => repo.CreateFolder("failed"), ResultCode.PersistenceFailed);
            failure = "folder-rename";
            Fails(() => repo.RenameFolder(folder.Id, "changed"), ResultCode.PersistenceFailed);
            failure = "folder-expanded";
            Fails(() => repo.SetFolderExpanded(folder.Id, true), ResultCode.PersistenceFailed);
            Check(repo.ListActive().SequenceEqual(before) && repo.GetStickerLayouts().Single() == targetLayout,
                "Failed grouping cannot leave an empty folder, cloned layout or partially move either bookmark.");
            failure = null;
            Check(repo.CreateFolder("successful").CaptureSequence == b.CaptureSequence + 1,
                "Failed folder creation and grouping also roll back the capture sequence.");
        }

        void Migration(bool injectFailure)
        {
            string database = Db();
            var expected = BookmarkColorChecks.CreateVersionSix(database).ToArray();
            expected[0] = expected[0] with { Color = BookmarkColor.Blue };
            expected[1] = expected[1] with { Color = BookmarkColor.Green, DeletedAtUtc = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
            expected[2] = expected[2] with { Color = BookmarkColor.Purple, DeletedAtUtc = null };
            using (var db = Open(database))
            {
                Execute(db, "ALTER TABLE bookmarks ADD COLUMN color INTEGER NOT NULL DEFAULT 0 CHECK(color BETWEEN 0 AND 6); PRAGMA user_version=7;" +
                    " CREATE TABLE bookmark_audit(value TEXT); CREATE TRIGGER preserve_trigger AFTER UPDATE ON bookmarks BEGIN INSERT INTO bookmark_audit VALUES('updated'); END;" +
                    " CREATE TABLE attachment(bookmark_id TEXT REFERENCES bookmarks(id),value TEXT); CREATE INDEX preserve_custom_index ON bookmarks(note);");
                foreach (var row in expected)
                {
                    using var update = db.CreateCommand();
                    update.CommandText = "UPDATE bookmarks SET color=$color,deleted_at_utc=$deleted WHERE id=$id; INSERT INTO attachment VALUES($id,'preserve attached data');";
                    update.Parameters.AddWithValue("$color", (int)row.Color);
                    update.Parameters.AddWithValue("$deleted", row.DeletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
                    update.Parameters.AddWithValue("$id", row.Id.ToString());
                    update.ExecuteNonQuery();
                }
            }
            string before = Snapshot(database);
            string layouts = Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id");
            string audit = Rows(database, "SELECT * FROM bookmark_audit");
            if (injectFailure)
            {
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Injected folder migration failure"); }); }, ResultCode.PersistenceFailed);
                Check(Snapshot(database) == before, "Failed rebuild preserves the exact version 7 database and all referencing rows.");
            }
            else { using var ignored = new SqliteBookmarkRepository(database); }
            Check(Snapshot(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single()) == before,
                "The consistent migration backup retains the full original version 7 graph and schema.");
            using var repo = new SqliteBookmarkRepository(database);
            var originals = expected.Select(row => repo.Get(row.Id)!).ToArray();
            Check(expected.Zip(originals).All(pair => pair.Second with { ParentId = pair.First.ParentId } == pair.First),
                "Original identities, link targets, notes, colors, capture ordering and deleted/resume history remain exact.");
            var outer = repo.Get(originals[0].ParentId!.Value)!;
            var inner = repo.Get(originals[1].ParentId!.Value)!;
            Check(outer.IsFolder && inner.IsFolder && outer.ParentId is null && inner.ParentId == outer.Id && originals[2].ParentId == inner.Id
                && outer.CaptureSequence == expected[0].CaptureSequence && inner.DeletedAtUtc == expected[1].DeletedAtUtc && !outer.FolderExpanded && !inner.FolderExpanded,
                "Each former parent gains one folder wrapper preserving logical nesting, order, and deleted ancestor visibility.");
            Check(Rows(database, "SELECT * FROM sticker_layouts WHERE bookmark_id IN (SELECT id FROM bookmarks WHERE kind<>9) ORDER BY bookmark_id") == layouts && Rows(database, "SELECT * FROM bookmark_audit") == audit,
                "Rebuild retains all geometry and topology snapshots and does not fire unrelated update triggers.");
            var saved = repo.GetStickerLayouts().ToDictionary(item => item.BookmarkId);
            Check(saved[outer.Id] == saved[originals[0].Id] with { BookmarkId = outer.Id, ParentId = outer.ParentId }
                && saved[inner.Id] == saved[originals[1].Id] with { BookmarkId = inner.Id, ParentId = inner.ParentId },
                "Migrated wrappers inherit their original parent's exact saved anchor without altering any original layout.");
            using (var db = Open(database))
            {
                Check(Scalar(db, "PRAGMA user_version") == 8 && Scalar(db, "SELECT count(*) FROM pragma_foreign_key_check") == 0
                    && Scalar(db, "SELECT count(*) FROM attachment") == expected.Length && Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name IN ('preserve_trigger','preserve_custom_index')") == 2,
                    "Migration preserves custom indexes/triggers and foreign-key dependents without cascade deletion or dangling references.");
            }
            Check(BookmarkTree.Flatten(repo.ListActive()).Single(row => row.Bookmark.Id == originals[2].Id).Depth == 0,
                "Active children of a deleted old parent remain visible roots after migration.");
            repo.Restore(inner.Id);
            Check(BookmarkTree.Flatten(repo.ListActive()).Single(row => row.Bookmark.Id == originals[2].Id).Depth == 2,
                "Restoring the container makes its retained active contents reachable in their original nested group.");
            Check(repo.CreateFolder("next").CaptureSequence == 24, "Migration wrappers do not consume or reset historical capture sequence.");
        }

        void Policy()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            foreach (string name in new[] { "", " ", new string('x', 101), "invalid\0name", "invalid\nname" })
                Fails(() => repo.CreateFolder(name), ResultCode.InvalidRequest);
            var folder = repo.CreateFolder(new string('가', 100));
            Fails(() => repo.UpsertCapture(folder.Target), ResultCode.UnsupportedTarget);
            Fails(() => repo.UpsertCapture(folder.Target with { Path = @"C:\synthetic\fake.txt" }), ResultCode.UnsupportedTarget);
            Fails(() => repo.RecordResume(folder.Id, ResultCode.OpenRequested), ResultCode.InvalidRequest);
            Fails(() => repo.Relink(folder.Id, Target("real")), ResultCode.InvalidRequest);
            var now = DateTimeOffset.UtcNow;
            foreach (var operation in Enum.GetValues<Operation>())
                Check(!FrameProtocol.IsValid(new WorkerRequest(FrameProtocol.Version, Guid.NewGuid(), operation, now.AddSeconds(5),
                    Snapshot: new TargetSnapshot(1, 1), Target: folder.Target), now), "Virtual folders never become worker capture/resume/relink launch targets.");
            Check(repo.Get(folder.Id) == folder, "Rejected external actions do not modify folder metadata.");
        }

        void Corruption()
        {
            foreach (int version in new[] { 7, 8 })
            {
                string database = Db();
                BookmarkColorChecks.CreateVersionSix(database);
                using (var db = Open(database))
                {
                    if (version == 8) Execute(db, "ALTER TABLE bookmarks ADD COLUMN color INTEGER NOT NULL DEFAULT 0 CHECK(color BETWEEN 0 AND 6)");
                    Execute(db, "PRAGMA user_version=" + version);
                }
                string before = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database); }, ResultCode.PersistenceFailed);
                Check(Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == before,
                    "Version 7 without color and version 8 without folder state are rejected without fabricating missing data.");
            }
            string extendedPath = Db();
            BookmarkColorChecks.CreateVersionSix(extendedPath);
            using (var db = Open(extendedPath)) Execute(db,
                "ALTER TABLE bookmarks ADD COLUMN color INTEGER NOT NULL DEFAULT 0; ALTER TABLE bookmarks ADD COLUMN custom_content TEXT DEFAULT 'retain this'; PRAGMA user_version=7;");
            string extendedRows = Rows(extendedPath, "SELECT * FROM bookmarks ORDER BY id");
            Fails(() => { using var ignored = new SqliteBookmarkRepository(extendedPath); }, ResultCode.PersistenceFailed);
            Check(Rows(extendedPath, "SELECT * FROM bookmarks ORDER BY id") == extendedRows,
                "A rebuild rejects unknown bookmark columns instead of silently dropping their data.");
            // This independent connection simulates external edits after repository initialization.
            string path = Db();
            using var corrupted = new SqliteBookmarkRepository(path);
            var folder = corrupted.CreateFolder("valid");
            using var db2 = Open(path);
            Execute(db2, "UPDATE bookmarks SET display_name='   ' WHERE kind=9");
            Fails(() => corrupted.Get(folder.Id), ResultCode.PersistenceFailed);
            Execute(db2, "UPDATE bookmarks SET display_name='valid'; PRAGMA ignore_check_constraints=ON; UPDATE bookmarks SET path='https://example.com' WHERE kind=9;");
            Fails(() => corrupted.Get(folder.Id), ResultCode.PersistenceFailed);
        }
    }

    private static CapturedTarget Target(string name) => new(TargetKind.File, @"C:\synthetic\" + name + ".txt");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code)
    {
        try { action(); }
        catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    private static string Rows(string database, string sql)
    {
        using var db = Open(database);
        using var command = db.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    private static string Snapshot(string database) => string.Join('\n', new[] { "PRAGMA user_version", "SELECT * FROM bookmarks ORDER BY id",
        "SELECT * FROM sticker_layouts ORDER BY bookmark_id", "SELECT * FROM metadata ORDER BY name", "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name",
        "SELECT * FROM attachment ORDER BY bookmark_id", "SELECT * FROM bookmark_audit" }.Select(sql => Rows(database, sql)));
}
