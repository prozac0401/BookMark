using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

// Real WinForms command/control behavior. No user documents or persistent application DB.
// Timer checks intentionally inject a past deadline and dispatch the real Tick handler;
// they prove expiry/race handling without pretending to wait through a login or real IME session.
internal static class AdditionalFormChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly = typeof(BookmarkApplicationContext).Assembly;

    internal static void Run(Action<bool, string> assert)
    {
        NoteCancelAndLength(assert);
        RelinkPreviewDecisions(assert);
        UndoDeadlineAndTimer(assert);
    }

    private static Bookmark Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new CapturedTarget(TargetKind.ExcelCell, @"C:\synthetic\original.xlsx", "확정자", "$D$127", false);
        return new(Guid.NewGuid(), target, target.Path, "시험 책갈피", "원래 메모 유지", now, now, 7,
            null, null, ResultCode.TargetUnavailable, null);
    }

    private static void NoteCancelAndLength(Action<bool, string> assert)
    {
        var noteType = AppAssembly.GetType("WorkBookmark.App.UI.NoteForm")!;
        var sample = Sample();
        int writes = 0;
        string? stored = null;
        Func<string, Task> save = value => { writes++; stored = value; return Task.CompletedTask; };
        using (var note = (Form)Activator.CreateInstance(noteType, sample, save)!)
        {
            var editor = Field<TextBox>(note, "_note");
            editor.Text = "취소할 변경";
            _ = editor.Handle;
            bool consumed = Command(note, Keys.Escape);
            assert(consumed && writes == 0 && stored is null && note.IsDisposed,
                "UI04 NoteForm Escape closes without persisting edited text");
        }

        using (var note = (Form)Activator.CreateInstance(noteType, sample, save)!)
        {
            var editor = Field<TextBox>(note, "_note");
            editor.Text = "";
            _ = editor.Handle;
            // WM_CHAR targets this test-owned native EDIT control only, not global input.
            for (int i = 0; i < 501; i++) SendMessage(editor.Handle, 0x0102, (nint)'가', (nint)1);
            assert(editor.Text == new string('가', 500) && editor.MaxLength == 500 && !editor.Multiline,
                "UI04 native note editor accepts 500 characters and refuses character 501");
            bool consumed = Command(note, Keys.Enter);
            assert(consumed && writes == 1 && stored == new string('가', 500) && note.IsDisposed,
                "UI04 NoteForm Enter persists the accepted 500-character note exactly once");
        }
    }

    private static void RelinkPreviewDecisions(Action<bool, string> assert)
    {
        var previewType = AppAssembly.GetType("WorkBookmark.App.UI.RelinkPreviewForm")!;
        var original = Sample();
        var candidate = original.Target with { Path = @"D:\synthetic\moved.xlsx" };
        foreach (var expected in new[] { DialogResult.Cancel, DialogResult.OK })
        {
            using var preview = (Form)Activator.CreateInstance(previewType, original, candidate)!;
            var text = preview.Controls.OfType<TextBox>().Single();
            assert(text.ReadOnly && text.Text.Contains(candidate.Path, StringComparison.Ordinal) &&
                text.Text.Contains(original.Note, StringComparison.Ordinal) &&
                text.Text.Contains("확정자", StringComparison.Ordinal) && text.Text.Contains("$D$127", StringComparison.Ordinal),
                $"RL01 {expected} relink preview shows candidate path, retained note and existing cell read-only");
            var decision = preview.Controls.OfType<Button>().Single(button => button.DialogResult == expected);
            bool clicked = false, timedOut = false;
            using var deadline = new System.Windows.Forms.Timer { Interval = 3000 };
            deadline.Tick += (_, _) => { timedOut = true; deadline.Stop(); preview.DialogResult = DialogResult.Abort; preview.Close(); };
            preview.Shown += (_, _) => preview.BeginInvoke((Action)(() =>
            {
                clicked = true;
                decision.PerformClick();
            }));
            deadline.Start();
            var actual = preview.ShowDialog();
            deadline.Stop();
            assert(clicked && !timedOut && actual == expected,
                $"RL01 actual modal relink {expected} button returns the correct commit/cancel decision");
        }
    }

    private static void UndoDeadlineAndTimer(Action<bool, string> assert)
    {
        string data = Path.Combine(Path.GetTempPath(), "WorkBookmark-AdditionalForms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F13),
            RecentHotkey = new Hotkey(7, (int)Keys.F14),
            IntroShown = true
        }).Save(data);
        using var repository = new UndoRepository(Sample());
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, data);
        var timer = Field<System.Windows.Forms.Timer>(context, "_undoTimer");
        var menu = Field<ToolStripMenuItem>(context, "_undoMenu");
        try
        {
            Pump(InvokeTask(context, "DeleteAsync", repository.Value));
            assert(repository.DeleteCalls == 1 && repository.Value.DeletedAtUtc is not null &&
                menu.Available && timer.Enabled && timer.Interval == 10000,
                "UI03 deleting a bookmark arms the real 10-second undo timer and menu");
            timer.Stop();
            SetField(context, "_undoUntil", DateTimeOffset.UtcNow.AddSeconds(-1));
            Pump(InvokeTask(context, "UndoAsync"));
            assert(repository.RestoreCalls == 0 && repository.Value.DeletedAtUtc is not null,
                "UI03 expired undo is refused even when its timer callback has not fired");

            typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Instance)!.Invoke(timer, [EventArgs.Empty]);
            assert(!timer.Enabled && !menu.Available && Field<object?>(context, "_undoId") is null,
                "UI03 real expiry callback clears the undo identity and hides its menu");
            Pump(InvokeTask(context, "UndoAsync"));
            assert(repository.RestoreCalls == 0,
                "UI03 a stale undo action after the expiry callback cannot restore a deleted bookmark");

            Pump(InvokeTask(context, "DeleteAsync", repository.Value));
            Pump(InvokeTask(context, "UndoAsync"));
            assert(repository.RestoreCalls == 1 && repository.Value.DeletedAtUtc is null &&
                !timer.Enabled && !menu.Available && Field<object?>(context, "_undoId") is null,
                "UI03 a fresh unexpired undo restores once and disarms both timer and menu");
            Pump(InvokeTask(context, "UndoAsync"));
            assert(repository.RestoreCalls == 1,
                "UI03 repeated undo after successful restoration does not issue a second repository restore");
        }
        finally { context.ExitThread(); }
    }

    private static bool Command(Form form, Keys keys)
    {
        object[] arguments = [Message.Create(form.Handle, 0x0100, (nint)(int)keys, nint.Zero), keys];
        return (bool)form.GetType().GetMethod("ProcessCmdKey", Instance)!.Invoke(form, arguments)!;
    }
    private static Task InvokeTask(object instance, string name, params object[] arguments) =>
        (Task)instance.GetType().GetMethod(name, Instance)!.Invoke(instance, arguments)!;
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, Instance)!.SetValue(instance, value);
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Additional form operation did not complete.");
        task.GetAwaiter().GetResult();
    }

    // Intentionally models storage acknowledgments only. Core suite owns real SQLite
    // deletion/recapture/note persistence; these checks assert the application UI lifecycle.
    private sealed class UndoRepository(Bookmark initial) : IBookmarkRepository
    {
        internal Bookmark Value = initial;
        internal int DeleteCalls, RestoreCalls;
        public void SoftDelete(Guid id)
        {
            if (id != Value.Id) throw new InvalidOperationException("Unexpected test bookmark.");
            DeleteCalls++; Value = Value with { DeletedAtUtc = DateTimeOffset.UtcNow };
        }
        public void Restore(Guid id)
        {
            if (id != Value.Id) throw new InvalidOperationException("Unexpected test bookmark.");
            RestoreCalls++; Value = Value with { DeletedAtUtc = null };
        }
        public SearchResults List(string query = "") => new(Value.DeletedAtUtc is null ? [Value] : [], false);
        public Bookmark? Get(Guid id) => id == Value.Id ? Value : null;
        public CaptureCommit UpsertCapture(CapturedTarget target) => throw new NotSupportedException();
        public void UpdateNote(Guid id, string note) => throw new NotSupportedException();
        public void RecordResume(Guid id, ResultCode result) => throw new NotSupportedException();
        public void Relink(Guid id, CapturedTarget validatedTarget) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
