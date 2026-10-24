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
