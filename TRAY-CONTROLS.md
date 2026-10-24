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
- `EVENT_SYSTEM_MINIMIZEEND` is **not** a reliable global Restore All event. It only marks a short-lived hint; **even two or more restored background windows cannot remove TOPMOST while Explorer's desktop remains foreground**. Actual foreground departure confirms the demotion and can use the hint to skip the usual 180 ms settle. Single-window restores and missing events remain covered by foreground handling and the watchdog.
- This workaround depends on Explorer's native behavior and is not a supported exemption from every Show Desktop gesture.

### Installation, runtime requirements and build

**Running the GitHub Actions artifact on Windows 10/11 x64** requires two separate runtimes installed on that machine:

1. **Microsoft .NET Desktop Runtime 8 (x64)** — install the *Desktop Runtime*, not just the ASP.NET Core Runtime or the general .NET Runtime: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
2. **Microsoft Edge WebView2 Runtime** — for the embedded Google Calendar browser: https://developer.microsoft.com/en-us/microsoft-edge/webview2/

Extract the `calendar-widget-tray-win-x64` artifact and run `CalendarWidget.exe`. It is a **framework-dependent** single-file publish (`--self-contained false`): the executable and accompanying files **do not bundle .NET 8 Desktop Runtime**. The WebView2 Runtime is a separate dependency, even when the .NET Desktop Runtime is installed. If either runtime is absent, install it before starting the app. This is intentional to keep the downloadable artifact smaller; no self-contained packaging is planned in this PR.

**Building from source** additionally requires the .NET 8 SDK and a Windows environment:

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


### Actual HWND reconciliation / native backoff / monitor recovery

- In attached wallpaper mode, the once-a-second watchdog checks `GetWindowRect` for the **actual** child HWND rectangle, not only the stored desired geometry. Significant drift is corrected via parent-relative `SetWindowPos`; a failed initial, delayed, or watchdog positioning call is logged, and the desired screen bounds remain available for a later retry.
- Native `SetParent` failure triggers the same exponential (2–30 second) backoff as WorkerW discovery failure. The retry gate is also enforced on orphan recovery; backoff resets only after a successful attachment, explicit user toggle, or display-change event.
- `WM_DISPLAYCHANGE` now handles ordinary top-level interactive and non-WorkerW click-through windows as well as attached wallpapers. A one-second fallback catches late monitor changes; top-level windows are repositioned only when at least 20% of their normal rectangle is inaccessible. Maximized state is preserved, while a changed monitor's working area is used for its restore bounds.
- `SetParent` WS_CHILD/WS_POPUP compatibility remains a known device-testing concern; it was deliberately not changed in this patch because that could regress already working desktop attachment.


### Restore accessibility and deferred interaction

- Recovery checks the usable top-left caption/drag zone **in addition to** overall visible window area. A window whose header is entirely above the monitor is relocated even if over 80% of its rectangle is visible.
- A normal window deliberately spanning two screens is kept in place when it remains mostly visible **and** its grab strip is accessible, consistently at startup, WorkerW attach/recovery and while top-level.
- A transient native detach failure no longer discards the user's request for calendar interaction. The desired setting remains checked, while actual click-through input is retained until verified top-level parenting. The existing 100 ms timer retries only when the shared 2–30 s native backoff allows it. Clicking the interaction toggle again cancels that request.
- The tray icon represents **effective** interaction mode; the menu says `retrying…` while activation is pending. Sign-in does not activate a WorkerW child until detach succeeds. No new timer or thread is added.


### Follow-up self-review: Z-order, system commands and attachment conversion

- A `MINIMIZEEND` notification cannot independently release TOPMOST: the foreground must actually leave the desktop first. Demotion uses `HWND_NOTOPMOST`; for a confirmed ordinary-app switch it additionally inserts below that app's root owner. Never unconditionally use `HWND_BOTTOM` on a reverse Win+D.
- `Move / resize window` also blocks native `WM_SYSCOMMAND` move, size and maximize (plus restore-from-maximized) when locked, while allowing restore from minimization for Explorer gestures. Programmatic display/DPI geometry changes are not intercepted.
- The short-lived restore hint is discarded on a new desktop foreground episode, on unrelated foreground changes, or after use; there is no multi-window burst detector to leak across episodes.
- Both initial and delayed `ScreenToClient` calculations are checked, and the delayed handler revalidates the native parent. Failure skips the unsafe `SetWindowPos` and leaves recovery to the existing WorkerW watchdog.


### Additional self-review fixes: unattached fallback and title strip

- When wallpaper is requested but no WorkerW has yet been found, the widget is still a top-level window. Display-change handling and the existing once-per-second geometry watchdog now recover this fallback if it becomes inaccessible after monitor removal. Reconciliation is skipped during an asynchronous discovery or active reparent, and does not treat a healthy attached WorkerW as a top-level window.
- After verified native WorkerW parenting, title-strip backdrop initialization now occurs **before** the first screen-to-client coordinate conversion. If that conversion fails and the recovery watchdog must take over, the decorative strip is not silently skipped. Startup still retries when the embedded WebView2 environment becomes available.


### Orphaned wallpaper recovery and deferred disable

- On Explorer/WorkerW loss, the actual top-level fallback now reconciles screen geometry while its original wallpaper maximize/restore snapshot is still valid. Original restore bounds remain available on later detach.
- Disabling live wallpaper persists as a desired setting even if SetParent(NULL) fails. The existing 2–30 second native retry gate eventually detaches the widget while preserving safe click-through until native ancestry is confirmed. Settings stays unchecked, and the tray shows 'disabling…' while pending.
- Stable switches to applications with modal dialogs place the calendar below the application's root owner (GA_ROOTOWNER), preventing it from sitting between the popup and main window. The proven reverse Win+D path continues to use HWND_NOTOPMOST alone.


### Latest self-review: safe restore signal

- Restoring two or more background windows programmatically while the desktop remains foreground must not demote the calendar. `MINIMIZEEND` is only a short-lived hint for faster demotion after a *confirmed* desktop foreground exit; a new desktop episode clears it.
- Manual Windows checks: reverse Win+D with 0/1/2+ windows, and two background windows restored without activation. The known `HWND_NOTOPMOST` flicker fix is still in place; the revised event timing needs device testing.


### Show Desktop Z-order corrections (latest self-review)

- A short-lived `MINIMIZEEND` restore hint can accelerate demotion **only after the foreground actually leaves the desktop**. That reverse Win+D path now calls `ReleaseInteractiveDesktopPromotion()` **without a foreground HWND**, meaning a single `HWND_NOTOPMOST` operation and no second app-relative Z-order insertion during the animation. Ordinary app switches without a restore hint still settle for 180 ms and place the calendar below the app's root owner.
- Explicitly unchecking **Stay visible on Show Desktop (experimental)** in the tray is *not* a reverse Win+D animation. It clears temporary TOPMOST and sends the interactive calendar to the bottom of the regular Z-order immediately, so it no longer floats over the desktop. This deliberate user action is the only new `HWND_BOTTOM` use; the reverse-gesture path remains unchanged.
- Manual Windows regression checks: reverse Win+D with one or multiple restored apps, ordinary app foreground switches (including modals), and **Win+D → calendar visible → disable Stay visible via tray → calendar disappears**.


### Foreground Z-order recheck after ambiguous restore hints

- A lone `MINIMIZEEND` can come from an unrelated background process. When the foreground actually leaves the desktop shortly afterwards, the widget still uses only `HWND_NOTOPMOST` during the reverse-gesture animation. It schedules a one-shot **native Z-order verification** after the 1.2-second restoration settling window, using the existing UI polling timer.
- The verification follows `GetWindow(GW_HWNDNEXT)` to check whether the calendar still sits **above** the foreground application's root owner; it repositions below that owner only if needed. If Explorer already established the correct order, it does nothing. A new desktop episode or disabling Show Desktop visibility cancels the pending verification.
- Regression scenario: Win+D → a background process restores one minimized window **without activation** → user clicks another unrelated app within 500 ms. That app must not remain permanently covered by the calendar. Also test actual reverse Win+D with 1/2+ restored windows for flicker.


### Manual Windows verification: input boundaries and active dragging

The current static review found **no confirmed new defect** in the notification/click-through code. The following are release checks, not claims of a tested behavior:

1. While in the interactive calendar, enable a real Google Calendar reminder and verify its notification. Switch to normal click-through mode, trigger another real reminder, click/dismiss it, and verify the **click action** actually runs rather than only closing the notification. Repeat with **Behind desktop icons** enabled.
2. If a notification opens a standalone WebView2 browser popup, login window, permission prompt, or dialog, check that it accepts **mouse and keyboard** while the underlying calendar remains click-through. Check popups opened both *before* and *after* toggling interaction, and repeat after Win+D.
3. If a popup becomes unclickable, record its HWND, **parent** (`GA_PARENT`), **root owner** (`GA_ROOTOWNER`), `GW_OWNER`, and whether `WS_EX_TRANSPARENT` is set. Also record WebView2 Runtime version and the reproduction steps. `EnumChildWindows` in the current `EnableClickThrough` applies transparent input to the main calendar's whole descendant tree; owned or independent popup HWNDs should remain outside that tree. Do **not** globally enable interaction or change notification handlers without an actual reproduction.
4. In interactive mode, unlock **Move / resize window**. Drag the title bar or resize an edge beyond a monitor working area for more than 1 second without releasing the mouse. The watchdog **must not snap the window during the move/resize loop**. Release, then verify that the existing offscreen recovery can restore inaccessible geometry on a subsequent check. Repeat across multiple monitors, at 100% and 150% DPI, and verify normal/maximized bounds are preserved.
5. Finish the outstanding tests for reverse Win+D with 0/1/2+ restored windows, an unrelated background `MINIMIZEEND`, Explorer restart, surviving WorkerW on monitor removal, injected `SetParent(NULL)` failure, and clean Windows launch with the separately installed .NET 8 Desktop Runtime and WebView2 Runtime.


### Why the Windows artifact has one DLL

The Win-x64 download keeps **one native `WebView2Loader.dll`** alongside `CalendarWidget.exe`. It is required to initialize Microsoft Edge WebView2 and **is not** the WebView2 Runtime itself. The WebView2 NuGet package can otherwise publish identical native loader copies both in the application root and under `runtimes/win-x64/native/`. GitHub Actions verifies SHA-256 equality before removing the nested duplicate. If they ever differ, the build fails rather than discarding an unknown binary. Do **not** delete the remaining root `WebView2Loader.dll` when extracting the artifact. The separate **.NET Desktop Runtime 8 x64** and **WebView2 Runtime** installation requirements remain unchanged.
