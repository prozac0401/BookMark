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
        var defaultClientSize = form.ClientSize;
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
        var resume = Field<Button>(form, "_resume");
        var minimumTitle = Field<Button>(form, "_title");
        assert(minimumTitle.Right <= resume.Left && minimumTitle.Height >= TextHeight(minimumTitle, "가Ag") &&
            minimumTitle.Height < TextHeight(minimumTitle, "가Ag\n가Ag") &&
            form.ClientRectangle.Contains(minimumTitle.Bounds) && form.ClientRectangle.Contains(resume.Bounds) &&
            resume.Size == new Size(Px(form, 28), Px(form, 28)) &&
            form.ClientSize == new Size(Px(form, 240), Px(form, 36)),
            "STK06 minimum one-line title and shortcut icon fit side by side without overlap");

        form.Show();
        Application.DoEvents();
        var updated = sample with { Note = "변경한 다음 작업", Target = sample.Target with { CellAddress = "$F$90" } };
        Invoke(form, "UpdateBookmark", updated);
        Field<Button>(form, "_resume").PerformClick();
        Command(form, Keys.Control | Keys.E);
        assert(resumed == updated && edited == updated && Field<Button>(form, "_title").Text == updated.Note,
            "STK07 resume and note commands always use the current shared bookmark revision");
        Invoke(form, "SetBusy", true);
        Command(form, Keys.Control | Keys.Enter);
        Command(form, Keys.Control | Keys.Delete);
        assert(resumeCount == 1 && removed is null && !resume.Enabled && form.Size == minimum &&
            !Field<Label>(form, "_resumeStatus").Visible && resume.Text != "이어가기" &&
            resume.AccessibleName == "바로가기" &&
            Field<ToolTip>(form, "_tooltip").GetToolTip(resume)?.Contains("처리 중", StringComparison.Ordinal) == true,
            "STK08 busy state prevents duplicate commands while retaining the compact icon layout");
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
        assert(Field<ToolTip>(form, "_tooltip").GetToolTip(resume)?.Contains("접근할 수 없습니다", StringComparison.Ordinal) == true &&
            resume.AccessibleDescription?.Contains("접근할 수 없습니다", StringComparison.Ordinal) == true,
            "STK12 failed resume retains an actionable message on the shortcut icon tooltip");
        form.Show();
        form.PerformLayout();
        var status = Field<Label>(form, "_resumeStatus");
        assert(!status.Visible && form.Size == minimum && minimumTitle.Right <= resume.Left &&
            form.Controls.Cast<Control>().Where(control => control.Visible).ToHashSet().SetEquals([minimumTitle, resume]) &&
            Field<ToolTip>(form, "_tooltip").GetToolTip(resume)?.Contains("접근할 수 없습니다", StringComparison.Ordinal) == true,
            "STK13 failed resume keeps only the title and shortcut icon without adding a row or enlarging the sticker");
        assert(defaultClientSize == new Size(Px(form, 260), Px(form, 36)) &&
            Math.Abs(form.Font.SizeInPoints - 9F) < .01F &&
            Math.Abs(Field<Button>(form, "_title").Font.SizeInPoints - 9.5F) < .01F,
            $"STK14 default width, height and type scale are compact (initialClient={defaultClientSize}, dpi={form.DeviceDpi}, font={form.Font.SizeInPoints}, titleFont={Field<Button>(form, "_title").Font.SizeInPoints})");

        var titled = sample with { Note = "  메모만 제목으로 표시합니다  " };
        Invoke(form, "UpdateBookmark", titled);
        var title = Field<Button>(form, "_title");
        assert(title.Text == titled.Note.Trim() && form.Text.StartsWith(title.Text, StringComparison.Ordinal) &&
            !status.Visible && !form.Controls.OfType<RichTextBox>().Any() &&
            Field<ToolTip>(form, "_tooltip").GetToolTip(title)?.Contains(sample.Target.Path, StringComparison.Ordinal) == true &&
            Get<Bookmark>(form, "Bookmark") == titled,
            "STK15 note titles hide duplicate target details while retaining tooltips and stored data");
        assert(form.Controls.Cast<Control>().Where(control => control.Visible).ToHashSet().SetEquals([title, Field<Button>(form, "_resume")]) &&
            (GetWindowLong(form.Handle, -16) & 0x00C00000) == 0 && form.ContextMenuStrip is not null &&
            resume.AccessibleName == "바로가기" && resume.Text != "이어가기" &&
            Field<ToolTip>(form, "_tooltip").GetToolTip(resume)?.Contains("바로가기", StringComparison.Ordinal) == true,
            "STK20 normal stickers show only title and the accessible shortcut icon, with caption removed and a context menu available");
        ShortcutGlyphRemainsVisible(form, resume, title, assert);
        var menu = Field<ContextMenuStrip>(form, "_menu");
        var deleteItem = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "책갈피 지우기");
        removed = null;
        deleteItem.PerformClick();
        assert(removed == titled && Get<Rectangle>(form, "PlacementBounds").Size == Get<Size>(form, "ExpandedSize"),
            "STK21 the context menu deletes the same bookmark while persisted placement remains the normal size");
        Apply(form, true, true);
        assert(title.Visible && title.Text == titled.Note.Trim() && !Field<Button>(form, "_resume").Visible &&
            form.Controls.Cast<Control>().Where(control => control.Visible).SequenceEqual([title]) &&
            Get<bool>(form, "PlacementIsCollapsed"),
            "STK16 collapsed stickers show only their memo-first title and retain the collapsed placement");
        Apply(form, false, true);

        var fallbackCases = new[]
        {
            (Target: sample.Target, DisplayName: "원래 Office 문서 제목", Expected: "2026년 3분기 운영 계획.xlsx", Note: ""),
            (Target: new CapturedTarget(TargetKind.WebPage, "https://example.test/folder?id=42"), DisplayName: "웹 제목", Expected: "https://example.test/folder?id=42", Note: "   "),
            (Target: new CapturedTarget(TargetKind.Folder, @"C:\업무\선행연구"), DisplayName: "선행연구", Expected: @"C:\업무\선행연구", Note: ""),
            (Target: sample.Target with { Path = "https://example.test/운영.xlsx" }, DisplayName: "웹 문서", Expected: "https://example.test/운영.xlsx", Note: "\t "),
            (Target: new CapturedTarget(TargetKind.NotepadSnapshot, "snapshot-private-id"), DisplayName: "자동 보관한 메모", Expected: "자동 보관한 메모", Note: "")
        };
        foreach (var item in fallbackCases)
        {
            Invoke(form, "UpdateBookmark", sample with { Target = item.Target, DisplayName = item.DisplayName, Note = item.Note });
            assert(title.Text == item.Expected && form.Text.StartsWith(item.Expected, StringComparison.Ordinal) && !status.Visible,
                $"STK17 empty or whitespace memo uses the appropriate {item.Target.Kind} fallback");
        }
        string longNote = string.Concat(Enumerable.Repeat("긴 메모에서 전체 내용을 확인하고 다음 작업으로 이어갑니다. ", 20));
        Invoke(form, "UpdateBookmark", sample with { Note = longNote });
        assert(title.Text == longNote.Trim() && title.AutoEllipsis && title.TabStop && title.CanSelect &&
            title.Right <= resume.Left &&
            Field<ToolTip>(form, "_tooltip").GetToolTip(title)?.Contains(longNote.Trim(), StringComparison.Ordinal) == true,
            "STK18 long notes remain keyboard reachable in one-line bounds with complete tooltip text");
        edited = null;
        title.Focus();
        Command(form, Keys.Enter);
        assert(edited?.Note == longNote && resumeCount == 1,
            "STK19 Enter on the focused title requests note editing without resuming the target");

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
        Invoke(form, "UpdateBookmark", Sample() with { Note = "", Target = new CapturedTarget(TargetKind.WebPage, "https://drive.google.com/drive/folders/example-work-folder") });
        Save("sticker-url-fallback.png");
        Invoke(form, "UpdateBookmark", Sample() with { Note = "", Target = new CapturedTarget(TargetKind.Folder, @"D:\02_Research\2026_2학기 수업내용\선행연구") });
        Save("sticker-folder-fallback.png");
        Invoke(form, "UpdateBookmark", Sample());
        form.Size = form.MinimumSize;
        Save("sticker-minimum.png");
        Invoke(form, "UpdateBookmark", Sample() with { Note = string.Concat(Enumerable.Repeat("고객별 변경 내용을 확인하고 다음 회의 전에 최종 수량을 확정합니다. 담당자에게 전달한 뒤 F90 셀을 다시 확인합니다. ", 10))[..500] });
        Save("sticker-long-note.png");
        Invoke(form, "UpdateBookmark", Sample() with { LastResumeResult = ResultCode.TargetUnavailable });
        Save("sticker-resume-error.png");
        Invoke(form, "UpdateBookmark", Sample());
        Invoke(form, "SetBusy", true);
        Save("sticker-shortcut-busy.png");
        Invoke(form, "SetBusy", false);
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

    private static void ShortcutGlyphRemainsVisible(Form form, Button shortcut, Button title, Action<bool, string> assert)
    {
        // Exercise real button paint states without moving or clicking the shared pointer.
        // Sample only the interior so focus rectangles and borders cannot count as the icon.
        var samples = new List<(string State, int White, int Background, int Area)>();
        void Mouse(string method) => typeof(Button).GetMethod(method, Instance)!.Invoke(shortcut, [EventArgs.Empty]);
        void Capture(string state)
        {
            using var bitmap = new Bitmap(shortcut.Width, shortcut.Height);
            shortcut.DrawToBitmap(bitmap, new Rectangle(Point.Empty, shortcut.Size));
            int inset = Px(form, 5), white = 0, background = 0;
            var interior = Rectangle.Inflate(shortcut.ClientRectangle, -inset, -inset);
            for (int y = interior.Top; y < interior.Bottom; y++)
            for (int x = interior.Left; x < interior.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 240 && pixel.R >= 235 && pixel.G >= 235 && pixel.B >= 235) white++;
                if (pixel.A >= 240 && pixel.R < 200 && pixel.G < 200 && pixel.B < 200) background++;
            }
            samples.Add((state, white, background, interior.Width * interior.Height));
        }

        bool focused;
        try
        {
            Mouse("OnMouseLeave");
            Capture("normal");
            Mouse("OnMouseEnter");
            Capture("hover");
            SendMessage(shortcut.Handle, 0x00F3, 1, 0); // BM_SETSTATE: native pressed appearance, no click.
            Capture("pressed");
            SendMessage(shortcut.Handle, 0x00F3, 0, 0);
            Mouse("OnMouseLeave");
            focused = shortcut.Focus();
            Capture("focused");
        }
        finally
        {
            SendMessage(shortcut.Handle, 0x00F3, 0, 0);
            Mouse("OnMouseLeave");
            title.Focus();
        }
        int minimumWhite = Math.Max(10, (int)Math.Round(10 * Math.Pow(form.DeviceDpi / 96D, 2)));
        assert(focused && samples.All(sample => sample.White >= minimumWhite && sample.Background >= sample.Area / 3),
            $"STK22 shortcut glyph remains visible after native button painting in every state (dpi={form.DeviceDpi}; " +
            string.Join(", ", samples.Select(sample => $"{sample.State}: white={sample.White}, background={sample.Background}")) + ")");
    }

    private static int TextHeight(Control control, string text)
    {
        using Graphics graphics = control.CreateGraphics();
        return TextRenderer.MeasureText(graphics, text, control.Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
    }

    private static int Px(Form form, int value) => (int)Math.Round(value * form.DeviceDpi / 96F);
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

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
}
