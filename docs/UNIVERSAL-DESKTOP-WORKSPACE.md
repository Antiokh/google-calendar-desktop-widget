# Universal desktop workspace — independent fork plan

**Status:** design direction, not yet an implemented product. This fork retains GNU AGPL-3.0-or-later for the original and derivative client code. We are not granting the original maintainer extra proprietary relicensing rights for contributions to our fork.

## Product idea

Turn the Windows/WebView2 desktop calendar widget into a configurable, optionally wallpaper-attached workspace capable of hosting authorized web applications (e.g. a corporate task board) instead of a hard-coded Google Calendar URL. Commercial value can come from configuration, management, enterprise connectors, deployment, support and services rather than closed-sourcing the AGPL desktop client.

## Current code constraints (observed in this fork)

- `MainForm.cs` currently owns the fixed `CalendarUrl`, the WebView2 instance and sign-in flow, tray/menu, preferences, click-through, Z-order, WorkerW parenting, window geometry, timers and focus hooks in one large class (~2,000 lines).
- `AppSettings` persists app-specific window settings as JSON under the current CalendarWidget profile; a general product needs versioned settings migration, configurable content profiles and an explicit URL allowlist.
- `WebView2` currently uses one fixed `WebViewDataFolder`, coupling Google sign-in data to a single local browser profile. Do not share cookies and storage between unrelated content sources/tenants by default.
- Windows desktop composition is not a stable public wallpaper embedding contract: WorkerW/Explorer attachment, window reparenting, Win+D, DPI and multi-monitor movement need dedicated Windows integration tests, recovery and predictable failure fallback.
- `TitleBar` and `HoverPanel` currently understand the calendar-specific user experience. Keep shell UI independent of hosted content.
- The existing CI proves compilation but does not validate Explorer shell runtime scenarios.

## Target modules (separate responsibilities first, new features second)

1. **ContentHost**: configurable absolute HTTPS app URL(s), navigation/redirect restrictions, isolated WebView2 user-data folders per profile, loading/error screens, downloads/popups/permissions handled explicitly.
2. **DesktopShell**: passive background placement above the desktop or optional WorkerW wallpaper attachment, with a clear state machine and safe detach/reattach.
3. **InteractionController**: interactive/pass-through, independent persistent move/resize lock, title-bar behavior, Win+D handling, accessibility.
4. **WindowPlacement**: monitor-aware saved normal/maximized bounds, DPI restoration and offscreen corrections only on startup/display changes; keep Z-order recovery separate.
5. **Preferences**: versioned JSON schema, migration from existing `settings.json`, per-profile settings, synchronization between Settings/tray and a testable model.
6. **EnterpriseConfig** (future): signed/validated tenant and app configuration, role-based presets, remote policy *with user-visible controls*, deployment packages and diagnostics.
7. **Observability & tests**: compile CI, state-machine unit tests, native abstraction for mockable geometry/Z-order calls, Windows real-device matrix.

An example first configurable profile:
```json
{
  "id": "work",
  "name": "Team workspace",
  "startUrl": "https://tasks.example.com/",
  "allowedOrigins": ["https://tasks.example.com"],
  "webViewProfile": "work",
  "mode": "passive"
}
```
This is an architectural sketch, **not** a current configuration format.

## Phased implementation

- **Phase A — hardening:** validate the pending Windows shell fixes, avoid moving windows during user drag, stabilize passive placement and WorkerW recovery; add diagnostics and tests.
- **Phase B — reusable host:** move fixed Calendar URL and Google-specific sign-in behavior out of `MainForm`; implement one configurable origin with explicit permission controls and profile isolation while retaining Google Calendar as the default preset.
- **Phase C — multi-profile:** chooser, per-profile storage, tray control/state, safe switching, monitor-specific placement, migration.
- **Phase D — enterprise:** optional separately developed administrative service, device policies, application catalog, SSO, deployment, tenant isolation, auditing. Design client/server licensing boundaries carefully before promising proprietary components.

## Security and licensing decisions before release

- Preserve original copyright, attribution, AGPL text and notices; identify authorship for new contributions.
- Selling copies of an AGPL derivative is permitted, but recipients must receive the AGPL-required source and redistribution rights. If a modified AGPL-covered version is offered for remote interaction, inspect AGPL section 13. **Do not assume a separate repository or REST API alone makes a proprietary component legally independent.**
- Avoid using the original author's or Google's logos/marks to suggest endorsement. Design new name, icon, install identity and website for an independent product.
- Require trusted HTTPS URLs or explicit admin policy; harden navigation, external links, file downloads, notifications, permissions and WebView2 profiles before hosting sensitive task/CRM interfaces.
- Clarify enterprise login/token handling and where secrets may persist. Document cleanup/revocation and update policy.
- Check third-party libraries, WebView2 runtime redistribution and any target web application terms independently.

## Relationship to upstream

The upstream author may continue his original project, including separately licensed versions **of code for which he has the rights**. We can continue this fork commercially under AGPL without accepting his separate commercial relicensing clause for our changes. Any future upstream contribution agreement must be considered independently, and none is implied by this roadmap.

References: https://www.gnu.org/licenses/agpl-3.0.en.html and https://www.gnu.org/licenses/gpl-faq.en.html.
