# Codex Quota Tray

**Version:** v1.0

Windows notification-area (system tray) edition of Codex Quota Menu Bar. It reads local Codex session logs only; no network connection or API key is used.

For the macOS version, see [PlutaB/codex-quota-menubar](https://github.com/PlutaB/codex-quota-menubar).

![Codex Quota Tray screenshot](Codex%20Quota%20Tray.png)

## Run

Double-click `CodexQuotaTray.exe`. A compact indicator appears in the lower-right notification area (it may initially be in the `^` overflow menu). Hover or right-click it for quota details, refresh, session-log folder access, startup toggle, and quit.

The indicator shows the first two quota windows in ascending duration (normally 5-hour and 7-day). Green means at least 40% remaining, amber means 20–39%, and red means below 20%.

## Command-line check

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\CodexQuotaTray.ps1 -Once
```

## Startup

Right-click the tray icon and choose **Start at login**. This creates/removes a per-user Run registry entry, pointing to this script. Keep the folder in a permanent location if enabling this option.

## Author

[**PlutaB**](https://github.com/PlutaB)  
Adapted from [BowenZZZZZZZ/codex-quota-menubar](https://github.com/BowenZZZZZZZ/codex-quota-menubar)

## License

Copyright © 2026 PlutaB.  
Licensed under the MIT License. See [LICENSE](LICENSE).
