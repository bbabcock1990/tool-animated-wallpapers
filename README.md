# Animated Desktop Wallpapers Helper

[![Latest release](https://img.shields.io/github/v/release/bbabcock1990/Animated-Desktop-Wall-Papers-Helper?color=6f42c1&label=release)](https://github.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/total?color=2ea043)](https://github.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/releases)
[![Release build](https://github.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/actions/workflows/release.yml/badge.svg)](https://github.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/actions/workflows/release.yml)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D6)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)

Render **any HTML page — CSS animations, JavaScript, and `<canvas>` — as your live Windows 11 desktop background**, behind your desktop icons. No third-party wallpaper app required.

It hosts **WebView2** (the Edge engine already on Windows 11) inside a full-screen window parented to the desktop using the Windows shell **WorkerW / Progman** technique.

![Animated aurora wallpaper](static-wallpaper.png)

## Features

- 🎨 **Any web page as wallpaper** — HTML/CSS animations, JavaScript, and `<canvas>`, rendered live behind your icons.
- 🖥️ **Multi-monitor, dynamic** — one window per display, and it rebuilds automatically on dock/undock or resolution changes.
- 📦 **One-command install** — a self-contained build (no .NET required); installs WebView2 if missing and starts at login.
- 🧩 **Modules** — layer optional features on any wallpaper (e.g. an Outlook calendar overlay) and toggle them from a system-tray icon or a global hotkey.
- 🔐 **Locked-down-tenant friendly calendar** — signs in via WorkIQ (reuses your Windows M365 sign-in) or MSAL/WAM, so it works even where generic Graph clients need admin consent.
- 🛠️ **Everything in one exe** — `set`, `stop`, `autostart`, and `module` are subcommands of `HtmlWallpaper.exe`; no helper scripts or scheduled tasks.

## Install (one command)

Open **Windows PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/install.ps1 | iex
```

That's it. No cloning, no build tools, no admin. The installer:

- downloads a **self-contained build** (no .NET install required) to `%LOCALAPPDATA%\AnimatedDesktopWallpaper`,
- installs the **WebView2 runtime** if it's missing (preinstalled on Windows 11),
- sets the animated wallpaper and **starts it at login**,
- optionally enables the **Outlook calendar module** — it asks first, and if you say yes it lets you choose a sign-in method (WorkIQ, which reuses your existing Windows M365 sign-in, or the Windows account broker/MSAL), then refreshes it automatically in the background.

Prefer double-clicking? Download the repo (green **Code ▸ Download ZIP**), unzip, and run **`install.bat`**.

### Uninstall

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/uninstall.ps1 | iex
```

Stops the wallpaper, removes the login entry, cleans up any legacy calendar task/tray from older versions, and deletes the install folder.

### Manage it

Everything is driven by `HtmlWallpaper.exe` itself — no helper scripts. After install it lives in `%LOCALAPPDATA%\AnimatedDesktopWallpaper`:

```powershell
$exe = "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\HtmlWallpaper.exe"

# Change the wallpaper (any local HTML file or a URL)
& $exe set "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\demo.html"
& $exe set https://example.com

# Stop / restore the normal desktop
& $exe stop

# Start-at-login on / off
& $exe autostart on --source "$env:LOCALAPPDATA\AnimatedDesktopWallpaper\wallpaper.html"
& $exe autostart off
```

- `set` and `autostart on` take `--primary` (primary monitor only) or `--monitor N` (0-based) to target a single display; the default is every monitor.
- **Dynamic multi-monitor**: it watches for display changes (dock/undock, monitor added/removed, resolution or layout changes) and rebuilds the per-monitor windows automatically — no restart needed.
- Only one instance runs at a time — launching a new one replaces the old.

### Non-interactive install options

Handy when scripting or when you already know what you want (use `install.bat` or a local `install.ps1`):

```powershell
.\install.ps1 -Calendar          # enable the calendar module, no prompt
.\install.ps1 -NoCalendar        # skip the calendar module, no prompt
.\install.ps1 -NoAutostart       # don't start at login
.\install.ps1 -InstallDir "D:\ADW"
.\install.ps1 -Tag v1.0.0        # install a specific release
```

## Build from source (optional)

You only need this to develop or customize the engine itself.

**Requirements:** Windows 10/11, WebView2 Runtime (preinstalled on Windows 11), .NET 8 SDK.

```powershell
# Build
dotnet build -c Release

# Run against any local HTML file or URL
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

## Included examples

- **`wallpaper.html`** — aurora ribbons, a particle constellation network, and a live clock/date/greeting. A thin shell over the shared core (`wallpaper-core.css` + `wallpaper-core.js`).
- **`demo.html`** — CSS-animated gradient + JS `<canvas>` starfield + clock.
- **`static-wallpaper.png`** — a matching still image for the OS wallpaper (shown during Win+D / login).

### Shared core

The base animation (aurora + particle network + clock) lives in **`wallpaper-core.css`** and **`wallpaper-core.js`** so it can be reused by any variant. Font sizes use `vmin` so text scales by the shorter screen edge and doesn't blow up on ultra-wide / spanned canvases.

## Modules

Modules are optional, self-contained features you layer on **any** wallpaper — a calendar, a to-do list, a stock ticker, weather, anything. Each is a folder under `modules/` with a manifest and web assets. The host discovers them, the running wallpaper loads the enabled ones, and a single CLI turns them on and off. No forking of the base animation, no separate scripts or scheduled tasks.

### Managing modules

Everything is driven by the app itself:

```powershell
HtmlWallpaper.exe module list              # show installed modules + state
HtmlWallpaper.exe module enable <id>       # sign in / fetch data, then turn on
HtmlWallpaper.exe module disable <id>      # turn off (data + assets stay)
HtmlWallpaper.exe module refresh <id>      # refresh a module's data now
HtmlWallpaper.exe module add <folder>      # install a module folder, then enable it
HtmlWallpaper.exe module registry          # rebuild modules/registry.js
```

The running wallpaper hosts a **system-tray icon** with a checkable item per toggleable module and a **Settings…** dialog (global hotkey + which module the hotkey targets). Because the tray, the data refresh timer, and sign-in all live **inside `HtmlWallpaper.exe`**, closing that one process cleans everything up — there's no separate tray process, VBScript shim, or `schtasks` job.

### How a module is wired

```
modules/
  registry.js          # generated: window.WALLPAPER_REGISTRY (read by the browser)
  state.json           # generated: per-user enabled flags + tray hotkey (git-ignored)
  <id>/
    module.json        # manifest (read only by the C# host)
    <assets>.css/.js   # injected into the wallpaper when the module is enabled
    data.js            # generated: window.<...>_DATA written by the refresher (git-ignored)
```

`module-loader.js` (already included by `wallpaper.html`) reads `modules/registry.js` via a `<script>` tag — WebView2 blocks `file://` XHR, so a generated JS file is used instead of JSON — and lazily injects each **enabled** module's CSS/JS. It re-polls the registry every ~2s, so toggling a module shows/hides it within seconds with no page reload and no restarted animation. Modules read their live state through a small `window.Wallpaper` SDK (`isEnabled`, `onStateChange`, `settings`, `base`).

A `module.json` looks like:

```json
{
  "id": "calendar",
  "name": "Outlook Calendar",
  "assets": { "css": ["overlay.css"], "js": ["overlay.js"] },
  "toggle": true,
  "refresh": { "everyMinutes": 15, "builtin": "calendar" },
  "hotkeyDefault": "Ctrl+Alt+C",
  "settings": { "clientId": "", "tenant": "organizations", "scopes": ["Calendars.Read"] }
}
```

- **`refresh.builtin`** names a data provider compiled into the host (currently `"calendar"`).
- **`refresh.command`** (alternative) runs any external script/EXE on the schedule instead — so a module's data fetch can be written in **any language**; it just has to write a `data.js` next to the manifest.
- Omit `refresh` for a purely client-side module (clock, CSS effect, etc.).

Default state for a freshly dropped-in module is **disabled** — nothing happens until you `module enable <id>`.

### The calendar module (`modules/calendar/`)

The first module: a top-right **"Today"** panel with your Outlook/M365 calendar — all-day chips, timed events with start/end, online/in-person dots, `NOW` / `NEXT` / `TENTATIVE` pills, past events dimmed.

```powershell
HtmlWallpaper.exe module enable calendar                 # Auto (recommended)
HtmlWallpaper.exe module enable calendar --auth workiq   # WorkIQ only
HtmlWallpaper.exe module enable calendar --auth msal      # Windows broker / MSAL only
```

**Two sign-in methods** are supported, and the installer lets you pick one:

| Method | How it signs in | Best for |
| --- | --- | --- |
| **WorkIQ** | Runs the [WorkIQ](https://www.npmjs.com/package/@microsoft/workiq) CLI (`npx @microsoft/workiq call-function`), reusing your existing Windows/WAM M365 sign-in through an app registration that's **already admin-approved** in locked-down tenants. Needs Node.js. | **microsoft.com** and other tenants where generic Graph clients require admin consent |
| **MSAL** | MSAL.NET: the **Windows broker (WAM)** first, then **device-code**, with a DPAPI token cache (`modules/calendar/calendar-token.bin`) for silent refresh. No Node.js. | Tenants where the Graph client is consentable, or machines without Node.js |

**Auto** (the default) tries **WorkIQ first, then MSAL** — so it works out of the box on locked-down tenants but still functions without Node.js. Your choice is saved to `modules/calendar/config.json` (git-ignored) so the 15-minute background refresh reuses it. Events come from Microsoft Graph (`me/calendarView`) and are written to `modules/calendar/data.js` (`window.CALENDAR_DATA`, git-ignored — it's your personal calendar). Each event's Graph `webLink` is captured too, so in **clickable mode** (**Ctrl+Alt+K**) clicking a meeting opens it in the **new Outlook desktop app** (`ms-outlook:` — never classic Outlook) when it's installed, falling back to Outlook on the web otherwise. (The new Outlook has no supported deep link to a specific event, so it opens the app; the web fallback lands on the exact meeting.) Toggle the panel with the tray item, and hide/show all widgets with **Ctrl+Alt+H**; customize both hotkeys in the tray's **Settings…** dialog.

For the MSAL method you can point at an admin-consented app or restrict the tenant via the manifest `settings` (`clientId`, `tenant`, `scopes`).

> **Note — locked-down tenants:** if your tenant disables user consent (e.g. **microsoft.com**), the generic *Microsoft Graph Command Line Tools* client used by MSAL shows **"Need admin approval"** and can't sign in until an admin consents to it. Use the **WorkIQ** method (or **Auto**), which authenticates through WorkIQ's already-approved app. Token-protection Conditional Access additionally blocks the MSAL **device-code** fallback (WAM still works there).

### The Azure Updates module (`modules/azure-updates/`)

A glass panel that shows **live [Azure product updates](https://azure.microsoft.com/updates/)**, filtered to the domains you care about. It ships defaulting to **Compute / Storage / Networking**, but it's fully domain-agnostic — anyone can pick their own.

```
HtmlWallpaper.exe module enable azure-updates    # fetch + turn on
HtmlWallpaper.exe module refresh azure-updates   # pull the latest now
```

- **Data source:** the public **[Azure updates RSS feed](https://www.microsoft.com/releasecommunications/api/v2/azure/rss)** (`…/releasecommunications/api/v2/azure/rss`) — no sign-in, newest-first. Each item shows a status pill (**GA** / **Preview** / **Dev** / **Retiring**), the domain chips it matched, and a relative date. Items you haven't seen before get a **NEW** glow (tracked in the WebView2 `localStorage`).
- **Pick your domains** — edit `domains` in `modules/azure-updates/config.json` (git-ignored, seeded on first run). Filtering is on the feed's own `productCategories`, so run `powershell -File modules/azure-updates/refresh.ps1 -ListDomains` to print every available domain (Databases, Security, AI + machine learning, …). An empty `domains` list means **all** domains. Also filter by `status` (`Launched` = GA, `In preview`, `In development`, `Retirement`).
- **Position** — because the wallpaper is a click-through layer behind the icons, the panel can't be mouse-dragged. Instead set `position` in `config.json` (`left-of-calendar` — the default — plus `top-right` / `top-left` / `center-left` / `bottom-left` / `bottom-right`) and optional `offsetX` / `offsetY` pixel nudges; run `module refresh azure-updates` and it moves within seconds. `left-of-calendar` tracks the calendar's own width so the two panels stay flush.
- **Open an update** — the *interactive overlay* (see below) makes each row a clickable link: press the **clickable-mode hotkey (Ctrl+Alt+K)** and click a card to open it in your browser. The refresher also writes a standalone `updates.html` plus an **"Azure Updates"** Start-menu shortcut as an always-available fallback. Toggle the panel itself with the tray item or the global hotkey (default **Ctrl+Alt+U**).
- **Reuse the pattern:** the tray submenu is generic — any module whose `module.json` declares `"tray": { "links": "links.json" }` and writes that file (an array of `{ "title", "url", "status" }`) gets a clickable submenu for free.

### The interactive overlay (clickable panels)

The wallpaper itself is a click-through layer parented **behind** the desktop icons, so it can never receive a mouse click. To make panels clickable, the app also renders a second, transparent, **top-most window in front of the desktop** (`overlay-interactive.html`) hosting the *same* modules. It's clipped with `SetWindowRgn` so only the interactive panel rectangles are "solid" — every other pixel passes straight through to the desktop and your icons — and clicking a link posts the URL to the host, which opens it in your browser (or, for an Outlook calendar link, in the **new Outlook desktop app** when it's installed — see the calendar module).

Two global hotkeys drive it (they never steal focus from the app you're using):

| Hotkey | Action |
| --- | --- |
| **Ctrl+Alt+K** | Toggle **clickable mode** — show the overlay so panels can be clicked. While on, the ambient copy of each interactive panel is hidden so the overlay is the *sole* renderer (no ghosted double image). |
| **Ctrl+Alt+H** | **Hide / show all widgets** — hide every module panel (the base animation and clock stay), e.g. for a clean screen-share. |

This is a **shared engine capability** — any module opts in purely by tagging its DOM, no host code required:

- put `data-wp-panel` on the panel's root element (so it participates in hide-all and, if it has links, gets clipped into the clickable overlay), and
- put `data-wp-href="https://…"` on any element that should open a link when clicked.

Panels with **no** links stay on the ambient wallpaper untouched in clickable mode. The same tagged markup is inert on the ambient wallpaper (which can't be clicked) and live on the overlay, so a module writes it once. The overlay lives on the **primary monitor** and rebuilds automatically with the wallpaper on display changes.

## How it works

Windows 11's modern "raised desktop with layered ShellView" renders the wallpaper differently from older Windows. `Progman` carries `WS_EX_NOREDIRECTIONBITMAP`, `SHELLDLL_DefView` (the icons) is a *layered child* of Progman, and the wallpaper is drawn by a `WorkerW` child of Progman that is z-ordered **under** the icons.

1. `FindWindow("Progman")` gets the desktop root; the engine detects the raised desktop via `WS_EX_NOREDIRECTIONBITMAP`.
2. It sends the `0x052C` message with **`wParam=0xD, lParam=0x1`** — the parameters that actually spawn the WorkerW child under the icons on Windows 11 (the classic `0,0` does nothing here).
3. The wallpaper form is created with **`WS_EX_LAYERED`** and made fully opaque via `SetLayeredWindowAttributes(alpha=255)`. This is required so DWM composites the WebView2 (DirectComposition) surface correctly *behind the icons* — without it the window renders solid black.
4. One form per target monitor is `SetParent`-ed to that WorkerW child. Because the WorkerW's client origin is the *virtual-screen* top-left (which can be negative when a monitor sits left of / above primary), each form's monitor rectangle is translated into that coordinate space (via `GetWindowRect` on the WorkerW) so it lands on the correct physical display.
5. On classic (non-raised) layouts it falls back to the sibling-WorkerW technique, then to Progman.
6. A lightweight watchdog re-attaches the window only if the desktop layer is destroyed (e.g., Explorer restart). It intentionally does **not** poll `GetParent` — on the raised desktop that returns `0` even while correctly attached, so re-parenting on every tick caused a periodic GPU-compositor flicker on the WebView2 surface (invisible to screenshots, visible to the eye).

Result: full HTML/CSS-animation/JS/Canvas as the live wallpaper, **with desktop icons fully visible on top**.

## Notes

- **Show desktop (Win+D) / Aero Peek** deliberately hides all windows to reveal the bare OS wallpaper, so it momentarily shows the static wallpaper underneath; the animation returns as soon as you leave "show desktop".
- The wallpaper does not receive mouse/keyboard input (desktop clicks pass through to icons).
- Heavy pages use GPU/CPU like any browser tab; keep animations reasonable for battery life.
- WebView2 user data is stored in `%LOCALAPPDATA%\HtmlWallpaper\WebView2`.

## Credits

The raised-desktop WorkerW handling follows the technique documented by Microsoft and implemented in [Lively Wallpaper](https://github.com/rocksdanister/lively) (`WinDesktopCore.cs`).
