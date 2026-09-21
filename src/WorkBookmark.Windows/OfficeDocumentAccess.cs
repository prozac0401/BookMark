using WorkBookmark.Core;

namespace WorkBookmark.Windows;

internal static class OfficeDocumentAccess
{
    internal static string Normalize(TargetKind kind, string path)
    {
        string normalized = OfficeLocation.Normalize(kind, path);
        // Preserve the existing local/UNC support boundary for Word and PowerPoint.
        if (!OfficeLocation.IsWeb(normalized) && kind != TargetKind.ExcelCell && normalized[1] != ':')
            throw new BookmarkException(ResultCode.UnsupportedTarget);
        return normalized;
    }

    internal static void CheckExists(CapturedTarget target, Action<CapturedTarget>? checkFile = null)
    {
        PathPolicy.Validate(target);
        _ = Normalize(target.Kind, target.Path);
        // An open Office document is the observation for a web location. Probing it with
        // File.Exists/HTTP would use a different authentication context and can falsely fail.
        if (!OfficeLocation.IsWebTarget(target)) (checkFile ?? WindowsAdapter.CheckExists)(target);
    }

    internal static void Open(CapturedTarget target, RequestContext context, Func<string, long>? shellOpen = null)
    {
        PathPolicy.Validate(target);
        string location = Normalize(target.Kind, target.Path);
        if (OfficeLocation.IsWebTarget(target)) location = OfficeLocation.LaunchUri(target);
        context.Check();
        bool alreadyStarted = context.ExternalActionStarted;
        context.ExternalActionStarted = true;
        long result = (shellOpen ?? OpenWithShell)(location);
        if (result <= 32)
        {
            context.ExternalActionStarted = alreadyStarted;
            throw new BookmarkException(ResultCode.TargetUnavailable);
        }
    }

    internal static bool VerifyPosition(CapturedTarget target, RequestContext context, Func<bool> matches,
        Func<DateTimeOffset>? clock = null, Action? wait = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        wait ??= () => { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125); };
        var until = clock().AddSeconds(2);
        bool web = OfficeLocation.IsWebTarget(target);
        while (true)
        {
            context.Check();
            try { if (matches()) return true; }
            catch (BookmarkException error) when (web && error.Code == ResultCode.ContextChanged) { }
            if (!web || clock() >= until || context.Request.DeadlineUtc <= clock().AddMilliseconds(750)) return false;
            // Office can return from Select/Goto before ActiveWindow/Selection reflects the new view.
            // Poll metadata only; never issue a second navigation to compensate for a delayed refresh.
            wait();
        }
    }

    private static long OpenWithShell(string location) => Native.ShellExecute(0, "open", location, null, null, 1).ToInt64();
}

internal enum OfficeDocumentMatch { Different, Exact, Uncertain }

/// <summary>URL identity never uses filesystem IDs or guesses a matching local cache.</summary>
internal sealed class OfficeDocumentMatcher
{
    private readonly TargetKind _kind;
    private readonly string _target;
    private readonly bool _web;
    private readonly Func<string, string?> _fileIdentity;
    private readonly string? _targetIdentity;

    internal OfficeDocumentMatcher(TargetKind kind, string path, Func<string, string?>? fileIdentity = null)
    {
        _kind = kind;
        _target = OfficeDocumentAccess.Normalize(kind, path);
        _web = OfficeLocation.IsWeb(_target);
        _fileIdentity = fileIdentity ?? FileIdentity.TryRead;
        if (!_web) _targetIdentity = _fileIdentity(_target);
    }

    internal OfficeDocumentMatch Match(string fullName)
    {
        if (OfficeLocation.IsWeb(fullName))
        {
            if (!_web) return OfficeDocumentMatch.Different;
            try
            {
                return string.Equals(OfficeLocation.Normalize(_kind, fullName), _target, StringComparison.Ordinal)
                    ? OfficeDocumentMatch.Exact : OfficeDocumentMatch.Different;
            }
            catch (BookmarkException) { return OfficeDocumentMatch.Uncertain; }
        }
        if (_web || Uri.TryCreate(fullName, UriKind.Absolute, out var uri) && !uri.IsFile) return OfficeDocumentMatch.Different;
        string path = PathPolicy.Normalize(fullName);
        if (string.Equals(path, _target, StringComparison.Ordinal)) return OfficeDocumentMatch.Exact;
        string? identity = _fileIdentity(path);
        if (_targetIdentity is null || identity is null || identity == _targetIdentity ||
            string.Equals(path, _target, StringComparison.OrdinalIgnoreCase)) return OfficeDocumentMatch.Uncertain;
        return OfficeDocumentMatch.Different;
    }
}
