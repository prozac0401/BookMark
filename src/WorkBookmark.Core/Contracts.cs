namespace WorkBookmark.Core;

public enum TargetKind { Folder, File, ExcelCell, WordPosition, PowerPointSlide, PdfPage, WebPage, NotepadPosition, NotepadSnapshot }
public enum Operation { Capture, Resume, ValidateRelink }
public enum ResultCode { Captured, CaptureCommitted, UnsupportedTarget, AmbiguousTarget, ContextChanged, MultipleSelection, UnsavedWorkbook, AppBusy, CaptureTimedOut, PersistenceFailed, OpenRequested, RevealRequested, PositionRestored, PositionRestoredFocusPending, OpenedPositionFailed, ResumeOutcomeUnknown, TargetUnavailable, InvalidRequest, Cancelled, Validated, EnumerationIncomplete, DuplicateTarget, UnsavedDocument, BrowserExtensionRequired, NotepadFileRequired, NotepadOpenRequested, OfficeResumePending, OfficeDocumentOpened, BrowserAddressUnavailable }
public sealed record CapturedTarget(TargetKind Kind, string Path, string? SheetName = null, string? CellAddress = null, bool? HadUnsavedChanges = null, int? WordStart = null, int? SlideId = null, int? SlideNumber = null, int? PdfPage = null, string? PageTitle = null, int? TextOffset = null, string? TextContent = null, int? TextSelectionEnd = null, string? SnapshotTitle = null);
public sealed record TargetSnapshot(long Hwnd, uint ProcessId, long ProcessStartTimeUtcTicks = 0, long FocusHwnd = 0, uint InputTick = 0, long ActiveViewHwnd = 0);
public sealed record NotepadObservation(string ContentFingerprint, int TextOffset, int SelectionEnd, bool HadUnsavedChanges);
public sealed record WorkerRequest(int ProtocolVersion, Guid RequestId, Operation Operation, DateTimeOffset DeadlineUtc, TargetSnapshot? Snapshot = null, CapturedTarget? Target = null, NotepadObservation? ExpectedNotepad = null, bool MonitorInput = false);
public sealed record WorkerResponse(int ProtocolVersion, Guid RequestId, ResultCode Code, CapturedTarget? Target = null, bool ExternalActionStarted = false, long TargetHwnd = 0, NotepadObservation? NotepadObservation = null);
public sealed record Bookmark(Guid Id, CapturedTarget Target, string NormalizedPath, string DisplayName, string Note, DateTimeOffset CreatedAtUtc, DateTimeOffset CapturedAtUtc, long CaptureSequence, DateTimeOffset? NoteUpdatedAtUtc, DateTimeOffset? LastResumeAtUtc, ResultCode? LastResumeResult, DateTimeOffset? DeletedAtUtc);
public sealed record CaptureCommit(Bookmark Bookmark, bool ExistingNotePreserved, bool RestoredDeleted);
public sealed record SearchResults(IReadOnlyList<Bookmark> Items, bool HasMore);
/// <summary>Sticker position and expanded size in 96-DPI logical units relative to the monitor working area.</summary>
public sealed record StickerLayout(Guid BookmarkId, string MonitorDevice, int Left, int Top, int Width, int Height, bool IsCollapsed = false, bool AlwaysOnTop = false);
public interface IBookmarkRepository : IDisposable {
 CaptureCommit UpsertCapture(CapturedTarget target);
 SearchResults List(string query = "");
 IReadOnlyList<Bookmark> ListActive() => List().Items;
 IReadOnlyList<Bookmark> ListDeleted(int limit = 100) => [];
 IReadOnlyList<StickerLayout> GetStickerLayouts() => [];
 void SaveStickerLayout(StickerLayout layout) => throw new BookmarkException(ResultCode.InvalidRequest);
 Bookmark? Get(Guid id);
 void UpdateNote(Guid id, string note);
 void SoftDelete(Guid id);
 void Restore(Guid id);
 void RecordResume(Guid id, ResultCode result);
 void Relink(Guid id, CapturedTarget validatedTarget);
}
public sealed class BookmarkException(ResultCode code) : Exception(code.ToString()) { public ResultCode Code { get; } = code; }
