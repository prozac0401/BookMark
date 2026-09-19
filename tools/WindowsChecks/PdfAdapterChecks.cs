using System.Runtime.InteropServices;
using System.Text;
using WorkBookmark.Core;
using WorkBookmark.Windows;

internal static class PdfAdapterChecks
{
    internal static int Run(string[] args)
    {
        var count = 0;
        void Assert(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            count++; Console.WriteLine("PASS: " + name);
        }
        bool Refused(Action action)
        {
            try { action(); return false; } catch (BookmarkException) { return true; }
        }
        const string path = @"C:\fixture\한글 (문서).pdf";
        string State(string file = path, string page = "4", string pages = "10", string version = "3.7.21999") =>
            $"path: {file}\npage: {page}\npageCount: {pages}\nsumver: {version}\nzoom: 125\nview: continuous\n";
        var actual = PdfAdapter.ParseState(State());
        var target = new CapturedTarget(TargetKind.PdfPage, path, PdfPage: 4);
        Assert(actual.Path == path && actual.Page == 4 && actual.PageCount == 10, "Unicode path and physical page decoded without title heuristics");
        Assert(PdfAdapter.Matches(actual, target), "matching exact path and page confirms position");
        Assert(!PdfAdapter.Matches(actual, target with { Path = @"C:\other\한글 (문서).pdf" }), "same filename in another folder cannot confirm position");
        Assert(!PdfAdapter.Matches(actual, target with { PdfPage = 5 }), "successful response at wrong page cannot confirm position");
        foreach (var invalid in new[] { State(page: "0"), State(page: "-1"), State(page: "11"), State(page: "2147483648"), State(page: "4.0"), State(version: "3.6.1"), State(version: "3.7-malformed"), State(file: "https://example.com/a.pdf"), State(file: @"C:\a.exe"), State() + "page: 4\n", "error: unknown command\n", "path: C:\\a.pdf\npage: 4\nsumver: 3.7\n" })
            Assert(Refused(() => PdfAdapter.ParseState(invalid)), "invalid, ambiguous, old-viewer or incomplete PDF state refused " + count);
        Assert(Refused(() => PdfAdapter.ParseState(new string('x', 65537))), "oversized PDF response refused");
        Assert(PdfAdapter.ParseOpenFiles(path + "\n" + path + "\n").Count == 2, "duplicate open documents retained for ambiguity detection");
        Assert(Refused(() => PdfAdapter.ParseOpenFiles("error: unknown command")), "unsupported open-files capability is not treated as no documents");
        Assert(Refused(() => PdfAdapter.ParseOpenFiles(path + "\0")), "embedded NUL cannot truncate an open path");
        Assert(PdfAdapter.OpenCommand(path).EndsWith(",0,0,0)]", StringComparison.Ordinal), "open does not force refresh or deliberately create another window");
        Assert(PdfAdapter.PageCommand(path, 4) == "[GotoPage(\"" + path + "\",4)]", "native DDE preserves Unicode path without command-line escaping");
        Assert(Refused(() => PdfAdapter.PageCommand("C:\\a\")]CmdClose]\\b.pdf", 1)), "command-injection quote in a pathname refused");

        // Real Windows DDEML against two private synthetic message windows; no viewer/user file needed.
        using var watchdog = new System.Threading.Timer(_ => { Console.Error.WriteLine("FAIL: native PDF checks exceeded the 8-second safety deadline."); Environment.Exit(1); }, null, 8000, Timeout.Infinite);
        var service = "WorkBookmarkPdfCheck" + Guid.NewGuid().ToString("N");
        using var first = new SyntheticDdeServer(service, State());
        using var other = new SyntheticDdeServer(service, State(@"C:\other\wrong.pdf", "1"));
        var context = new RequestContext(new WorkerRequest(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(10)));
        using (var dde = new PdfDdeClient(first.Handle, context, service))
        {
            Assert(PdfAdapter.ParseState(dde.Request("[GetFileState()]")).Path == path, "native DDE binds response to requested HWND among two servers");
            dde.Execute(PdfAdapter.PageCommand(path, 4));
            Assert(first.Executed.Count == 1 && other.Executed.Count == 0, "native DDE executes only on selected HWND");
            first.Response = State(page: "5");
            Assert(!PdfAdapter.Matches(PdfAdapter.ParseState(dde.Request("[GetFileState()]")), target), "execute acknowledgement with wrong actual page remains unconfirmed");
        }
        Assert(Refused(() => { using var missing = new PdfDdeClient(0, context, service); }), "unmatched requested HWND does not fall back to another DDE server");
        var expired = new RequestContext(new WorkerRequest(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert(Refused(() => { using var missing = new PdfDdeClient(first.Handle, expired, service); }), "expired whole-request deadline prevents DDE connection");
        Console.WriteLine($"RESULT: {count} PDF checks passed. Real Windows DDE transport with synthetic servers; SumatraPDF application remains untested.");
        return 0;
    }

    private sealed class SyntheticDdeServer : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();
        private uint threadId;
        private Exception? failure;
        internal nint Handle { get; private set; }
        internal volatile string Response;
        internal System.Collections.Concurrent.ConcurrentQueue<string> Executed { get; } = new();
        internal SyntheticDdeServer(string service, string response)
        {
            Response = response;
            thread = new Thread(() =>
            {
                try
                {
                    threadId = GetCurrentThreadId();
                    using var window = new ServerWindow(this, service);
                    Handle = window.Handle; ready.Set();
                    System.Windows.Forms.Application.Run();
                }
                catch (Exception error) { failure = error; ready.Set(); }
            }) { IsBackground = true, Name = "WorkBookmark synthetic DDE server" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Synthetic DDE server did not start.");
            if (failure is not null) throw new InvalidOperationException("Synthetic DDE server failed.", failure);
        }
        public void Dispose()
        {
            if (threadId != 0) PostThreadMessageW(threadId, 0x12, 0, 0); // WM_QUIT, only our private server thread
            if (!thread.Join(1000)) throw new TimeoutException("Synthetic DDE server did not stop.");
            ready.Dispose();
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

        private sealed class ServerWindow : System.Windows.Forms.NativeWindow, IDisposable
        {
            private readonly SyntheticDdeServer owner;
            private readonly string service;
            internal ServerWindow(SyntheticDdeServer owner, string service)
            {
                this.owner = owner; this.service = service;
                CreateHandle(new System.Windows.Forms.CreateParams { Caption = "WorkBookmark synthetic DDE" });
            }
            protected override void WndProc(ref System.Windows.Forms.Message message)
            {
                const int Initiate = 0x3E0, Terminate = 0x3E1, Ack = 0x3E4, Data = 0x3E5, Request = 0x3E6, Execute = 0x3E8;
                if (message.Msg == Initiate)
                {
                    var serverAtom = GlobalAddAtomW(service); var topicAtom = GlobalAddAtomW("control");
                    if ((ushort)message.LParam.ToInt64() == serverAtom && (ushort)(message.LParam.ToInt64() >> 16) == topicAtom)
                        SendMessageW(message.WParam, Ack, Handle, (nint)(serverAtom | ((uint)topicAtom << 16)));
                    else { GlobalDeleteAtom(serverAtom); GlobalDeleteAtom(topicAtom); }
                    return;
                }
                if (message.Msg == Request)
                {
                    var atom = (ushort)(message.LParam.ToInt64() >> 16);
                    var bytes = Encoding.Unicode.GetBytes(owner.Response + "\0");
                    var memory = GlobalAlloc(0x2002, (nuint)(bytes.Length + 4));
                    var pointer = GlobalLock(memory);
                    Marshal.WriteInt16(pointer, unchecked((short)0x3000)); // fResponse + fRelease
                    Marshal.WriteInt16(pointer, 2, 13); // CF_UNICODETEXT
                    Marshal.Copy(bytes, 0, pointer + 4, bytes.Length); GlobalUnlock(memory);
                    PostMessageW(message.WParam, Data, Handle, PackDDElParam(Data, (nuint)memory, atom));
                    return;
                }
                if (message.Msg == Execute)
                {
                    var pointer = GlobalLock(message.LParam);
                    owner.Executed.Enqueue(Marshal.PtrToStringUni(pointer) ?? ""); GlobalUnlock(message.LParam);
                    PostMessageW(message.WParam, Ack, Handle, PackDDElParam(Ack, 0x8000, (nuint)message.LParam));
                    return;
                }
                if (message.Msg == Terminate) { PostMessageW(message.WParam, Terminate, Handle, 0); return; }
                base.WndProc(ref message);
            }
            public void Dispose() => DestroyHandle();
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern ushort GlobalAddAtomW(string text);
            [DllImport("kernel32.dll")] private static extern ushort GlobalDeleteAtom(ushort atom);
            [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint length);
            [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
            [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
            [DllImport("user32.dll")] private static extern nint PackDDElParam(uint message, nuint low, nuint high);
            [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, int message, nint wParam, nint lParam);
            [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);
        }
    }
}
