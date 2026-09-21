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
            Assert(new OfficeWebRecovery(target, Context(target, .5)).NearDeadline &&
                !new OfficeWebRecovery(target, Context(target, 10)).NearDeadline,
                target.Kind + " authentication/loading is observed until the final response allowance");
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

        var remote = new CapturedTarget(TargetKind.WordPosition, "https://office.invalid:8443/sites/private/document.docx?token=private#position", HadUnsavedChanges: false, WordStart: 37);
        var retryFailure = Context(remote);
        OfficeDocumentAccess.Open(remote, retryFailure, _ => 33);
        Assert(Refused(() => OfficeDocumentAccess.Open(remote, retryFailure, _ => 31), ResultCode.TargetUnavailable) && retryFailure.ExternalActionStarted,
            "a failed retry preserves the fact that the initial Office open request was accepted");
        Assert(OfficeOriginWarmUp.Origin(remote.Path).AbsoluteUri == "https://office.invalid:8443/",
            "authentication warm-up strips the document path, query and fragment while retaining its origin port");
        Assert(Refused(() => OfficeOriginWarmUp.Origin("https://user:password@office.invalid/doc.docx"), ResultCode.UnsupportedTarget),
            "authentication warm-up rejects embedded credentials before any network boundary");
        Assert(OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(0, 3) && OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(0x20000, 1),
            "automatic Windows logon honors explicit silent-logon or conditional intranet policy");
        Assert(!OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(0x20000, 3) && !OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(0x10000, 1) &&
            !OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(0x30000, 1) && !OfficeOriginWarmUp.PolicyAllowsAutomaticLogon(123, 1),
            "Internet-zone conditional logon, prompts, anonymous-only and unknown policies cannot release credentials");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var recoveryContext = new RequestContext(new(1, Guid.NewGuid(), Operation.Resume, now.AddSeconds(45), Target: remote));
        var warmUp = new TaskCompletionSource();
        int visits = 0, retries = 0, interactionChecks = 0;
        Uri? visited = null;
        var recovery = new OfficeWebRecovery(remote, recoveryContext, origin => { visits++; visited = origin; return warmUp.Task; }, () => now);
        void Continue(bool absent) => recovery.Recover(absent, () => interactionChecks++, () => retries++);
        Continue(true);
        Assert(visits == 0 && retries == 0, "no origin visit or retry is performed before an initial Office launch");
        recovery.Opened(); now = now.AddSeconds(3); Continue(true);
        Assert(visits == 0, "ordinary Office startup gets an initial grace period without authentication requests");
        now = now.AddSeconds(1); Continue(false); Continue(false);
        Assert(visits == 1 && retries == 0 && visited?.AbsoluteUri == "https://office.invalid:8443/",
            "one background root visit starts even while Office inventory is temporarily incomplete");
        Continue(true);
        Assert(retries == 0, "Office is never reopened while authentication warm-up is still running");
        warmUp.SetResult(); Continue(false);
        Assert(retries == 0, "completed warm-up does not turn incomplete Office inventory into proven absence");
        Continue(true); Continue(true);
        Assert(visits == 1 && retries == 1 && interactionChecks == 1,
            "complete absence after warm-up permits exactly one input-guarded Office retry");
        recovery.Observed(0);
        Assert(recovery.IncompleteResult == ResultCode.OfficeResumePending, "a process without an exact document window never claims an opened document");
        recovery.Observed(321);
        Assert(recovery.IncompleteResult == ResultCode.OfficeDocumentOpened && recoveryContext.TargetHwnd == 321,
            "an exact observed document reports opened separately from unconfirmed position restoration");

        var observedRecovery = new OfficeWebRecovery(remote, Context(remote, 45), _ => { visits++; return Task.CompletedTask; }, () => now);
        observedRecovery.Opened(); observedRecovery.Observed(321); now = now.AddSeconds(5);
        int beforeObserved = visits;
        observedRecovery.Recover(true, () => interactionChecks++, () => retries++);
        Assert(visits == beforeObserved && retries == 1, "an observed exact document suppresses warm-up and duplicate opens");

        int cancelledRetries = 0, cancelledVisits = 0;
        now = DateTimeOffset.UtcNow;
        var cancelledRecovery = new OfficeWebRecovery(remote, Context(remote, 45), _ => { cancelledVisits++; return Task.CompletedTask; }, () => now);
        cancelledRecovery.Opened(); now = now.AddSeconds(5);
        cancelledRecovery.Recover(true, () => throw new BookmarkException(ResultCode.Cancelled), () => cancelledRetries++);
        cancelledRecovery.Recover(true, () => { }, () => cancelledRetries++);
        cancelledRecovery.Observed(456);
        Assert(cancelledVisits == 1 && cancelledRetries == 0 && cancelledRecovery.IncompleteResult == ResultCode.OfficeDocumentOpened,
            "login/user input permanently prevents retries while read-only observation can still confirm the document");

        int localVisits = 0;
        var localRecovery = new OfficeWebRecovery(local, Context(local), _ => { localVisits++; return Task.CompletedTask; }, () => now);
        localRecovery.Opened(); now = now.AddSeconds(5); localRecovery.Recover(true, () => { }, () => localVisits++);
        Assert(localVisits == 0 && !localRecovery.NearDeadline, "local Office documents never invoke web authentication recovery");

        now = DateTimeOffset.UtcNow;
        int lateVisits = 0;
        var lateRecovery = new OfficeWebRecovery(remote, Context(remote, 10), _ => { lateVisits++; return Task.CompletedTask; }, () => now);
        lateRecovery.Opened(); now = now.AddSeconds(5); lateRecovery.Recover(true, () => { }, () => lateVisits++);
        Assert(lateVisits == 0, "insufficient remaining budget cannot start a root visit or delayed retry");

        int failedVisits = 0, afterFailureRetries = 0;
        var failedRecovery = new OfficeWebRecovery(remote, Context(remote, 45), _ => { failedVisits++; return Task.FromException(new IOException("synthetic")); }, () => now);
        failedRecovery.Opened(); now = now.AddSeconds(5);
        failedRecovery.Recover(true, () => { }, () => afterFailureRetries++);
        failedRecovery.Recover(true, () => { }, () => afterFailureRetries++);
        Assert(failedVisits == 1 && afterFailureRetries == 1, "a failed root request stays bounded and is not confused with document success");
        Assert(failedRecovery.ShouldObserveAfter(new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x80010001))) &&
            failedRecovery.ShouldObserveAfter(new System.Reflection.TargetInvocationException(new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x8001010A)))) &&
            !failedRecovery.ShouldObserveAfter(new IOException()) && !localRecovery.ShouldObserveAfter(new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x80010001))),
            "only transient web Office COM-busy responses are retried observationally");

        now = DateTimeOffset.UtcNow;
        int reads = 0, waits = 0;
        Assert(OfficeDocumentAccess.VerifyPosition(remote, Context(remote), () => ++reads == 3, () => now,
            () => { waits++; now = now.AddMilliseconds(125); }) && reads == 3 && waits == 2,
            "delayed Office position metadata is observed until it confirms the requested position");
        reads = 0; waits = 0;
        Assert(!OfficeDocumentAccess.VerifyPosition(remote, Context(remote), () => { reads++; return false; }, () => now,
            () => { waits++; now = now.AddMilliseconds(500); }) && reads == 5 && waits == 4,
            "position verification has a two-second bound and never claims an unchanged position succeeded");
        reads = 0;
        Assert(OfficeDocumentAccess.VerifyPosition(remote, Context(remote), () => { if (++reads == 1) throw new BookmarkException(ResultCode.ContextChanged); return true; }, () => now,
            () => now = now.AddMilliseconds(125)) && reads == 2,
            "transient ActiveWindow/Selection changes after web navigation are observed again");
        reads = 0;
        Assert(!OfficeDocumentAccess.VerifyPosition(local, Context(local), () => { reads++; return false; }, () => now,
            () => throw new InvalidOperationException("local must not wait")) && reads == 1,
            "local position verification retains its existing immediate result");
        Guid requestId = Guid.NewGuid();
        using (var signal = ResumeInputSignal.Create(requestId))
        {
            signal.MarkInput(); // An editing/login key press occurred before the worker installed its hooks.
            using var guarded = new ResumeGuard(null, requestId);
            Assert(Refused(guarded.CheckForNewInput, ResultCode.Cancelled),
                "input latched before worker startup prevents any automatic Office retry");
            Assert(Refused(guarded.Check, ResultCode.Cancelled),
                "input latched before worker startup also prevents position navigation and focus changes");
        }
        Assert(Refused(() => { using var guard = new ResumeGuard(null, Guid.NewGuid()); }, ResultCode.Cancelled),
            "a missing requested input signal fails closed before Office automation starts");
        Console.WriteLine($"RESULT: {passed} Office URL adapter checks passed. AD/Office desktop acceptance remains manual.");
        return 0;
    }
}
