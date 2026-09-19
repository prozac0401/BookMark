using System.Text.RegularExpressions;

namespace WorkBookmark.Core;

/// <summary>Pure Windows path policy: no file or network access and no alias merging.</summary>
public static partial class PathPolicy
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".rtf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv",
        ".ppt", ".pptx", ".pptm", ".pdf", ".txt", ".md", ".hwp", ".hwpx", ".png",
        ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff"
    };
    private static readonly HashSet<string> Workbooks = new(StringComparer.OrdinalIgnoreCase)
        { ".xlsx", ".xlsm", ".xlsb", ".xls" };

    private static readonly HashSet<string> WordDocuments = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".docm", ".rtf" };
    private static readonly HashSet<string> Presentations = new(StringComparer.OrdinalIgnoreCase) { ".ppt", ".pptx", ".pptm" };

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32766 || path.Any(c => c < 32))
            throw Invalid();
        string value = path.Replace('/', '\\');
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal)
            || value.StartsWith(@"\??\", StringComparison.Ordinal) || value.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0)
            throw Invalid();
        string root;
        string remainder;
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\')
        {
            root = value[..3];
            remainder = value[3..];
        }
        else if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = value[2..].Split('\\');
            if (parts.Length < 2 || !ValidComponent(parts[0]) || !ValidComponent(parts[1])
                || parts[0] is "." or ".." || parts[1] is "." or ".."
                || parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("mailslot", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            root = @"\\" + parts[0] + "\\" + parts[1] + "\\";
            remainder = string.Join("\\", parts.Skip(2));
        }
        else throw Invalid();
        var segments = new List<string>();
        foreach (var segment in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw Invalid();
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (!ValidComponent(segment)) throw Invalid();
            segments.Add(segment);
        }
        return root + string.Join("\\", segments);
    }

    public static CapturedTarget Validate(CapturedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == TargetKind.NotepadSnapshot) return NotepadSnapshotPolicy.Validate(target);
        if (target.TextContent is not null || target.TextSelectionEnd is not null || target.SnapshotTitle is not null) throw Invalid();
        if (target.Kind == TargetKind.WebPage) return BrowserProtocol.ValidateTarget(target);
        if (target.PageTitle is not null || target.Kind != TargetKind.NotepadPosition && target.TextOffset is not null) throw Invalid();
        string normalized = Normalize(target.Path);
        if (!Enum.IsDefined(target.Kind)) throw Invalid();
        if (target.Kind != TargetKind.Folder && normalized.EndsWith('\\')) throw Invalid();
        if (target.Kind != TargetKind.PdfPage && target.PdfPage is not null) throw Invalid();
        if (target.Kind != TargetKind.WordPosition && target.WordStart is not null ||
            target.Kind != TargetKind.PowerPointSlide && (target.SlideId is not null || target.SlideNumber is not null)) throw Invalid();
        if (target.Kind == TargetKind.ExcelCell)
        {
            if (!Workbooks.Contains(Extension(normalized)) || string.IsNullOrEmpty(target.SheetName)
                || target.SheetName.Length > 31 || target.SheetName.Any(c => c < 32)
                || target.SheetName.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0
                || target.SheetName.StartsWith('\'') || target.SheetName.EndsWith('\'')
                || target.HadUnsavedChanges is null) throw Invalid();
            var match = CellPattern().Match(target.CellAddress ?? "");
            if (!match.Success) throw Invalid();
            int column = 0;
            foreach (char c in match.Groups[1].Value) column = checked(column * 26 + c - 'A' + 1);
            if (column > 16384 || !int.TryParse(match.Groups[2].Value, out int row) || row > 1048576) throw Invalid();
        }
        else if (target.Kind == TargetKind.WordPosition)
        {
            if (!WordDocuments.Contains(Extension(normalized)) || target.WordStart is not >= 0 ||
                target.HadUnsavedChanges is null || target.SheetName is not null || target.CellAddress is not null) throw Invalid();
        }
        else if (target.Kind == TargetKind.PowerPointSlide)
        {
            if (!Presentations.Contains(Extension(normalized)) || target.SlideId is not > 0 || target.SlideNumber is not > 0 ||
                target.HadUnsavedChanges is null || target.SheetName is not null || target.CellAddress is not null) throw Invalid();
        }
        else if (target.Kind == TargetKind.NotepadPosition)
        {
            // The adapter opens this as text in Notepad; the file association is never invoked.
            if (target.TextOffset is not >= 0 || target.HadUnsavedChanges is null ||
                target.SheetName is not null || target.CellAddress is not null) throw Invalid();
        }
        else if (target.Kind == TargetKind.PdfPage)
        {
            if (!Extension(normalized).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || target.PdfPage is not > 0 ||
                target.HadUnsavedChanges is not null || target.SheetName is not null || target.CellAddress is not null) throw Invalid();
        }
        else if (target.SheetName != null || target.CellAddress != null || target.HadUnsavedChanges != null) throw Invalid();
        return target;
    }

    public static string NormalizeLocation(CapturedTarget target) => target.Kind switch
    {
        TargetKind.NotepadSnapshot => NotepadSnapshotPolicy.Validate(target).Path,
        TargetKind.WebPage => BrowserProtocol.ValidateTarget(target).Path,
        _ => Normalize(target.Path)
    };
    public static string DisplayName(CapturedTarget target) => target.Kind switch
    {
        TargetKind.NotepadSnapshot => NotepadSnapshotPolicy.Validate(target).SnapshotTitle!,
        TargetKind.WebPage => BrowserProtocol.ValidateTarget(target).PageTitle!,
        _ => DisplayName(target.Path)
    };

    public static bool ShouldOpenDocument(string path) => Documents.Contains(Extension(Normalize(path)));

    public static string DisplayName(string path)
    {
        string normalized = Normalize(path);
        if (normalized.EndsWith('\\')) return normalized;
        return normalized[(normalized.LastIndexOf('\\') + 1)..];
    }

    private static string Extension(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('\\') ? path[dot..] : "";
    }

    private static bool ValidComponent(string part)
    {
        if (part.Length == 0 || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.') || part.Contains(':')) return false;
        string stem = part.Split('.')[0].TrimEnd(' ', '.');
        return !DevicePattern().IsMatch(stem);
    }

    private static BookmarkException Invalid() => new(ResultCode.UnsupportedTarget);
    [GeneratedRegex(@"^\$([A-Z]{1,3})\$([1-9][0-9]{0,6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();
}
