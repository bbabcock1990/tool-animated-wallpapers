# Animated Desktop Wallpapers Helper

[![Latest release](https://img.shields.io/github/v/release/bbabcock1990/tool-animated-wallpapers?color=6f42c1&label=release)](https://github.com/bbabcock1990/tool-animated-wallpapers/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/bbabcock1990/tool-animated-wallpapers/total?color=2ea043)](https://github.com/bbabcock1990/tool-animated-wallpapers/releases)
[![Release build](https://github.com/bbabcock1990/tool-animated-wallpapers/actions/workflows/release.yml/badge.svg)](https://github.com/bbabcock1990/tool-animated-wallpapers/actions/workflows/release.yml)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D6)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)

Render **any HTML page — CSS animations, JavaScript, and `<canvas>` — as your live Windows 11 desktop background**, behind your desktop icons. No third-party wallpaper app required.

The app hosts **WebView2** inside borderless WinForms windows, parents them to the Windows desktop wallpaper layer, and keeps the wallpaper correct across monitor changes. Optional modules can add live widgets such as your Outlook calendar or Azure product updates without forking the base wallpaper.

![Animated aurora wallpaper](static-wallpaper.png)

## What's included

- 🎨 **HTML/CSS/JS wallpapers** — render local HTML files or public URLs, including canvas and CSS animations.
- 🖥️ **Dynamic multi-monitor support** — one wallpaper window per selected display; dock/undock, display layout, and resolution changes rebuild automatically.
- 📦 **One-command install** — downloads a self-contained build, installs WebView2 if needed, sets the wallpaper, and can start at login.
- 🧩 **Module runtime** — discovers `modules/<id>/module.json`, injects enabled module assets live, and refreshes module data in-process.
- 🗓️ **Outlook Calendar module** — today's M365/Outlook events with WorkIQ or MSAL/WAM sign-in.
- ☁️ **Azure Updates module** — live Azure product updates filtered by domain/status, with clickable overlay cards and a generated standalone updates page.
- 🖱️ **Interactive overlay** — press **Ctrl+Alt+K** to make tagged module panels clickable while the rest of the desktop remains usable.
- 🙈 **Hide widgets hotkey** — press **Ctrl+Alt+H** to hide/show module panels for a clean desktop or screen share.
- 🛠️ **Single executable management** — `set`, `stop`, `autostart`, and `module` are all subcommands of `HtmlWallpaper.exe`; no separate tray helper or scheduled task is required.

## Install

Open **Windows PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/tool-animated-wallpapers/main/install.ps1 | iex
```

The installer:

- downloads a self-contained release to `%LOCALAPPDATA%\AnimatedDesktopWallpaper`,
- installs the WebView2 Runtime if it is missing,
- sets `wallpaper.html` as the animated wallpaper,
- enables start-at-login unless you opt out,
- optionally enables the Outlook Calendar module and lets you choose **Auto**, **WorkIQ**, or **MSAL/WAM** sign-in.

Prefer double-clicking? Download the repo ZIP, unzip it, and run **`install.bat`**.

### Non-interactive install options

```powershell
.\install.ps1 -Calendar          # enable the calendar module, no prompt
.\install.ps1 -NoCalendar        # skip the calendar module, no prompt
.\install.ps1 -NoAutostart       # don't start at login
.\install.ps1 -InstallDir "D:\ADW"
.\install.ps1 -Tag v1.0.0        # install a specific release
```

### Uninstall

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/tool-animated-wallpapers/main/uninstall.ps1 | iex
```

This stops the wallpaper, removes the login entry, cleans up legacy calendar task/tray artifacts from older versions, and deletes the install folder.

## Manage the wallpaper

After install, the executable lives at `%LOCALAPPDATA%\AnimatedDesktopWallpaper\HtmlWallpaper.exe`:

```powershell
$exe = "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\HtmlWallpaper.exe"

# Change the wallpaper to any local HTML file or URL
& $exe set "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\wallpaper.html"
& $exe set https://example.com

# Target a single display instead of every monitor
& $exe set "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\wallpaper.html" --primary
& $exe set "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\wallpaper.html" --monitor 1

# Stop / restore the normal desktop
& $exe stop

# Start-at-login on / off
& $exe autostart on --source "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\wallpaper.html"
& $exe autostart off
```

Only one wallpaper instance runs at a time. Starting a new one replaces the previous instance.

## Modules at a glance

Modules are optional, self-contained widgets layered on top of any wallpaper. The host discovers module folders, writes a browser-readable registry, injects enabled CSS/JS assets, refreshes module data, and exposes tray toggles plus global hotkeys.

| Module | Folder | What it does | Refresh |
| --- | --- | --- | --- |
| **Outlook Calendar** | [`modules/calendar/`](modules/calendar/) | Shows today's Outlook/M365 meetings, all-day events, meeting status, location/online indicators, and `Now`/`Next`/`Tentative` pills. | Built-in C# Graph refresher every 15 minutes. |
| **Azure Updates** | [`modules/azure-updates/`](modules/azure-updates/) | Shows live Azure product updates filtered by Azure domain and status, highlights new items, and makes update cards clickable in interactive mode. | PowerShell RSS refresher every 60 minutes. |

For the full module deep dive — architecture, manifests, generated files, current modules, and extension points — see **[`modules/README.md`](modules/README.md)**.

### Common module commands

```powershell
HtmlWallpaper.exe module list              # show installed modules + enabled state
HtmlWallpaper.exe module enable <id>       # refresh/sign in if needed, then turn on
HtmlWallpaper.exe module disable <id>      # turn off; data/assets stay on disk
HtmlWallpaper.exe module refresh <id>      # refresh a module's data now
HtmlWallpaper.exe module add <folder>      # install a module folder, then enable it
HtmlWallpaper.exe module registry          # rebuild modules/registry.js
```

The tray icon mirrors module state with a checkable item per toggleable module. Its **Settings...** dialog lets you customize the engine hotkeys for clickable mode and hide/show widgets.

## Included wallpaper examples

- **`wallpaper.html`** — the default aurora wallpaper with animated ribbons, a particle constellation network, and a live clock/date/greeting.
- **`wallpaper-core.css`** + **`wallpaper-core.js`** — shared base animation assets used by `wallpaper.html`.
- **`demo.html`** — CSS-animated gradient, JS canvas starfield, and clock.
- **`static-wallpaper.png`** — matching still image for moments when Windows intentionally shows the OS wallpaper, such as Win+D or sign-in.

## Build from source

You only need this if you want to develop or customize the engine.

**Requirements:** Windows 10/11, WebView2 Runtime, .NET 8 SDK.

```powershell
# Build
dotnet build -c Release

# Run against a local HTML file or URL
.\bin\Release\net8.0-windows\HtmlWallpaper.exe .\wallpaper.html
.\bin\Release\net8.0-windows\HtmlWallpaper.exe https://example.com

# Management verbs are built into the exe
.\bin\Release\net8.0-windows\HtmlWallpaper.exe set .\wallpaper.html
.\bin\Release\net8.0-windows\HtmlWallpaper.exe stop
```

Produce the same single-file, self-contained build the installer ships:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## How it works

Windows 11's modern raised desktop uses `Progman`, `WorkerW`, and a layered `SHELLDLL_DefView` icon surface. HtmlWallpaper creates a WebView2-backed WinForms window, parents it to the wallpaper `WorkerW` layer below the icons, and makes it fully opaque/layered so DWM composites the browser surface correctly behind the desktop.

At runtime it:

1. locates or creates the wallpaper `WorkerW` layer under the desktop icons,
2. enumerates live monitors directly from Win32 so dock/undock changes are reliable,
3. creates one wallpaper form per selected monitor,
4. writes `modules/registry.js` and starts the tray plus in-process module scheduler,
5. optionally creates a transparent top-most interactive overlay on the primary monitor,
6. polls the live monitor signature and rebuilds wallpaper/overlay windows when display topology changes.

The wallpaper windows are click-through because they sit behind the desktop icons. The interactive overlay solves clickable widgets by rendering the same module markup in a clipped top-most window: only rectangles tagged as module panels receive clicks; every other pixel passes through to the desktop.

## Notes

- **Show desktop (Win+D) / Aero Peek** deliberately hides windows to reveal the OS wallpaper, so you may briefly see `static-wallpaper.png`; the animation returns when you leave show-desktop mode.
- Heavy pages use GPU/CPU like any browser tab. Keep animations reasonable for battery life.
- WebView2 user data is stored in `%LOCALAPPDATA%\HtmlWallpaper\WebView2`.
- Generated module state and data files are git-ignored because they may contain personal calendar data or local preferences.

## Credits

The raised-desktop WorkerW handling follows the technique documented by Microsoft and implemented in [Lively Wallpaper](https://github.com/rocksdanister/lively) (`WinDesktopCore.cs`).
