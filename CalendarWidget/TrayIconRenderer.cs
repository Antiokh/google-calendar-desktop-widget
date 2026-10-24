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
/// The filled day number is always visible at its original large size.
/// Effective interactive mode uses two top-corner pixels; when the title bar
/// is visible the pixels become a single one-pixel top row.
/// Follows Windows' SYSTEM/taskbar theme
/// rather than AppsUseLightTheme, which can differ in custom light/dark mode.
/// </summary>
internal static class TrayIconRenderer
{
    // Render at 4x the usual 16px notification area icon so downsampling
    // produces smooth numerals. Four source pixels equal one displayed pixel.
    private const int Size = 64;
    private const int StatusPixel = Size / 16; // one pixel in the 16x16 tray rendering
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

    public static Icon Create(int day, bool interactive, bool titleBarVisible, Color ink)
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

            // Center the actual numeral contour, not the font line box.
            // Restore the original large 52px glyph; the status indicator
            // lives in the top pixel row and never shrinks the date.
            var bounds = path.GetBounds();
            float scale = Math.Min(52f / bounds.Width, 52f / bounds.Height);
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
                // Keep the digits identical in both modes. The interaction
                // indicator occupies only the icon's first 16px-scaled row:
                // a dot at either corner, or a full line while the title bar
                // is shown. No frame around the glyphs or the remaining edges.
                if (titleBarVisible)
                    g.FillRectangle(fill, 0, 0, Size, StatusPixel);
                else
                {
                    g.FillRectangle(fill, 0, 0, StatusPixel, StatusPixel);
                    g.FillRectangle(fill, Size - StatusPixel, 0,
                        StatusPixel, StatusPixel);
                }
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
