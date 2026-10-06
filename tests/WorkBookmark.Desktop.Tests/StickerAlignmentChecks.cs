using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerAlignmentChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F17), RecentHotkey = new Hotkey(7, (int)Keys.F18),
            DisplayMode = BookmarkDisplayMode.Stickers, IntroShown = true,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion,
            StickerSnapEnabled = false
        }).Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        var first = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "alignment-first.txt"))).Bookmark;
        repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "alignment-second.txt")));
        repository.UpdateNote(first.Id, "정렬 후에도 유지할 메모");
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        try
        {
            object manager = Field<object>(context, "_stickers");
            PumpUntil(() => Forms(manager).Length == 2 && Forms(manager).All(form => form.Visible));
            var moving = Forms(manager).Single(form => BookmarkFor(form).Id == first.Id);
            var peer = Forms(manager).Single(form => form != moving);
            var screen = Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;
            foreach (var form in Forms(manager)) form.Location = area.Location + new Size(32, 32);
            int gap = (int)Math.Round(8 * moving.DeviceDpi / 96d);
            var proposed = new Rectangle(peer.Right + gap + 3, peer.Top + 3, moving.Width, moving.Height);
            var expected = new Rectangle(peer.Right + gap, peer.Top, moving.Width, moving.Height);
            assert(!(bool)manager.GetType().GetProperty("SnapEnabled", Private)!.GetValue(manager)! &&
                Snap(manager, moving, proposed, Keys.None) == proposed,
                "SA01 startup applies the saved magnetic alignment opt-out to actual sticker movement");

            manager.GetType().GetProperty("SnapEnabled", Private)!.SetValue(manager, true);
            assert(Snap(manager, moving, proposed, Keys.None) == expected,
                "SA02 a visible neighboring sticker aligns the dragged sticker's row and adjacent spacing");
            assert(Snap(manager, moving, proposed, Keys.Alt) == proposed,
                "SA03 holding Alt bypasses magnetic alignment without changing the saved preference");
            peer.Hide();
            var nearSelf = new Rectangle(moving.Left + 3, moving.Top + 3, moving.Width, moving.Height);
            assert(Snap(manager, moving, proposed, Keys.None) == proposed &&
                Snap(manager, moving, nearSelf, Keys.None) == nearSelf,
                "SA04 hidden stickers and the moving sticker itself cannot attract a drag");
            peer.Show();

            // Exercise the native message and the manager's installed callback, while
            // keeping ambient physical modifier keys from affecting this synthetic drag.
            var adjustProperty = moving.GetType().GetProperty("AdjustMoveBounds", Private)!;
            var adjust = (Func<Rectangle, Keys, Rectangle>)adjustProperty.GetValue(moving)!;
            Rectangle? received = null;
            adjustProperty.SetValue(moving, (Func<Rectangle, Keys, Rectangle>)((bounds, _) =>
            {
                received = bounds;
                return adjust(bounds, Keys.None);
            }));
            Rectangle final;
            nint result;
            try { (final, result) = SendMoving(moving, proposed); }
            finally { adjustProperty.SetValue(moving, adjust); }
            assert(result == new nint(1) && received == proposed && final == expected && final.Size == proposed.Size,
                "SA05 WM_MOVING returns the manager's corrected native rectangle without resizing the sticker");
            moving.Bounds = final;
            Invoke(moving, "OnResizeEnd", EventArgs.Empty);
            Flush(manager);
            assert(PlacementMatches(repository, moving),
                "SA06 finishing the native drag persists the final snapped position in normalized monitor coordinates");

            moving.Size += new Size(24, 20);
            Invoke(moving, "OnResizeEnd", EventArgs.Empty);
            peer.Size += new Size(12, 24);
            Invoke(peer, "OnResizeEnd", EventArgs.Empty);
            Field<ToolStripMenuItem>(peer, "_collapseMenu").PerformClick();
            var ordered = Forms(manager).OrderByDescending(form => BookmarkFor(form).CaptureSequence).ToArray();
            var sizes = ordered.Select(form => form.Size).ToArray();
            var expanded = ordered.Select(form => (Size)form.GetType().GetProperty("ExpandedSize")!.GetValue(form)!).ToArray();
            var arrangement = Field<ToolStripMenuItem>(context, "_arrangeStickersMenu");
            var choices = new[] { ("격자로 정렬", "Grid"), ("가로로 정렬", "Horizontal"), ("세로로 정렬", "Vertical") };
            assert(arrangement.DropDownItems.OfType<ToolStripMenuItem>().Select(item => item.Text)
                .SequenceEqual(choices.Select(choice => choice.Item1)),
                "SA07 the tray exposes grid, horizontal and vertical arrangement choices");
            foreach (var (label, mode) in choices)
            {
                // Pure layout behavior has separate checks. Here its result verifies
                // that each real tray item dispatches the matching mode and order.
                var expectedBounds = Arrangement(ordered, area, mode);
                Invoke(manager, "HideAll");
                arrangement.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == label).PerformClick();
                PumpUntil(() => !Field<bool>(manager, "_showPending") && ordered.All(form => form.Visible));
                Flush(manager);
                assert(ordered.Select(form => form.Bounds).SequenceEqual(expectedBounds) &&
                    ordered.Select(form => form.Size).SequenceEqual(sizes) &&
                    ordered.Select(form => (Size)form.GetType().GetProperty("ExpandedSize")!.GetValue(form)!).SequenceEqual(expanded) &&
                    (bool)peer.GetType().GetProperty("IsCollapsed")!.GetValue(peer)! &&
                    ordered.All(form => PlacementMatches(repository, form)) &&
                    repository.Get(first.Id)!.Note == "정렬 후에도 유지할 메모" && BookmarkFor(moving).Note == "정렬 후에도 유지할 메모",
                    $"SA08 {mode} tray arrangement reveals stickers, dispatches the selected layout and preserves sizes, collapse state and notes in storage");
            }
        }
        finally { context.ExitThread(); }
    }

    private static IReadOnlyList<Rectangle> Arrangement(Form[] forms, Rectangle area, string mode)
    {
        var assembly = typeof(BookmarkApplicationContext).Assembly;
        var modeType = assembly.GetType("WorkBookmark.App.StickerArrangementMode")!;
        return (IReadOnlyList<Rectangle>)assembly.GetType("WorkBookmark.App.StickerArrangement")!
            .GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [forms.Select(form => form.Size).ToArray(), area, forms[0].DeviceDpi, Enum.Parse(modeType, mode)])!;
    }

    private static bool PlacementMatches(SqliteBookmarkRepository repository, Form form)
    {
        var layout = repository.GetStickerLayouts().Single(value => value.BookmarkId == BookmarkFor(form).Id);
        var bounds = (Rectangle)form.GetType().GetProperty("PlacementBounds")!.GetValue(form)!;
        var screen = Screen.FromControl(form);
        float scale = 96F / Math.Max(96, form.DeviceDpi);
        return layout.MonitorDevice == screen.DeviceName &&
            layout.Left == (int)Math.Round((bounds.Left - screen.WorkingArea.Left) * scale) &&
            layout.Top == (int)Math.Round((bounds.Top - screen.WorkingArea.Top) * scale) &&
            layout.Width == (int)Math.Round(bounds.Width * scale) && layout.Height == (int)Math.Round(bounds.Height * scale) &&
            layout.IsCollapsed == (bool)form.GetType().GetProperty("IsCollapsed")!.GetValue(form)!;
    }

    private static (Rectangle Bounds, nint Result) SendMoving(Form form, Rectangle proposed)
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        try
        {
            Marshal.StructureToPtr(new NativeRectangle
            {
                Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom
            }, pointer, false);
            nint result = SendMessage(form.Handle, 0x0216, nint.Zero, pointer);
            var final = Marshal.PtrToStructure<NativeRectangle>(pointer);
            return (Rectangle.FromLTRB(final.Left, final.Top, final.Right, final.Bottom), result);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static Rectangle Snap(object manager, Form moving, Rectangle proposed, Keys modifiers) =>
        (Rectangle)Invoke(manager, "SnapMove", moving, proposed, modifiers)!;
    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static void Flush(object manager)
    {
        PumpUntil(() => Field<Task?>(manager, "_writeTask") is null);
        var save = (Task)Invoke(manager, "SavePendingAsync")!;
        PumpUntil(() => save.IsCompleted);
        save.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!ready()) throw new TimeoutException("Sticker alignment check timed out.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
}
