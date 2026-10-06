using System.Drawing;
using System.Reflection;
using WorkBookmark.App;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerSnapChecks
{
    private static readonly MethodInfo SnapMethod = typeof(UserSettings).Assembly
        .GetType("WorkBookmark.App.StickerSnap")!
        .GetMethod("Snap", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Run(Action<bool, string> assert)
    {
        Rectangle area = new(-1600, -1000, 3200, 2000);
        Rectangle peer = new(100, 100, 200, 120);
        Rectangle Snap(Rectangle proposed, Rectangle[] peers, int dpi = 96, Rectangle? workArea = null) =>
            (Rectangle)SnapMethod.Invoke(null, [proposed, peers, workArea ?? area, dpi])!;

        var free = new Rectangle(412, 375, 200, 120);
        assert(Snap(free, []) == free, "SN01 an isolated sticker remains freely positioned");
        assert(Snap(new(312, 106, 200, 120), [peer]) == new Rectangle(308, 100, 200, 120),
            "SN02 adjacent stickers snap into a row with an eight-pixel gap");
        assert(Snap(new(106, 234, 200, 120), [peer]) == new Rectangle(100, 228, 200, 120),
            "SN03 adjacent stickers snap into a column with an eight-pixel gap");
        assert(Snap(new(-106, 106, 200, 120), [peer]) == new Rectangle(-108, 100, 200, 120),
            "SN04 placing a sticker before its peer supports negative screen coordinates");
        assert(Snap(new(106, -26, 200, 120), [peer]) == new Rectangle(100, -28, 200, 120),
            "SN05 placing a sticker above its peer preserves the same gap");
        assert(Snap(new(201, 235, 100, 80), [peer]) == new Rectangle(200, 228, 100, 80),
            "SN06 unequal-width stickers can align their right edges in a column");
        assert(Snap(new(312, 162, 200, 60), [peer]) == new Rectangle(308, 160, 200, 60),
            "SN07 unequal-height stickers can align their bottom edges in a row");
        var distant = new Rectangle(600, 106, 200, 120);
        assert(Snap(distant, [peer]) == distant,
            "SN08 matching edges on a distant sticker do not attract a dragged sticker");
        assert(Snap(new(320, 100, 200, 120), [peer]) == new Rectangle(308, 100, 200, 120),
            "SN09 the twelve-pixel snap threshold is inclusive");
        var beyond = new Rectangle(321, 101, 200, 120);
        assert(Snap(beyond, [peer]) == beyond, "SN10 movement beyond the snap threshold is unchanged");
        assert(Snap(new(297, 100, 200, 120), [peer]) == new Rectangle(308, 100, 200, 120),
            "SN11 slight edge overlap can snap outward into separated placement");
        var overlapping = new Rectangle(102, 102, 200, 120);
        assert(Snap(overlapping, [peer]) == overlapping,
            "SN12 magnets do not pull a crossing sticker deeper over a peer");

        Rectangle second = new(104, 100, 200, 120);
        assert(Snap(new(315, 106, 200, 120), [peer, second]) == new Rectangle(312, 100, 200, 120),
            "SN13 the closest eligible peer edge wins");
        var tied = new Rectangle(310, 106, 200, 120);
        assert(Snap(tied, [peer, second]) == new Rectangle(308, 100, 200, 120) &&
            Snap(tied, [second, peer]) == Snap(tied, [peer, second]),
            "SN14 equal-distance ties are stable regardless of peer enumeration order");

        Rectangle scaledPeer = new(150, 150, 300, 180);
        assert(Snap(new(480, 159, 300, 180), [scaledPeer], 144) == new Rectangle(462, 150, 300, 180),
            "SN15 150-percent DPI scales both the gap and inclusive threshold");
        var scaledBeyond = new Rectangle(481, 151, 300, 180);
        assert(Snap(scaledBeyond, [scaledPeer], 144) == scaledBeyond,
            "SN16 the scaled threshold does not attract a sticker one pixel beyond it");
        assert(Snap(new(640, 211, 400, 240), [new Rectangle(200, 200, 400, 240)], 192) ==
            new Rectangle(616, 200, 400, 240), "SN17 200-percent DPI preserves proportional magnetic spacing");

        Rectangle offsetArea = new(-1920, -1080, 1920, 1080);
        assert(Snap(new(-1588, -894, 200, 120), [new Rectangle(-1800, -900, 200, 120)], workArea: offsetArea) ==
            new Rectangle(-1592, -900, 200, 120), "SN18 a monitor left and above the primary screen snaps correctly");
        var edge = new Rectangle(407, 105, 200, 120);
        assert(Snap(edge, [new Rectangle(210, 100, 200, 120)], workArea: new Rectangle(0, 0, 610, 500)) == edge,
            "SN19 a candidate extending beyond the working area is rejected");
        var outside = new Rectangle(-2100, -1200, 200, 120);
        assert(Snap(outside, [peer], workArea: offsetArea) == outside,
            "SN20 unsnapped movement is not forcibly clamped to a monitor");
        assert(Snap(free, [Rectangle.Empty, new Rectangle(410, 370, -5, 20)]) == free,
            "SN21 empty or invalid peer bounds do not participate in snapping");

        Rectangle[] mixedPeers = [peer, second, new Rectangle(308, 340, 130, 80)];
        bool invariants = true;
        for (int x = 280; x <= 340; x += 3)
        for (int y = 210; y <= 250; y += 2)
        {
            Rectangle proposed = new(x, y, 200, 120);
            Rectangle snapped = Snap(proposed, mixedPeers);
            invariants &= snapped.Size == proposed.Size && Math.Abs(snapped.X - proposed.X) <= 12 &&
                Math.Abs(snapped.Y - proposed.Y) <= 12 && area.Contains(snapped) &&
                mixedPeers.All(item => Overlap(snapped, item) <= Overlap(proposed, item));
        }
        assert(invariants, "SN22 multi-peer corner movement preserves size, threshold, bounds and overlap invariants");
    }

    private static long Overlap(Rectangle first, Rectangle second)
    {
        Rectangle intersection = Rectangle.Intersect(first, second);
        return (long)intersection.Width * intersection.Height;
    }
}
