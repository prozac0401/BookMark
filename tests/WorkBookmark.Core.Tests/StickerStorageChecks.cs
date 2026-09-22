using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class StickerStorageChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-sticker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("S-S01 Sticker history includes all active records and deleted history is bounded and ordered", Lists);
            Run("S-S02 Sticker geometry and presentation round-trip without altering bookmark history", RoundTrip);
            Run("S-S03 Delete, restart, restore and recapture retain sticker geometry and note", Deletion);
            Run("S-S04 Failed layout, deletion and restoration writes preserve committed state", Rollback);
            Run("S-S05 Invalid and orphan layouts cannot alter persisted state", Validation);
            Run("S-S06 Schema 4 upgrade preserves all bookmark fields, schema, unrelated content and backup", () => Migration(false));
            Run("S-S07 Schema 4 upgrade failure rolls back and retry retains original content", () => Migration(true));
            Run("S-S08 Incomplete current sticker schema is rejected without initialization", IncompleteSchema);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"STICKER STORAGE RESULT: {passed} passed; {failed} failed. Isolated Core/SQLite checks; desktop interaction is verified separately.");
        return failed == 0 ? 0 : 1;

        void Run(string title, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + title); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + title + ": " + error); }
        }
        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");

        void Lists()
        {
            var now = DateTimeOffset.Parse("2026-09-20T01:00:00Z");
            using var repo = new SqliteBookmarkRepository(Db(), utcNow: () => now);
            var rows = Enumerable.Range(0, 135).Select(index => repo.UpsertCapture(FileTarget(index.ToString())).Bookmark).ToArray();
            Check(repo.List().Items.Count == 20 && repo.ListActive().Count == 135, "Sticker mode must not inherit the recent-list limit.");
            Check(repo.ListActive().Select(row => row.Id).SequenceEqual(rows.Reverse().Select(row => row.Id)), "All active bookmarks retain capture ordering.");
            // Delete in the opposite order to capture order to prove ordering uses deletion time.
            foreach (var row in rows.Take(105).Reverse()) { now = now.AddMinutes(1); repo.SoftDelete(row.Id); }
            Check(repo.ListActive().Count == 30 && repo.ListActive().All(row => row.DeletedAtUtc is null), "Hidden bookmarks cannot appear as active stickers.");
            Check(repo.ListDeleted().Count == 100 && repo.ListDeleted(1000).Count == 105, "Deleted-history default limit remains bounded and can be extended.");
            Check(repo.ListDeleted(2).Select(row => row.Id).SequenceEqual(rows.Take(2).Select(row => row.Id)), "The last two deletions lead deleted history regardless of capture order.");
            Fails(() => repo.ListDeleted(0), ResultCode.InvalidRequest);
            Fails(() => repo.ListDeleted(1001), ResultCode.InvalidRequest);
        }

        void RoundTrip()
        {
            string database = Db();
            Bookmark first;
            StickerLayout expected;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                first = repo.UpsertCapture(FileTarget()).Bookmark;
                repo.UpdateNote(first.Id, "검토한 내용 이어가기");
                repo.RecordResume(first.Id, ResultCode.OpenRequested);
                first = repo.Get(first.Id)!;
                var second = repo.UpsertCapture(FileTarget("second")).Bookmark;
                var other = new StickerLayout(second.Id, @"\\.\DISPLAY2", 40, 80, 320, 260);
                repo.SaveStickerLayout(other);
                repo.SaveStickerLayout(new StickerLayout(first.Id, @"\\.\DISPLAY1", -25, -40, 300, 250));
                expected = new StickerLayout(first.Id, @"\\.\DISPLAY2", 125, -15, 440, 370, true, true);
                repo.SaveStickerLayout(expected);
                Check(repo.Get(first.Id) == first, "Moving, resizing and pinning a sticker cannot change capture, note or resume data.");
                Check(repo.GetStickerLayouts().Count == 2 && repo.GetStickerLayouts().Contains(other), "Updating one layout cannot replace another layout.");
                Check(repo.UpsertCapture(FileTarget("third")).Bookmark.CaptureSequence == second.CaptureSequence + 1, "Layout writes cannot consume capture sequence numbers.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(first.Id) == first && reopened.GetStickerLayouts().Single(layout => layout.BookmarkId == first.Id) == expected,
                "Monitor, negative offset, expanded size, collapse and topmost state survive restart exactly.");
        }

        void Deletion()
        {
            string database = Db();
            Bookmark first;
            StickerLayout expected;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                first = repo.UpsertCapture(FileTarget()).Bookmark;
                repo.UpdateNote(first.Id, "계속할 작업");
                first = repo.Get(first.Id)!;
                expected = new StickerLayout(first.Id, @"\\.\DISPLAY1", 80, 120, 340, 290, true);
                repo.SaveStickerLayout(expected);
                repo.SoftDelete(first.Id);
                Check(repo.ListActive().Count == 0 && repo.ListDeleted().Single().Id == first.Id && repo.GetStickerLayouts().Single() == expected,
                    "Deleting the sticker hides the shared bookmark but retains its layout.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.ListDeleted().Single().Id == first.Id && reopened.GetStickerLayouts().Single() == expected, "Deleted history and layout survive restart.");
            reopened.Restore(first.Id);
            Check(reopened.Get(first.Id) == first && reopened.ListDeleted().Count == 0 && reopened.GetStickerLayouts().Single() == expected,
                "Restore recovers the original note, ordering and layout.");
            reopened.SoftDelete(first.Id);
            var recaptured = reopened.UpsertCapture(first.Target);
            Check(recaptured.RestoredDeleted && recaptured.ExistingNotePreserved && recaptured.Bookmark.Id == first.Id && reopened.GetStickerLayouts().Single() == expected,
                "Recapture restores the same identity and its existing placement.");
        }

        void Rollback()
        {
            string? fail = null;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (operation == fail) throw new IOException("Injected sticker write failure"); });
            var first = repo.UpsertCapture(FileTarget()).Bookmark;
            var second = repo.UpsertCapture(FileTarget("second")).Bookmark;
            var layout = new StickerLayout(first.Id, @"\\.\DISPLAY1", 20, 30, 300, 280);
            repo.SaveStickerLayout(layout);
            fail = "sticker-layout";
            Fails(() => repo.SaveStickerLayout(layout with { Left = 999, Width = 500, IsCollapsed = true }));
            Fails(() => repo.SaveStickerLayout(layout with { BookmarkId = second.Id }));
            Check(repo.GetStickerLayouts().Single() == layout && repo.Get(first.Id) == first && repo.Get(second.Id) == second,
                "Failed layout creates and updates leave geometry and bookmark data unchanged.");
            fail = "delete";
            Fails(() => repo.SoftDelete(first.Id));
            Check(repo.ListActive().Count == 2 && repo.GetStickerLayouts().Single() == layout, "Failed deletion keeps the sticker active at its saved position.");
            fail = null;
            repo.SoftDelete(first.Id);
            fail = "restore";
            Fails(() => repo.Restore(first.Id));
            Check(repo.ListActive().Count == 1 && repo.ListDeleted().Single().Id == first.Id && repo.GetStickerLayouts().Single() == layout,
                "Failed restore keeps the item deleted and preserves its placement for a later retry.");
        }

        void Validation()
        {
            string database = Db();
            using var repo = new SqliteBookmarkRepository(database);
            var first = repo.UpsertCapture(FileTarget()).Bookmark;
            var layout = new StickerLayout(first.Id, "", -100000, 100000, 1, 16384);
            repo.SaveStickerLayout(layout);
            foreach (var invalid in new[] { layout with { BookmarkId = Guid.Empty }, layout with { MonitorDevice = null! },
                layout with { MonitorDevice = new string('x', 257) }, layout with { MonitorDevice = "bad\0monitor" }, layout with { MonitorDevice = "bad\nmonitor" },
                layout with { Left = -100001 }, layout with { Top = 100001 }, layout with { Width = 0 }, layout with { Width = 16385 }, layout with { Height = -1 } })
                Fails(() => repo.SaveStickerLayout(invalid), ResultCode.InvalidRequest);
            Fails(() => repo.SaveStickerLayout(null!), ResultCode.InvalidRequest);
            Fails(() => repo.SaveStickerLayout(layout with { BookmarkId = Guid.NewGuid() }), ResultCode.TargetUnavailable);
            Check(repo.GetStickerLayouts().Single() == layout && repo.Get(first.Id) == first, "Validation rejects invalid values without changing committed state.");
            using var db = Open(database);
            Execute(db, "PRAGMA foreign_keys=ON;");
            foreach (string sql in new[] { "UPDATE sticker_layouts SET width=0", "UPDATE sticker_layouts SET height=16385", "UPDATE sticker_layouts SET left_offset=-100001",
                "UPDATE sticker_layouts SET is_collapsed=2", "UPDATE sticker_layouts SET always_on_top=-1", "UPDATE sticker_layouts SET bookmark_id='missing'" })
            {
                try { Execute(db, sql); throw new InvalidOperationException("Expected SQLite constraint rejection."); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
            }
            Check(repo.GetStickerLayouts().Single() == layout, "Database constraints also protect layout integrity.");
        }

        void Migration(bool injectFailure)
        {
            string database = Db();
            var expected = CreateVersionFour(database);
            string beforeRows = Rows(database, "SELECT * FROM bookmarks ORDER BY id");
            string beforeSchema = Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name");
            if (injectFailure)
            {
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Injected v4 migration failure"); }); });
                CheckVersionFour(database, beforeRows, beforeSchema);
            }
            else
            {
                using var migrated = new SqliteBookmarkRepository(database);
                Check(expected.All(row => migrated.Get(row.Id) == row), "Every version 4 field, including the full snapshot, survives migration.");
            }
            string backup = Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single();
            CheckVersionFour(backup, beforeRows, beforeSchema);
            using var repo = new SqliteBookmarkRepository(database);
            Check(expected.All(row => repo.Get(row.Id) == row) && repo.GetStickerLayouts().Count == 0, "Migration starts with no invented placements or modified bookmarks.");
            Check(Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT IN ('sticker_layouts','sqlite_autoindex_sticker_layouts_1','bookmark_deleted') ORDER BY type,name") == beforeSchema,
                "Version 4 tables, indexes and unrelated content keep their exact schema.");
            using (var db = Open(database))
            {
                Check(Scalar(db, "PRAGMA user_version") == 5 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23,
                    "Version and sequence update independently.");
                Check(Scalar(db, "SELECT count(*) FROM unrelated_content WHERE value='preserve me'") == 1, "Unrelated user data is preserved.");
            }
            Check(repo.UpsertCapture(FileTarget("after-upgrade")).Bookmark.CaptureSequence == 24, "The first new capture continues the original monotonic sequence.");
            repo.SaveStickerLayout(new StickerLayout(expected[0].Id, @"\\.\DISPLAY1", 10, 20, 300, 250));
            Check(repo.GetStickerLayouts().Count == 1, "Migrated repositories can persist layouts.");
        }

        void IncompleteSchema()
        {
            string database = Db();
            using (var repo = new SqliteBookmarkRepository(database)) repo.UpsertCapture(FileTarget());
            using (var db = Open(database)) Execute(db, "DROP TABLE sticker_layouts;");
            string before = Rows(database, "SELECT * FROM bookmarks");
            Fails(() => { using var ignored = new SqliteBookmarkRepository(database); });
            Check(Rows(database, "SELECT * FROM bookmarks") == before, "A missing layout table never causes silent database reinitialization.");
        }
    }

    private static CapturedTarget FileTarget(string name = "original") => new(TargetKind.File, @"C:\synthetic\" + name + ".txt");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code = ResultCode.PersistenceFailed)
    {
        try { action(); }
        catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static SqliteConnection Open(string database)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
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
    private static void CheckVersionFour(string database, string expectedRows, string expectedSchema)
    {
        using var db = Open(database);
        using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
        Check((string?)check.ExecuteScalar() == "ok" && Scalar(db, "PRAGMA user_version") == 4 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23,
            "Version 4 backups and rollbacks retain database integrity, original version and sequence.");
        Check(Rows(database, "SELECT * FROM bookmarks ORDER BY id") == expectedRows && Rows(database, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name") == expectedSchema,
            "All original fields and schema objects survive backup and rollback.");
    }
    private static IReadOnlyList<Bookmark> CreateVersionFour(string database)
    {
        var time = DateTimeOffset.Parse("2026-09-01T01:02:03Z");
        CapturedTarget[] targets = [FileTarget(), new(TargetKind.ExcelCell, @"C:\synthetic\sheet.xlsx", "확정자", "$D$127", true),
            NotepadSnapshotPolicy.Create("이전 메모", "첫 줄\n둘째 줄 😀", 3, 7, true)];
        var rows = targets.Select((target, index) => new Bookmark(Guid.NewGuid(), target, PathPolicy.NormalizeLocation(target), PathPolicy.DisplayName(target),
            "version 4 note " + index, time, time.AddHours(index + 1), 11 + index, time.AddMinutes(index + 1), time.AddHours(index + 2),
            ResultCode.PositionRestoredFocusPending, index == 2 ? time.AddDays(1) : null)).ToArray();
        using var db = Open(database);
        Execute(db, VersionFourSchema + " CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL); INSERT INTO metadata VALUES('capture_sequence',23);" +
            " CREATE TABLE unrelated_content(value TEXT); INSERT INTO unrelated_content VALUES('preserve me'); CREATE INDEX unrelated_index ON unrelated_content(value); PRAGMA user_version=4;");
        foreach (var item in rows)
        {
            object?[] fields = [item.Id.ToString(), (int)item.Target.Kind, item.Target.Path, item.NormalizedPath, item.Target.SheetName, item.Target.CellAddress,
                item.DisplayName, item.Note, item.CreatedAtUtc.ToString("O"), item.CapturedAtUtc.ToString("O"), item.CaptureSequence, item.NoteUpdatedAtUtc?.ToString("O"),
                item.Target.HadUnsavedChanges, item.LastResumeAtUtc?.ToString("O"), (int?)item.LastResumeResult, item.DeletedAtUtc?.ToString("O"), item.Target.WordStart,
                item.Target.SlideId, item.Target.SlideNumber, item.Target.PdfPage, item.Target.PageTitle, item.Target.TextOffset, item.Target.TextContent, item.Target.TextSelectionEnd, item.Target.SnapshotTitle];
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO bookmarks VALUES(" + string.Join(',', Enumerable.Range(0, fields.Length).Select(index => "$p" + index)) + ")";
            for (int index = 0; index < fields.Length; index++) insert.Parameters.AddWithValue("$p" + index, fields[index] ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        return rows;
    }

    // Frozen schema 4 fixture, independent of current production schema and migration code.
    private const string VersionFourSchema = @"
CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL,
 kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 8),
 path TEXT NOT NULL,
 normalized_path TEXT COLLATE BINARY NOT NULL,
 sheet_name TEXT COLLATE BINARY,
 cell_address TEXT COLLATE BINARY,
 display_name TEXT NOT NULL,
 note TEXT NOT NULL DEFAULT '' CHECK(length(note)<=500),
 created_at_utc TEXT NOT NULL,
 captured_at_utc TEXT NOT NULL,
 capture_sequence INTEGER NOT NULL CHECK(capture_sequence>0),
 note_updated_at_utc TEXT,
 had_unsaved_changes INTEGER CHECK(had_unsaved_changes IS NULL OR had_unsaved_changes IN (0,1)),
 last_resume_at_utc TEXT,
 last_resume_result INTEGER,
 deleted_at_utc TEXT,
 word_start INTEGER CHECK(word_start>=0),
 slide_id INTEGER CHECK(slide_id>0),
 slide_number INTEGER CHECK(slide_number>0),
 pdf_page INTEGER CHECK(pdf_page>0),
 page_title TEXT CHECK(length(page_title)<=256),
 text_offset INTEGER CHECK(text_offset>=0),
 text_content TEXT CHECK(text_content IS NULL OR (length(text_content)<=2097152 AND instr(text_content,char(0))=0 AND instr(text_content,char(13))=0)),
 text_selection_end INTEGER CHECK(text_selection_end>=0 AND text_selection_end<=2097152),
 snapshot_title TEXT CHECK(length(snapshot_title) BETWEEN 1 AND 256),
 CHECK((kind IN (7,8) AND text_offset IS NOT NULL) OR (kind NOT IN (7,8) AND text_offset IS NULL)),
 CHECK((kind=8 AND text_content IS NOT NULL AND text_selection_end IS NOT NULL AND snapshot_title IS NOT NULL AND text_offset<=text_selection_end
        AND length(path)=81 AND substr(path,1,17)='notepad-snapshot:' AND substr(path,18) NOT GLOB '*[^0-9A-F]*' AND normalized_path=path) OR
       (kind<>8 AND text_content IS NULL AND text_selection_end IS NULL AND snapshot_title IS NULL)),
 CHECK((kind=6 AND page_title IS NOT NULL) OR (kind<>6 AND page_title IS NULL)),
 CHECK((kind IN (0,1) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=2 AND sheet_name IS NOT NULL AND cell_address IS NOT NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=3 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NOT NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=4 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NOT NULL AND slide_number IS NOT NULL AND pdf_page IS NULL) OR
       (kind=5 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NOT NULL) OR
       (kind=6 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind IN (7,8) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL))
);
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind, normalized_path, ifnull(sheet_name,''), ifnull(cell_address,''), ifnull(word_start,-1), ifnull(slide_id,-1), ifnull(pdf_page,-1), ifnull(text_offset,-1), ifnull(text_selection_end,-1));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);";
}
