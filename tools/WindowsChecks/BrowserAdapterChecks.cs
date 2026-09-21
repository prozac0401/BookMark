using WorkBookmark.Core;
using WorkBookmark.Windows;

// No browser sessions, browsing data, keys, clipboard, network or user windows are touched.
internal static class BrowserAdapterChecks
{
    internal static int Run()
    {
        int passed = 0;
        void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        bool Refused(Action action, ResultCode expected)
        {
            try { action(); return false; }
            catch (BookmarkException error) when (error.Code == expected) { return true; }
        }
        var snapshot = new TargetSnapshot(101, 202, 303, 404);
        RequestContext Context(double seconds = 5) => new(new(1, Guid.NewGuid(), Operation.Capture,
            DateTimeOffset.UtcNow.AddSeconds(seconds), snapshot));
        var observed = new BrowserObservation("window-1", "address-1", "example.invalid/Page?a=One%2FTwo#position", false,
            "document-1", "https://example.invalid/Page?a=One%2FTwo#position", "Synthetic page");
        var source = new SequenceSource(observed, observed);
        int verified = 0;
        var result = BrowserAdapter.Capture(snapshot, Context(), source, _ => verified++);
        Assert(result.Kind == TargetKind.WebPage && result.Path == observed.DocumentUrl && result.PageTitle == observed.Title &&
            verified == 3 && source.Reads == 2, "plugin-free capture preserves the committed full URL after two stable observations");
        Assert(BrowserAdapter.AddressMatchesDocument("example.invalid/path", "http://example.invalid/path"),
            "an elided HTTP address never becomes invented HTTPS");
        Assert(BrowserAdapter.AddressMatchesDocument("example.invalid/path", "https://www.example.invalid/path"),
            "omitted www prefix is taken only from the document's committed URL");
        Assert(BrowserAdapter.AddressMatchesDocument("https://example.invalid/한글?q=가", "https://example.invalid/%ED%95%9C%EA%B8%80?q=%EA%B0%80"),
            "Unicode display is compared as a URI without dropping path or query");
        Assert(BrowserAdapter.AddressMatchesDocument("example.invalid", "https://example.invalid/"),
            "root slash display elision is accepted");
        var badAddresses = new[] { "https://example.invalid/Other?a=One%2FTwo#position", "example.invalid/Page?a=One%2FTwo#other",
            "example.invalid/page?a=One%2FTwo#position", "example.invalid/Page?a=One/Two#position", "other.invalid/Page?a=One%2FTwo#position",
            "http://example.invalid/Page?a=One%2FTwo#position", "user:pass@example.invalid/Page?a=One%2FTwo#position",
            "example.invalid/…", "example.invalid\\Page", "example.invalid/\nPage" };
        for (int i = 0; i < badAddresses.Length; i++)
            Assert(!BrowserAdapter.AddressMatchesDocument(badAddresses[i], observed.DocumentUrl),
                "different, elided or unsafe address refused: fixture " + (i + 1));
        Assert(Refused(() => BrowserAdapter.ValidateObservation(observed with { AddressFocused = true }), ResultCode.BrowserAddressUnavailable),
            "editing the omnibox cannot capture a typed but unvisited URL");
        foreach (var url in new[] { "edge://settings", "chrome://newtab", "file:///C:/private.txt", "javascript:alert(1)", "https://user:secret@example.invalid/" })
            Assert(Refused(() => BrowserAdapter.ValidateObservation(observed with { DocumentUrl = url }), ResultCode.BrowserAddressUnavailable),
                "browser-internal, local and credential-bearing URLs require no capture");
        Assert(Refused(() => BrowserAdapter.ValidateObservation(observed with { AddressIdentity = "" }), ResultCode.BrowserAddressUnavailable),
            "missing accessibility identity cannot be saved");
        foreach (var changed in new[] { observed with { DocumentUrl = observed.DocumentUrl + "2" }, observed with { DocumentIdentity = "document-2" },
            observed with { AddressValue = "example.invalid/Next" }, observed with { WindowIdentity = "window-2" }, observed with { Title = "Navigating" } })
            Assert(Refused(() => BrowserAdapter.Capture(snapshot, Context(), new SequenceSource(observed, changed), _ => { }), ResultCode.ContextChanged),
                "tab, navigation, title or element changes fail the stable observation check");
        Assert(Refused(() => BrowserAdapter.Capture(snapshot, Context(), new SequenceSource(observed, observed),
            _ => throw new BookmarkException(ResultCode.ContextChanged)), ResultCode.ContextChanged),
            "foreground context is checked before browser metadata is read");
        var expired = new SequenceSource(observed, observed);
        Assert(Refused(() => BrowserAdapter.Capture(snapshot, Context(-1), expired, _ => { }), ResultCode.CaptureTimedOut) && expired.Reads == 0,
            "expired captures do not read browser metadata");
        Assert(BrowserAdapter.NormalizeTitle(new string('a', 255) + "😀\n") == new string('a', 255) &&
            BrowserAdapter.NormalizeTitle("Title\r\nMore") == "Title  More", "title cap preserves surrogate pairs and strips controls");
        Console.WriteLine($"RESULT: {passed} browser adapter checks passed. Native Edge/Chrome acceptance remains manual.");
        return 0;
    }

    private sealed class SequenceSource(params BrowserObservation[] observations) : IBrowserCaptureSource
    {
        internal int Reads { get; private set; }
        public BrowserObservation Read(TargetSnapshot snapshot, RequestContext context) => observations[Reads++];
    }
}
