## Tray-driven desktop calendar (fork)

This fork displays the signed-in Google Calendar web app directly on the Windows desktop, and adds tray-driven controls:

- **Show title bar**: persistent. Hide the bar and reclaim its reserved top strip for the calendar.
- **Enable calendar interaction**: toggle click-through/wallpaper and interactive mode; double-click the tray icon to switch.
- **Move / resize window**: available only when interaction is enabled. Temporarily reveal a drag handle; resize with the normal window-edge grips.
- **Behind desktop icons (live wallpaper)**: attach the passive calendar into Explorer's WorkerW layer (also available in Settings).
- **Stay visible on Show Desktop (experimental)**: keep the interactive calendar visible while Explorer's desktop has foreground focus, without a permanently topmost window. Use the same menu to disable this workaround.
- **Settings…** and **Exit** remain in the tray.

The tray icon shows today's local day, a small G, and a pointer badge when interaction is enabled. The icon refreshes immediately when switching modes, and after the local date changes.

### Window geometry and Explorer recovery

- Persist both the last normal window bounds and whether the window was maximized.
- At startup, clamp a restored normal rectangle to its nearest monitor's **working area**, so resize edges and the header remain accessible after monitor changes.
- During WorkerW attachment, the window may need temporary normal-mode bounds. Preserve its original maximize/restore snapshot across WorkerW destruction, reconnection attempts, and Explorer restarts.
- Freeze painting only during the DPI-sensitive attach/detach transition. Queued correction callbacks unfreeze in `finally`; if WorkerW disappears, the recovery path invalidates old callbacks and explicitly unfreezes, even when discovering the replacement fails.

### Win+D / Show Desktop

- Use `EVENT_SYSTEM_FOREGROUND` to reconcile the foreground desktop and actual `WS_EX_TOPMOST` flag. The existing timer performs a once-per-second fallback check.
- Promotion requests are bounded to avoid repeatedly fighting Explorer over Z-order. Releasing temporary topmost uses **`HWND_NOTOPMOST`**, *not* `HWND_BOTTOM`, because pushing the interactive calendar under every ordinary window caused visible reverse-gesture flicker.
- `EVENT_SYSTEM_MINIMIZEEND` is **not** a reliable global Restore All event. Treat it only as a batch heuristic (at least two distinct restored windows while Explorer's desktop is foreground); a single unrelated restore cannot trigger demotion. Restores of a single window are handled by the foreground event and fallback.
- This workaround depends on Explorer's native behavior and is not a supported exemption from every Show Desktop gesture.

### Build and testing

Requires the .NET 8 SDK and Windows 10/11:

```powershell
dotnet publish CalendarWidget/CalendarWidget.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

GitHub Actions runs on pull requests, on merges/pushes to `main`, and manually. The uploaded artifact is `calendar-widget-tray-win-x64`. Exit the running widget before starting another build because they share a single-instance mutex.

Existing Google sign-in and local settings are preserved in `%LOCALAPPDATA%\CalendarWidget`. Runtime behaviors that require manual verification: reverse Win+D and touchpad gestures; ordinary app restore; WorkerW destruction/Explorer restart; hot-plugging and DPI mismatches; maximized/normal round-trips; Google login persistence. Successful compilation alone does not validate these Windows-shell scenarios.


### Multi-monitor recovery and nonblocking WorkerW discovery

- The one-second watchdog now reconciles **screen-space geometry even while the same WorkerW is alive**, so disconnecting a monitor cannot leave the calendar parked offscreen indefinitely.
- The target bounds use the nearest current monitor's working area. Maximized wallpapers occupy that work area; normal windows are clamped to usable size and position.
- If the Explorer WorkerW is missing, window discovery runs via `Task.Run` instead of blocking the WinForms message loop with native timeouts and sleeps. Only one lookup can be in flight; repeated failure backs off exponentially from 2 to 30 seconds. A display-change notification or a user changing the wallpaper setting resets the backoff.
- The live Settings dialog receives wallpaper setting changes immediately from the tray, with a guard against recursive checkbox callbacks.

### Latest self-review remediation

- **WorkerW parenting:** verify actual `GetAncestor(GA_PARENT)` after every `SetParent` (including detach and orphan recovery). Do not flip app state when the native parent did not change. Failed detach keeps interaction disabled until top-level ancestry is confirmed.
- **WebView2 zoom:** reset to 100% on normal or orphaned detach; reconcile physical monitor DPI against inherited WorkerW DPI on attachment and while checking monitor geometry.
- **Rapid Win+D:** the next foreground transition into Explorer's desktop resets suppression left over from an earlier Restore All.
- **Layout lock:** when Move / resize is unchecked, disable both the title bar's drag/double-click actions and native border/caption hit testing for moving and resizing. Calendar interaction remains enabled.
- **CI:** Windows publish verifies compilation only. Actual parent transitions, mixed-DPI scaling, Explorer restarts, and Show Desktop gestures require on-device verification.
