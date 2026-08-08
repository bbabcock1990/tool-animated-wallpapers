/* Azure Updates overlay — a wallpaper module.
 *
 * Injected by module-loader.js when the "azure-updates" module is enabled. It
 * builds a glass panel (default: left of the calendar), loads its generated data
 * file (data.js, produced by refresh.ps1), and keeps it current. Visibility
 * follows the module's enabled state via window.Wallpaper.onStateChange — no page
 * reload, so the base animation never restarts.
 *
 * The wallpaper is a click-through layer behind the desktop icons, so this panel
 * is display-only. To open an update, use the system-tray "Azure Updates" submenu
 * or the generated updates.html (see the module README).
 *
 * Data (window.AZUPDATES_DATA) is written by:
 *   HtmlWallpaper.exe module refresh azure-updates   (and the in-process scheduler) */
(function () {
  "use strict";

  var MODULE_ID = "azure-updates";
  var base = (window.Wallpaper && window.Wallpaper.base(MODULE_ID)) || "modules/azure-updates/";
  var POSITIONS = ["left-of-calendar", "top-right", "top-left", "center-left", "bottom-left", "bottom-right"];
  var SEEN_KEY = "azupdates.seen.v1";

  function settings() {
    return (window.Wallpaper && window.Wallpaper.settings(MODULE_ID)) || {};
  }

  function injectPanel() {
    if (document.getElementById("azu")) return;
    var el = document.createElement("div");
    el.className = "azu";
    el.id = "azu";
    el.setAttribute("data-wp-panel", "azure-updates");
    el.innerHTML =
      '<div class="azu-head">' +
        '<div class="azu-title">Azure Updates</div>' +
        '<div class="azu-scope" id="azuScope"></div>' +
      '</div>' +
      '<div class="azu-list" id="azuList"></div>' +
      '<div class="azu-foot" id="azuFoot"></div>';
    document.body.appendChild(el);
  }

  /* ---------- Position (data-driven; drag is impossible on the wallpaper) ---------- */
  function applyPosition(meta) {
    var el = document.getElementById("azu");
    if (!el) return;
    var pos = (meta && meta.position) || settings().position || "left-of-calendar";
    if (POSITIONS.indexOf(pos) === -1) pos = "left-of-calendar";
    POSITIONS.forEach(function (p) { el.classList.remove("pos-" + p); });
    el.classList.add("pos-" + pos);

    var ox = num(meta && meta.offsetX, settings().offsetX);
    var oy = num(meta && meta.offsetY, settings().offsetY);
    var ty = pos === "center-left" ? "-50%" : "0";
    el.style.transform = "translate(" + ox + "px, calc(" + ty + " + " + oy + "px))";
  }
  function num(a, b) {
    var v = (a === undefined || a === null) ? b : a;
    v = parseFloat(v);
    return isNaN(v) ? 0 : v;
  }

  /* ---------- Rendering ---------- */
  function relTime(iso) {
    var then = new Date(iso).getTime();
    if (isNaN(then)) return "";
    var mins = Math.round((Date.now() - then) / 60000);
    if (mins < 1) return "just now";
    if (mins < 60) return mins + "m ago";
    var hrs = Math.round(mins / 60);
    if (hrs < 24) return hrs + "h ago";
    var days = Math.round(hrs / 24);
    if (days < 30) return days + "d ago";
    var d = new Date(iso);
    return d.toLocaleDateString([], { month: "short", day: "numeric" });
  }

  function loadSeen() {
    try { return JSON.parse(localStorage.getItem(SEEN_KEY) || "[]"); } catch (e) { return []; }
  }
  function saveSeen(ids) {
    try { localStorage.setItem(SEEN_KEY, JSON.stringify(ids.slice(0, 200))); } catch (e) {}
  }

  function render() {
    var data = window.AZUPDATES_DATA;
    var listEl = document.getElementById("azuList");
    var scopeEl = document.getElementById("azuScope");
    var footEl = document.getElementById("azuFoot");
    if (!listEl) return;

    applyPosition(data && data.meta);

    if (!data || !Array.isArray(data.items)) {
      listEl.innerHTML = '<div class="azu-empty">Azure updates unavailable</div>';
      return;
    }

    if (scopeEl) {
      var doms = (data.meta && data.meta.domains) || [];
      scopeEl.textContent = doms.length ? doms.join(" \u00b7 ") : "All domains";
    }

    var seen = loadSeen();
    listEl.innerHTML = "";

    if (data.items.length === 0) {
      listEl.innerHTML = '<div class="azu-empty">No updates match your filters</div>';
    }

    data.items.forEach(function (it) {
      var isNew = it.id && seen.indexOf(it.id) === -1;
      var isRetire = it.statusClass === "retire";

      var row = document.createElement("div");
      row.className = "azu-item" + (isNew ? " is-new" : "") + (isRetire ? " is-retire" : "");
      // Interactive overlay: make the whole row a clickable link. Inert on the
      // ambient wallpaper (which can't receive clicks); the front overlay routes it.
      if (it.url) row.setAttribute("data-wp-href", it.url);

      var top = document.createElement("div");
      top.className = "azu-top";
      top.appendChild(pill(it.statusClass, it.statusLabel));
      if (isNew) top.appendChild(pill("new", "New"));
      (it.domains || []).slice(0, 2).forEach(function (d) {
        var c = document.createElement("span");
        c.className = "azu-domain";
        c.textContent = d;
        top.appendChild(c);
      });

      var subj = document.createElement("div");
      subj.className = "azu-subject";
      subj.textContent = it.title || "";

      var meta = document.createElement("div");
      meta.className = "azu-meta";
      meta.textContent = relTime(it.date);

      row.appendChild(top);
      row.appendChild(subj);
      row.appendChild(meta);
      listEl.appendChild(row);
    });

    // Remember what we've shown so the next batch highlights only truly-new items.
    var ids = data.items.map(function (it) { return it.id; }).filter(Boolean);
    saveSeen(mergeUnique(ids, seen));

    if (footEl && data.generatedAt) {
      var gen = new Date(data.generatedAt);
      var ageH = (Date.now() - gen.getTime()) / 3.6e6;
      footEl.textContent = "Updated " + gen.toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
      footEl.className = ageH > 26 ? "azu-foot warn" : "azu-foot";
    }
  }

  function pill(cls, text) {
    var p = document.createElement("span");
    p.className = "azu-pill " + (cls || "dev");
    p.textContent = text || "";
    return p;
  }
  function mergeUnique(a, b) {
    var out = a.slice();
    for (var i = 0; i < b.length; i++) if (out.indexOf(b[i]) === -1) out.push(b[i]);
    return out;
  }

  /* ---------- Data loading (cache-busted, no page reload) ---------- */
  function loadData() {
    var s = document.createElement("script");
    s.src = base + "data.js?t=" + Date.now();
    s.onload = function () { render(); s.remove(); };
    s.onerror = function () { s.remove(); };
    document.body.appendChild(s);
  }

  /* ---------- On/off state (driven by the module loader) ---------- */
  function applyState(on) {
    var el = document.getElementById("azu");
    if (el) el.style.display = on ? "" : "none";
  }

  /* ---------- Boot ---------- */
  injectPanel();
  applyPosition(null);
  loadData();
  if (window.Wallpaper && window.Wallpaper.onStateChange) {
    window.Wallpaper.onStateChange(MODULE_ID, applyState);
  }
  setInterval(render, 60 * 1000);          // refresh relative times + position tweaks
  setInterval(loadData, 5 * 60 * 1000);    // pull fresh data written by the host

  // Re-read right after wake from sleep / long idle so a resumed machine isn't stale.
  var lastTick = Date.now();
  setInterval(function () {
    var now = Date.now();
    if (now - lastTick > 90 * 1000) loadData();
    lastTick = now;
  }, 30 * 1000);
})();
