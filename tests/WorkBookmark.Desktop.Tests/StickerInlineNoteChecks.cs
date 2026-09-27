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
        On(form, "NoteRequested", (Action<Bookmark>)(_ => Begin(form, persist)));
        Field<Button>(form, "_title").PerformClick();
        var editor = Field<TextBox>(form, "_noteEditor");
        var editingBounds = form.Bounds;
        assert(Editing(form) && form.Handle == window && form.Location == bounds.Location && form.Width == bounds.Width &&
            form.ClientSize.Height == Px(form, 170) && Placement(form) == bounds && !PlacementCollapsed(form) &&
            editor.Parent == form && editor.Visible && editor.Focused,
            "STN01 clicking the compact title temporarily enlarges only its height in the same window and preserves placement");
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
            Field<Label>(form, "_noteStatus").Text.Contains("Enter", StringComparison.Ordinal) && form.Bounds == editingBounds && Placement(form) == bounds,
            "STN08 injected persistence failure retains editable text and inline retry feedback");
        failSave = false;
        Command(form, Keys.Enter);
        assert(saves == 2 && saved == "저장 실패 뒤에도 보존하는 메모" && !Editing(form) && form.Visible &&
            Field<Button>(form, "_title").Text == saved && resumes == 0 && form.Bounds == bounds,
            "STN09 retry by Enter commits the retained draft without opening the bookmarked target");

        Begin(form, persist);
        editor.Text = "취소할 메모";
        Command(form, Keys.Escape);
        assert(!Editing(form) && form.Visible && saves == 2 && Field<Button>(form, "_title").Text == saved && form.Bounds == bounds,
            "STN10 Escape cancels only the inline draft and keeps the saved note and sticker visible");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayedSaves = 0;
        Begin(form, _ => { delayedSaves++; return completion.Task; });
        editor.Text = "진행 중 쓰기";
        Task pending = Save(form);
        Task duplicate = Save(form);
        Command(form, Keys.Escape);
        assert(delayedSaves == 1 && !pending.IsCompleted && duplicate.IsCompleted && Editing(form) && editor.ReadOnly &&
            !Field<Button>(form, "_cancelNote").Enabled && form.Bounds == editingBounds && Placement(form) == bounds,
            "STN11 a pending save blocks duplicate writes and cancel until commit completes");
        completion.SetResult();
        Pump(pending);
        assert(!Editing(form) && Field<Button>(form, "_title").Text == "진행 중 쓰기" && form.Bounds == bounds,
            "STN12 asynchronous note commit restores normal sticker actions");

        Begin(form, persist);
        form.Size = form.MinimumSize;
        form.PerformLayout();
        assert(editor.Bottom < Field<Button>(form, "_cancelNote").Top &&
            Field<Label>(form, "_noteStatus").Top > editor.Bottom &&
            Field<Label>(form, "_noteStatus").Bottom < Field<Button>(form, "_cancelNote").Top &&
            Field<Label>(form, "_noteStatus").Height >= Field<Label>(form, "_noteStatus").Font.Height * 2 &&
            editor.Right <= form.ClientSize.Width && Field<Button>(form, "_cancelNote").Bottom < form.ClientSize.Height,
            "STN13 inline editor and feedback remain inside the minimum sticker bounds");
        assert(form.Controls.Cast<Control>().Where(control => control.Visible).ToHashSet().SetEquals(
            [editor, Field<Label>(form, "_noteStatus"), Field<Button>(form, "_cancelNote")]) && Placement(form) == bounds,
            "STN14 temporary editing shows only editor, feedback and cancel without replacing the normal placement");

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
            Field<Button>(form, "_title").Text == latest.Note && form.Bounds == bounds,
            "STN15 a delayed save completion cannot overwrite a newer published bookmark note");

        Invoke(form, "UpdateBookmark", sample with { Note = "" });
        Command(form, Keys.Control | Keys.E);
        editor.Text = "새로 추가한 메모";
        Command(form, Keys.Enter);
        assert(!Editing(form) && Field<Button>(form, "_title").Text == "새로 추가한 메모" &&
            form.Text.StartsWith("새로 추가한 메모", StringComparison.Ordinal),
            "STN26 adding a memo by Ctrl+E immediately replaces the file fallback and window title");
        Begin(form, persist);
        editor.Text = "   ";
        Command(form, Keys.Enter);
        assert(!Editing(form) && Field<Button>(form, "_title").Text == "운영 계획.xlsx" && saved == "   " &&
            form.Text.StartsWith("운영 계획.xlsx", StringComparison.Ordinal),
            "STN27 clearing a memo immediately restores its fallback without rewriting the stored draft");
        Invoke(form, "ApplyPresentation", true, true);
        var collapsedBounds = form.Bounds;
        var collapsedPlacement = Placement(form);
        Command(form, Keys.Control | Keys.E);
        assert(Editing(form) && !(bool)StickerType.GetProperty("IsCollapsed")!.GetValue(form)! &&
            editor.Visible && !Field<Button>(form, "_title").Visible && PlacementCollapsed(form) && Placement(form) == collapsedPlacement,
            "STN28 editing a collapsed sticker temporarily reveals its editor while retaining collapsed placement");
        Command(form, Keys.Escape);
        assert(form.Bounds == collapsedBounds && PlacementCollapsed(form) && Field<Button>(form, "_title").Visible,
            "STN29 cancelling an edit restores the original collapsed size and presentation");
        Invoke(form, "ApplyPresentation", false, true);

        RunAutoSave(form, assert);
        EditingGeometry(assert);
    }

    private static void RunAutoSave(Form form, Action<bool, string> assert)
    {
        // This is a synthetic ordinary window: it exercises real activation/deactivation,
        // without manipulating another application or a user's document.
        using var other = new Form { Text = "Automatic note save test", ShowInTaskbar = false };
        other.Controls.Add(new TextBox { Text = "Other window", Dock = DockStyle.Top });
        other.Show();
        var editor = Field<TextBox>(form, "_noteEditor");
        var normalBounds = form.Bounds;
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
            Field<Button>(form, "_title").Text == stored && form.Bounds == normalBounds,
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
            Field<Label>(form, "_noteStatus").Text.Contains("저장하지 못했습니다", StringComparison.Ordinal) && other.ContainsFocus &&
            form.ClientSize.Height == Px(form, 170) && Placement(form) == normalBounds,
            "STN18 automatic save failure retains an editable draft and feedback without stealing focus");
        rejectWrite = false;
        form.Activate();
        editor.Focus();
        FocusOther(other);
        PumpUntil(() => !Editing(form));
        assert(writes == 3 && stored == "자동저장 실패 후 재시도할 초안" && other.ContainsFocus && form.Bounds == normalBounds,
            "STN19 returning to a failed draft and leaving again retries and commits it automatically");

        Begin(form, persist);
        editor.Text = "취소한 내용은 저장하지 않음";
        // Queue a departure, then cancel before the queued save runs.
        Deactivate(form);
        Command(form, Keys.Escape);
        FocusOther(other);
        PumpEvents();
        assert(writes == 3 && !Editing(form) && Field<Button>(form, "_title").Text == stored,
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
        assert(writes == 4 && stored == "조합이 확정된 최종 메모" && other.ContainsFocus && form.Bounds == normalBounds,
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
        assert(delayedWrites == 1 && other.ContainsFocus && Field<Button>(form, "_title").Text == "자동저장 진행 중" && form.Bounds == normalBounds,
            "STN25 a delayed automatic save completes in the background without reactivating its sticker");
    }

    private static void EditingGeometry(Action<bool, string> assert)
    {
        using var form = Create(Sample());
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        form.Location = new Point(screen.WorkingArea.Left + 60, screen.WorkingArea.Top + 60);
        form.Show();
        foreach (var client in new[] { new Size(420, 210), new Size(240, 160) })
        {
            form.ClientSize = new Size(Px(form, client.Width), Px(form, client.Height));
            var original = form.Bounds;
            Begin(form, _ => Task.CompletedTask);
            assert(form.Bounds == original && Placement(form) == original,
                $"STN30 editing a {client.Width}x{client.Height} custom client never resizes an already sufficient window");
            Pump(Save(form));
            assert(form.Bounds == original, "STN31 saving preserves the full custom window size and position");
        }
        form.ClientSize = new Size(Px(form, 240), Px(form, 88));
        var narrow = form.Bounds;
        Begin(form, _ => Task.CompletedTask);
        assert(form.Width == narrow.Width && form.ClientSize.Height == Px(form, 170) && Placement(form) == narrow,
            "STN32 a narrow compact sticker keeps its chosen width while only its editing height expands");
        form.Size += new Size(Px(form, 30), Px(form, 20));
        StickerType.GetMethod("OnResizeEnd", Instance)!.Invoke(form, [EventArgs.Empty]);
        assert(Placement(form) == narrow, "STN33 resizing while editing changes only the temporary editor geometry");
        Command(form, Keys.Escape);
        assert(form.Bounds == narrow, "STN34 cancelling restores the original narrow size after a temporary resize");
        form.ClientSize = new Size(Px(form, 260), Px(form, 88));
        form.Location = new Point(screen.WorkingArea.Left + 60, screen.WorkingArea.Bottom - form.Height - 2);
        var bottom = form.Bounds;
        Begin(form, _ => Task.FromException(new IOException("Injected bottom-edge save failure")));
        var expanded = form.Bounds;
        assert(screen.WorkingArea.Contains(expanded) && expanded.Top < bottom.Top && Placement(form) == bottom,
            "STN35 editing at the work-area bottom moves the temporary editor into view while preserving the original anchor");
        Pump(Save(form));
        assert(Editing(form) && form.Bounds == expanded && Placement(form) == bottom,
            "STN36 a bottom-edge save failure retains the visible enlarged draft and its original persisted placement");
        Command(form, Keys.Escape);
        assert(form.Bounds == bottom, "STN37 cancelling a bottom-edge edit restores both its compact size and original position");
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

    private static int Px(Form form, int value) => (int)Math.Round(value * form.DeviceDpi / 96F);
    private static Rectangle Placement(Form form) => (Rectangle)StickerType.GetProperty("PlacementBounds")!.GetValue(form)!;
    private static bool PlacementCollapsed(Form form) => (bool)StickerType.GetProperty("PlacementIsCollapsed")!.GetValue(form)!;
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
