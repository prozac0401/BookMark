namespace WorkBookmark.Core;

public enum TargetKind { Folder, File, ExcelCell, WordPosition, PowerPointSlide, PdfPage, WebPage, NotepadPosition, NotepadSnapshot }
public enum Operation { Capture, Resume, ValidateRelink }
public enum ResultCode { Captured, CaptureCommitted, UnsupportedTarget, AmbiguousTarget, ContextChanged, MultipleSelection, UnsavedWorkbook, AppBusy, CaptureTimedOut, PersistenceFailed, OpenRequested, RevealRequested, PositionRestored, PositionRestoredFocusPending, OpenedPositionFailed, ResumeOutcomeUnknown, TargetUnavailable, InvalidRequest, Cancelled, Validated, EnumerationIncomplete, DuplicateTarget, UnsavedDocument, BrowserExtensionRequired, NotepadFileRequired, NotepadOpenRequested, OfficeResumePending }
public sealed record CapturedTarget(TargetKind Kind, string Path, string? SheetName = null, string? CellAddress = null, bool? HadUnsavedChanges = null, int? WordStart = null, int? SlideId = null, int? SlideNumber = null, int? PdfPage = null, string? PageTitle = null, int? TextOffset = null, string? TextContent = null, int? TextSelectionEnd = null, string? SnapshotTitle = null);
public sealed record TargetSnapshot(long Hwnd, uint ProcessId, long ProcessStartTimeUtcTicks = 0, long FocusHwnd = 0, uint InputTick = 0, long ActiveViewHwnd = 0);
public sealed record NotepadObservation(string ContentFingerprint, int TextOffset, int SelectionEnd, bool HadUnsavedChanges);
public sealed record WorkerRequest(int ProtocolVersion, Guid RequestId, Operation Operation, DateTimeOffset DeadlineUtc, TargetSnapshot? Snapshot = null, CapturedTarget? Target = null, NotepadObservation? ExpectedNotepad = null);
public sealed record WorkerResponse(int ProtocolVersion, Guid RequestId, ResultCode Code, CapturedTarget? Target = null, bool ExternalActionStarted = false, long TargetHwnd = 0, NotepadObservation? NotepadObservation = null);
public sealed record Bookmark(Guid Id, CapturedTarget Target, string NormalizedPath, string DisplayName, string Note, DateTimeOffset CreatedAtUtc, DateTimeOffset CapturedAtUtc, long CaptureSequence, DateTimeOffset? NoteUpdatedAtUtc, DateTimeOffset? LastResumeAtUtc, ResultCode? LastResumeResult, DateTimeOffset? DeletedAtUtc);
public sealed record CaptureCommit(Bookmark Bookmark, bool ExistingNotePreserved, bool RestoredDeleted);
public sealed record SearchResults(IReadOnlyList<Bookmark> Items, bool HasMore);
public interface IBookmarkRepository : IDisposable {
 CaptureCommit UpsertCapture(CapturedTarget target);
 SearchResults List(string query = "");
 Bookmark? Get(Guid id);
 void UpdateNote(Guid id, string note);
 void SoftDelete(Guid id);
 void Restore(Guid id);
 void RecordResume(Guid id, ResultCode result);
 void Relink(Guid id, CapturedTarget validatedTarget);
}
public sealed class BookmarkException(ResultCode code) : Exception(code.ToString()) { public ResultCode Code { get; } = code; }
