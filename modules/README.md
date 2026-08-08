# Wallpaper modules

Modules are optional widgets that run on top of any HtmlWallpaper page. They are designed to be self-contained: each module has a folder, a manifest, web assets, and optionally a refresher that writes generated data for the browser to consume.

## Runtime flow

1. **Discovery** — the C# host scans `modules/*/module.json`.
2. **State** — enabled flags and tray hotkeys are stored in `modules/state.json` (generated and git-ignored).
3. **Registry** — the host writes `modules/registry.js` with `window.WALLPAPER_REGISTRY` for WebView2.
4. **Injection** — `module-loader.js` polls the registry and injects CSS/JS for enabled modules without reloading the base animation.
5. **Refresh** — enabled modules with a `refresh` block are refreshed by the in-process scheduler on their own cadence.
6. **Interaction** — modules can opt into hide/show and clickable-overlay support with DOM attributes.

## Module folder shape

```text
modules/
  README.md
  registry.js          # generated: browser-readable module registry
  state.json           # generated: enabled flags + tray settings
  <id>/
    module.json        # manifest read by the host
    overlay.css        # module styles injected when enabled
    overlay.js         # module script injected when enabled
    data.js            # generated module data, if needed
    config.json        # generated per-user settings, if needed
```

Generated files are intentionally git-ignored. Some modules write personal data, such as calendar events, or local preferences, such as filters and panel position.

## Manifest reference

A module manifest describes how the host should expose the module:

```json
{
  "id": "calendar",
  "name": "Outlook Calendar",
  "description": "Shows today's Outlook/M365 meetings in a top-right panel.",
  "assets": { "css": ["overlay.css"], "js": ["overlay.js"] },
  "toggle": true,
  "hotkeyDefault": "Ctrl+Alt+C",
  "refresh": { "everyMinutes": 15, "builtin": "calendar" },
  "settings": { "authMethod": "auto" }
}
```

Key fields:

| Field | Purpose |
| --- | --- |
| `id` | Stable folder/module identifier. |
| `name` | Friendly name used by the tray and CLI. |
| `description` | Human-readable summary. |
| `assets.css` / `assets.js` | Files injected into the wallpaper when the module is enabled. |
| `toggle` | Whether the tray exposes an on/off item. |
| `hotkeyDefault` | Reserved/default hotkey hint in the manifest; tray toggles and engine hotkeys are managed by the host. |
| `refresh.builtin` | Built-in C# refresher name, currently `calendar`. |
| `refresh.command` | External refresh command, run from the module folder through PowerShell. |
| `settings` | Default settings exposed to browser code through `window.Wallpaper.settings(id)`. |
| `tray.links` | Optional generated JSON file for a tray submenu of clickable links. |

## Browser module SDK

`module-loader.js` exposes a small SDK to module scripts:

| API | Use |
| --- | --- |
| `window.Wallpaper.base(id)` | Returns the module folder URL, such as `modules/calendar/`. |
| `window.Wallpaper.settings(id)` | Returns manifest settings for the module. |
| `window.Wallpaper.isEnabled(id)` | Returns current enabled state. |
| `window.Wallpaper.onStateChange(id, callback)` | Subscribes to enable/disable changes and fires immediately with current state. |

Module scripts usually create their panel once, load their generated `data.js`, then show/hide the panel when `onStateChange` changes.

## Interactive overlay support

The ambient wallpaper is behind desktop icons and cannot receive clicks. For clickable widgets, the host also renders `overlay-interactive.html` in a transparent top-most overlay. The overlay clips itself to module panel rectangles and opens safe HTTP/HTTPS links in the default browser.

Modules opt in with attributes:

- `data-wp-panel` on the panel root so it participates in hide/show and overlay clipping.
- `data-wp-href="https://..."` on elements that should open a link in clickable mode.

Engine hotkeys:

| Hotkey | Action |
| --- | --- |
| **Ctrl+Alt+K** | Toggle clickable mode for interactive panels. |
| **Ctrl+Alt+H** | Hide/show all module panels. |

Both hotkeys can be changed from the tray **Settings...** dialog.

## Available modules

### Outlook Calendar (`modules/calendar/`)

Shows today's Outlook/Microsoft 365 calendar in a top-right panel.

What it displays:

- all-day events as chips,
- timed events sorted through the day,
- start/end times,
- online vs. in-person indicators,
- `Now`, `Next`, and `Tentative` pills,
- dimmed past events and cancelled-event styling,
- generated-data freshness text.

How data refresh works:

- The manifest uses `refresh.builtin: "calendar"` every 15 minutes.
- The C# `CalendarRefresher` calls Microsoft Graph `me/calendarView` for the local day.
- Data is written to `modules/calendar/data.js` as `window.CALENDAR_DATA`.
- Token/cache files and generated data are git-ignored.

Supported auth methods:

| Method | Behavior | Best for |
| --- | --- | --- |
| `auto` | Tries WorkIQ first, then MSAL/WAM. | Default choice. |
| `workiq` | Runs `npx -y @microsoft/workiq@latest call-function` and reuses an existing Windows/M365 sign-in. | Locked-down tenants where generic Graph clients need admin approval. |
| `msal` | Uses MSAL.NET with Windows broker (WAM), then device-code fallback, plus a DPAPI token cache. | Tenants where the public Graph client can be used or admin-consented. |

Commands:

```powershell
HtmlWallpaper.exe module enable calendar                 # auto auth
HtmlWallpaper.exe module enable calendar --auth workiq   # WorkIQ only
HtmlWallpaper.exe module enable calendar --auth msal     # MSAL/WAM only
HtmlWallpaper.exe module refresh calendar
HtmlWallpaper.exe module disable calendar
```

### Azure Updates (`modules/azure-updates/`)

Shows live Azure product updates in a glass panel, filtered to the Azure domains and statuses you care about. See the module's own README for extra configuration details: [`modules/azure-updates/README.md`](azure-updates/README.md).

What it displays:

- newest matching Azure updates,
- status pills such as `GA`, `Preview`, `Dev`, and `Retiring`,
- matched Azure domain chips,
- relative publish date,
- `New` glow for items not previously seen in the WebView2 profile.

How data refresh works:

- The manifest uses `refresh.command: "refresh.ps1"` every 60 minutes.
- `refresh.ps1` fetches the public Azure Updates RSS feed anonymously.
- The first refresh seeds `modules/azure-updates/config.json` with defaults.
- Data is written to `data.js` as `window.AZUPDATES_DATA`.
- Additional generated outputs include `links.json` and `updates.html`.

Default settings:

| Setting | Default | Purpose |
| --- | --- | --- |
| `domains` | `Compute`, `Storage`, `Networking` | Azure categories to keep; empty means all domains. |
| `status` | `Launched`, `In preview`, `Retirement` | Update statuses to show. |
| `position` | `left-of-calendar` | Panel placement. |
| `offsetX` / `offsetY` | `0` / `0` | Pixel nudges for placement. |
| `maxItems` | `8` | Number of panel cards. |

Commands:

```powershell
HtmlWallpaper.exe module enable azure-updates
HtmlWallpaper.exe module refresh azure-updates
HtmlWallpaper.exe module disable azure-updates

# Discover available Azure domains from the feed
powershell -NoProfile -ExecutionPolicy Bypass -File .\modules\azure-updates\refresh.ps1 -ListDomains
```

Use **Ctrl+Alt+K** to enter clickable mode, then click an Azure update card to open it in your browser. The generated `updates.html` is a standalone clickable fallback.

## Adding another module

Create a new folder under `modules/`, add a `module.json`, then add the CSS/JS assets listed in the manifest. If the module needs server or local data, add either a built-in refresher in C# or an external command that writes a generated JS file next to the manifest. Install external modules with:

```powershell
HtmlWallpaper.exe module add <folder-path>
```

Keep generated data out of source control and prefer writing browser-readable `data.js` files instead of using `fetch()` from local `file://` pages, because WebView2 blocks many local XHR/fetch patterns.
