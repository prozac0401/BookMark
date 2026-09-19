using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WorkBookmark.Core;

/// <summary>Canonical local Notepad content snapshots. Coordinates are UTF-16 offsets in LF-normalized text.</summary>
public static class NotepadSnapshotPolicy
{
    public const int MaximumTextLength = 2 * 1024 * 1024;
    public const int MaximumTitleLength = 256;
    public const string PathPrefix = "notepad-snapshot:";

    /// <summary>Accepts editor text and its raw UTF-16 offsets; normalizes CRLF/CR and adjusts both offsets.</summary>
    public static CapturedTarget Create(string title, string text, int start, int end, bool modified)
    {
        if (text is null || text.Length > MaximumTextLength * 2 || start < 0 || end < start || end > text.Length)
            throw Invalid();
        ValidateUnicode(text);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int normalizedStart = NormalizedOffset(text, start);
        int normalizedEnd = NormalizedOffset(text, end);
        string normalizedTitle = SanitizeTitle(title);
        return Validate(new CapturedTarget(TargetKind.NotepadSnapshot, Identity(normalizedTitle, normalized),
            HadUnsavedChanges: modified, TextOffset: normalizedStart, TextContent: normalized,
            TextSelectionEnd: normalizedEnd, SnapshotTitle: normalizedTitle));
    }

    public static CapturedTarget Validate(CapturedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != TargetKind.NotepadSnapshot || target.TextContent is null ||
            target.TextContent.Length > MaximumTextLength || target.TextContent.Contains('\r') ||
            target.SnapshotTitle is null || target.SnapshotTitle != SanitizeTitle(target.SnapshotTitle) ||
            target.TextOffset is not >= 0 || target.TextSelectionEnd is null ||
            target.TextSelectionEnd < target.TextOffset || target.TextSelectionEnd > target.TextContent.Length ||
            target.HadUnsavedChanges is null || target.SheetName is not null || target.CellAddress is not null ||
            target.WordStart is not null || target.SlideId is not null || target.SlideNumber is not null ||
            target.PdfPage is not null || target.PageTitle is not null)
            throw Invalid();
        ValidateUnicode(target.TextContent);
        if (!string.Equals(target.Path, Identity(target.SnapshotTitle, target.TextContent), StringComparison.Ordinal)) throw Invalid();
        return target;
    }

    private static int NormalizedOffset(string text, int offset)
    {
        int result = offset;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                // A boundary between CR and LF maps to the same boundary as the full newline.
                if (i + 1 < offset) { result--; i++; }
            }
        return result;
    }

    private static string SanitizeTitle(string title)
    {
        if (title is null) throw Invalid();
        ValidateUnicode(title);
        string value = new(title.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
        value = value.Trim();
        if (value.Length > MaximumTitleLength)
        {
            int length = MaximumTitleLength;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            value = value[..length].TrimEnd();
        }
        return value.Length == 0 ? "제목 없음" : value;
    }

    private static void ValidateUnicode(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\0' || char.IsLowSurrogate(text[i])) throw Invalid();
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) throw Invalid();
            }
        }
    }

    private static string Identity(string title, string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (string field in new[] { title, text })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return PathPrefix + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static BookmarkException Invalid() => new(ResultCode.UnsupportedTarget);
}
