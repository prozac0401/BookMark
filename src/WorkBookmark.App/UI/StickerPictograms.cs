using System.Drawing;
using System.Drawing.Drawing2D;

namespace WorkBookmark.App.UI;

/// <summary>Scalable Fluent System Icons, compiled from Microsoft's 20 px regular SVGs.</summary>
internal static class StickerPictograms
{
    public static void DrawOpen(Graphics graphics, RectangleF bounds, Color color)
    {
        using var path = CreateOpen();
        Draw(graphics, path, bounds, color);
    }

    public static void DrawFolder(Graphics graphics, RectangleF bounds, Color color, bool expanded)
    {
        using var path = expanded ? CreateFolderOpen() : CreateFolder();
        Draw(graphics, path, bounds, color);
    }

    private static void Draw(Graphics graphics, GraphicsPath path, RectangleF bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        float scale = Math.Min(bounds.Width, bounds.Height) / 20F;
        using var transform = new Matrix(scale, 0, 0, scale,
            bounds.X + (bounds.Width - 20F * scale) / 2F,
            bounds.Y + (bounds.Height - 20F * scale) / 2F);
        path.Transform(transform);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        // Give compact desktop actions a 1.5-DIP optical weight after downsampling.
        using var edge = new Pen(color, .5F * scale) { LineJoin = LineJoin.Round };
        graphics.DrawPath(edge, path);
    }

    // Copyright (c) 2020 Microsoft Corporation. MIT licensed; see Assets/FluentSystemIcons.LICENSE.txt.
    // Upstream revision: 83b26348c811f072429427ff48362238656524fe
    // Source: https://github.com/microsoft/fluentui-system-icons/blob/83b26348c811f072429427ff48362238656524fe/assets/Open/SVG/ic_fluent_open_20_regular.svg
    private static GraphicsPath CreateOpen()
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.StartFigure();
        path.AddBezier(6F, 4F, 4.89543F, 4F, 4F, 4.89543F, 4F, 6F);
        path.AddLine(4F, 6F, 4F, 14F);
        path.AddBezier(4F, 14F, 4F, 15.1046F, 4.89543F, 16F, 6F, 16F);
        path.AddLine(6F, 16F, 14F, 16F);
        path.AddBezier(14F, 16F, 15.1046F, 16F, 16F, 15.1046F, 16F, 14F);
        path.AddLine(16F, 14F, 16F, 11.5F);
        path.AddBezier(16F, 11.5F, 16F, 11.2239F, 16.2239F, 11F, 16.5F, 11F);
        path.AddBezier(16.5F, 11F, 16.7761F, 11F, 17F, 11.2239F, 17F, 11.5F);
        path.AddLine(17F, 11.5F, 17F, 14F);
        path.AddBezier(17F, 14F, 17F, 15.6569F, 15.6569F, 17F, 14F, 17F);
        path.AddLine(14F, 17F, 6F, 17F);
        path.AddBezier(6F, 17F, 4.34315F, 17F, 3F, 15.6569F, 3F, 14F);
        path.AddLine(3F, 14F, 3F, 6F);
        path.AddBezier(3F, 6F, 3F, 4.34315F, 4.34315F, 3F, 6F, 3F);
        path.AddLine(6F, 3F, 8.5F, 3F);
        path.AddBezier(8.5F, 3F, 8.77614F, 3F, 9F, 3.22386F, 9F, 3.5F);
        path.AddBezier(9F, 3.5F, 9F, 3.77614F, 8.77614F, 4F, 8.5F, 4F);
        path.AddLine(8.5F, 4F, 6F, 4F);
        path.CloseFigure();
        path.StartFigure();
        path.AddBezier(11F, 3.5F, 11F, 3.22386F, 11.2239F, 3F, 11.5F, 3F);
        path.AddLine(11.5F, 3F, 16.5F, 3F);
        path.AddBezier(16.5F, 3F, 16.7761F, 3F, 17F, 3.22386F, 17F, 3.5F);
        path.AddLine(17F, 3.5F, 17F, 8.5F);
        path.AddBezier(17F, 8.5F, 17F, 8.77614F, 16.7761F, 9F, 16.5F, 9F);
        path.AddBezier(16.5F, 9F, 16.2239F, 9F, 16F, 8.77614F, 16F, 8.5F);
        path.AddLine(16F, 8.5F, 16F, 4.70711F);
        path.AddLine(16F, 4.70711F, 11.8536F, 8.85355F);
        path.AddBezier(11.8536F, 8.85355F, 11.6583F, 9.04882F, 11.3417F, 9.04882F, 11.1464F, 8.85355F);
        path.AddBezier(11.1464F, 8.85355F, 10.9512F, 8.65829F, 10.9512F, 8.34171F, 11.1464F, 8.14645F);
        path.AddLine(11.1464F, 8.14645F, 15.2929F, 4F);
        path.AddLine(15.2929F, 4F, 11.5F, 4F);
        path.AddBezier(11.5F, 4F, 11.2239F, 4F, 11F, 3.77614F, 11F, 3.5F);
        path.CloseFigure();
        return path;
    }

    // Source: https://github.com/microsoft/fluentui-system-icons/blob/83b26348c811f072429427ff48362238656524fe/assets/Folder/SVG/ic_fluent_folder_20_regular.svg
    private static GraphicsPath CreateFolder()
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.StartFigure();
        path.AddBezier(4.5F, 3F, 3.11929F, 3F, 2F, 4.11929F, 2F, 5.5F);
        path.AddLine(2F, 5.5F, 2F, 14.5F);
        path.AddBezier(2F, 14.5F, 2F, 15.8807F, 3.11929F, 17F, 4.5F, 17F);
        path.AddLine(4.5F, 17F, 15.5F, 17F);
        path.AddBezier(15.5F, 17F, 16.8807F, 17F, 18F, 15.8807F, 18F, 14.5F);
        path.AddLine(18F, 14.5F, 18F, 7.5F);
        path.AddBezier(18F, 7.5F, 18F, 6.11929F, 16.8807F, 5F, 15.5F, 5F);
        path.AddLine(15.5F, 5F, 9.70711F, 5F);
        path.AddLine(9.70711F, 5F, 8.21967F, 3.51256F);
        path.AddBezier(8.21967F, 3.51256F, 7.89148F, 3.18437F, 7.44636F, 3F, 6.98223F, 3F);
        path.AddLine(6.98223F, 3F, 4.5F, 3F);
        path.CloseFigure();
        path.StartFigure();
        path.AddBezier(3F, 5.5F, 3F, 4.67157F, 3.67157F, 4F, 4.5F, 4F);
        path.AddLine(4.5F, 4F, 6.98223F, 4F);
        path.AddBezier(6.98223F, 4F, 7.18115F, 4F, 7.37191F, 4.07902F, 7.51256F, 4.21967F);
        path.AddLine(7.51256F, 4.21967F, 8.79289F, 5.5F);
        path.AddLine(8.79289F, 5.5F, 7.43934F, 6.85355F);
        path.AddBezier(7.43934F, 6.85355F, 7.34557F, 6.94732F, 7.21839F, 7F, 7.08579F, 7F);
        path.AddLine(7.08579F, 7F, 3F, 7F);
        path.AddLine(3F, 7F, 3F, 5.5F);
        path.CloseFigure();
        path.StartFigure();
        path.AddLine(3F, 8F, 7.08579F, 8F);
        path.AddBezier(7.08579F, 8F, 7.48361F, 8F, 7.86514F, 7.84196F, 8.14645F, 7.56066F);
        path.AddLine(8.14645F, 7.56066F, 9.70711F, 6F);
        path.AddLine(9.70711F, 6F, 15.5F, 6F);
        path.AddBezier(15.5F, 6F, 16.3284F, 6F, 17F, 6.67157F, 17F, 7.5F);
        path.AddLine(17F, 7.5F, 17F, 14.5F);
        path.AddBezier(17F, 14.5F, 17F, 15.3284F, 16.3284F, 16F, 15.5F, 16F);
        path.AddLine(15.5F, 16F, 4.5F, 16F);
        path.AddBezier(4.5F, 16F, 3.67157F, 16F, 3F, 15.3284F, 3F, 14.5F);
        path.AddLine(3F, 14.5F, 3F, 8F);
        path.CloseFigure();
        return path;
    }

    // Source: https://github.com/microsoft/fluentui-system-icons/blob/83b26348c811f072429427ff48362238656524fe/assets/Folder%20Open/SVG/ic_fluent_folder_open_20_regular.svg
    private static GraphicsPath CreateFolderOpen()
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.StartFigure();
        path.AddLine(3.00026F, 5.5F, 3.00026F, 12.1041F);
        path.AddLine(3.00026F, 12.1041F, 4.50091F, 9.50488F);
        path.AddBezier(4.50091F, 9.50488F, 5.03681F, 8.57668F, 6.02719F, 8.00488F, 7.09898F, 8.00488F);
        path.AddLine(7.09898F, 8.00488F, 15.0003F, 8.00488F);
        path.AddLine(15.0003F, 8.00488F, 15.0003F, 7.5F);
        path.AddBezier(15.0003F, 7.5F, 15.0003F, 6.67157F, 14.3287F, 6F, 13.5003F, 6F);
        path.AddLine(13.5003F, 6F, 9.50026F, 6F);
        path.AddBezier(9.50026F, 6F, 9.36765F, 6F, 9.24047F, 5.94732F, 9.1467F, 5.85355F);
        path.AddLine(9.1467F, 5.85355F, 7.4396F, 4.14645F);
        path.AddBezier(7.4396F, 4.14645F, 7.34583F, 4.05268F, 7.21865F, 4F, 7.08604F, 4F);
        path.AddLine(7.08604F, 4F, 4.50026F, 4F);
        path.AddBezier(4.50026F, 4F, 3.67183F, 4F, 3.00026F, 4.67157F, 3.00026F, 5.5F);
        path.CloseFigure();
        path.StartFigure();
        path.AddBezier(4.28361F, 15.9845F, 4.35498F, 15.9947F, 4.42844F, 16F, 4.50373F, 16F);
        path.AddLine(4.50373F, 16F, 13.9012F, 16F);
        path.AddBezier(13.9012F, 16F, 14.6157F, 16F, 15.276F, 15.6188F, 15.6332F, 15F);
        path.AddLine(15.6332F, 15F, 17.7955F, 11.2549F);
        path.AddBezier(17.7955F, 11.2549F, 18.3728F, 10.2549F, 17.6511F, 9.00488F, 16.4964F, 9.00488F);
        path.AddLine(16.4964F, 9.00488F, 7.09898F, 9.00488F);
        path.AddBezier(7.09898F, 9.00488F, 6.38445F, 9.00488F, 5.7242F, 9.38608F, 5.36694F, 10.0049F);
        path.AddLine(5.36694F, 10.0049F, 3.20469F, 13.75F);
        path.AddBezier(3.20469F, 13.75F, 2.78223F, 14.4817F, 3.0553F, 15.3473F, 3.68462F, 15.7591F);
        path.AddBezier(3.68462F, 15.7591F, 3.86197F, 15.8742F, 4.06517F, 15.9529F, 4.28361F, 15.9845F);
        path.CloseFigure();
        path.StartFigure();
        path.AddLine(2.00026F, 14.4607F, 2.00026F, 5.5F);
        path.AddBezier(2.00026F, 5.5F, 2.00026F, 4.11929F, 3.11955F, 3F, 4.50026F, 3F);
        path.AddLine(4.50026F, 3F, 7.08604F, 3F);
        path.AddBezier(7.08604F, 3F, 7.48387F, 3F, 7.8654F, 3.15804F, 8.1467F, 3.43934F);
        path.AddLine(8.1467F, 3.43934F, 9.70736F, 5F);
        path.AddLine(9.70736F, 5F, 13.5003F, 5F);
        path.AddBezier(13.5003F, 5F, 14.881F, 5F, 16.0003F, 6.11929F, 16.0003F, 7.5F);
        path.AddLine(16.0003F, 7.5F, 16.0003F, 8.00488F);
        path.AddLine(16.0003F, 8.00488F, 16.4964F, 8.00488F);
        path.AddBezier(16.4964F, 8.00488F, 18.4209F, 8.00488F, 19.6237F, 10.0882F, 18.6615F, 11.7549F);
        path.AddLine(18.6615F, 11.7549F, 16.4992F, 15.5F);
        path.AddBezier(16.4992F, 15.5F, 15.9633F, 16.4282F, 14.973F, 17F, 13.9012F, 17F);
        path.AddLine(13.9012F, 17F, 4.50026F, 17F);
        path.AddBezier(4.50026F, 17F, 4.4378F, 17F, 4.37588F, 16.9977F, 4.31457F, 16.9932F);
        path.AddBezier(4.31457F, 16.9932F, 3.75344F, 16.9527F, 3.26124F, 16.7329F, 2.87665F, 16.4011F);
        path.AddBezier(2.87665F, 16.4011F, 2.36561F, 15.9642F, 2.03283F, 15.3249F, 2.00252F, 14.6073F);
        path.AddBezier(2.00252F, 14.6073F, 2.00031F, 14.5586F, 1.99955F, 14.5097F, 2.00026F, 14.4607F);
        path.CloseFigure();
        return path;
    }

}
