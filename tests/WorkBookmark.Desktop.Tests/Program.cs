using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WorkBookmark.App;
using WorkBookmark.Core;
using System.Text.Json;

namespace WorkBookmark.Desktop.Tests;

internal static class Program
{
    private static readonly List<object> Checks = [];
    static void Assert(bool condition, string name) { Checks.Add(new { name, passed = condition }); if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    [STAThread] static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string data = Path.Combine(Path.GetTempPath(), "WorkBookmark-UiQa-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        var custom = UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F19), RecentHotkey = new Hotkey(7, (int)Keys.F20), IntroShown = true };
        UserSettings.Default.Save(data); custom.Save(data);
        Assert(UserSettings.Load(data) == custom, "settings atomic overwrite round-trip");
        Assert(Directory.GetFiles(data, "*.tmp").Length == 0, "settings no temporary leftovers");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{invalid"); bool refused = false; try { UserSettings.Load(data); } catch { refused = true; }
        Assert(refused && File.ReadAllText(Path.Combine(data, "settings.json")) == "{invalid", "corrupt settings preserved");
        using var first = new HotkeyWindow(new Hotkey(7, (int)Keys.F19), new Hotkey(7, (int)Keys.F20));
        Assert(first.CaptureRegistered && first.RecentRegistered, "native hotkeys registered");
        using var blocker = new HotkeyWindow(new Hotkey(7, (int)Keys.F21), new Hotkey(7, (int)Keys.F22));
        Assert(blocker.CaptureRegistered && blocker.RecentRegistered, "conflict fixture registered");
        Assert(!first.TryUpdate(new Hotkey(7, (int)Keys.F23), new Hotkey(7, (int)Keys.F21), out _), "replacement collision refused");
        using var check = new HotkeyWindow(new Hotkey(7, (int)Keys.F19), new Hotkey(7, (int)Keys.F23));
        Assert(!check.CaptureRegistered && check.RecentRegistered, "old registration retained and pending reservation rolled back");
        Assert(!first.TryUpdate(new Hotkey(7, (int)Keys.F24), new Hotkey(7, (int)Keys.F24), out _), "duplicate shortcut pair refused");
        string target = Path.Combine(data, "한글 공백 (시험)", "WorkBookmark.exe"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, [0]);
        byte[] bytes = (byte[])typeof(StartupRegistration).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [target])!;
        string shortcut = Path.Combine(data, "shortcut.lnk"); File.WriteAllBytes(shortcut, bytes);
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            ((IPersistFile)shell).Load(shortcut, 0);
            var path = new StringBuilder(32768); ((IShellLinkW)shell).GetPath(path, path.Capacity, IntPtr.Zero, 4);
            Assert(path.ToString() == target, "Windows Shell parses Unicode startup target");
            var description = new StringBuilder(1000); ((IShellLinkW)shell).GetDescription(description, description.Capacity);
            Assert(description.ToString() == "WorkBookmark per-user startup shortcut v1", "startup ownership description parsed");
            var working = new StringBuilder(32768); ((IShellLinkW)shell).GetWorkingDirectory(working, working.Capacity);
            Assert(working.ToString() == Path.GetDirectoryName(target), "startup working directory parsed");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        var assembly = typeof(UserSettings).Assembly;
        var imeType = assembly.GetType("WorkBookmark.App.UI.ImeTextBox")!;
        using (var ime = (Control)Activator.CreateInstance(imeType)!)
        {
            SendMessage(ime.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
            Assert((bool)imeType.GetProperty("IgnoreSubmit")!.GetValue(ime)!, "IME composition suppresses submission");
            SendMessage(ime.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            Assert((bool)imeType.GetProperty("IgnoreSubmit")!.GetValue(ime)!, "IME commit Enter has a suppression window");
        }
        var sample = new Bookmark(Guid.NewGuid(), new CapturedTarget(TargetKind.File, target), target, "WorkBookmark.exe", "진행 메모", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null, null, null, null);
        bool failNote = true;
        string? saved = null;
        Func<string, Task> persist = value => { if (failNote) return Task.FromException(new IOException("Injected storage failure")); saved = value; return Task.CompletedTask; };
        Type noteType = assembly.GetType("WorkBookmark.App.UI.NoteForm")!;
        using (var note = (Form)Activator.CreateInstance(noteType, sample, persist)!)
        {
            var editor = (TextBox)noteType.GetField("_note", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(note)!;
            editor.Text = "실패 후 보존할 메모";
            var save = noteType.GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)save.Invoke(note, null)!).GetAwaiter().GetResult();
            Assert(!note.IsDisposed && editor.Text == "실패 후 보존할 메모" && !editor.ReadOnly, "note write failure retains editable content");
            failNote = false;
            ((Task)save.Invoke(note, null)!).GetAwaiter().GetResult();
            Assert(saved == "실패 후 보존할 메모", "note retry saves retained text");
        }
        Type recentType = assembly.GetType("WorkBookmark.App.UI.RecentForm")!;
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults([sample], false));
        using (var recent = (Form)Activator.CreateInstance(recentType, load)!)
        {
            Task open = (Task)recentType.GetMethod("OpenAsync")!.Invoke(recent, null)!;
            while (!open.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            open.GetAwaiter().GetResult();
            Application.DoEvents();
            Assert(recent.Visible && ((Bookmark?)recentType.GetProperty("Selected")!.GetValue(recent))?.Id == sample.Id, "recent form opens with loaded first selection");
            if (args.Length == 1)
            {
                using var bitmap = new Bitmap(recent.Width, recent.Height);
                recent.DrawToBitmap(bitmap, new Rectangle(Point.Empty, recent.Size));
                bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0])!, "recent-component.png"), ImageFormat.Png);
            }
        }
        Console.WriteLine($"RESULT: {Checks.Count} checks passed. Native launch, actual IME and rendered UI remain separate manual gates.");
        if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(new { suite = "Desktop checks", checks = Checks, limitations = "Synthetic IME messages test event suppression only; actual Korean IME and rendered interaction require manual verification." }, new JsonSerializerOptions { WriteIndented = true }));

    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder working, int count);
    }
}
