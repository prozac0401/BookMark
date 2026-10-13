using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class BookmarkColorChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly App = typeof(BookmarkApplicationContext).Assembly;
    private static readonly Type StickerType = App.GetType("WorkBookmark.App.UI.StickerForm")!;
    private static readonly Type RecentType = App.GetType("WorkBookmark.App.UI.RecentForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var parent = Make("프로젝트", BookmarkColor.Yellow);
        var child = Make("검토 문서", BookmarkColor.Blue, parent.Id);
        using var sticker = (Form)Activator.CreateInstance(StickerType, child)!;
        sticker.Location = new Point(60, 70);
        Invoke(sticker, "SetHierarchy", 1, parent.DisplayName, 0);
        var originalBounds = sticker.Bounds;
        var originalPaper = sticker.BackColor;
        var menu = Field<ToolStripMenuItem>(sticker, "_colorMenu");
        var requests = new List<(Guid Id, BookmarkColor Color)>();
        StickerType.GetEvent("ColorChangeRequested")!.AddEventHandler(sticker,
            (Action<Bookmark, BookmarkColor>)((item, color) => requests.Add((item.Id, color))));
        Invoke(sticker, "UpdateMenu");
        assert(menu.Text == "색상" && menu.DropDownItems.Count == 7 && Checked(menu) == BookmarkColor.Blue &&
            menu.DropDownItems.Cast<ToolStripMenuItem>().All(item => item.Image is not null),
            "BC01 sticker color menu exposes seven swatches and checks its committed color");
        Choice(menu, BookmarkColor.Pink).PerformClick();
        assert(requests.SequenceEqual([(child.Id, BookmarkColor.Pink)]) && Bookmark(sticker).Color == BookmarkColor.Blue && sticker.BackColor == originalPaper,
            "BC02 selecting a sticker color requests persistence and retains its committed appearance until success");
        Choice(menu, BookmarkColor.Blue).PerformClick();
        assert(requests.Count == 1, "BC03 selecting the existing color does not request a redundant write");

        child = child with { Color = BookmarkColor.Pink };
        Invoke(sticker, "UpdateBookmark", child);
        assert(sticker.BackColor != originalPaper && sticker.Bounds == originalBounds && Checked(menu) == BookmarkColor.Pink &&
            new[] { "_header", "_title", "_treeMark", "_noteEditor", "_noteStatus", "_cancelNote" }.All(name => Field<Control>(sticker, name).BackColor == sticker.BackColor) &&
            Field<int>(sticker, "_treeDepth") == 1,
            "BC04 committed color refresh updates all paper surfaces while preserving compact geometry and hierarchy");
        Color committedPink = sticker.BackColor;
        Invoke(sticker, "SetDropTarget", true);
        Color dropPaper = sticker.BackColor;
        child = child with { Color = BookmarkColor.Purple };
        Invoke(sticker, "UpdateBookmark", child);
        assert(dropPaper != committedPink && sticker.BackColor == dropPaper && Field<Control>(sticker, "_title").BackColor == dropPaper,
            "BC05 a color refresh preserves the active sticker drop-target highlight");
        Invoke(sticker, "SetDropTarget", false);
        assert(sticker.BackColor != dropPaper && sticker.BackColor != committedPink && Bookmark(sticker).Color == BookmarkColor.Purple,
            "BC06 leaving a drop target restores the latest committed color");

        Invoke(sticker, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        var editor = Field<TextBox>(sticker, "_noteEditor");
        editor.Text = "색상을 바꿔도 유지할 작성 중인 메모";
        Rectangle editingBounds = sticker.Bounds;
        Rectangle placement = (Rectangle)StickerType.GetProperty("PlacementBounds")!.GetValue(sticker)!;
        child = child with { Color = BookmarkColor.Yellow };
        Invoke(sticker, "UpdateBookmark", child);
        assert(editor.Text == "색상을 바꿔도 유지할 작성 중인 메모" && editor.BackColor == sticker.BackColor && sticker.Bounds == editingBounds &&
            (Rectangle)StickerType.GetProperty("PlacementBounds")!.GetValue(sticker)! == placement && (bool)StickerType.GetProperty("IsEditingNote")!.GetValue(sticker)!,
            "BC07 committed color changes preserve an inline draft, its temporary bounds and saved placement");
        Invoke(sticker, "CancelNoteEdit");
        assert(sticker.Bounds == originalBounds && Bookmark(sticker).Color == BookmarkColor.Yellow,
            "BC08 cancelling the draft restores compact bounds and retains the independently saved color");
        Invoke(sticker, "SetBusy", true);
        Invoke(sticker, "UpdateMenu");
        Choice(menu, BookmarkColor.Gray).PerformClick();
        assert(!menu.Enabled && requests.Count == 1, "BC09 a busy sticker cannot issue a color request");
        Invoke(sticker, "SetBusy", false);

        Bookmark[] records = [parent, child];
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records, false));
        using var recent = (Form)Activator.CreateInstance(RecentType, load)!;
        Pump((Task)Invoke(recent, "OpenAsync")!);
        var list = Field<ListBox>(recent, "_items");
        list.SelectedIndex = list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == child.Id);
        var listMenu = Field<ToolStripMenuItem>(recent, "_colorMenu");
        var listRequests = new List<(Guid Id, BookmarkColor Color)>();
        RecentType.GetEvent("ColorChangeRequested")!.AddEventHandler(recent,
            (Action<Bookmark, BookmarkColor>)((item, color) => listRequests.Add((item.Id, color))));
        Choice(listMenu, BookmarkColor.Green).PerformClick();
        assert(listRequests.SequenceEqual([(child.Id, BookmarkColor.Green)]) && ((Bookmark)list.SelectedItem!).Color == BookmarkColor.Yellow,
            "BC10 list color menu emits the selected bookmark request without optimistic mutation");
        child = child with { Color = BookmarkColor.Green };
        Invoke(recent, "Replace", child);
        var rows = Field<Dictionary<Guid, BookmarkTreeRow>>(recent, "_rows");
        assert(((Bookmark)list.SelectedItem!).Color == BookmarkColor.Green && Checked(listMenu) == BookmarkColor.Green && rows[child.Id].Depth == 1 &&
            list.Items.Cast<Bookmark>().Single(item => item.Id == parent.Id).Color == BookmarkColor.Yellow,
            "BC11 list refresh retains tree nesting and selection while changing only the committed bookmark color");
        int index = list.SelectedIndex;
        using var ordinary = DrawRow(recent, index, DrawItemState.None);
        using var selected = DrawRow(recent, index, DrawItemState.Selected);
        RecentType.GetField("_dropTargetId", Private)!.SetValue(recent, (Guid?)child.Id);
        using var target = DrawRow(recent, index, DrawItemState.None);
        int swatchX = (int)Math.Round(list.DeviceDpi / 96F) + Math.Max(1, (int)Math.Round(3 * list.DeviceDpi / 96F) - 1);
        assert(ordinary.GetPixel(590, 100) != selected.GetPixel(590, 100) && selected.GetPixel(590, 100) != target.GetPixel(590, 100) &&
            ordinary.GetPixel(swatchX, 50) == selected.GetPixel(swatchX, 50) && selected.GetPixel(swatchX, 50) == target.GetPixel(swatchX, 50),
            "BC12 rendered list preserves the color swatch while selection and drop backgrounds remain distinct");
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        string[] names = ["기본 · 업무 정리", "노랑 · 출시 계획", "초록 · 검토 완료", "파랑 · 참고 자료", "분홍 · 고객 의견", "보라 · 아이디어", "회색 · 보관 항목"];
        var root = Make(names[0], BookmarkColor.Default);
        Bookmark[] records = Enum.GetValues<BookmarkColor>().Select((color, index) => index == 0 ? root : Make(names[index], color, root.Id)).ToArray();
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records, false));
        using (var recent = (Form)Activator.CreateInstance(RecentType, load)!)
        {
            Pump((Task)Invoke(recent, "OpenAsync")!);
            var list = Field<ListBox>(recent, "_items");
            recent.ClientSize = new Size(660, 760);
            list.SetBounds(16, 58, 628, 644);
            Field<Label>(recent, "_status").Top = 722;
            list.SelectedIndex = -1;
            Save(recent, Path.Combine(directory, "list-colors.png"));
        }
        using var sheet = new Bitmap(620, 350);
        using (Graphics graphics = Graphics.FromImage(sheet))
        {
            graphics.Clear(Color.FromArgb(248, 250, 249));
            using var labelFont = new Font("맑은 고딕", 10);
            for (int index = 0; index < records.Length; index++)
            {
                using var sticker = (Form)Activator.CreateInstance(StickerType, records[index])!;
                sticker.Location = new Point(40, 50);
                if (index > 0) Invoke(sticker, "SetHierarchy", 1, root.DisplayName, 0);
                sticker.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(sticker.Width, sticker.Height);
                sticker.DrawToBitmap(bitmap, new Rectangle(Point.Empty, sticker.Size));
                int left = 14 + index % 2 * 300, top = 12 + index / 2 * 82;
                graphics.DrawString(names[index].Split('·')[0].Trim(), labelFont, Brushes.DimGray, left, top);
                graphics.DrawImageUnscaled(bitmap, left, top + 26);
                sticker.Hide();
            }
        }
        sheet.Save(Path.Combine(directory, "sticker-colors.png"), ImageFormat.Png);
        using var editing = (Form)Activator.CreateInstance(StickerType, records[3])!;
        editing.Location = new Point(40, 50);
        editing.Show();
        Invoke(editing, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        Field<TextBox>(editing, "_noteEditor").Text = "같은 색상으로 메모를 이어서 작성합니다.";
        Save(editing, Path.Combine(directory, "editing-color.png"));
        Invoke(editing, "CancelNoteEdit");
    }

    private static Bitmap DrawRow(Form form, int index, DrawItemState state)
    {
        var bitmap = new Bitmap(600, 120);
        using var graphics = Graphics.FromImage(bitmap);
        Invoke(form, "DrawBookmark", Field<ListBox>(form, "_items"), new DrawItemEventArgs(graphics, form.Font, new Rectangle(0, 0, 600, 120), index, state));
        return bitmap;
    }
    private static void Save(Form form, string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
    private static Bookmark Make(string title, BookmarkColor color, Guid? parent = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Bookmark(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\업무\" + title + ".txt"), title, title, "", now, now, (int)color + 1, null, null, null, null, parent, color);
    }
    private static Bookmark Bookmark(Form form) => (Bookmark)StickerType.GetProperty("Bookmark")!.GetValue(form)!;
    private static ToolStripMenuItem Choice(ToolStripMenuItem menu, BookmarkColor color) => menu.DropDownItems.Cast<ToolStripMenuItem>().Single(item => (BookmarkColor)item.Tag! == color);
    private static BookmarkColor Checked(ToolStripMenuItem menu) => (BookmarkColor)menu.DropDownItems.Cast<ToolStripMenuItem>().Single(item => item.Checked).Tag!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string method, params object[] arguments) => value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(value, arguments);
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Bookmark color operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
