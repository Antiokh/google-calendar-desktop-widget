// Google Calendar Desktop Widget
// Copyright (C) 2026 Mahdi Kazemiesfahani
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace CalendarWidget;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_POPUP = 0x80000000L;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const long WS_EX_TRANSPARENT = 0x00000020;  // mouse passes through
    public const long WS_EX_NOACTIVATE = 0x08000000;   // never steals keyboard focus
    public const long WS_EX_TOPMOST = 0x00000008;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_SYSCOLORCHANGE = 0x0015;
    public const int WM_THEMECHANGED = 0x031A;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MOVE = 0xF010;
    public const int SC_SIZE = 0xF000;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_RESTORE = 0xF120;
    public const int HTCAPTION = 2;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOSIZE_NOMOVE_NOACTIVATE = 0x0013;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    public static void AddExStyle(IntPtr hwnd, long style)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & style) != style)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | style));
    }

    public static void RemoveExStyle(IntPtr hwnd, long style)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & style) != 0)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~style));
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Input hit-testing descends into child HWNDs. WebView2 hosts its input in its own
    /// child window tree (Chrome_WidgetWin_*), so unlike the Chrome PWA case (top-level
    /// handles all input), WS_EX_TRANSPARENT must be on EVERY window in the hit path.
    /// Windows that gain the style are recorded in <paramref name="applied"/> — some of
    /// Chrome's windows (e.g. the Intermediate D3D compositing surface) carry
    /// WS_EX_TRANSPARENT natively, and stripping it from them blanks the rendering.
    /// </summary>
    public static void EnableClickThrough(IntPtr root, ISet<IntPtr> applied)
    {
        Apply(root);
        EnumChildWindows(root, (h, _) => { Apply(h); return true; }, IntPtr.Zero);

        void Apply(IntPtr h)
        {
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TRANSPARENT) == 0)
            {
                SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT));
                applied.Add(h);
            }
        }
    }

    /// <summary>Remove WS_EX_TRANSPARENT only from the windows EnableClickThrough added it to.</summary>
    public static void DisableClickThrough(ISet<IntPtr> applied)
    {
        foreach (var h in applied)
            RemoveExStyle(h, WS_EX_TRANSPARENT);  // destroyed handles fail silently, which is fine
        applied.Clear();
    }

    public static bool SendToBottom(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE_NOMOVE_NOACTIVATE);

    /// <summary>Read the OS state rather than trusting our last SetWindowPos call.</summary>
    public static bool IsTopmost(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    // ---- live-wallpaper mode: reparent the widget behind the desktop icons ----

    private const uint GA_PARENT = 1;
    private const uint GA_ROOTOWNER = 3;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>Resolve an active dialog to its root owner for safe Z-order placement.</summary>
    public static IntPtr RootOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        return root != IntPtr.Zero && IsWindow(root) ? root : hwnd;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>
    /// SetParent and WS_CHILD/WS_POPUP are independent Win32 operations.
    /// Return whether the native parent reached its requested target, NOT a
    /// combined parent/style result. The caller must never mistake an attached
    /// WorkerW child for a still-top-level HWND if a style update fails.
    /// </summary>
    public static bool TrySetParent(IntPtr child, IntPtr parent, out int error)
    {
        error = 0;
        if (child == IntPtr.Zero || !IsWindow(child))
        {
            error = 1400; // ERROR_INVALID_WINDOW_HANDLE
            return false;
        }

        bool attaching = parent != IntPtr.Zero;
        IntPtr originalParent = GetAncestor(child, GA_PARENT);
        if (attaching && !TryRepairParentStyle(child, attached: true, out error))
        {
            // A frame refresh can fail AFTER SetWindowLongPtr has already
            // applied WS_CHILD. Roll back before returning unsuccessful so
            // callers never receive an apparently top-level child-style HWND.
            int originalError = error;
            if (!TryRepairParentStyle(child, attached: false, out int rollbackError))
                System.Diagnostics.Debug.WriteLine(
                    $"WorkerW pre-attach style rollback failed: {rollbackError}");
            error = originalError;
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        _ = SetParent(child, parent);
        int parentError = Marshal.GetLastPInvokeError();
        IntPtr actualParent = GetAncestor(child, GA_PARENT);
        bool parentMatches = actualParent == (attaching ? parent : GetDesktopWindow());

        if (!parentMatches)
        {
            error = parentError == 0 ? 87 : parentError;
            // SetParent did not move the window: restore the original top-level
            // style. A stray third-party parent is NOT treated as success.
            if (attaching && actualParent == originalParent)
                TryRepairParentStyle(child, attached: false, out _);
            return false;
        }

        // A successful SetParent must be reported as success even if a
        // subsequent style/frame update fails; otherwise the caller would
        // clear its snapshot and manipulate a WorkerW child as a top-level form.
        if (!TryRepairParentStyle(child, attaching, out int styleError))
        {
            error = styleError;
            System.Diagnostics.Debug.WriteLine(
                $"SetParent reached target, but WS_CHILD/WS_POPUP repair failed: {styleError}");
        }
        return true;
    }

    /// <summary>
    /// Idempotent style reconciliation; may be retried by the guarded WorkerW
    /// watchdog after a transient failure. When a previous SWP_FRAMECHANGED
    /// failed after applying style bits, forceFrameRefresh retries it even if
    /// the style already matches. Check style readback before reporting success.
    /// </summary>
    public static bool TryRepairParentStyle(IntPtr child, bool attached, out int error,
        bool forceFrameRefresh = false)
    {
        error = 0;
        if (child == IntPtr.Zero || !IsWindow(child))
        {
            error = 1400;
            return false;
        }
        long current = GetWindowLongPtr(child, GWL_STYLE).ToInt64();
        long desired = attached
            ? (current | WS_CHILD) & ~WS_POPUP
            : (current & ~WS_CHILD) | WS_POPUP;
        int styleError = 0;
        if (current != desired)
        {
            Marshal.SetLastPInvokeError(0);
            _ = SetWindowLongPtr(child, GWL_STYLE, new IntPtr(desired));
            styleError = Marshal.GetLastPInvokeError();
        }
        else if (!forceFrameRefresh)
            return true;
        long actual = GetWindowLongPtr(child, GWL_STYLE).ToInt64();
        bool styleMatches = attached
            ? (actual & WS_CHILD) != 0 && (actual & WS_POPUP) == 0
            : (actual & WS_CHILD) == 0 && (actual & WS_POPUP) != 0;
        if (!styleMatches)
        {
            error = styleError == 0 ? 87 : styleError;
            return false;
        }
        if (!SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE_NOMOVE_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        return true;
    }

    public static bool HasParent(IntPtr child, IntPtr parent) =>
        GetAncestor(child, GA_PARENT) == parent;

    public static bool HasDesktopParent(IntPtr child) =>
        child != IntPtr.Zero && IsWindow(child) &&
        GetAncestor(child, GA_PARENT) == GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// Keep a top-level click-through calendar behind ordinary application
    /// windows but ABOVE Explorer's desktop surface. HWND_BOTTOM does not
    /// guarantee the latter and can make the widget completely disappear.
    /// Never reorder WorkerW children; their native parent determines the
    /// proper layer behind the desktop icons.
    /// </summary>
    public static bool PlacePassiveAboveDesktop(IntPtr hwnd, bool onlyIfHidden = false)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) ||
            GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow())
            return false;

        // EnumWindows walks the top-level Z-order from front to back. The
        // first visible Progman/WorkerW is the top desktop shell layer; placing
        // immediately above it keeps the widget visible without covering apps.
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            if (candidate != hwnd && IsWindowVisible(candidate) &&
                IsDesktopForeground(candidate))
            {
                desktop = candidate;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (desktop == IntPtr.Zero)
            return false; // don't blindly fall back to HWND_BOTTOM

        // Also recover a passive window incorrectly raised above the active
        // ordinary application's root owner, without fighting correctly ordered
        // windows or touching WorkerW children.
        if (onlyIfHidden && IsAboveInZOrder(hwnd, desktop))
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == hwnd ||
                IsDesktopForeground(foreground))
                return true;
            IntPtr appRoot = RootOwner(foreground);
            if (appRoot == IntPtr.Zero || appRoot == hwnd || IsTopmost(appRoot) ||
                !IsAboveInZOrder(hwnd, appRoot))
                return true;
            return SetWindowPos(hwnd, appRoot, 0, 0, 0, 0,
                SWP_NOSIZE_NOMOVE_NOACTIVATE);
        }

        IntPtr beforeDesktop = GetWindow(desktop, GW_HWNDPREV);
        if (beforeDesktop == hwnd)
            return true; // already exactly above the desktop layer
        // IntPtr.Zero is HWND_TOP in SetWindowPos, not "before desktop".
        // If the desktop is already first, never promote a passive window
        // over ordinary apps simply to force its relative placement.
        if (beforeDesktop == IntPtr.Zero)
            return false;
        if (IsTopmost(beforeDesktop))
            return false; // do not accidentally promote into the topmost band

        return SetWindowPos(hwnd, beforeDesktop, 0, 0, 0, 0,
            SWP_NOSIZE_NOMOVE_NOACTIVATE);
    }

    public static bool IsAboveInZOrder(IntPtr upper, IntPtr lower)
    {
        if (upper == IntPtr.Zero || lower == IntPtr.Zero || upper == lower ||
            !IsWindow(upper) || !IsWindow(lower))
            return false;

        var current = upper;
        for (int i = 0; i < 1024; i++)
        {
            current = GetWindow(current, GW_HWNDNEXT);
            if (current == IntPtr.Zero)
                return false;
            if (current == lower)
                return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // Foreground changes arrive on our WinForms message-loop thread, much
    // sooner than a periodic foreground-window poll.
    public delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>Win+D / Show Desktop activates the Explorer desktop window.</summary>
    public static bool IsDesktopForeground(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return false;
        var cls = new System.Text.StringBuilder(32);
        GetClassNameW(window, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW";
    }

    /// <summary>Is this handle a live WorkerW? Used to detect when the shell recreated it and orphaned us.</summary>
    public static bool IsWorkerW(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;
        var sb = new System.Text.StringBuilder(16);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString() == "WorkerW";
    }

    public const int WM_SETREDRAW = 0x000B;
    public const int WM_DISPLAYCHANGE = 0x007E;

    /// <summary>Suspend/resume painting so a multi-step reparent+DPI transition doesn't flicker.</summary>
    public static void SetRedraw(IntPtr hwnd, bool on) =>
        SendMessage(hwnd, WM_SETREDRAW, new IntPtr(on ? 1 : 0), IntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    /// <summary>Force a full repaint of the window and all children after resuming redraw.</summary>
    public static void RepaintAll(IntPtr hwnd) =>
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100 | 0x400);  // INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW|FRAME

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// The Wallpaper-Engine trick: message 0x052C makes Progman split off a WorkerW
    /// window that sits behind the desktop icons (SHELLDLL_DefView) but above the
    /// wallpaper. A window reparented into it renders under the icons. Classic layout:
    /// the WorkerW is the sibling AFTER the window hosting SHELLDLL_DefView; on
    /// Win11 24H2 it is a CHILD of Progman instead. Returns IntPtr.Zero when the
    /// shell doesn't cooperate — callers must degrade to normal bottom-pinning.
    /// </summary>
    public static IntPtr FindDesktopWorkerW()
    {
        IntPtr progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);

        // explorer creates the WorkerW asynchronously after the message; poll briefly
        for (int attempt = 0; attempt < 20; attempt++)
        {
            IntPtr workerW = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (FindWindowExW(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr sibling = FindWindowExW(IntPtr.Zero, top, "WorkerW", null);
                    if (sibling != IntPtr.Zero)
                        workerW = sibling;
                }
                return true;
            }, IntPtr.Zero);

            if (workerW == IntPtr.Zero)
                workerW = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);  // Win11 24H2 layout
            if (workerW != IntPtr.Zero)
                return workerW;
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    // custom title bar: window dragging + resize hit-test codes
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public const int WM_NCHITTEST = 0x84;
    public const int WM_NCCALCSIZE = 0x83;
    public const int WM_GETMINMAXINFO = 0x24;
    public const int HTCLIENT = 1;
    public const uint SWP_NOZORDER_NOACTIVATE = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>
    /// Real outer HWND coordinates in screen space. Both this P/Invoke and the
    /// WinForms Screen coordinates run in the widget's per-monitor DPI context.
    /// </summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out Rectangle bounds)
    {
        if (GetWindowRect(hwnd, out var rect))
        {
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = Rectangle.Empty;
        return false;
    }

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// DPI of the monitor the window is (mostly) on. Unlike GetDpiForWindow this is the
    /// physical monitor's scale even while we're reparented into the WorkerW, whose DPI
    /// context is the primary monitor's.
    /// </summary>
    public static int MonitorDpi(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        return GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out uint dpi, out _) == 0 ? (int)dpi : 96;
    }

    /// <summary>Effective DPI of the screen that contains a screen-space rectangle.</summary>
    public static int MonitorDpiAt(Rectangle bounds)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        IntPtr monitor = MonitorFromPoint(center, 2 /*MONITOR_DEFAULTTONEAREST*/);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? (int)dpi : 96;
    }

    /// <summary>
    /// True if the window is maximized, read from Win32 rather than WinForms' WindowState —
    /// which can still report Normal mid-transition, making WM_NCCALCSIZE skip the frame
    /// inset and let the title bar slide off the top of the screen.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// Thickness of the invisible resize frame at this window's DPI. Windows positions a
    /// maximized WS_THICKFRAME window so this frame hangs OUTSIDE the monitor; the client
    /// rect must be inset by it in WM_NCCALCSIZE or the content edges land off-screen.
    /// </summary>
    public static (int X, int Y) MaximizedFrameThickness(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        int pad = GetSystemMetricsForDpi(92 /*SM_CXPADDEDBORDER*/, dpi);
        return (GetSystemMetricsForDpi(32 /*SM_CXSIZEFRAME*/, dpi) + pad,
                GetSystemMetricsForDpi(33 /*SM_CYSIZEFRAME*/, dpi) + pad);
    }

    // window styles that enable native behaviors (Aero Snap, drag-to-top maximize)
    // on an otherwise borderless window; the visual frame is removed via WM_NCCALCSIZE
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Win11 niceties; both calls fail harmlessly (square corners / light titlebar) on older builds
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        int pref = 2;  // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    public static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
