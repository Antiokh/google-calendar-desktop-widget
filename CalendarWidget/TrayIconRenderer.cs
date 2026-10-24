// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CalendarWidget;

/// <summary>
/// Monochrome taskbar calendar date, with no badge, sheet, or background.
/// The filled day number is always visible. Effective interactive mode adds
/// a one-pixel rectangular border around the icon, NOT a glyph outline.
/// Follows Windows' SYSTEM/taskbar theme
/// rather than AppsUseLightTheme, which can differ in custom light/dark mode.
/// </summary>
internal static class TrayIconRenderer
{
    // Render at 4x the usual 16px notification area icon so downsampling
    // produces smooth numerals. Four source pixels equal one displayed pixel.
    private const int Size = 64;
    private const float FrameWidth = 4f; // exactly 1px at the 16px tray size
    private const string ThemeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Color GetTaskbarTextColor()
    {
        // High contrast takes precedence over dark/light theme preferences.
        if (SystemInformation.HighContrast)
            return SystemColors.WindowText;

        try
        {
            // 0 = dark Windows taskbar (light clock text); 1 = light taskbar.
            // The app's own AppsUseLightTheme flag is NOT what controls the tray.
            if (Registry.GetValue(ThemeKey, "SystemUsesLightTheme", null) is int light)
                return light == 0
                    ? Color.FromArgb(245, 245, 245)
                    : Color.FromArgb(32, 32, 32);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                  or UnauthorizedAccessException or IOException)
        {
            // Restricted registry access: use a legible default for the
            // standard dark taskbar, without breaking tray functionality.
        }
        return Color.FromArgb(245, 245, 245);
    }

    public static Icon Create(int day, bool interactive, Color ink)
    {
        if (day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(day));

        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            path.AddString(day.ToString(CultureInfo.InvariantCulture), family,
                (int)FontStyle.Bold, 53f, PointF.Empty, format);

            // Center the actual numeral contour (not its font line box).
            // Keep the glyph inside the optional 1px outer frame, with a
            // small clear gap between digit and border at 16px scale.
            var bounds = path.GetBounds();
            float scale = Math.Min(46f / bounds.Width, 46f / bounds.Height);
            using var centered = new Matrix();
            centered.Translate(-bounds.Left, -bounds.Top);
            centered.Scale(scale, scale, MatrixOrder.Append);
            centered.Translate((Size - bounds.Width * scale) / 2f,
                (Size - bounds.Height * scale) / 2f, MatrixOrder.Append);
            path.Transform(centered);

            // Both modes draw the same solid digit(s).
            using var fill = new SolidBrush(ink);
            g.FillPath(fill, path);

            if (interactive)
            {
                // One display pixel wide at 16x16, inset just enough that
                // GDI+'s centered stroke remains fully inside the icon.
                // No mouse pointer, extra badge, glyph outline or opaque tile.
                using var frame = new Pen(ink, FrameWidth);
                g.DrawRectangle(frame, 3f, 3f, Size - 6f, Size - 6f);
            }
        }

        // GetHicon allocates a native HICON; Icon.FromHandle does not own it.
        // Clone so NotifyIcon can keep it after we destroy the temporary HICON.
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
}
