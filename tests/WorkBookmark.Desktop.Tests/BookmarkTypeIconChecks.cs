using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class BookmarkTypeIconChecks
{
    private static readonly Type IconType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.BookmarkTypeIcon")!;

    internal static void Run(Action<bool, string> assert)
    {
        CapturedTarget[] targets =
        [
            new(TargetKind.Folder, @"\\unavailable.invalid\synthetic\folder.pdf"),
            new(TargetKind.File, @"\\unavailable.invalid\synthetic\plain.unknown"),
            new(TargetKind.ExcelCell, @"C:\does-not-exist\sheet.xlsx", "시트", "$B$7"),
            new(TargetKind.WordPosition, "https://example.invalid/documents/doc.docx?version=1", WordStart: 20),
            new(TargetKind.PowerPointSlide, @"C:\does-not-exist\slides.pptx", SlideNumber: 2),
            new(TargetKind.PdfPage, @"C:\does-not-exist\document.pdf", PdfPage: 4),
            new(TargetKind.NotepadSnapshot, "notepad-snapshot:synthetic-private-id", TextContent: "합성 내용"),
            new(TargetKind.WebPage, "https://example.invalid/document.pdf?private=value", PageTitle: "합성 페이지")
        ];
        var hashes = new HashSet<string>();
        foreach (var target in targets)
        {
            using var image = Create(target, 24);
            hashes.Add(Pixels(image));
            assert(image.Size == new Size(24, 24) && HasDrawing(image), $"TIC01 {target.Kind} has a visible 24px type icon without a real target");
        }
        assert(hashes.Count == targets.Length, "TIC02 folder, file, Office, PDF, snapshot and webpage fallbacks are visually distinct");
        foreach ((string extension, TargetKind kind) in new[] { (".xlsx", TargetKind.ExcelCell), (".docx", TargetKind.WordPosition), (".pptx", TargetKind.PowerPointSlide), (".pdf", TargetKind.PdfPage), (".txt", TargetKind.NotepadPosition) })
        {
            var file = new CapturedTarget(TargetKind.File, @"\\unavailable.invalid\synthetic\anything" + extension);
            using var fileIcon = Create(file, 24);
            using var typedIcon = Create(file with { Kind = kind }, 24);
            assert(Pixels(fileIcon) == Pixels(typedIcon), $"TIC03 Explorer file {extension} receives its document type fallback");
        }
        var original = new CapturedTarget(TargetKind.File, @"\\unavailable.invalid\synthetic\original.PDF");
        var sameType = original with { Path = @"C:\also-does-not-exist\different.pdf" };
        var relinked = original with { Path = @"C:\also-does-not-exist\different.xlsx" };
        assert(Identity(original) == Identity(sameType) && Identity(original) != Identity(relinked),
            "TIC04 icon identity ignores business paths but changes when relink changes file type");
        var shellExtension = IconType.GetMethod("ShellExtension", BindingFlags.Static | BindingFlags.NonPublic)!;
        assert((string)shellExtension.Invoke(null, [targets[0]])! == "" &&
            (string)shellExtension.Invoke(null, [targets[3]])! == ".docx" &&
            (string)shellExtension.Invoke(null, [targets[6]])! == ".txt" &&
            (string)shellExtension.Invoke(null, [targets[7]])! == ".html",
            "TIC05 folder, Office URL, snapshot and webpage use only synthetic Shell type lookups");
        assert((string)shellExtension.Invoke(null, [original with { Path = @"\\private.invalid\share\report.pdf:stream" }])! == "" &&
            (string)shellExtension.Invoke(null, [original with { Path = "C:\\synthetic\\name." + new string('a', 100) }])! == "",
            "TIC06 unsafe or excessive extensions cannot reach the native Shell lookup");
        foreach (int size in new[] { 24, 36, 48, 72 })
        {
            using var icon = Create(targets[2], size);
            assert(icon.Size == new Size(size, size) && HasDrawing(icon), $"TIC07 fallback renders directly at {size}px for caller DPI");
        }

        var blocked = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = IconType.GetMethod("CompleteAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        using var fallback = ((Task<Bitmap>)complete.Invoke(null, [targets[2], 24, blocked.Task])!).GetAwaiter().GetResult();
        assert(clock.Elapsed < TimeSpan.FromSeconds(2) && HasDrawing(fallback),
            "TIC08 a blocked native lookup returns a fallback within a bounded wait");
        blocked.SetResult(null);
        var failure = Task.FromException<byte[]?>(new System.Runtime.InteropServices.ExternalException("Synthetic Shell failure"));
        using var failed = ((Task<Bitmap>)complete.Invoke(null, [targets[2], 24, failure])!).GetAwaiter().GetResult();
        assert(HasDrawing(failed), "TIC09 failed native lookup is contained and preserves the fallback");

        var asyncFactory = IconType.GetMethod("CreateAsync")!;
        var requests = Enumerable.Range(0, 8).Select(_ => (Task<Bitmap>)asyncFactory.Invoke(null, [original, 48])!).ToArray();
        Bitmap[] icons = Task.WhenAll(requests).GetAwaiter().GetResult();
        try
        {
            assert(icons.All(icon => icon.Size == new Size(48, 48) && HasDrawing(icon)) && icons.Distinct().Count() == icons.Length,
                "TIC10 concurrent type lookups return separately owned DPI-sized bitmaps");
            icons[0].Dispose();
            assert(HasDrawing(icons[1]), "TIC11 disposing one sticker icon does not invalidate another sticker's image");
        }
        finally { foreach (var icon in icons) icon.Dispose(); }
    }

    private static Bitmap Create(CapturedTarget target, int size) => (Bitmap)IconType.GetMethod("Create")!.Invoke(null, [target, size])!;
    private static string Identity(CapturedTarget target) => (string)IconType.GetMethod("GetIdentity")!.Invoke(null, [target])!;
    private static bool HasDrawing(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A > 100) return true;
        return false;
    }
    private static string Pixels(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = bitmap.GetPixel(x, y).ToArgb();
        return string.Join(',', pixels);
    }
}
