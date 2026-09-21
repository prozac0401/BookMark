using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

/// <summary>Explicit URL entry also works when a browser does not expose accessibility metadata.</summary>
internal sealed class WebBookmarkForm : Form
{
    private readonly ImeTextBox _url, _title;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Func<CapturedTarget, Task> _persist;
    private bool _saving;

    public WebBookmarkForm(Func<CapturedTarget, Task> persist)
    {
        _persist = persist;
        UiStyle.Apply(this);
        Text = "웹페이지 책갈피 추가";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(610, 270);
        Controls.Add(new Label { Text = "주소 표시줄에서 복사한 전체 URL (http:// 또는 https://)", Location = new Point(20, 18), Size = new Size(570, 28) });
        _url = new ImeTextBox { MaxLength = BrowserProtocol.MaximumUrlLength, Location = new Point(20, 51), Size = new Size(570, 28) };
        Controls.Add(new Label { Text = "제목 (선택)", Location = new Point(20, 91), Size = new Size(570, 28) });
        _title = new ImeTextBox { MaxLength = 256, Location = new Point(20, 121), Size = new Size(570, 28) };
        _status = new Label { Text = "확장 프로그램 없이 저장할 수 있습니다. 로그인은 페이지를 열 때 진행합니다.", Location = new Point(20, 162), Size = new Size(570, 48), ForeColor = UiStyle.Muted };
        _save = new Button { Text = "저장", Location = new Point(408, 222), Size = new Size(82, 30) };
        var cancel = new Button { Text = "취소", Location = new Point(508, 222), Size = new Size(82, 30), DialogResult = DialogResult.Cancel };
        _save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { if (!_saving) Close(); };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
        Controls.AddRange([_url, _title, _status, _save, cancel]);
        Shown += (_, _) => _url.Focus();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter && !_url.IgnoreSubmit && !_title.IgnoreSubmit) { _ = SaveAsync(); return true; }
        if (keyData == Keys.Escape && !_url.IsComposing && !_title.IsComposing) { if (!_saving) Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private async Task SaveAsync()
    {
        if (_saving || IsDisposed) return;
        CapturedTarget target;
        try { target = BrowserProtocol.ValidateTarget(new(TargetKind.WebPage, _url.Text.Trim(), PageTitle: _title.Text.Trim())); }
        catch (BookmarkException) { _status.Text = "http:// 또는 https://로 시작하는 전체 주소를 입력해 주세요. 사용자 이름·암호가 들어 있는 주소는 저장할 수 없습니다."; return; }
        _saving = true; _save.Enabled = false; _url.ReadOnly = _title.ReadOnly = true;
        try
        {
            await _persist(target);
            _saving = false; Close();
        }
        catch { if (!IsDisposed) _status.Text = "저장하지 못했습니다. 입력 내용은 유지됩니다. 잠시 후 다시 시도해 주세요."; }
        finally
        {
            _saving = false;
            if (!IsDisposed) { _save.Enabled = true; _url.ReadOnly = _title.ReadOnly = false; }
        }
    }
}
