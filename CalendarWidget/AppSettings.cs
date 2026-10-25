// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarWidget;

public class AppSettings
{
    public int Transparency { get; set; } = 220;  // stored as window alpha, 13..255

    /// <summary>UI-facing value: how transparent the widget is, 0..95 % (95 keeps it faintly visible).</summary>
    [JsonIgnore]
    public int TransparencyPercent
    {
        get => Math.Clamp((int)Math.Round((1 - Transparency / 255.0) * 100), 0, 95);
        set => Transparency = (int)Math.Round(255 * (100 - Math.Clamp(value, 0, 95)) / 100.0);
    }

    // Layout options are independent of the interactive/pass-through mode.
    public bool ShowTitleBar { get; set; } = true;
    public bool LayoutUnlocked { get; set; } = true;
    public bool UseDateTrayIcon { get; set; } // app icon remains default

    // Optional interactive visibility while Windows Show Desktop is active.
    public bool KeepInteractiveVisibleOnDesktop { get; set; } = true;

    // widget mode sits behind the desktop icons (live-wallpaper style, WorkerW reparenting)
    public bool BehindDesktopIcons { get; set; }

    // widget mode darkens the calendar's edges (fading from black to clear) so desktop icons
    // on top read against a smooth backdrop instead of calendar text; width in % of the
    // calendar, 0 = off. Their sum stays <= 100 - EdgeShadowSlider.MinGap.
    public int EdgeShadowLeftPercent { get; set; }
    public int EdgeShadowRightPercent { get; set; }

    // hover panel in a screen corner (click-through mode); redundant with the title bar, so toggleable
    public bool CornerPanelEnabled { get; set; } = true;

    // which screen corner hosts the hover panel: BottomRight, BottomLeft, TopRight, TopLeft
    public string PanelCorner { get; set; } = "BottomRight";

    // last background color seen under the title bar ("#RRGGBB"), so the chrome starts in the
    // calendar's light/dark theme instead of flashing the default until the page loads
    public string? ThemeColor { get; set; }

    // saved window bounds (borderless state); Width == 0 means "not set yet"
    public int WinX { get; set; }
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool WinMaximized { get; set; }

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalendarWidget");

    public static string WebViewDataFolder => Path.Combine(DataFolder, "WebView2Data");

    private static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>True when no settings file existed yet (used to start in interactive mode for sign-in).</summary>
    public static bool IsFirstRun => !File.Exists(SettingsFile);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch
        {
            // corrupt settings fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // settings persistence is best-effort; never crash the widget over it
        }
    }
}
