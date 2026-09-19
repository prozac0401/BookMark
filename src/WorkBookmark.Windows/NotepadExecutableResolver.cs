using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Finds Microsoft's registered Notepad executable without consulting file associations or PATH.</summary>
internal static class NotepadExecutableResolver
{
    private const string Family = "Microsoft.WindowsNotepad_8wekyb3d8bbwe";
    private const string Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private const int InsufficientBuffer = 122;
    private const uint MaximumPackages = 64;
    private const uint MaximumBufferCharacters = 65536;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint length, nint buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);

    internal static string Resolve()
    {
        try
        {
            var names = RegisteredPackages();
            if (names.Count == 0) return ClassicExecutable();
            var candidates = new List<(Version Version, string Path)>();
            foreach (string name in names)
            {
                string root = InstalledPath(name);
                using var reader = XmlReader.Create(Path.Combine(root, "AppxManifest.xml"), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1024 * 1024
                });
                var manifest = XDocument.Load(reader);
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identity = manifest.Root?.Element(ns + "Identity");
                if (identity is null || (string?)identity.Attribute("Name") != "Microsoft.WindowsNotepad" ||
                    (string?)identity.Attribute("Publisher") != Publisher ||
                    !Version.TryParse((string?)identity.Attribute("Version"), out var version)) continue;
                var applications = manifest.Root?.Element(ns + "Applications")?.Elements(ns + "Application")
                    .Where(app => (string?)app.Attribute("Id") == "App" &&
                        (string?)app.Attribute("EntryPoint") == "Windows.FullTrustApplication").ToArray() ?? [];
                if (applications.Length == 0) continue; // Resource packages have no executable application.
                if (applications.Length != 1) throw Unavailable();
                string? relative = (string?)applications[0].Attribute("Executable");
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw Unavailable();
                string executable = Path.GetFullPath(Path.Combine(root, relative));
                string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
                if (!executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(executable).Equals("Notepad.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                    throw Unavailable();
                candidates.Add((version, executable));
            }
            if (candidates.Count == 0) throw Unavailable();
            var newest = candidates.Max(candidate => candidate.Version)!;
            var paths = candidates.Where(candidate => candidate.Version == newest).Select(candidate => candidate.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length != 1) throw Unavailable();
            return paths[0];
        }
        catch (EntryPointNotFoundException) { return ClassicExecutable(); }
        catch (BookmarkException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or OverflowException)
        { throw Unavailable(); }
    }

    private static List<string> RegisteredPackages()
    {
        uint count = 0, length = 0;
        int result = GetPackagesByPackageFamily(Family, ref count, 0, ref length, 0);
        if (result == 0 && count == 0) return [];
        if (result != InsufficientBuffer || count is 0 or > MaximumPackages || length is 0 or > MaximumBufferCharacters)
            throw Unavailable();
        nint names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        nint buffer = 0;
        try
        {
            buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            uint capacity = length, nameCapacity = count;
            result = GetPackagesByPackageFamily(Family, ref count, names, ref length, buffer);
            if (result != 0 || count > nameCapacity || length > capacity) throw Unavailable();
            var values = new List<string>();
            for (int i = 0; i < count; i++)
            {
                nint pointer = Marshal.ReadIntPtr(names, i * IntPtr.Size);
                long offset = pointer.ToInt64() - buffer.ToInt64();
                if (offset < 0 || offset % sizeof(char) != 0 || offset >= capacity * sizeof(char)) throw Unavailable();
                int available = Math.Min(512, checked((int)capacity - (int)(offset / sizeof(char))));
                string value = Marshal.PtrToStringUni(pointer, available) ?? throw Unavailable();
                int end = value.IndexOf('\0');
                if (end <= 0) throw Unavailable();
                value = value[..end];
                if (!value.StartsWith("Microsoft.WindowsNotepad_", StringComparison.Ordinal) ||
                    !value.EndsWith("_8wekyb3d8bbwe", StringComparison.Ordinal)) throw Unavailable();
                values.Add(value);
            }
            return values;
        }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(names); }
    }

    private static string InstalledPath(string name)
    {
        uint length = 0;
        if (GetPackagePathByFullName(name, ref length, null) != InsufficientBuffer || length is 0 or > 32767)
            throw Unavailable();
        var path = new StringBuilder((int)length);
        if (GetPackagePathByFullName(name, ref length, path) != 0) throw Unavailable();
        string root = PathPolicy.Normalize(path.ToString());
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw Unavailable();
        return root;
    }

    private static string ClassicExecutable()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(path)) throw Unavailable();
        return path;
    }
    private static BookmarkException Unavailable() => new(ResultCode.TargetUnavailable);
}
