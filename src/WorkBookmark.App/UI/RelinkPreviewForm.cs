using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class RelinkPreviewForm : Form
{
    public RelinkPreviewForm(Bookmark original, CapturedTarget candidate)
    {
        UiStyle.Apply(this); Text = "경로 변경 확인";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(610, 320);
        Controls.Add(new Label { Text = "아래 경로로 책갈피를 변경합니다.", Location = new Point(16, 14), Size = new Size(570, 28) });
        string position = candidate.Kind == TargetKind.ExcelCell ? $"기존 시트·셀 유지: {candidate.SheetName}!{candidate.CellAddress}" : "대상 종류 유지: " + (candidate.Kind == TargetKind.Folder ? "폴더" : "파일");
        Controls.Add(new TextBox { Text = $"새 경로: {candidate.Path}\r\n\r\n{position}\r\n\r\n기존 메모: {original.Note}", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 49), Size = new Size(578, 204) });
        var apply = new Button { Text = "적용", DialogResult = DialogResult.OK, Location = new Point(409, 271), Size = new Size(82, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(508, 271), Size = new Size(82, 30) };
        Controls.AddRange([apply, cancel]); CancelButton = cancel;
        UiStyle.Position(this);
    }
}
