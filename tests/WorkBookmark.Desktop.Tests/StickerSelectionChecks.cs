using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerSelectionChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var folder = Item("선택 폴더", 1) with { Target = new(TargetKind.BookmarkFolder, ""), FolderExpanded = true };
        var child = Item("폴더 안 항목", 2) with { ParentId = folder.Id };
        var first = Item("첫 선택", 3);
        var second = Item("두 번째 선택", 4);
        using var fixture = new Fixture(folder, child, first, second);
        Form a = fixture.View(first.Id), b = fixture.View(second.Id), root = fixture.View(folder.Id), nested = fixture.View(child.Id);
        int edits = 0;
        a.GetType().GetEvent("NoteRequested", Members)!.AddEventHandler(a, (Action<Bookmark>)(_ => edits++));
        var title = (Control)a.GetType().GetField("_title", Members)!.GetValue(a)!;
        Mouse(title, "OnMouseDown");
        Mouse(title, "OnMouseUp");
        assert(fixture.Selected().SetEquals([first.Id]) && Selected(a) && !Selected(b) && edits == 0,
            "SM01 a title mouse click selects one sticker without opening the editor");

        Gesture(b, Keys.Control);
        assert(fixture.Selected().SetEquals([first.Id, second.Id]) && Selected(a) && Selected(b),
            "SM02 Ctrl-click adds another root and keeps both selected after mouse-up");
        var previousPaint = BorderPixel(a);
        b.Focus();
        Application.DoEvents();
        assert(Selected(a) && BorderPixel(a) == previousPaint && previousPaint.ToArgb() != Color.White.ToArgb(),
            "SM03 the selected border remains visible when another sticker receives focus");

        Gesture(a, Keys.Control);
        assert(fixture.Selected().SetEquals([second.Id]) && !Selected(a),
            "SM04 Ctrl-click toggles a previously selected sticker off");
        Gesture(a, Keys.Control);
        Call(a, "BeginSelectionPointer", Keys.None);
        assert(fixture.Selected().SetEquals([first.Id, second.Id]),
            "SM05 pressing a selected sticker preserves the group while a drag may begin");
        // BeginPointerMove marks the pointer gesture even when the synthetic
        // check has no physical left button held and the manager declines to drag.
        Call(a, "BeginPointerMove", (Point?)a.PointToScreen(new Point(40, 10)));
        Call(a, "EndSelectionPointer");
        assert(fixture.Selected().SetEquals([first.Id, second.Id]),
            "SM06 a drag gesture does not collapse the group on mouse-up");
        Gesture(a, Keys.None);
        assert(fixture.Selected().SetEquals([first.Id]) && !Selected(b),
            "SM07 an ordinary click without dragging returns to a single selected sticker");

        Pointer(title, 0x0201, 0x0009);
        Pointer(title, 0x0202, 0);
        Gesture(b, Keys.Control);
        Pointer(title, 0x0203, 0x0001);
        assert(title.Capture && edits == 0 && fixture.Selected().SetEquals([first.Id, second.Id]),
            "SM14 a quick press after releasing Ctrl keeps native capture and does not open the editor");
        Mouse(title, "OnMouseMove", 12 + SystemInformation.DragSize.Width + 8);
        Pointer(title, 0x0202, 0);
        assert(edits == 0 && fixture.Selected().SetEquals([first.Id, second.Id]),
            "SM15 the quick post-Ctrl gesture can drag the complete selection");
        Gesture(a, Keys.None);

        Gesture(root, Keys.Control);
        Gesture(nested, Keys.Control);
        assert(fixture.Selected().SetEquals([first.Id, folder.Id, child.Id]) && Selected(nested),
            "SM08 roots and embedded child cards share one Ctrl selection across trees");
        Call(a, "BeginSelectionPointer", Keys.Control);
        Call(a, "EndSelectionPointer");
        assert(fixture.Selected().SetEquals([folder.Id, child.Id]),
            "SM09 Ctrl state is captured at pointer-down and release does not replace the selection");

        Command(root, Keys.Escape);
        assert(fixture.Selected().Count == 0 && root.Visible && nested.Visible && !Selected(root) && !Selected(nested),
            "SM10 Escape clears the selection without hiding the tree");
        Gesture(nested, Keys.None);
        fixture.Publish(folder with { FolderExpanded = false });
        assert(!nested.Visible && fixture.Selected().Count == 0 && !Selected(nested),
            "SM11 collapsing a folder removes hidden children from the move selection");
        Gesture(a, Keys.None);
        Call(fixture.Manager, "Remove", first.Id);
        assert(!fixture.Selected().Contains(first.Id),
            "SM12 removing a bookmark cannot leave a stale selected identity");

        Gesture(b, Keys.None);
        Pump((Task)Call(fixture.Manager, "HideAllAsync")!);
        assert(fixture.Selected().Count == 0 && !b.Visible,
            "SM13 hiding all stickers clears the previous move selection");
    }

    internal static void Render(string path)
    {
        var folder = Item("프로젝트 자료", 10) with { Target = new(TargetKind.BookmarkFolder, ""), FolderExpanded = true };
        var child = Item("선택한 하위 북마크", 11) with { ParentId = folder.Id };
        var first = Item("함께 이동할 북마크", 12) with { Color = BookmarkColor.Blue };
        var second = Item("선택하지 않은 북마크", 13) with { Color = BookmarkColor.Yellow };
        using var fixture = new Fixture(folder, child, first, second);
        Form root = fixture.View(folder.Id), nested = fixture.View(child.Id), a = fixture.View(first.Id), b = fixture.View(second.Id);
        Gesture(root, Keys.None); Gesture(a, Keys.Control); Gesture(nested, Keys.Control);
        b.Focus();
        Application.DoEvents();
        (string Caption, Form Form)[] cards = [("선택된 폴더", root), ("선택하지 않은 항목", b), ("선택된 다른 루트", a), ("선택된 하위 항목", nested)];
        int cardWidth = cards.Max(card => card.Form.Width), cardHeight = cards.Max(card => card.Form.Height);
        using var image = new Bitmap(cardWidth * 2 + 72, cardHeight * 2 + 154);
        using var graphics = Graphics.FromImage(image);
        using var font = new Font("맑은 고딕", 10F);
        graphics.Clear(Color.FromArgb(238, 241, 244));
        using var brush = new SolidBrush(Color.FromArgb(35, 48, 60));
        for (int index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            int x = 24 + index % 2 * (cardWidth + 24), y = 24 + index / 2 * (cardHeight + 56);
            graphics.DrawString(card.Caption, font, brush, x, y);
            using var rendered = new Bitmap(card.Form.Width, card.Form.Height);
            card.Form.DrawToBitmap(rendered, new Rectangle(Point.Empty, card.Form.Size));
            graphics.DrawImageUnscaled(rendered, x, y + 26);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        image.Save(path, ImageFormat.Png);
    }

    private static void Gesture(Form form, Keys modifiers)
    {
        Call(form, "BeginSelectionPointer", modifiers);
        Call(form, "EndSelectionPointer");
    }
    private static bool Selected(Form form) => (bool)form.GetType().GetProperty("SelectedForMove", Members)!.GetValue(form)!;
    private static void Mouse(Control control, string method, int x = 8) => Call(control, method, new MouseEventArgs(MouseButtons.Left, 1, x, 8, 0));
    private static void Pointer(Control control, int message, int buttons) => SendMessage(control.Handle, message, buttons, (nint)(12 | 8 << 16));
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    private static void Command(Form form, Keys key)
    {
        object?[] arguments = [Message.Create(form.Handle, 0, nint.Zero, nint.Zero), key];
        form.GetType().GetMethod("ProcessCmdKey", Members)!.Invoke(form, arguments);
    }
    private static Color BorderPixel(Form form)
    {
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        Call(form, "OnPaint", new PaintEventArgs(graphics, form.ClientRectangle));
        return bitmap.GetPixel(bitmap.Width / 2, 1);
    }
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Members)!.Invoke(target, args);
    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new(TargetKind.File, @"C:\selection-checks\" + sequence + ".txt"),
        title, title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Sticker selection check timed out.");
        task.GetAwaiter().GetResult();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<Guid, Bookmark> _items;
        internal object Manager { get; }
        internal Fixture(params Bookmark[] items)
        {
            _items = items.ToDictionary(item => item.Id);
            Func<Task<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>> load = () =>
                Task.FromResult(((IReadOnlyList<Bookmark>)_items.Values.ToArray(), (IReadOnlyList<StickerLayout>)Array.Empty<StickerLayout>()));
            Manager = ManagerType.GetConstructors(Members).Single().Invoke([load,
                (Action<IReadOnlyList<StickerLayout>>)(_ => { }), (Action<string>)(_ => { })]);
            Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!);
        }
        internal Form View(Guid id) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Members)!.GetValue(Manager)!)
            .Cast<Form>().Single(form => ((Bookmark)form.GetType().GetProperty("Bookmark", Members)!.GetValue(form)!).Id == id);
        internal HashSet<Guid> Selected() => ((IReadOnlyCollection<Guid>)ManagerType.GetProperty("SelectedBookmarkIds", Members)!.GetValue(Manager)!).ToHashSet();
        internal void Publish(Bookmark item)
        {
            _items[item.Id] = item;
            Call(Manager, "ApplyFolderChanges", new[] { item }, null);
        }
        public void Dispose()
        {
            Pump((Task)Call(Manager, "SavePendingAsync")!);
            ((IDisposable)Manager).Dispose();
        }
    }
}
