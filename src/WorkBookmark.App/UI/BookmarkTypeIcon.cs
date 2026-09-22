using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

/// <summary>Owned type images, without opening or inspecting the bookmarked document.</summary>
internal static class BookmarkTypeIcon
{
    private const uint FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;
    private const uint UseFileAttributes = 0x10, SystemIconIndex = 0x4000;
    private const int MaximumCachedTypes = 128;
    private static readonly object CacheLock = new();
    // Cache encoded pixels, not live Bitmap/Icon handles. One native call may be in
    // progress; all callers get an immediate vector fallback and a bounded async wait.
    private static readonly Dictionary<string, Task<byte[]?>> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim NativeGate = new(1, 1);

    public static string GetIdentity(CapturedTarget target) => $"{target.Kind}:{ShellExtension(target)}";

    /// <summary>Creates a safe, immediate fallback. The caller owns the returned bitmap.</summary>
    public static Bitmap Create(CapturedTarget target, int pixelSize)
    {
        ValidateSize(pixelSize);
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(pixelSize / 24F, pixelSize / 24F);
        string category = Category(target);
        if (category == "Folder")
        {
            using var rear = new SolidBrush(Color.FromArgb(205, 149, 45));
            using var front = new SolidBrush(Color.FromArgb(241, 190, 76));
            graphics.FillPolygon(rear, [new PointF(2, 5), new(9, 5), new(11, 7), new(22, 7), new(22, 20), new(2, 20)]);
            graphics.FillPolygon(front, [new PointF(2, 9), new(22, 9), new(21, 20), new(3, 20)]);
        }
        else if (category == "Web")
        {
            using var globe = new Pen(Color.FromArgb(36, 111, 177), 1.6F);
            graphics.DrawEllipse(globe, 2.5F, 2.5F, 19, 19);
            graphics.DrawEllipse(globe, 7, 2.5F, 10, 19);
            graphics.DrawLine(globe, 2.5F, 12, 21.5F, 12);
            graphics.DrawArc(globe, 3, 4, 18, 6, 0, 180);
            graphics.DrawArc(globe, 3, 14, 18, 6, 180, 180);
        }
        else
        {
            Color accent = category switch
            {
                "Excel" => Color.FromArgb(30, 120, 78),
                "Word" => Color.FromArgb(43, 103, 177),
                "Hangul" => Color.FromArgb(55, 134, 183),
                "PowerPoint" => Color.FromArgb(187, 76, 49),
                "PDF" => Color.FromArgb(186, 53, 59),
                "Text" => Color.FromArgb(52, 132, 158),
                "Image" => Color.FromArgb(125, 87, 172),
                "Archive" => Color.FromArgb(151, 110, 55),
                _ => Color.FromArgb(98, 114, 130)
            };
            PointF[] paper = [new(5, 2), new(15, 2), new(20, 7), new(20, 22), new(5, 22)];
            using var fill = new SolidBrush(Color.FromArgb(250, 252, 253));
            using var outline = new Pen(accent, 1.2F);
            using var color = new SolidBrush(accent);
            graphics.FillPolygon(fill, paper);
            graphics.DrawPolygon(outline, paper);
            graphics.DrawLines(outline, [new PointF(15, 2), new(15, 7), new(20, 7)]);
            if (category == "Text")
            {
                for (int y = 10; y <= 18; y += 4) graphics.DrawLine(outline, 8, y, 17, y);
            }
            else if (category == "Image")
            {
                graphics.FillEllipse(color, 8, 9, 3, 3);
                graphics.FillPolygon(color, [new PointF(7, 19), new(11, 14), new(13, 16), new(16, 12), new(18, 19)]);
            }
            else if (category == "Archive")
            {
                for (int y = 9; y <= 19; y += 3) graphics.FillRectangle(color, y % 2 == 0 ? 12 : 10, y, 2, 2);
            }
            else
            {
                string label = category switch { "Excel" => "X", "Word" => "W", "Hangul" => "H", "PowerPoint" => "P", "PDF" => "PDF", _ => "" };
                if (label.Length > 0)
                {
                    using var font = new Font("Segoe UI", label.Length > 1 ? 5.5F : 10F, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    graphics.DrawString(label, font, color, new RectangleF(5, 8, 15, 13), format);
                }
                else
                {
                    graphics.DrawLine(outline, 8, 12, 17, 12);
                    graphics.DrawLine(outline, 8, 16, 15, 16);
                }
            }
        }
        return bitmap;
    }

    /// <summary>
    /// Returns a fresh owned image. Registered icons are looked up off the UI thread,
    /// using only a dummy extension and synthetic attributes. A slow/broken Shell
    /// handler cannot delay the caller longer than 250 ms or start more native workers.
    /// </summary>
    public static Task<Bitmap> CreateAsync(CapturedTarget target, int pixelSize)
    {
        ValidateSize(pixelSize);
        int imageList = pixelSize <= 32 ? 0 : pixelSize <= 48 ? 2 : 4;
        bool folder = target.Kind == TargetKind.Folder;
        string extension = ShellExtension(target);
        string key = $"{(folder ? "folder" : extension)}:{imageList}";
        Task<byte[]?>? pending;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out pending) && Cache.Count < MaximumCachedTypes)
            {
                // Passing just '.ext' is the Shell API's documented type-only lookup.
                // Neither this path nor the cache key includes any business filename.
                string dummyPath = folder ? "WorkBookmark-Type-Folder" : extension.Length > 0 ? extension : "WorkBookmark-Type-File";
                pending = Task.Run(async () =>
                {
                    await NativeGate.WaitAsync().ConfigureAwait(false);
                    try { return ReadRegisteredPixels(dummyPath, folder ? FileAttributeDirectory : FileAttributeNormal, imageList); }
                    catch (Exception error) when (IsIconFailure(error)) { return null; }
                    finally { NativeGate.Release(); }
                });
                Cache.Add(key, pending);
            }
        }
        return CompleteAsync(target, pixelSize, pending);
    }

    private static async Task<Bitmap> CompleteAsync(CapturedTarget target, int pixelSize, Task<byte[]?>? pending)
    {
        if (pending is not null)
        {
            try
            {
                byte[]? pixels = await pending.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                if (pixels is not null)
                {
                    using var stream = new MemoryStream(pixels, writable: false);
                    using var source = new Bitmap(stream);
                    var result = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
                    try
                    {
                        using var graphics = Graphics.FromImage(result);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(source, new Rectangle(0, 0, pixelSize, pixelSize));
                        return result;
                    }
                    catch { result.Dispose(); throw; }
                }
            }
            catch (Exception error) when (error is TimeoutException || IsIconFailure(error)) { }
        }
        return Create(target, pixelSize);
    }

    private static bool IsIconFailure(Exception error) => error is ExternalException or ArgumentException or
        InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static byte[]? ReadRegisteredPixels(string dummyPath, uint attributes, int imageList)
    {
        int initialized = CoInitializeEx(nint.Zero, 0); // this worker is MTA
        if (initialized < 0) return null;
        nint list = nint.Zero, iconHandle = nint.Zero;
        try
        {
            if (SHGetFileInfo(dummyPath, attributes, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                    UseFileAttributes | SystemIconIndex) == nint.Zero) return null;
            // An unregistered extension produces the generic blank document icon.
            // Keep our semantic fallback in that case (e.g. Excel without Office).
            if (attributes == FileAttributeNormal && SHGetFileInfo("WorkBookmark-Type-File", FileAttributeNormal,
                    out var generic, (uint)Marshal.SizeOf<ShellFileInfo>(), UseFileAttributes | SystemIconIndex) != nint.Zero &&
                generic.IconIndex == info.IconIndex) return null;
            Guid iid = new("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IID_IImageList
            if (SHGetImageList(imageList, ref iid, out list) < 0 || list == nint.Zero) return null;
            // Windows documents that this interface pointer can be used as HIMAGELIST.
            iconHandle = ImageList_GetIcon(list, info.IconIndex, 1); // ILD_TRANSPARENT
            if (iconHandle == nint.Zero) return null;
            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            using var encoded = new MemoryStream();
            bitmap.Save(encoded, ImageFormat.Png);
            return encoded.ToArray();
        }
        finally
        {
            if (iconHandle != nint.Zero) DestroyIcon(iconHandle);
            if (list != nint.Zero) Marshal.Release(list);
            CoUninitialize();
        }
    }

    private static string ShellExtension(CapturedTarget target)
    {
        if (target.Kind == TargetKind.Folder) return "";
        if (target.Kind == TargetKind.WebPage) return ".html";
        if (target.Kind is TargetKind.NotepadPosition or TargetKind.NotepadSnapshot) return ".txt";
        string path = target.Path;
        // Office URL fragments/query strings are not part of the document extension.
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") path = uri.AbsolutePath;
        int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        int dot = path.LastIndexOf('.');
        string extension = dot > separator ? path[dot..].ToLowerInvariant() : "";
        if (extension.Length is < 2 or > 17 || extension.AsSpan(1).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789")) extension = "";
        return extension.Length > 0 ? extension : target.Kind switch
        {
            TargetKind.ExcelCell => ".xlsx", TargetKind.WordPosition => ".docx",
            TargetKind.PowerPointSlide => ".pptx", TargetKind.PdfPage => ".pdf", _ => ""
        };
    }

    private static string Category(CapturedTarget target) => target.Kind switch
    {
        TargetKind.Folder => "Folder", TargetKind.WebPage => "Web", TargetKind.ExcelCell => "Excel",
        TargetKind.WordPosition => "Word", TargetKind.PowerPointSlide => "PowerPoint", TargetKind.PdfPage => "PDF",
        TargetKind.NotepadPosition or TargetKind.NotepadSnapshot => "Text",
        _ => ShellExtension(target) switch
        {
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".ods" => "Excel",
            ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" => "Word",
            ".hwp" or ".hwpx" => "Hangul",
            ".ppt" or ".pptx" or ".pptm" or ".odp" => "PowerPoint",
            ".pdf" => "PDF", ".txt" or ".md" or ".log" => "Text",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".svg" or ".webp" => "Image",
            ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => "Archive", _ => "File"
        }
    };

    private static void ValidateSize(int pixelSize)
    {
        if (pixelSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pixelSize));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static extern int SHGetImageList(int imageList, ref Guid iid, out nint result);
    [DllImport("comctl32.dll")]
    private static extern nint ImageList_GetIcon(nint imageList, int index, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
