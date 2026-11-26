using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

/// <summary>Explicit URL entry also works when a browser does not expose accessibility metadata.</summary>
internal sealed class WebBookmarkForm : Form
{
    private readonly ImeTextBox _url;
    private readonly ImeTextBox? _title;
    private readonly Label _status;
    private readonly Button _save, _cancel;
    private readonly Func<CapturedTarget, Task> _persist;
    private readonly Bookmark? _original;
    private bool _saving;

    public WebBookmarkForm(Func<CapturedTarget, Task> persist) : this(persist, null) { }

    public WebBookmarkForm(Func<CapturedTarget, Task> persist, Bookmark? original)
    {
        _persist = persist;
        _original = original;
        UiStyle.Apply(this);
        Text = original is null ? "웹페이지 책갈피 추가" : "웹 주소 수정";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(610, 270);
        _url = new ImeTextBox { MaxLength = BrowserProtocol.MaximumUrlLength, Size = new Size(570, 28), TabIndex = 0 };
        if (original is null)
        {
            Controls.Add(new Label { Text = "주소 표시줄에서 복사한 전체 URL (http:// 또는 https://)", Location = new Point(20, 18), Size = new Size(570, 28) });
            _url.AccessibleName = "웹 주소"; _url.Location = new Point(20, 51);
            Controls.Add(new Label { Text = "제목 (선택)", Location = new Point(20, 91), Size = new Size(570, 28) });
            _title = new ImeTextBox { MaxLength = 256, AccessibleName = "제목", Location = new Point(20, 121), Size = new Size(570, 28), TabIndex = 1 };
            Controls.Add(_title);
        }
        else
        {
            Controls.Add(new Label { Text = "기존 웹 주소", Location = new Point(20, 18), Size = new Size(570, 28) });
            Controls.Add(new TextBox { Text = original.Target.Path, ReadOnly = true, AccessibleName = "기존 웹 주소", Location = new Point(20, 51), Size = new Size(570, 28), TabIndex = 4 });
            Controls.Add(new Label { Text = "새 웹 주소 (http:// 또는 https://)", Location = new Point(20, 91), Size = new Size(570, 28) });
            _url.AccessibleName = "새 웹 주소"; _url.Location = new Point(20, 121); _url.Text = original.Target.Path;
        }
        _status = new Label { Text = original is null ? "확장 프로그램 없이 저장할 수 있습니다. 로그인은 페이지를 열 때 진행합니다." : "다음에 책갈피를 열면 새 주소로 이동합니다.",
            Location = new Point(20, 162), Size = new Size(570, 48), ForeColor = UiStyle.Muted };
        _save = new Button { Text = original is null ? "저장" : "수정 적용", Location = new Point(388, 222), Size = new Size(102, 30), TabIndex = 2 };
        _cancel = new Button { Text = "취소", Location = new Point(508, 222), Size = new Size(82, 30), TabIndex = 3, DialogResult = DialogResult.Cancel };
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => { if (!_saving) Close(); };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
        Controls.AddRange([_url, _status, _save, _cancel]);
        Shown += (_, _) => { _url.Focus(); if (original is not null) _url.SelectAll(); };
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter && !_url.IgnoreSubmit && _title?.IgnoreSubmit != true)
        {
            if (_cancel.Focused) _cancel.PerformClick(); else _ = SaveAsync();
            return true;
        }
        if (keyData == Keys.Escape && !_url.IsComposing && _title?.IsComposing != true) { if (!_saving) Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private async Task SaveAsync()
    {
        if (_saving || IsDisposed) return;
        CapturedTarget target;
        try { target = BrowserProtocol.ValidateTarget(new(TargetKind.WebPage, _url.Text.Trim(), PageTitle: _original?.Target.PageTitle ?? _title?.Text.Trim() ?? "")); }
        catch (BookmarkException) { _status.Text = "http:// 또는 https://로 시작하는 전체 주소를 입력해 주세요. 사용자 이름·암호가 들어 있는 주소는 저장할 수 없습니다."; return; }
        if (_original?.Target.Path == target.Path) { _status.Text = "기존 주소와 같습니다. 새 웹 주소를 입력해 주세요."; _url.Focus(); return; }
        _saving = true; _save.Enabled = _cancel.Enabled = false; _url.ReadOnly = true;
        if (_title is not null) _title.ReadOnly = true;
        try
        {
            await _persist(target);
            _saving = false; DialogResult = DialogResult.OK; Close();
        }
        catch (BookmarkException error) when (_original is not null)
        {
            if (!IsDisposed) _status.Text = error.Code switch
            {
                ResultCode.ContextChanged => "이 책갈피의 주소가 이미 변경되었습니다. 창을 닫고 현재 주소를 확인해 주세요.",
                ResultCode.TargetUnavailable => "이 책갈피가 삭제되었습니다. 창을 닫고 목록을 확인해 주세요.",
                _ => UiStyle.Result(error.Code)
            };
        }
        catch { if (!IsDisposed) _status.Text = "저장하지 못했습니다. 입력 내용은 유지됩니다. 잠시 후 다시 시도해 주세요."; }
        finally
        {
            _saving = false;
            if (!IsDisposed)
            {
                _save.Enabled = _cancel.Enabled = true; _url.ReadOnly = false;
                if (_title is not null) _title.ReadOnly = false;
            }
        }
    }
}
