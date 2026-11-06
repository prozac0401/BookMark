using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerTreeChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var root = Folder("프로젝트 기획", 1);
        var nested = Folder("시장 조사", 2);
        var child = Item("비교표", 3);
        var peer = Item("독립된 회의 자료", 4);
        using var fixture = new Fixture(root, nested, child, peer);
        Form r = fixture.View(root.Id), n = fixture.View(nested.Id), c = fixture.View(child.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(r).WorkingArea;
        r.Location = area.Location + new Size(40, 40);
        p.Location = new Point(area.Right - p.Width - 30, area.Top + 40);
        Call(r, "OnResizeEnd", EventArgs.Empty); Call(p, "OnResizeEnd", EventArgs.Empty); fixture.Flush();
        Rectangle rootBefore = r.Bounds, peerBefore = p.Bounds;
        nested = nested with { ParentId = root.Id, SiblingOrder = 0 };
        child = child with { ParentId = nested.Id, SiblingOrder = 0 };
        fixture.Publish(nested, child);
        assert(n.Visible && c.Visible && !n.TopLevel && !c.TopLevel && n.Parent == c.Parent && r.Bounds == rootBefore,
            "STR01 committed hierarchy embeds descendants in one panel while retaining the root anchor");
        assert(ScreenBounds(c).Top > ScreenBounds(n).Bottom && p.Bounds == peerBefore,
            "STR02 depth-first rows remain ordered without moving unrelated stickers");
        Call(n, "UpdateMenu");
        assert(!Field<Control>(n, "_resizeGrip").Visible && !Field<ToolStripMenuItem>(n, "_moveMenu").Available && Field<ToolStripMenuItem>(n, "_detachMenu").Available,
            "STR03 embedded cards allow explicit detach without independent resize or native movement");

        Rectangle childBefore = ScreenBounds(c);
        Point destination = r.Location + new Size(70, 25);
        object preview = fixture.Preview(r, destination, Keys.Alt);
        assert(Property<bool>(preview, "Valid") && r.Bounds == rootBefore && ScreenBounds(c) == childBefore,
            "STR04 a root movement preview leaves real root and child geometry unchanged");
        bool moved = fixture.Commit(r, preview, destination, Keys.Alt);
        Rectangle committed = Property<Rectangle>(preview, "Bounds");
        Size delta = new(committed.X - rootBefore.X, committed.Y - rootBefore.Y);
        assert(moved && r.Bounds == committed && ScreenBounds(c).Location == childBefore.Location + delta &&
            fixture.Panel(root.Id).Bounds == Property<Rectangle?>(preview, "PanelBounds") && p.Bounds == peerBefore,
            "STR05 a committed move exactly matches its root and panel preview without moving peers");
        Rectangle stable = r.Bounds;
        fixture.FailWrites = true;
        destination += new Size(30, 20); preview = fixture.Preview(r, destination, Keys.Alt);
        assert(!fixture.Commit(r, preview, destination, Keys.Alt) && r.Bounds == stable && p.Bounds == peerBefore,
            "STR06 failed placement storage preserves displayed root and peer positions");
        fixture.FailWrites = false;

        Point desktop = new(area.Right - 12, area.Bottom - 12);
        preview = fixture.Preview(n, desktop, Keys.None);
        var detach = Property<BookmarkDropRequest?>(preview, "Request");
        assert(detach is { Kind: BookmarkDropKind.Detach, RootLayout: not null } && BookmarkFor(n).ParentId == root.Id,
            "STR07 a subtree desktop drop previews a concrete root placement without changing its parent");
        nested = nested with { ParentId = null }; fixture.Publish(nested);
        Call(fixture.Manager, "ApplySavedLayouts", (object)new[] { detach!.RootLayout! });
        assert(n.TopLevel && n.Bounds == Property<Rectangle>(preview, "Bounds") && !c.TopLevel && BookmarkFor(c).ParentId == nested.Id &&
            area.Contains(fixture.Panel(nested.Id).Bounds) && fixture.Panel(nested.Id).Bottom == n.Top,
            "STR08 committed detach retains descendants and opens above the unchanged bottom-edge root");
        var arrangement = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerArrangementMode")!;
        Pump((Task)Call(fixture.Manager, "ArrangeAsync", Enum.Parse(arrangement, "Grid"))!);
        assert(BookmarkFor(c).ParentId == nested.Id && !c.TopLevel && area.Contains(fixture.Panel(nested.Id).Bounds),
            "STR09 explicit root arrangement retains hierarchy and screen-bounded child panels");
        PersistenceChecks(assert);
        DropIntentChecks(assert);
    }

    private static void DropIntentChecks(Action<bool, string> assert)
    {
        var target = Item("묶기 대상", 1); var source = Item("이동할 항목", 2); var other = Item("다른 대상", 3);
        using var fixture = new Fixture(target, source, other);
        Form t = fixture.View(target.Id), s = fixture.View(source.Id), o = fixture.View(other.Id);
        Rectangle area = Screen.FromControl(t).WorkingArea;
        t.Location = area.Location + new Size(60, 60); s.Location = t.Location + new Size(t.Width + 100, 0); o.Location = t.Location + new Size(0, t.Height + 100);
        int requests = 0; BookmarkDropRequest? requested = null;
        On(fixture.Manager, "DropRequested", (Action<BookmarkDropRequest>)(value => { requests++; requested = value; }));
        foreach (bool snap in new[] { false, true })
        {
            ManagerType.GetProperty("SnapEnabled", Members)!.SetValue(fixture.Manager, snap);
            Point center = Center(t); Rectangle before = s.Bounds;
            object group = fixture.Preview(s, center, Keys.None); int count = requests;
            assert(Property<BookmarkDropRequest?>(group, "Request") is { Kind: BookmarkDropKind.Group } && fixture.Commit(s, group, center, Keys.None) &&
                requests == count + 1 && requested!.TargetId == target.Id && s.Bounds == before && BookmarkFor(s).ParentId is null,
                $"STR10 snap={snap}: a central grouping preview emits one atomic request without mutating real cards");
            object alt = fixture.Preview(s, center, Keys.Alt);
            assert(Property<BookmarkDropRequest?>(alt, "Request") is null && !fixture.Commit(s, alt, center, Keys.None) && requests == count + 1 && s.Bounds == before,
                $"STR11 snap={snap}: changing Alt at release cannot commit an unpreviewed grouping");
            assert(!fixture.Commit(s, group, Center(o), Keys.None) && requests == count + 1,
                $"STR12 snap={snap}: release over a different target rejects the stale grouping preview");
            object edge = fixture.Preview(s, new Point(t.Left + 2, t.Top + t.Height / 2), Keys.None);
            assert(Property<BookmarkDropRequest?>(edge, "Request") is null,
                $"STR13 snap={snap}: an ordinary card's outer edge does not silently create a folder");
        }
    }

    private static void PersistenceChecks(Action<bool, string> assert)
    {
        var root = Folder("재시작할 프로젝트", 1); var child = Item("기존 좌표가 남은 자식", 2) with { ParentId = root.Id }; var peer = Item("다른 스티커", 3);
        using var fixture = new Fixture(root, child, peer);
        Form r = fixture.View(root.Id), c = fixture.View(child.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(r).WorkingArea;
        r.Location = new Point(area.Left + 80, area.Bottom - r.Height); p.Location = area.Location + new Size(400, 80);
        Call(r, "OnResizeEnd", EventArgs.Empty); Call(p, "OnResizeEnd", EventArgs.Empty); fixture.Flush();
        Rectangle savedRoot = r.Bounds, savedPeer = p.Bounds;
        var legacy = new StickerLayout(child.Id, Screen.FromControl(r).DeviceName, 460, 220, 420, 180, false, true, root.Id); fixture.Saved[child.Id] = legacy;
        root = root with { FolderExpanded = false }; fixture.Publish(root); fixture.Restart();
        r = fixture.View(root.Id); c = fixture.View(child.Id); p = fixture.View(peer.Id);
        assert(r.Bounds == savedRoot && p.Bounds == savedPeer && !c.Visible && !BookmarkFor(r).FolderExpanded,
            "STR14 restart preserves root anchors and fold state without revealing descendants");
        root = root with { FolderExpanded = true }; fixture.Publish(root);
        assert(!c.TopLevel && c.Visible && c.Height < legacy.Height && fixture.Panel(root.Id).Bottom == r.Top && r.Bounds == savedRoot &&
            p.Bounds == savedPeer && fixture.Saved[child.Id] == legacy,
            $"STR15 reopening derives child geometry while ignoring retained legacy coordinates and preserving root anchors (embedded={!c.TopLevel}, visible={c.Visible}, height={c.Height}, panelBottom={fixture.Panel(root.Id).Bottom}, rootTop={r.Top}, rootSame={r.Bounds == savedRoot}, peerSame={p.Bounds == savedPeer}, legacySame={fixture.Saved[child.Id] == legacy})");
        Pump((Task)Call(fixture.Manager, "HideTreeAsync", child.Id)!);
        root = root with { FolderExpanded = false }; fixture.Publish(root); root = root with { FolderExpanded = true }; fixture.Publish(root);
        assert(!r.Visible && !c.Visible && p.Visible, "STR16 folding a hidden tree cannot override its visibility policy");
        fixture.Restart();
        assert(fixture.View(root.Id).Visible && fixture.View(child.Id).Visible && !fixture.View(child.Id).TopLevel && fixture.View(root.Id).Bounds == savedRoot,
            "STR17 temporary hiding does not erase saved expansion or anchor geometry across restart");
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var root = Folder("신규 서비스 기획", 1); var first = Item("시장 조사 자료", 4) with { ParentId = root.Id, SiblingOrder = 0 };
        var nested = Folder("경쟁 서비스 비교", 3) with { ParentId = root.Id, SiblingOrder = 1 }; var deep = Item("기능 비교표", 2) with { ParentId = nested.Id };
        using var fixture = new Fixture(root, first, nested, deep); Form r = fixture.View(root.Id);
        r.Location = Screen.FromControl(r).WorkingArea.Location + new Size(40, 40);
        Draw("sticker-tree.png"); Call(r, "SetDropTarget", true); Draw("sticker-tree-drop.png");
        void Draw(string name)
        {
            var surfaces = fixture.Surfaces(); Rectangle envelope = surfaces.Select(form => form.Bounds).Aggregate(Rectangle.Union);
            using var bitmap = new Bitmap(envelope.Width + 40, envelope.Height + 40);
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(238, 241, 242));
            foreach (Form form in surfaces) form.DrawToBitmap(bitmap, new Rectangle(form.Left - envelope.Left + 20, form.Top - envelope.Top + 20, form.Width, form.Height));
            bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<Guid, Bookmark> _items;
        internal object Manager { get; private set; }
        internal ConcurrentDictionary<Guid, StickerLayout> Saved { get; } = new();
        internal bool FailWrites { get; set; }
        internal Fixture(params Bookmark[] items) { _items = items.ToDictionary(item => item.Id); Manager = Create(); Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!); }
        private object Create()
        {
            Func<Task<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>> load = () => Task.FromResult(((IReadOnlyList<Bookmark>)_items.Values.ToArray(), (IReadOnlyList<StickerLayout>)Saved.Values.ToArray()));
            Action<IReadOnlyList<StickerLayout>> save = layouts => { if (FailWrites) throw new IOException("Synthetic placement failure."); foreach (var layout in layouts) Saved[layout.BookmarkId] = layout; };
            return ManagerType.GetConstructors(Members).Single().Invoke([load, save, (Action<string>)(_ => { })]);
        }
        private Form[] Forms => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Members)!.GetValue(Manager)!).Cast<Form>().ToArray();
        private Form[] Panels => ((System.Collections.IEnumerable)ManagerType.GetProperty("Panels", Members)!.GetValue(Manager)!).Cast<Form>().ToArray();
        internal Form View(Guid id) => Forms.Single(form => BookmarkFor(form).Id == id);
        internal Form Panel(Guid id) => Panels.Single(panel => BookmarkFor(Property<Form>(panel, "Root")).Id == id);
        internal Form[] Surfaces() => Forms.Where(form => form.TopLevel && form.Visible).Concat(Panels.Where(form => form.Visible)).ToArray();
        internal void Publish(params Bookmark[] changes) { foreach (var item in changes) _items[item.Id] = item; Call(Manager, "ApplyFolderChanges", changes, null); }
        internal object Preview(Form form, Point cursor, Keys keys) => Call(Manager, "EvaluateDrop", form, cursor, Size.Empty, keys)!;
        internal bool Commit(Form form, object preview, Point cursor, Keys keys) => Pump((Task<bool>)Call(Manager, "CommitDropPreviewAsync", form, preview, cursor, Size.Empty, keys)!);
        internal void Flush() => Pump((Task)Call(Manager, "SavePendingAsync")!);
        internal void Restart() { ((IDisposable)Manager).Dispose(); Manager = Create(); Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!); }
        public void Dispose() => ((IDisposable)Manager).Dispose();
    }

    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new(TargetKind.File, @"C:\tree-check\sample.txt"), "sample.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Bookmark Folder(string title, long sequence) => Item(title, sequence) with { Target = new(TargetKind.BookmarkFolder, ""), FolderExpanded = true };
    private static Bookmark BookmarkFor(Form form) => Property<Bookmark>(form, "Bookmark");
    private static Rectangle ScreenBounds(Form form) => Property<Rectangle>(form, "ScreenBounds");
    private static Point Center(Form form) => new(form.Left + form.Width / 2, form.Top + form.Height / 2);
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Members)!.Invoke(value, args);
    private static void On(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Members)!.GetAddMethod(true)!.Invoke(value, [handler]);
    private static void Pump(Task task) { var timeout = Stopwatch.StartNew(); while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Yield(); } if (!task.IsCompleted) throw new TimeoutException("Tree policy check timed out."); task.GetAwaiter().GetResult(); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
