using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using WorkBookmark.Core;
using WorkBookmark.Windows;

internal static class AdapterRegressionChecks
{
    internal static int Run(string[] args)
    {
        var checks = new List<object>();
        void Assert(bool value, string name)
        {
            checks.Add(new { name, passed = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine("PASS: " + name);
        }
        string fixture = Path.Combine(Path.GetTempPath(), "WorkBookmark-AdapterChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string zip = Path.Combine(fixture, "한글 압축 (선택).zip"), folder = Path.Combine(fixture, "일반 폴더");
        Directory.CreateDirectory(folder);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("synthetic.txt");
        try
        {
            using (var scope = new ComScope())
            {
                object shell = scope.Keep(Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"), true)!)!);
                var view = scope.Call(shell, "NameSpace", fixture);
                var item = scope.Call(view, "ParseName", Path.GetFileName(zip));
                Assert(scope.Flag(item, "IsFileSystem") && scope.Flag(item, "IsFolder"), "native Shell reproduces ZIP-as-folder namespace");
                var target = ExplorerAdapter.ClassifySelection(scope.Text(item, "Path"), scope.Flag(item, "IsLink"));
                Assert(target.Kind == TargetKind.File && target.Path == zip && !PathPolicy.ShouldOpenDocument(target.Path), "selected ZIP persists as file and uses reveal policy");
            }
            Assert(ExplorerAdapter.ClassifySelection(folder, false).Kind == TargetKind.Folder, "real selected directory remains a folder");
            var shortcut = Path.Combine(fixture, "synthetic.lnk"); File.WriteAllBytes(shortcut, [0]);
            Assert(ExplorerAdapter.ClassifySelection(shortcut, true).Kind == TargetKind.File, "shortcut remains a file without following its target");
            bool missingRefused = false;
            try { ExplorerAdapter.ClassifySelection(Path.Combine(fixture, "removed.zip"), false); }
            catch (FileNotFoundException) { missingRefused = true; }
            Assert(missingRefused, "removed selection cannot produce a normal captured target");

            var context = Context(); var shellApi = new FakeShell(); bool abandoned = false;
            shellApi.AfterParse = count => { if (count == 2) abandoned = true; };
            bool cancelled = false;
            try { WindowsAdapter.Reveal(zip, context, () => { if (abandoned) throw new BookmarkException(ResultCode.Cancelled); }, shellApi); }
            catch (BookmarkException error) when (error.Code == ResultCode.Cancelled) { cancelled = true; }
            Assert(cancelled && shellApi.OpenCalls == 0 && !context.ExternalActionStarted && shellApi.Freed.Count == 2, "interaction change during PIDL parsing prevents reveal and frees both PIDLs");

            context = Context(); shellApi = new FakeShell(); bool checkedAfterParse = false;
            WindowsAdapter.Reveal(zip, context, () => checkedAfterParse = shellApi.ParseCalls == 2, shellApi);
            Assert(checkedAfterParse && shellApi.OpenCalls == 1 && context.ExternalActionStarted && shellApi.Freed.Count == 2, "stable reveal checks interaction after parsing and opens exactly once");

            context = Context(expired: true); shellApi = new FakeShell(); cancelled = false;
            try { WindowsAdapter.Reveal(zip, context, () => { }, shellApi); }
            catch (BookmarkException error) when (error.Code == ResultCode.Cancelled) { cancelled = true; }
            Assert(cancelled && shellApi.OpenCalls == 0 && shellApi.Freed.Count == 2, "expired whole-request deadline prevents reveal after parsing");

            context = Context(); shellApi = new FakeShell { FailSecondParse = true }; bool parseFailed = false;
            try { WindowsAdapter.Reveal(zip, context, () => { }, shellApi); }
            catch (IOException) { parseFailed = true; }
            Assert(parseFailed && shellApi.OpenCalls == 0 && shellApi.Freed.SetEquals([1]), "second parse failure frees the first PIDL without opening");

            context = Context(); shellApi = new FakeShell { OpenResult = unchecked((int)0x80070005) }; bool openFailed = false;
            try { WindowsAdapter.Reveal(zip, context, () => { }, shellApi); }
            catch (UnauthorizedAccessException) { openFailed = true; }
            Assert(openFailed && shellApi.OpenCalls == 1 && !context.ExternalActionStarted && shellApi.Freed.Count == 2, "Shell refusal clears external-action flag and releases both PIDLs");
            Console.WriteLine($"RESULT: {checks.Count} adapter checks passed. Synthetic filesystem/Shell metadata and injected call boundary; no full Explorer UI gate claimed.");
            if (args.Length > 1) File.WriteAllText(args[1], JsonSerializer.Serialize(new { suite = "Windows adapter regressions", checks, limitation = "Actual local ZIP namespace; injected reveal delays. Does not replace P0-A tab capture or D07 full UI." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            // Only the freshly-created private synthetic fixture directory is removed.
            Directory.Delete(fixture, recursive: true);
        }
    }
    private static RequestContext Context(bool expired = false) => new(new WorkerRequest(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(expired ? -1 : 15)));
    private sealed class FakeShell : IShellSelectionApi
    {
        internal int ParseCalls, OpenCalls;
        internal int OpenResult;
        internal bool FailSecondParse;
        internal Action<int>? AfterParse;
        internal HashSet<nint> Freed = [];
        public nint Parse(string path)
        {
            ParseCalls++;
            if (FailSecondParse && ParseCalls == 2) throw new IOException("Synthetic parse failure");
            AfterParse?.Invoke(ParseCalls);
            return ParseCalls;
        }
        public int Open(nint folder, nint item) { OpenCalls++; return OpenResult; }
        public void Free(nint item) { if (!Freed.Add(item)) throw new InvalidOperationException("PIDL freed twice"); }
    }
}
