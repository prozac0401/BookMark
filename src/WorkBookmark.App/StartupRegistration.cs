using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WorkBookmark.App;

/// <summary>User-owned startup shortcut. ShellLink COM runs only in a short-lived STA helper.</summary>
public static class StartupRegistration
{
    private static readonly object HelperGate = new();
    private static Process? _activeHelper;
    private static bool _stopping;
    private const string Marker = "WorkBookmark per-user startup shortcut v1";
    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WorkBookmark.lnk");
    public static bool IsEnabled => File.Exists(ShortcutPath) && IsOwned(File.ReadAllBytes(ShortcutPath));
    public static void SetEnabled(bool enabled)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("실행파일 위치를 확인하지 못했습니다.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--startup-worker");
        Process process;
        lock (HelperGate)
        {
            if (_stopping) throw new IOException("앱을 종료하는 중입니다.");
            process = Process.Start(start) ?? throw new IOException("시작프로그램 작업을 시작하지 못했습니다.");
            _activeHelper = process;
        }
        try
        {
            process.StandardInput.Write(enabled ? "1" : "0"); process.StandardInput.Close();
            if (!process.WaitForExit(5000)) throw new IOException("시작프로그램 변경 결과를 확인하지 못했습니다.");
            if (process.ExitCode != 0 || process.StandardOutput.ReadToEnd() != "OK") throw new IOException("시작프로그램을 변경하지 못했습니다. 기존 바로가기를 확인해 주세요.");
        }
        finally
        {
            lock (HelperGate)
            {
                if (ReferenceEquals(_activeHelper, process)) _activeHelper = null;
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }
    }
    public static void StopWorker()
    {
        lock (HelperGate)
        {
            _stopping = true;
            try { if (_activeHelper is { HasExited: false }) _activeHelper.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    // Entered before the app mutex, on Program.Main's STA thread. Only a single 0/1 byte is accepted.
    public static int RunWorker()
    {
        try
        {
            int command = Console.In.Read();
            if (command is not ('0' or '1') || Console.In.Read() != -1) return 2;
            SetEnabledInWorker(command == '1');
            Console.Out.Write("OK"); return 0;
        }
        catch { return 1; }
    }
    private static void SetEnabledInWorker(bool enabled)
    {
        string path = ShortcutPath;
        if (File.Exists(path) && !IsOwned(File.ReadAllBytes(path))) throw new IOException("같은 이름의 다른 시작프로그램 바로가기가 있습니다.");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        string executable = Environment.ProcessPath ?? throw new IOException("실행파일 위치를 확인하지 못했습니다.");
        if (executable.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(executable)) throw new IOException("로컬 폴더에 배포한 앱만 등록할 수 있습니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] shortcut = CreateShortcut(executable);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, shortcut); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool IsOwned(byte[] bytes) => bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Marker)) >= 0;
    internal static byte[] CreateShortcut(string executable)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "WorkBookmark-startup-" + Guid.NewGuid().ToString("N") + ".lnk");
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            var link = (IShellLinkW)shell;
            link.SetPath(executable); link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription(Marker); link.SetShowCmd(1);
            ((IPersistFile)shell).Save(temporary, true);
            return File.ReadAllBytes(temporary);
        }
        finally { Marshal.FinalReleaseComObject(shell); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder working, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string working);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
