using System.Runtime.InteropServices;
using System.Windows.Automation;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Read-only metadata from browser chrome and its top-level document; never enters page content.</summary>
internal static class BrowserAdapter
{
    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context,
        IBrowserCaptureSource? source = null, Action<TargetSnapshot>? verify = null)
    {
        source ??= UiaBrowserCaptureSource.Instance;
        verify ??= ForegroundSnapshot.Verify;
        context.Check(); verify(snapshot);
        var first = source.Read(snapshot, context);
        var target = ValidateObservation(first);
        context.Check(); verify(snapshot);
        var second = source.Read(snapshot, context);
        // A tab switch, navigation, URL edit or stale accessibility element is never saved as the old page.
        if (first != second) throw new BookmarkException(ResultCode.ContextChanged);
        ValidateObservation(second);
        context.Check(); verify(snapshot);
        return target;
    }

    internal static CapturedTarget ValidateObservation(BrowserObservation observation)
    {
        if (observation.AddressFocused || string.IsNullOrEmpty(observation.WindowIdentity) ||
            string.IsNullOrEmpty(observation.AddressIdentity) || string.IsNullOrEmpty(observation.DocumentIdentity))
            throw Unavailable();
        CapturedTarget target;
        try
        {
            target = BrowserProtocol.ValidateTarget(new(TargetKind.WebPage, observation.DocumentUrl,
                PageTitle: NormalizeTitle(observation.Title)));
        }
        catch (BookmarkException) { throw Unavailable(); }
        if (!AddressMatchesDocument(observation.AddressValue, target.Path)) throw Unavailable();
        return target;
    }

    internal static bool AddressMatchesDocument(string displayed, string fullUrl)
    {
        if (string.IsNullOrWhiteSpace(displayed) || displayed.Length > BrowserProtocol.MaximumUrlLength ||
            displayed.Any(char.IsControl) || displayed.Contains('\\') || displayed.Contains('\u2026')) return false;
        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var document) ||
            document.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(document.UserInfo)) return false;
        bool Matches(string candidate) => Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
            string.IsNullOrEmpty(parsed.UserInfo) && string.Equals(parsed.AbsoluteUri, document.AbsoluteUri, StringComparison.Ordinal);
        if (displayed.Contains("://", StringComparison.Ordinal)) return Matches(displayed);
        // Chromium can hide the scheme and the trivial www prefix. The committed document supplies
        // those exact values; never invent HTTPS or change the URL that is ultimately stored.
        var prefix = document.Scheme + "://";
        return Matches(prefix + displayed) ||
            (document.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && Matches(prefix + "www." + displayed));
    }

    internal static string NormalizeTitle(string title)
    {
        var value = new string(title.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
        if (value.Length > 256) value = value[..256];
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        return value;
    }

    internal static BookmarkException Unavailable() => new(ResultCode.BrowserAddressUnavailable);
}

internal sealed record BrowserObservation(string WindowIdentity, string AddressIdentity, string AddressValue,
    bool AddressFocused, string DocumentIdentity, string DocumentUrl, string Title);

internal interface IBrowserCaptureSource
{
    BrowserObservation Read(TargetSnapshot snapshot, RequestContext context);
}

internal sealed class UiaBrowserCaptureSource : IBrowserCaptureSource
{
    internal static readonly UiaBrowserCaptureSource Instance = new();
    private const int MaximumChromeElements = 512;
    private const int MaximumDepth = 16;

    public BrowserObservation Read(TargetSnapshot snapshot, RequestContext context)
    {
        try
        {
            context.Check();
            var root = AutomationElement.FromHandle((nint)snapshot.Hwnd) ?? throw BrowserAdapter.Unavailable();
            if (root.Current.ProcessId != checked((int)snapshot.ProcessId)) throw new BookmarkException(ResultCode.ContextChanged);
            var addresses = new List<AutomationElement>();
            var documents = new List<AutomationElement>();
            var pending = new Queue<(AutomationElement Element, int Depth, bool Toolbar)>();
            pending.Enqueue((root, 0, false));
            var walker = TreeWalker.ControlViewWalker;
            var visited = 0;
            while (pending.TryDequeue(out var item))
            {
                context.Check();
                if (++visited > MaximumChromeElements) throw BrowserAdapter.Unavailable();
                var info = item.Element.Current;
                if (info.IsOffscreen) continue;
                if (info.ControlType == ControlType.Document)
                {
                    // Deliberately do not enumerate document children, web inputs, frames or page text.
                    documents.Add(item.Element);
                    continue;
                }
                var toolbar = item.Toolbar || info.ControlType == ControlType.ToolBar;
                if (toolbar && info.ControlType == ControlType.Edit && IsAddressBar(info)) addresses.Add(item.Element);
                if (item.Depth >= MaximumDepth) throw BrowserAdapter.Unavailable();
                for (var child = walker.GetFirstChild(item.Element); child is not null; child = walker.GetNextSibling(child))
                {
                    context.Check();
                    if (visited + pending.Count >= MaximumChromeElements) throw BrowserAdapter.Unavailable();
                    pending.Enqueue((child, item.Depth + 1, toolbar));
                }
            }
            // Side-by-side tabs and browser sidebars can expose several documents. An ambiguous
            // tree is left to explicit URL entry, rather than selecting whichever document comes first.
            if (addresses.Count != 1 || documents.Count != 1) throw BrowserAdapter.Unavailable();
            var address = addresses[0]; var document = documents[0];
            return new(Identity(root), Identity(address), Value(address), address.Current.HasKeyboardFocus,
                Identity(document), Value(document), document.Current.Name);
        }
        catch (BookmarkException) { throw; }
        catch (Exception error) when (error is ElementNotAvailableException or ElementNotEnabledException or
            InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            context.Check();
            throw BrowserAdapter.Unavailable();
        }
    }

    private static string Value(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern value)
            throw BrowserAdapter.Unavailable();
        return value.Current.Value;
    }

    private static string Identity(AutomationElement element) => string.Join(".", element.GetRuntimeId());

    private static bool IsAddressBar(AutomationElement.AutomationElementInformation info) =>
        info.AutomationId is "addressEditBox" or "omnibox" ||
        info.AcceleratorKey.Replace(" ", "", StringComparison.Ordinal).Equals("Ctrl+L", StringComparison.OrdinalIgnoreCase) ||
        info.Name is "Address and search bar" or "Search or enter web address" or "주소 및 검색창" or
            "주소 및 검색 창" or "주소 및 검색 표시줄" or "검색 또는 웹 주소 입력";
}
