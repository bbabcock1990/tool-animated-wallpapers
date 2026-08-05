# Animated Desktop Wallpapers Helper

Render **any HTML page — CSS animations, JavaScript, and `<canvas>` — as your live Windows 11 desktop background**, behind your desktop icons. No third-party wallpaper app required.

It hosts **WebView2** (the Edge engine already on Windows 11) inside a full-screen window parented to the desktop using the Windows shell **WorkerW / Progman** technique.

![Animated aurora wallpaper](static-wallpaper.png)

## Install (one command)

Open **Windows PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/install.ps1 | iex
```

That's it. No cloning, no build tools, no admin. The installer:

- downloads a **self-contained build** (no .NET install required) to `%LOCALAPPDATA%\AnimatedDesktopWallpaper`,
- installs the **WebView2 runtime** if it's missing (preinstalled on Windows 11),
- sets the animated wallpaper and **starts it at login**,
- optionally enables the **Outlook calendar module** — it asks first, and if you say yes it signs you in to Microsoft 365 (Windows account broker, with a device-code fallback) and refreshes it automatically in the background.

Prefer double-clicking? Download the repo (green **Code ▸ Download ZIP**), unzip, and run **`install.bat`**.

### Uninstall

```powershell
irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/uninstall.ps1 | iex
```

Stops the wallpaper, removes the login entry, cleans up any legacy calendar task/tray from older versions, and deletes the install folder.

### Manage it

After install, everything lives in `%LOCALAPPDATA%\AnimatedDesktopWallpaper`:

```powershell
$dir = "$env:LOCALAPPDATA\AnimatedDesktopWallpaper"

# Change the wallpaper (any local HTML file or a URL)
& "$dir\Set-Wallpaper.ps1" -Source "$dir\demo.html"
& "$dir\Set-Wallpaper.ps1" -Source https://example.com

# Stop / restore the normal desktop
& "$dir\Stop-Wallpaper.ps1"

# Start-at-login on / off
& "$dir\Enable-Startup.ps1" -Source "$dir\wallpaper.html"
& "$dir\Disable-Startup.ps1"
```

- Renders on **every monitor by default** (one window per display, each correctly positioned); pass `--primary` (or `-Primary`) for the primary monitor only, or `--monitor N` (0-based) to target a single display.
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

# Or use the helper scripts
.\Set-Wallpaper.ps1 -Source .\wallpaper.html
.\Stop-Wallpaper.ps1
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
HtmlWallpaper.exe module enable calendar
```

Sign-in and data fetch are built into the host (MSAL.NET). It signs in with the **Windows account broker (WAM)** first, falling back to **device-code** sign-in, then caches the token (DPAPI, per-user, `modules/calendar/calendar-token.bin`) so the 15-minute background refresh stays silent. Data is fetched from Microsoft Graph (`me/calendarView`) and written to `modules/calendar/data.js` (`window.CALENDAR_DATA`, git-ignored — it's your personal calendar). Toggle it any time with the tray item or the global hotkey (default **Ctrl+Alt+C**); change the hotkey in the tray's **Settings…** dialog.

You can point the calendar at an admin-consented app or restrict the tenant via the manifest `settings` (`clientId`, `tenant`, `scopes`).

> **Note:** some tenants enforce a Conditional Access **token protection** policy that *requires* the WAM broker — there, only the Windows-broker sign-in works and the device-code fallback is blocked by design. Enable the module interactively once (so the WAM dialog can appear) before relying on the silent background refresh.

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
