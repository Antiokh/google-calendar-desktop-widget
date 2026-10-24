## Tray controls (fork addition)

This fork lets you remove the widget's native title bar while preserving the live Google Calendar below it. Right-click the existing system-tray icon for three independent options:

- **Show title bar** — persistent. Hiding it frees the full reserved top strip for calendar content.
- **Enable calendar interaction** — enter or leave click-through wallpaper mode. Double-clicking the tray icon switches interaction. The wallpaper stays under desktop icons when interaction is disabled and *Sit behind desktop icons* is enabled in Settings.
- **Move / resize window** — available only while calendar interaction is enabled. Temporarily reveals the header as a drag handle even if normally hidden. Resize using the window edges. The calendar remains interactive. Choose **Finish moving / resizing** to restore your preferred header visibility.

Existing Google sign-in profiles and settings are retained. The header visibility preference is stored in `%LOCALAPPDATA%\CalendarWidget\settings.json`. Layout mode is not persisted.

### Build

Requires .NET 8 SDK and Windows 10/11:

```powershell
dotnet publish CalendarWidget/CalendarWidget.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Windows CI publishes an artifact from the PR. Exit the installed widget before launching this version (they share the single-instance mutex).

Note: Windows desktop attachment and WebView2 input behavior need manual testing on a real Windows desktop.

### Dynamic tray icon

The system-tray icon is rendered locally (no Google Calendar API requests):

- Calendar-page silhouette, with a small **G** in the blue header.
- Today's **local day number** in the calendar page, updated automatically after midnight (30-second polling with redraw only when needed).
- A white mouse-pointer badge in the lower-right corner when **Enable calendar interaction** is on; absent in wallpaper mode.
- Hover text also shows the full local date and the interaction state.
- The tray icon is recreated immediately when toggling interaction and the old native resources are cleaned up.

Note: on taskbars that display tray icons at only 16×16, the date is prioritized; the G is intentionally small.


### Desktop placement and Win+D

- **Behind desktop icons (live wallpaper)** is now in the tray menu, using the same setting as the Settings dialog.
- **Stay visible on Show Desktop (experimental)** defaults to on for interactive mode. When Explorer's desktop becomes the foreground window, the widget is temporarily raised above it without activation. When another app is focused, it returns to the bottom of the window stack. This is not an officially supported Windows exclusion from Win+D and must be tested with Windows 11 Show Desktop / Win+D / touchpad gestures.
- Normal window bounds and the maximized state are now saved separately. The last normal RestoreBounds rectangle is kept while maximized; the window is restored to that monitor on startup, and WorkerW child coordinates are never written to settings.
- Reparenting async corrections use a generation check to ignore stale callbacks after rapid mode toggles.

### Reduced Show Desktop flicker

- Handle foreground changes with a native out-of-context `EVENT_SYSTEM_FOREGROUND` hook instead of waiting up to 300 ms for a timer (the 1-second timer now only provides fallback).
- Clear `WS_MINIMIZEBOX` from the borderless calendar window. Windows Show Desktop minimizes minimizable windows before raising the desktop; the calendar has no minimize UI.
- When leaving the temporary desktop-visible state, use a single `SetWindowPos(HWND_BOTTOM)`, which both removes the topmost flag and restores bottom Z-order. No intermediate `HWND_NOTOPMOST` move.
- Foreground callback stays rooted for the application's lifetime and is unhooked on exit.
- These changes reduce avoidable composition churn; Explorer and WebView2 might still create a one-frame flash on some Windows builds.


### Native-state reconciliation and reverse-gesture flicker

- The foreground-event callback and the existing 100 ms UI timer now invoke a **single idempotent reconciliation method**. In steady state, the timer reconciles once per second; no new thread or timer is created.
- Read actual `WS_EX_TOPMOST` via `GetWindowLongPtr` and `IsIconic` for the native minimized state, rather than trusting an internal boolean flag. Correct externally changed Z-order, but never issue redundant `SetWindowPos` calls.
- On returning from Show Desktop, wait until the same non-desktop foreground window remains stable for ~180 ms before dropping temporary `TOPMOST` status. The existing 100 ms timer completes this transition even if there is no second foreground event.
- Promotion retries are limited to once per second if Explorer repeatedly overrides Z-order; safety checks retain a once-per-second cadence.
- This addresses a plausible source of reverse-gesture flicker (premature `HWND_BOTTOM`), but Windows shell animations can still briefly cover the widget on some configurations. Please test Win+D, taskbar Show Desktop, trackpad reverse gesture, and switching to regular applications.
