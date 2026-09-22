using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerInlineNoteChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type StickerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.StickerForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var sample = Sample();
        using var form = Create(sample);
        form.Location = new Point(55, 65);
        form.Show();
        Application.DoEvents();
        nint window = form.Handle;
        Rectangle bounds = form.Bounds;
        int resumes = 0, deletes = 0, restores = 0;
        On(form, "ResumeRequested", (Action<Bookmark>)(_ => resumes++));
        On(form, "DeleteRequested", (Action<Bookmark>)(_ => deletes++));
        On(form, "UndoRequested", (Action)(() => restores++));
        bool failSave = true;
        int saves = 0;
        string? saved = null;
        Func<string, Task> persist = text =>
        {
            saves++;
            if (failSave) return Task.FromException(new IOException("Injected inline note failure"));
            saved = text;
            return Task.CompletedTask;
        };
        Begin(form, persist);
        var editor = Field<TextBox>(form, "_noteEditor");
        assert(Editing(form) && form.Handle == window && form.Bounds == bounds && editor.Parent == form && editor.Visible && editor.Focused,
            "STN01 note editing uses the originating sticker window and position");
        assert(editor.Text == sample.Note && !editor.Multiline && editor.MaxLength == 500 &&
            !form.Controls.OfType<Button>().Any(button => button.Text.StartsWith("저장", StringComparison.Ordinal)) &&
            Field<Button>(form, "_cancelNote").Visible && !Field<Button>(form, "_resume").Visible,
            "STN02 inline editing has a one-line limit and cancel control without a save button");

        editor.Text = "사용자가 입력 중인 초안";
        Invoke(form, "UpdateBookmark", sample with { Note = "새로 읽은 저장 메모", LastResumeResult = ResultCode.AppBusy });
        Begin(form, _ => throw new Exception("A second reveal must keep the original callback."));
        Field<Button>(form, "_cancelNote").Focus();
        Application.DoEvents();
        Invoke(form, "FocusResume");
        assert(Editing(form) && editor.Text == "사용자가 입력 중인 초안" && editor.Focused && saves == 0,
            "STN03 background refresh, repeated editing and focus within the sticker retain the draft without writing");

        Command(form, Keys.Control | Keys.Space);
        Invoke(form, "ApplyPresentation", true, false);
        assert(!(bool)StickerType.GetProperty("IsCollapsed")!.GetValue(form)! && form.TopMost && editor.Visible,
            "STN04 collapse requests cannot hide an active memo draft and saved unpinned layouts remain topmost");
        Command(form, Keys.Control | Keys.Delete);
        Command(form, Keys.Control | Keys.Z);
        assert(deletes == 0 && restores == 0 && Editing(form),
            "STN05 editor deletion and undo shortcuts cannot delete or restore bookmarks");

        SendMessage(editor.Handle, 0x010D, nint.Zero, nint.Zero);
        Command(form, Keys.Enter);
        Command(form, Keys.Control | Keys.Enter);
        Command(form, Keys.Escape);
        assert(saves == 0 && resumes == 0 && Editing(form) && form.Visible,
            "STN06 synthetic IME composition Enter/Escape cannot submit, resume or cancel the note");
        SendMessage(editor.Handle, 0x010E, nint.Zero, nint.Zero);
        Command(form, Keys.Enter);
        assert(saves == 0 && Editing(form), "STN07 IME commit Enter retains the suppression interval");
        Thread.Sleep(140);

        editor.Text = "저장 실패 뒤에도 보존하는 메모";
        Pump(Save(form));
        assert(saves == 1 && Editing(form) && !editor.ReadOnly && editor.Text == "저장 실패 뒤에도 보존하는 메모" &&
            Field<Label>(form, "_noteStatus").Text.Contains("저장하지 못했습니다", StringComparison.Ordinal) &&
            Field<Label>(form, "_noteStatus").Text.Contains("Enter", StringComparison.Ordinal),
            "STN08 injected persistence failure retains editable text and inline retry feedback");
        failSave = false;
        Command(form, Keys.Enter);
        assert(saves == 2 && saved == "저장 실패 뒤에도 보존하는 메모" && !Editing(form) && form.Visible &&
            Field<RichTextBox>(form, "_note").Text == saved && resumes == 0,
            "STN09 retry by Enter commits the retained draft without opening the bookmarked target");

        Begin(form, persist);
        editor.Text = "취소할 메모";
        Command(form, Keys.Escape);
        assert(!Editing(form) && form.Visible && saves == 2 && Field<RichTextBox>(form, "_note").Text == saved,
            "STN10 Escape cancels only the inline draft and keeps the saved note and sticker visible");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayedSaves = 0;
        Begin(form, _ => { delayedSaves++; return completion.Task; });
        editor.Text = "진행 중 쓰기";
        Task pending = Save(form);
        Task duplicate = Save(form);
        Command(form, Keys.Escape);
        assert(delayedSaves == 1 && !pending.IsCompleted && duplicate.IsCompleted && Editing(form) && editor.ReadOnly &&
            !Field<Button>(form, "_cancelNote").Enabled,
            "STN11 a pending save blocks duplicate writes and cancel until commit completes");
        completion.SetResult();
        Pump(pending);
        assert(!Editing(form) && Field<RichTextBox>(form, "_note").Text == "진행 중 쓰기",
            "STN12 asynchronous note commit restores normal sticker actions");

        Begin(form, persist);
        form.Size = form.MinimumSize;
        form.PerformLayout();
        assert(editor.Bottom < Field<Button>(form, "_cancelNote").Top &&
            Field<Label>(form, "_noteStatus").Bottom < Field<Label>(form, "_noteLabel").Top &&
            editor.Right <= form.ClientSize.Width && Field<Button>(form, "_cancelNote").Bottom < form.ClientSize.Height,
            "STN13 inline editor and feedback remain inside the minimum sticker bounds");
        var icon = Field<PictureBox>(form, "_typeIcon");
        assert(icon.Image is not null && icon.Width == (int)Math.Round(24 * form.DeviceDpi / 96F) &&
            icon.Right < Field<Label>(form, "_kind").Left && Field<Label>(form, "_kind").Right < Field<Button>(form, "_collapse").Left,
            "STN14 the top-left file type icon fits alongside the label and header controls");

        Command(form, Keys.Escape);
        var laterCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Begin(form, _ => laterCommit.Task);
        editor.Text = "먼저 저장을 요청한 메모";
        Task earlierSave = Save(form);
        var latest = sample with { Note = "다른 편집기에서 나중에 저장한 최신 메모", CaptureSequence = sample.CaptureSequence + 1 };
        Invoke(form, "UpdateBookmark", latest);
        laterCommit.SetResult();
        Pump(earlierSave);
        assert(!Editing(form) && ReferenceEquals(StickerType.GetProperty("Bookmark")!.GetValue(form), latest) &&
            Field<RichTextBox>(form, "_note").Text == latest.Note,
            "STN15 a delayed save completion cannot overwrite a newer published bookmark note");

        RunAutoSave(form, assert);
    }

    private static void RunAutoSave(Form form, Action<bool, string> assert)
    {
        // This is a synthetic ordinary window: it exercises real activation/deactivation,
        // without manipulating another application or a user's document.
        using var other = new Form { Text = "Automatic note save test", ShowInTaskbar = false };
        other.Controls.Add(new TextBox { Text = "Other window", Dock = DockStyle.Top });
        other.Show();
        var editor = Field<TextBox>(form, "_noteEditor");
        int writes = 0;
        string? stored = null;
        bool rejectWrite = false;
        Func<string, Task> persist = text =>
        {
            writes++;
            if (rejectWrite) return Task.FromException(new IOException("Injected automatic save failure"));
            stored = text;
            return Task.CompletedTask;
        };

        Begin(form, persist);
        editor.Text = "다른 창으로 이동하며 자동저장";
        FocusOther(other);
        PumpUntil(() => !Editing(form));
        assert(writes == 1 && stored == "다른 창으로 이동하며 자동저장" && other.ContainsFocus &&
            Field<RichTextBox>(form, "_note").Text == stored,
            "STN16 moving to an ordinary window automatically saves the note without taking focus back");
        Deactivate(form);
        PumpEvents();
        assert(writes == 1, "STN17 repeated deactivation of a completed editor does not write again");

        rejectWrite = true;
        Begin(form, persist);
        editor.Text = "자동저장 실패 후 재시도할 초안";
        FocusOther(other);
        PumpUntil(() => writes == 2 && !Field<bool>(form, "_noteSaving"));
        assert(Editing(form) && !editor.ReadOnly && editor.Text == "자동저장 실패 후 재시도할 초안" &&
            Field<Label>(form, "_noteStatus").Text.Contains("저장하지 못했습니다", StringComparison.Ordinal) && other.ContainsFocus,
            "STN18 automatic save failure retains an editable draft and feedback without stealing focus");
        rejectWrite = false;
        form.Activate();
        editor.Focus();
        FocusOther(other);
        PumpUntil(() => !Editing(form));
        assert(writes == 3 && stored == "자동저장 실패 후 재시도할 초안" && other.ContainsFocus,
            "STN19 returning to a failed draft and leaving again retries and commits it automatically");

        Begin(form, persist);
        editor.Text = "취소한 내용은 저장하지 않음";
        // Queue a departure, then cancel before the queued save runs.
        Deactivate(form);
        Command(form, Keys.Escape);
        FocusOther(other);
        PumpEvents();
        assert(writes == 3 && !Editing(form) && Field<RichTextBox>(form, "_note").Text == stored,
            "STN20 Escape cancels the draft and any queued automatic save");

        Begin(form, persist);
        editor.Text = "조합 중";
        SendMessage(editor.Handle, 0x010D, nint.Zero, nint.Zero);
        FocusOther(other);
        PumpEvents();
        assert(writes == 3 && Editing(form),
            "STN21 leaving during synthetic IME composition defers automatic persistence");
        SendMessage(editor.Handle, 0x010E, nint.Zero, nint.Zero);
        editor.Text = "조합이 확정된 최종 메모";
        PumpUntil(() => !Editing(form));
        assert(writes == 4 && stored == "조합이 확정된 최종 메모" && other.ContainsFocus,
            "STN22 deferred automatic save waits for the committed IME text and keeps the destination focused");

        Begin(form, persist);
        editor.Text = "다시 돌아와 계속 편집";
        SendMessage(editor.Handle, 0x010D, nint.Zero, nint.Zero);
        FocusOther(other);
        PumpEvents();
        form.Activate();
        editor.Focus();
        SendMessage(editor.Handle, 0x010E, nint.Zero, nint.Zero);
        PumpEvents();
        assert(writes == 4 && Editing(form) && editor.Focused,
            "STN23 returning before IME completion cancels the pending automatic save and keeps editing");
        Command(form, Keys.Escape);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayedWrites = 0;
        Begin(form, _ => { delayedWrites++; return completion.Task; });
        editor.Text = "자동저장 진행 중";
        FocusOther(other);
        PumpUntil(() => delayedWrites == 1);
        Deactivate(form);
        Deactivate(form);
        PumpEvents();
        assert(delayedWrites == 1 && Editing(form) && editor.ReadOnly && other.ContainsFocus,
            "STN24 repeated departure events cannot duplicate a pending automatic write");
        completion.SetResult();
        PumpUntil(() => !Editing(form));
        assert(delayedWrites == 1 && other.ContainsFocus && Field<RichTextBox>(form, "_note").Text == "자동저장 진행 중",
            "STN25 a delayed automatic save completes in the background without reactivating its sticker");
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        using var form = Create(Sample());
        form.Location = new Point(70, 70);
        form.Show();
        Begin(form, _ => Task.FromException(new IOException("Injected render feedback")));
        Field<TextBox>(form, "_noteEditor").Text = "수량과 변경 내용을 확인해 운영팀에 전달하기";
        Capture("sticker-inline-note.png");
        Thread.Sleep(140);
        Pump(Save(form));
        Capture("sticker-inline-note-error.png");
        form.Size = form.MinimumSize;
        Capture("sticker-inline-note-minimum.png");

        void Capture(string name)
        {
            form.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
    }

    private static Bookmark Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new CapturedTarget(TargetKind.ExcelCell, @"C:\업무\운영 계획.xlsx", "실행 계획", "$F$42");
        return new(Guid.NewGuid(), target, target.Path, "운영 계획.xlsx", "변경 수량 확인하기", now, now, 10, null, null, null, null);
    }

    private static Form Create(Bookmark bookmark) => (Form)Activator.CreateInstance(StickerType, bookmark)!;
    private static void On(Form form, string name, Delegate handler) => StickerType.GetEvent(name)!.AddEventHandler(form, handler);
    private static bool Editing(Form form) => (bool)StickerType.GetProperty("IsEditingNote")!.GetValue(form)!;
    private static T Field<T>(Form form, string name) => (T)StickerType.GetField(name, Instance)!.GetValue(form)!;
    private static void Invoke(Form form, string name, params object[] arguments) => StickerType.GetMethod(name)!.Invoke(form, arguments);
    private static void Begin(Form form, Func<string, Task> save) => Invoke(form, "BeginNoteEdit", save);
    private static Task Save(Form form) => (Task)StickerType.GetMethod("SaveNoteAsync", Instance)!.Invoke(form, null)!;
    private static void Deactivate(Form form) => typeof(Form).GetMethod("OnDeactivate", Instance)!.Invoke(form, [EventArgs.Empty]);
    private static void FocusOther(Form other)
    {
        other.Activate();
        other.Controls[0].Focus();
    }
    private static bool Command(Form form, Keys keys)
    {
        object[] arguments = [Message.Create(form.Handle, 0x0100, (nint)(int)keys, nint.Zero), keys];
        return (bool)StickerType.GetMethod("ProcessCmdKey", Instance)!.Invoke(form, arguments)!;
    }
    private static void Pump(Task pending)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!pending.IsCompleted && DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(1); }
        if (!pending.IsCompleted) throw new TimeoutException("Inline memo check timed out.");
        pending.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(1); }
        if (!ready()) throw new TimeoutException("Automatic inline memo check timed out.");
    }
    private static void PumpEvents()
    {
        var limit = DateTime.UtcNow.AddMilliseconds(160);
        while (DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(1); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
