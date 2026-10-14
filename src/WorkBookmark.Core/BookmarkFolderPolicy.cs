namespace WorkBookmark.Core;

public static class BookmarkFolderPolicy
{
    public static string ValidateName(string name)
    {
        if (name is null) throw new BookmarkException(ResultCode.InvalidRequest);
        string normalized = name.Trim();
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl))
            throw new BookmarkException(ResultCode.InvalidRequest);
        return normalized;
    }
}
