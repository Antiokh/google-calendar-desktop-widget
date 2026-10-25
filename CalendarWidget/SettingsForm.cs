// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Win32;

namespace CalendarWidget;

/// <summary>
/// Settings window. Laid out in code at 96 DPI and scaled to the DPI of the monitor it is
/// on (<see cref="LayoutForDpi"/>): positions and font sizes scale together, and each row
/// is placed below the measured height of the one above, so nothing overlaps at any scale.
/// </summary>
public class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CalendarWidget";

    // 96-DPI design metrics
    private const int LogicalWidth = 340;  // client width
    private const int Gutter = 20;         // left/right margin

    private readonly MainForm main;
    private bool syncingTrayOptions;
    private bool syncingBehindDesktopIcons;
    private readonly CheckBox cbInteraction;
    private readonly CheckBox cbLayout;
    private readonly Button btnMove;
    private readonly CheckBox cbTitleBar;
    private readonly CheckBox cbStayDesktop;
    private readonly CheckBox cbDateIcon;

    private readonly Label lblAlpha = SectionLabel("TRANSPARENCY");
    private readonly Label valAlpha = ValueLabel(ContentAlignment.TopRight);
    private readonly TrackBar sldAlpha;
    private readonly CheckBox cbBehind;
    private readonly Label lblShadow = SectionLabel("EDGE SHADOWS");
    private readonly EdgeShadowSlider sldShadow;
    private readonly Label valShadowLeft = ValueLabel(ContentAlignment.TopLeft);
    private readonly Label valShadowRight = ValueLabel(ContentAlignment.TopRight);
    private readonly Label lblCorner = SectionLabel("HOVER PANEL");
    private readonly CheckBox cbCorner;
    private readonly ComboBox cmbCorner;
    private readonly CheckBox cbStartup;
    private readonly Label lblTip;
    private readonly Button btnDonate;
    private readonly Button btnExit;
    private readonly Button btnAccount;

    private static readonly Color Back = Color.FromArgb(32, 33, 36);
    private static readonly Color CardBack = Color.FromArgb(48, 49, 52);
    private static readonly Color Border = Color.FromArgb(94, 99, 104);
    private static readonly Color Fore = Color.FromArgb(232, 234, 237);
    private static readonly Color Muted = Color.FromArgb(154, 160, 166);
    private static readonly Color Accent = Color.FromArgb(138, 180, 248);

    private static readonly string[] CornerIds = ["BottomRight", "BottomLeft", "TopRight", "TopLeft"];
    private static readonly string[] CornerNames = ["Bottom right", "Bottom left", "Top right", "Top left"];

    public SettingsForm(MainForm main, AppSettings settings)
    {
        this.main = main;
        Text = "Google Calendar Desktop Widget";
        AutoScaleMode = AutoScaleMode.None;  // LayoutForDpi scales everything itself
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScroll = true;
        StartPosition = FormStartPosition.Manual;  // centered by ShowAndActivate / OnHandleCreated
        BackColor = Back;
        ForeColor = Fore;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep default */ }

        // ---- transparency ----
        valAlpha.Text = settings.TransparencyPercent + "%";
        sldAlpha = new TrackBar
        {
            Minimum = 0,
            Maximum = 95,
            Value = Math.Clamp(settings.TransparencyPercent, 0, 95),
            TickStyle = TickStyle.None,
            BackColor = Back,
        };
        sldAlpha.ValueChanged += (_, _) =>
        {
            valAlpha.Text = sldAlpha.Value + "%";
            main.SetTransparencyPercent(sldAlpha.Value);
        };

        cbInteraction = Check("Enable calendar interaction", main.CalendarInteractionEnabled);
        cbInteraction.CheckedChanged += (_, _) =>
        {
            if (!syncingTrayOptions) main.SetCalendarInteraction(cbInteraction.Checked);
        };
        cbLayout = Check("Move / resize window", settings.LayoutUnlocked);
        cbLayout.CheckedChanged += (_, _) =>
        {
            if (!syncingTrayOptions) main.SetLayoutEditing(cbLayout.Checked);
        };
        btnMove = ActionButton("Move window (keyboard)…", Fore);
        btnMove.Click += (_, _) => main.BeginKeyboardMove();
        cbTitleBar = Check("Show title bar", settings.ShowTitleBar);
        cbTitleBar.CheckedChanged += (_, _) =>
        {
            if (!syncingTrayOptions) main.SetHeaderVisible(cbTitleBar.Checked);
        };

        cbStayDesktop = Check("Stay visible on Show Desktop (experimental)",
            settings.KeepInteractiveVisibleOnDesktop);
        cbStayDesktop.CheckedChanged += (_, _) =>
        {
            if (!syncingTrayOptions) main.SetStayVisibleOnShowDesktop(cbStayDesktop.Checked);
        };

        cbDateIcon = Check("Show date in tray icon", settings.UseDateTrayIcon);
        cbDateIcon.CheckedChanged += (_, _) =>
        {
            if (!syncingTrayOptions) main.SetUseDateTrayIcon(cbDateIcon.Checked);
        };

        // ---- live-wallpaper mode ----
        cbBehind = Check("Sit behind desktop icons (live wallpaper)", settings.BehindDesktopIcons);
        cbBehind.CheckedChanged += (_, _) =>
        {
            if (!syncingBehindDesktopIcons) main.SetBehindDesktopIcons(cbBehind.Checked);
        };

        // ---- edge shadows: dark fades behind desktop icons ----
        sldShadow = new EdgeShadowSlider
        {
            BackColor = Back,
            LeftPercent = settings.EdgeShadowLeftPercent,
            RightPercent = settings.EdgeShadowRightPercent,
        };
        void ShowShadowValues()
        {
            valShadowLeft.Text = SideValue("Left", sldShadow.LeftPercent);
            valShadowRight.Text = SideValue("Right", sldShadow.RightPercent);
        }
        ShowShadowValues();
        sldShadow.ValuesChanged += (_, _) =>
        {
            ShowShadowValues();
            main.SetEdgeShadows(sldShadow.LeftPercent, sldShadow.RightPercent);
        };

        // ---- corner hover panel ----
        cbCorner = Check("Show hover panel in a screen corner", settings.CornerPanelEnabled);
        cmbCorner = new ComboBox
        {
            Enabled = settings.CornerPanelEnabled,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = CardBack,
            ForeColor = Fore,
        };
        cmbCorner.Items.AddRange(CornerNames);
        cmbCorner.SelectedIndex = Math.Max(0, Array.IndexOf(CornerIds, settings.PanelCorner));
        cmbCorner.SelectedIndexChanged += (_, _) => main.SetPanelCorner(CornerIds[cmbCorner.SelectedIndex]);
        cbCorner.CheckedChanged += (_, _) =>
        {
            cmbCorner.Enabled = cbCorner.Checked;
            main.SetCornerPanelEnabled(cbCorner.Checked);
        };

        // ---- startup ----
        cbStartup = Check("Start with Windows", IsStartupEnabled());
        cbStartup.CheckedChanged += (_, _) => SetStartup(cbStartup.Checked);

        // ---- tip ----
        lblTip = new Label
        {
            Text = "Tip: for a dark widget, enable dark mode inside Google Calendar's own settings (gear icon).",
            ForeColor = Muted,
        };

        // ---- support the project ----
        btnDonate = ActionButton("♥  Support development (PayPal)", Color.FromArgb(242, 139, 130));  // soft red, matching the dark palette
        btnDonate.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://paypal.me/MahdiKazemiesfahani") { UseShellExecute = true });

        btnExit = ActionButton("Exit widget", Fore);
        btnExit.Click += (_, _) => Application.Exit();

        btnAccount = ActionButton("Sign in", Fore);  // refreshed from the real cookie state whenever the form is shown
        btnAccount.Click += OnAccountClick;

        Controls.AddRange([lblAlpha, valAlpha, sldAlpha, cbInteraction, cbLayout, btnMove, cbTitleBar, cbStayDesktop, cbDateIcon, cbBehind, lblShadow, sldShadow, valShadowLeft, valShadowRight,
            lblCorner, cbCorner, cmbCorner, cbStartup, lblTip, btnDonate, btnExit, btnAccount]);

        LayoutForDpi(DeviceDpi);  // provisional (system DPI); redone once the window's monitor is known
    }

    /// <summary>
    /// Lay the window out for <paramref name="dpi"/>. Every size is a 96-DPI value scaled by
    /// dpi/96 and fonts are sized in pixels for the same DPI, so text and boxes always scale
    /// together. Rows advance by measured heights rather than fixed offsets.
    /// </summary>
    private void LayoutForDpi(int dpi)
    {
        int S(int v) => UiScale.Px(v, dpi);

        var body = UiScale.Font(10f, dpi);
        var small = UiScale.Font(9f, dpi);
        var section = UiScale.Font(8.5f, dpi, FontStyle.Bold);

        SuspendLayout();
        // every control gets its font explicitly: on a DPI change WinForms hands children
        // LOCAL fonts of its own, after which they would no longer follow the form's font
        Font = body;
        foreach (Control c in Controls)
            c.Font = body;
        lblAlpha.Font = valAlpha.Font = lblShadow.Font = lblCorner.Font = section;
        valShadowLeft.Font = valShadowRight.Font = lblTip.Font = small;

        int x = S(Gutter);
        int w = S(LogicalWidth - 2 * Gutter);
        int half = w / 2;

        // place a control at its preferred height for the given width; returns the next free y
        int Row(Control c, int left, int top, int width, int gapAfter)
        {
            int h = c.GetPreferredSize(new Size(width, 0)).Height;
            c.SetBounds(left, top, width, h);
            return top + h + S(gapAfter);
        }

        int y = S(16);
        Row(valAlpha, x + half, y, half, 0);  // same line as its section label, right-aligned
        y = Row(lblAlpha, x, y, half, 4);
        // the native trackbar insets its thumb travel; widen it so the track lines up with the text
        sldAlpha.SetBounds(x - S(6), y, w + S(12), 0);  // height: the trackbar's own (AutoSize)
        y += sldAlpha.Height + S(8);
        y = Row(cbInteraction, x, y, w, 6);
        y = Row(cbLayout, x, y, w, 6);
        btnMove.SetBounds(x, y, w, S(30));
        y += S(36);
        y = Row(cbTitleBar, x, y, w, 6);
        y = Row(cbStayDesktop, x, y, w, 6);
        y = Row(cbDateIcon, x, y, w, 6);
        y = Row(cbBehind, x, y, w, 20);

        y = Row(lblShadow, x, y, w, 4);
        int shadowPad = S(EdgeShadowSlider.LogicalHeight / 2);  // the slider pads its track ends by its half height
        sldShadow.SetBounds(x - shadowPad, y, w + 2 * shadowPad, S(EdgeShadowSlider.LogicalHeight));
        y += sldShadow.Height + S(2);
        Row(valShadowRight, x + half, y, half, 0);
        y = Row(valShadowLeft, x, y, half, 20);

        y = Row(lblCorner, x, y, w, 6);
        y = Row(cbCorner, x, y, w, 8);
        cmbCorner.SetBounds(x, y, w, cmbCorner.PreferredHeight);
        y += cmbCorner.PreferredHeight + S(16);

        y = Row(cbStartup, x, y, w, 12);
        y = Row(lblTip, x, y, w, 12);  // wraps to as many lines as the text needs

        btnDonate.SetBounds(x, y, w, S(36));
        y += S(36 + 12);
        int bw = (w - S(20)) / 2;
        btnExit.SetBounds(x, y, bw, S(36));
        btnAccount.SetBounds(x + w - bw, y, bw, S(36));
        y += S(36 + 16);

        var wa = IsHandleCreated ? Screen.FromHandle(Handle).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        AutoScrollMinSize = new Size(0, y);
        ClientSize = new Size(S(LogicalWidth), Math.Min(y, Math.Max(S(250), wa.Height - S(90))));
        ResumeLayout();
    }

    /// <summary>Show — the first time centered on the monitor under the mouse — and bring to front.</summary>
    public void ShowAndActivate()
    {
        if (!IsHandleCreated)
            CenterIn(Screen.FromPoint(Cursor.Position));  // so the window is created at that monitor's DPI
        Show();
        Activate();
    }

    private void CenterIn(Screen screen)
    {
        var wa = screen.WorkingArea;
        Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2), wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyDarkTitleBar(Handle);
        // the handle carries the real monitor DPI: final layout, re-centered for the new size
        LayoutForDpi(DeviceDpi);
        CenterIn(Screen.FromHandle(Handle));
    }

    // dragged onto a monitor with another scale. Cancelled so WinForms doesn't also rescale
    // controls and fonts; we lay out from scratch and take only the suggested position.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
        LayoutForDpi(e.DeviceDpiNew);
        Location = e.SuggestedRectangle.Location;
    }

    public void SyncTrayOptions(bool interaction, bool layout, bool titleBar,
        bool behind, bool showDesktop, bool dateIcon)
    {
        if (IsDisposed) return;
        syncingTrayOptions = true;
        try
        {
            cbInteraction.Checked = interaction;
            cbLayout.Checked = layout;
            btnMove.Enabled = interaction && layout;
            cbTitleBar.Checked = titleBar;
            cbStayDesktop.Checked = showDesktop;
            cbDateIcon.Checked = dateIcon;
        }
        finally { syncingTrayOptions = false; }
        SyncBehindDesktopIcons(behind);
    }

    public void SyncBehindDesktopIcons(bool behind)
    {
        if (IsDisposed || cbBehind.Checked == behind) return;
        syncingBehindDesktopIcons = true;
        try { cbBehind.Checked = behind; }
        finally { syncingBehindDesktopIcons = false; }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            RefreshAccountButton();
        else
            main.EndPreview();  // interactive mode returns to fully opaque once Settings closes
    }

    private async void RefreshAccountButton()
    {
        try
        {
            btnAccount.Text = await main.IsSignedInAsync() ? "Sign out" : "Sign in";
        }
        catch
        {
            btnAccount.Text = "Sign in";  // webview not ready yet; signing in is the sane default
        }
    }

    private async void OnAccountClick(object? sender, EventArgs e)
    {
        if (!await main.IsSignedInAsync())
        {
            main.BeginSignIn();
            Hide();  // get out of the way of the sign-in page
            return;
        }

        var keepBtn = new TaskDialogButton("Sign out, keep account");
        var wipeBtn = new TaskDialogButton("Sign out & delete all data");
        var page = new TaskDialogPage
        {
            Caption = "Google Calendar Desktop Widget",
            Heading = "Sign out of Google?",
            Text = "Keep account: Google remembers this account, so signing back in is quicker.\n\n"
                 + "Delete all data: wipes the widget's entire browser profile (accounts, cookies, cache) — "
                 + "like a fresh install.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            Buttons = { keepBtn, wipeBtn, TaskDialogButton.Cancel },
            AllowCancel = true,
        };

        var result = TaskDialog.ShowDialog(this, page);
        if (result == keepBtn)
            await main.SignOutKeepAccountAsync();
        else if (result == wipeBtn)
            await main.SignOutAsync();
        else
            return;
        RefreshAccountButton();
    }

    // closing the settings window only hides it; the widget keeps running
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    private static Label SectionLabel(string text) => new() { Text = text, ForeColor = Muted };

    // live value next to a section label, or under one end of the edge-shadow slider
    private static Label ValueLabel(ContentAlignment align) => new() { ForeColor = Accent, TextAlign = align };

    private static CheckBox Check(string text, bool isChecked) => new() { Text = text, ForeColor = Fore, Checked = isChecked };

    private static Button ActionButton(string text, Color fore)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = CardBack,
            ForeColor = fore,
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 64, 67);
        return b;
    }

    private static string SideValue(string side, int percent) => percent == 0 ? side + " off" : $"{side} {percent}%";

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
