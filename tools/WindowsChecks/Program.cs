using System.Runtime.InteropServices;
using System.Text.Json;
using WorkBookmark.Core;
using WorkBookmark.Windows;
internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("WindowsChecks commands:\n  session APP_EXE PLAN_JSON OUTPUT_JSON\n  series APP_EXE EXCEL_EXE FIXTURE_ROOT OUTPUT_JSON [COUNT] [A_CELL] [B_CELL]\n  session-selftest | evidence-selftest | adapter-checks | office-url-checks\n  HWND [resume]\nUse synthetic fixtures only; see tools/WindowsChecks/README.md.");
            return args.Length == 0 ? 2 : 0;
        }
        if (args.Length > 0 && args[0] == "series") return ExcelSeries.Run(args);
        if (args.Length > 0 && args[0] == "session") return CaptureSession.Run(args);
        if (args.Length > 0 && args[0] == "session-selftest") return CaptureSession.RunSelfTests();
        if (args.Length > 0 && args[0] == "evidence-selftest") return ExcelSeries.RunEvidenceSelfTests();
        if (args.Length > 0 && args[0] == "adapter-checks") return AdapterRegressionChecks.Run(args);
        if (args[0] == "word-native-checks") return WordNativeChecks.Run(args);
        if (args[0] == "word-checks") return WordAdapterChecks.Run(args);
        if (args[0] == "office-url-checks") return OfficeUrlAdapterChecks.Run();
        if (args[0] == "powerpoint-checks") return PowerPointAdapterChecks.Run(args);
        if (args[0] == "pdf-checks") return PdfAdapterChecks.Run(args);
        if (args[0] == "notepad-checks") return NotepadAdapterChecks.Run(args);
        if (!long.TryParse(args[0], out long rawHwnd) || rawHwnd <= 0) { Console.Error.WriteLine("Unknown command. Run WindowsChecks --help."); return 2; }
        var hwnd = (nint)rawHwnd;
        Console.WriteLine("focus=" + Native.SetForegroundWindow(hwnd)); Thread.Sleep(200);
        var before = ForegroundSnapshot.Capture(); Console.WriteLine("before=" + JsonSerializer.Serialize(before));
        using var scope = new ComScope();
        foreach (var child in Native.Children(hwnd, "EXCEL7", true))
        {
            Console.WriteLine("native=" + child);
            var iid = new Guid("00020400-0000-0000-C000-000000000046");
            Console.WriteLine("accessible=" + Native.AccessibleObjectFromWindow(child, unchecked((uint)-16), ref iid, out var window)); scope.Keep(window);
            var application = scope.Get(window,"Application");
            var activeWindow = scope.Get(application,"ActiveWindow");
            Console.WriteLine("window hwnd="+scope.Get(window,"Hwnd")+" active="+scope.Get(activeWindow,"Hwnd")+" same="+ComScope.Same(window,activeWindow));
            var sheet=scope.Get(window,"ActiveSheet"); var cell=scope.Get(window,"ActiveCell"); var book=scope.Get(sheet,"Parent");
            Console.WriteLine("sheet type="+scope.Get(sheet,"Type")+" cell-parent="+ComScope.Same(sheet,scope.Get(cell,"Parent"))+" app-parent="+ComScope.Same(application,scope.Get(book,"Application"))+" active-book="+ComScope.Same(book,scope.Get(application,"ActiveWorkbook")));
            var windows=scope.Get(book,"Windows"); var count=scope.Number(windows,"Count");
            for(var i=1;i<=count;i++){var other=scope.Get(windows,"Item",i);Console.WriteLine("book-window="+scope.Get(other,"Hwnd")+" same="+ComScope.Same(window,other));}
        }
        var after = ForegroundSnapshot.Capture(); Console.WriteLine("after="+JsonSerializer.Serialize(after));
        var result=WindowsAdapter.Execute(new(1,Guid.NewGuid(),Operation.Capture,DateTimeOffset.UtcNow.AddSeconds(5),after)); Console.WriteLine("capture="+JsonSerializer.Serialize(result));
        if (args.Length > 1 && args[1] == "resume" && result.Target is not null)
        {
            ResumeGuard.ProbeTrace = message => Console.WriteLine("guard=" + message);
            ExcelAdapter.ProbeTrace = message => Console.WriteLine("enumeration=" + message);
            var resumed=WindowsAdapter.Execute(new(1,Guid.NewGuid(),Operation.Resume,DateTimeOffset.UtcNow.AddSeconds(15),after,result.Target with { CellAddress="$F$42" }));
            Console.WriteLine("resume=" + JsonSerializer.Serialize(resumed));
            Console.WriteLine("resume-after=" + JsonSerializer.Serialize(ForegroundSnapshot.Capture()));
        }
        return 0;
    }
}
