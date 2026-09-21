using System.Runtime.InteropServices;
using System.Text;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

internal static class Native
{
    internal delegate bool EnumWindowProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder value, int count);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint id, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int informationClass, out uint elevation, uint length, out uint returnedLength);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromWindow(nint hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern nint ShellExecute(nint hwnd, string operation, string file, string? parameters, string? directory, int show);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributes, out uint actual);
    [DllImport("shell32.dll")] internal static extern int SHOpenFolderAndSelectItems(nint folderPidl, uint count, nint[] children, uint flags);
    [DllImport("shell32.dll")] internal static extern nint ILFindLastID(nint pidl);
    [DllImport("user32.dll")] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct LastInputInfo { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    internal static string Class(nint hwnd) { var value = new StringBuilder(256); GetClassName(hwnd, value, value.Capacity); return value.ToString(); }
    internal static List<nint> Children(nint parent, string className, bool visibleOnly)
    {
        var found = new List<nint>();
        EnumChildWindows(parent, (h, _) => { if (Class(h) == className && (!visibleOnly || IsWindowVisible(h))) found.Add(h); return true; }, 0);
        return found;
    }
    internal static List<nint> ExcelRoots()
    {
        var found = new List<nint>();
        EnumWindows((h, _) => { if (Class(h) == "XLMAIN") found.Add(h); return true; }, 0);
        return found;
    }
    // Worker-side capability boundary: unknown/elevated targets are unsupported, never retried with elevation.
    internal static bool IsUnelevatedProcess(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return false;
        nint token = 0;
        try
        {
            return OpenProcessToken(process, 0x0008, out token) &&
                GetTokenInformation(token, 20, out var elevation, sizeof(uint), out var returned) && returned == sizeof(uint) && elevation == 0;
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
    internal static long ProcessStamp(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return 0;
        try { return GetProcessTimes(process, out var stamp, out _, out _, out _) ? DateTime.FromFileTimeUtc(stamp).Ticks : 0; }
        finally { CloseHandle(process); }
    }
    internal static bool BelongsTo(nint hwnd, nint top) => hwnd == top || IsChild(top, hwnd) || GetAncestor(hwnd, 2) == top;
}

/// <summary>Only local Win32 calls: safe to take before showing any application UI.</summary>
public static class ForegroundSnapshot
{
    public static TargetSnapshot Capture()
    {
        var hwnd = Native.GetForegroundWindow();
        var tid = Native.GetWindowThreadProcessId(hwnd, out var pid);
        var gui = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        Native.GetGUIThreadInfo(tid, ref gui);
        var input = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
        Native.GetLastInputInfo(ref input);
        var cls = Native.Class(hwnd);
        var children = cls is "CabinetWClass" or "ExploreWClass" ? Native.Children(hwnd, "SHELLDLL_DefView", true) : cls == "XLMAIN" ? Native.Children(hwnd, "EXCEL7", true) :
            cls == "OpusApp" ? Native.Children(hwnd, "_WwG", true) : cls == "PPTFrameClass" ? Native.Children(hwnd, "paneClassDC", true) :
            cls == "Notepad" ? Native.Children(hwnd, "RichEditD2DPT", true).Concat(Native.Children(hwnd, "Edit", true)).ToList() : [];
        return new TargetSnapshot(hwnd.ToInt64(), pid, Native.ProcessStamp(pid), gui.Focus.ToInt64(), input.Tick,
            children.Count == 1 ? children[0].ToInt64() : 0);
    }
    internal static void Verify(TargetSnapshot snapshot)
    {
        var now = Capture();
        if (snapshot.Hwnd == 0 || now.Hwnd != snapshot.Hwnd || now.ProcessId != snapshot.ProcessId ||
            snapshot.ProcessStartTimeUtcTicks == 0 || now.ProcessStartTimeUtcTicks != snapshot.ProcessStartTimeUtcTicks ||
            now.ActiveViewHwnd != snapshot.ActiveViewHwnd || now.FocusHwnd != snapshot.FocusHwnd)
            throw new BookmarkException(ResultCode.ContextChanged);
    }
    /// <summary>Return from our file picker to the original live editor; the worker also checks its content and selection observation.</summary>
    public static bool TryReturnToNotepad(TargetSnapshot snapshot)
    {
        var hwnd = (nint)snapshot.Hwnd;
        if (!Native.IsWindow(hwnd) || Native.Class(hwnd) != "Notepad" || snapshot.ActiveViewHwnd == 0 ||
            !Native.IsWindowVisible((nint)snapshot.ActiveViewHwnd) || !Native.IsChild(hwnd, (nint)snapshot.ActiveViewHwnd)) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != snapshot.ProcessId || snapshot.ProcessStartTimeUtcTicks == 0 || Native.ProcessStamp(pid) != snapshot.ProcessStartTimeUtcTicks) return false;
        return Native.SetForegroundWindow(hwnd);
    }
}

/// <summary>Observes only that a fresh key/button press occurred, never input contents.</summary>
internal sealed class ResumeGuard : IDisposable
{
    internal static Action<string>? ProbeTrace { get; set; }
    private readonly nint original;
    private readonly Dictionary<nint, (uint Pid, long Stamp)> expected = [];
    private readonly (uint Pid, long Stamp) originalIdentity;
    private readonly Native.HookProc keyboardCallback, mouseCallback;
    private readonly ResumeInputSignal? inputSignal;
    private nint keyboard, mouse;
    private bool newInput;
    internal ResumeGuard(TargetSnapshot? snapshot, Guid? inputSignalRequest = null)
    {
        inputSignal = inputSignalRequest is { } requestId ? ResumeInputSignal.OpenExisting(requestId) : null;
        original = snapshot is null ? Native.GetForegroundWindow() : (nint)snapshot.Hwnd;
        originalIdentity = snapshot is null ? Identity(original) : (snapshot.ProcessId, snapshot.ProcessStartTimeUtcTicks);
        keyboardCallback = (code, message, data) => { if (code >= 0 && (message == 0x100 || message == 0x104)) newInput = true; return Native.CallNextHookEx(0, code, message, data); };
        mouseCallback = (code, message, data) => { if (code >= 0 && (message == 0x201 || message == 0x204 || message == 0x207 || message == 0x20B || message == 0x20A || message == 0x20E)) newInput = true; return Native.CallNextHookEx(0, code, message, data); };
        keyboard = Native.SetWindowsHookEx(13, keyboardCallback, Native.GetModuleHandle(null), 0);
        mouse = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
        if (keyboard == 0 || mouse == 0) { Dispose(); throw new BookmarkException(ResultCode.Cancelled); }
    }
    private static (uint Pid, long Stamp) Identity(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return (pid, Native.ProcessStamp(pid));
    }
    private static bool IsSameWindow(nint hwnd, (uint Pid, long Stamp) identity) =>
        identity.Pid != 0 && identity.Stamp != 0 && Native.IsWindow(hwnd) && Identity(hwnd) == identity;
    internal void Permit(nint hwnd) => expected[hwnd] = Identity(hwnd);
    // A pending Office launch may move foreground to its splash/login window before the
    // document HWND exists. A bounded reopen may proceed only while the user has not acted.
    internal void CheckForNewInput()
    {
        System.Windows.Forms.Application.DoEvents();
        if (newInput || inputSignal?.HasInput == true) throw new BookmarkException(ResultCode.Cancelled);
    }
    internal void Check()
    {
        System.Windows.Forms.Application.DoEvents();
        var foreground = Native.GetForegroundWindow();
        ProbeTrace?.Invoke("input=" + newInput + ":original=" + original + ":foreground=" + foreground + ":permitted=" + expected.ContainsKey(foreground));
        if (newInput || inputSignal?.HasInput == true || foreground == 0 ||
            (foreground == original ? !IsSameWindow(original, originalIdentity) : !expected.ContainsKey(foreground)) ||
            expected.Any(window => !IsSameWindow(window.Key, window.Value)))
            throw new BookmarkException(ResultCode.Cancelled);
    }
    public void Dispose()
    {
        if (keyboard != 0) Native.UnhookWindowsHookEx(keyboard);
        if (mouse != 0) Native.UnhookWindowsHookEx(mouse);
        keyboard = mouse = 0;
        inputSignal?.Dispose();
        GC.KeepAlive(keyboardCallback); GC.KeepAlive(mouseCallback);
    }
}
