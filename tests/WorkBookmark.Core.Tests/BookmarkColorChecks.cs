using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class BookmarkColorChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-color-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("C-S01 Explicit colors round-trip and reset without changing hierarchy, layout or history", RoundTrip);
            Run("C-S02 Delete, restore and recapture retain explicit bookmark colors", Deletion);
            Run("C-S03 Invalid, unavailable and failed color writes preserve committed data", ValidationAndRollback);
            Run("C-S04 Version 6 migration preserves tree, placement snapshots, historical fields and backup", () => Migration(false));
            Run("C-S05 Version 6 migration failure rolls back and retry retains all original data", () => Migration(true));
            Run("C-S06 Missing version 6 hierarchy columns are rejected before migration", IncompleteSchema);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"BOOKMARK COLOR RESULT: {passed} passed; {failed} failed.");
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
            Bookmark parent, child;
            StickerLayout layout;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                parent = repo.UpsertCapture(Target("parent")).Bookmark;
                child = repo.UpsertCapture(Target("child")).Bookmark;
                Check(parent.Color == BookmarkColor.Default && child.Color == BookmarkColor.Default, "New captures retain the default palette unless explicitly changed.");
                repo.SetParent(child.Id, parent.Id);
                repo.UpdateNote(child.Id, "색을 바꾸어도 유지할 메모");
                repo.RecordResume(child.Id, ResultCode.PositionRestored);
                child = repo.Get(child.Id)!;
                layout = new StickerLayout(child.Id, @"\\.\DISPLAY2", -25, 40, 320, 200, true, true, parent.Id);
                repo.SaveStickerLayout(layout);
                foreach (var color in Enum.GetValues<BookmarkColor>())
                {
                    repo.SetColor(child.Id, color);
                    Check(repo.Get(child.Id) == child with { Color = color } && repo.Get(parent.Id) == parent
                        && repo.GetStickerLayouts().Single() == layout, "Each explicit palette choice changes only the selected bookmark color.");
                }
            }
            using (var reopened = new SqliteBookmarkRepository(database))
            {
                Check(reopened.Get(child.Id) == child with { Color = BookmarkColor.Gray } && reopened.GetStickerLayouts().Single() == layout,
                    "Color and all unrelated content survive repository restart.");
                reopened.SetColor(child.Id, BookmarkColor.Default);
                Check(reopened.Get(child.Id) == child, "Resetting to default restores the original color without altering any bookmark field.");
                Check(reopened.UpsertCapture(Target("after-color")).Bookmark.CaptureSequence == child.CaptureSequence + 1,
                    "Color writes do not consume capture sequence numbers.");
            }
            using var reset = new SqliteBookmarkRepository(database);
            Check(reset.Get(child.Id) == child && reset.Get(parent.Id) == parent, "Default reset and independent parent color persist across restart.");
        }

        void Deletion()
        {
            string database = Db();
            Bookmark colored;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                var parent = repo.UpsertCapture(Target("parent")).Bookmark;
                var child = repo.UpsertCapture(Target("child")).Bookmark;
                repo.SetParent(child.Id, parent.Id);
                repo.SetColor(parent.Id, BookmarkColor.Blue);
                Check(repo.Get(child.Id)!.Color == BookmarkColor.Default, "Children do not inherit the parent's color.");
                repo.SetColor(child.Id, BookmarkColor.Purple);
                colored = repo.Get(child.Id)!;
                repo.SoftDelete(child.Id);
                Check(repo.ListDeleted().Single().Color == BookmarkColor.Purple, "Deleted history retains each item's explicit color.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            reopened.Restore(colored.Id);
            Check(reopened.Get(colored.Id) == colored, "Restore retains original color, relationship and history exactly.");
            reopened.SoftDelete(colored.Id);
            var recaptured = reopened.UpsertCapture(colored.Target);
            Check(recaptured.RestoredDeleted && recaptured.Bookmark.Id == colored.Id && recaptured.Bookmark.Color == BookmarkColor.Purple
                && recaptured.Bookmark.ParentId == colored.ParentId, "Recapture restores the same color and hierarchy instead of resetting presentation.");
        }

        void ValidationAndRollback()
        {
            string database = Db();
            bool fail = false;
            using var repo = new SqliteBookmarkRepository(database, operation => { if (fail && operation == "color") throw new IOException("Injected color write failure"); });
            var item = repo.UpsertCapture(Target("original")).Bookmark;
            repo.SetColor(item.Id, BookmarkColor.Green);
            var before = repo.Get(item.Id)!;
            Fails(() => repo.SetColor(item.Id, (BookmarkColor)(-1)), ResultCode.InvalidRequest);
            Fails(() => repo.SetColor(item.Id, (BookmarkColor)7), ResultCode.InvalidRequest);
            Fails(() => repo.SetColor(Guid.Empty, BookmarkColor.Blue), ResultCode.InvalidRequest);
            Fails(() => repo.SetColor(Guid.NewGuid(), BookmarkColor.Blue), ResultCode.TargetUnavailable);
            fail = true;
            Fails(() => repo.SetColor(item.Id, BookmarkColor.Pink), ResultCode.PersistenceFailed);
            Fails(() => repo.SetColor(item.Id, BookmarkColor.Default), ResultCode.PersistenceFailed);
            Check(repo.Get(item.Id) == before, "Rejected updates and failed color/default-reset commits preserve the previous color and all metadata.");
            fail = false;
            repo.SoftDelete(item.Id);
            var deleted = repo.Get(item.Id)!;
            Fails(() => repo.SetColor(item.Id, BookmarkColor.Gray), ResultCode.TargetUnavailable);
            Check(repo.Get(item.Id) == deleted, "Color changes cannot modify or restore a deleted item.");
            using var db = Open(database);
            foreach (var sql in new[] { "UPDATE bookmarks SET color=-1", "UPDATE bookmarks SET color=7", "UPDATE bookmarks SET color=NULL" })
            {
                try { Execute(db, sql); throw new InvalidOperationException("Expected color constraint rejection."); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
            }
            Check(repo.Get(item.Id) == deleted, "SQLite constraints protect the supported stored color range.");
        }

        void Migration(bool injectFailure)
        {
            string database = Db();
            var expected = CreateVersionSix(database);
            string beforeRows = Rows(database, "SELECT * FROM bookmarks ORDER BY id");
            string beforeLayouts = Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id");
            string beforeSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
            string beforeColumns = Rows(database, "SELECT * FROM pragma_table_info('bookmarks')");
            string unchangedSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name<>'bookmarks' ORDER BY type,name");
            if (injectFailure)
            {
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Injected color migration failure"); }); }, ResultCode.PersistenceFailed);
                CheckOld(database);
            }
            else
            {
                using var migrated = new SqliteBookmarkRepository(database);
                Check(expected.All(item => migrated.Get(item.Id) == item), "All version 6 historical and hierarchy fields survive color migration.");
            }
            CheckOld(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single());
            using var repo = new SqliteBookmarkRepository(database);
            Check(expected.All(item => repo.Get(item.Id) == item)
                && Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id") == beforeLayouts
                && Rows(database, "SELECT * FROM pragma_table_info('bookmarks') WHERE name<>'color'") == beforeColumns
                && Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name<>'bookmarks' ORDER BY type,name") == unchangedSchema,
                "Migration preserves exact sticker geometry, parent snapshots, existing columns, indexes and unrelated schema.");
            Check(repo.GetStickerLayouts().Any(layout => layout.ParentId is not null), "The version 6 fixture exercises nonempty placement parent snapshots.");
            using (var db = Open(database))
            {
                Check(Scalar(db, "PRAGMA user_version") == 7 && Scalar(db, "SELECT count(*) FROM bookmarks WHERE color=0") == expected.Count,
                    "Version 7 initializes every existing active and deleted item to the default color.");
                Check(Scalar(db, "SELECT count(*) FROM unrelated_content WHERE value='preserve me'") == 1, "Color migration preserves unrelated user content.");
            }
            repo.SetColor(expected[1].Id, BookmarkColor.Yellow);
            Check(repo.Get(expected[1].Id) == expected[1] with { Color = BookmarkColor.Yellow }
                && repo.UpsertCapture(Target("after-upgrade")).Bookmark.CaptureSequence == 24,
                "Migrated colors are writable without changing historical relationships or capture sequence.");

            void CheckOld(string path)
            {
                using var db = Open(path);
                Check(Scalar(db, "PRAGMA user_version") == 6 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23
                    && Rows(path, "SELECT * FROM bookmarks ORDER BY id") == beforeRows
                    && Rows(path, "SELECT * FROM sticker_layouts ORDER BY bookmark_id") == beforeLayouts
                    && Rows(path, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == beforeSchema,
                    "The backup or failed migration retains version 6 tree, placement snapshots, schema, rows and sequence exactly.");
            }
        }

        void IncompleteSchema()
        {
            foreach (bool missingBookmarkParent in new[] { true, false })
            {
                string database = Db();
                CreateVersionSix(database, includeBookmarkParent: !missingBookmarkParent, includeStickerParent: missingBookmarkParent);
                string beforeRows = Rows(database, "SELECT * FROM bookmarks ORDER BY id");
                string beforeLayouts = Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id");
                string beforeSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database); }, ResultCode.PersistenceFailed);
                using var db = Open(database);
                Check(Scalar(db, "PRAGMA user_version") == 6 && Rows(database, "SELECT * FROM bookmarks ORDER BY id") == beforeRows
                    && Rows(database, "SELECT * FROM sticker_layouts ORDER BY bookmark_id") == beforeLayouts
                    && Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == beforeSchema,
                    "A version 6 database missing either relationship column is preserved without fabricating state.");
            }
        }
    }

    private static IReadOnlyList<Bookmark> CreateVersionSix(string database, bool includeBookmarkParent = true, bool includeStickerParent = true)
    {
        var rows = StickerStorageChecks.CreateVersionFour(database).ToArray();
        using var db = Open(database);
        Execute(db, BookmarkHierarchyChecks.VersionFiveStickerSchema);
        // Frozen version 6 changes, layered on independent version 4/5 fixtures.
        if (includeBookmarkParent)
        {
            Execute(db, "ALTER TABLE bookmarks ADD COLUMN parent_id TEXT REFERENCES bookmarks(id) ON DELETE SET NULL CHECK(parent_id IS NULL OR parent_id<>id); CREATE INDEX bookmark_parent ON bookmarks(parent_id);");
            for (int index = 1; index < rows.Length; index++)
            {
                rows[index] = rows[index] with { ParentId = rows[index - 1].Id };
                using var update = db.CreateCommand();
                update.CommandText = "UPDATE bookmarks SET parent_id=$parent WHERE id=$id";
                update.Parameters.AddWithValue("$parent", rows[index - 1].Id.ToString());
                update.Parameters.AddWithValue("$id", rows[index].Id.ToString());
                update.ExecuteNonQuery();
            }
        }
        if (includeStickerParent) Execute(db, "ALTER TABLE sticker_layouts ADD COLUMN parent_id TEXT;");
        foreach (var row in rows)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO sticker_layouts(bookmark_id,monitor_device,left_offset,top_offset,width,height,is_collapsed,always_on_top) VALUES($id,'version 6 display',-25,90,320,210,1,1)";
            insert.Parameters.AddWithValue("$id", row.Id.ToString());
            insert.ExecuteNonQuery();
            if (includeStickerParent && row.ParentId is Guid parent)
            {
                using var update = db.CreateCommand();
                update.CommandText = "UPDATE sticker_layouts SET parent_id=$parent WHERE bookmark_id=$id";
                update.Parameters.AddWithValue("$parent", parent.ToString());
                update.Parameters.AddWithValue("$id", row.Id.ToString());
                update.ExecuteNonQuery();
            }
        }
        Execute(db, "PRAGMA user_version=6;");
        return rows;
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
}
