using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WorkBookmark.Windows;

// Used only during resume observation to detect potential aliases. Never changes permanent bookmark keys.
internal static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInformation information);
    internal static string? TryRead(string path)
    {
        using var handle = CreateFile(path, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) return null;
        return $"{information.Volume:X8}:{information.IndexHigh:X8}:{information.IndexLow:X8}";
    }
}
