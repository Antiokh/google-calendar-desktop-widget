// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CalendarWidget;

public class MainForm : Form
{
    private const string CalendarUrl = "https://calendar.google.com/calendar/u/0/r";

    private readonly AppSettings settings = AppSettings.Load();
    private readonly bool firstRun = AppSettings.IsFirstRun;
    private readonly WebView2 webView = new()
    {
        Dock = DockStyle.Fill,
        DefaultBackgroundColor = Color.FromArgb(32, 33, 36),  // no white flash while pages load
    };
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer hoverPoll = new() { Interval = 100 };
    private readonly HoverPanel hoverPanel;
    private readonly TitleBar titleBar;
    private SettingsForm? settingsForm;
    private WebView2? stripBackdrop;      // the strip's color behind the desktop icons: see EnsureStripBackdrop

    private Theme theme;                  // chrome colors, following the calendar page's background
    private bool isClickThrough;
    private bool isLayoutUnlocked;  // persisted independently of click-through
    private bool previewLook;             // interactive mode showing the widget look while Settings is open
    private DateTime? hoverHideDeadline;  // set while the panel is visible but the mouse has left it
    private int guardTick;                // periodic re-assert of click-through styles
    private int styleBurst;               // fast re-assert right after enabling (Chrome recreates windows)
    private int attachCheckTick;
    private bool nativeMoveSizeActive;
    private bool desktopDiscoveryInFlight;
    private int desktopDiscoveryFailures;
    private long nextDesktopDiscoveryAt;
    private bool pendingInteractiveDetach;
    private bool pendingWallpaperDetach;
    // SetParent can succeed although a later style/frame refresh fails.
    // Retry style repair independently from wallpaper mode and interaction.
    private bool pendingTopLevelStyleRepair;
    private bool pendingAttachedStyleRepair;
    private long nextTopLevelStyleRepairAt;
    private bool desktopStateSnapshotValid;
    private bool reparentInProgress;
    private int reparentGeneration;          // throttle for the behind-icons re-attach watchdog
    private bool attachedToDesktop;       // reparented into WorkerW (behind the desktop icons)
    private IntPtr attachedWorkerW;       // the specific WorkerW we parented into; its destruction = orphaned
    private Rectangle boundsBeforeAttach; // screen bounds to restore on detach (child coords are parent-relative)
    private bool maximizedBeforeAttach;   // attached windows are always Normal; re-maximize on detach
    private Rectangle normalBoundsBeforeAttach;  // the pre-maximize rect to give back to RestoreBounds
    private readonly HashSet<IntPtr> clickThroughApplied = [];  // windows WE made transparent

    public MainForm()
    {
        Text = "Google Calendar Desktop Widget";
        FormBorderStyle = FormBorderStyle.None;  // always borderless; TitleBar is the caption in interactive mode
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = InitialBounds();
        isLayoutUnlocked = settings.LayoutUnlocked;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }

        // the bar is a separate owned window floating over the form's top edge; the padding
        // reserves that strip so the calendar never renders underneath it. The bar sizes
        // itself for its monitor's DPI and reports changes, so the strip always matches.
        titleBar = new TitleBar(this, ToggleClickThrough, ShowSettings, ToggleMaximize);
        titleBar.BarHeightChanged += (_, _) =>
        {
            ApplyPadding();
            titleBar.Reposition();
        };
        titleBar.SetLayoutUnlocked(isLayoutUnlocked);
        ApplyPadding();
        Controls.Add(webView);

        hoverPanel = new HoverPanel(ToggleClickThrough, ShowSettings);

        // last known calendar theme, so the chrome starts in the right colors before the page loads
        theme = Theme.FromHex(settings.ThemeColor) ?? Theme.Dark;
        ApplyThemeColors();

        SetupTray();
        hoverPoll.Tick += (_, _) => CheckMouseHover();
        hoverPoll.Start();
    }

    // native window behaviors (Aero Snap, drag-to-top / double-click maximize) require these
    // styles; the frame and caption they'd normally draw are removed in WM_NCCALCSIZE below
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MAXIMIZEBOX;
            return cp;
        }
    }

    public void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Normal) SaveBounds();
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        SaveBounds();
        settings.Save();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        titleBar?.Reposition();
        // Win+Arrow / Aero Snap may change position without OnResizeEnd.
        // Cache it now; persist to disk at exit or explicit layout actions.
        if (IsHandleCreated)
            SaveBounds();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        titleBar?.Reposition();
        // Capture keyboard/system maximize, restore and snap transitions.
        if (IsHandleCreated)
            SaveBounds();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveBounds();
        settings.Save();
    }

    // Validate accessibility on launch or monitor changes, not continuously.
    private static Rectangle RecoverAccessibleBounds(Rectangle saved)
    {
        if (saved.Width <= 0 || saved.Height <= 0)
            return Screen.PrimaryScreen!.WorkingArea;
        long area = (long)saved.Width * saved.Height;
        long visible = 0;
        foreach (var screen in Screen.AllScreens)
        {
            var intersection = Rectangle.Intersect(saved, screen.WorkingArea);
            visible += (long)intersection.Width * intersection.Height;
        }
        bool topReachable = Screen.AllScreens.Any(screen =>
            screen.WorkingArea.IntersectsWith(
                new Rectangle(saved.X, saved.Y, Math.Min(160, saved.Width), Math.Min(34, saved.Height))));
        if (visible * 5 >= area * 4 && topReachable) return saved;
        var work = Screen.FromRectangle(saved).WorkingArea;
        int w = Math.Clamp(saved.Width, Math.Min(320, work.Width), work.Width);
        int h = Math.Clamp(saved.Height, Math.Min(200, work.Height), work.Height);
        return new Rectangle(Math.Clamp(saved.X, work.Left, work.Right - w),
            Math.Clamp(saved.Y, work.Top, work.Bottom - h), w, h);
    }

    private Rectangle InitialBounds()
    {
        if (settings.WinW > 0 && settings.WinH > 0)
            return RecoverAccessibleBounds(new Rectangle(
                settings.WinX, settings.WinY, settings.WinW, settings.WinH));

        // default: right side of the primary work area (no handle yet: DeviceDpi is the primary's)
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int gap = LogicalToDeviceUnits(20);
        int w = Math.Min(LogicalToDeviceUnits(1000), wa.Width / 2);
        return new Rectangle(wa.Right - w - gap, wa.Top + gap, w, wa.Height - 2 * gap);
    }

    // moved to a monitor with another scale: keep the resize frame matched to it (the bar
    // rescales itself on its own DPI change and reports back through BarHeightChanged)
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyPadding();
        titleBar.Reposition();
    }

    /// <summary>
    /// The title bar's strip on top; in interactive mode also a thin side/bottom frame that
    /// doubles as resize grips (see WM_NCHITTEST). Scaled to the current DPI.
    /// </summary>
    private Padding ModePadding()
    {
        // Layout editing never overrides the explicit Show title bar preference.
        int bar = settings.ShowTitleBar ? titleBar.BarHeight : 0;
        if (isClickThrough)
            return new Padding(0, bar, 0, 0);
        int frame = isLayoutUnlocked ? LogicalToDeviceUnits(6) : 0;
        return new Padding(frame, bar, frame, frame);
    }

    private void ApplyPadding()
    {
        Padding = ModePadding();
        if (stripBackdrop is not null)
        {
            stripBackdrop.Visible = settings.ShowTitleBar && isClickThrough;
            stripBackdrop.SetBounds(0, 0, ClientSize.Width,
                stripBackdrop.Visible ? titleBar.BarHeight : 0);
        }
    }

    /// <summary>
    /// Behind the desktop icons only DirectComposition content shows: on current Windows 11
    /// the desktop window (Progman) has no GDI surface (WS_EX_NOREDIRECTIONBITMAP), so inside
    /// the WorkerW nothing GDI-painted is visible — not even in a layered child — and the
    /// strip beside the title bar's buttons (this form's BackColor) shows the wallpaper
    /// instead. A WebView2 that never navigates renders only its DefaultBackgroundColor,
    /// through DirectComposition like the calendar, so it fills the strip in the theme color,
    /// takes the widget's opacity and sits under the icons. It costs one small renderer
    /// process, so it's created on the first attach only, then kept (elsewhere it looks
    /// identical to the painted strip).
    /// </summary>
    private async void EnsureStripBackdrop()
    {
        var env = webView.CoreWebView2?.Environment;
        if (stripBackdrop is not null || env is null)
            return;  // no environment yet: OnLoad calls again once the calendar's WebView is up
        stripBackdrop = new WebView2
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            DefaultBackgroundColor = theme.Back,
            TabStop = false,
        };
        Controls.Add(stripBackdrop);
        ApplyPadding();
        try
        {
            await stripBackdrop.EnsureCoreWebView2Async(env);
        }
        catch (Exception)
        {
            // purely cosmetic: without it the strip just shows the wallpaper; retry on next attach
            Controls.Remove(stripBackdrop);
            stripBackdrop.Dispose();
            stripBackdrop = null;
            return;
        }
        // its Chrome windows were created just now: make them click-through like the rest
        if (isClickThrough)
        {
            NativeMethods.EnableClickThrough(Handle, clickThroughApplied);
            styleBurst = 20;
        }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // start the bar on the owner's monitor so it is created at that monitor's DPI
        titleBar.Location = PointToScreen(Point.Empty);
        titleBar.Show(this);  // owned controls can be available in either mode
        UpdateHeaderVisibility();

        if (settings.WinMaximized)
            WindowState = FormWindowState.Maximized;

        // first run: stay interactive so the user can sign in to Google;
        // afterwards the widget always starts in click-through mode
        SetClickThrough(!firstRun);
        if (firstRun)
        {
            tray.BalloonTipTitle = "Google Calendar Desktop Widget";
            tray.BalloonTipText = "Sign in to Google Calendar, then use the tray menu or the "
                + "hover panel (bottom-right of the screen) to enable click-through mode.";
            tray.ShowBalloonTip(10000);
        }

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppSettings.WebViewDataFolder);
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ThemeProbeScript);
            webView.CoreWebView2.DOMContentLoaded += (_, _) => ApplyEdgeShadows();
            webView.CoreWebView2.Navigate(CalendarUrl);

            // WebView2's child windows didn't exist during the startup toggle; cover them now
            if (isClickThrough)
                NativeMethods.EnableClickThrough(Handle, clickThroughApplied);
            if (attachedToDesktop)
                EnsureStripBackdrop();  // the startup attach ran before there was a WebView2 environment
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to start the embedded browser (WebView2 runtime missing?):\n\n" + ex.Message,
                "Google Calendar Desktop Widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }
    }

    // ---------------- click-through / interactive ----------------

    public bool CalendarInteractionEnabled => !isClickThrough;

    public void ToggleClickThrough() => SetClickThrough(!isClickThrough);
    public void SetCalendarInteraction(bool enabled)
    {
        if (enabled != !isClickThrough)
            SetClickThrough(!enabled);
    }

    public void SetLayoutEditing(bool enabled)
    {
        if (isLayoutUnlocked == enabled) return;
        isLayoutUnlocked = enabled;
        settings.LayoutUnlocked = enabled;
        ApplyPadding();
        UpdateHeaderVisibility();
        settings.Save();
        SyncSettingsControls();
    }

    public void SetHeaderVisible(bool visible)
    {
        settings.ShowTitleBar = visible;
        ApplyPadding();
        UpdateHeaderVisibility();
        if (visible && attachedToDesktop) EnsureStripBackdrop();
        settings.Save();
        SyncSettingsControls();
    }

    private void UpdateHeaderVisibility()
    {
        titleBar.SetLayoutUnlocked(isLayoutUnlocked && !isClickThrough);
        if (!IsHandleCreated) return;
        if (settings.ShowTitleBar)
        {
            if (!titleBar.Visible)
            {
                titleBar.Location = PointToScreen(Point.Empty);
                titleBar.Show(this);
            }
            titleBar.Reposition();
        }
        else if (titleBar.Visible)
            titleBar.Hide();
    }

    // With no title bar, the system move command is available via tray/Settings.
    public void BeginKeyboardMove()
    {
        if (isClickThrough || !isLayoutUnlocked || !IsHandleCreated)
            return;
        bool restoreSettings = settingsForm is { Visible: true };
        if (restoreSettings)
            settingsForm!.Hide();
        BeginInvoke(() =>
        {
            if (!IsDisposed && !isClickThrough && isLayoutUnlocked)
            {
                Activate();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_SYSCOMMAND,
                    new IntPtr(NativeMethods.SC_MOVE), IntPtr.Zero);
            }
            if (restoreSettings && settingsForm is { IsDisposed: false })
                settingsForm.ShowAndActivate();
        });
    }

    private void SyncSettingsControls() =>
        settingsForm?.SyncTrayOptions(!isClickThrough, isLayoutUnlocked,
            settings.ShowTitleBar, settings.BehindDesktopIcons);

    private void SetClickThrough(bool enable)
    {
        if (enable)
        {
            pendingInteractiveDetach = false;
            if (!settings.BehindDesktopIcons && attachedToDesktop)
                pendingWallpaperDetach = true;
            ResetDesktopDiscoveryBackoff();
        }
        isClickThrough = enable;
        previewLook = false;  // widget mode shows the real look; interactive starts opaque again
        if (enable)
        {
            SaveBounds();  // no-op while maximized: the last normal bounds stay saved
            ApplyPadding();  // widget mode: no resize frame
            // opacity first: going layered makes Chrome recreate its input windows, which
            // would shed a just-applied WS_EX_TRANSPARENT. SetOpacity then styles the whole
            // tree and keeps re-asserting on every poll tick (~100ms) for the next ~2s to
            // catch any window Chrome recreates asynchronously — only this window's tree
            // goes transparent; the owned TitleBar stays clickable.
            SetOpacity(settings.Transparency / 255.0);
            NativeMethods.SendToBottom(Handle);
            if (settings.BehindDesktopIcons)
                AttachToDesktop();
        }
        else
        {
            if (!DetachFromDesktop())
            {
                isClickThrough = true;
                pendingInteractiveDetach = true;
                RecordDesktopDiscoveryFailure();
                hoverPanel.UpdateState(true);
                titleBar.UpdateState(true, Opacity);
                return;
            }
            pendingInteractiveDetach = false;
            pendingWallpaperDetach = false;
            ResetDesktopDiscoveryBackoff();
            styleBurst = 0;
            NativeMethods.DisableClickThrough(clickThroughApplied);
            NativeMethods.RemoveExStyle(Handle, NativeMethods.WS_EX_NOACTIVATE);
            SetOpacity(1.0);
            ApplyPadding();  // side/bottom frame = resize grips
            hoverPanel.Hide();
            Activate();
        }
        hoverPanel.UpdateState(enable);
        titleBar.UpdateState(enable, Opacity);
        UpdateHeaderVisibility();
        titleBar.Reposition();
        SyncSettingsControls();
        ApplyEdgeShadows();  // widget mode only: they'd hide content while interacting
        settings.Save();
    }

    // pin the window to the bottom of the Z-order: any attempt to raise it
    // (taskbar click, alt-tab, shell repositioning) is rewritten to HWND_BOTTOM
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_ENTERSIZEMOVE)
            nativeMoveSizeActive = true;
        else if (m.Msg == NativeMethods.WM_EXITSIZEMOVE)
            nativeMoveSizeActive = false;

        if (m.Msg == NativeMethods.WM_WINDOWPOSCHANGING && isClickThrough && m.LParam != IntPtr.Zero)
        {
            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(m.LParam);
            wp.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
            wp.flags &= ~NativeMethods.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, m.LParam, false);
        }

        // claim the caption/frame area as client: removes the native title bar visuals
        // while keeping the WS_CAPTION/WS_THICKFRAME behaviors (snap, maximize).
        // When maximized, Windows hangs the invisible frame outside the monitor, so the
        // client must be inset by the frame thickness or content edges get cut off.
        if (m.Msg == NativeMethods.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            if (NativeMethods.IsZoomed(Handle) && m.LParam != IntPtr.Zero)
            {
                var rc = Marshal.PtrToStructure<NativeMethods.RECT>(m.LParam);
                var (fx, fy) = NativeMethods.MaximizedFrameThickness(Handle);
                rc.Left += fx;
                rc.Top += fy;
                rc.Right -= fx;
                rc.Bottom -= fy;
                Marshal.StructureToPtr(rc, m.LParam, false);
            }
            m.Result = IntPtr.Zero;
            return;
        }

        // Lock both custom and native borderless resize affordances.
        if (m.Msg == NativeMethods.WM_SYSCOMMAND && !isClickThrough && !isLayoutUnlocked)
        {
            int command = unchecked((int)(m.WParam.ToInt64() & 0xFFF0));
            if (command is NativeMethods.SC_MOVE or NativeMethods.SC_SIZE
                or NativeMethods.SC_MAXIMIZE ||
                (command == NativeMethods.SC_RESTORE && NativeMethods.IsZoomed(Handle)))
            {
                m.Result = IntPtr.Zero;
                return;
            }
        }

        // borderless resize: the padding frame around the webview doubles as resize grips
        if (m.Msg == NativeMethods.WM_NCHITTEST && !isClickThrough)
        {
            base.WndProc(ref m);
            if (!isLayoutUnlocked)
            {
                if ((int)m.Result is NativeMethods.HTCAPTION
                    or NativeMethods.HTLEFT or NativeMethods.HTRIGHT
                    or NativeMethods.HTTOP or NativeMethods.HTBOTTOM
                    or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT
                    or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
                    m.Result = new IntPtr(NativeMethods.HTCLIENT);
                return;
            }
            if (NativeMethods.IsZoomed(Handle)) return;
            if ((int)m.Result == NativeMethods.HTCLIENT)
            {
                int x = unchecked((short)(long)m.LParam);
                int y = unchecked((short)((long)m.LParam >> 16));
                var pt = PointToClient(new Point(x, y));
                int grip = LogicalToDeviceUnits(8);
                bool left = pt.X < grip, right = pt.X >= ClientSize.Width - grip;
                bool top = pt.Y < grip, bottom = pt.Y >= ClientSize.Height - grip;
                int hit =
                    top && left ? NativeMethods.HTTOPLEFT :
                    top && right ? NativeMethods.HTTOPRIGHT :
                    bottom && left ? NativeMethods.HTBOTTOMLEFT :
                    bottom && right ? NativeMethods.HTBOTTOMRIGHT :
                    left ? NativeMethods.HTLEFT :
                    right ? NativeMethods.HTRIGHT :
                    top ? NativeMethods.HTTOP :
                    bottom ? NativeMethods.HTBOTTOM :
                    NativeMethods.HTCLIENT;
                m.Result = new IntPtr(hit);
            }
            return;
        }

        // a display-config change (resolution, monitor add/remove/power) recreates the
        // shell's WorkerW; re-attach promptly rather than waiting for the 1s watchdog
        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE)
            BeginInvoke(ReconcileDisplayConfiguration);

        base.WndProc(ref m);
    }

    // WinForms recreates the handle for some property changes; re-apply manual styles if so
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        nativeMoveSizeActive = false;
        NativeMethods.ApplyRoundedCorners(Handle);  // borderless windows get square corners by default
        if (isClickThrough)
        {
            NativeMethods.AddExStyle(Handle, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.SendToBottom(Handle);
        }
    }

    public void SetTransparencyPercent(int percent)
    {
        settings.TransparencyPercent = percent;
        if (!isClickThrough)
            previewLook = true;  // interactive mode: show the result while Settings is open
        ApplyLook();
        settings.Save();
    }

    /// <summary>
    /// Window alpha. Switching between opaque and translucent makes WinForms rewrite the
    /// ex-style from CreateParams — wiping our manual WS_EX_NOACTIVATE/TRANSPARENT — and
    /// makes Chrome recreate its input windows, so widget mode re-asserts both right after.
    /// </summary>
    private void SetOpacity(double value)
    {
        try
        {
            Opacity = value;
        }
        catch (Win32Exception)
        {
            // SetLayeredWindowAttributes refused — e.g. layering a child of the desktop's
            // WorkerW without Windows 8+ semantics (see app.manifest). Transparency is
            // cosmetic; it must never take the widget down.
        }
        if (isClickThrough)
        {
            NativeMethods.AddExStyle(Handle, NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.EnableClickThrough(Handle, clickThroughApplied);
            styleBurst = 20;
        }
    }

    /// <summary>
    /// The widget look — transparency and edge shadows. On in widget mode; in interactive
    /// mode only while Settings previews a change (until it closes: <see cref="EndPreview"/>).
    /// </summary>
    private void ApplyLook()
    {
        SetOpacity(isClickThrough || previewLook ? settings.Transparency / 255.0 : 1.0);
        titleBar.UpdateState(isClickThrough, Opacity);
        ApplyEdgeShadows();
    }

    /// <summary>Settings closed: interactive mode drops the previewed widget look again.</summary>
    public void EndPreview()
    {
        if (!previewLook || IsDisposed)
            return;
        previewLook = false;
        ApplyLook();
    }

    // ---------------- theme (chrome follows the calendar's light/dark look) ----------------

    /// <summary>
    /// Injected into every page: reports the first opaque background behind the page's
    /// top-left corner — the color right under the title bar — whenever it changes, so the
    /// bar, the strip beside it and the frame match Google Calendar's light or dark theme
    /// (also on the sign-in pages). Polled because a theme switch can restyle the page
    /// without a navigation; one getComputedStyle walk per second is negligible.
    /// </summary>
    internal const string ThemeProbeScript = """
        (() => {
          if (window !== window.top || !window.chrome?.webview) return;
          const opaque = c => {
            const m = /^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$/.exec(c);
            return m && (m[4] === undefined || +m[4] >= 0.9) ? [+m[1], +m[2], +m[3]] : null;
          };
          let last = '';
          const probe = () => {
            let rgb = null;
            // pointer-events:none overlays (our edge shadows) are skipped by elementFromPoint
            for (let el = document.elementFromPoint(1, 1); el && !rgb; el = el.parentElement)
              rgb = opaque(getComputedStyle(el).backgroundColor);
            for (const el of [document.body, document.documentElement])
              if (!rgb && el) rgb = opaque(getComputedStyle(el).backgroundColor);
            if (!rgb || rgb.join() === last) return;
            last = rgb.join();
            window.chrome.webview.postMessage({ theme: rgb });
          };
          addEventListener('DOMContentLoaded', probe);
          addEventListener('load', probe);
          setInterval(probe, 1000);
        })();
        """;

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // {"theme":[r,g,b]} from ThemeProbeScript
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("theme", out var rgb) && rgb.GetArrayLength() == 3)
                SetTheme(Theme.FromBackground(Color.FromArgb(Channel(rgb[0]), Channel(rgb[1]), Channel(rgb[2]))));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // not a theme message
        }

        static int Channel(JsonElement v) => Math.Clamp(v.GetInt32(), 0, 255);
    }

    private void SetTheme(Theme t)
    {
        if (t == theme)
            return;
        theme = t;
        ApplyThemeColors();
        settings.ThemeColor = t.ToHex();
        settings.Save();
    }

    private void ApplyThemeColors()
    {
        BackColor = theme.Back;                     // the strip beside the bar's buttons + the resize frame
        webView.DefaultBackgroundColor = theme.Back;  // shown while a page loads: no off-theme flash
        if (stripBackdrop is not null)
            stripBackdrop.DefaultBackgroundColor = theme.Back;  // the same strip, behind the desktop icons
        titleBar.ApplyTheme(theme);
        hoverPanel.ApplyTheme(theme);
    }

    // ---------------- edge shadows (icon backdrop) ----------------

    // cosine ease from opaque black to clear: no visible edge where the fade starts or ends.
    // Invariant formatting — a decimal comma would silently break the CSS.
    private static readonly string ShadowStops = string.Join(", ", Enumerable.Range(0, 11).Select(i =>
        FormattableString.Invariant($"rgba(0,0,0,{(1 + Math.Cos(Math.PI * i / 10)) / 2:0.###}) {i * 10}%")));

    /// <summary>
    /// Widget mode: dark gradients over the calendar's left/right edges so desktop icons on
    /// top of it read against a smooth backdrop instead of calendar text. Drawn INSIDE the
    /// page as fixed, pointer-events:none overlays — a WinForms control can't be translucent
    /// over WebView2's own HWND. Re-run on every DOMContentLoaded, since a navigation
    /// (sign-in, reload) starts a fresh document without them.
    /// </summary>
    private void ApplyEdgeShadows()
    {
        if (webView.CoreWebView2 is null)
            return;
        bool widgetLook = isClickThrough || previewLook;
        int left = widgetLook ? Math.Clamp(settings.EdgeShadowLeftPercent, 0, 100) : 0;
        int right = widgetLook ? Math.Clamp(settings.EdgeShadowRightPercent, 0, 100) : 0;
        // style via CSSOM properties (not a <style> tag or style attribute): Google's CSP allows it
        _ = webView.CoreWebView2.ExecuteScriptAsync($$"""
            (() => {
              const shade = (id, side, width) => {
                let el = document.getElementById(id);
                if (!width) { el?.remove(); return; }
                if (!el) {
                  el = document.createElement('div');
                  el.id = id;
                  document.documentElement.appendChild(el);
                }
                Object.assign(el.style, {
                  position: 'fixed', top: '0', bottom: '0', [side]: '0', width: width + 'vw',
                  pointerEvents: 'none', zIndex: '2147483647',
                  background: `linear-gradient(to ${side === 'left' ? 'right' : 'left'}, {{ShadowStops}})`,
                });
              };
              shade('cw-shadow-left', 'left', {{left}});
              shade('cw-shadow-right', 'right', {{right}});
            })();
            """);
    }

    public void SetEdgeShadows(int leftPercent, int rightPercent)
    {
        settings.EdgeShadowLeftPercent = leftPercent;
        settings.EdgeShadowRightPercent = rightPercent;
        if (!isClickThrough)
            previewLook = true;  // interactive mode: show the result while Settings is open
        ApplyLook();
        settings.Save();
    }

    // ---------------- live-wallpaper mode (behind desktop icons) ----------------

    private static bool NeedsOffscreenRecovery(Rectangle rect) =>
        RecoverAccessibleBounds(rect) != rect;

    private void FinishReparentTransition(int generation)
    {
        if (generation != reparentGeneration)
            return;
        reparentInProgress = false;
        NativeMethods.SetRedraw(Handle, true);
        NativeMethods.RepaintAll(Handle);
    }

    private void CancelReparentTransition()
    {
        ++reparentGeneration;  // invalidates any queued callback
        reparentInProgress = false;
        NativeMethods.SetRedraw(Handle, true);
        NativeMethods.RepaintAll(Handle);
    }

    /// <summary>
    /// WorkerW inherits Explorer's DPI context, which may differ from the
    /// physical monitor displaying this child after a hot-plug. Apply the
    /// effective screen-to-parent DPI ratio only when it actually changed.
    /// </summary>
    private void UpdateWallpaperZoom()
    {
        if (!attachedToDesktop || !IsHandleCreated)
            return;
        double target = NativeMethods.MonitorDpiAt(boundsBeforeAttach)
                        / (double)Math.Max(1, NativeMethods.GetDpiForWindow(Handle));
        try
        {
            if (Math.Abs(webView.ZoomFactor - target) > 0.005)
                webView.ZoomFactor = target;
        }
        catch (InvalidOperationException)
        {
            // WebView2 controller has not been initialized or was disposed.
        }
    }

    private void ResetWallpaperZoom()
    {
        try
        {
            if (Math.Abs(webView.ZoomFactor - 1.0) > 0.005)
                webView.ZoomFactor = 1.0;
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Discovery may take more than two seconds if Explorer is unavailable.
    /// Do all synchronous EnumWindows/SendMessageTimeout/sleeps off the UI
    /// thread, with one in-flight request and bounded exponential retry delay.
    /// All WinForms calls after the await resume on the UI message loop.
    /// </summary>
    private async void AttachToDesktop()
    {
        if (attachedToDesktop || !isClickThrough || !settings.BehindDesktopIcons ||
            desktopDiscoveryInFlight || Environment.TickCount64 < nextDesktopDiscoveryAt ||
            IsDisposed || Disposing || !IsHandleCreated)
            return;

        desktopDiscoveryInFlight = true;
        try
        {
            IntPtr workerW = await Task.Run(NativeMethods.FindDesktopWorkerW);
            if (IsDisposed || Disposing || !IsHandleCreated ||
                attachedToDesktop || !isClickThrough || !settings.BehindDesktopIcons)
                return;

            if (!NativeMethods.IsWorkerW(workerW))
            {
                // Explorer unavailable or recycled the candidate before the
                // discovery completed. Leave the tray responsive and retry later.
                RecordDesktopDiscoveryFailure();
                return;
            }

            // Successful discovery is not sufficient: SetParent can still fail.
            // Do not reset retry throttling until native attachment succeeded.
            if (AttachToWorkerW(workerW))
                ResetDesktopDiscoveryBackoff();
            else
                RecordDesktopDiscoveryFailure();
        }
        catch (Exception ex)
        {
            // Native Explorer interactions can fail when the shell restarts;
            // keep an async-void continuation from crashing the UI thread.
            System.Diagnostics.Debug.WriteLine($"WorkerW discovery failed: {ex}");
            // If reparenting itself failed after suspending painting, do not
            // leave the browser's native window frozen until another retry.
            if (reparentInProgress)
                CancelReparentTransition();
            RecordDesktopDiscoveryFailure();
        }
        finally
        {
            desktopDiscoveryInFlight = false;
        }
    }

    private void ResetDesktopDiscoveryBackoff()
    {
        desktopDiscoveryFailures = 0;
        nextDesktopDiscoveryAt = 0;
    }

    private void RecordDesktopDiscoveryFailure()
    {
        desktopDiscoveryFailures = Math.Min(desktopDiscoveryFailures + 1, 5);
        nextDesktopDiscoveryAt = Environment.TickCount64 +
            Math.Min(30000, 2000L << (desktopDiscoveryFailures - 1));
    }

    /// <summary>
    /// UI-thread-only attachment after WorkerW discovery on a background thread.
    /// </summary>
    private bool AttachToWorkerW(IntPtr workerW)
    {
        if (attachedToDesktop || !isClickThrough || !settings.BehindDesktopIcons ||
            !NativeMethods.IsWorkerW(workerW))
            return false;

        int generation = ++reparentGeneration;
        reparentInProgress = true;
        NativeMethods.SetRedraw(Handle, false);  // freeze painting so the DPI rescale doesn't flash

        // a child window must not be "maximized": drop to Normal sized to the work area
        // (no invisible-frame overhang, no WM_NCCALCSIZE maximize inset) and re-maximize
        // on detach. RestoreBounds keeps the pre-maximize rect for later.
        if (!desktopStateSnapshotValid)
        {
            // Snapshot only once per wallpaper session; Win11 may destroy and
            // recreate WorkerW several times without a mode change.
            maximizedBeforeAttach = WindowState == FormWindowState.Maximized;
            if (maximizedBeforeAttach)
                normalBoundsBeforeAttach = RestoreBounds;
            desktopStateSnapshotValid = true;
        }
        if (maximizedBeforeAttach && WindowState == FormWindowState.Maximized)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Normal;
            Bounds = wa;
        }
        // A previous WorkerW may have been destroyed while the display layout
        // changed. Fit the old screen-space rectangle before reparenting.
        Bounds = maximizedBeforeAttach
            ? Screen.FromRectangle(Bounds).WorkingArea
            : RecoverAccessibleBounds(Bounds);
        boundsBeforeAttach = Bounds;

        var pt = boundsBeforeAttach.Location;
        if (!NativeMethods.TrySetParent(Handle, workerW, out int nativeError))
        {
            System.Diagnostics.Debug.WriteLine($"WorkerW attach failed, GetLastError={nativeError}");
            if (maximizedBeforeAttach)
            {
                Bounds = RecoverAccessibleBounds(normalBoundsBeforeAttach);
                WindowState = FormWindowState.Maximized;
            }
            desktopStateSnapshotValid = false;
            maximizedBeforeAttach = false;
            FinishReparentTransition(generation);
            return false;
        }
        attachedToDesktop = true;  // native parent already verified
        attachedWorkerW = workerW;
        pendingTopLevelStyleRepair = false;
        pendingAttachedStyleRepair = nativeError != 0;
        // A failed coordinate conversion must not bypass title-strip setup.
        // OnLoad handles a not-yet-initialized WebView2 environment as usual.
        EnsureStripBackdrop();
        if (!NativeMethods.ScreenToClient(workerW, ref pt))
        {
            // Explorer may destroy/reparent WorkerW between SetParent and the
            // coordinate conversion. Never pass absolute screen coordinates
            // to a child-relative SetWindowPos; the watchdog will recover.
            System.Diagnostics.Debug.WriteLine("WorkerW attach: ScreenToClient failed; watchdog will reconcile");
            FinishReparentTransition(generation);
            return true;
        }
        if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y,
                boundsBeforeAttach.Width, boundsBeforeAttach.Height, NativeMethods.SWP_NOZORDER_NOACTIVATE))
            System.Diagnostics.Debug.WriteLine("WorkerW attach: initial SetWindowPos failed; watchdog will retry");

        // reparenting swaps the DPI context to the primary monitor's: WebView2 re-zooms
        // and WinForms may rescale layout. After the DPI messages drain, compensate the
        // browser zoom and re-assert physical geometry so nothing shifts or resizes, then
        // resume painting in one clean repaint (no visible zoom-in/zoom-out).
        try
        {
            BeginInvoke(() =>
            {
                if (generation != reparentGeneration)
                    return;
                try
                {
                    if (!attachedToDesktop)
                        return;
                    UpdateWallpaperZoom();
                    var p2 = boundsBeforeAttach.Location;
                    if (!NativeMethods.IsWorkerW(workerW) ||
                        !NativeMethods.HasParent(Handle, workerW) ||
                        !NativeMethods.ScreenToClient(workerW, ref p2))
                    {
                        System.Diagnostics.Debug.WriteLine("WorkerW attach: delayed ScreenToClient failed; watchdog will reconcile");
                        return;
                    }
                    if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, p2.X, p2.Y,
                            boundsBeforeAttach.Width, boundsBeforeAttach.Height, NativeMethods.SWP_NOZORDER_NOACTIVATE))
                        System.Diagnostics.Debug.WriteLine("WorkerW attach: delayed SetWindowPos failed; watchdog will retry");
                    ApplyPadding();
                    titleBar.Reposition();
                }
                finally
                {
                    FinishReparentTransition(generation);
                }
            });
        }
        catch (InvalidOperationException)
        {
            FinishReparentTransition(generation);
        }
        return true;
    }

    private bool DetachFromDesktop()
    {
        if (!attachedToDesktop)
        {
            // Orphan recovery returned the window to top-level; the old
            // wallpaper DPI compensation must not survive interactive mode.
            ResetWallpaperZoom();
            if (desktopStateSnapshotValid)
            {
                if (maximizedBeforeAttach)
                {
                    normalBoundsBeforeAttach = RecoverAccessibleBounds(normalBoundsBeforeAttach);
                    Bounds = normalBoundsBeforeAttach;
                    WindowState = FormWindowState.Maximized;
                }
                desktopStateSnapshotValid = false;
                maximizedBeforeAttach = false;
            }
            return true;
        }
        int generation = ++reparentGeneration;
        reparentInProgress = true;
        NativeMethods.SetRedraw(Handle, false);
        if (!NativeMethods.TrySetParent(Handle, IntPtr.Zero, out int nativeError))
        {
            System.Diagnostics.Debug.WriteLine($"WorkerW detach failed, GetLastError={nativeError}");
            FinishReparentTransition(generation);
            return false;
        }
        attachedToDesktop = false;
        attachedWorkerW = IntPtr.Zero;
        QueueTopLevelStyleRepair(nativeError);
        ResetWallpaperZoom();
        Bounds = boundsBeforeAttach;
        if (maximizedBeforeAttach)
        {
            normalBoundsBeforeAttach = RecoverAccessibleBounds(normalBoundsBeforeAttach);
            Bounds = normalBoundsBeforeAttach;  // seed RestoreBounds with the original rect
            WindowState = FormWindowState.Maximized;
        }
        desktopStateSnapshotValid = false;
        maximizedBeforeAttach = false;
        // same DPI-context swap in reverse; re-assert geometry after messages drain
        try
        {
            BeginInvoke(() =>
            {
                if (generation != reparentGeneration)
                    return;
                try
                {
                    if (attachedToDesktop)
                        return;
                    if (WindowState == FormWindowState.Normal && Bounds != boundsBeforeAttach)
                        Bounds = boundsBeforeAttach;
                    ApplyPadding();
                    titleBar.Reposition();
                }
                finally
                {
                    FinishReparentTransition(generation);
                }
            });
        }
        catch (InvalidOperationException)
        {
            FinishReparentTransition(generation);
        }
        return true;
    }

    /// <summary>
    /// Correct actual HWND drift (including WinForms/DPI side effects) even
    /// when the cached desired rectangle has not changed. GetWindowRect is
    /// screen-relative; SetWindowPos on a WorkerW child is parent-relative.
    /// </summary>
    private void ReconcileAttachedGeometry()
    {
        if (reparentInProgress || !attachedToDesktop || !NativeMethods.IsWindow(attachedWorkerW))
            return;

        var target = maximizedBeforeAttach
            ? Screen.FromRectangle(boundsBeforeAttach).WorkingArea
            : RecoverAccessibleBounds(boundsBeforeAttach);
        if (!NativeMethods.TryGetWindowRect(Handle, out var actual))
            return;

        // A small tolerance absorbs harmless pixel rounding in DPI transitions.
        static bool Near(int x, int y) => Math.Abs((long)x - y) <= 2;
        bool correct = Near(actual.Left, target.Left) && Near(actual.Top, target.Top)
                    && Near(actual.Width, target.Width) && Near(actual.Height, target.Height);

        if (!correct)
        {
            var childLocation = target.Location;
            if (!NativeMethods.ScreenToClient(attachedWorkerW, ref childLocation))
                return;
            if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero,
                    childLocation.X, childLocation.Y, target.Width, target.Height,
                    NativeMethods.SWP_NOZORDER_NOACTIVATE))
            {
                // Do not overwrite our desired bounds; next watchdog can
                // retry even if no monitor work-area change has happened.
                System.Diagnostics.Debug.WriteLine("WorkerW geometry reconciliation: SetWindowPos failed");
                return;
            }
            titleBar.Reposition();
        }

        boundsBeforeAttach = target;
        UpdateWallpaperZoom();
    }

    /// <summary>
    /// Watchdog: the shell recreates its WorkerW on display changes / monitor power events
    /// (common with multiple monitors), which orphans our reparented window — it survives
    /// but floats at stale WorkerW-relative coordinates, so it appears to vanish. Detect the
    /// orphaning and re-attach to the fresh WorkerW. Runs from the 100 ms poll, throttled.
    ///
    /// Validate the real native parent with GetAncestor(GA_PARENT) in addition
    /// to WorkerW liveness, since a previous SetParent may have partly failed
    /// or Explorer may have replaced the shell window.
    /// </summary>
    private void EnsureDesktopAttachment()
    {
        if (!settings.BehindDesktopIcons || !isClickThrough)
            return;
        if (attachedToDesktop && NativeMethods.IsWorkerW(attachedWorkerW) &&
            NativeMethods.HasParent(Handle, attachedWorkerW))
        {
            // Child-relative coordinates may have failed to apply immediately
            // after SetParent. The guarded 1s watchdog retries only actual
            // geometry drift; unrelated top-level geometry remains event-driven.
            if (!reparentInProgress)
            {
                bool fixedStyle = NativeMethods.TryRepairParentStyle(Handle, attached: true,
                    out int styleError, forceFrameRefresh: pendingAttachedStyleRepair);
                pendingAttachedStyleRepair = !fixedStyle;
                if (!fixedStyle)
                    System.Diagnostics.Debug.WriteLine($"WorkerW child style retry failed: {styleError}");
                ReconcileAttachedGeometry();
            }
            return;
        }

        // Honor the same retry gate for a failed orphan detach as for failed
        // discovery/attachment. Otherwise the 1s watchdog bypasses backoff.
        if (Environment.TickCount64 < nextDesktopDiscoveryAt)
            return;

        if (attachedToDesktop)
        {
            // The parent may disappear before the frozen attach callback ran.
            // Always thaw even if no replacement WorkerW can be discovered.
            try
            {
                if (!NativeMethods.TrySetParent(Handle, IntPtr.Zero, out int nativeError))
                {
                    System.Diagnostics.Debug.WriteLine($"WorkerW orphan recovery failed, GetLastError={nativeError}");
                    RecordDesktopDiscoveryFailure();
                    return;  // retain native attachment state until a successful detach
                }
                attachedToDesktop = false;
                attachedWorkerW = IntPtr.Zero;
                QueueTopLevelStyleRepair(nativeError);
                ResetWallpaperZoom();
                boundsBeforeAttach = maximizedBeforeAttach
                    ? Screen.FromRectangle(boundsBeforeAttach).WorkingArea
                    : RecoverAccessibleBounds(boundsBeforeAttach);
                Bounds = boundsBeforeAttach;
            }
            finally
            {
                CancelReparentTransition();
            }
        }
        else if (desktopStateSnapshotValid)
        {
            // Recovery already left us top-level; handle further display
            // changes during Explorer downtime before retrying attachment.
            boundsBeforeAttach = maximizedBeforeAttach
                ? Screen.FromRectangle(boundsBeforeAttach).WorkingArea
                : RecoverAccessibleBounds(boundsBeforeAttach);
            if (Bounds != boundsBeforeAttach)
                Bounds = boundsBeforeAttach;
        }
        AttachToDesktop();
    }

    /// <summary>
    /// After monitor removal, Windows may leave a normal top-level window
    /// (interactive or plain click-through) mostly offscreen. Only relocate
    /// inaccessible bounds; preserve Maximize state and the usable restore
    /// rectangle. Avoid touching WorkerW attachment transitions.
    /// </summary>
    private void ReconcileTopLevelGeometry()
    {
        if (!IsHandleCreated || IsDisposed || attachedToDesktop ||
            desktopStateSnapshotValid || reparentInProgress || nativeMoveSizeActive ||
            WindowState == FormWindowState.Minimized)
            return;

        bool maximized = WindowState == FormWindowState.Maximized;
        var normal = maximized ? RestoreBounds : Bounds;
        if (!NeedsOffscreenRecovery(normal))
            return;

        var recovered = RecoverAccessibleBounds(normal);
        if (maximized)
        {
            // WinForms exposes RestoreBounds only as a getter. Change it
            // through a brief Normal state, then restore maximization; this
            // path runs only for an actually inaccessible restore rectangle.
            WindowState = FormWindowState.Normal;
            Bounds = recovered;
            WindowState = FormWindowState.Maximized;
        }
        else
            Bounds = recovered;

        SaveBounds();
        settings.Save();
    }

    /// <summary>
    /// If wallpaper is requested but Explorer has not supplied WorkerW, the
    /// window remains a normal top-level (taskbar-hidden) fallback. Keep that
    /// fallback on a visible monitor without interrupting a pending reparent.
    /// </summary>
    private void ReconcileUnattachedWallpaperFallback()
    {
        if (attachedToDesktop || reparentInProgress || desktopDiscoveryInFlight ||
            nativeMoveSizeActive)
            return;

        if (!desktopStateSnapshotValid)
        {
            ReconcileTopLevelGeometry();
            return;
        }

        // A destroyed WorkerW leaves a top-level HWND yet the wallpaper
        // maximize/restore snapshot intentionally stays valid. The regular
        // top-level method skips it to avoid persisting temporary bounds.
        // Reconcile here without discarding that snapshot.
        if (WindowState == FormWindowState.Minimized)
            return;

        if (maximizedBeforeAttach)
        {
            var work = Screen.FromRectangle(boundsBeforeAttach).WorkingArea;
            normalBoundsBeforeAttach = RecoverAccessibleBounds(normalBoundsBeforeAttach);
            if (Bounds != work)
                Bounds = work;
            boundsBeforeAttach = work;
        }
        else
        {
            // Prefer the actual window location when Windows has already moved
            // it successfully to the remaining monitors.
            if (NeedsOffscreenRecovery(Bounds))
                Bounds = RecoverAccessibleBounds(Bounds);
            if (NeedsOffscreenRecovery(boundsBeforeAttach) || boundsBeforeAttach != Bounds)
                boundsBeforeAttach = Bounds;
        }
    }

    private void ReconcileDisplayConfiguration()
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        if (settings.BehindDesktopIcons && isClickThrough)
        {
            ReconcileUnattachedWallpaperFallback();
            ReconcileAttachedGeometry();
            EnsureDesktopAttachment();
        }
        else
            ReconcileTopLevelGeometry();
    }

    /// <summary>
    /// Retry the requested wallpaper disable after a transient native failure.
    /// Keep click-through enabled until we have verified top-level parenting.
    /// </summary>
    private void RetryPendingWallpaperDetach()
    {
        if (!pendingWallpaperDetach || settings.BehindDesktopIcons ||
            !isClickThrough || pendingInteractiveDetach)
            return;
        if (!DetachFromDesktop())
        {
            RecordDesktopDiscoveryFailure();
            return;
        }
        pendingWallpaperDetach = false;
        ResetDesktopDiscoveryBackoff();
        NativeMethods.SendToBottom(Handle);
    }

    public void SetBehindDesktopIcons(bool behind)
    {
        settings.BehindDesktopIcons = behind; // desired mode, regardless of native outcome
        pendingWallpaperDetach = false; // explicit user toggle supersedes prior request
        ResetDesktopDiscoveryBackoff();
        settingsForm?.SyncBehindDesktopIcons(behind);
        if (isClickThrough)
        {
            if (behind)
                AttachToDesktop();
            else if (!DetachFromDesktop())
            {
                pendingWallpaperDetach = true;
                RecordDesktopDiscoveryFailure();
            }
            else
                NativeMethods.SendToBottom(Handle);
        }
        settings.Save();
        SyncSettingsControls();
    }

    public void SetCornerPanelEnabled(bool enabled)
    {
        settings.CornerPanelEnabled = enabled;
        PreviewHoverPanel();  // shows it briefly when enabled, hides it when disabled
        settings.Save();
    }

    // ---------------- hover panel ----------------

    /// <summary>Where the panel goes on the widget's monitor, sized for that monitor's DPI.</summary>
    private (Rectangle Bounds, int Dpi) GetPanelPlacement()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int dpi = NativeMethods.MonitorDpi(Handle);
        var size = HoverPanel.SizeFor(dpi);
        int x = settings.PanelCorner is "BottomLeft" or "TopLeft"
            ? wa.Left + UiScale.Px(40, dpi)
            : wa.Right - size.Width - UiScale.Px(40, dpi);
        int y = settings.PanelCorner is "TopLeft" or "TopRight"
            ? wa.Top + UiScale.Px(30, dpi)
            : wa.Bottom - size.Height - UiScale.Px(30, dpi);
        return (new Rectangle(new Point(x, y), size), dpi);
    }

    public void SetPanelCorner(string corner)
    {
        settings.PanelCorner = corner;
        settings.Save();
        PreviewHoverPanel();
    }

    /// <summary>Flash the panel at its corner for 2 s so a settings change is visible in either mode.</summary>
    private void PreviewHoverPanel()
    {
        if (!settings.CornerPanelEnabled)
        {
            hoverPanel.Hide();
            return;
        }
        var (bounds, dpi) = GetPanelPlacement();
        hoverPanel.ShowAt(bounds, dpi);
        hoverHideDeadline = DateTime.Now.AddSeconds(2);  // CheckMouseHover hides it unless hovered
    }

    private void QueueTopLevelStyleRepair(int previousError)
    {
        pendingAttachedStyleRepair = false;
        // No error means SetParent and its top-level style/frame update both
        // succeeded. Otherwise retry independently even in interactive mode.
        pendingTopLevelStyleRepair = previousError != 0;
        nextTopLevelStyleRepairAt = Environment.TickCount64 + 1000;
    }

    private void RetryTopLevelStyleRepair()
    {
        if (!pendingTopLevelStyleRepair || !IsHandleCreated || IsDisposed ||
            reparentInProgress || attachedToDesktop ||
            Environment.TickCount64 < nextTopLevelStyleRepairAt)
            return;

        nextTopLevelStyleRepairAt = Environment.TickCount64 + 1000;
        if (!NativeMethods.HasDesktopParent(Handle))
            return; // A different native parent cannot be repaired as top-level.

        if (NativeMethods.TryRepairParentStyle(Handle, attached: false,
                out int error, forceFrameRefresh: true))
            pendingTopLevelStyleRepair = false;
        else
            System.Diagnostics.Debug.WriteLine($"Top-level style retry failed: {error}");
    }

    private void CheckMouseHover()
    {
        // This is not gated by click-through or wallpaper mode: a successful
        // detach may need a delayed WS_POPUP/frame repair in interactive mode.
        RetryTopLevelStyleRepair();
        // Retry failed worker-parent detach without sacrificing safe click-through.
        if (pendingInteractiveDetach && !isClickThrough == false &&
            Environment.TickCount64 >= nextDesktopDiscoveryAt)
            SetClickThrough(false);
        else if (pendingWallpaperDetach && isClickThrough &&
                 Environment.TickCount64 >= nextDesktopDiscoveryAt)
            RetryPendingWallpaperDetach();

        // interactive mode: controls are integrated in the title bar; the corner panel only
        // shows as a settings preview, which just has to time out
        if (!isClickThrough)
        {
            if (hoverPanel.Visible && (hoverHideDeadline is null || DateTime.Now >= hoverHideDeadline))
            {
                hoverPanel.Hide();
                hoverHideDeadline = null;
            }
            return;
        }

        // ~every 1s, make sure we're still behind the icons (the shell can orphan us)
        if (settings.BehindDesktopIcons && ++attachCheckTick >= 10)
        {
            attachCheckTick = 0;
            EnsureDesktopAttachment();
        }

        // right after enabling: re-assert on every tick until the burst runs out, so
        // windows Chrome recreates for layered rendering go transparent within ~100ms
        if (styleBurst > 0)
        {
            styleBurst--;
            NativeMethods.EnableClickThrough(Handle, clickThroughApplied);
        }
        // every ~2s after that: Chrome can still recreate input windows (renderer restart,
        // navigation) and WinForms style updates can wipe manually-set ex-styles.
        // EnableClickThrough no-ops for windows that already carry the style.
        else if (++guardTick >= 20)
        {
            guardTick = 0;
            NativeMethods.EnableClickThrough(Handle, clickThroughApplied);
        }

        if (!settings.CornerPanelEnabled)
            return;

        var (rect, dpi) = GetPanelPlacement();

        if (rect.Contains(Cursor.Position))
        {
            hoverHideDeadline = null;
            if (!hoverPanel.Visible)
                hoverPanel.ShowAt(rect, dpi);  // no-activate: HoverPanel overrides ShowWithoutActivation
        }
        else if (hoverPanel.Visible)
        {
            hoverHideDeadline ??= DateTime.Now.AddSeconds(2);
            if (DateTime.Now >= hoverHideDeadline)
            {
                hoverPanel.Hide();
                hoverHideDeadline = null;
            }
        }
    }

    // ---------------- Google account ----------------

    /// <summary>Signed-in check: Google's session cookies exist in the widget's profile.</summary>
    public async Task<bool> IsSignedInAsync()
    {
        if (webView.CoreWebView2 is null)
            return false;
        var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://calendar.google.com");
        return cookies.Any(c => c.Name is "SID" or "__Secure-1PSID" or "__Secure-3PSID");
    }

    /// <summary>Clear the embedded browser profile (cookies, storage) and return to the sign-in page.</summary>
    public async Task SignOutAsync()
    {
        if (webView.CoreWebView2 is null)
            return;
        await webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
        EnsureInteractive();  // show the signed-out page so the user can sign back in
        webView.CoreWebView2.Navigate(CalendarUrl);
    }

    /// <summary>
    /// Sign out via Google's logout endpoint, keeping the account on Google's chooser
    /// so signing back in doesn't require retyping the address (often just the password).
    /// </summary>
    public async Task SignOutKeepAccountAsync()
    {
        if (webView.CoreWebView2 is null)
            return;
        EnsureInteractive();
        webView.CoreWebView2.Navigate("https://accounts.google.com/Logout");
        await Task.Delay(2500);  // let the logout round-trip settle before heading back
        webView.CoreWebView2.Navigate(CalendarUrl);
    }

    /// <summary>Bring the widget to interactive mode on the calendar page, which redirects to Google sign-in.</summary>
    public void BeginSignIn()
    {
        EnsureInteractive();
        webView.CoreWebView2?.Navigate(CalendarUrl);
    }

    private void EnsureInteractive()
    {
        if (isClickThrough)
            SetClickThrough(false);
        Activate();
    }

    // ---------------- tray + settings ----------------

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        var headerItem = new ToolStripMenuItem("Show title bar", null,
            (_, _) => SetHeaderVisible(!settings.ShowTitleBar));
        var interactionItem = new ToolStripMenuItem("Enable calendar interaction", null,
            (_, _) => ToggleClickThrough());
        var layoutItem = new ToolStripMenuItem("Move / resize window", null,
            (_, _) => SetLayoutEditing(!isLayoutUnlocked));
        var moveItem = new ToolStripMenuItem("Move window (keyboard)…", null,
            (_, _) => BeginKeyboardMove());
        var behindItem = new ToolStripMenuItem("Behind desktop icons (live wallpaper)", null,
            (_, _) => SetBehindDesktopIcons(!settings.BehindDesktopIcons));
        menu.Items.Add(headerItem);
        menu.Items.Add(interactionItem);
        menu.Items.Add(layoutItem);
        menu.Items.Add(moveItem);
        menu.Items.Add(behindItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        menu.Opening += (_, _) =>
        {
            headerItem.Checked = settings.ShowTitleBar;
            interactionItem.Checked = !isClickThrough;
            layoutItem.Checked = isLayoutUnlocked;
            moveItem.Enabled = !isClickThrough && isLayoutUnlocked;
            behindItem.Checked = settings.BehindDesktopIcons;
        };
        tray.Icon = Icon ?? SystemIcons.Application;
        tray.Text = "Google Calendar Desktop Widget";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += (_, _) => ToggleClickThrough();
    }

    public void ShowSettings()
    {
        settingsForm ??= new SettingsForm(this, settings);
        SyncSettingsControls();
        settingsForm.ShowAndActivate();
    }

    private void SaveBounds()
    {
        if (attachedToDesktop || desktopStateSnapshotValid || reparentInProgress ||
            WindowState == FormWindowState.Minimized) return;
        var normal = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
        if (normal.Width < 100 || normal.Height < 100) return;
        settings.WinX = normal.X;
        settings.WinY = normal.Y;
        settings.WinW = normal.Width;
        settings.WinH = normal.Height;
        settings.WinMaximized = WindowState == FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Called while the HWND and RestoreBounds still exist. The SaveBounds
        // guard protects wallpaper child coordinates and minimized state.
        // Unlike the former passive-only close path, this includes an
        // interactive window maximized/snapped with Win+Arrow.
        SaveBounds();
        settings.Save();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        hoverPoll.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
