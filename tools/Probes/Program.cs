using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using WorkBookmark.Core;
using WorkBookmark.Windows;
using WorkBookmark.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--worker") { ApplicationConfiguration.Initialize(); return WorkerHost.Run(); }
        if (args.Length == 0) { Console.WriteLine("Commands: inventory | capture APP_EXE [delaySeconds] | capture-window APP_EXE HWND | resume APP_EXE TARGET_JSON | fixtures DIRECTORY"); return 2; }
        if (args[0] == "fixtures") { FixtureGenerator.Generate(args[1]); return 0; }
        if (args[0] is "inventory" or "inventory-process")
        {
            var windows = new List<object>();
            EnumWindows((h, _) => { var cls = new StringBuilder(256); GetClassName(h, cls, cls.Capacity); GetWindowThreadProcessId(h, out uint pid);
                if ((args[0] == "inventory-process" && pid == uint.Parse(args[1])) || (args[0] == "inventory" && IsWindowVisible(h) && cls.ToString() is "XLMAIN" or "CabinetWClass")) { GetWindowRect(h, out var rect); var title=new StringBuilder(256); GetWindowText(h,title,title.Capacity); windows.Add(new { hwnd = h.ToInt64(), pid, className = cls.ToString(), visible=IsWindowVisible(h), foreground=GetForegroundWindow()==h, title=title.ToString(), left=rect.Left,top=rect.Top,right=rect.Right,bottom=rect.Bottom }); } return true; }, 0);
            Console.WriteLine(JsonSerializer.Serialize(windows)); return 0;
        }
        string app = Path.GetFullPath(args[1]);
        using var client = new WorkerClient(app);
        if (args[0] == "capture") Thread.Sleep(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 3));
        if (args[0] == "capture-window")
        {
            nint hwnd = (nint)long.Parse(args[2]);
            if (!IsWindow(hwnd) || !SetForegroundWindow(hwnd)) { Console.WriteLine("{\"error\":\"Foreground activation denied\"}"); return 3; }
            Thread.Sleep(200);
        }
        var snapshot = ForegroundSnapshot.Capture();
        var operation = args[0] == "resume" ? Operation.Resume : Operation.Capture;
        CapturedTarget? target = operation == Operation.Resume ? JsonSerializer.Deserialize<CapturedTarget>(File.ReadAllText(args[2])) : null;
        var request = new WorkerRequest(1, Guid.NewGuid(), operation, DateTimeOffset.UtcNow.AddSeconds(operation == Operation.Resume ? 15 : 3), snapshot, target);
        var response = client.RunAsync(request).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new { snapshot, response }, new JsonSerializerOptions { WriteIndented = true }));
        return response.Code is ResultCode.Captured or ResultCode.PositionRestored or ResultCode.PositionRestoredFocusPending or ResultCode.OpenRequested or ResultCode.RevealRequested ? 0 : 1;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint hwnd,StringBuilder text,int size);
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}
