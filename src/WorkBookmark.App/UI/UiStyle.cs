using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal static class UiStyle
{
    public static readonly Color Ink = Color.FromArgb(32, 43, 53);
    public static readonly Color Muted = Color.FromArgb(92, 104, 111);
    public static readonly Color Accent = Color.FromArgb(24, 109, 100);
    public static readonly Color Surface = Color.FromArgb(248, 250, 249);
    public static void Apply(Form form)
    {
        form.Font = new Font("맑은 고딕", 10F);
        form.ForeColor = Ink; form.BackColor = Surface;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Shown += (_, _) => Position(form);
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.Icon = SystemIcons.Application;
    }
    public static void Position(Form form)
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.Location = new Point(area.Right - form.Width - 20, area.Bottom - form.Height - 20);
    }
    public static string PositionLabel(CapturedTarget target) => target.Kind switch
    {
        TargetKind.ExcelCell => $" · {target.SheetName}!{target.CellAddress}",
        TargetKind.WordPosition => $" · 본문 문자 위치 {target.WordStart + 1}",
        TargetKind.PowerPointSlide => $" · 슬라이드 {target.SlideNumber} (저장 당시)",
        TargetKind.PdfPage => $" · {target.PdfPage}쪽",
        TargetKind.NotepadPosition => $" · 메모장 문자 위치 {target.TextOffset + 1}",
        TargetKind.NotepadSnapshot => target.TextSelectionEnd > target.TextOffset
            ? $" · 메모장 선택 {target.TextOffset + 1}–{target.TextSelectionEnd}"
            : $" · 메모장 문자 위치 {target.TextOffset + 1}",
        _ => ""
    };
    public static string Location(Bookmark bookmark) => bookmark.Target.Kind == TargetKind.NotepadSnapshot
        ? PositionLabel(bookmark.Target).Trim(' ', '·') + "   자동 보관한 내용 · 원본 파일 저장 불필요"
        : PositionLabel(bookmark.Target).Trim(' ', '·') + (PositionLabel(bookmark.Target).Length > 0 ? "   " : "") + bookmark.Target.Path;
    public static string Result(ResultCode code) => code switch
    {
        ResultCode.CaptureCommitted => "책갈피를 남겼습니다.",
        ResultCode.UnsupportedTarget => "이 화면의 작업 위치는 아직 지원하지 않습니다.",
        ResultCode.BrowserExtensionRequired or ResultCode.BrowserAddressUnavailable => "주소 표시줄의 전체 URL을 확인하지 못했습니다. 페이지 본문을 선택하고 다시 저장하거나 ‘URL 입력’으로 남길 수 있습니다.",
        ResultCode.NotepadFileRequired => "메모장 캡처 요청을 완료하지 못했습니다. 최신 앱을 다시 실행해 주세요.",
        ResultCode.NotepadOpenRequested => "이전 메모장 파일 책갈피의 열기를 요청했습니다. 이 항목은 내용 보관본이 아닙니다.",
        ResultCode.AmbiguousTarget or ResultCode.ContextChanged => "현재 작업 위치를 정확히 확인하지 못했습니다. 다시 눌러 주세요.",
        ResultCode.MultipleSelection => "파일이나 폴더를 하나만 선택해 주세요.",
        ResultCode.UnsavedWorkbook or ResultCode.UnsavedDocument => "파일로 저장한 뒤 책갈피를 남겨 주세요.",
        ResultCode.AppBusy => "편집이나 대화상자를 마친 뒤 다시 눌러 주세요.",
        ResultCode.CaptureTimedOut => "위치를 확인하지 못해 책갈피를 남기지 않았습니다.",
        ResultCode.PersistenceFailed => "책갈피를 저장하지 못했습니다. 기존 기록은 유지됩니다.",
        ResultCode.OpenRequested => "열기를 요청했습니다.",
        ResultCode.RevealRequested => "파일 위치 표시를 요청했습니다.",
        ResultCode.PositionRestored => "기록한 위치로 이동했습니다.",
        ResultCode.PositionRestoredFocusPending => "기록한 위치로 이동했습니다. 문서는 대상 앱에 열려 있습니다.",
        ResultCode.OpenedPositionFailed => "파일은 열렸지만 기록한 위치로 이동하지 못했습니다.",
        ResultCode.ResumeOutcomeUnknown => "처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요.",
        ResultCode.OfficeResumePending => "문서 열기를 요청했습니다. 로그인이 필요하면 사이트에 로그인한 뒤 책갈피를 다시 실행해 주세요.",
        ResultCode.OfficeDocumentOpened => "문서가 열려 있습니다. 저장한 위치로의 이동은 완료되지 않았습니다.",
        ResultCode.TargetUnavailable => "저장한 대상에 접근할 수 없습니다.",
        ResultCode.EnumerationIncomplete => "열린 문서를 모두 확인하지 못했습니다. 대상 앱을 확인해 주세요.",
        ResultCode.DuplicateTarget => "같은 위치의 책갈피가 이미 있습니다. 기존 기록은 유지됩니다.",
        ResultCode.Cancelled => "요청을 중단했습니다.",
        _ => "요청을 완료하지 못했습니다. 잠시 후 다시 시도해 주세요."
    };
}

internal sealed class ImeTextBox : TextBox
{
    private bool _composing;
    private long _endedAt;
    public event Action? CompositionEnded;
    public bool IsComposing => _composing;
    public bool IgnoreSubmit => _composing || Environment.TickCount64 - _endedAt < 120;
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x010D) _composing = true;
        if (message.Msg == 0x010E) { _composing = false; _endedAt = Environment.TickCount64; }
        base.WndProc(ref message);
        if (message.Msg == 0x010E) CompositionEnded?.Invoke();
    }
}
