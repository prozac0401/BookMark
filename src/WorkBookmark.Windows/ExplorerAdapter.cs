using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

internal static class ExplorerAdapter
{
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid BrowserInterface = typeof(IShellBrowser).GUID;
    private static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context)
    {
        if (snapshot.ActiveViewHwnd == 0) throw new BookmarkException(ResultCode.AmbiguousTarget);
        ForegroundSnapshot.Verify(snapshot);
        using var scope = new ComScope();
        var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!;
        var shellWindows = scope.Keep(Activator.CreateInstance(type)!);
        var first = ReadCurrent(shellWindows, snapshot, scope, context);
        ForegroundSnapshot.Verify(snapshot);
        var second = ReadCurrent(shellWindows, snapshot, scope, context);
        ForegroundSnapshot.Verify(snapshot);
        if (first != second) throw new BookmarkException(ResultCode.ContextChanged);
        return first;
    }
    private static CapturedTarget ReadCurrent(object windows, TargetSnapshot snapshot, ComScope scope, RequestContext context)
    {
        var count = scope.Number(windows, "Count");
        var views = new Dictionary<nint, object>();
        for (var index = 0; index < count; index++)
        {
            context.Check();
            var item = scope.Call(windows, "Item", index);
            if (item is null) throw new BookmarkException(ResultCode.ContextChanged);
            var hwnd = (nint)Convert.ToInt64(scope.Get(item, "HWND"));
            if (!Native.BelongsTo(hwnd, (nint)snapshot.Hwnd)) continue;
            var service = (IComServiceProvider)item;
            var sid = TopLevelBrowser; var iid = BrowserInterface;
            Marshal.ThrowExceptionForHR(service.QueryService(ref sid, ref iid, out var browserObject));
            scope.Keep(browserObject);
            var browser = (IShellBrowser)browserObject;
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var view));
            scope.Keep(view);
            Marshal.ThrowExceptionForHR(view.GetWindow(out var viewHwnd));
            if (!Native.IsWindowVisible(viewHwnd)) continue;
            if (!Native.BelongsTo(viewHwnd, (nint)snapshot.Hwnd) || viewHwnd.ToInt64() != snapshot.ActiveViewHwnd)
                throw new BookmarkException(ResultCode.AmbiguousTarget);
            // The dispatch comes from the verified IShellView itself, never a possibly stale WebBrowser.Document.
            var dispatch = Dispatch;
            Marshal.ThrowExceptionForHR(view.GetItemObject(0, ref dispatch, out var folderView));
            scope.Keep(folderView);
            if (views.TryGetValue(viewHwnd, out var previous) && !ComScope.Same(previous, folderView))
                throw new BookmarkException(ResultCode.AmbiguousTarget);
            views[viewHwnd] = folderView;
        }
        if (count != scope.Number(windows, "Count")) throw new BookmarkException(ResultCode.ContextChanged);
        if (views.Count != 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
        var exactView = views.Values.Single();
        var folder = scope.Get(exactView, "Folder");
        var self = scope.Get(folder, "Self");
        if (!scope.Flag(self, "IsFileSystem") || !scope.Flag(self, "IsFolder")) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var folderPath = scope.Text(self, "Path");
        PathPolicy.Normalize(folderPath);
        // ZIP Shell namespaces and any other virtual view fail this actual-directory requirement.
        if (!Directory.Exists(folderPath)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var selection = scope.Call(exactView, "SelectedItems");
        var selectedCount = scope.Number(selection, "Count");
        if (selectedCount > 1) throw new BookmarkException(ResultCode.MultipleSelection);
        if (selectedCount == 0) return new CapturedTarget(TargetKind.Folder, folderPath);
        var selected = scope.Call(selection, "Item", 0);
        if (!scope.Flag(selected, "IsFileSystem")) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var selectedPath = scope.Text(selected, "Path");
        PathPolicy.Normalize(selectedPath);
        var target = new CapturedTarget(scope.Flag(selected, "IsFolder") ? TargetKind.Folder : TargetKind.File, selectedPath);
        if (scope.Flag(selected, "IsLink")) target = target with { Kind = TargetKind.File }; // Never follow shortcut targets.
        context.Check();
        return target;
    }
}
