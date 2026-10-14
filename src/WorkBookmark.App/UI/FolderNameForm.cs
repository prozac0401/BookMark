using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class FolderNameForm : Form
{
    private readonly ImeTextBox _name = new() { MaxLength = 100, AccessibleName = "폴더 이름" };
    private readonly Label _status = new() { Text = "최대 100자 · 폴더에는 링크가 없습니다.", ForeColor = UiStyle.Muted };
    private readonly Button _save = new() { Text = "저장" };
    private readonly Button _cancel = new() { Text = "취소" };
    private readonly Func<string, Task> _commit;
    private bool _saving;

    internal FolderNameForm(string name, bool rename, Func<string, Task> commit)
    {
        UiStyle.Apply(this);
        _commit = commit;
        Text = rename ? "폴더 이름 변경" : "새 폴더";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(410, 155);
        _name.Text = name;
        _name.SetBounds(16, 16, 378, 28);
        _status.SetBounds(16, 55, 378, 42);
        _save.SetBounds(216, 108, 82, 30);
        _cancel.SetBounds(312, 108, 82, 30);
        Controls.AddRange([_name, _status, _save, _cancel]);
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => { if (!_saving) Close(); };
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
        UiStyle.Position(this);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            if (_cancel.Focused) { if (!_saving) Close(); return true; }
            if (_name.IgnoreSubmit) return base.ProcessCmdKey(ref message, keyData);
            _ = SaveAsync(); return true;
        }
        if (keyData == Keys.Escape && !_name.IsComposing)
        {
            if (!_saving) Close();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private async Task SaveAsync()
    {
        if (_saving || IsDisposed) return;
        string name;
        try { name = BookmarkFolderPolicy.ValidateName(_name.Text); }
        catch { _status.Text = "폴더 이름을 1~100자로 입력해 주세요."; _name.Focus(); return; }
        _saving = true; _save.Enabled = false; _cancel.Enabled = false; _name.ReadOnly = true;
        try
        {
            await _commit(name);
            _saving = false;
            Close();
        }
        catch
        {
            if (!IsDisposed) _status.Text = "폴더를 저장하지 못했습니다. 입력을 유지했으니 다시 시도해 주세요.";
        }
        finally
        {
            _saving = false;
            if (!IsDisposed) { _save.Enabled = true; _cancel.Enabled = true; _name.ReadOnly = false; }
        }
    }
}
