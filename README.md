# Animated Desktop Wallpapers Helper

Render **any HTML page — CSS animations, JavaScript, and `<canvas>` — as your live Windows 11 desktop background**, behind your desktop icons. No third-party wallpaper app required.

It hosts **WebView2** (the Edge engine already on Windows 11) inside a full-screen window parented to the desktop using the Windows shell **WorkerW / Progman** technique.

![Animated aurora wallpaper](static-wallpaper.png)

## Requirements

- Windows 10/11
- WebView2 Runtime (preinstalled on Windows 11)
- .NET 8 SDK (only needed to build)

## Quick start

```powershell
# 1. Build
dotnet build -c Release

# 2. Set an animated wallpaper (bundled example)
.\Set-Wallpaper.ps1 -Source .\wallpaper.html

# 3. Stop / restore the normal wallpaper
.\Stop-Wallpaper.ps1
```

You can point it at any local HTML file or a URL:

```powershell
.\bin\Release\net8.0-windows\HtmlWallpaper.exe .\wallpaper.html
.\bin\Release\net8.0-windows\HtmlWallpaper.exe https://example.com
```

- Spans **all monitors** by default; pass `--primary` (or `-Primary`) for the primary monitor only.
- Only one instance runs at a time — launching a new one replaces the old.

Run automatically at login:

```powershell
.\Enable-Startup.ps1 -Source .\wallpaper.html
.\Disable-Startup.ps1     # remove
```

## Included examples

- **`wallpaper.html`** — aurora ribbons, a particle constellation network, and a live clock/date/greeting.
- **`demo.html`** — CSS-animated gradient + JS `<canvas>` starfield + clock.
- **`static-wallpaper.png`** — a matching still image for the OS wallpaper (shown during Win+D / login).

## How it works

Windows 11's modern "raised desktop with layered ShellView" renders the wallpaper differently from older Windows. `Progman` carries `WS_EX_NOREDIRECTIONBITMAP`, `SHELLDLL_DefView` (the icons) is a *layered child* of Progman, and the wallpaper is drawn by a `WorkerW` child of Progman that is z-ordered **under** the icons.

1. `FindWindow("Progman")` gets the desktop root; the engine detects the raised desktop via `WS_EX_NOREDIRECTIONBITMAP`.
2. It sends the `0x052C` message with **`wParam=0xD, lParam=0x1`** — the parameters that actually spawn the WorkerW child under the icons on Windows 11 (the classic `0,0` does nothing here).
3. The wallpaper form is created with **`WS_EX_LAYERED`** and made fully opaque via `SetLayeredWindowAttributes(alpha=255)`. This is required so DWM composites the WebView2 (DirectComposition) surface correctly *behind the icons* — without it the window renders solid black.
4. The form is `SetParent`-ed to that WorkerW child and sized to the virtual screen.
5. On classic (non-raised) layouts it falls back to the sibling-WorkerW technique, then to Progman.
6. A lightweight watchdog re-attaches the window only if the shell actually detaches it (e.g., Explorer restart). It never re-sends the spawn message or re-parents while already attached — doing so forces a full wallpaper repaint that shows up as a periodic flash.

Result: full HTML/CSS-animation/JS/Canvas as the live wallpaper, **with desktop icons fully visible on top**.

## Notes

- **Show desktop (Win+D) / Aero Peek** deliberately hides all windows to reveal the bare OS wallpaper, so it momentarily shows the static wallpaper underneath; the animation returns as soon as you leave "show desktop".
- The wallpaper does not receive mouse/keyboard input (desktop clicks pass through to icons).
- Heavy pages use GPU/CPU like any browser tab; keep animations reasonable for battery life.
- WebView2 user data is stored in `%LOCALAPPDATA%\HtmlWallpaper\WebView2`.

## Credits

The raised-desktop WorkerW handling follows the technique documented by Microsoft and implemented in [Lively Wallpaper](https://github.com/rocksdanister/lively) (`WinDesktopCore.cs`).
