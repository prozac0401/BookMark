using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerInteractionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type StickerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.StickerForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        TitleGestures(assert);
        EditingAffordances(assert);
        foreach (bool collapsed in new[] { false, true })
        foreach (bool changeHeight in new[] { false, true })
        foreach (bool save in new[] { false, true })
            ResizeEditor(collapsed, changeHeight, save, assert);
        CancelledResize(assert);
        MoveResizesEditor(assert);
        MagneticPointerMovement(assert);
        InlineFootprints(assert);
    }

    internal static void Preview()
    {
        ApplicationConfiguration.Initialize();
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        using var panel = new Form
        {
            Text = "BookMark 스티커 동작 미리보기 · 임시 데이터",
            StartPosition = FormStartPosition.Manual,
            Location = area.Location + new Size(80, 320), ClientSize = new Size(610, 190)
        };
        var instruction = new Label
        {
            Text = "클릭 선택 · 드래그 이동 · 더블 클릭/F2 편집 · 우측 아래 크기 조절\nShift+드래그로 폴더에 넣기/새 폴더 묶기 · Alt로 자유 이동",
            Location = new Point(16, 14), Size = new Size(580, 44)
        };
        var magnet = new CheckBox { Text = "자석 정렬", Checked = true, Location = new Point(16, 72), AutoSize = true };
        var reveal = new Button { Text = "모두 표시", Location = new Point(150, 66), Size = new Size(110, 32) };
        var exit = new Button { Text = "미리보기 종료", Location = new Point(280, 66), Size = new Size(140, 32) };
        var status = new Label { Text = "메모와 폴더 변경은 이 미리보기의 메모리에만 유지됩니다.", Location = new Point(16, 120), Size = new Size(580, 48) };
        panel.Controls.AddRange([instruction, magnet, reveal, exit, status]);
        var now = DateTimeOffset.UtcNow;
        Bookmark Make(string title, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\preview\sample.txt"),
            "sample.txt", title, title, now, now, sequence, null, null, null, null);
        var folder = Make("자료 폴더", 1) with { Target = new CapturedTarget(TargetKind.BookmarkFolder, ""), Note = "", FolderExpanded = true };
        var first = Make("회의 메모 · 더블 클릭으로 수정", 2) with { ParentId = folder.Id, Color = BookmarkColor.Yellow };
        var second = Make("다음 작업 · 클릭 후 드래그", 3) with { Color = BookmarkColor.Blue };
        var rows = new Dictionary<Guid, Bookmark> { [folder.Id] = folder, [first.Id] = first, [second.Id] = second };
        var managerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = () =>
            Task.FromResult(((IReadOnlyList<Bookmark>)rows.Values.ToArray(), (IReadOnlyList<StickerLayout>)Array.Empty<StickerLayout>()));
        object manager = managerType.GetConstructors(Instance).Single().Invoke([load,
            (Action<IReadOnlyList<StickerLayout>>)(_ => { }), (Action<string>)(message => status.Text = message)]);
        using var lifetime = (IDisposable)manager;
        Form[] Forms() => ((System.Collections.IEnumerable)managerType.GetProperty("Forms", Instance)!.GetValue(manager)!).Cast<Form>().ToArray();
        void Publish(params Bookmark[] changes)
        {
            foreach (var item in changes) rows[item.Id] = item;
            Call(manager, "ApplyFolderChanges", changes, null!);
        }
        void Edit(Bookmark item, bool rename)
        {
            Call(manager, rename ? "RenameFolder" : "EditNote", rows[item.Id], (Func<string, Task>)(text =>
            {
                Bookmark current = rows[item.Id];
                if (rename) text = BookmarkFolderPolicy.ValidateName(text);
                Publish(rename ? current with { DisplayName = text } : current with { Note = text });
                status.Text = $"{(rename ? "폴더 이름" : "메모")} 저장: {text}";
                return Task.CompletedTask;
            }));
        }
        On(manager, "NoteRequested", (Action<Bookmark>)(item => Edit(item, false)));
        On(manager, "RenameFolderRequested", (Action<Bookmark>)(item => Edit(item, true)));
        On(manager, "FolderToggleRequested", (Action<Bookmark>)(item =>
        {
            Bookmark current = rows[item.Id];
            Publish(current with { FolderExpanded = !current.FolderExpanded });
            status.Text = current.FolderExpanded ? "폴더 닫힘" : "폴더 열림";
        }));
        On(manager, "ResumeRequested", (Action<Bookmark>)(item => status.Text = $"바로가기 요청 확인: {rows[item.Id].Note}"));
        On(manager, "ParentChangeRequested", (Action<Bookmark, Guid?>)((item, targetId) =>
        {
            Bookmark source = rows[item.Id];
            if (targetId is not Guid target) { Publish(source with { ParentId = null }); return; }
            Bookmark destination = rows[target];
            if (destination.IsFolder) Publish(source with { ParentId = target });
            else
            {
                Bookmark created = Make("새 폴더", rows.Values.Max(row => row.CaptureSequence) + 1) with
                {
                    Target = new CapturedTarget(TargetKind.BookmarkFolder, ""), Note = "",
                    ParentId = destination.ParentId, FolderExpanded = true
                };
                Bookmark[] changes = [created, source with { ParentId = created.Id }, destination with { ParentId = created.Id }];
                foreach (var changed in changes) rows[changed.Id] = changed;
                Call(manager, "ApplyFolderChanges", changes, (Guid?)target);
            }
            status.Text = "Shift+드래그 폴더 이동 반영됨";
        }));
        On(manager, "ColorChangeRequested", (Action<Bookmark, BookmarkColor>)((item, color) => Publish(rows[item.Id] with { Color = color })));
        On(manager, "SettingsRequested", (Action)(() => panel.Activate()));
        On(manager, "ShowListRequested", (Action)(() => panel.Activate()));
        magnet.CheckedChanged += (_, _) => managerType.GetProperty("SnapEnabled", Instance)!.SetValue(manager, magnet.Checked);
        reveal.Click += async (_, _) => await (Task)Call(manager, "ShowAllAsync", false)!;
        exit.Click += (_, _) => panel.Close();
        panel.Shown += async (_, _) =>
        {
            await (Task)Call(manager, "SetEnabledAsync", true, false)!;
            foreach (var form in Forms())
            {
                Guid id = Get<Bookmark>(form, "Bookmark").Id;
                form.Location = area.Location + (id == folder.Id ? new Size(100, 110) : id == first.Id ? new Size(120, 170) : new Size(440, 110));
            }
            panel.Activate();
        };
        Application.Run(panel);
    }

    private static void TitleGestures(Action<bool, string> assert)
    {
        Type titleType = StickerType.GetNestedType("TitleButton", BindingFlags.NonPublic)!;
        using var host = new Form
        {
            Text = "Sticker interaction component checks", ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100),
            ClientSize = new Size(300, 100), TopMost = true
        };
        var title = (Button)Activator.CreateInstance(titleType, true)!;
        title.Text = "선택 · 이동 · 편집";
        title.Bounds = new Rectangle(20, 20, 260, 32);
        host.Controls.Add(title);
        host.Show();
        host.Activate();
        Application.DoEvents();
        int edits = 0, drags = 0;
        bool captureReleased = false;
        Point? dragOrigin = null;
        title.Click += (_, _) => edits++;
        title.DoubleClick += (_, _) => edits++;
        titleType.GetEvent("DragRequested", Instance)!.GetAddMethod(true)!.Invoke(title, [(EventHandler)((_, _) =>
        {
            drags++;
            captureReleased = !title.Capture;
            dragOrigin = Get<Point>(title, "DragOrigin");
            title.PerformClick(); // A click during the nested native move must never edit.
        })]);

        Pointer(title, 0x0201, 1, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 21, 13, 0));
        SendMessage(title.Handle, 0x2111, 0, 0); // Reflected BN_CLICKED, independent of desktop hit-testing.
        Pointer(title, 0x0202, 0, 21, 13);
        assert(edits == 0 && drags == 0,
            "SI01 a title pointer click with small jitter selects without editing or starting a move");

        Pointer(title, 0x0203, 1, 20, 12); // WM_LBUTTONDBLCLK, as delivered after the first click.
        Pointer(title, 0x0202, 0, 20, 12);
        assert(edits == 1 && drags == 0,
            "SI02 a native title double click requests one edit without a move");
        title.PerformClick();
        assert(edits == 2,
            "SI03 programmatic and assistive title activation still requests editing");

        Pointer(title, 0x0201, 1, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        MouseEventHandler lateClick = (_, _) => SendMessage(title.Handle, 0x2111, 0, 0);
        title.MouseUp += lateClick;
        Pointer(title, 0x0202, 0, 25, 12);
        title.MouseUp -= lateClick;
        assert(drags == 1 && captureReleased && edits == 2,
            "SI04 title dragging releases capture and suppresses nested plus late native clicks");
        assert(dragOrigin == title.PointToScreen(new Point(20, 12)),
            "SI21 title dragging retains the original pointer-down screen position after crossing the drag threshold");
        title.PerformClick();
        assert(edits == 3,
            "SI05 assistive title activation remains available after a completed drag");

        Pointer(title, 0x0201, 1, 20, 12);
        title.Capture = false;
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        Pointer(title, 0x0202, 0, 20, 12);
        Pointer(title, 0x0204, 2, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Right, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        Pointer(title, 0x0205, 0, 20, 12);
        assert(edits == 3 && drags == 1,
            "SI06 capture loss and right-button gestures cannot edit or drag a title");
    }

    private static void EditingAffordances(Action<bool, string> assert)
    {
        using var form = Create();
        form.Show();
        Application.DoEvents();
        var title = Field<Button>(form, "_title");
        var action = Field<Button>(form, "_resume");
        var grip = Field<Control>(form, "_resizeGrip");
        assert(action.Visible && action.Right <= title.Left && title.Cursor == Cursors.SizeAll &&
            grip.Visible && grip.Cursor == Cursors.SizeNWSE && form.ClientRectangle.Contains(grip.Bounds),
            "SI07 the left primary action, move cursor and visible resize grip distinguish sticker actions");
        int requests = 0;
        On(form, "NoteRequested", (Action<Bookmark>)(_ =>
        {
            requests++;
            Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        }));
        Rectangle original = form.Bounds;
        Command(form, Keys.F2);
        var editor = Field<TextBox>(form, "_noteEditor");
        var status = Field<Label>(form, "_noteStatus");
        var cancel = Field<Button>(form, "_cancelNote");
        assert(requests == 1 && Editing(form) && form.ClientSize.Height == Px(form, 96) &&
            editor.Visible && editor.Focused && status.Top >= editor.Bottom && status.Bottom <= cancel.Top &&
            cancel.Bottom <= form.ClientSize.Height && grip.Visible && !grip.Bounds.IntersectsWith(cancel.Bounds) &&
            Placement(form) == original,
            "SI08 F2 opens a compact inline editor with distinct input, status, cancel and resize areas");
        Command(form, Keys.Escape);
        Command(form, Keys.Control | Keys.E);
        assert(requests == 2 && Editing(form), "SI09 Ctrl+E retains direct keyboard access to memo editing");
        Command(form, Keys.Escape);
        title.PerformClick();
        assert(requests == 3 && Editing(form), "SI10 accessible title activation opens the same inline editor");
        Command(form, Keys.Escape);
        Pointer(title, 0x0203, 1, 20, 12);
        bool doubleClickOpened = requests == 4 && Editing(form) && !title.Visible;
        // The editor replaces the title before the second button release. Do not
        // send an artificial mouse-up to the hidden title to clean up its state.
        Command(form, Keys.Escape);
        title.PerformClick();
        assert(doubleClickOpened && requests == 5 && Editing(form),
            "SI15 accessibility activation remains available after double-click editing hides the title before mouse-up");
        Command(form, Keys.Escape);
        Call(form, "ApplyPresentation", true, true);
        assert(grip.Visible && grip.Cursor == Cursors.SizeWE,
            "SI11 a collapsed sticker exposes a width-only resize affordance");
    }

    private static void ResizeEditor(bool collapsed, bool changeHeight, bool save, Action<bool, string> assert)
    {
        using var form = Create();
        if (collapsed) form.ClientSize = new Size(Px(form, 260), Px(form, 130));
        Call(form, "ApplyPresentation", collapsed, true);
        form.Show();
        Application.DoEvents();
        Rectangle original = form.Bounds;
        Rectangle originalPlacement = Placement(form);
        int saves = 0, adjustments = 0;
        StickerType.GetProperty("AdjustMoveBounds", Instance)!.SetValue(form,
            (Func<Rectangle, Keys, Rectangle>)((bounds, _) => { adjustments++; return bounds; }));
        Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => { saves++; return Task.CompletedTask; }));
        Field<TextBox>(form, "_noteEditor").Text = "크기 조절 후 메모";
        SendMessage(form.Handle, 0x0231, 0, 0); // Begin a real native sizing transaction.
        form.Size += new Size(Px(form, 35), changeHeight ? Px(form, 23) : 0);
        Size resized = form.Size;
        SendMessage(form.Handle, 0x0232, 0, 0);
        Size expectedExpanded = new(resized.Width, changeHeight ? resized.Height : originalPlacement.Height);
        bool expectedCollapsed = collapsed && !changeHeight;
        string state = $"collapsed={collapsed}, heightChanged={changeHeight}, save={save}";
        assert(adjustments == 0 && Placement(form).Size == expectedExpanded &&
            Get<bool>(form, "PlacementIsCollapsed") == expectedCollapsed,
            $"SI12 native editor resize records only intentional dimensions without magnetic move processing ({state})");
        if (save) Pump((Task)Call(form, "SaveNoteAsync")!);
        else Command(form, Keys.Escape);
        Size expectedVisible = new(resized.Width, expectedCollapsed ? original.Height : expectedExpanded.Height);
        assert(!Editing(form) && form.Size == expectedVisible && form.Location == original.Location &&
            Get<bool>(form, "IsCollapsed") == expectedCollapsed && Get<Size>(form, "ExpandedSize") == expectedExpanded &&
            saves == (save ? 1 : 0),
            $"SI13 saving or cancelling text preserves deliberate native resizing and the correct collapsed state ({state})");
    }

    private static void CancelledResize(Action<bool, string> assert)
    {
        using var form = Create();
        form.Show();
        Application.DoEvents();
        Rectangle original = form.Bounds;
        Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        Size editing = form.Size;
        SendMessage(form.Handle, 0x0231, 0, 0);
        form.Size += new Size(Px(form, 40), Px(form, 20));
        form.Size = editing; // Windows restores the transaction origin when resizing is cancelled with Esc.
        SendMessage(form.Handle, 0x0232, 0, 0);
        Command(form, Keys.Escape);
        assert(form.Bounds == original && !Editing(form),
            "SI14 cancelling native resizing does not persist the editor's temporary enlargement");
    }

    private static void MoveResizesEditor(Action<bool, string> assert)
    {
        foreach (bool withAdjuster in new[] { false, true })
        {
            using var form = Create();
            form.Show();
            Application.DoEvents();
            Rectangle original = form.Bounds;
            int adjustments = 0;
            if (withAdjuster)
                StickerType.GetProperty("AdjustMoveBounds", Instance)!.SetValue(form,
                    (Func<Rectangle, Keys, Rectangle>)((bounds, _) => { adjustments++; return bounds; }));
            Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            var delta = new Size(Px(form, 25), Px(form, 16));
            SendMessage(form.Handle, 0x0231, 0, 0);
            var proposed = new Rectangle(form.Location + delta, form.Size);
            nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
            try
            {
                Marshal.StructureToPtr(new NativeRectangle
                {
                    Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom
                }, pointer, false);
                SendMessage(form.Handle, 0x0216, 0, pointer);
                var moved = Marshal.PtrToStructure<NativeRectangle>(pointer);
                form.Bounds = Rectangle.FromLTRB(moved.Left, moved.Top, moved.Right, moved.Bottom);
            }
            finally { Marshal.FreeHGlobal(pointer); }
            // Native movement can alter the outer size (for example, on a DPI
            // transition). That is not an intentional editor resize gesture.
            form.Size += new Size(Px(form, 40), Px(form, 20));
            SendMessage(form.Handle, 0x0232, 0, 0);
            Rectangle expected = new(original.Location + delta, original.Size);
            bool normalPlacementRetained = Placement(form) == expected;
            Command(form, Keys.Escape);
            assert(adjustments == (withAdjuster ? 1 : 0) && normalPlacementRetained && form.Bounds == expected,
                $"SI16 native move size changes retain the resting editor size and only persist movement (adjuster={withAdjuster})");
        }
    }

    private static void MagneticPointerMovement(Action<bool, string> assert)
    {
        using var form = Create();
        _ = form.Handle;
        MethodInfo snap = typeof(UserSettings).Assembly.GetType("WorkBookmark.App.StickerSnap")!
            .GetMethod("Snap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var offsetField = StickerType.GetField("_pointerMoveOffset", Instance)!;
        var adjustProperty = StickerType.GetProperty("AdjustMoveBounds", Instance)!;
        Rectangle area = new(-4000, -4000, 8000, 8000);
        Rectangle peer = new(-600, -300, form.Width, form.Height);
        Point grip = new(Px(form, 40), Px(form, 16));
        int gap = Px(form, 8), threshold = Px(form, 12);
        Rectangle? received = null;
        adjustProperty.SetValue(form, (Func<Rectangle, Keys, Rectangle>)((bounds, modifiers) =>
        {
            received = bounds;
            return (modifiers & Keys.Alt) != 0 ? bounds
                : (Rectangle)snap.Invoke(null, [bounds, new[] { peer }, area, form.DeviceDpi])!;
        }));

        foreach (bool horizontal in new[] { true, false })
        foreach (int direction in new[] { -1, 1 })
        {
            Point origin = horizontal
                ? new Point(direction > 0 ? peer.Right + gap : peer.Left - gap - form.Width, peer.Top)
                : new Point(peer.Left, direction > 0 ? peer.Bottom + gap : peer.Top - gap - form.Height);
            Point startCursor = origin + new Size(grip);
            offsetField.SetValue(form, grip);
            Rectangle last = new(origin, form.Size);
            bool followsCursor = true;
            for (int distance = 1; distance <= threshold + 20; distance++)
            {
                // Model Windows using the corrected previous rectangle plus
                // one pixel, even though the physical cursor keeps travelling.
                Size step = horizontal ? new Size(direction, 0) : new Size(0, direction);
                Size total = horizontal ? new Size(distance * direction, 0) : new Size(0, distance * direction);
                Rectangle native = new(last.Location + step, form.Size);
                last = (Rectangle)Call(form, "AdjustNativeMoveBounds", native, startCursor + total, Keys.None)!;
                Point expected = distance <= threshold ? origin : origin + total;
                followsCursor &= received == new Rectangle(origin + total, form.Size) &&
                    last.Location == expected && last.Size == form.Size;
            }
            assert(followsCursor,
                $"SI17 one-pixel pointer steps escape a magnet after the cumulative threshold (horizontal={horizontal}, direction={direction})");

            Size near = horizontal ? new Size(direction * 3, 0) : new Size(0, direction * 3);
            Rectangle arbitraryNative = new(last.Location, form.Size);
            Rectangle bypass = (Rectangle)Call(form, "AdjustNativeMoveBounds", arbitraryNative, startCursor + near, Keys.Alt)!;
            Rectangle resnapped = (Rectangle)Call(form, "AdjustNativeMoveBounds", bypass, startCursor + near, Keys.None)!;
            assert(bypass.Location == origin + near && resnapped.Location == origin,
                $"SI18 Alt immediately follows the original pointer grip and releasing Alt restores snapping (horizontal={horizontal}, direction={direction})");
        }

        // Exercise the real WM_MOVING route without moving the desktop pointer.
        Rectangle target = new(-1600, -1200, form.Width, form.Height);
        Point cursor = Cursor.Position;
        offsetField.SetValue(form, new Point(cursor.X - target.Left, cursor.Y - target.Top));
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        try
        {
            Marshal.StructureToPtr(new NativeRectangle
            {
                Left = peer.Left, Top = peer.Top, Right = peer.Right, Bottom = peer.Bottom
            }, pointer, false);
            SendMessage(form.Handle, 0x0216, 0, pointer);
            var moved = Marshal.PtrToStructure<NativeRectangle>(pointer);
            assert(Rectangle.FromLTRB(moved.Left, moved.Top, moved.Right, moved.Bottom) == target,
                "SI19 WM_MOVING uses the unsnapped pointer anchor instead of a stale native rectangle");
        }
        finally { Marshal.FreeHGlobal(pointer); }

        SendMessage(form.Handle, 0x0232, 0, 0);
        Rectangle keyboard = new(-1400, -1000, form.Width, form.Height);
        Rectangle result = (Rectangle)Call(form, "AdjustNativeMoveBounds", keyboard, Point.Empty, Keys.None)!;
        assert(offsetField.GetValue(form) is null && received == keyboard && result == keyboard,
            "SI20 ending a pointer move clears its anchor and preserves later native keyboard movement");
    }

    private static void InlineFootprints(Action<bool, string> assert)
    {
        foreach (bool save in new[] { false, true })
        {
            using var form = Create();
            form.Show();
            Application.DoEvents();
            Rectangle original = form.Bounds;
            var changes = new List<(Rectangle Bounds, bool Editing, bool Changing)>();
            int placementChanges = 0;
            On(form, "FootprintChanged", (Action<Form>)(changed => changes.Add(
                (changed.Bounds, Editing(changed), Field<bool>(changed, "_presentationChanging")))));
            On(form, "PlacementChanged", (Action<Form>)(_ => placementChanges++));
            Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            Call(form, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            if (save) Pump((Task)Call(form, "SaveNoteAsync")!);
            else Command(form, Keys.Escape);
            assert(changes.Count == 2 && changes[0].Editing && changes[0].Bounds.Height > original.Height &&
                !changes[0].Changing && !changes[1].Editing && changes[1].Bounds == original &&
                !changes[1].Changing && placementChanges == 0,
                $"SI22 editor footprint notifications expose only completed enlargement and restoration without saving temporary placement (save={save})");
        }
    }

    private static Form Create()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new CapturedTarget(TargetKind.File, @"C:\업무\선택과 이동.txt");
        var bookmark = new Bookmark(Guid.NewGuid(), target, target.Path, "선택과 이동.txt", "수정할 메모", now, now, 1, null, null, null, null);
        var form = (Form)Activator.CreateInstance(StickerType, bookmark)!;
        form.Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(80, 80);
        return form;
    }

    private static bool Editing(Form form) => Get<bool>(form, "IsEditingNote");
    private static Rectangle Placement(Form form) => Get<Rectangle>(form, "PlacementBounds");
    private static int Px(Form form, int value) => (int)Math.Round(value * form.DeviceDpi / 96F);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Instance)!.Invoke(value, arguments);
    private static void On(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Instance)!.GetAddMethod(true)!.Invoke(value, [handler]);
    private static void Pointer(Control control, int message, int buttons, int x, int y) =>
        SendMessage(control.Handle, message, buttons, unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16))));
    private static void Command(Form form, Keys keys)
    {
        object[] arguments = [Message.Create(form.Handle, 0x0100, (nint)(int)keys, nint.Zero), keys];
        StickerType.GetMethod("ProcessCmdKey", Instance)!.Invoke(form, arguments);
    }
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Sticker interaction save did not complete.");
        task.GetAwaiter().GetResult();
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
}
