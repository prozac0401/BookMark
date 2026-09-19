using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WorkBookmark.Core;
using WorkBookmark.Windows;

/// <summary>Read-only source checks plus real installed Notepad recovery of explicitly prepared synthetic tabs.</summary>
internal static class NotepadSnapshotChecks
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, StringBuilder lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, uint length, out uint needed);

    internal static int RunNative(string[] args)
    {
        if (args.Length < 4 || !long.TryParse(args[2], out var raw) || raw == 0)
            throw new ArgumentException("notepad-checks snapshot-native SYNTHETIC_HWND OUTPUT_JSON");
        var checks = new List<object>();
        var trace = new List<string>();
        NotepadAdapter.SnapshotTrace = message => { trace.Add(message); Console.WriteLine(message); };
        ResumeGuard.ProbeTrace = message => { trace.Add(message); Console.WriteLine("guard=" + message); };
        void Assert(bool passed, string name)
        {
            checks.Add(new { name, passed });
            Console.WriteLine((passed ? "PASS: " : "FAIL: ") + name);
            if (!passed) throw new InvalidOperationException(name);
        }
        RequestContext Context() => new(new WorkerRequest(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(5)));
        string ReadText(nint editor)
        {
            var text = new StringBuilder(NotepadAdapter.MaximumTextCharacters + 1);
            if (SendMessageTimeout(editor, 0xD, (nuint)text.Capacity, text, 0x22, 500, out _) == 0)
                throw new InvalidOperationException("Cannot read synthetic text.");
            return text.ToString();
        }
        string Title(nint hwnd) { var text = new StringBuilder(1024); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
        var snapshot = ForegroundSnapshot.Capture();
        string Desktop(uint thread) { var name = new StringBuilder(256); GetUserObjectInformation(GetThreadDesktop(thread), 2, name, 512, out _); return name.ToString(); }
        Console.WriteLine("test desktop=" + Desktop(GetCurrentThreadId()) + " source desktop=" + Desktop(Native.GetWindowThreadProcessId((nint)snapshot.Hwnd, out _)));
        using (var sourceProcess = System.Diagnostics.Process.GetProcessById((int)snapshot.ProcessId)) Console.WriteLine("source executable=" + sourceProcess.MainModule?.FileName);
        Assert(snapshot.Hwnd == raw && Native.Class((nint)raw) == "Notepad" && snapshot.ActiveViewHwnd != 0,
            "the explicitly prepared synthetic Notepad window is foreground with one editor");
        var source = (nint)snapshot.ActiveViewHwnd;
        var originalRawText = ReadText(source);
        var originalText = NotepadAdapter.NormalizeText(originalRawText);
        Assert(originalText.Length == 0 || originalText.StartsWith("WORKBOOKMARK SNAPSHOT AUTOMATION", StringComparison.Ordinal),
            "only a blank or clearly marked synthetic test tab is accepted");
        var originalTitle = Title((nint)raw);
        var originalSelection = NotepadAdapter.ReadSelection(source, Context());
        SendMessageTimeout(source, 0xCE, 0, 0, 0x22, 500, out var originalFirstVisibleLine);
        var copiesBefore = Directory.Exists(NotepadAdapter.RecoveryDirectory) ? Directory.GetFiles(NotepadAdapter.RecoveryDirectory).Length : 0;
        CapturedTarget? capturedTarget = null;
        WorkerResponse? resumed = null;
        string? failure = null;
        try
        {
            var captured = WindowsAdapter.Execute(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(10), snapshot));
            Assert(captured.Code == ResultCode.Captured && captured.Target?.Kind == TargetKind.NotepadSnapshot && captured.NotepadObservation is null,
                "new document captures immediately as a text snapshot without source-file linking");
            capturedTarget = captured.Target!;
            Assert(capturedTarget.TextContent == originalText, "capture contains the full exact LF-normalized editor text, including blank content");
            Assert(capturedTarget.TextOffset == NotepadAdapter.ToStoredOffset(originalRawText, originalSelection.Start, Native.Class(source) == "Edit") &&
                capturedTarget.TextSelectionEnd == NotepadAdapter.ToStoredOffset(originalRawText, originalSelection.End, Native.Class(source) == "Edit"),
                "capture retains both ends of the actual source selection");
            var copiesAfter = Directory.Exists(NotepadAdapter.RecoveryDirectory) ? Directory.GetFiles(NotepadAdapter.RecoveryDirectory).Length : 0;
            Assert(copiesAfter == copiesBefore, "capturing creates no recovery file and requires no user-chosen save location");
            // Force a protocol serialization boundary: recovery must use saved text, never a live editor reference.
            var persisted = JsonSerializer.Deserialize<CapturedTarget>(JsonSerializer.Serialize(capturedTarget))!;
            Assert(persisted == capturedTarget, "snapshot text, title, state and selection survive JSON persistence transport");
            resumed = WindowsAdapter.Execute(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), snapshot, persisted));
            Assert(resumed.Code is ResultCode.PositionRestored or ResultCode.PositionRestoredFocusPending,
                "installed Notepad confirms the new recovery document content and selection");
            Assert(resumed.ExternalActionStarted && resumed.TargetHwnd != 0,
                "resume identifies the actual recovery window after an explicit Notepad launch");
            var restoredRoot = (nint)resumed.TargetHwnd;
            var restoredEditors = Native.Children(restoredRoot, "Edit", true).Concat(Native.Children(restoredRoot, "RichEditD2DPT", true)).ToArray();
            Assert(restoredEditors.Length == 1 && (originalText.Length == 0 || restoredEditors[0] != source), "recovery never overwrites a nonempty source editor (Notepad may reuse a blank tab)");
            var restoredEditor = restoredEditors.Single();
            var restoredRawText = ReadText(restoredEditor);
            Assert(NotepadAdapter.NormalizeText(restoredRawText) == originalText, "independent native read verifies all recovered Unicode text and line breaks");
            var selected = NotepadAdapter.ReadSelection(restoredEditor, Context());
            Assert(selected.Start == NotepadAdapter.ToNativeOffset(restoredRawText, persisted.TextOffset!.Value, Native.Class(restoredEditor) == "Edit") &&
                selected.End == NotepadAdapter.ToNativeOffset(restoredRawText, persisted.TextSelectionEnd!.Value, Native.Class(restoredEditor) == "Edit"),
                "independent native read verifies the restored caret and selection range");
            if (originalFirstVisibleLine > 0)
            {
                Assert(SendMessageTimeout(restoredEditor, 0xCE, 0, 0, 0x22, 500, out var restoredFirstVisibleLine) != 0 && restoredFirstVisibleLine > 0,
                    "restoring a distant selected range scrolls the actual Notepad view beyond the initial page");
            }
            var newCopies = Directory.GetFiles(NotepadAdapter.RecoveryDirectory);
            Assert(newCopies.Length == copiesBefore + 1, "restore creates exactly one automatically managed working copy");
            Assert(ReadText(source) == originalRawText && (resumed.TargetHwnd == raw || Title((nint)raw) == originalTitle) && NotepadAdapter.ReadSelection(source, Context()) == originalSelection,
                "source editor text and selection remain unchanged; the window title may reflect the restored active tab");
        }
        catch (Exception error) { failure = error.Message; }
        finally
        {
            var evidence = new
            {
                suite = "Installed Notepad snapshot capture and recovery", executedAtUtc = DateTimeOffset.UtcNow,
                sourceHwnd = raw, sourceEditorClass = Native.Class(source), characters = originalText.Length,
                selection = originalSelection.ToString(), capturedKind = capturedTarget?.Kind.ToString(),
                resumeCode = resumed?.Code.ToString(), recoveryHwnd = resumed?.TargetHwnd,
                checks, trace, failure, limitation = "Actual native Notepad UI plus JSON transport. SQLite restart, hotkey/tray UX and active typing races are covered separately. No source document is saved or closed; recovery windows are left open for inspection."
            };
            File.WriteAllText(args[3], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;
    }
}
