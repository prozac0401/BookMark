using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

internal static partial class NotepadAdapter
{
    internal static Action<string>? SnapshotTrace { get; set; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    internal static string RecoveryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBookmark", "NotepadRecovery");

    private static WorkerResponse ResumeSnapshot(CapturedTarget target, RequestContext context, bool validateOnly)
    {
        PathPolicy.Validate(target);
        if (validateOnly) throw new BookmarkException(ResultCode.InvalidRequest);
        using var guard = new ResumeGuard(context.Request.Snapshot,
            context.Request.MonitorInput ? context.Request.RequestId : null);
        context.Check();
        var path = WriteRecoveryCopy(target, context);
        context.Check();
        context.ExternalActionStarted = true;
        SnapshotTrace?.Invoke("Notepad activation recovery=" + path);
        try { NotepadLauncher.Open(path); }
        catch (Exception error)
        {
            SnapshotTrace?.Invoke("activation failed " + error.GetType().Name + " HRESULT=" + error.HResult.ToString("X8"));
            throw;
        }

        // A never-reused nonce filename, absent before this open, identifies the new recovery tab.
        // We never send selection/text messages to any pre-existing tab merely matching its content.
        while (true)
        {
            context.Check();
            System.Windows.Forms.Application.DoEvents();
            var matches = FindRecoveryEditors(path);
            if (matches.Count > 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
            if (matches.Count == 1)
            {
                var editor = matches[0];
                guard.Permit(editor.Root);
                context.Check();
                EditorState? ReadReadyState()
                {
                    try { return ReadEditor(editor, context); }
                    catch (BookmarkException error) when (error.Code == ResultCode.AppBusy)
                    {
                        // File loading can temporarily block a read-only editor message. A retry is
                        // safe only before any navigation and while the input/identity guard holds.
                        context.Check();
                        SnapshotTrace?.Invoke("waiting for busy recovery editor");
                        return null;
                    }
                }
                var before = ReadReadyState();
                if (before is null) { Thread.Sleep(25); continue; }
                SnapshotTrace?.Invoke("recovery initial characters=" + before.Text.Length + " normalized=" + NormalizeText(before.Text).Length +
                    " modified=" + before.Modified + " selection=" + before.Start + ":" + before.End);
                if (before.Modified) throw new BookmarkException(ResultCode.ContextChanged);
                if (!IsRecoveryState(before, path, target))
                {
                    // Modern Notepad publishes the nonce tab before the file has finished loading.
                    // Wait without modifying it; input is still checked on every observation.
                    SnapshotTrace?.Invoke("waiting for recovery text characters=" + NormalizeText(before.Text).Length + " modified=" + before.Modified);
                    Thread.Sleep(25);
                    continue;
                }
                var stable = ReadReadyState();
                if (stable != before) { Thread.Sleep(25); continue; }
                context.DocumentObserved = true;
                context.TargetHwnd = editor.Root.ToInt64();
                SnapshotTrace?.Invoke("recovery text verified characters=" + NormalizeText(before.Text).Length);
                guard.Check(); context.Check();
                // Close the race with tab reuse immediately before sending EM_SETSEL.
                if (ReadEditor(editor, context) != before) throw new BookmarkException(ResultCode.ContextChanged);
                var classic = Native.Class(editor.Hwnd) == "Edit";
                var begin = ToNativeOffset(before.Text, target.TextOffset!.Value, classic);
                var end = ToNativeOffset(before.Text, target.TextSelectionEnd!.Value, classic);
                guard.Check(); context.Check();
                Number(editor.Hwnd, 0xB1, (nuint)begin, (nint)end, context); // EM_SETSEL, never WM_SETTEXT.
                guard.Check(); context.Check();
                if (!IsRecoveryState(ReadEditor(editor, context), path, target)) throw new BookmarkException(ResultCode.ContextChanged);
                guard.Check(); context.Check();
                Number(editor.Hwnd, 0xB7, 0, 0, context); // EM_SCROLLCARET makes a distant saved location visible.
                guard.Check(); context.Check();
                var after = ReadEditor(editor, context);
                SnapshotTrace?.Invoke("recovery selection observed=" + after.Start + ":" + after.End + " expected=" + begin + ":" + end);
                if (!IsRecoveryState(after, path, target) || after.Start != begin || after.End != end)
                    throw new BookmarkException(ResultCode.OpenedPositionFailed);
                context.PositionVerified = true;
                guard.Check(); context.Check();
                context.TargetHwnd = editor.Root.ToInt64();
                return context.Response(Native.GetForegroundWindow() == editor.Root
                    ? ResultCode.PositionRestored : ResultCode.PositionRestoredFocusPending);
            }
            Thread.Sleep(25);
        }
    }

    internal static string WriteRecoveryCopy(CapturedTarget target, RequestContext context)
    {
        PathPolicy.Validate(target);
        if (target.Kind != TargetKind.NotepadSnapshot) throw new BookmarkException(ResultCode.InvalidRequest);
        context.Check();
        Directory.CreateDirectory(RecoveryDirectory);
        var path = Path.Combine(RecoveryDirectory, "Bookmark-" + Guid.NewGuid().ToString("N") + ".txt");
        // CreateNew prevents a restore from ever overwriting an earlier working copy, including one
        // the user has since edited. The BOM makes the intended UTF-8 encoding explicit.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true, true));
        writer.Write(target.TextContent);
        writer.Flush(); stream.Flush(true);
        context.Check();
        // A packaged host may virtualize LocalAppData writes. Give the separately activated Notepad
        // the actual file behind this open handle, not a logical path visible only to our host.
        var physical = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(stream.SafeFileHandle, physical, (uint)physical.Capacity, 0);
        if (count == 0 || count >= physical.Capacity) throw new BookmarkException(ResultCode.TargetUnavailable);
        var resolved = physical.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (resolved.Length < 3 || resolved[1] != ':' || !Path.GetFileName(resolved).Equals(Path.GetFileName(path), StringComparison.Ordinal))
            throw new BookmarkException(ResultCode.TargetUnavailable);
        return PathPolicy.Normalize(resolved);
    }

    private static bool IsRecoveryState(EditorState state, string path, CapturedTarget target) =>
        MatchesTitle(state.Title, path) && !state.Modified && NormalizeText(state.Text) == target.TextContent;

    private static List<Editor> FindRecoveryEditors(string path)
    {
        var roots = new List<nint>();
        Native.EnumWindows((root, _) => { if (Native.Class(root) == "Notepad" && Native.IsWindowVisible(root)) roots.Add(root); return true; }, 0);
        var result = new List<Editor>();
        foreach (var root in roots)
        {
            var title = new StringBuilder(1024); GetWindowText(root, title, title.Capacity);
            if (!MatchesTitle(title.ToString(), path)) continue;
            var visible = Editors(root).Where(Native.IsWindowVisible).ToArray();
            if (visible.Length != 1) continue; // The tab title may precede editor creation or the old tab hiding.
            Native.GetWindowThreadProcessId(root, out var pid);
            var editor = new Editor(root, visible[0], pid, Native.ProcessStamp(pid));
            VerifyEditor(editor);
            result.Add(editor);
        }
        return result;
    }
}
