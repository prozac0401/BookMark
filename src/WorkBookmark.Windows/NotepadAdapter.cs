using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Stable editor snapshots; old file-linked bookmarks retain their open-only behavior.</summary>
internal static partial class NotepadAdapter
{
    internal const int MaximumTextCharacters = 2 * 1024 * 1024;
    internal const long MaximumFileBytes = 8 * 1024 * 1024;
    private const uint TimeoutFlags = 0x0002 | 0x0020; // ABORTIFHUNG | ERRORONEXIT
    private sealed record Editor(nint Root, nint Hwnd, uint ProcessId, long ProcessStamp);
    private sealed record EditorState(string Title, string Text, int Start, int End, bool Modified);
    private sealed record FileState(string Text, string? Identity, long Length, DateTime LastWriteUtc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, ref int wParam, ref int lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, StringBuilder lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);

    internal static WorkerResponse Capture(TargetSnapshot snapshot, RequestContext context)
    {
        ForegroundSnapshot.Verify(snapshot);
        var editor = FromSnapshot(snapshot);
        var first = ReadEditor(editor, context);
        var classic = Native.Class(editor.Hwnd) == "Edit";
        var start = ToStoredOffset(first.Text, first.Start, classic);
        var end = ToStoredOffset(first.Text, first.End, classic);
        ForegroundSnapshot.Verify(snapshot);
        var second = ReadEditor(editor, context);
        if (first != second) throw new BookmarkException(ResultCode.ContextChanged);
        context.Check(); ForegroundSnapshot.Verify(snapshot); VerifyEditor(editor);
        // The complete snapshot is persisted in the bookmark transaction. Capturing never prompts for
        // a filename, saves the source document, or changes its text/selection, including an empty tab.
        var target = NotepadSnapshotPolicy.Create(NormalizeSnapshotTitle(first.Title), NormalizeText(first.Text), start, end, first.Modified);
        return context.Response(ResultCode.Captured, target);
    }
    internal static WorkerResponse Resume(CapturedTarget target, RequestContext context, bool validateOnly)
    {
        if (target.Kind == TargetKind.NotepadSnapshot) return ResumeSnapshot(target, context, validateOnly);
        var path = RequireLocalPath(target.Path);
        if (validateOnly)
        {
            var file = ReadFile(path, context);
            if (target.TextOffset is not >= 0 || !CanRestoreOffset(target.TextOffset.Value, NormalizeText(file.Text).Length))
                throw new BookmarkException(ResultCode.OpenedPositionFailed);
            return context.Response(ResultCode.Validated, target);
        }
        WindowsAdapter.CheckExists(target);
        context.Check();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        context.ExternalActionStarted = true;
        try { using var launched = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch { context.ExternalActionStarted = false; throw new BookmarkException(ResultCode.TargetUnavailable); }
        // Modern Notepad forwards opens and restores old sessions. Neither a new HWND nor a matching title/text
        // proves that a tab owns this exact path. Report the open request without touching any editor's selection.
        return context.Response(ResultCode.NotepadOpenRequested);
    }

    private static Editor FromSnapshot(TargetSnapshot snapshot)
    {
        var editor = new Editor((nint)snapshot.Hwnd, (nint)snapshot.ActiveViewHwnd, snapshot.ProcessId, snapshot.ProcessStartTimeUtcTicks);
        VerifyEditor(editor);
        var visible = Editors(editor.Root).Where(Native.IsWindowVisible).ToArray();
        if (visible.Length != 1 || visible[0] != editor.Hwnd) throw new BookmarkException(ResultCode.AmbiguousTarget);
        return editor;
    }
    private static void VerifyEditor(Editor editor)
    {
        if (editor.Root == 0 || editor.Hwnd == 0 || !Native.IsWindow(editor.Root) || !Native.IsWindow(editor.Hwnd) ||
            Native.Class(editor.Root) != "Notepad" || !IsEditorClass(Native.Class(editor.Hwnd)) || !Native.BelongsTo(editor.Hwnd, editor.Root))
            throw new BookmarkException(ResultCode.ContextChanged);
        Native.GetWindowThreadProcessId(editor.Root, out var pid);
        Native.GetWindowThreadProcessId(editor.Hwnd, out var childPid);
        if (pid != editor.ProcessId || childPid != pid || editor.ProcessStamp == 0 || Native.ProcessStamp(pid) != editor.ProcessStamp)
            throw new BookmarkException(ResultCode.ContextChanged);
        if (!Native.IsUnelevatedProcess(pid)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        using var process = Process.GetProcessById((int)pid);
        if (!process.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase)) throw new BookmarkException(ResultCode.UnsupportedTarget);
    }
    internal static bool IsEditorClass(string value) => value is "Edit" or "RichEditD2DPT";
    private static IEnumerable<nint> Editors(nint root) => Native.Children(root, "Edit", false).Concat(Native.Children(root, "RichEditD2DPT", false)).Distinct();
    private static EditorState ReadEditor(Editor editor, RequestContext context)
    {
        VerifyEditor(editor); context.Check();
        var title = new StringBuilder(1024); GetWindowText(editor.Root, title, title.Capacity);
        var length = Number(editor.Hwnd, 0xE, 0, 0, context); // WM_GETTEXTLENGTH may overestimate rich-edit length.
        if (length > MaximumTextCharacters * 2) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var text = new StringBuilder(checked((int)length + 1));
        if (SendMessageTimeout(editor.Hwnd, 0xD, (nuint)text.Capacity, text, TimeoutFlags, 500, out var copied) == 0)
            throw new BookmarkException(ResultCode.AppBusy);
        if (copied >= (nuint)text.Capacity) throw new BookmarkException(ResultCode.ContextChanged);
        if (NormalizeText(text.ToString()).Length > MaximumTextCharacters) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var (begin, end) = ReadSelection(editor.Hwnd, context);
        var modified = Number(editor.Hwnd, 0xB8, 0, 0, context) != 0;
        if (begin < 0 || end < begin || end > (Native.Class(editor.Hwnd) == "Edit" ? text.Length : NormalizeText(text.ToString()).Length)) throw new BookmarkException(ResultCode.ContextChanged);
        context.Check(); VerifyEditor(editor);
        return new(title.ToString(), text.ToString(), begin, end, modified);
    }
    internal static (int Start, int End) ReadSelection(nint hwnd, RequestContext context)
    {
        context.Check(); var begin = -1; var end = -1;
        // The packed return is limited to 16 bits; the marshalled outputs preserve full DWORD offsets.
        if (SendMessageTimeout(hwnd, 0xB0, ref begin, ref end, TimeoutFlags, 500, out _) == 0)
            throw new BookmarkException(ResultCode.AppBusy);
        context.Check(); return (begin, end);
    }
    private static nuint Number(nint hwnd, uint message, nuint wParam, nint lParam, RequestContext context)
    {
        context.Check();
        if (SendMessageTimeout(hwnd, message, wParam, lParam, TimeoutFlags, 500, out var value) == 0) throw new BookmarkException(ResultCode.AppBusy);
        context.Check(); return value;
    }
    private static FileState ReadFile(string path, RequestContext context)
    {
        context.Check();
        var info = new FileInfo(path);
        if (!info.Exists) throw new BookmarkException(ResultCode.TargetUnavailable);
        if (info.Length > MaximumFileBytes) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var identity = FileIdentity.TryRead(path);
        var length = info.Length; var written = info.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // BOM detection supports UTF-8/16/32. Invalid BOM-less UTF-8 fails closed instead of comparing replacement characters.
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var buffer = new char[8192]; var text = new StringBuilder();
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                context.Check(); if (text.Length + count > MaximumTextCharacters) throw new BookmarkException(ResultCode.UnsupportedTarget);
                text.Append(buffer, 0, count);
            }
        }
        catch (DecoderFallbackException) { throw new BookmarkException(ResultCode.UnsupportedTarget); }
        var result = new FileState(text.ToString(), identity, length, written);
        if (!SameFile(path, result)) throw new BookmarkException(ResultCode.ContextChanged);
        return result;
    }
    private static bool SameFile(string path, FileState saved)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == saved.Length && info.LastWriteTimeUtc == saved.LastWriteUtc &&
            saved.Identity is not null && FileIdentity.TryRead(path) == saved.Identity;
    }
    private static string RequireLocalPath(string path)
    {
        var normalized = PathPolicy.Normalize(path);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) || normalized.Length < 3 || normalized[1] != ':')
            throw new BookmarkException(ResultCode.UnsupportedTarget);
        return normalized;
    }
    internal static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    internal static string NormalizeSnapshotTitle(string title)
    {
        if (title.StartsWith('*')) title = title[1..];
        foreach (var suffix in new[] { " - 메모장", " - Notepad" })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return title[..^suffix.Length];
        return title;
    }
    internal static int ToStoredOffset(string text, int offset, bool classic)
    {
        var coordinates = classic ? text : NormalizeText(text);
        if (!CanRestoreOffset(offset, coordinates.Length)) throw new BookmarkException(ResultCode.ContextChanged);
        if (classic && offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n')
            throw new BookmarkException(ResultCode.ContextChanged);
        return classic ? NormalizeText(text[..offset]).Length : offset;
    }
    internal static int ToNativeOffset(string text, int offset, bool classic)
    {
        if (!CanRestoreOffset(offset, NormalizeText(text).Length)) throw new BookmarkException(ResultCode.OpenedPositionFailed);
        if (!classic) return offset;
        var stored = 0; var native = 0;
        while (stored < offset)
        {
            if (text[native] == '\r' && native + 1 < text.Length && text[native + 1] == '\n') native++;
            native++; stored++;
        }
        return native;
    }
    internal static bool CanRestoreOffset(int offset, int length) => offset >= 0 && offset <= length;
    internal static bool MatchesTitle(string title, string path)
    {
        var filename = Path.GetFileName(path);
        if (string.IsNullOrEmpty(filename)) return false;
        if (title.StartsWith('*')) title = title[1..];
        return title.Equals(filename + " - 메모장", StringComparison.OrdinalIgnoreCase) ||
            title.Equals(filename + " - Notepad", StringComparison.OrdinalIgnoreCase);
    }
}
