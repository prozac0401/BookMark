using System.Diagnostics;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Opens a file explicitly in Notepad, using packaged application activation when registered.</summary>
internal static class NotepadLauncher
{
    private const string AppUserModelId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    internal static void Open(string path)
    {
        string normalized = PathPolicy.Normalize(path);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(normalized))
            throw new BookmarkException(ResultCode.TargetUnavailable);
        string executable = NotepadExecutableResolver.Resolve();
        string classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (string.Equals(executable, classic, StringComparison.OrdinalIgnoreCase))
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add(normalized);
            using var process = Process.Start(start) ?? throw new BookmarkException(ResultCode.TargetUnavailable);
            return;
        }

        // The worker is short-lived. CLSCTX_LOCAL_SERVER keeps activation arguments in the broker,
        // rather than tying their lifetime to this worker. No default file handler is consulted.
        Guid clsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        Guid activationId = typeof(IApplicationActivationManager).GUID;
        IApplicationActivationManager? manager = null;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 4, ref activationId, out manager));
            _ = CoAllowSetForegroundWindow(manager, 0);
            // The registered Notepad entry point is Windows.FullTrustApplication. Its launch
            // contract accepts a command line; PathPolicy excludes embedded quotes and this is
            // one quoted file argument, passed to the fixed app identity without a shell.
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(AppUserModelId, "\"" + normalized + "\"", 0, out _));
        }
        finally
        {
            if (manager is not null) Marshal.FinalReleaseComObject(manager);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object instance, nint reserved);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, out uint processId);
    }
}
