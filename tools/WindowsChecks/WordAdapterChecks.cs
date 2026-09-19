using System.Text.Json;
using WorkBookmark.Core;
using WorkBookmark.Windows;

internal static class WordAdapterChecks
{
    internal static int Run(string[] args)
    {
        var checks = new List<object>();
        void Assert(bool condition, string name)
        {
            checks.Add(new { name, passed = condition });
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS: " + name);
        }
        bool Accepted(int story, int type, int start, int end, int documentEnd)
        {
            try { WordAdapter.ValidateCaptureCoordinates(story, type, start, end, documentEnd); return true; }
            catch (BookmarkException) { return false; }
        }
        bool ProtectionAccepted(int protectionType)
        {
            try { WordAdapter.ValidateDocumentProtection(protectionType); return true; }
            catch (BookmarkException error) when (error.Code == ResultCode.UnsupportedTarget) { return false; }
        }
        Assert(ProtectionAccepted(-1), "unprotected body navigation remains supported");
        Assert(ProtectionAccepted(3), "read-only editing restriction permits metadata capture and body navigation without removing protection");
        foreach (var protectionType in new[] { 0, 1, 2, 4, int.MinValue, int.MaxValue })
            Assert(!ProtectionAccepted(protectionType), $"unsupported or unknown editing restriction {protectionType} is refused");
        Assert(Accepted(1, 1, 0, 0, 1), "saved empty Word body accepts its first insertion point");
        Assert(Accepted(1, 2, 10, 30, 100), "normal body selection records its start without requiring selected text");
        Assert(!Accepted(7, 1, 10, 10, 100), "header coordinate cannot masquerade as a body offset");
        Assert(!Accepted(2, 1, 10, 10, 100), "footnote coordinate cannot masquerade as a body offset");
        Assert(!Accepted(5, 2, 10, 30, 100), "text-frame coordinate cannot masquerade as a body offset");
        Assert(!Accepted(1, 6, 10, 30, 100), "discontinuous block selection is refused");
        Assert(!Accepted(1, 8, 10, 30, 100), "shape selection is refused");
        Assert(!Accepted(1, 0, 0, 0, 100), "no-selection state is refused");
        Assert(!Accepted(1, 1, -1, 0, 100), "negative capture offset is refused");
        Assert(!Accepted(1, 2, 30, 10, 100), "reversed capture coordinates are refused");
        Assert(!Accepted(1, 2, 10, 101, 100), "selection that outlived a shortened body is refused");
        Assert(WordAdapter.CanRestoreOffset(0, 1), "restore accepts zero offset in an empty body");
        Assert(!WordAdapter.CanRestoreOffset(-1, 100), "restore rejects negative offsets");
        Assert(!WordAdapter.CanRestoreOffset(100, 99), "restore never clamps an offset after the document shrinks");
        Assert(!WordAdapter.CanRestoreOffset(0, 0), "restore refuses unavailable main-story bounds");
        Assert(WordAdapter.CanRestoreOffset(int.MaxValue, int.MaxValue), "offset comparison does not overflow at COM Long maximum");
        Console.WriteLine($"RESULT: {checks.Count} Word coordinate/protection checks passed. This does not certify installed Word COM capture or full UI behavior.");
        if (args.Length > 1) File.WriteAllText(args[1], JsonSerializer.Serialize(new { suite = "Word coordinate/protection safety", checks, limitation = "Pure coordinate and protection regression checks; installed Word capture/resume, edit drift, multiple windows and format coverage require separate fixtures." }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
