using System.Text.Json;
using System.Windows.Forms;
using WorkBookmark.Core;
using WorkBookmark.Windows;

internal static class NotepadAdapterChecks
{
    internal static int Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "snapshot-native") return NotepadSnapshotChecks.RunNative(args);
        var checks = new List<object>();
        void Assert(bool condition, string name)
        {
            checks.Add(new { name, passed = condition });
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS: " + name);
        }
        bool Rejects(Action action, ResultCode expected)
        {
            try { action(); return false; }
            catch (BookmarkException error) { return error.Code == expected; }
        }
        var draft = NotepadSnapshotPolicy.Create("제목 없음 - 메모장", "A😀한\r\n끝", 1, 4, true);
        Assert(draft.Kind == TargetKind.NotepadSnapshot && draft.HadUnsavedChanges == true, "unsaved new text is a complete restorable snapshot");
        Assert(draft.TextContent == "A😀한\n끝" && draft.TextOffset == 1 && draft.TextSelectionEnd == 4, "snapshot preserves Unicode and selected UTF-16 range");
        Assert(NotepadSnapshotPolicy.Create("제목 없음", "", 0, 0, false).TextContent == "", "a new empty tab is a valid snapshot");
        Assert(NotepadSnapshotPolicy.Create(NotepadAdapter.NormalizeSnapshotTitle("*notes.txt - Notepad"), "same", 1, 2, true).Path ==
            NotepadSnapshotPolicy.Create(NotepadAdapter.NormalizeSnapshotTitle("notes.txt - 메모장"), "same", 1, 2, false).Path,
            "dirty marker and localized app suffix do not split identical snapshots after source saving");
        var paths = new List<string>();
        try
        {
            var contextForCopies = new RequestContext(new WorkerRequest(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(10)));
            var firstCopy = NotepadAdapter.WriteRecoveryCopy(draft, contextForCopies); paths.Add(firstCopy);
            Assert(FileIdentity.TryRead(firstCopy) == FileIdentity.TryRead(Path.Combine(NotepadAdapter.RecoveryDirectory, Path.GetFileName(firstCopy))) && File.ReadAllText(firstCopy) == draft.TextContent,
                "working copy is automatically stored under app LocalAppData with exact text and resolved through host file virtualization");
            Assert(File.ReadAllBytes(firstCopy).AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "physical working copy explicitly stores a UTF-8 BOM");
            File.WriteAllText(firstCopy, "edited recovery work");
            var secondCopy = NotepadAdapter.WriteRecoveryCopy(draft, contextForCopies); paths.Add(secondCopy);
            Assert(firstCopy != secondCopy && File.ReadAllText(firstCopy) == "edited recovery work" && File.ReadAllText(secondCopy) == draft.TextContent, "repeat resume never overwrites an earlier edited recovery copy");
            var blankCopy = NotepadAdapter.WriteRecoveryCopy(NotepadSnapshotPolicy.Create("empty", "", 0, 0, false), contextForCopies); paths.Add(blankCopy);
            Assert(File.ReadAllText(blankCopy) == "", "empty recovery copies decode to empty text");
            Assert(Rejects(() => NotepadAdapter.WriteRecoveryCopy(draft with { TextContent = "tampered" }, contextForCopies), ResultCode.UnsupportedTarget), "corrupt content fails before a recovery copy is written");
        }
        finally { foreach (var path in paths) File.Delete(path); }
        const string sourcePath = @"C:\synthetic\한글 메모.txt";
        Assert(NotepadAdapter.MatchesTitle("한글 메모.txt - 메모장", sourcePath), "Korean title matches the complete fresh recovery basename");
        Assert(NotepadAdapter.MatchesTitle("한글 메모.txt - Notepad", sourcePath), "English title matches the complete fresh recovery basename");
        Assert(!NotepadAdapter.MatchesTitle("한글 메모.txt - other.txt - 메모장", sourcePath), "filename-prefix collisions cannot authorize another editor");
        Assert(NotepadAdapter.ToStoredOffset("A\r\n한글\r\nB", 7, true) == 5, "classic CRLF caret converts to a stable LF offset");
        Assert(NotepadAdapter.ToNativeOffset("A\r\n한글\r\nB", 5, true) == 7, "stable LF offset converts back to a classic caret");
        Assert(NotepadAdapter.ToStoredOffset("A\r\n한글\r\nB", 5, false) == 5, "RichEdit already counts a line break as one character");
        Assert(Rejects(() => NotepadAdapter.ToStoredOffset("A\r\nB", 2, true), ResultCode.ContextChanged), "an impossible caret inside a CRLF pair fails closed");
        Assert(NotepadAdapter.CanRestoreOffset(0, 0), "empty documents preserve the initial caret");
        Assert(!NotepadAdapter.CanRestoreOffset(20, 19), "reopen never clamps a caret after text becomes shorter");
        Assert(!NotepadAdapter.CanRestoreOffset(-1, 20), "negative offsets cannot be restored");
        var unicode = "A😀한\r\n끝";
        for (var offset = 0; offset <= NotepadAdapter.NormalizeText(unicode).Length; offset++)
        {
            var native = NotepadAdapter.ToNativeOffset(unicode, offset, true);
            Assert(NotepadAdapter.ToStoredOffset(unicode, native, true) == offset, $"UTF-16 offset {offset} round trips across CRLF without losing surrogate units");
        }
        using var form = new Form();
        using var editor = new RichTextBox { Text = new string('x', 100000) };
        form.Controls.Add(editor); _ = form.Handle; _ = editor.Handle;
        editor.Select(70000, 1234);
        var context = new RequestContext(new WorkerRequest(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(5)));
        var selection = NotepadAdapter.ReadSelection(editor.Handle, context);
        Assert(selection == (70000, 71234), "native selection transport preserves start and end beyond 65,535");
        Assert(editor.TextLength == 100000 && editor.SelectionStart == 70000 && editor.SelectionLength == 1234, "read-only selection transport does not change text or selection");
        Console.WriteLine($"RESULT: {checks.Count} Notepad snapshot recovery and coordinate checks passed.");
        if (args.Length > 1) File.WriteAllText(args[1], JsonSerializer.Serialize(new { suite = "Notepad snapshot recovery and caret safety", checks, limitation = "Synthetic control and pure policy checks; installed Notepad capture and reopen are separate validation." }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
