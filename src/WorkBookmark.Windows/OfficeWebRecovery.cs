using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>One root visit and at most one retry; an incomplete inventory never proves absence.</summary>
internal sealed class OfficeWebRecovery
{
    private readonly CapturedTarget target;
    private readonly RequestContext context;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<Uri, Task> warmUp;
    private readonly DateTimeOffset startedAt;
    private DateTimeOffset? openedAt;
    private Task? warmUpTask;
    private bool retried, actionsCancelled, observed;

    internal OfficeWebRecovery(CapturedTarget target, RequestContext context, Func<Uri, Task>? warmUp = null,
        Func<DateTimeOffset>? now = null)
    {
        this.target = target; this.context = context;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        startedAt = this.now();
        this.warmUp = warmUp ?? (origin => Task.Run(() => OfficeOriginWarmUp.Visit(origin)));
    }

    internal bool IsWeb => OfficeLocation.IsWebTarget(target);
    internal bool NearDeadline => IsWeb && context.Request.DeadlineUtc <= now().AddMilliseconds(750);
    internal ResultCode IncompleteResult => context.PositionVerified ? ResultCode.PositionRestoredFocusPending :
        observed ? ResultCode.OfficeDocumentOpened : ResultCode.OfficeResumePending;
    // Each pass inventories native windows, processes and several cross-process COM collections.
    // Authentication can take tens of seconds, so avoid continuously hammering Office's UI thread.
    internal int ObservationIntervalMilliseconds => now() - startedAt < TimeSpan.FromSeconds(2) ? 250 : 500;
    internal void WaitForObservation()
    {
        System.Windows.Forms.Application.DoEvents();
        Thread.Sleep(ObservationIntervalMilliseconds);
    }
    internal void Opened() => openedAt ??= now();
    internal void Observed(long hwnd)
    {
        if (hwnd == 0) return;
        observed = true;
        context.DocumentObserved = true;
        context.TargetHwnd = hwnd;
    }

    internal void Recover(bool completelyAbsent, Action checkInteraction, Action retry)
    {
        if (!IsWeb || openedAt is null || observed || actionsCancelled) return;
        var current = now();
        if (warmUpTask is null && current - openedAt.Value >= TimeSpan.FromSeconds(4) &&
            context.Request.DeadlineUtc > current.AddSeconds(8))
        {
            // No document URL, query token or caller-supplied header goes to this request.
            // The Windows Internet security-zone policy decides whether automatic logon is allowed.
            try { warmUpTask = warmUp(OfficeOriginWarmUp.Origin(target.Path)); }
            catch (Exception) { warmUpTask = Task.CompletedTask; }
        }
        if (warmUpTask is not { IsCompleted: true } || retried || !completelyAbsent ||
            context.Request.DeadlineUtc <= current.AddSeconds(5)) return;
        // Observe task failures without logging URLs, response bodies or authentication data.
        _ = warmUpTask.Exception;
        try { checkInteraction(); }
        catch (BookmarkException error) when (error.Code == ResultCode.Cancelled)
        {
            actionsCancelled = true; // Continue observing after login/input; never steal focus or reopen.
            return;
        }
        retried = true;
        context.Check();
        retry();
    }

    internal bool ShouldObserveAfter(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } wrapper) exception = wrapper.InnerException!;
        return IsWeb && !NearDeadline && exception is COMException com &&
            com.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A);
    }
}

/// <summary>
/// A background, header-only root visit using the user's Windows Internet settings. This can
/// initialize integrated-authentication/cookie state but cannot perform JavaScript, MFA or share
/// a Chromium-only profile. It never overrides zone policy, TLS validation or redirect boundaries.
/// </summary>
internal static class OfficeOriginWarmUp
{
    internal static Uri Origin(string documentUrl)
    {
        var document = new Uri(OfficeLocation.NormalizeUrl(documentUrl));
        return new Uri(document.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    internal static void Visit(Uri origin)
    {
        // Revalidate even the injected boundary: only a normalized HTTP(S) origin is admissible.
        if (Origin(origin.AbsoluteUri) != origin) throw new BookmarkException(ResultCode.UnsupportedTarget);
        using var session = InternetOpen("WorkBookmark", 0, null, null, 0); // INTERNET_OPEN_TYPE_PRECONFIG
        if (session.IsInvalid) return;
        uint timeout = 4000;
        // These options are inherited by requests; the worker's absolute deadline is a final bound.
        if (!InternetSetOption(session, 2, ref timeout, sizeof(uint)) || // CONNECT_TIMEOUT
            !InternetSetOption(session, 5, ref timeout, sizeof(uint)) || // SEND_TIMEOUT
            !InternetSetOption(session, 6, ref timeout, sizeof(uint))) return; // RECEIVE_TIMEOUT
        uint flags = 0x80000000u | 0x04000000u | 0x00400000u | 0x00200000u | 0x00000200u;
        // RELOAD | NO_CACHE_WRITE | KEEP_CONNECTION | NO_AUTO_REDIRECT | NO_UI.
        // No explicit credentials, no security-option overrides, no NO_COOKIES: Windows owns its jar.
        if (!CanAutomaticallyAuthenticate(origin)) flags |= 0x00040000u; // NO_AUTH; fail closed on unknown policy.
        using var request = InternetOpenUrl(session, origin.AbsoluteUri, null, 0, flags, 0);
        // Opening processes the response headers/cookies. Never read, persist or log the body.
    }

    internal static bool PolicyAllowsAutomaticLogon(uint policy, uint zone) =>
        policy == 0 || policy == 0x00020000 && zone == 1; // SILENT_LOGON_OK / CONDITIONAL_PROMPT + INTRANET

    private static bool CanAutomaticallyAuthenticate(Uri origin)
    {
        IInternetSecurityManager? manager = null;
        try
        {
            if (CoInternetCreateSecurityManager(0, out manager, 0) != 0 || manager is null) return false;
            if (manager.MapUrlToZone(origin.AbsoluteUri, out uint zone, 0) != 0) return false;
            byte[] policy = [255, 255, 255, 255];
            if (manager.ProcessUrlAction(origin.AbsoluteUri, 0x1A00, policy, 4, 0, 0, 1, 0) != 0) return false;
            return PolicyAllowsAutomaticLogon(BitConverter.ToUInt32(policy), zone);
        }
        catch (COMException) { return false; }
        finally { if (manager is not null) Marshal.FinalReleaseComObject(manager); }
    }

    private sealed class InternetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private InternetHandle() : base(true) { }
        protected override bool ReleaseHandle() => InternetCloseHandle(handle);
    }
    [DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern InternetHandle InternetOpen(string agent, uint accessType, string? proxy, string? bypass, uint flags);
    [DllImport("wininet.dll", EntryPoint = "InternetOpenUrlW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern InternetHandle InternetOpenUrl(InternetHandle session, string url, string? headers, uint headerLength, uint flags, nuint context);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(InternetHandle session, uint option, ref uint value, uint length);
    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetCloseHandle(nint handle);

    [DllImport("urlmon.dll")]
    private static extern int CoInternetCreateSecurityManager(nint serviceProvider, out IInternetSecurityManager manager, uint reserved);
    [ComImport, Guid("79eac9ee-baf9-11ce-8c82-00aa004ba90b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInternetSecurityManager
    {
        [PreserveSig] int SetSecuritySite(nint site);
        [PreserveSig] int GetSecuritySite(out nint site);
        [PreserveSig] int MapUrlToZone([MarshalAs(UnmanagedType.LPWStr)] string url, out uint zone, uint flags);
        [PreserveSig] int GetSecurityId([MarshalAs(UnmanagedType.LPWStr)] string url, nint securityId, ref uint length, nuint reserved);
        [PreserveSig] int ProcessUrlAction([MarshalAs(UnmanagedType.LPWStr)] string url, uint action,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] policy, uint policyLength,
            nint context, uint contextLength, uint flags, uint reserved);
    }
}
