using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class FolderStickerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var folder = Folder("프로젝트 자료", 1);
        var first = Item("시장 조사", 2) with { ParentId = folder.Id, Color = BookmarkColor.Blue };
        var nested = Folder("경쟁 서비스", 3) with { ParentId = folder.Id };
        var deep = Item("비교표", 4) with { ParentId = nested.Id };
        var loose = Item("새 자료", 5);
        var disk = Item("연결된 디스크 폴더", 6) with { Target = new(TargetKind.Folder, @"C:\folder-check") };
        var rows = new Dictionary<Guid, Bookmark>(new[] { folder, first, nested, deep, loose, disk }.Select(item => KeyValuePair.Create(item.Id, item)));
        var saved = new ConcurrentDictionary<Guid, StickerLayout>();
        object manager = Create(() => rows.Values.ToArray(), () => saved.Values.ToArray(), batch =>
        {
            foreach (var layout in batch) saved[layout.BookmarkId] = layout;
        });
        using var lifetime = (IDisposable)manager;
        Point cursorBefore = Cursor.Position;
        try
        {
            Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
            Form View(Guid id) => Forms(manager).Single(form => BookmarkFor(form).Id == id);
            void Publish(params Bookmark[] changes)
            {
                foreach (var change in changes) rows[change.Id] = change;
                Call(manager, "ApplyFolderChanges", changes, null!);
            }
            Form f = View(folder.Id), a = View(first.Id), g = View(nested.Id), b = View(deep.Id), c = View(loose.Id), d = View(disk.Id);
            assert(f.Visible && c.Visible && d.Visible && !a.Visible && !g.Visible && !b.Visible && Forms(manager).Length == 6,
                "FS01 a closed virtual folder hides descendants while retaining their live view identities");
            assert(Field<Button>(f, "_title").Text == folder.DisplayName &&
                Field<Button>(f, "_resume").AccessibleName == "폴더 열기" && Field<Label>(f, "_treeMark").Visible,
                "FS02 a virtual folder uses its name and a folder action rather than a link or note title");
            bool inline = (bool)Call(manager, "EditNote", first, (Func<string, Task>)(_ => Task.CompletedTask))!;
            assert(!inline && !a.Visible && !Property<bool>(a, "IsEditingNote"),
                "FS03 a note request beneath a closed folder declines inline editing without revealing the child");

            int resumes = 0, toggles = 0, renames = 0;
            Guid? createdParent = null;
            On(manager, "ResumeRequested", (Action<Bookmark>)(_ => resumes++));
            On(manager, "FolderToggleRequested", (Action<Bookmark>)(_ => toggles++));
            On(manager, "RenameFolderRequested", (Action<Bookmark>)(_ => renames++));
            On(manager, "CreateFolderRequested", (Action<Guid?>)(parent => createdParent = parent));
            Field<Button>(f, "_title").PerformClick();
            Field<Button>(f, "_resume").PerformClick();
            Command(f, Keys.Control | Keys.Enter);
            assert(toggles == 2 && renames == 1 && resumes == 0 && !BookmarkFor(f).FolderExpanded && !a.Visible,
                "FS04 a folder title requests editing while its action and Ctrl+Enter request a persisted toggle without resuming a worker");
            Call(f, "SetBusy", true);
            Field<Button>(f, "_title").PerformClick();
            Command(f, Keys.Control | Keys.Enter);
            Call(f, "UpdateMenu");
            Field<ToolStripMenuItem>(f, "_renameFolderMenu").PerformClick();
            assert(toggles == 2 && resumes == 0 && renames == 1,
                "FS05 busy folder actions cannot toggle, rename, or resume");
            Call(f, "SetBusy", false);
            Call(f, "UpdateMenu");
            Field<ToolStripMenuItem>(f, "_renameFolderMenu").PerformClick();
            Field<ToolStripMenuItem>(f, "_createFolderMenu").PerformClick();
            assert(renames == 2 && createdParent == folder.Id && !Field<ToolStripMenuItem>(f, "_copyMenu").Visible &&
                !Field<ToolStripMenuItem>(f, "_noteMenu").Visible,
                "FS06 virtual-folder menus route rename/create and omit nonexistent path and note actions");
            Field<Button>(d, "_resume").PerformClick();
            assert(resumes == 1 && !BookmarkFor(d).IsFolder,
                "FS07 a linked disk folder remains an ordinary resumable bookmark");

            Rectangle rootBefore = f.Bounds, looseBefore = c.Bounds, diskBefore = d.Bounds;
            folder = folder with { FolderExpanded = true }; Publish(folder);
            assert(f.Visible && a.Visible && g.Visible && !b.Visible && !a.TopLevel && !g.TopLevel && a.Parent == g.Parent &&
                f.Bounds == rootBefore && c.Bounds == looseBefore && d.Bounds == diskBefore,
                "FS08 opening embeds visible descendants without moving the root or unrelated stickers");
            nested = nested with { FolderExpanded = true }; Publish(nested);
            assert(b.Visible && !b.TopLevel && b.Parent == a.Parent && f.Bounds == rootBefore,
                "FS09 nested expansion derives a row in the same bounded panel");
            Pump((Task)Call(manager, "HideTreeAsync", first.Id)!);
            assert(!f.Visible && !a.Visible && !g.Visible && !b.Visible && c.Visible && d.Visible,
                "FS10 hiding from a child hides its containing tree rather than leaving orphan windows");
            Call(manager, "ShowTree", first.Id);
            assert(a.Visible && g.Visible && b.Visible && f.Bounds == rootBefore,
                "FS11 revealing a tree restores all persisted folder states at the same anchor");

            int noteSaves = 0;
            inline = (bool)Call(manager, "EditNote", first, (Func<string, Task>)(text =>
            {
                noteSaves++;
                first = first with { Note = text }; rows[first.Id] = first;
                Call(manager, "Upsert", first);
                return Task.CompletedTask;
            }))!;
            Field<TextBox>(a, "_noteEditor").Text = "숨기기 전에 저장할 메모";
            Pump((Task)Call(manager, "HideTreeAsync", first.Id)!);
            assert(inline && noteSaves == 1 && !Property<bool>(a, "IsEditingNote") && !f.Visible && !a.Visible &&
                first.Note == "숨기기 전에 저장할 메모",
                "FS12 hiding a tree commits an embedded draft before hiding its root and panel");
            Call(manager, "ShowTree", folder.Id);
            folder = folder with { Color = BookmarkColor.Purple }; Publish(folder);
            assert(a.Visible && BookmarkFor(f).Color == BookmarkColor.Purple && f.Bounds == rootBefore,
                "FS13 recoloring a reopened folder preserves its anchor and derived rows");
            Pump((Task)Call(manager, "HideAllAsync")!);
            folder = folder with { FolderExpanded = false }; Publish(folder);
            folder = folder with { FolderExpanded = true }; Publish(folder);
            assert(Forms(manager).All(form => !form.Visible),
                "FS14 committed folder toggles never override the hide-all policy");
            Pump((Task)Call(manager, "ShowAllAsync", false)!);

            var area = Screen.FromControl(f).WorkingArea;
            d.Location = area.Location + new Size(60, 70);
            c.Location = area.Location + new Size(d.Width + 130, 70);
            f.Location = area.Location + new Size(60, Math.Min(370, area.Height / 2));
            Rectangle sourceBefore = c.Bounds, targetBounds = d.Bounds;
            object decision = Call(manager, "EvaluateDrop", c, Center(d), Size.Empty, Keys.None)!;
            BookmarkDropRequest? request = Property<BookmarkDropRequest?>(decision, "Request");
            assert(request is { Kind: BookmarkDropKind.Group } && request.BookmarkId == loose.Id && request.TargetId == disk.Id &&
                BookmarkFor(c).ParentId is null && c.Bounds == sourceBefore && d.Bounds == targetBounds,
                "FS15 grouping preview is explicit and cannot mutate relationships or real placements");
            var grouped = Folder("새 폴더", 7);
            disk = disk with { ParentId = grouped.Id, SiblingOrder = 0 }; loose = loose with { ParentId = grouped.Id, SiblingOrder = 1 };
            foreach (var item in new[] { grouped, disk, loose }) rows[item.Id] = item;
            Call(manager, "ApplyFolderChanges", new[] { grouped, disk, loose }, (Guid?)disk.Id);
            Form h = View(grouped.Id);
            assert(h.Visible && !d.Visible && !c.Visible && h.Bounds == targetBounds,
                "FS16 committed grouping creates a closed root at the displayed target position");
            Rectangle folderBefore = f.Bounds;
            grouped = grouped with { FolderExpanded = true }; Publish(grouped);
            assert(c.Visible && d.Visible && !c.TopLevel && !d.TopLevel && c.Parent == d.Parent &&
                h.Bounds == targetBounds && f.Bounds == folderBefore,
                "FS17 a newly opened group derives rows in its own panel without moving an existing tree");
            grouped = grouped with { FolderExpanded = false }; Publish(grouped);
            Call(manager, "BringTreeToFront", folder.Id);
            decision = Call(manager, "EvaluateDrop", h, Center(f), Size.Empty, Keys.None)!;
            request = Property<BookmarkDropRequest?>(decision, "Request");
            assert(request is { Kind: BookmarkDropKind.MoveInto } && request.TargetId == folder.Id && BookmarkFor(h).ParentId is null,
                "FS18 moving a folder into another folder previews an authoritative hierarchy change");
            grouped = grouped with { ParentId = folder.Id }; Publish(grouped);
            assert(h.Visible && !h.TopLevel && !c.Visible && !d.Visible && BookmarkFor(f).FolderExpanded,
                "FS19 nesting an existing folder preserves its own closed state");
            folder = folder with { FolderExpanded = false }; Publish(folder);
            Pump((Task)Call(manager, "ShowAllAsync", false)!);
            assert(!a.Visible && !g.Visible && !b.Visible && !h.Visible && !c.Visible && !d.Visible,
                "FS20 an authoritative reload never reveals descendants of a closed folder");
        }
        finally { Cursor.Position = cursorBefore; }
        LateReadCheck(assert);
        PersistenceCheck(assert);
        RenameChecks(assert);
        TitleGestureChecks(assert);
    }

    private static void RenameChecks(Action<bool, string> assert)
    {
        var folder = Folder("이름을 바꿀 폴더", 1) with { FolderExpanded = true };
        var child = Item("이동할 하위 자료", 2) with { ParentId = folder.Id };
        Bookmark[] items = [folder, child];
        object manager = Create(() => items);
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        var f = Forms(manager).Single(form => BookmarkFor(form).Id == folder.Id);
        var c = Forms(manager).Single(form => BookmarkFor(form).Id == child.Id);
        f.Location = Screen.FromControl(f).WorkingArea.Location + new Size(70, 80);
        Rectangle bounds = f.Bounds;
        nint window = f.Handle;
        int writes = 0;
        bool fail = false;
        Func<string, Task> save = name =>
        {
            writes++;
            if (fail) return Task.FromException(new IOException("Injected folder rename failure."));
            folder = BookmarkFor(f) with { DisplayName = name };
            items = [folder, child];
            Call(manager, "Upsert", folder);
            return Task.CompletedTask;
        };
        On(manager, "RenameFolderRequested", (Action<Bookmark>)(value => Call(manager, "RenameFolder", value, save)));
        Field<Button>(f, "_title").PerformClick();
        var editor = Field<TextBox>(f, "_noteEditor");
        assert(Property<bool>(f, "IsEditingNote") && f.Handle == window && editor.Text == folder.DisplayName &&
            editor.MaxLength == 100 && editor.SelectionLength == folder.DisplayName.Length && editor.Visible && editor.Focused &&
            Property<Rectangle>(f, "PlacementBounds") == bounds && BookmarkFor(f).FolderExpanded,
            "FS23 clicking a folder title opens the same inline editor with its selected name and preserves normal placement and expansion");

        editor.Text = "   ";
        Pump((Task)Call(f, "SaveNoteAsync")!);
        assert(writes == 0 && Property<bool>(f, "IsEditingNote") && editor.Text == "   " && !editor.ReadOnly &&
            (Field<Label>(f, "_noteStatus").AccessibleDescription ?? "").Contains("1~100", StringComparison.Ordinal),
            "FS24 an invalid folder name is rejected before persistence and retains the editable draft with validation feedback");
        fail = true;
        editor.Text = "저장 실패 후 보존할 이름";
        Pump((Task)Call(f, "SaveNoteAsync")!);
        folder = folder with { Color = BookmarkColor.Purple }; items = [folder, child];
        Call(manager, "Upsert", folder);
        Call(f, "BeginFolderRename", (Func<string, Task>)(_ => throw new InvalidOperationException("Cannot replace an active draft callback.")));
        assert(writes == 1 && Property<bool>(f, "IsEditingNote") && editor.Text == "저장 실패 후 보존할 이름" && !editor.ReadOnly &&
            BookmarkFor(f).Color == BookmarkColor.Purple && (Field<Label>(f, "_noteStatus").AccessibleDescription ?? "").Contains("저장하지 못했습니다", StringComparison.Ordinal),
            "FS25 failed rename, color refresh and repeated editing preserve the original draft and save callback");

        fail = false;
        var delta = new Size(19, 13);
        f.Location += delta;
        editor.Text = "  변경한 폴더 이름  ";
        Command(f, Keys.Enter);
        assert(writes == 2 && !Property<bool>(f, "IsEditingNote") && BookmarkFor(f).DisplayName == "변경한 폴더 이름" &&
            BookmarkFor(f).Note == folder.Note && f.Bounds == new Rectangle(bounds.Location + delta, bounds.Size) &&
            Field<Button>(f, "_title").Text == "변경한 폴더 이름" && BookmarkFor(f).FolderExpanded,
            "FS26 Enter retries a normalized folder rename without changing its note, expansion, or moved normal geometry");
        bounds = f.Bounds;
        Command(f, Keys.F2);
        editor.Text = "취소할 이름";
        Command(f, Keys.Escape);
        assert(writes == 2 && !Property<bool>(f, "IsEditingNote") && f.Visible && f.Bounds == bounds &&
            BookmarkFor(f).DisplayName == "변경한 폴더 이름",
            "FS27 F2 opens folder editing and Escape cancels only the draft while restoring the normal sticker");
        Command(f, Keys.F2);
        editor.Text = "취소 버튼에서 Enter";
        Field<Button>(f, "_cancelNote").Focus();
        Command(f, Keys.Enter);
        assert(writes == 2 && !Property<bool>(f, "IsEditingNote") && BookmarkFor(f).DisplayName == "변경한 폴더 이름" && f.Bounds == bounds,
            "FS37 Enter on the focused inline cancel button cancels the folder draft instead of saving it");

        using var other = new Form { Text = "Folder rename focus check", ShowInTaskbar = false };
        other.Controls.Add(new TextBox { Dock = DockStyle.Top });
        other.Show();
        Command(f, Keys.Control | Keys.E);
        editor.Text = "자동저장할 이름";
        other.Activate(); other.Controls[0].Focus();
        Call(f, "OnDeactivate", EventArgs.Empty);
        PumpUntil(() => !Property<bool>(f, "IsEditingNote"));
        assert(writes == 3 && BookmarkFor(f).DisplayName == "자동저장할 이름" && f.Bounds == bounds && other.ContainsFocus,
            "FS28 leaving folder inline editing automatically persists its name without stealing focus or changing geometry");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Call(f, "BeginFolderRename", (Func<string, Task>)(_ => completion.Task));
        editor.Text = "이전 저장 요청";
        Task pending = (Task)Call(f, "SaveNoteAsync")!;
        folder = BookmarkFor(f) with { DisplayName = "나중에 확정된 이름" }; items = [folder, child];
        Call(manager, "Upsert", folder);
        completion.SetResult(); Pump(pending);
        assert(BookmarkFor(f).DisplayName == "나중에 확정된 이름" && !Property<bool>(f, "IsEditingNote") && f.Bounds == bounds,
            "FS29 a delayed rename completion cannot overwrite a newer authoritative folder name");

        Rectangle original = f.Bounds, childOriginal = Property<Rectangle>(c, "ScreenBounds");
        object preview = Call(manager, "EvaluateDrop", f, f.Location + new Size(70, 70), Size.Empty, Keys.Alt)!;
        assert(Property<bool>(preview, "Valid") && f.Bounds == original && Property<Rectangle>(c, "ScreenBounds") == childOriginal &&
            BookmarkFor(f).DisplayName == folder.DisplayName,
            "FS30 an uncommitted folder move preview leaves its root, descendants, name and relationships unchanged");
    }
    private static void TitleGestureChecks(Action<bool, string> assert)
    {
        Type sticker = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.StickerForm")!;
        Type titleType = sticker.GetNestedType("TitleButton", BindingFlags.NonPublic)!;
        using var host = new Form { Text = "Sticker title pointer checks", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100), ClientSize = new Size(280, 90), TopMost = true };
        var title = (Button)Activator.CreateInstance(titleType, true)!;
        title.FlatStyle = FlatStyle.Flat;
        title.UseVisualStyleBackColor = false;
        title.Text = "클릭 편집 · 드래그 이동"; title.Bounds = new Rectangle(20, 20, 240, 32);
        host.Controls.Add(title); host.Show(); host.Activate();
        Application.DoEvents();
        int clicks = 0, drags = 0;
        bool captureReleased = false;
        title.Click += (_, _) => clicks++;
        On(title, "DragRequested", (EventHandler)((_, _) =>
        {
            drags++;
            captureReleased = !title.Capture;
            title.PerformClick(); // Any click delivered by the nested native move loop must be suppressed.
        }));
        // Synthetic WM_MOUSEMOVE reads the real global MouseButtons state, and
        // WinForms' click hit-test requires the interactive input desktop. Feed
        // component move events explicitly; retain real capture/down/up messages
        // to exercise the product's release guard without injecting OS input.
        title.PerformClick();
        assert(clicks == 1 && drags == 0, "FS31 a title component activation invokes editing exactly once without starting a move");
        Pointer(title, 0x0201, 1, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 21, 13, 0));
        bool jitterRetainedCapture = title.Capture;
        title.Capture = false;
        title.PerformClick();
        assert(clicks == 2 && drags == 0 && jitterRetainedCapture,
            "FS32 component pointer jitter within the system threshold keeps capture and leaves title editing available");
        Pointer(title, 0x0201, 1, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        // Model a late native BN_CLICKED while WM_LBUTTONUP is being processed.
        // The reflected command reaches Button.OnClick regardless of desktop
        // hit-testing, so this specifically verifies the complete message guard.
        MouseEventHandler lateClick = (_, _) => SendMessage(title.Handle, 0x2111, 0, 0);
        title.MouseUp += lateClick;
        Pointer(title, 0x0202, 0, 25, 12);
        title.MouseUp -= lateClick;
        assert(drags == 1 && captureReleased && clicks == 2,
            "FS33 a component drag releases capture and suppresses nested activation plus a reflected click inside native WM_LBUTTONUP");
        title.PerformClick();
        Pointer(title, 0x0201, 1, 20, 12);
        title.PerformClick();
        title.Capture = false;
        assert(clicks == 3 && drags == 1, "FS34 accessibility activation remains editable and a fresh pointer gesture only selects after dragging");
        Pointer(title, 0x0201, 1, 20, 12);
        title.Capture = false;
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        Pointer(title, 0x0202, 0, 20, 12);
        assert(clicks == 3 && drags == 1, "FS35 native capture loss cancels the pending component gesture without editing or dragging");
        Pointer(title, 0x0204, 2, 20, 12);
        Call(title, "OnMouseMove", new MouseEventArgs(MouseButtons.Right, 0, 20 + SystemInformation.DragSize.Width + 8, 12, 0));
        Pointer(title, 0x0205, 0, 20, 12);
        assert(clicks == 3 && drags == 1, "FS36 right-button component gestures remain available for the context menu and never start a move");
    }

    private static void LateReadCheck(Action<bool, string> assert)
    {
        var folder = Folder("읽기 경합 폴더", 1) with { FolderExpanded = true };
        var child = Item("하위 자료", 2) with { ParentId = folder.Id };
        var items = new[] { folder, child };
        TaskCompletionSource<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>? gate = null;
        object manager = Create(() => items, customLoad: () => gate?.Task ?? Task.FromResult(((IReadOnlyList<Bookmark>)items, (IReadOnlyList<StickerLayout>)Array.Empty<StickerLayout>())));
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = (Task)Call(manager, "ShowAllAsync", false)!;
        var old = items;
        folder = folder with { FolderExpanded = false };
        items = [folder, child];
        Call(manager, "ApplyFolderChanges", new[] { folder }, null!);
        var pending = gate;
        gate = null;
        pending.SetResult((old, Array.Empty<StickerLayout>()));
        Pump(delayed);
        assert(!Forms(manager).Single(form => BookmarkFor(form).Id == child.Id).Visible &&
            !BookmarkFor(Forms(manager).Single(form => BookmarkFor(form).Id == folder.Id)).FolderExpanded,
            "FS21 a stale in-flight reveal retries after a committed folder close rather than resurrecting its children");
    }

    private static void PersistenceCheck(Action<bool, string> assert)
    {
        var folder = Folder("저장된 폴더", 1) with { FolderExpanded = true };
        var child = Item("파생되는 자식 위치", 2) with { ParentId = folder.Id };
        var items = new[] { folder, child };
        var saved = new ConcurrentDictionary<Guid, StickerLayout>();
        object New() => Create(() => items, () => saved.Values.ToArray(), batch =>
        {
            foreach (var layout in batch) saved[layout.BookmarkId] = layout;
        });
        Rectangle rootBounds;
        object first = New();
        using ((IDisposable)first)
        {
            Pump((Task)Call(first, "SetEnabledAsync", true, false)!);
            Form root = Forms(first).Single(form => BookmarkFor(form).Id == folder.Id);
            root.Location = Screen.FromControl(root).WorkingArea.Location + new Size(110, 160);
            Call(root, "OnResizeEnd", EventArgs.Empty);
            rootBounds = root.Bounds;
            folder = folder with { FolderExpanded = false }; items = [folder, child];
            Call(first, "ApplyFolderChanges", new[] { folder }, null!);
        }
        StickerLayout before = saved[folder.Id];
        object second = New();
        using ((IDisposable)second)
        {
            Pump((Task)Call(second, "SetEnabledAsync", true, false)!);
            Form root = Forms(second).Single(form => BookmarkFor(form).Id == folder.Id);
            Form card = Forms(second).Single(form => BookmarkFor(form).Id == child.Id);
            bool initiallyHidden = !card.Visible;
            folder = folder with { FolderExpanded = true, Color = BookmarkColor.Green }; items = [folder, child];
            Call(second, "ApplyFolderChanges", new[] { folder }, null!);
            assert(initiallyHidden && card.Visible && !card.TopLevel && root.Bounds == rootBounds &&
                saved[folder.Id] == before && !saved.ContainsKey(child.Id),
                "FS22 restart preserves the closed root anchor and reopening derives child geometry without saving child coordinates");
        }
    }
    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var folder = Folder("신규 서비스 기획", 1) with { Color = BookmarkColor.Yellow };
        var child = Item("시장 조사 자료", 4) with { ParentId = folder.Id, Color = BookmarkColor.Blue };
        var nested = Folder("경쟁 서비스 분석", 3) with { ParentId = folder.Id, Color = BookmarkColor.Green };
        var deep = Item("주요 기능 비교표", 2) with { ParentId = nested.Id };
        Bookmark[] rows = [folder, child, nested, deep];
        object manager = Create(() => rows);
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        Form root = Forms(manager).Single(form => BookmarkFor(form).Id == folder.Id);
        root.Location = Screen.FromControl(root).WorkingArea.Location + new Size(40, 40);
        Call(manager, "LayoutGroup", folder.Id);
        Draw("folders-closed.png");
        folder = folder with { FolderExpanded = true };
        rows = [folder, child, nested, deep];
        Call(manager, "ApplyFolderChanges", new[] { folder }, null!);
        Draw("folders-open.png");
        nested = nested with { FolderExpanded = true };
        rows = [folder, child, nested, deep];
        Call(manager, "ApplyFolderChanges", new[] { nested }, null!);
        Draw("folders-nested-open.png");
        Call(root, "BeginFolderRename", (Func<string, Task>)(_ => Task.CompletedTask));
        Field<TextBox>(root, "_noteEditor").Text = "신규 서비스 기획 · 자료 정리";
        Draw("folder-rename.png");
        Call(root, "CancelNoteEdit");
        void Draw(string filename)
        {
            var visible = Forms(manager).Where(form => form.TopLevel && form.Visible).Concat(Panels(manager).Where(form => form.Visible)).ToArray();
            Rectangle area = visible.Select(form => form.Bounds).Aggregate(Rectangle.Union);
            using var image = new Bitmap(area.Width + 40, area.Height + 40);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(238, 241, 242));
            foreach (var form in visible.OrderBy(form => form.Owner is not null))
            {
                using var card = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(card, new Rectangle(Point.Empty, card.Size));
                graphics.DrawImageUnscaled(card, form.Left - area.Left + 20, form.Top - area.Top + 20);
            }
            image.Save(Path.Combine(directory, filename), ImageFormat.Png);
        }
    }

    private static object Create(Func<IReadOnlyList<Bookmark>> items, Func<IReadOnlyList<StickerLayout>>? layouts = null,
        Action<IReadOnlyList<StickerLayout>>? save = null,
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>>? customLoad = null)
    {
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = customLoad ?? (() => Task.FromResult((items(), layouts?.Invoke() ?? Array.Empty<StickerLayout>())));
        return ManagerType.GetConstructors(Private).Single().Invoke([load, save ?? (Action<IReadOnlyList<StickerLayout>>)(_ => { }),
            (Action<string>)(message => throw new InvalidOperationException(message))]);
    }
    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\folder-check\sample.txt"),
        "sample.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Bookmark Folder(string title, long sequence) => Item(title, sequence) with
    {
        Target = new(TargetKind.BookmarkFolder, ""), Note = "폴더 제목에 표시되면 안 되는 메모", FolderExpanded = false
    };
    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Form[] Panels(object manager) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Panels", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => Property<Bookmark>(form, "Bookmark");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] arguments) => value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static void On(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Private | BindingFlags.Public)!.GetAddMethod(true)!.Invoke(value, [handler]);
    private static Point Center(Form form) => new(form.Left + form.Width / 2, form.Top + form.Height / 2);
    private static void Command(Form form, Keys keys) => Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x0100, 0, 0), keys);
    private static void Pointer(Control control, uint message, int buttons, int x, int y)
    {
        SendMessage(control.Handle, message, buttons, unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16))));
    }
    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(3)) { Application.DoEvents(); Thread.Yield(); }
        if (!predicate()) throw new TimeoutException("Folder inline editor did not settle.");
    }
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Folder sticker check timed out.");
        task.GetAwaiter().GetResult();
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
