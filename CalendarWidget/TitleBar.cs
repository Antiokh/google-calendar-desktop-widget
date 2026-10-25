// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace CalendarWidget;

/// <summary>
/// The widget's title bar, visible in BOTH modes. It is a separate top-level window
/// owned by MainForm (owned windows always float directly above their owner), so it is
/// never subject to the click-through styling applied to the main window's tree — the
/// controls keep working while the calendar underneath passes clicks to the desktop.
/// Dragging it forwards a native caption drag to the owner, so Aero Snap works.
/// In widget mode the window clips itself (Region) to just its buttons: the rest of the
/// strip shows the main window beneath and passes clicks through like the calendar does.
/// Colors follow the calendar's theme (<see cref="ApplyTheme"/>); sizes follow the DPI of
/// the monitor the bar is on (<see cref="BarHeight"/> changes with it).
/// </summary>
public class TitleBar : Form
{
    // 96-DPI design metrics, scaled to the bar's monitor in ApplyScale
    private const int LogicalHeight = 34;
    private const int LogicalBtnW = 46;

    private static readonly Color CloseHover = Color.FromArgb(196, 43, 28);  // Win11 close-button red

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;

    private readonly Form owner;
    private readonly Action onToggleMaximize;
    private readonly PictureBox iconBox;
    private readonly Label title;
    private readonly Button btnToggle;
    private readonly Button btnMenu;
    private readonly Button btnClose;
    private Theme theme = Theme.Dark;
    private bool clickThrough;
    private bool layoutUnlocked = true;
    private int btnW = LogicalBtnW;

    /// <summary>Bar height in pixels at the bar's DPI; the owner reserves a strip this tall.</summary>
    public int BarHeight { get; private set; } = LogicalHeight;

    /// <summary>Raised when a DPI change resized the bar, so the owner can resize its strip.</summary>
    public event EventHandler? BarHeightChanged;

    // the bar must never steal focus from the owner or anything else
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= unchecked((int)NativeMethods.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    public TitleBar(Form owner, Action onToggle, Action onSettings, Action onToggleMaximize)
    {
        this.owner = owner;
        this.onToggleMaximize = onToggleMaximize;

        AutoScaleMode = AutoScaleMode.None;  // ApplyScale lays the bar out for its DPI
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;

        iconBox = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom };
        try { iconBox.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap(); } catch { }

        title = new Label
        {
            Text = "Google Calendar Desktop Widget",
            AutoEllipsis = true,  // a narrow widget truncates the title instead of hiding it under the buttons
            BackColor = Color.Transparent,
        };

        btnToggle = MakeButton("");  // mouse: toggle click-through
        btnMenu = MakeButton("");    // hamburger: settings
        btnClose = MakeButton("");   // ChromeClose
        btnToggle.Click += (_, _) => onToggle();
        btnMenu.Click += (_, _) => onSettings();
        btnClose.Click += (_, _) => Application.Exit();
        // Win11 caption style: white glyph on the red hover
        btnClose.MouseEnter += (_, _) => btnClose.ForeColor = Color.White;
        btnClose.MouseLeave += (_, _) => btnClose.ForeColor = theme.Fore;

        var tips = new ToolTip();
        tips.SetToolTip(btnToggle, "Toggle click-through (pass clicks to the desktop or not)");
        tips.SetToolTip(btnMenu, "Settings");
        tips.SetToolTip(btnClose, "Exit widget");

        Controls.AddRange([iconBox, title, btnToggle, btnMenu, btnClose]);

        // right-align the buttons whenever the bar resizes with the window
        Resize += (_, _) => LayoutButtons();

        // dragging the bar (or the title/icon on it) moves the owner window
        MouseDown += StartDrag;
        title.MouseDown += StartDrag;
        iconBox.MouseDown += StartDrag;

        ApplyScale();
        ApplyTheme(theme);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyRoundedCorners(Handle);
        ApplyScale();  // DeviceDpi is the real monitor's only once the handle exists
    }

    // moved onto a monitor with another scale: re-lay out ourselves. Cancelled so WinForms
    // doesn't also rescale controls/fonts on top; Reposition() owns the bounds.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
        ApplyScale();
    }

    private void ApplyScale()
    {
        int dpi = DeviceDpi;
        int S(int v) => UiScale.Px(v, dpi);

        var titleFont = UiScale.Font(9.5f, dpi);
        var iconFont = UiScale.IconFont(10f, dpi);

        int oldHeight = BarHeight;
        BarHeight = S(LogicalHeight);
        btnW = S(LogicalBtnW);

        iconBox.Bounds = new Rectangle(S(12), (BarHeight - S(16)) / 2, S(16), S(16));
        title.Font = titleFont;
        int titleH = title.PreferredHeight;
        title.SetBounds(S(36), (BarHeight - titleH) / 2, title.Width, titleH);
        foreach (var b in new[] { btnToggle, btnMenu, btnClose })
        {
            b.Font = iconFont;
            b.Size = new Size(btnW, BarHeight);
        }
        Height = BarHeight;
        LayoutButtons();

        if (BarHeight != oldHeight)
            BarHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recolor to match the calendar page: background, glyphs, hover highlight.</summary>
    public void ApplyTheme(Theme t)
    {
        theme = t;
        BackColor = t.Back;
        title.ForeColor = t.Fore;
        foreach (var b in new[] { btnToggle, btnMenu, btnClose })
        {
            b.BackColor = t.Back;
            b.ForeColor = t.Fore;
            b.FlatAppearance.MouseOverBackColor = b == btnClose ? CloseHover : t.Hover;
            b.FlatAppearance.MouseDownBackColor = b == btnClose ? CloseHover : t.Hover;
        }
        btnToggle.ForeColor = clickThrough ? t.Accent : t.Fore;
    }

    /// <summary>
    /// Snap the bar onto the top edge of the owner window. Called for every move during a
    /// drag, so it uses raw SetWindowPos — the WinForms Bounds setter would run the full
    /// managed layout pipeline per pixel and make dragging visibly laggy.
    /// </summary>
    public void Reposition()
    {
        if (!IsHandleCreated || !owner.IsHandleCreated)
            return;
        // track the CLIENT origin, not Bounds: when maximized the window rect overhangs
        // the monitor by the invisible frame, but the client rect stays fully visible
        var origin = owner.PointToScreen(Point.Empty);
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, origin.X, origin.Y, owner.ClientSize.Width, BarHeight,
            NativeMethods.SWP_NOZORDER_NOACTIVATE);
    }

    /// <summary>
    /// Widget mode: tint the mouse icon; hide close; disable drag/maximize; shrink to the
    /// buttons. The bar always takes the widget's current opacity so it reads as part of
    /// the calendar — also in interactive mode while Settings previews a transparency.
    /// </summary>
    public void UpdateState(bool clickThrough, double widgetOpacity)
    {
        this.clickThrough = clickThrough;
        btnToggle.ForeColor = clickThrough ? theme.Accent : theme.Fore;
        btnClose.Visible = !clickThrough;
        LayoutButtons();
        Opacity = widgetOpacity;
    }

    private void LayoutButtons()
    {
        // decided by mode, not Button.Visible: that reads false until the bar is first shown
        Button[] shown = clickThrough ? [btnToggle, btnMenu] : [btnToggle, btnMenu, btnClose];
        int buttonsLeft = Width - shown.Length * btnW;
        for (int i = 0; i < shown.Length; i++)
            shown[i].Location = new Point(buttonsLeft + i * btnW, 0);
        title.Width = Math.Max(0, buttonsLeft - title.Left);

        // widget mode: the window is ONLY its buttons — outside them hit-testing falls
        // through to whatever is below (the click-through calendar, desktop icons in
        // live-wallpaper mode). A region, not HTTRANSPARENT: that only forwards clicks to
        // windows on this thread, never to the desktop.
        Region = clickThrough
            ? new Region(new Rectangle(buttonsLeft, 0, shown.Length * btnW, BarHeight))
            : null;
    }

    private static Button MakeButton(string glyph)
    {
        var b = new Button
        {
            Text = glyph,
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    public void SetLayoutUnlocked(bool enabled) => layoutUnlocked = enabled;

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        // widget mode: the bar only hosts the toggle/settings buttons — the pinned
        // widget must not be movable or maximizable by accident
        if (clickThrough || !layoutUnlocked || e.Button != MouseButtons.Left)
            return;
        if (e.Clicks == 2)
        {
            onToggleMaximize();  // native caption double-click behavior
            return;
        }
        // native caption drag on the OWNER: gives live move + Aero Snap (drag-to-top/edges)
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(owner.Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }
}
