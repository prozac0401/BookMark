using System.Runtime.InteropServices;
using System.Text;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>DDEML owns wire buffers and late replies. Run only in the isolated worker:
/// connect/disconnect have no native timeout; the worker supervisor enforces the outer deadline.</summary>
internal sealed class PdfDdeClient : IDisposable
{
    private uint instance;
    private nint list, conversation;
    private readonly DdeCallback callback = (_, _, _, _, _, _, _, _) => 0;
    private readonly RequestContext context;
    private readonly nint expectedHwnd;
    internal PdfDdeClient(nint hwnd, RequestContext context, string serviceName = "SUMATRA")
    {
        this.context = context; expectedHwnd = hwnd;
        context.Check();
        if (hwnd == 0 || !Native.IsWindow(hwnd)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        if (DdeInitializeW(ref instance, callback, 0x10, 0) != 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
        nint service = 0, topic = 0;
        try
        {
            service = StringHandle(serviceName); topic = StringHandle("control");
            list = DdeConnectList(instance, service, topic, 0, 0);
            if (list == 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
            for (var next = DdeQueryNextServer(list, 0); next != 0; next = DdeQueryNextServer(list, next))
            {
                context.Check();
                var info = new ConvInfo { Size = (uint)Marshal.SizeOf<ConvInfo>() };
                if (DdeQueryConvInfo(next, uint.MaxValue, ref info) == 0) throw new BookmarkException(ResultCode.EnumerationIncomplete);
                if (info.PartnerHwnd != hwnd) continue;
                if (conversation != 0) throw new BookmarkException(ResultCode.AmbiguousTarget);
                conversation = next;
            }
            if (conversation == 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
            VerifyPartner(); context.Check();
        }
        catch { Dispose(); throw; }
        finally
        {
            if (instance != 0 && service != 0) DdeFreeStringHandle(instance, service);
            if (instance != 0 && topic != 0) DdeFreeStringHandle(instance, topic);
        }
    }
    internal string Request(string command)
    {
        VerifyPartner(); var item = StringHandle(command);
        nint data = 0;
        try
        {
            data = DdeClientTransaction(null, 0, conversation, item, 13, 0x20B0, Timeout(), out _);
            context.Check(); VerifyPartner();
            if (data == 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
            var size = DdeGetData(data, null, 0, 0);
            if (size < 2 || size > 131072 || size % 2 != 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
            var bytes = new byte[size];
            if (DdeGetData(data, bytes, size, 0) != size || bytes[^1] != 0 || bytes[^2] != 0)
                throw new BookmarkException(ResultCode.UnsupportedTarget);
            var value = new UnicodeEncoding(false, false, true).GetString(bytes, 0, bytes.Length - 2);
            if (value.Contains('\0')) throw new BookmarkException(ResultCode.UnsupportedTarget);
            return value;
        }
        finally
        {
            if (data != 0) DdeFreeDataHandle(data);
            DdeFreeStringHandle(instance, item);
        }
    }
    internal void Execute(string command)
    {
        VerifyPartner();
        var bytes = Encoding.Unicode.GetBytes(command + "\0");
        // A nonzero execute return is an acknowledgement, not an HDDEDATA to free.
        var result = DdeClientTransaction(bytes, (uint)bytes.Length, conversation, 0, 0, 0x4050, Timeout(), out _);
        context.Check(); VerifyPartner();
        if (result == 0) throw new BookmarkException(ResultCode.ResumeOutcomeUnknown);
    }
    private uint Timeout()
    {
        context.Check();
        return (uint)Math.Clamp(Math.Ceiling((context.Request.DeadlineUtc - DateTimeOffset.UtcNow).TotalMilliseconds), 1, 1500);
    }
    private nint StringHandle(string text)
    {
        if (text.Length is 0 or > 255 || text.Contains('\0')) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var handle = DdeCreateStringHandleW(instance, text, 1200);
        return handle != 0 ? handle : throw new BookmarkException(ResultCode.UnsupportedTarget);
    }
    private void VerifyPartner()
    {
        var info = new ConvInfo { Size = (uint)Marshal.SizeOf<ConvInfo>() };
        if (!Native.IsWindow(expectedHwnd) || conversation == 0 || DdeQueryConvInfo(conversation, uint.MaxValue, ref info) == 0 || info.PartnerHwnd != expectedHwnd)
            throw new BookmarkException(ResultCode.ContextChanged);
    }
    public void Dispose()
    {
        if (list != 0) DdeDisconnectList(list);
        if (instance != 0) DdeUninitialize(instance);
        list = conversation = 0; instance = 0; GC.KeepAlive(callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint DdeCallback(uint type, uint format, nint conversation, nint string1, nint string2, nint data, nuint data1, nuint data2);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityQuality { public uint Length, ImpersonationLevel; public byte Tracking, EffectiveOnly; }
    [StructLayout(LayoutKind.Sequential)] private struct ConvContext { public uint Size, Flags, Country; public int CodePage; public uint Language, Security; public SecurityQuality Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct ConvInfo
    {
        public uint Size;
        public nuint User;
        public nint PartnerConversation, ServicePartner, ServiceRequested, Topic, Item;
        public uint Format, Type, Status, State, LastError;
        public nint List;
        public ConvContext Context;
        public nint Hwnd, PartnerHwnd;
    }
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint DdeInitializeW(ref uint instance, DdeCallback callback, uint command, uint reserved);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint DdeCreateStringHandleW(uint instance, string value, int codePage);
    [DllImport("user32.dll")] private static extern bool DdeFreeStringHandle(uint instance, nint text);
    [DllImport("user32.dll")] private static extern nint DdeConnectList(uint instance, nint service, nint topic, nint list, nint context);
    [DllImport("user32.dll")] private static extern nint DdeQueryNextServer(nint list, nint previous);
    [DllImport("user32.dll")] private static extern uint DdeQueryConvInfo(nint conversation, uint transaction, ref ConvInfo info);
    [DllImport("user32.dll")] private static extern bool DdeDisconnectList(nint list);
    [DllImport("user32.dll")] private static extern bool DdeUninitialize(uint instance);
    [DllImport("user32.dll")] private static extern nint DdeClientTransaction(byte[]? data, uint length, nint conversation, nint item, uint format, uint type, uint timeout, out uint result);
    [DllImport("user32.dll")] private static extern uint DdeGetData(nint data, [Out] byte[]? destination, uint count, uint offset);
    [DllImport("user32.dll")] private static extern bool DdeFreeDataHandle(nint data);
}
