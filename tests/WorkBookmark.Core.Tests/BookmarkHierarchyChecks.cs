using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class BookmarkHierarchyChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-hierarchy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("T-S01 Reparent, detach and restart retain bookmark history, subtree and sticker layout", RoundTrip);
            Run("T-S02 Deleted parents project as roots and restore or recapture retains relationships", Deletion);
            Run("T-S03 Self, descendant, deleted-ancestor cycles and unavailable moves are rejected atomically", Validation);
            Run("T-S04 Failed attach, move and detach roll back all relationship state", Rollback);
            Run("T-S05 Schema 5 migration preserves every field, sticker layout, backup and capture sequence", () => Migration(false));
            Run("T-S06 Failed schema 5 migration preserves original schema and retry succeeds", () => Migration(true));
            Run("T-S07 Incomplete version 5 presentation schema is rejected before migration", IncompleteMigrationSchema);
            Run("T-C01 Full tree projection preserves parents beyond recent limits, sibling order and collapsed branches", FullTree);
            Run("T-C02 Projection tolerates deep trees and malformed cycles without omissions or recursion", DefensiveProjection);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"BOOKMARK HIERARCHY RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;

        void Run(string title, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + title); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + title + ": " + error); }
        }
        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");

        void RoundTrip()
        {
            string database = Db();
            Bookmark parent, child, grandchild, other;
            StickerLayout layout;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                parent = repo.CreateFolder("parent");
                child = repo.CreateFolder("child");
                grandchild = repo.UpsertCapture(Target("grandchild")).Bookmark;
                other = repo.CreateFolder("other");
                repo.UpdateNote(child.Id, "계층 이동 후에도 유지할 메모");
                child = repo.Get(child.Id)!;
                layout = new StickerLayout(child.Id, @"\\.\DISPLAY2", -40, 220, 340, 290, true, true, parent.Id);
                repo.SaveStickerLayout(layout);
                repo.SetParent(child.Id, parent.Id);
                repo.SetParent(grandchild.Id, child.Id);
                Check(repo.Get(child.Id) == child with { ParentId = parent.Id }, "Attaching changes only the parent link.");
                repo.SetParent(child.Id, other.Id);
                Check(repo.Get(child.Id) == child with { ParentId = other.Id } && repo.Get(grandchild.Id) == grandchild with { ParentId = child.Id },
                    "Moving a subtree preserves its descendants and every non-hierarchy field.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(child.Id) == child with { ParentId = other.Id } && reopened.Get(grandchild.Id) == grandchild with { ParentId = child.Id }
                && reopened.GetStickerLayouts().Single() == layout, "Parent links, subtree, geometry and the independently saved placement parent survive repository restart.");
            Check(reopened.GetStickerLayouts().Single().ParentId != reopened.Get(child.Id)!.ParentId,
                "A relationship changed while stickers are closed remains distinguishable from the saved placement after restart.");
            reopened.SetParent(child.Id, null);
            Check(reopened.Get(child.Id) == child && reopened.Get(grandchild.Id)!.ParentId == child.Id, "Detaching moves only the subtree root.");
            reopened.SaveStickerLayout(layout with { ParentId = null });
            Check(reopened.GetStickerLayouts().Single() == layout with { ParentId = null }, "Saving a detached placement clears its parent snapshot without changing geometry.");
            Check(reopened.UpsertCapture(Target("after-move")).Bookmark.CaptureSequence == other.CaptureSequence + 1,
                "Hierarchy writes never consume capture sequence numbers.");
        }

        void Deletion()
        {
            string database = Db();
            Guid parentId, childId;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                parentId = repo.CreateFolder("parent").Id;
                childId = repo.UpsertCapture(Target("child")).Bookmark.Id;
                repo.SetParent(childId, parentId);
                repo.SoftDelete(parentId);
                var projected = BookmarkTree.Flatten(repo.ListActive());
                Check(projected.Count == 1 && projected[0].Bookmark.Id == childId && projected[0].Depth == 0
                    && repo.Get(childId)!.ParentId == parentId, "Deleting a parent leaves active children visible without destroying their links.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            reopened.Restore(parentId);
            Check(BookmarkTree.Flatten(reopened.ListActive()).Select(row => row.Depth).SequenceEqual([0, 1]), "Restoring the parent restores its visible hierarchy.");
            reopened.SoftDelete(childId);
            Check(reopened.ListDeleted().Single().ParentId == parentId, "Deleted history retains parent links.");
            var recapture = reopened.UpsertCapture(Target("child"));
            Check(recapture.RestoredDeleted && recapture.Bookmark.Id == childId && recapture.Bookmark.ParentId == parentId, "Recapture restores the same parent relationship.");
        }

        void Validation()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var parent = repo.CreateFolder("parent");
            var middle = repo.CreateFolder("middle");
            var child = repo.CreateFolder("child");
            repo.SetParent(middle.Id, parent.Id);
            repo.SetParent(child.Id, middle.Id);
            var before = repo.ListActive().ToArray();
            Fails(() => repo.SetParent(parent.Id, parent.Id), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(parent.Id, child.Id), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(Guid.Empty, parent.Id), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(child.Id, Guid.Empty), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(Guid.NewGuid(), parent.Id), ResultCode.TargetUnavailable);
            Fails(() => repo.SetParent(child.Id, Guid.NewGuid()), ResultCode.TargetUnavailable);
            Check(repo.ListActive().SequenceEqual(before), "Rejected moves preserve the committed tree.");
            repo.SoftDelete(middle.Id);
            Fails(() => repo.SetParent(parent.Id, child.Id), ResultCode.InvalidRequest);
            Fails(() => repo.SetParent(child.Id, middle.Id), ResultCode.TargetUnavailable);
            Fails(() => repo.SetParent(middle.Id, null), ResultCode.TargetUnavailable);
            var all = repo.ListActive().Concat(repo.ListDeleted()).ToArray();
            Check(!BookmarkTree.CanReparent(all, parent.Id, child.Id) && !BookmarkTree.CanReparent(all, child.Id, middle.Id)
                && !BookmarkTree.CanReparent(all, middle.Id, null) && BookmarkTree.CanReparent(all, child.Id, null),
                "Shared validation includes retained deleted ancestry when supplied and accepts an active detach.");
            repo.Restore(middle.Id);
            Check(repo.ListActive().SequenceEqual(before), "Restoring deleted ancestors cannot reveal a cycle from rejected moves.");
        }

        void Rollback()
        {
            bool fail = false;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (fail && operation == "parent") throw new IOException("Injected parent write failure"); });
            var parent = repo.CreateFolder("parent");
            var other = repo.CreateFolder("other");
            var child = repo.UpsertCapture(Target("child")).Bookmark;
            fail = true;
            Fails(() => repo.SetParent(child.Id, parent.Id), ResultCode.PersistenceFailed);
            Check(repo.Get(child.Id) == child, "A failed initial attach leaves the item at root.");
            fail = false;
            repo.SetParent(child.Id, parent.Id);
            var before = repo.ListActive().ToArray();
            fail = true;
            Fails(() => repo.SetParent(child.Id, other.Id), ResultCode.PersistenceFailed);
            Fails(() => repo.SetParent(child.Id, null), ResultCode.PersistenceFailed);
            Check(repo.ListActive().SequenceEqual(before), "Failed parent replacement and detach both preserve the previous committed tree.");
        }

        void Migration(bool injectFailure)
        {
            string database = Db();
            var expected = StickerStorageChecks.CreateVersionFour(database);
            using (var db = Open(database))
            {
                Execute(db, VersionFiveStickerSchema);
                foreach (var item in expected)
                {
                    using var insert = db.CreateCommand();
                    insert.CommandText = "INSERT INTO sticker_layouts VALUES($id,'display before migration',-35,120,350,270,1,1)";
                    insert.Parameters.AddWithValue("$id", item.Id.ToString());
                    insert.ExecuteNonQuery();
                }
            }
            string beforeRows = Rows(database, "SELECT * FROM bookmarks ORDER BY id");
            string beforeLayouts = Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id");
            string beforeSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
            if (injectFailure)
            {
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Injected hierarchy migration failure"); }); }, ResultCode.PersistenceFailed);
                CheckOld(database);
            }
            else
            {
                using var migrated = new SqliteBookmarkRepository(database);
                Check(expected.All(item => migrated.Get(item.Id) == item), "All version 5 bookmark fields survive the migration.");
            }
            CheckOld(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single());
            using var repo = new SqliteBookmarkRepository(database);
            Check(expected.All(item => repo.Get(item.Id) == item) && repo.GetStickerLayouts().Count == expected.Count
                && repo.GetStickerLayouts().All(layout => layout.ParentId is null)
                && Rows(database, "SELECT bookmark_id,monitor_device,left_offset,top_offset,width,height,is_collapsed,always_on_top FROM sticker_layouts ORDER BY bookmark_id") == beforeLayouts,
                "Hierarchy migration preserves active and deleted records and their complete sticker geometry, with unknown placement parents starting null.");
            using (var db = Open(database))
            {
                Check(Scalar(db, "PRAGMA user_version") == 8 && Scalar(db, "SELECT count(*) FROM pragma_foreign_key_list('bookmarks') WHERE \"from\"='parent_id' AND \"table\"='bookmarks'") == 1,
                    "Schema 8 parent links reference existing bookmark identities.");
                Execute(db, "PRAGMA foreign_keys=ON");
                foreach (var sql in new[] { "UPDATE bookmarks SET parent_id='missing'", "UPDATE bookmarks SET parent_id=id" })
                {
                    try { Execute(db, sql); throw new InvalidOperationException("Expected parent constraint failure."); }
                    catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
                }
                Check(Scalar(db, "SELECT count(*) FROM unrelated_content WHERE value='preserve me'") == 1, "Migration preserves unrelated user content.");
            }
            var group = repo.CreateFolder("after-migration");
            repo.SetParent(expected[1].Id, group.Id);
            Check(repo.Get(expected[1].Id)!.ParentId == group.Id && group.CaptureSequence == 24 && repo.UpsertCapture(Target("after-migration")).Bookmark.CaptureSequence == 25,
                "Migrated trees are writable without resetting capture sequence.");

            void CheckOld(string path)
            {
                using var db = Open(path);
                Check(Scalar(db, "PRAGMA user_version") == 5 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23
                    && Rows(path, "SELECT * FROM bookmarks ORDER BY id") == beforeRows
                    && Rows(path, "SELECT * FROM sticker_layouts ORDER BY bookmark_id") == beforeLayouts
                    && Rows(path, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == beforeSchema,
                    "Migration backups and failed attempts retain the exact original schema, content and version.");
            }
        }

        void FullTree()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var parent = repo.CreateFolder("old-parent");
            var children = Enumerable.Range(0, 134).Select(index => repo.UpsertCapture(Target("child-" + index)).Bookmark).ToArray();
            foreach (var child in children) repo.SetParent(child.Id, parent.Id);
            var rows = BookmarkTree.Flatten(repo.ListActive());
            Check(!repo.List().Items.Any(item => item.Id == parent.Id) && rows.Count == 135 && rows[0].Bookmark.Id == parent.Id && rows[0].HasChildren,
                "Tree projection includes an old parent beyond recent-list and search limits.");
            Check(rows.Skip(1).All(row => row.Depth == 1 && !row.HasChildren)
                && rows.Skip(1).Select(row => row.Bookmark.Id).SequenceEqual(children.Reverse().Select(item => item.Id)),
                "Sibling order follows descending capture sequence under its parent.");
            Check(BookmarkTree.Flatten(repo.ListActive(), new HashSet<Guid> { parent.Id }).Count == 1,
                "Collapsed branches hide every descendant, without promoting hidden children to roots.");
            var tied = children.Take(2).Select(item => item with { CaptureSequence = 1 }).ToArray();
            Check(BookmarkTree.Flatten(tied).Select(row => row.Bookmark.Id).SequenceEqual(tied.OrderBy(item => item.Id).Select(item => item.Id)),
                "Equal capture sequence has deterministic identity tie-breaking, and missing parents project as roots.");
        }

        void IncompleteMigrationSchema()
        {
            string database = Db();
            StickerStorageChecks.CreateVersionFour(database);
            using (var db = Open(database)) Execute(db, VersionFiveStickerSchema + " DROP TABLE sticker_layouts;");
            string beforeRows = Rows(database, "SELECT * FROM bookmarks ORDER BY id");
            string beforeSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
            Fails(() => { using var ignored = new SqliteBookmarkRepository(database); }, ResultCode.PersistenceFailed);
            using var current = Open(database);
            Check(Scalar(current, "PRAGMA user_version") == 5 && Rows(database, "SELECT * FROM bookmarks ORDER BY id") == beforeRows
                && Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == beforeSchema,
                "An incomplete version 5 database retains its original rows and schema without inventing replacement layouts.");
        }

        void DefensiveProjection()
        {
            var now = DateTimeOffset.UtcNow;
            var sample = new Bookmark(Guid.NewGuid(), Target("sample"), @"C:\synthetic\sample.txt", "sample", "", now, now, 1, null, null, null, null);
            var chain = new List<Bookmark>();
            for (int index = 0; index < 10000; index++) chain.Add(sample with { Id = Guid.NewGuid(), CaptureSequence = index + 1, ParentId = index == 0 ? null : chain[index - 1].Id });
            var rows = BookmarkTree.Flatten(chain);
            Check(rows.Count == chain.Count && rows[^1].Depth == chain.Count - 1 && !BookmarkTree.CanReparent(chain, chain[0].Id, chain[^1].Id),
                "Deep hierarchies project and validate without recursive stack growth.");
            var cycle = chain.Take(3).ToArray();
            cycle[0] = cycle[0] with { ParentId = cycle[2].Id };
            var cycleRows = BookmarkTree.Flatten(cycle);
            Check(cycleRows.Count == 3 && cycleRows.Select(row => row.Bookmark.Id).Distinct().Count() == 3
                && cycleRows.Select(row => row.Bookmark.Id).SequenceEqual(BookmarkTree.Flatten(cycle.Reverse()).Select(row => row.Bookmark.Id)),
                "Malformed cycles project each item once and break consistently regardless of input order.");
            Check(BookmarkTree.Flatten([sample with { ParentId = sample.Id }]).Single().Depth == 0, "Malformed self links remain visible as roots.");
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
        db.Open();
        return db;
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

    // Frozen version 5 presentation schema layered on the independent version 4 bookmark fixture.
    internal const string VersionFiveStickerSchema = @"
CREATE TABLE sticker_layouts (
 bookmark_id TEXT PRIMARY KEY NOT NULL REFERENCES bookmarks(id) ON DELETE CASCADE,
 monitor_device TEXT NOT NULL CHECK(length(monitor_device)<=256 AND instr(monitor_device,char(0))=0),
 left_offset INTEGER NOT NULL CHECK(left_offset BETWEEN -100000 AND 100000),
 top_offset INTEGER NOT NULL CHECK(top_offset BETWEEN -100000 AND 100000),
 width INTEGER NOT NULL CHECK(width BETWEEN 1 AND 16384),
 height INTEGER NOT NULL CHECK(height BETWEEN 1 AND 16384),
 is_collapsed INTEGER NOT NULL CHECK(is_collapsed IN (0,1)),
 always_on_top INTEGER NOT NULL CHECK(always_on_top IN (0,1))
);
CREATE INDEX bookmark_deleted ON bookmarks(deleted_at_utc DESC,capture_sequence DESC) WHERE deleted_at_utc IS NOT NULL;
PRAGMA user_version=5;";
}
