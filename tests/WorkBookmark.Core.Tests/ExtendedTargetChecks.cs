using Microsoft.Data.Sqlite;
using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class ExtendedTargetChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-extended-targets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("Browser URL state is preserved and unsupported handlers are rejected", BrowserPolicy);
            Run("Word/PowerPoint/PDF coordinates reject mixed or incomplete target metadata", PositionPolicy);
            Run("Notepad requires an unambiguous nonnegative text offset and unsaved state", NotepadPolicy);
            Run("Notepad offsets preserve notes, separate positions and survive restart", NotepadIdentity);
            Run("Notepad failed capture preserves committed state and sequence", NotepadRollback);
            Run("Version 2 migration preserves all target kinds and history with backup", VersionTwoMigration);
            Run("Version 2 migration failure restores schema, rows and sequence before retry", VersionTwoRollback);
            Run("Browser exact-URL recapture updates title and preserves notes", BrowserIdentity);
            Run("Word offsets, stable slide IDs and PDF pages survive persistence", PositionIdentity);
            Run("Position relink preserves coordinates and rejects collisions", PositionRelink);
            Run("Extended-target mutation failures preserve metadata and sequence", ExtendedRollback);
            Run("Version 1 migration preserves history, sequence and a consistent backup", LegacyMigration);
            Run("Version 1 migration failure rolls back schema and rows before retry", LegacyRollback);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"EXTENDED RESULT: {passed} passed; {failed} failed. No browser or Office automation.");
        return failed == 0 ? 0 : 1;

        void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        string Db() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");

        void NotepadIdentity()
        {
            string database = Db();
            var target = Notepad(37);
            Bookmark retained;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                var first = repo.UpsertCapture(target).Bookmark;
                repo.UpdateNote(first.Id, "continue editing here");
                repo.RecordResume(first.Id, ResultCode.PositionRestored);
                var baseline = repo.Get(first.Id)!;
                var duplicate = repo.UpsertCapture(target with { HadUnsavedChanges = true });
                Check(duplicate.Bookmark.Id == first.Id && duplicate.ExistingNotePreserved && duplicate.Bookmark.Note == baseline.Note &&
                    duplicate.Bookmark.NoteUpdatedAtUtc == baseline.NoteUpdatedAtUtc && duplicate.Bookmark.CreatedAtUtc == baseline.CreatedAtUtc &&
                    duplicate.Bookmark.LastResumeAtUtc == baseline.LastResumeAtUtc && duplicate.Bookmark.Target.HadUnsavedChanges == true,
                    "Same path and offset retain identity, notes and history while updating captured unsaved state.");
                Check(repo.UpsertCapture(target with { TextOffset = 38 }).Bookmark.Id != first.Id, "Distinct text offsets must not merge.");
                Check(repo.UpsertCapture(new(TargetKind.File, target.Path)).Bookmark.Id != first.Id, "A file bookmark is distinct from a Notepad position.");
                repo.SoftDelete(first.Id);
                var recaptured = repo.UpsertCapture(target);
                Check(recaptured.RestoredDeleted && recaptured.Bookmark.Id == first.Id && recaptured.Bookmark.Note == baseline.Note,
                    "Recapture restores the existing Notepad bookmark and its note.");
                retained = recaptured.Bookmark;
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(retained.Id) == retained, "Notepad position and all history survive restart.");
            Check(reopened.UpsertCapture(Notepad(int.MaxValue)).Bookmark.CaptureSequence == retained.CaptureSequence + 1,
                "Large valid offsets persist without resetting sequence.");
        }

        void NotepadRollback()
        {
            bool fail = false;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (fail && operation == "capture") throw new IOException("Synthetic Notepad rollback"); });
            var first = repo.UpsertCapture(Notepad(37)).Bookmark;
            repo.UpdateNote(first.Id, "keep committed note");
            var baseline = repo.Get(first.Id)!;
            fail = true;
            Fails(() => repo.UpsertCapture(first.Target with { HadUnsavedChanges = true }), ResultCode.PersistenceFailed);
            Fails(() => repo.UpsertCapture(Notepad(38)), ResultCode.PersistenceFailed);
            Check(repo.Get(first.Id) == baseline && repo.List().Items.Count == 1, "Failed recapture and a new text position both roll back.");
            fail = false;
            Check(repo.UpsertCapture(Notepad(38)).Bookmark.CaptureSequence == baseline.CaptureSequence + 1, "Failed text captures do not consume sequence.");
        }

        void VersionTwoMigration()
        {
            string database = Db();
            var expected = CreateVersionTwo(database);
            string before = SnapshotVersionTwo(database);
            using (var repo = new SqliteBookmarkRepository(database))
            {
                foreach (var row in expected) Check(repo.Get(row.Id) == row, "Every version 2 target and history field must migrate unchanged.");
                Check(repo.List().Items.Count == expected.Count(row => row.DeletedAtUtc is null), "Migrated deletion state must remain effective.");
                Check(repo.UpsertCapture(Notepad(37)).Bookmark.CaptureSequence == 24, "Schema 5 continues the version 2 metadata sequence.");
            }
            string backup = Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single();
            CheckVersionTwo(backup, before);
            using var current = Open(database);
            Check(Scalar(current, "PRAGMA user_version") == 5 && Scalar(current, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='text_offset'") == 1,
                "Schema 5 exposes exactly one text offset column.");
        }

        void VersionTwoRollback()
        {
            string database = Db();
            var expected = CreateVersionTwo(database);
            string before = SnapshotVersionTwo(database);
            Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Synthetic version 2 rollback"); }); }, ResultCode.PersistenceFailed);
            CheckVersionTwo(database, before);
            CheckVersionTwo(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single(), before);
            using var retried = new SqliteBookmarkRepository(database);
            foreach (var row in expected) Check(retried.Get(row.Id) == row, "Retry must retain every version 2 target and history field.");
            Check(retried.UpsertCapture(Notepad(37)).Bookmark.CaptureSequence == 24, "Retry keeps the original version 2 sequence.");
        }

        void BrowserIdentity()
        {
            string database = Db();
            const string url = "https://example.invalid/Case/%2fpath?x=1&x=2#Section%202";
            Guid id; long lastSequence;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                var first = repo.UpsertCapture(Web(url, "Original title")).Bookmark;
                id = first.Id;
                repo.UpdateNote(id, "keep browser note");
                var old = repo.Get(id)!;
                var updated = repo.UpsertCapture(Web(url, "Updated title"));
                Check(updated.Bookmark.Id == id && updated.ExistingNotePreserved, "Exact URL must keep bookmark identity and note flag.");
                Check(updated.Bookmark.Target.Path == url && updated.Bookmark.NormalizedPath == url && updated.Bookmark.Target.PageTitle == "Updated title" && updated.Bookmark.DisplayName == "Updated title", "Exact query/fragment and new title must persist.");
                Check(updated.Bookmark.Note == old.Note && updated.Bookmark.NoteUpdatedAtUtc == old.NoteUpdatedAtUtc && updated.Bookmark.CreatedAtUtc == old.CreatedAtUtc, "Recapture must retain historical note and creation time.");
                var fragment = repo.UpsertCapture(Web(url.Replace("Section%202", "Section%203"), "Updated title")).Bookmark;
                var query = repo.UpsertCapture(Web(url.Replace("x=1&x=2", "x=2&x=1"), "Updated title")).Bookmark;
                Check(fragment.Id != id && query.Id != id && fragment.Id != query.Id && repo.List().Items.Count == 3, "Fragment and query ordering are independent application state.");
                lastSequence = query.CaptureSequence;
                Fails(() => repo.Relink(id, Web("https://example.invalid/replaced", "New")), ResultCode.InvalidRequest);
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(id)!.Target == Web(url, "Updated title") && reopened.Get(id)!.Note == "keep browser note", "Browser state must survive restart.");
            Check(reopened.UpsertCapture(Web("https://example.invalid/next", "Next")).Bookmark.CaptureSequence == lastSequence + 1, "Browser sequence must survive restart.");
        }

        void PositionIdentity()
        {
            string database = Db();
            var word = Word(37); var slide = Slide(257, 2); var pdf = Pdf(2);
            Guid wordId, slideId, pdfId;
            using (var repo = new SqliteBookmarkRepository(database))
            {
                wordId = repo.UpsertCapture(word).Bookmark.Id;
                slideId = repo.UpsertCapture(slide).Bookmark.Id;
                pdfId = repo.UpsertCapture(pdf).Bookmark.Id;
                repo.UpdateNote(slideId, "stable slide note");
                var reordered = repo.UpsertCapture(slide with { SlideNumber = 5, HadUnsavedChanges = true });
                Check(reordered.Bookmark.Id == slideId && reordered.Bookmark.Target.SlideNumber == 5 && reordered.Bookmark.Note == "stable slide note", "A reordered stable SlideID updates its display number without splitting or losing a note.");
                Check(repo.UpsertCapture(word with { WordStart = 38 }).Bookmark.Id != wordId, "Different Word offsets are distinct.");
                Check(repo.UpsertCapture(slide with { SlideId = 258 }).Bookmark.Id != slideId, "Different slide IDs are distinct even at the same displayed number.");
                Check(repo.UpsertCapture(pdf with { PdfPage = 3 }).Bookmark.Id != pdfId, "Different PDF pages are distinct.");
                Check(repo.UpsertCapture(new(TargetKind.File, pdf.Path)).Bookmark.Id != pdfId, "A whole-file bookmark is distinct from a page bookmark.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            Check(reopened.Get(wordId)!.Target == word && reopened.Get(pdfId)!.Target == pdf, "Word and PDF coordinates survive restart.");
            Check(reopened.Get(slideId)!.Target == slide with { SlideNumber = 5, HadUnsavedChanges = true }, "Slide ID and latest captured display number survive restart.");
        }

        void PositionRelink()
        {
            using var repo = new SqliteBookmarkRepository(Db());
            foreach (var target in new[] { Word(37), Slide(257, 2), Pdf(2), Notepad(37) })
            {
                var first = repo.UpsertCapture(target).Bookmark;
                repo.UpdateNote(first.Id, "location survives relink");
                var baseline = repo.Get(first.Id)!;
                var moved = target with { Path = target.Path.Replace(@"C:\synthetic", @"D:\moved") };
                var wrong = target.Kind switch
                {
                    TargetKind.WordPosition => moved with { WordStart = 38 },
                    TargetKind.PowerPointSlide => moved with { SlideId = 258 },
                    TargetKind.NotepadPosition => moved with { TextOffset = 38 },
                    _ => moved with { PdfPage = 3 }
                };
                Fails(() => repo.Relink(first.Id, wrong), ResultCode.InvalidRequest);
                repo.UpsertCapture(moved);
                Fails(() => repo.Relink(first.Id, moved), ResultCode.DuplicateTarget);
                Check(repo.Get(first.Id) == baseline, "Refused relink must leave the original target intact.");
                var accepted = moved with { Path = moved.Path.Replace(@"D:\moved", @"D:\accepted") };
                repo.Relink(first.Id, accepted);
                var actual = repo.Get(first.Id)!;
                Check(actual.Target == accepted && actual.Note == baseline.Note && actual.CaptureSequence == baseline.CaptureSequence && actual.CapturedAtUtc == baseline.CapturedAtUtc, "Relink changes only the path and preserves captured position and history.");
            }
        }

        void ExtendedRollback()
        {
            bool fail = false;
            using var repo = new SqliteBookmarkRepository(Db(), operation => { if (fail && operation == "capture") throw new IOException("Synthetic capture rollback"); });
            var original = repo.UpsertCapture(Web("https://example.invalid/rollback#one", "Old title")).Bookmark;
            repo.UpdateNote(original.Id, "committed note");
            var baseline = repo.Get(original.Id)!;
            fail = true;
            Fails(() => repo.UpsertCapture(original.Target with { PageTitle = "Uncommitted title" }), ResultCode.PersistenceFailed);
            Fails(() => repo.UpsertCapture(Slide(257, 2)), ResultCode.PersistenceFailed);
            Check(repo.Get(original.Id) == baseline && repo.List().Items.Count == 1, "Title changes and new slide records both roll back atomically.");
            fail = false;
            Check(repo.UpsertCapture(Pdf(2)).Bookmark.CaptureSequence == baseline.CaptureSequence + 1, "Failed extended-target captures do not consume sequence.");
        }

        void LegacyMigration()
        {
            string database = Db();
            var legacy = CreateLegacy(database);
            using (var repo = new SqliteBookmarkRepository(database))
            {
                Check(repo.Get(legacy.Active.Id) == legacy.Active && repo.Get(legacy.Deleted.Id) == legacy.Deleted, "All legacy fields, notes, result, timestamps and deletion state must migrate unchanged.");
                Check(repo.List().Items.Single().Id == legacy.Active.Id, "Deleted legacy rows remain hidden.");
                Check(repo.UpsertCapture(Slide(257, 2)).Bookmark.CaptureSequence == 12, "The existing sequence continues at 12 after migration.");
            }
            string backup = Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single();
            CheckLegacy(backup, legacy);
            using var current = Open(database);
            Check(Scalar(current, "PRAGMA user_version") == 5, "Migrated schema version must be 5.");
            Check(Scalar(current, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name IN ('word_start','slide_id','slide_number','pdf_page','page_title','text_offset')") == 6, "All new position/browser columns must exist.");
        }

        void LegacyRollback()
        {
            string database = Db();
            var legacy = CreateLegacy(database);
            Fails(() => { using var ignored = new SqliteBookmarkRepository(database, operation => { if (operation == "migration") throw new IOException("Synthetic migration rollback"); }); }, ResultCode.PersistenceFailed);
            CheckLegacy(database, legacy);
            CheckLegacy(Directory.GetFiles(root, Path.GetFileName(database) + ".pre-migration-*.bak").Single(), legacy);
            using (var raw = Open(database)) Check(Scalar(raw, "SELECT count(*) FROM sqlite_master WHERE name IN ('bookmarks_v1','bookmarks_previous')") == 0, "Rolled-back table rename cannot leak.");
            using var retried = new SqliteBookmarkRepository(database);
            Check(retried.Get(legacy.Active.Id) == legacy.Active && retried.Get(legacy.Deleted.Id) == legacy.Deleted, "Retry must retain both original rows.");
            Check(retried.UpsertCapture(Word(37)).Bookmark.CaptureSequence == 12, "Retry keeps the original sequence.");
        }
    }

    private static void NotepadPolicy()
    {
        Check((int)TargetKind.NotepadPosition == 7 && (int)ResultCode.BrowserExtensionRequired == 23 && (int)ResultCode.NotepadFileRequired == 24,
            "Persisted enum ordinals remain stable.");
        foreach (var valid in new[] { Notepad(0), Notepad(int.MaxValue), Notepad(5) with { Path = @"C:\synthetic\script.ps1" },
            Notepad(5) with { Path = @"C:\synthetic\program.exe" }, Notepad(5) with { Path = @"C:\synthetic\README" } })
            Check(PathPolicy.Validate(valid) == valid, "Explicit Notepad text navigation supports valid file paths regardless of extension.");
        foreach (var invalid in new[] { Notepad(-1), Notepad(0) with { TextOffset = null }, Notepad(0) with { HadUnsavedChanges = null },
            Notepad(0) with { WordStart = 1 }, Notepad(0) with { SlideId = 257 }, Notepad(0) with { SlideNumber = 2 },
            Notepad(0) with { PdfPage = 1 }, Notepad(0) with { PageTitle = "Mixed" }, Notepad(0) with { SheetName = "A" },
            Notepad(0) with { CellAddress = "$A$1" }, Notepad(0) with { Path = @"C:\" },
            Notepad(0) with { Path = "https://example.invalid/file.txt" }, Notepad(0) with { Path = @"C:\synthetic\file.txt:stream" },
            Word(0) with { TextOffset = 1 }, new CapturedTarget(TargetKind.File, @"C:\synthetic\file.txt", TextOffset: 0) })
            Fails(() => PathPolicy.Validate(invalid));
        Check(!PathPolicy.ShouldOpenDocument(@"C:\synthetic\program.exe"), "Notepad support must not broaden the shell document allowlist.");
    }

    private static void BrowserPolicy()
    {
        const string exact = "https://Example.invalid/Case/%2fkeep?b=2&a=%2B&a=1#Section%202";
        var target = Web(exact, "Page title");
        Check(PathPolicy.Validate(target) == target && PathPolicy.NormalizeLocation(target) == exact, "URL case, escaping, repeated query keys and fragment must remain exact.");
        Check(BrowserProtocol.Validate(new(1, "capture", Guid.NewGuid(), exact, "Page title")) == target, "Native host capture must use the same target policy.");
        Check(PathPolicy.Validate(Web("https://example.invalid/page", " ")).PageTitle == "example.invalid", "Blank title uses the hostname.");
        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/private.txt", "data:text/plain,secret", "ftp://example.invalid/file", "https://user:password@example.invalid/", "https://example.invalid/space here", "https://example.invalid/\n", @"https://example.invalid\path", "/relative" })
            Fails(() => PathPolicy.Validate(Web(bad, "Title")));
        Fails(() => PathPolicy.Validate(Web("https://example.invalid/" + new string('a', BrowserProtocol.MaximumUrlLength), "Title")));
        Fails(() => PathPolicy.Validate(Web(exact, new string('t', 257))));
        Fails(() => PathPolicy.Validate(Web(exact, "bad\ttitle")));
        Fails(() => PathPolicy.Validate(target with { HadUnsavedChanges = false }));
        Fails(() => PathPolicy.Validate(target with { WordStart = 0 }));
        Fails(() => PathPolicy.Validate(target with { TextOffset = 0 }));
        Fails(() => BrowserProtocol.Validate(new(2, "capture", Guid.NewGuid(), exact, "Title")), ResultCode.InvalidRequest);
        Fails(() => BrowserProtocol.Validate(new(1, "open", Guid.NewGuid(), exact, "Title")), ResultCode.InvalidRequest);
        Fails(() => BrowserProtocol.Validate(new(1, "capture", Guid.Empty, exact, "Title")), ResultCode.InvalidRequest);
    }

    private static void PositionPolicy()
    {
        foreach (var valid in new[] { Word(0), Word(int.MaxValue), Slide(257, 2), Pdf(1) }) Check(PathPolicy.Validate(valid) == valid, "Valid positional shape rejected.");
        foreach (var invalid in new[] { Word(-1), Word(0) with { WordStart = null }, Word(0) with { HadUnsavedChanges = null },
            Word(0) with { Path = @"C:\synthetic\document.pdf" }, Word(0) with { SlideId = 257 },
            Slide(0, 2), Slide(257, 0), Slide(257, 2) with { SlideNumber = null }, Slide(257, 2) with { PdfPage = 1 },
            Slide(257, 2) with { Path = @"C:\synthetic\deck.ppsx" }, Pdf(0), Pdf(1) with { HadUnsavedChanges = false },
            Pdf(1) with { SheetName = "A" }, new CapturedTarget(TargetKind.File, @"C:\synthetic\plain.txt", WordStart: 0),
            Word(0) with { PageTitle = "Mixed" } }) Fails(() => PathPolicy.Validate(invalid));
    }

    private static CapturedTarget Web(string url, string title) => new(TargetKind.WebPage, url, PageTitle: title);
    private static CapturedTarget Word(int offset) => new(TargetKind.WordPosition, @"C:\synthetic\document.docx", HadUnsavedChanges: false, WordStart: offset);
    private static CapturedTarget Slide(int id, int number) => new(TargetKind.PowerPointSlide, @"C:\synthetic\deck.pptx", HadUnsavedChanges: false, SlideId: id, SlideNumber: number);
    private static CapturedTarget Notepad(int offset) => new(TargetKind.NotepadPosition, @"C:\synthetic\notes.txt", HadUnsavedChanges: false, TextOffset: offset);
    private static CapturedTarget Pdf(int page) => new(TargetKind.PdfPage, @"C:\synthetic\document.pdf", PdfPage: page);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code = ResultCode.UnsupportedTarget)
    {
        try { action(); }
        catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }

    private sealed record LegacyRows(Bookmark Active, Bookmark Deleted);
    private static LegacyRows CreateLegacy(string database)
    {
        var created = DateTimeOffset.Parse("2026-09-01T01:02:03Z");
        var captured = created.AddHours(2);
        var active = new Bookmark(Guid.NewGuid(), new(TargetKind.ExcelCell, @"C:\synthetic\legacy.xlsx", "Sheet1", "$B$2", true), @"C:\synthetic\legacy.xlsx", "legacy.xlsx", "legacy note", created, captured, 7, captured.AddMinutes(1), captured.AddMinutes(2), ResultCode.PositionRestored, null);
        var deleted = new Bookmark(Guid.NewGuid(), new(TargetKind.File, @"C:\synthetic\deleted.txt"), @"C:\synthetic\deleted.txt", "deleted.txt", "deleted note", created, captured.AddHours(1), 11, captured.AddMinutes(3), null, null, captured.AddHours(3));
        using var db = Open(database);
        using (var command = db.CreateCommand())
        {
            command.CommandText = @"CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL, kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2), path TEXT NOT NULL,
 normalized_path TEXT COLLATE BINARY NOT NULL, sheet_name TEXT COLLATE BINARY, cell_address TEXT COLLATE BINARY,
 display_name TEXT NOT NULL, note TEXT NOT NULL DEFAULT '' CHECK(length(note)<=500), created_at_utc TEXT NOT NULL,
 captured_at_utc TEXT NOT NULL, capture_sequence INTEGER NOT NULL CHECK(capture_sequence>0), note_updated_at_utc TEXT,
 had_unsaved_changes INTEGER CHECK(had_unsaved_changes IS NULL OR had_unsaved_changes IN (0,1)), last_resume_at_utc TEXT,
 last_resume_result INTEGER, deleted_at_utc TEXT,
 CHECK((kind IN (0,1) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL) OR
       (kind=2 AND sheet_name IS NOT NULL AND cell_address IS NOT NULL AND had_unsaved_changes IS NOT NULL)));
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind,normalized_path,ifnull(sheet_name,''),ifnull(cell_address,''));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);
CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL);
INSERT INTO metadata VALUES('capture_sequence',11); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        foreach (var item in new[] { active, deleted })
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO bookmarks VALUES($id,$kind,$path,$normalized,$sheet,$cell,$display,$note,$created,$captured,$sequence,$noteAt,$unsaved,$resumeAt,$result,$deleted)";
            foreach (var parameter in new (string Name, object? Value)[] {
                ("$id",item.Id.ToString()), ("$kind",(int)item.Target.Kind), ("$path",item.Target.Path), ("$normalized",item.NormalizedPath),
                ("$sheet",item.Target.SheetName), ("$cell",item.Target.CellAddress), ("$display",item.DisplayName), ("$note",item.Note),
                ("$created",item.CreatedAtUtc.ToString("O")), ("$captured",item.CapturedAtUtc.ToString("O")), ("$sequence",item.CaptureSequence),
                ("$noteAt",item.NoteUpdatedAtUtc?.ToString("O")), ("$unsaved",item.Target.HadUnsavedChanges), ("$resumeAt",item.LastResumeAtUtc?.ToString("O")),
                ("$result",item.LastResumeResult is null ? null : (int)item.LastResumeResult), ("$deleted",item.DeletedAtUtc?.ToString("O")) })
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        return new(active, deleted);
    }

    private static void CheckLegacy(string database, LegacyRows expected)
    {
        using var db = Open(database);
        using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
        Check((string?)check.ExecuteScalar() == "ok", "Legacy/backup integrity must remain valid.");
        Check(Scalar(db, "PRAGMA user_version") == 1 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 11, "Legacy schema and sequence must remain unchanged.");
        Check(Scalar(db, "SELECT count(*) FROM bookmarks") == 2 && Scalar(db, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='word_start'") == 0, "Legacy rows and schema must remain intact.");
        foreach (var item in new[] { expected.Active, expected.Deleted })
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT note,capture_sequence,deleted_at_utc FROM bookmarks WHERE id=$id";
            command.Parameters.AddWithValue("$id", item.Id.ToString()); using var row = command.ExecuteReader();
            Check(row.Read() && row.GetString(0) == item.Note && row.GetInt64(1) == item.CaptureSequence && row.IsDBNull(2) == (item.DeletedAtUtc is null), "Legacy note, sequence and deletion state must remain unchanged.");
        }
    }

    // This is an independent version 2 fixture; it never builds an old database by editing a version 3 one.
    private static IReadOnlyList<Bookmark> CreateVersionTwo(string database)
    {
        var created = DateTimeOffset.Parse("2026-09-01T01:02:03Z");
        var targets = new CapturedTarget[] { new(TargetKind.Folder, @"C:\synthetic\folder"), new(TargetKind.File, @"C:\synthetic\file.txt"),
            new(TargetKind.ExcelCell, @"C:\synthetic\book.xlsx", "Sheet1", "$B$2", true), Word(37), Slide(257, 5) with { HadUnsavedChanges = true },
            Pdf(3), Web("https://example.invalid/Case?query=%2f#part", "Preserved page title") };
        var rows = targets.Select((target, index) => new Bookmark(Guid.NewGuid(), target, PathPolicy.NormalizeLocation(target), PathPolicy.DisplayName(target),
            "version 2 note " + index, created, created.AddHours(index + 1), 11 + index, created.AddMinutes(index + 1),
            created.AddHours(index + 2), ResultCode.PositionRestoredFocusPending, index == 4 ? created.AddDays(1) : null)).ToList();
        using var db = Open(database);
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = @"CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL, kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 6), path TEXT NOT NULL,
 normalized_path TEXT COLLATE BINARY NOT NULL, sheet_name TEXT COLLATE BINARY, cell_address TEXT COLLATE BINARY,
 display_name TEXT NOT NULL, note TEXT NOT NULL, created_at_utc TEXT NOT NULL, captured_at_utc TEXT NOT NULL,
 capture_sequence INTEGER NOT NULL, note_updated_at_utc TEXT, had_unsaved_changes INTEGER, last_resume_at_utc TEXT,
 last_resume_result INTEGER, deleted_at_utc TEXT, word_start INTEGER, slide_id INTEGER, slide_number INTEGER, pdf_page INTEGER, page_title TEXT);
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind,normalized_path,ifnull(sheet_name,''),ifnull(cell_address,''),ifnull(word_start,-1),ifnull(slide_id,-1),ifnull(pdf_page,-1));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);
CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL);
INSERT INTO metadata VALUES('capture_sequence',23); PRAGMA user_version=2;";
            schema.ExecuteNonQuery();
        }
        foreach (var item in rows)
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO bookmarks VALUES($id,$kind,$path,$normalized,$sheet,$cell,$display,$note,$created,$captured,$sequence,$noteAt,$unsaved,$resumeAt,$result,$deleted,$word,$slide,$slideNumber,$pdf,$title)";
            foreach (var parameter in new (string Name, object? Value)[] {
                ("$id",item.Id.ToString()), ("$kind",(int)item.Target.Kind), ("$path",item.Target.Path), ("$normalized",item.NormalizedPath),
                ("$sheet",item.Target.SheetName), ("$cell",item.Target.CellAddress), ("$display",item.DisplayName), ("$note",item.Note),
                ("$created",item.CreatedAtUtc.ToString("O")), ("$captured",item.CapturedAtUtc.ToString("O")), ("$sequence",item.CaptureSequence),
                ("$noteAt",item.NoteUpdatedAtUtc?.ToString("O")), ("$unsaved",item.Target.HadUnsavedChanges), ("$resumeAt",item.LastResumeAtUtc?.ToString("O")),
                ("$result",item.LastResumeResult is null ? null : (int)item.LastResumeResult), ("$deleted",item.DeletedAtUtc?.ToString("O")),
                ("$word",item.Target.WordStart), ("$slide",item.Target.SlideId), ("$slideNumber",item.Target.SlideNumber), ("$pdf",item.Target.PdfPage), ("$title",item.Target.PageTitle) })
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        return rows;
    }

    private static string SnapshotVersionTwo(string database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM bookmarks ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    private static void CheckVersionTwo(string database, string before)
    {
        using var db = Open(database);
        using var integrity = db.CreateCommand(); integrity.CommandText = "PRAGMA quick_check";
        Check((string?)integrity.ExecuteScalar() == "ok", "Version 2 backup/rollback integrity remains valid.");
        Check(Scalar(db, "PRAGMA user_version") == 2 && Scalar(db, "SELECT value FROM metadata WHERE name='capture_sequence'") == 23,
            "Version 2 schema and sequence must remain unchanged.");
        Check(Scalar(db, "SELECT count(*) FROM pragma_table_info('bookmarks') WHERE name='text_offset'") == 0 &&
            Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='bookmarks_previous'") == 0, "Rollback cannot leak the new schema or temporary table.");
        Check(SnapshotVersionTwo(database) == before, "Every version 2 raw field must remain byte-for-byte equivalent after backup/rollback.");
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static long Scalar(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
