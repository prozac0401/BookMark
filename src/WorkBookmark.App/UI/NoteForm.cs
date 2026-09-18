using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class NoteForm : Form
{
    private readonly ImeTextBox _note;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Func<string, Task> _saveNote;
    private bool _saving, _closing, _shown, _saveAfterComposition;
    public NoteForm(Bookmark bookmark, Func<string, Task> saveNote)
    {
        UiStyle.Apply(this);
        Text = "책갈피 메모"; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(570, 184); _saveNote = saveNote;
        Controls.Add(new Label { Text = bookmark.DisplayName, AutoEllipsis = true, Location = new Point(16, 13), Size = new Size(536, 25) });
        _note = new ImeTextBox { Text = bookmark.Note, MaxLength = 500, Location = new Point(16, 45), Size = new Size(536, 28), Multiline = false };
        _status = new Label { Text = "한 줄, 최대 500자 · Enter/외부 클릭 저장 · Esc 취소", ForeColor = UiStyle.Muted, Location = new Point(16, 84), Size = new Size(536, 46) };
        _save = new Button { Text = "저장", Location = new Point(376, 140), Size = new Size(82, 30) };
        var cancel = new Button { Text = "취소", Location = new Point(470, 140), Size = new Size(82, 30) };
        _save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { if (!_saving) { _closing = true; Close(); } };
        Controls.AddRange([_note, _status, _save, cancel]);
        Shown += (_, _) => { _shown = true; _note.Focus(); _note.SelectionStart = _note.TextLength; };
        Deactivate += async (_, _) =>
        {
            if (!_shown || _closing || _saving) return;
            if (_note.IsComposing) { _saveAfterComposition = true; return; }
            await SaveAsync();
        };
        Activated += (_, _) => _saveAfterComposition = false;
        _note.CompositionEnded += () =>
        {
            if (_saveAfterComposition && IsHandleCreated) BeginInvoke((Action)(() =>
            {
                if (_saveAfterComposition && !ContainsFocus) { _saveAfterComposition = false; _ = SaveAsync(); }
            }));
        };
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; else _closing = true; };
        UiStyle.Position(this);
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            if (_note.IgnoreSubmit) return base.ProcessCmdKey(ref message, keyData);
            _ = SaveAsync(); return true;
        }
        if (keyData == Keys.Escape && !_note.IsComposing)
        {
            if (!_saving) { _closing = true; Close(); } return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private async Task SaveAsync()
    {
        if (_saving || _closing || IsDisposed) return;
        _saving = true; _save.Enabled = false; _note.ReadOnly = true;
        try
        {
            string text = _note.Text.Replace('\r', ' ').Replace('\n', ' ');
            await _saveNote(text);
            _saving = false; _closing = true; Close();
        }
        catch
        {
            if (!IsDisposed) _status.Text = "메모를 저장하지 못했습니다. 입력은 남아 있습니다. [저장]으로 다시 시도해 주세요.";
        }
        finally { _saving = false; if (!IsDisposed) { _save.Enabled = true; _note.ReadOnly = false; } }
    }
}
