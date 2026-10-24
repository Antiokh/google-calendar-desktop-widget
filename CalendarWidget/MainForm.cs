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
    private bool isClickThrough;           // effective OS-level pass-through state
    private bool calendarInteractionEnabled; // the user's independent interaction preference
    private bool isLayoutUnlocked;         // temporary move/resize mode, without editing calendar events
    private bool previewLook;             // interactive mode showing the widget look while Settings is open
    private DateTime? hoverHideDeadline;  // set while the panel is visible but the mouse has left it
    private int guardTick;                // periodic re-assert of click-through styles
    private int styleBurst;               // fast re-assert right after enabling (Chrome recreates windows)
    private int attachCheckTick;          // throttle for the behind-icons re-attach watchdog
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

    public void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        titleBar?.Reposition();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        titleBar?.Reposition();
    }

    private Rectangle InitialBounds()
    {
        if (settings.WinW > 0)
            return new Rectangle(settings.WinX, settings.WinY, settings.WinW, settings.WinH);

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
        // When the title bar is hidden, its strip belongs to the calendar.
        // Layout mode temporarily reveals the bar as a safe drag handle.
        int top = settings.ShowTitleBar || isLayoutUnlocked ? titleBar.BarHeight : 0;
        if (isClickThrough)
            return new Padding(0, top, 0, 0);
        int frame = LogicalToDeviceUnits(6);
        return new Padding(frame, top, frame, frame);
    }

    private void ApplyPadding()
    {
        Padding = ModePadding();
        if (stripBackdrop is not null)
        {
            // Do not leave an empty painted strip after removing the header.
            stripBackdrop.Visible = settings.ShowTitleBar && !isLayoutUnlocked;
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
        if (!settings.ShowTitleBar || isLayoutUnlocked)
            return;
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

        // Keep the sign-in experience interactive on the very first run.
        // Layout unlock is intentionally session-only, never restored at startup.
        calendarInteractionEnabled = firstRun;
        ApplyInteractionModes();
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

    public void ToggleClickThrough()
    {
        calendarInteractionEnabled = !calendarInteractionEnabled;
        // A direct request to interact with the calendar leaves layout mode.
        if (isLayoutUnlocked)
            isLayoutUnlocked = false;
        ApplyInteractionModes();
    }

    private void ToggleLayoutEditing()
    {
        isLayoutUnlocked = !isLayoutUnlocked;
        ApplyInteractionModes();
    }

    private void SetHeaderVisible(bool visible)
    {
        settings.ShowTitleBar = visible;
        ApplyPadding();
        UpdateHeaderVisibility();
        if (visible && attachedToDesktop)
            EnsureStripBackdrop();
        settings.Save();
    }

    private void UpdateHeaderVisibility()
    {
        if (!IsHandleCreated)
            return;
        if (settings.ShowTitleBar || isLayoutUnlocked)
        {
            if (!titleBar.Visible)
            {
                titleBar.Location = PointToScreen(Point.Empty);
                titleBar.Show(this);
            }
            titleBar.Reposition();
        }
        else if (titleBar.Visible)
        {
            titleBar.Hide();
        }
    }

    /// <summary>
    /// Interaction and positioning are distinct user preferences. Editing layout
    /// temporarily detaches the window from WorkerW and reveals its drag/resize
    /// frame, but disables the browser input so dragging cannot edit events.
    /// </summary>
    private void ApplyInteractionModes()
    {
        bool passiveWallpaper = !calendarInteractionEnabled && !isLayoutUnlocked;
        SetClickThrough(passiveWallpaper);
        webView.Enabled = !isLayoutUnlocked;
        UpdateHeaderVisibility();
        ApplyPadding();
    }

    private void SetClickThrough(bool enable)
    {
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
            DetachFromDesktop();  // interactive mode is always a normal top-level window
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
        titleBar.Reposition();
        ApplyEdgeShadows();  // widget mode only: they'd hide content while interacting
        settings.Save();
    }

    // pin the window to the bottom of the Z-order: any attempt to raise it
    // (taskbar click, alt-tab, shell repositioning) is rewritten to HWND_BOTTOM
    protected override void WndProc(ref Message m)
    {
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

        // borderless resize: the padding frame around the webview doubles as resize grips
        if (m.Msg == NativeMethods.WM_NCHITTEST && !isClickThrough && !NativeMethods.IsZoomed(Handle))
        {
            base.WndProc(ref m);
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
        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE && settings.BehindDesktopIcons && isClickThrough)
            BeginInvoke(EnsureDesktopAttachment);

        base.WndProc(ref m);
    }

    // WinForms recreates the handle for some property changes; re-apply manual styles if so
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
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

    /// <summary>Reparent into the shell's WorkerW layer so the widget renders UNDER the desktop icons.</summary>
    private void AttachToDesktop()
    {
        if (attachedToDesktop)
            return;
        IntPtr workerW = NativeMethods.FindDesktopWorkerW();
        if (workerW == IntPtr.Zero)
            return;  // shell didn't cooperate; stay a normal bottom-pinned window

        NativeMethods.SetRedraw(Handle, false);  // freeze painting so the DPI rescale doesn't flash

        // a child window must not be "maximized": drop to Normal sized to the work area
        // (no invisible-frame overhang, no WM_NCCALCSIZE maximize inset) and re-maximize
        // on detach. RestoreBounds keeps the pre-maximize rect for later.
        maximizedBeforeAttach = WindowState == FormWindowState.Maximized;
        if (maximizedBeforeAttach)
        {
            normalBoundsBeforeAttach = RestoreBounds;
            var wa = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Normal;
            Bounds = wa;
        }
        boundsBeforeAttach = Bounds;

        uint dpiBefore = NativeMethods.GetDpiForWindow(Handle);
        var pt = boundsBeforeAttach.Location;
        NativeMethods.SetParent(Handle, workerW);
        NativeMethods.ScreenToClient(workerW, ref pt);  // child coordinates are WorkerW-relative
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y,
            boundsBeforeAttach.Width, boundsBeforeAttach.Height, NativeMethods.SWP_NOZORDER_NOACTIVATE);
        attachedToDesktop = true;
        attachedWorkerW = workerW;
        EnsureStripBackdrop();  // the painted strip is invisible from here on

        // reparenting swaps the DPI context to the primary monitor's: WebView2 re-zooms
        // and WinForms may rescale layout. After the DPI messages drain, compensate the
        // browser zoom and re-assert physical geometry so nothing shifts or resizes, then
        // resume painting in one clean repaint (no visible zoom-in/zoom-out).
        BeginInvoke(() =>
        {
            if (!attachedToDesktop)
            {
                NativeMethods.SetRedraw(Handle, true);
                return;
            }
            uint dpiAfter = Math.Max(1, NativeMethods.GetDpiForWindow(Handle));
            try { webView.ZoomFactor = dpiBefore / (double)dpiAfter; } catch { }
            var p2 = boundsBeforeAttach.Location;
            NativeMethods.ScreenToClient(workerW, ref p2);
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, p2.X, p2.Y,
                boundsBeforeAttach.Width, boundsBeforeAttach.Height, NativeMethods.SWP_NOZORDER_NOACTIVATE);
            ApplyPadding();
            titleBar.Reposition();
            NativeMethods.SetRedraw(Handle, true);
            NativeMethods.RepaintAll(Handle);
        });
    }

    private void DetachFromDesktop()
    {
        if (!attachedToDesktop)
            return;
        NativeMethods.SetRedraw(Handle, false);
        NativeMethods.SetParent(Handle, IntPtr.Zero);
        attachedToDesktop = false;
        attachedWorkerW = IntPtr.Zero;
        try { webView.ZoomFactor = 1.0; } catch { }
        Bounds = boundsBeforeAttach;
        if (maximizedBeforeAttach)
        {
            Bounds = normalBoundsBeforeAttach;  // seed RestoreBounds with the original rect
            WindowState = FormWindowState.Maximized;
            maximizedBeforeAttach = false;
        }
        // same DPI-context swap in reverse; re-assert geometry after messages drain
        BeginInvoke(() =>
        {
            if (attachedToDesktop)
            {
                NativeMethods.SetRedraw(Handle, true);
                return;
            }
            if (WindowState == FormWindowState.Normal)
                Bounds = boundsBeforeAttach;
            ApplyPadding();
            titleBar.Reposition();
            NativeMethods.SetRedraw(Handle, true);
            NativeMethods.RepaintAll(Handle);
        });
    }

    /// <summary>
    /// Watchdog: the shell recreates its WorkerW on display changes / monitor power events
    /// (common with multiple monitors), which orphans our reparented window — it survives
    /// but floats at stale WorkerW-relative coordinates, so it appears to vanish. Detect the
    /// orphaning and re-attach to the fresh WorkerW. Runs from the 100 ms poll, throttled.
    ///
    /// The orphaning signal is the DESTRUCTION of the WorkerW we attached to
    /// (`IsWindow(attachedWorkerW)`), NOT `GetParent(Handle)`: a WinForms window keeps its
    /// top-level style after SetParent, so GetParent returns 0 even while correctly parented
    /// — checking it would re-attach every tick and flicker the WebView white.
    /// </summary>
    private void EnsureDesktopAttachment()
    {
        if (!settings.BehindDesktopIcons || !isClickThrough)
            return;
        if (attachedToDesktop && NativeMethods.IsWindow(attachedWorkerW))
            return;  // the WorkerW we're parented into is still alive — nothing to do

        if (attachedToDesktop)
        {
            // our WorkerW was destroyed: return to a valid top-level state at known-good bounds
            NativeMethods.SetParent(Handle, IntPtr.Zero);
            attachedToDesktop = false;
            attachedWorkerW = IntPtr.Zero;
            Bounds = boundsBeforeAttach;
        }
        AttachToDesktop();
    }

    public void SetBehindDesktopIcons(bool behind)
    {
        settings.BehindDesktopIcons = behind;
        if (isClickThrough)
        {
            if (behind)
            {
                AttachToDesktop();
            }
            else
            {
                DetachFromDesktop();
                NativeMethods.SendToBottom(Handle);
            }
        }
        settings.Save();
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

    private void CheckMouseHover()
    {
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
        if (!calendarInteractionEnabled || isLayoutUnlocked)
        {
            calendarInteractionEnabled = true;
            isLayoutUnlocked = false;
            ApplyInteractionModes();
        }
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
            (_, _) => ToggleLayoutEditing());

        menu.Items.Add(headerItem);
        menu.Items.Add(interactionItem);
        menu.Items.Add(layoutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        menu.Opening += (_, _) =>
        {
            headerItem.Checked = settings.ShowTitleBar;
            interactionItem.Checked = calendarInteractionEnabled;
            layoutItem.Checked = isLayoutUnlocked;
            layoutItem.Text = isLayoutUnlocked ? "Finish moving / resizing" : "Move / resize window";
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
        settingsForm.ShowAndActivate();
    }

    private void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal)
            return;
        settings.WinX = Bounds.X;
        settings.WinY = Bounds.Y;
        settings.WinW = Bounds.Width;
        settings.WinH = Bounds.Height;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        hoverPoll.Stop();
        // while attached, Bounds are WorkerW-relative and the window can't have moved
        // since attach anyway — the entering-widget-mode SaveBounds already captured them
        if (isClickThrough && !attachedToDesktop)
            SaveBounds();
        settings.Save();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
