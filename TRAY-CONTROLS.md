## Tray controls (fork addition)

This fork lets you remove the widget's native title bar while preserving the live Google Calendar below it. Right-click the existing system-tray icon for three independent options:

- **Show title bar** — persistent. Hiding it frees the full reserved top strip for calendar content.
- **Enable calendar interaction** — enter or leave click-through wallpaper mode. Double-clicking the tray icon switches interaction. The wallpaper stays under desktop icons when interaction is disabled and *Sit behind desktop icons* is enabled in Settings.
- **Move / resize window** — temporary layout mode. It detaches the window for adjustment and displays the header as a drag handle even if normally hidden. Browser input is disabled while positioning to avoid editing calendar entries accidentally. Choose **Finish moving / resizing** to return to your selected calendar-interaction state.

Existing Google sign-in profiles and settings are retained. The header visibility preference is stored in `%LOCALAPPDATA%\CalendarWidget\settings.json`. Layout mode is not persisted.

### Build

Requires .NET 8 SDK and Windows 10/11:

```powershell
dotnet publish CalendarWidget/CalendarWidget.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Windows CI publishes an artifact from the PR. Exit the installed widget before launching this version (they share the single-instance mutex).

Note: Windows desktop attachment and WebView2 input behavior need manual testing on a real Windows desktop.
