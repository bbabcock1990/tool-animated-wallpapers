# Azure Updates module

A wallpaper module that shows **live [Azure product updates](https://azure.microsoft.com/updates/)**
in a glass panel — filtered to the domains you care about. Ships defaulting to
**Compute / Storage / Networking**, but it's fully domain-agnostic.

## Enable

```powershell
HtmlWallpaper.exe module enable azure-updates    # fetch + turn on
HtmlWallpaper.exe module refresh azure-updates   # pull the latest now
HtmlWallpaper.exe module disable azure-updates   # turn off
```

Default global hotkey to toggle the panel: **Ctrl+Alt+U**.

## Configure (`config.json`)

Seeded on first refresh (git-ignored). Edit it, then run
`HtmlWallpaper.exe module refresh azure-updates`.

| Key        | Meaning                                                                                 |
| ---------- | --------------------------------------------------------------------------------------- |
| `domains`  | Domains to keep, matched on the feed's `productCategories`. **Empty = all domains.**    |
| `position` | `left-of-calendar` (default), `top-right`, `top-left`, `center-left`, `bottom-left`, `bottom-right`. |
| `offsetX`  | Horizontal pixel nudge.                                                                  |
| `offsetY`  | Vertical pixel nudge.                                                                    |
| `status`   | Any of `Launched` (GA), `In preview`, `In development`, `Retirement`.                    |
| `maxItems` | How many items to show in the wallpaper panel.                                           |

Discover every domain available in the feed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\refresh.ps1 -ListDomains
```

## Why can't I click or drag the panel?

The wallpaper is a **click-through layer behind the desktop icons** (see the repo's
"How it works"), so it receives no mouse input. Therefore:

- **Move it** by setting `position` / `offsetX` / `offsetY` in `config.json`, not by dragging.
- **Open an update** from the system-tray **Azure Updates ▸** submenu, or from the
  generated `updates.html` (an **"Azure Updates"** Start-menu shortcut points to it).

## Generated files (all git-ignored)

| File           | Consumer                                                              |
| -------------- | -------------------------------------------------------------------- |
| `data.js`      | `window.AZUPDATES_DATA` — the wallpaper panel (`overlay.js`).         |
| `links.json`   | The tray "Azure Updates" submenu (`{ title, url, status }` array).   |
| `updates.html` | A standalone, fully clickable page you can open in any browser.      |
| `config.json`  | Your saved preferences.                                              |

## Data source

The public Azure Updates feed:
`https://www.microsoft.com/releasecommunications/api/v2/azure` (anonymous, no sign-in).
