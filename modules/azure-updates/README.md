# Azure Updates module

A wallpaper module that shows **live [Azure product updates](https://azure.microsoft.com/updates/)** in a glass panel, filtered to the domains and statuses you care about. It ships defaulting to **Compute / Storage / Networking**, but it is domain-agnostic.

For the full module-system overview and summaries of every bundled module, see [`../README.md`](../README.md).

## Enable

```powershell
HtmlWallpaper.exe module enable azure-updates    # fetch + turn on
HtmlWallpaper.exe module refresh azure-updates   # pull the latest now
HtmlWallpaper.exe module disable azure-updates   # turn off
```


## Configure (`config.json`)

`config.json` is seeded on first refresh and is git-ignored. Edit it, then run `HtmlWallpaper.exe module refresh azure-updates`.

| Key | Meaning |
| --- | --- |
| `domains` | Domains to keep, matched on the feed's `productCategories`. Empty = all domains. |
| `position` | `left-of-calendar` (default), `top-right`, `top-left`, `center-left`, `bottom-left`, or `bottom-right`. |
| `offsetX` | Horizontal pixel nudge. |
| `offsetY` | Vertical pixel nudge. |
| `status` | Any of `Launched` (GA), `In preview`, `In development`, `Retirement`. |
| `maxItems` | How many items to show in the wallpaper panel. |

Discover every domain available in the feed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\refresh.ps1 -ListDomains
```

## Opening updates

The ambient wallpaper is click-through because it sits behind the desktop icons. To open an update:

- press **Ctrl+Alt+K** to enter clickable mode, then click an update card in the interactive overlay, or
- open the generated `updates.html` page in a browser.

Move the panel by editing `position`, `offsetX`, and `offsetY` in `config.json`; panels cannot be dragged on the wallpaper surface.

## Generated files (all git-ignored)

| File | Consumer |
| --- | --- |
| `data.js` | `window.AZUPDATES_DATA` for the wallpaper panel (`overlay.js`). |
| `links.json` | A generated `{ title, url, status }` link list that can be used by tray/link integrations. |
| `updates.html` | A standalone, fully clickable page you can open in any browser. |
| `config.json` | Your saved preferences. |

## Data source

The public Azure Updates RSS feed:
`https://www.microsoft.com/releasecommunications/api/v2/azure/rss` (anonymous, no sign-in).
