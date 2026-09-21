using WorkBookmark.Core;
using WorkBookmark.Storage;

internal static class OfficeUrlChecks
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-office-url-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        try
        {
            Run("HTTP/HTTPS Office targets retain their coordinates and original URL", Policy);
            Run("Office URL validation rejects credentials, handler injection and malformed input", Rejections);
            Run("Office IRI identity preserves document paths and URL state", Identity);
            Run("Office URI launch always selects the recorded application", Launch);
            Run("Office URLs, distinct positions, notes and resume results survive SQLite restart", Storage);
            Run("Office URL failed writes and file relinking preserve committed records", Rollback);
            Run("Office URL metadata survives the worker frame protocol", Protocol);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"OFFICE URL RESULT: {passed} passed; {failed} failed. No Office or authentication session used.");
        return failed == 0 ? 0 : 1;

        void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        void Storage()
        {
            string database = Path.Combine(root, "positions.db");
            var saved = new List<Bookmark>();
            using (var repo = new SqliteBookmarkRepository(database))
            {
                foreach (var target in Targets("https://OFFICE.invalid/Shared Documents/한글"))
                {
                    var first = repo.UpsertCapture(target).Bookmark;
                    repo.UpdateNote(first.Id, "계속할 작업");
                    repo.RecordResume(first.Id, ResultCode.OfficeResumePending);
                    var duplicate = repo.UpsertCapture(target with { Path = OfficeLocation.NormalizeUrl(target.Path), HadUnsavedChanges = true });
                    Check(duplicate.Bookmark.Id == first.Id && duplicate.ExistingNotePreserved && duplicate.Bookmark.Note == "계속할 작업", "IRI/escaped recapture lost its identity or note.");
                    Check(duplicate.Bookmark.DisplayName == OfficeLocation.DisplayName(target.Path) && duplicate.Bookmark.LastResumeResult == ResultCode.OfficeResumePending, "Display or pending resume result changed.");
                    Check(repo.UpsertCapture(OtherPosition(target)).Bookmark.Id != first.Id, "Distinct positions merged.");
                    Check(repo.UpsertCapture(target with { Path = target.Path + "?revision=2" }).Bookmark.Id != first.Id, "Query state merged.");
                    Check(repo.UpsertCapture(new(TargetKind.WebPage, target.Path.Replace(" ", "%20"), PageTitle: "Browser page")).Bookmark.Id != first.Id, "Browser and Office targets merged.");
                    repo.SoftDelete(first.Id);
                    var restored = repo.UpsertCapture(target);
                    Check(restored.RestoredDeleted && restored.Bookmark.Id == first.Id && restored.Bookmark.Note == "계속할 작업", "Recapture did not restore the note.");
                    saved.Add(restored.Bookmark);
                }
                Check(repo.List("계속할 작업").Items.Count == 3, "Office URL notes are not searchable.");
            }
            using var reopened = new SqliteBookmarkRepository(database);
            foreach (var expected in saved) Check(reopened.Get(expected.Id) == expected, "Office URL or position changed after restart.");
        }

        void Rollback()
        {
            bool fail = false;
            using var repo = new SqliteBookmarkRepository(Path.Combine(root, "rollback.db"), beforeCommit: operation =>
            {
                if (fail && operation == "capture") throw new IOException("synthetic failure");
            });
            var target = Targets("https://office.invalid/document")[0];
            var original = repo.UpsertCapture(target).Bookmark;
            fail = true;
            Fails(() => repo.UpsertCapture(target with { HadUnsavedChanges = true }), ResultCode.PersistenceFailed);
            Check(repo.Get(original.Id) == original, "Failed write changed committed URL state.");
            fail = false;
            Fails(() => repo.Relink(original.Id, target), ResultCode.InvalidRequest);
            Fails(() => repo.Relink(original.Id, target with { Path = @"C:\synthetic\other.xlsx" }), ResultCode.InvalidRequest);
            Check(repo.Get(original.Id) == original, "Relink silently changed a remote document into a local file.");
            Check(repo.UpsertCapture(OtherPosition(target)).Bookmark.CaptureSequence == original.CaptureSequence + 1, "Failed writes advanced sequence.");
        }
    }

    private static void Policy()
    {
        foreach (string prefix in new[] { "https://office.invalid/Shared Documents/한글", "http://intranet/document" })
        foreach (var target in Targets(prefix))
        {
            Check(PathPolicy.Validate(target) == target, "Native Office URL or coordinates changed.");
            Check(PathPolicy.NormalizeLocation(target) == OfficeLocation.NormalizeUrl(target.Path), "Repository identity used a file path.");
            Check(PathPolicy.Validate(target with { Path = "https://office.invalid/download?id=42&revision=3" }).Kind == target.Kind, "Document endpoints do not require a filename extension.");
            Fails(() => PathPolicy.Validate(target with { HadUnsavedChanges = null }));
            Fails(() => PathPolicy.Validate(target with { PageTitle = "mixed browser metadata" }));
        }
        Fails(() => PathPolicy.Validate(Targets("https://office.invalid/document")[0] with { CellAddress = "$XFE$1" }));
        Fails(() => PathPolicy.Validate(Targets("https://office.invalid/document")[1] with { WordStart = -1 }));
        Fails(() => PathPolicy.Validate(Targets("https://office.invalid/document")[2] with { SlideId = 0 }));
        foreach (var kind in new[] { TargetKind.File, TargetKind.Folder, TargetKind.PdfPage, TargetKind.NotepadPosition })
            Fails(() => PathPolicy.Validate(new(kind, "https://office.invalid/document.xlsx")));
        Fails(() => PathPolicy.Normalize("https://office.invalid/document.xlsx"));
    }

    private static void Rejections()
    {
        foreach (var target in Targets("https://office.invalid/document"))
        foreach (string bad in new[] {
            "https://user:password@office.invalid/doc", "https://user@office.invalid/doc", "https://", "https:doc", "https:/office.invalid/doc",
            "file:///C:/file.docx", "ftp://office.invalid/doc", "javascript:alert(1)", "ms-word:ofe|u|https://office.invalid/doc", "//office.invalid/doc",
            " https://office.invalid/doc", "https://office.invalid/doc ", "https://office.invalid/\tdoc", "https://office.invalid/doc\n",
            "https://office.invalid/doc|s|https://other.invalid/", "https://office.invalid/doc%7cs%7chttps://other.invalid/",
            "https://office.invalid/doc%0a", "https://office.invalid/doc%00", "https://office.invalid/doc%5cpath", "https://office.invalid/doc%22",
            "https://office.invalid/doc%", "https://office.invalid/doc%gg", "https://office.invalid/doc\\x", "https://office.invalid/\ud800",
            "https://office.invalid/" + new string('a', OfficeLocation.MaximumUrlLength)
        }) Fails(() => PathPolicy.Validate(target with { Path = bad }));
    }

    private static void Identity()
    {
        const string raw = "https://OFFICE.invalid:443/Shared Documents/한글.xlsx?b=2&a=%2f&a=1#Section%202";
        string canonical = OfficeLocation.NormalizeUrl(raw);
        Check(!canonical.Contains(' ') && canonical.StartsWith("https://office.invalid/Shared%20Documents/", StringComparison.Ordinal), "Native IRI not encoded for Office launching.");
        Check(canonical.EndsWith("?b=2&a=%2f&a=1#Section%202", StringComparison.Ordinal), "Query/fragment was decoded, reordered or discarded.");
        Check(OfficeLocation.NormalizeUrl(canonical) == canonical, "URL normalization is not idempotent.");
        Check(OfficeLocation.DisplayName(raw) == "한글.xlsx", "Display name includes escaped data or query tokens.");
        Check(OfficeLocation.DisplayName("https://office.invalid/?id=42") == "office.invalid", "Empty URL path has no display name.");
        foreach (string different in new[] { raw.Replace("Documents", "documents"), raw.Replace("b=2", "b=3"), raw.Replace("Section%202", "Section%203") })
            Check(OfficeLocation.NormalizeUrl(different) != canonical, "Distinct resource/state merged.");
        Check(OfficeLocation.NormalizeUrl("https://office.invalid/a%2Fb.xlsx") != OfficeLocation.NormalizeUrl("https://office.invalid/a/b.xlsx"), "Encoded path separator was decoded.");
    }

    private static void Launch()
    {
        string[] schemes = ["ms-excel", "ms-word", "ms-powerpoint"];
        var targets = Targets("https://office.invalid/한글 공백");
        for (int i = 0; i < targets.Length; i++)
            Check(OfficeLocation.LaunchUri(targets[i]) == schemes[i] + ":ofe|u|" + OfficeLocation.NormalizeUrl(targets[i].Path), "Wrong Office application or launch argument.");
        Fails(() => OfficeLocation.LaunchUri(new(TargetKind.WebPage, "https://office.invalid/", PageTitle: "page")));
        Fails(() => OfficeLocation.LaunchUri(targets[0] with { Path = @"C:\synthetic\document.xlsx" }));
    }

    private static void Protocol()
    {
        foreach (var target in Targets("https://office.invalid/Shared Documents/한글"))
        {
            var request = new WorkerRequest(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), Target: target);
            using var stream = new MemoryStream();
            FrameProtocol.WriteAsync(stream, request).GetAwaiter().GetResult(); stream.Position = 0;
            Check(FrameProtocol.ReadAsync<WorkerRequest>(stream).GetAwaiter().GetResult() == request, "Worker DTO lost remote location/coordinates.");
        }
    }

    private static CapturedTarget[] Targets(string prefix) => [
        new(TargetKind.ExcelCell, prefix + ".xlsx", "확정자", "$D$127", false),
        new(TargetKind.WordPosition, prefix + ".docx", HadUnsavedChanges: false, WordStart: 37),
        new(TargetKind.PowerPointSlide, prefix + ".pptx", HadUnsavedChanges: false, SlideId: 257, SlideNumber: 2)
    ];
    private static CapturedTarget OtherPosition(CapturedTarget target) => target.Kind switch
    {
        TargetKind.ExcelCell => target with { CellAddress = "$F$42" },
        TargetKind.WordPosition => target with { WordStart = 38 },
        _ => target with { SlideId = 258, SlideNumber = 3 }
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, ResultCode code = ResultCode.UnsupportedTarget)
    {
        try { action(); }
        catch (BookmarkException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
