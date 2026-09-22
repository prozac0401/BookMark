using System.Drawing;
using System.Drawing.Imaging;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerFormChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type StickerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.StickerForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var sample = Sample();
        using var form = Create(sample);
        Bookmark? resumed = null, removed = null, edited = null;
        int resumeCount = 0, undoCount = 0;
        On(form, "ResumeRequested", (Action<Bookmark>)(value => { resumed = value; resumeCount++; }));
        On(form, "DeleteRequested", (Action<Bookmark>)(value => removed = value));
        On(form, "NoteRequested", (Action<Bookmark>)(value => edited = value));
        On(form, "UndoRequested", (Action)(() => undoCount++));
        var placement = new PlacementObserver();
        var changed = StickerType.GetEvent("PlacementChanged")!;
        var parameter = Expression.Parameter(StickerType, "sticker");
        changed.AddEventHandler(form, Expression.Lambda(changed.EventHandlerType!,
            Expression.Call(Expression.Constant(placement), typeof(PlacementObserver).GetMethod(nameof(PlacementObserver.Changed))!,
                Expression.Convert(parameter, typeof(Form))), parameter).Compile());

        form.Location = new Point(40, 40);
        _ = form.Handle;
        assert((GetWindowLong(form.Handle, -20) & 0x08000000) == 0,
            "STK01 sticker accepts normal activation for accessible controls and keyboard navigation");
        assert((bool)StickerType.GetProperty("ShowWithoutActivation", Instance)!.GetValue(form)! && !form.ShowInTaskbar,
            "STK02 initial sticker display avoids focus stealing and taskbar clutter");
        form.Size = new Size(390, 365);
        var fullSize = form.Size;
        Apply(form, true, true);
        assert(Get<bool>(form, "IsCollapsed") && form.TopMost && form.Height < fullSize.Height && Get<Size>(form, "ExpandedSize") == fullSize,
            "STK03 collapse preserves expanded dimensions and applies always-on-top");
        Apply(form, false, false);
        assert(!Get<bool>(form, "IsCollapsed") && form.TopMost && form.Size == fullSize && placement.Count == 0,
            "STK04 restoring a legacy unpinned presentation keeps the sticker above ordinary windows and returns its full size");
        Command(form, Keys.Control | Keys.Space);
        form.Width += 40;
        Command(form, Keys.Control | Keys.Space);
        assert(form.Size == new Size(fullSize.Width + 40, fullSize.Height) && placement.Count == 2,
            "STK05 resizing a collapsed sticker retains its new width and original expanded height");
        var minimum = form.MinimumSize;
        form.Size = minimum;
        form.PerformLayout();
        assert(Field<RichTextBox>(form, "_note").Bottom < Field<Button>(form, "_resume").Top &&
            Field<RichTextBox>(form, "_note").ClientSize.Height >= Field<RichTextBox>(form, "_note").Font.Height + 4 &&
            Field<Label>(form, "_location").Bottom < Field<Label>(form, "_noteLabel").Top,
            "STK06 minimum size preserves non-overlapping note, location and action controls");

        form.Show();
        Application.DoEvents();
        var updated = sample with { Note = "변경한 다음 작업", Target = sample.Target with { CellAddress = "$F$90" } };
        Invoke(form, "UpdateBookmark", updated);
        Field<Button>(form, "_resume").PerformClick();
        Command(form, Keys.Control | Keys.E);
        assert(resumed == updated && edited == updated && Field<RichTextBox>(form, "_note").Text == updated.Note,
            "STK07 resume and note commands always use the current shared bookmark revision");
        Invoke(form, "SetBusy", true);
        Command(form, Keys.Control | Keys.Enter);
        Command(form, Keys.Control | Keys.Delete);
        assert(resumeCount == 1 && removed is null && !Field<Button>(form, "_resume").Enabled,
            "STK08 busy state prevents duplicate resume and deletion commands");
        Invoke(form, "SetBusy", false);
        Command(form, Keys.Control | Keys.Delete);
        assert(removed == updated, "STK09 explicit delete routes the bookmark identity to shared storage");
        removed = null;
        Command(form, Keys.Control | Keys.Z);
        form.Close();
        assert(undoCount == 1 && !form.IsDisposed && !form.Visible && removed is null,
            "STK10 undo is routed centrally while native close only hides the sticker");
        form.Show();
        Command(form, Keys.Escape);
        assert(!form.IsDisposed && !form.Visible && removed is null,
            "STK11 Escape hides the sticker without deleting or disposing its bookmark");
        Invoke(form, "UpdateBookmark", updated with { LastResumeResult = ResultCode.TargetUnavailable });
        assert(Field<Label>(form, "_location").Text.Contains("접근할 수 없습니다", StringComparison.Ordinal),
            "STK12 failed resume leaves an actionable message on the originating sticker");
    }

    internal static void Render(string directory)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Directory.CreateDirectory(directory);
        using var form = Create(Sample());
        form.Location = new Point(60, 60);
        form.Show();
        Application.DoEvents();
        Save("sticker-default.png");
        Invoke(form, "UpdateBookmark", Sample() with { Note = "" });
        Save("sticker-empty-note.png");
        Invoke(form, "UpdateBookmark", Sample());
        form.Size = form.MinimumSize;
        Save("sticker-minimum.png");
        form.Size = new Size(460, 400);
        Invoke(form, "UpdateBookmark", Sample() with { Note = string.Concat(Enumerable.Repeat("고객별 변경 내용을 확인하고 다음 회의 전에 최종 수량을 확정합니다. 담당자에게 전달한 뒤 F90 셀을 다시 확인합니다. ", 10))[..500] });
        Save("sticker-long-note.png");
        Apply(form, true, true);
        Save("sticker-collapsed.png");

        void Save(string name)
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
        var target = new CapturedTarget(TargetKind.ExcelCell, @"C:\업무\2026년 3분기 운영 계획.xlsx", "실행 계획", "$F$42");
        return new(Guid.NewGuid(), target, target.Path, "2026년 3분기 운영 계획.xlsx", "수량 변경 내용을 확인하고 운영팀에 전달하기", now, now, 10, null, null, null, null);
    }

    private static Form Create(Bookmark bookmark) => (Form)Activator.CreateInstance(StickerType, bookmark)!;
    private static void On(Form form, string name, Delegate handler) => StickerType.GetEvent(name)!.AddEventHandler(form, handler);
    private static T Get<T>(Form form, string name) => (T)StickerType.GetProperty(name)!.GetValue(form)!;
    private static T Field<T>(Form form, string name) => (T)StickerType.GetField(name, Instance)!.GetValue(form)!;
    private static void Invoke(Form form, string name, params object[] arguments) => StickerType.GetMethod(name)!.Invoke(form, arguments);
    private static void Apply(Form form, bool collapsed, bool topmost) => Invoke(form, "ApplyPresentation", collapsed, topmost);
    private static bool Command(Form form, Keys keys)
    {
        object[] arguments = [Message.Create(form.Handle, 0x0100, (nint)(int)keys, nint.Zero), keys];
        return (bool)StickerType.GetMethod("ProcessCmdKey", Instance)!.Invoke(form, arguments)!;
    }
    private sealed class PlacementObserver
    {
        internal int Count;
        public void Changed(Form form) => Count++;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
}
