using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class NotepadSnapshotChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("N-S01 Empty, Korean, emoji and newline snapshots use canonical UTF-16 selections", Policy);
            Run("N-S02 Snapshot bodies, selections, metadata and content hashes reject tampering", InvalidSnapshots);
            Run("N-S03 Snapshot boundaries allow two Mi UTF-16 characters and reject larger input", Boundaries);
            Run("N-S04 Snapshot metadata cannot leak onto file, browser or legacy position targets", MixedMetadata);
            Run("N-S05 Snapshot identity preserves notes and distinguishes content, title and selection", Identity);
            Run("N-S06 Snapshot content, selection, history and deletion survive restart", Restart);
            Run("N-S07 Failed snapshot transactions preserve committed content and capture sequence", Rollback);
            Run("N-S08 Schema 3 migration preserves every historical field and consistent backup", () => Migration(false));
            Run("N-S09 Schema 3 migration failure rolls back all fields and can be retried", () => Migration(true));
            Run("N-S10 SQLite snapshot constraints and read validation reject corrupted content", StorageConstraints);
            Run("N-S11 Full-size request/response frames round-trip and over-limit frames fail", () => Frames().GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"SNAPSHOT RESULT: {passed} passed; {failed} failed. Isolated Core/SQLite/protocol checks; editor acceptance remains separate.");
        return failed == 0 ? 0 : 1;

        void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");

        void Identity()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            var target = Snapshot();
            var first = repo.UpsertCapture(target).Bookmark;
            repo.UpdateNote(first.Id, "계속 작업할 메모");
            repo.RecordResume(first.Id, ResultCode.PositionRestored);
            var prior = repo.Get(first.Id)!;
            var duplicate = repo.UpsertCapture(target with { HadUnsavedChanges = false });
            Check(duplicate.Bookmark.Id == first.Id && duplicate.ExistingNotePreserved && duplicate.Bookmark.Note == prior.Note &&
                duplicate.Bookmark.NoteUpdatedAtUtc == prior.NoteUpdatedAtUtc && duplicate.Bookmark.CreatedAtUtc == prior.CreatedAtUtc &&
                duplicate.Bookmark.LastResumeAtUtc == prior.LastResumeAtUtc && duplicate.Bookmark.Target.HadUnsavedChanges == false,
                "An unchanged snapshot retains note and identity while updating unsaved state.");
            foreach (var different in new[] { NotepadSnapshotPolicy.Create("작업", "다른 본문", 0, 0, true),
                NotepadSnapshotPolicy.Create("다른 제목", target.TextContent!, 0, 0, true),
                target with { TextOffset = 1, TextSelectionEnd = 1 }, target with { TextSelectionEnd = 1 } })
                Check(repo.UpsertCapture(different).Bookmark.Id != first.Id, "Content, title, start and selection end must each distinguish a snapshot.");
            Check(repo.List().Items.Count == 5 && repo.List("작업").Items.Count >= 1 && repo.List("계속 작업할 메모").Items.Single().Id == first.Id,
                "Readable title and note are searchable without using a synthetic path as a display name.");
            Fails(() => repo.Relink(first.Id, target), ResultCode.InvalidRequest);
            Check(repo.Get(first.Id)!.Target == duplicate.Bookmark.Target, "Snapshot relink is not meaningful and cannot mutate history.");
        }

        void Restart()
        {
            string database = Db();
            Bookmark retained;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                var target = NotepadSnapshotPolicy.Create("새 메모", "한글\n😀 선택\n마지막", 3, 8, true);
                var first = repo.UpsertCapture(target).Bookmark;
                repo.UpdateNote(first.Id, "원문 보존");
                repo.RecordResume(first.Id, ResultCode.PositionRestored);
                repo.SoftDelete(first.Id);
                Check(repo.List().Items.Count == 0 && repo.Get(first.Id)!.Target == target, "Soft delete hides but preserves the exact snapshot.");
                repo.Restore(first.Id);
                Check(repo.Get(first.Id)!.CaptureSequence == first.CaptureSequence, "Undo retains ordering.");
                repo.SoftDelete(first.Id);
                var restored = repo.UpsertCapture(target);
                Check(restored.RestoredDeleted && restored.ExistingNotePreserved && restored.Bookmark.Id == first.Id, "Recapture restores deleted snapshot identity and note.");
                retained = restored.Bookmark;
                var empty = repo.UpsertCapture(NotepadSnapshotPolicy.Create("", "", 0, 0, false)).Bookmark;
                Check(empty.Target.TextContent == "" && empty.DisplayName == "제목 없음", "An empty new document persists without a user file.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(retained.Id) == retained, "Every field including Unicode content and selected range survives restart.");
        }

        void Rollback()
        {
            string? fail = null;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (operation == fail) throw new IOException("Injected snapshot rollback"); });
            var first = repo.UpsertCapture(Snapshot()).Bookmark;
            repo.UpdateNote(first.Id, "committed note");
            var before = repo.Get(first.Id)!;
            foreach (var operation in new (string Name, Action Action)[] {
                ("capture", () => repo.UpsertCapture(Snapshot() with { HadUnsavedChanges = false })),
                ("capture", () => repo.UpsertCapture(NotepadSnapshotPolicy.Create("작업", "새 내용", 0, 0, true))),
                ("note", () => repo.UpdateNote(first.Id, "uncommitted")),
                ("delete", () => repo.SoftDelete(first.Id)),
                ("resume", () => repo.RecordResume(first.Id, ResultCode.PositionRestored)) })
            {
                fail = operation.Name;
                Fails(operation.Action, ResultCode.PersistenceFailed);
                Check(repo.Get(first.Id) == before && repo.List().Items.Count == 1, "Failed " + fail + " leaves all snapshot fields unchanged.");
            }
            fail = null;
            Check(repo.UpsertCapture(Snapshot()).Bookmark.CaptureSequence == first.CaptureSequence + 1, "Failed snapshot writes consume no sequence.");
        }

        void Migration(bool injectFailure)
        {
            string database = Db();
            var expected = CreateVersionThree(database);
            string before = RawRows(database);
            if (injectFailure)
            {
                Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Migration rollback"); }); }, ResultCode.PersistenceFailed);
                CheckVersionThree(database, before);
            }
            else
            {
                using var migrated = new SqliteBookmarkRepository(database);
                foreach (var item in expected) Check(migrated.Get(item.Id) == item, "All version 3 fields must survive migration unchanged.");
            }
            CheckVersionThree(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single(), before);
            using var repo = new SqliteBookmarkRepository(database);
            foreach (var item in expected) Check(repo.Get(item.Id) == item, "Successful retry preserves old rows.");
            Check(repo.UpsertCapture(Snapshot()).Bookmark.CaptureSequence == 24, "Version 5 preserves version 3 capture sequence.");
            using var db = Open(database);
            Check(Scalar(db, "PRAGMA user_version") == 5 && Scalar(db, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name IN ('text_content','text_selection_end','snapshot_title')") == 3,
                "Schema version 5 exposes each snapshot field once.");
        }

        void StorageConstraints()
        {
            string database = Db();
            using var repo = new SqliteBookmarkRepository(database);
            var first = repo.UpsertCapture(Snapshot()).Bookmark;
            using var db = Open(database);
            foreach (string sql in new[] { "UPDATE bookmarks SET text_content=NULL", "UPDATE bookmarks SET snapshot_title=NULL", "UPDATE bookmarks SET snapshot_title=''",
                "UPDATE bookmarks SET text_selection_end=NULL", "UPDATE bookmarks SET text_selection_end=-1", "UPDATE bookmarks SET text_offset=99,text_selection_end=0",
                "UPDATE bookmarks SET kind=7", "UPDATE bookmarks SET path='notepad-snapshot:bad'", "UPDATE bookmarks SET text_content=char(13)",
                "UPDATE bookmarks SET text_content=char(0)", "UPDATE bookmarks SET page_title='Mixed'" })
            {
                try { Execute(db, sql); throw new InvalidOperationException("Expected SQL constraint rejection: " + sql); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
            }
            Check(repo.Get(first.Id)!.Target == first.Target, "Rejected direct writes preserve content.");
            Execute(db, "UPDATE bookmarks SET text_content='tampered'");
            Fails(() => repo.Get(first.Id), ResultCode.PersistenceFailed);
        }
    }

    private static CapturedTarget Snapshot() => NotepadSnapshotPolicy.Create("작업", "첫 줄\n둘째 줄 😀", 0, 0, true);
    private static void Policy()
    {
        Check((int)TargetKind.NotepadSnapshot == 8 && (int)TargetKind.NotepadPosition == 7, "Persisted enum ordinals cannot change.");
        var empty = NotepadSnapshotPolicy.Create("  ", "", 0, 0, false);
        Check(empty.TextContent == "" && empty.TextOffset == 0 && empty.TextSelectionEnd == 0 && empty.SnapshotTitle == "제목 없음", "Empty text is a valid unsaved document.");
        const string raw = "가\r\n😀\rZ\n";
        const string normalized = "가\n😀\nZ\n";
        var target = NotepadSnapshotPolicy.Create("  첫\t제목\n  ", raw, raw.IndexOf('Z'), raw.Length, true);
        Check(target.TextContent == normalized && target.TextOffset == normalized.IndexOf('Z') && target.TextSelectionEnd == normalized.Length && target.SnapshotTitle == "첫 제목",
            "Newline conversion updates both UTF-16 coordinates and title controls.");
        Check(PathPolicy.Validate(target) == target && PathPolicy.NormalizeLocation(target) == target.Path && PathPolicy.DisplayName(target) == "첫 제목", "Snapshots use synthetic identities and meaningful display names.");
        Check(target.Path == NotepadSnapshotPolicy.Create("첫 제목", normalized, 0, 0, true).Path, "Content identity is independent of selection and modified state.");
        Check(NotepadSnapshotPolicy.Create("ab", "c", 0, 0, true).Path != NotepadSnapshotPolicy.Create("a", "bc", 0, 0, true).Path, "Length-delimited title and body cannot have an ambiguous hash preimage.");
        Check(NotepadSnapshotPolicy.Create("유니코드", "é", 0, 0, true).Path != NotepadSnapshotPolicy.Create("유니코드", "e\u0301", 0, 0, true).Path, "Unicode content is preserved without normalization.");
        var title = NotepadSnapshotPolicy.Create(new string('t', 255) + "😀", "x", 0, 0, true);
        Check(title.SnapshotTitle!.Length == 255, "Truncating the title cannot split an emoji surrogate pair.");
    }

    private static void InvalidSnapshots()
    {
        var target = Snapshot();
        foreach (var invalid in new[] { target with { TextContent = null }, target with { TextContent = "changed" }, target with { TextContent = "\0" },
            target with { TextContent = "\ud800" }, target with { TextContent = "\udc00" }, target with { TextContent = "line\r\n" },
            target with { Path = target.Path.ToLowerInvariant() }, target with { Path = "C:\\temp\\note.txt" }, target with { SnapshotTitle = " changed " },
            target with { SnapshotTitle = null }, target with { SnapshotTitle = "other" }, target with { TextOffset = -1 }, target with { TextOffset = null },
            target with { TextSelectionEnd = null }, target with { TextSelectionEnd = -1 }, target with { TextSelectionEnd = target.TextContent!.Length + 1 },
            target with { TextOffset = 2, TextSelectionEnd = 1 }, target with { HadUnsavedChanges = null }, target with { SheetName = "A" },
            target with { CellAddress = "$A$1" }, target with { WordStart = 0 }, target with { SlideId = 1 }, target with { SlideNumber = 1 },
            target with { PdfPage = 1 }, target with { PageTitle = "Mixed" } }) Fails(() => PathPolicy.Validate(invalid));
        foreach (string badText in new[] { "\0", "\ud800", "\udc00", "x\ud800y" }) Fails(() => NotepadSnapshotPolicy.Create("title", badText, 0, 0, true));
        Fails(() => NotepadSnapshotPolicy.Create("title", "abc", 2, 1, true));
        Fails(() => NotepadSnapshotPolicy.Create("title", "abc", 0, 4, true));
    }

    private static void Boundaries()
    {
        string maximum = new('가', NotepadSnapshotPolicy.MaximumTextLength);
        var target = NotepadSnapshotPolicy.Create("maximum", maximum, maximum.Length, maximum.Length, true);
        Check(target.TextContent == maximum && target.TextOffset == maximum.Length, "Exact maximum UTF-16 length is supported.");
        Fails(() => NotepadSnapshotPolicy.Create("too large", maximum + "x", 0, 0, true));
        Fails(() => PathPolicy.Validate(target with { TextContent = maximum + "x" }));
        string emoji = string.Concat(Enumerable.Repeat("😀", NotepadSnapshotPolicy.MaximumTextLength / 2));
        Check(NotepadSnapshotPolicy.Create("emoji", emoji, emoji.Length, emoji.Length, true).TextContent == emoji, "Size and offsets count UTF-16 code units, including surrogate pairs.");
    }

    private static void MixedMetadata()
    {
        var targets = new[] { new CapturedTarget(TargetKind.File, @"C:\synthetic\note.txt"),
            new CapturedTarget(TargetKind.NotepadPosition, @"C:\synthetic\note.txt", HadUnsavedChanges: true, TextOffset: 0),
            new CapturedTarget(TargetKind.WebPage, "https://example.invalid/", PageTitle: "page") };
        foreach (var target in targets)
            foreach (var invalid in new[] { target with { TextContent = "" }, target with { TextSelectionEnd = 0 }, target with { SnapshotTitle = "name" } })
                Fails(() => PathPolicy.Validate(invalid));
        Fails(() => BrowserProtocol.ValidateTarget(targets[2] with { TextContent = "private" }));
    }

    private static async Task Frames()
    {
        var target = NotepadSnapshotPolicy.Create("최대 메시지", new string('가', NotepadSnapshotPolicy.MaximumTextLength), 0, 0, true);
        var request = new WorkerRequest(FrameProtocol.Version, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(10), Target: target);
        await using var requestStream = new MemoryStream();
        await FrameProtocol.WriteAsync(requestStream, request);
        Check(requestStream.Length <= FrameProtocol.MaximumFrameBytes + 4 && requestStream.Length > 12 * 1024 * 1024, "Worst-case escaped Unicode snapshot fits the bounded frame.");
        requestStream.Position = 0;
        Check(await FrameProtocol.ReadAsync<WorkerRequest>(requestStream) == request, "A complete maximum-size request round-trips without truncation.");
        var response = new WorkerResponse(FrameProtocol.Version, request.RequestId, ResultCode.Captured, target);
        await using var responseStream = new MemoryStream();
        await FrameProtocol.WriteAsync(responseStream, response);
        responseStream.Position = 0;
        Check(await FrameProtocol.ReadAsync<WorkerResponse>(responseStream) == response, "Capture responses use the same safe upper bound.");
        byte[] header = new byte[4];
        foreach (int size in new[] { -1, 0, FrameProtocol.MaximumFrameBytes + 1 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, size);
            await using var bad = new MemoryStream(header);
            try { await FrameProtocol.ReadAsync<WorkerResponse>(bad); throw new InvalidOperationException("Expected rejected frame size."); }
            catch (InvalidDataException) { }
        }
        await using var excessive = new MemoryStream();
        try { await FrameProtocol.WriteAsync(excessive, new string('a', FrameProtocol.MaximumFrameBytes)); throw new InvalidOperationException("Expected rejected outbound frame size."); }
        catch (InvalidDataException) { Check(excessive.Length == 0, "Rejected oversized writes emit no frame header."); }
    }

    private static IReadOnlyList<Bookmark> CreateVersionThree(string database)
    {
        var time = DateTimeOffset.Parse("2026-09-01T01:02:03Z");
        var targets = new CapturedTarget[] { new(TargetKind.Folder, @"C:\synthetic\folder"), new(TargetKind.File, @"C:\synthetic\file.txt"),
            new(TargetKind.ExcelCell, @"C:\synthetic\book.xlsx", "Sheet1", "$B$2", true),
            new(TargetKind.WordPosition, @"C:\synthetic\document.docx", HadUnsavedChanges: false, WordStart: 37),
            new(TargetKind.PowerPointSlide, @"C:\synthetic\deck.pptx", HadUnsavedChanges: true, SlideId: 257, SlideNumber: 5),
            new(TargetKind.PdfPage, @"C:\synthetic\file.pdf", PdfPage: 3), new(TargetKind.WebPage, "https://example.invalid/Case?q=%2f#part", PageTitle: "Old title"),
            new(TargetKind.NotepadPosition, @"C:\synthetic\note.txt", HadUnsavedChanges: true, TextOffset: 123) };
        var rows = targets.Select((target, index) => new Bookmark(Guid.NewGuid(), target, PathPolicy.NormalizeLocation(target), PathPolicy.DisplayName(target),
            "version 3 note " + index, time, time.AddHours(index + 1), 11 + index, time.AddMinutes(index + 1), time.AddHours(index + 2),
            ResultCode.PositionRestoredFocusPending, index == 7 ? time.AddDays(1) : null)).ToList();
        using var db = Open(database);
        Execute(db, VersionThreeSchema + " CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL); INSERT INTO metadata VALUES('capture_sequence',23); PRAGMA user_version=3;");
        foreach (var item in rows)
        {
            object?[] fields = [item.Id.ToString(), (int)item.Target.Kind, item.Target.Path, item.NormalizedPath, item.Target.SheetName, item.Target.CellAddress,
                item.DisplayName, item.Note, item.CreatedAtUtc.ToString("O"), item.CapturedAtUtc.ToString("O"), item.CaptureSequence, item.NoteUpdatedAtUtc?.ToString("O"),
                item.Target.HadUnsavedChanges, item.LastResumeAtUtc?.ToString("O"), (int?)item.LastResumeResult, item.DeletedAtUtc?.ToString("O"), item.Target.WordStart,
                item.Target.SlideId, item.Target.SlideNumber, item.Target.PdfPage, item.Target.PageTitle, item.Target.TextOffset];
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO bookmarks VALUES(" + string.Join(',', Enumerable.Range(0, fields.Length).Select(i => "$p" + i)) + ")";
            for (int i = 0; i < fields.Length; i++) insert.Parameters.AddWithValue("$p" + i, fields[i] ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        return rows;
    }

    private static string RawRows(string database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM bookmarks ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    private static void CheckVersionThree(string database, string before)
    {
        using var db = Open(database);
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA quick_check";
        Check((string?)command.ExecuteScalar() == "ok" && Scalar(db, "PRAGMA user_version") == 3 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23,
            "Schema 3 backup/rollback retains integrity, version and sequence.");
        Check(Scalar(db, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='text_content'") == 0 &&
            Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='bookmarks_previous'") == 0 && RawRows(database) == before,
            "All raw fields and the original schema remain unchanged.");
    }
    private static SqliteConnection Open(string database)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static long Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code = ResultCode.UnsupportedTarget)
    {
        try { action(); }
        catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }

    // Independent schema 3 fixture: never manufacture old data by downgrading a current database.
    private const string VersionThreeSchema = @"
CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL,
 kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 7),
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
 CHECK((kind=7 AND text_offset IS NOT NULL) OR (kind<>7 AND text_offset IS NULL)),
 CHECK((kind=6 AND page_title IS NOT NULL) OR (kind<>6 AND page_title IS NULL)),
 CHECK((kind IN (0,1) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=2 AND sheet_name IS NOT NULL AND cell_address IS NOT NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=3 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NOT NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=4 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NOT NULL AND slide_number IS NOT NULL AND pdf_page IS NULL) OR
       (kind=5 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NOT NULL) OR
       (kind=6 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=7 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL))
);
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind, normalized_path, ifnull(sheet_name,''), ifnull(cell_address,''), ifnull(word_start,-1), ifnull(slide_id,-1), ifnull(pdf_page,-1), ifnull(text_offset,-1));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);";
}
