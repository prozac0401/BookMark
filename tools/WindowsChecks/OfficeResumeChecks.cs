using WorkBookmark.Core;
using WorkBookmark.Windows;

// Deterministic outcome/cadence checks: no Office automation, hooks, user input or network requests.
internal static class OfficeResumeChecks
{
    internal static int Run()
    {
        int passed = 0;
        void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name); passed++;
        }

        foreach (var target in new CapturedTarget[] {
            new(TargetKind.ExcelCell, @"C:\synthetic\document.xlsx", "Sheet1", "$D$127", false),
            new(TargetKind.WordPosition, @"C:\synthetic\document.docx", HadUnsavedChanges: false, WordStart: 37),
            new(TargetKind.PowerPointSlide, @"C:\synthetic\document.pptx", HadUnsavedChanges: false, SlideId: 257, SlideNumber: 2),
            new(TargetKind.ExcelCell, "https://office.invalid/document.xlsx", "Sheet1", "$D$127", false),
            new(TargetKind.WordPosition, "https://office.invalid/document.docx", HadUnsavedChanges: false, WordStart: 37),
            new(TargetKind.PowerPointSlide, "https://office.invalid/document.pptx", HadUnsavedChanges: false, SlideId: 257, SlideNumber: 2)
        })
        {
            string name = target.Kind + (OfficeLocation.IsWebTarget(target) ? " web" : " local");
            var now = DateTimeOffset.UtcNow;
            RequestContext Context(Operation operation = Operation.Resume) =>
                new(new(1, Guid.NewGuid(), operation, now.AddSeconds(45), Target: target));
            var context = Context();
            var recovery = new OfficeWebRecovery(target, context, _ => throw new InvalidOperationException("No network"), () => now);
            OfficeDocumentAccess.Open(target, context, _ => 33);
            recovery.Opened();
            recovery.Observed(0);
            var pending = OfficeLocation.IsWebTarget(target) ? ResultCode.OfficeResumePending : ResultCode.ResumeOutcomeUnknown;
            Assert(!context.DocumentObserved && !context.PositionVerified && context.ResumeFailure(ResultCode.Cancelled) == pending,
                name + " an accepted Shell launch or missing HWND does not claim the document opened");

            recovery.Observed(321);
            Assert(context.DocumentObserved && !context.PositionVerified && context.TargetHwnd == 321 &&
                context.ResumeFailure(ResultCode.Cancelled) == ResultCode.OfficeDocumentOpened &&
                context.ResumeFailure(ResultCode.ContextChanged) == ResultCode.OfficeDocumentOpened &&
                context.ResumeFailure(ResultCode.ResumeOutcomeUnknown) == ResultCode.OfficeDocumentOpened,
                name + " verified document identity survives a click, focus change or later timeout");
            Assert(context.ResumeFailure(ResultCode.OpenedPositionFailed) == ResultCode.OpenedPositionFailed,
                name + " an actual position failure retains its precise result");

            context.PositionVerified = true;
            Assert(context.ResumeFailure(ResultCode.Cancelled) == ResultCode.PositionRestoredFocusPending &&
                context.ResumeFailure(ResultCode.ContextChanged) == ResultCode.PositionRestoredFocusPending &&
                recovery.IncompleteResult == ResultCode.PositionRestoredFocusPending,
                name + " verified position survives focus acquisition interruption");

            var existing = Context();
            new OfficeWebRecovery(target, existing).Observed(654);
            Assert(!existing.ExternalActionStarted && existing.ResumeFailure(ResultCode.Cancelled) == ResultCode.OfficeDocumentOpened,
                name + " an already open exact document is confirmed without a new activation");

            var validation = Context(Operation.ValidateRelink);
            validation.DocumentObserved = true;
            validation.PositionVerified = true;
            Assert(validation.ResumeFailure(ResultCode.ContextChanged) == ResultCode.ContextChanged,
                name + " relink validation cannot turn an interrupted check into resume success");

            var cadenceContext = Context();
            var cadence = new OfficeWebRecovery(target, cadenceContext, now: () => now);
            Assert(cadence.ObservationIntervalMilliseconds == 250, name + " initial document detection has a short bounded interval");
            int observations = 0;
            var until = now.AddSeconds(45);
            while (now < until)
            {
                observations++;
                now = now.AddMilliseconds(cadence.ObservationIntervalMilliseconds);
            }
            Assert(observations <= 100 && cadence.ObservationIntervalMilliseconds == 500,
                name + " a 45-second wait performs at most 100 full Office inventories");
        }
        return passed;
    }
}
