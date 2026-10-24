// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CalendarWidget;

/// <summary>
/// A dynamic Windows tray icon: calendar sheet, small 'G' and current local
/// day of month. A conventional pointer overlays the lower-right corner
/// only while calendar interaction is enabled.
///
/// Drawn at 64px, so the day stays legible on common 16/24/32px taskbar sizes.
/// The caller owns the returned Icon and must dispose it when replacing it.
/// </summary>
internal static class TrayIconRenderer
{
    private const int Size = 64;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(int day, bool interactive)
    {
        if (day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(day));

        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var outline = RoundRectangle(new RectangleF(3, 2, 58, 60), 9);
            using var paper = new SolidBrush(Color.FromArgb(250, 252, 255));
            using var header = new SolidBrush(Color.FromArgb(50, 112, 222));
            using var border = new Pen(Color.FromArgb(30, 51, 86), 2.4f);
            g.FillPath(paper, outline);

            // Clip the colored header to the calendar's rounded silhouette.
            var state = g.Save();
            g.SetClip(outline);
            g.FillRectangle(header, 3, 2, 58, 22);
            g.Restore(state);
            g.DrawPath(border, outline);

            using var align = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            using var glyphFont = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var dateFont = new Font("Segoe UI", 36, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            using var ink = new SolidBrush(Color.FromArgb(29, 42, 70));

            g.DrawString("G", glyphFont, white, new RectangleF(3, 3, 58, 21), align);
            g.DrawString(day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dateFont, ink, new RectangleF(4, 24, 56, 36), align);

            if (interactive)
            {
                // High-contrast mouse pointer, recognizable even when the tray
                // icon is reduced to 16x16. A small overlay leaves the day readable.
                var vertices = new[]
                {
                    new PointF(42, 39), new PointF(42, 59), new PointF(47, 54),
                    new PointF(51, 62), new PointF(56, 59), new PointF(51, 51),
                    new PointF(59, 49),
                };
                using var pointer = new GraphicsPath();
                pointer.AddLines(vertices);
                pointer.CloseFigure();
                using var halo = new Pen(Color.FromArgb(18, 25, 40), 3.5f)
                { LineJoin = LineJoin.Round };
                g.FillPath(white, pointer);
                g.DrawPath(halo, pointer);
            }
        }

        // Bitmap.GetHicon allocates a native HICON. FromHandle does not own it.
        // Clone to create a managed Icon, then destroy the temporary HICON.
        var hIcon = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundRectangle(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
