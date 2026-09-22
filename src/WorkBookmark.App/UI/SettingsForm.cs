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
    private readonly RadioButton _listDisplay;
    private readonly RadioButton _stickerDisplay;
    public SettingsForm(UserSettings settings, bool captureRegistered, bool recentRegistered, string directory, Func<UserSettings, Task<string?>> apply, Action openData, Action openLogs)
    {
        UiStyle.Apply(this); Text = "업무 책갈피 설정";
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(646, 662);
        MinimumSize = new Size(548, 460);

        var headingFont = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        var sectionFont = new Font(Font, FontStyle.Bold);
        Disposed += (_, _) => { headingFont.Dispose(); sectionFont.Dispose(); };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18),
            ColumnCount = 1, RowCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, TabStop = false };
        var content = Stack();
        content.Dock = DockStyle.Top;
        content.Padding = new Padding(0, 0, 16, 8);
        scroll.Controls.Add(content);
        root.Controls.Add(scroll, 0, 0);

        var title = TextLabel("업무 책갈피 설정", false, new Padding(0, 0, 0, 4));
        title.Font = headingFont;
        content.Controls.Add(title);
        content.Controls.Add(TextLabel("표시 방식과 단축키를 이 PC에 저장합니다.", true, new Padding(0, 0, 0, 20)));
        var viewTitle = TextLabel("책갈피 표시 방식", false, new Padding(0, 0, 0, 10));
        viewTitle.Font = sectionFont;
        content.Controls.Add(viewTitle);

        var modes = Stack();
        modes.BackColor = Color.White;
        modes.Padding = new Padding(16, 12, 16, 12);
        modes.Margin = new Padding(0, 0, 0, 10);
        _listDisplay = new RadioButton
        {
            Text = "목록으로 보기 (&L)", AutoSize = true,
            Checked = settings.DisplayMode == BookmarkDisplayMode.List,
            Margin = new Padding(0, 0, 0, 3), AccessibleName = "목록으로 보기",
            AccessibleDescription = "검색과 최근 책갈피를 한 창에서 확인합니다."
        };
        _stickerDisplay = new RadioButton
        {
            Text = "포스트잇 스티커로 보기 (&S)", AutoSize = true,
            Checked = settings.DisplayMode == BookmarkDisplayMode.Stickers,
            Margin = new Padding(0, 12, 0, 3), AccessibleName = "포스트잇 스티커로 보기",
            AccessibleDescription = "책갈피를 각각 띄워 놓고 위치와 크기를 조절합니다."
        };
        modes.Controls.Add(_listDisplay);
        modes.Controls.Add(TextLabel("검색과 최근 책갈피를 한 창에서 확인합니다.", true, new Padding(25, 0, 0, 0)));
        modes.Controls.Add(_stickerDisplay);
        modes.Controls.Add(TextLabel("책갈피를 각각 띄워 놓고 위치와 크기를 자유롭게 조절합니다.", true, new Padding(25, 0, 0, 0)));
        content.Controls.Add(modes);
        content.Controls.Add(TextLabel("‘지우기’는 책갈피 삭제 · × / Esc는 스티커 숨기기\n스티커 모드에서도 트레이 메뉴로 목록을 열 수 있습니다.", true, new Padding(0, 0, 0, 20)));

        var hotkeyTitle = TextLabel("단축키", false, new Padding(0, 0, 0, 8));
        hotkeyTitle.Font = sectionFont;
        content.Controls.Add(hotkeyTitle);
        var hotkeys = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotkeys.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hotkeys.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hotkeys.Controls.Add(TextLabel("현재 위치 남기기", false, new Padding(0, 5, 18, 8)), 0, 0);
        hotkeys.Controls.Add(TextLabel("책갈피 보기", false, new Padding(0, 5, 18, 8)), 0, 1);
        _capture = new HotkeyBox(settings.CaptureHotkey) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 8), AccessibleName = "현재 위치 남기기 단축키" };
        _recent = new HotkeyBox(settings.RecentHotkey) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 8), AccessibleName = "책갈피 보기 단축키" };
        hotkeys.Controls.Add(_capture, 1, 0);
        hotkeys.Controls.Add(_recent, 1, 1);
        hotkeys.Controls.Add(RegistrationLabel(captureRegistered), 2, 0);
        hotkeys.Controls.Add(RegistrationLabel(recentRegistered), 2, 1);
        content.Controls.Add(hotkeys);
        content.Controls.Add(TextLabel("책갈피 보기는 위에서 선택한 방식으로 열립니다.\n입력칸에서 Ctrl 또는 Alt를 포함한 조합을 누르세요.", true, new Padding(0, 0, 0, 18)));

        _startup = new CheckBox { Text = "Windows 로그인 시 실행 · 이 사용자만 (&W)", Checked = settings.StartWithWindows, AutoSize = true, Margin = new Padding(0, 0, 0, 15) };
        content.Controls.Add(_startup);
        var links = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true };
        var data = new LinkLabel { Text = "데이터 폴더", LinkColor = UiStyle.Accent, AutoSize = true, Margin = new Padding(0, 0, 22, 0) };
        var logs = new LinkLabel { Text = "진단 로그", LinkColor = UiStyle.Accent, AutoSize = true, Margin = Padding.Empty };
        data.LinkClicked += (_, _) => openData(); logs.LinkClicked += (_, _) => openLogs();
        links.Controls.AddRange([data, logs]);
        content.Controls.Add(links);
        var tip = new ToolTip(); tip.SetToolTip(data, directory); Disposed += (_, _) => tip.Dispose();

        var footer = Stack();
        footer.Margin = new Padding(0, 12, 0, 0);
        _status = TextLabel("경로·메모와 메모장의 본문·선택 위치는 이 PC에 보관됩니다.", true, new Padding(0, 0, 0, 12));
        _status.AccessibleName = "설정 상태";
        footer.Controls.Add(_status);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        _apply = new Button { Text = "적용 (&A)", AutoSize = true, MinimumSize = new Size(92, 34), Margin = new Padding(0, 0, 10, 0), TabIndex = 0 };
        var close = new Button { Text = "닫기", AutoSize = true, MinimumSize = new Size(92, 34), Margin = Padding.Empty, DialogResult = DialogResult.Cancel, TabIndex = 1 };
        _apply.Click += async (_, _) =>
        {
            _apply.Enabled = false;
            try
            {
                string? error = await apply(settings with { CaptureHotkey = _capture.Value, RecentHotkey = _recent.Value, StartWithWindows = _startup.Checked, IntroShown = true, DisplayMode = _stickerDisplay.Checked ? BookmarkDisplayMode.Stickers : BookmarkDisplayMode.List });
                if (IsDisposed) return;
                if (error is null) Close();
                else { _status.Text = error; _status.ForeColor = Color.Firebrick; }
            }
            catch { if (!IsDisposed) { _status.Text = "설정을 저장하지 못했습니다. 다시 시도해 주세요."; _status.ForeColor = Color.Firebrick; } }
            finally { if (!IsDisposed) _apply.Enabled = true; }
        };
        buttons.Controls.AddRange([close, _apply]);
        footer.Controls.Add(buttons);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        AcceptButton = _apply;
        CancelButton = close;
        Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            var available = new Size(Math.Max(1, area.Width - 40), Math.Max(1, area.Height - 40));
            MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            UiStyle.Position(this);
        };
        UiStyle.Position(this);
    }
    private static TableLayoutPanel Stack()
    {
        var panel = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }
    private static Label TextLabel(string text, bool muted, Padding margin) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill,
        ForeColor = muted ? UiStyle.Muted : UiStyle.Ink, Margin = margin,
        UseMnemonic = false
    };
    private static Label RegistrationLabel(bool registered)
    {
        var label = TextLabel(registered ? "사용 중" : "등록 실패 · 비활성", false, new Padding(0, 5, 0, 8));
        label.ForeColor = registered ? UiStyle.Accent : Color.Firebrick;
        return label;
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
