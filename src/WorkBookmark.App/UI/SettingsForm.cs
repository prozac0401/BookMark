using System.Drawing;
using System.Windows.Forms;

namespace WorkBookmark.App.UI;

internal sealed class SettingsForm : Form
{
    private readonly HotkeyBox _capture;
    private readonly HotkeyBox _recent;
    private readonly CheckBox _startup;
    private readonly Label _status;
    private readonly Button _apply;
    public SettingsForm(UserSettings settings, bool captureRegistered, bool recentRegistered, string directory, Func<UserSettings, Task<string?>> apply, Action openData, Action openLogs)
    {
        UiStyle.Apply(this); Text = "업무 책갈피 설정 · 제한된 시험판";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(610, 418);
        var title = new Label { Text = "업무 책갈피", Font = new Font(Font, FontStyle.Bold), Location = new Point(20, 18), Size = new Size(560, 28) };
        var description = new Label { Text = "탐색기 실제 폴더·단일 파일 / 저장된 Excel 시트·셀\nWindows·Office 실기 검증이 필요한 제한된 시험판입니다.", Location = new Point(20, 51), Size = new Size(570, 48) };
        Controls.AddRange([title, description]);
        Controls.Add(new Label { Text = "현재 위치 남기기", Location = new Point(20, 115), Size = new Size(170, 27) });
        Controls.Add(new Label { Text = "최근 책갈피 열기", Location = new Point(20, 158), Size = new Size(170, 27) });
        _capture = new HotkeyBox(settings.CaptureHotkey) { Location = new Point(200, 111), Size = new Size(238, 28) };
        _recent = new HotkeyBox(settings.RecentHotkey) { Location = new Point(200, 154), Size = new Size(238, 28) };
        Controls.AddRange([_capture, _recent]);
        Controls.Add(new Label { Text = captureRegistered ? "사용 중" : "등록 실패 · 비활성", ForeColor = captureRegistered ? UiStyle.Accent : Color.Firebrick, Location = new Point(451, 115), Size = new Size(148, 27) });
        Controls.Add(new Label { Text = recentRegistered ? "사용 중" : "등록 실패 · 비활성", ForeColor = recentRegistered ? UiStyle.Accent : Color.Firebrick, Location = new Point(451, 158), Size = new Size(148, 27) });
        Controls.Add(new Label { Text = "입력칸을 선택하고 Ctrl 또는 Alt를 포함한 조합을 누르세요.", ForeColor = UiStyle.Muted, Location = new Point(20, 197), Size = new Size(570, 25) });
        _startup = new CheckBox { Text = "Windows 로그인 시 실행 (이 사용자만)", Checked = settings.StartWithWindows, Location = new Point(20, 231), Size = new Size(500, 29) };
        Controls.Add(_startup);
        var data = new LinkLabel { Text = "데이터 폴더", LinkColor = UiStyle.Accent, Location = new Point(20, 274), AutoSize = true };
        var logs = new LinkLabel { Text = "진단 로그", LinkColor = UiStyle.Accent, Location = new Point(133, 274), AutoSize = true };
        data.LinkClicked += (_, _) => openData(); logs.LinkClicked += (_, _) => openLogs();
        Controls.AddRange([data, logs]);
        var tip = new ToolTip(); tip.SetToolTip(data, directory); Disposed += (_, _) => tip.Dispose();
        _status = new Label { Text = "기록의 경로와 메모는 이 PC의 DB에 평문으로 보관됩니다.", ForeColor = UiStyle.Muted, Location = new Point(20, 312), Size = new Size(570, 48) };
        _apply = new Button { Text = "적용", Location = new Point(408, 371), Size = new Size(80, 30) };
        var close = new Button { Text = "닫기", Location = new Point(503, 371), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
        _apply.Click += async (_, _) =>
        {
            _apply.Enabled = false;
            try
            {
                string? error = await apply(settings with { CaptureHotkey = _capture.Value, RecentHotkey = _recent.Value, StartWithWindows = _startup.Checked, IntroShown = true });
                if (IsDisposed) return;
                if (error is null) Close(); else _status.Text = error;
            }
            catch { if (!IsDisposed) _status.Text = "설정을 저장하지 못했습니다. 다시 시도해 주세요."; }
            finally { if (!IsDisposed) _apply.Enabled = true; }
        };
        Controls.AddRange([_status, _apply, close]); CancelButton = close;
        UiStyle.Position(this);
    }
    private sealed class HotkeyBox : TextBox
    {
        public Hotkey Value { get; private set; }
        public HotkeyBox(Hotkey value) { Value = value; Text = value.ToString(); ReadOnly = true; ShortcutsEnabled = false; }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.Tab or Keys.Escape) return base.ProcessCmdKey(ref message, keyData);
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;
            uint modifiers = 0;
            if ((keyData & Keys.Control) != 0) modifiers |= 2;
            if ((keyData & Keys.Alt) != 0) modifiers |= 1;
            if ((keyData & Keys.Shift) != 0) modifiers |= 4;
            var candidate = new Hotkey(modifiers, (int)key);
            if (candidate.IsValid) { Value = candidate; Text = candidate.ToString(); }
            return true;
        }
    }
}
