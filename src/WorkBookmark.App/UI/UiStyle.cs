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
    public static string Location(Bookmark bookmark) => bookmark.Target.Kind == TargetKind.ExcelCell
        ? $"{bookmark.Target.SheetName} · {bookmark.Target.CellAddress}   {bookmark.Target.Path}" : bookmark.Target.Path;
    public static string Result(ResultCode code) => code switch
    {
        ResultCode.CaptureCommitted => "책갈피를 남겼습니다.",
        ResultCode.UnsupportedTarget => "이 화면의 작업 위치는 아직 지원하지 않습니다.",
        ResultCode.AmbiguousTarget or ResultCode.ContextChanged => "현재 작업 위치를 정확히 확인하지 못했습니다. 다시 눌러 주세요.",
        ResultCode.MultipleSelection => "파일이나 폴더를 하나만 선택해 주세요.",
        ResultCode.UnsavedWorkbook => "파일로 저장한 뒤 책갈피를 남겨 주세요.",
        ResultCode.AppBusy => "편집이나 대화상자를 마친 뒤 다시 눌러 주세요.",
        ResultCode.CaptureTimedOut => "위치를 확인하지 못해 책갈피를 남기지 않았습니다.",
        ResultCode.PersistenceFailed => "책갈피를 저장하지 못했습니다. 기존 기록은 유지됩니다.",
        ResultCode.OpenRequested => "열기를 요청했습니다.",
        ResultCode.RevealRequested => "파일 위치 표시를 요청했습니다.",
        ResultCode.PositionRestored => "기록한 셀로 이동했습니다.",
        ResultCode.PositionRestoredFocusPending => "셀로 이동했습니다. Excel 창을 선택해 주세요.",
        ResultCode.OpenedPositionFailed => "파일은 열렸지만 기록한 셀로 이동하지 못했습니다.",
        ResultCode.ResumeOutcomeUnknown => "처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요.",
        ResultCode.TargetUnavailable => "파일 또는 폴더에 접근할 수 없습니다.",
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
