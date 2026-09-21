using WorkBookmark.Core;
using WorkBookmark.Windows;

// Injected filesystem/Shell boundaries: no Office processes, web requests, credentials or user documents.
internal static class OfficeUrlAdapterChecks
{
    internal static int Run()
    {
        int passed = 0;
        void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        bool Refused(Action action, ResultCode code)
        {
            try { action(); return false; }
            catch (BookmarkException error) when (error.Code == code) { return true; }
        }
        RequestContext Context(CapturedTarget target, double seconds = 10) =>
            new(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(seconds), Target: target));

        foreach (var target in new CapturedTarget[] {
            new(TargetKind.ExcelCell, "https://office.invalid/Shared Documents/같은이름.xlsx", "Sheet1", "$D$127", false),
            new(TargetKind.WordPosition, "https://office.invalid/Shared Documents/같은이름.docx", HadUnsavedChanges: false, WordStart: 37),
            new(TargetKind.PowerPointSlide, "https://office.invalid/Shared Documents/같은이름.pptx", HadUnsavedChanges: false, SlideId: 257, SlideNumber: 2)
        })
        {
            int probes = 0;
            OfficeDocumentAccess.CheckExists(target, _ => { probes++; throw new IOException("Must not probe remote resources as files."); });
            var matcher = new OfficeDocumentMatcher(target.Kind, target.Path, _ => { probes++; throw new IOException("Must not read web file identities."); });
            Assert(matcher.Match(OfficeLocation.NormalizeUrl(target.Path)) == OfficeDocumentMatch.Exact && probes == 0,
                target.Kind + " matches the opened URL/IRI without filesystem or authentication probes");
            Assert(matcher.Match(target.Path.Replace("Shared Documents", "other")) == OfficeDocumentMatch.Different &&
                matcher.Match(target.Path + "?revision=2") == OfficeDocumentMatch.Different &&
                matcher.Match(@"C:\cache\" + OfficeLocation.DisplayName(target.Path)) == OfficeDocumentMatch.Different && probes == 0,
                target.Kind + " never confuses same-name documents, URL states or local caches");
            Assert(matcher.Match("https://office.invalid/bad%0a") == OfficeDocumentMatch.Uncertain,
                target.Kind + " invalid observed URL is not treated as proven absence");

            int launches = 0; string? launch = null;
            var context = Context(target);
            OfficeDocumentAccess.Open(target, context, value => { launches++; launch = value; return 33; });
            Assert(launches == 1 && launch == OfficeLocation.LaunchUri(target) && context.ExternalActionStarted,
                target.Kind + " requests the correct Office handler exactly once");
            var failed = Context(target);
            Assert(Refused(() => OfficeDocumentAccess.Open(target, failed, _ => 31), ResultCode.TargetUnavailable) && !failed.ExternalActionStarted,
                target.Kind + " missing handler is reported as failure without a claimed open");
            int invalidLaunches = 0;
            Assert(Refused(() => OfficeDocumentAccess.Open(target with { Path = "https://office.invalid/doc|s|payload" }, Context(target), _ => { invalidLaunches++; return 33; }), ResultCode.UnsupportedTarget) && invalidLaunches == 0,
                target.Kind + " invalid URL never reaches Shell");
            int lateLaunches = 0;
            Assert(Refused(() => OfficeDocumentAccess.Open(target, Context(target, -1), _ => { lateLaunches++; return 33; }), ResultCode.Cancelled) && lateLaunches == 0,
                target.Kind + " expired request cannot start an Office open");
            Assert(!OfficeDocumentAccess.WaitingForWebDocument(target, false, Context(target, 1)) &&
                OfficeDocumentAccess.WaitingForWebDocument(target, true, Context(target, 1)) &&
                !OfficeDocumentAccess.WaitingForWebDocument(target, true, Context(target)),
                target.Kind + " incomplete authentication/loading has a bounded pending result");
        }

        var local = new CapturedTarget(TargetKind.ExcelCell, @"C:\synthetic\document.xlsx", "Sheet1", "$A$1", false);
        int fileChecks = 0;
        OfficeDocumentAccess.CheckExists(local, t => { if (t == local) fileChecks++; });
        Assert(fileChecks == 1, "local Office documents still require the filesystem existence check");
        var localMatcher = new OfficeDocumentMatcher(local.Kind, local.Path, p => p.Contains("alias", StringComparison.Ordinal) ? "original" : p == local.Path ? "original" : "different");
        Assert(localMatcher.Match(local.Path) == OfficeDocumentMatch.Exact && localMatcher.Match(@"C:\synthetic\other.xlsx") == OfficeDocumentMatch.Different &&
            localMatcher.Match(@"D:\alias.xlsx") == OfficeDocumentMatch.Uncertain && localMatcher.Match(@"c:\synthetic\document.xlsx") == OfficeDocumentMatch.Uncertain,
            "local exact paths and conservative file-alias decisions are preserved");
        Assert(localMatcher.Match("https://office.invalid/document.xlsx") == OfficeDocumentMatch.Different,
            "an unrelated open web document does not block local resume");
        Assert(Refused(() => OfficeDocumentAccess.CheckExists(local, _ => throw new BookmarkException(ResultCode.TargetUnavailable)), ResultCode.TargetUnavailable),
            "missing local file is not silently accepted");
        Assert(OfficeDocumentAccess.Normalize(TargetKind.ExcelCell, @"\\server\share\book.xlsx") == @"\\server\share\book.xlsx" &&
            Refused(() => OfficeDocumentAccess.Normalize(TargetKind.WordPosition, @"\\server\share\word.docx"), ResultCode.UnsupportedTarget) &&
            Refused(() => OfficeDocumentAccess.Normalize(TargetKind.PowerPointSlide, @"\\server\share\slides.pptx"), ResultCode.UnsupportedTarget),
            "existing Excel UNC and Word/PowerPoint local-only filesystem scope is unchanged");
        Console.WriteLine($"RESULT: {passed} Office URL adapter checks passed. AD/Office desktop acceptance remains manual.");
        return 0;
    }
}
