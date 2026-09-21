namespace WorkBookmark.Core;

/// <summary>Office document locations only. No authentication, downloading or filesystem access.</summary>
public static class OfficeLocation
{
    public const int MaximumUrlLength = 16384;

    public static bool IsOfficeKind(TargetKind kind) =>
        kind is TargetKind.ExcelCell or TargetKind.WordPosition or TargetKind.PowerPointSlide;

    public static bool IsWeb(string? path) => path is not null &&
        (path.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https:", StringComparison.OrdinalIgnoreCase));

    public static bool IsWebTarget(CapturedTarget target) => IsOfficeKind(target.Kind) && IsWeb(target.Path);

    public static string Normalize(TargetKind kind, string path)
    {
        if (!IsOfficeKind(kind)) throw Invalid();
        return IsWeb(path) ? NormalizeUrl(path) : PathPolicy.Normalize(path);
    }

    public static string NormalizeUrl(string path)
    {
        // Native Office can return an IRI with literal spaces/Unicode in the path.
        // Preserve path case, query order, escaped separators and fragments; never infer
        // equivalence between sharing links, redirects, local caches or different URLs.
        if (string.IsNullOrEmpty(path) || path.Length > MaximumUrlLength || path != path.Trim() ||
            !(path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
            path.Any(c => Forbidden(c) || char.IsWhiteSpace(c) && c != ' ')) throw Invalid();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && (i + 2 >= path.Length || !Uri.IsHexDigit(path[i + 1]) || !Uri.IsHexDigit(path[i + 2]))) throw Invalid();
            if (char.IsSurrogate(path[i]) && (!char.IsHighSurrogate(path[i]) || ++i >= path.Length || !char.IsLowSurrogate(path[i]))) throw Invalid();
        }
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            Uri.UnescapeDataString(path).Any(Forbidden)) throw Invalid();
        string normalized = uri.AbsoluteUri;
        if (normalized.Length > MaximumUrlLength) throw Invalid();
        return normalized;
    }

    public static string DisplayName(string path)
    {
        var uri = new Uri(NormalizeUrl(path));
        string name = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]);
        return string.IsNullOrEmpty(name) ? uri.Host : name;
    }

    public static string LaunchUri(CapturedTarget target)
    {
        PathPolicy.Validate(target);
        if (!IsWebTarget(target)) throw Invalid();
        string scheme = target.Kind switch
        {
            TargetKind.ExcelCell => "ms-excel",
            TargetKind.WordPosition => "ms-word",
            TargetKind.PowerPointSlide => "ms-powerpoint",
            _ => throw Invalid()
        };
        // A fixed command and application, never an arbitrary handler from a saved URL.
        // Office performs its normal sign-in and applies the server's access permissions.
        return scheme + ":ofe|u|" + NormalizeUrl(target.Path);
    }

    private static bool Forbidden(char c) => char.IsControl(c) || c is '\\' or '|' or '"' or '<' or '>';
    private static BookmarkException Invalid() => new(ResultCode.UnsupportedTarget);
}
