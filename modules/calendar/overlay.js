/* Outlook "Today" calendar overlay — a wallpaper module.
 *
 * Injected by module-loader.js when the "calendar" module is enabled. It builds
 * a top-right "Today" panel, loads its generated data file (data.js, produced by
 * the host's built-in calendar refresher), and keeps it current during the day.
 * Visibility follows the module's enabled state via window.Wallpaper.onStateChange
 * — no page reload, so the base animation never restarts.
 *
 * Data (window.CALENDAR_DATA) is written by:
 *   HtmlWallpaper.exe module refresh calendar   (and the in-process scheduler) */
(function () {
  "use strict";

  var MODULE_ID = "calendar";
  var base = (window.Wallpaper && window.Wallpaper.base(MODULE_ID)) || "modules/calendar/";

  function injectPanel() {
    if (document.getElementById("cal")) return;
    var cal = document.createElement("div");
    cal.className = "cal";
    cal.id = "cal";
    cal.setAttribute("data-wp-panel", "calendar");
    cal.innerHTML =
      '<div class="cal-head">' +
        '<div class="cal-title">Today</div>' +
        '<div class="cal-date" id="calDate"></div>' +
      '</div>' +
      '<div class="allday" id="allday"></div>' +
      '<div class="events" id="events"></div>' +
      '<div class="stale" id="stale"></div>';
    document.body.appendChild(cal);
  }

  /* ---------- Rendering ---------- */
  function pad(n) { return String(n).padStart(2, '0'); }
  function fmtTime(dt) {
    var d = new Date(dt);
    var h = d.getHours(); var m = d.getMinutes();
    var ap = h < 12 ? 'AM' : 'PM';
    h = h % 12; if (h === 0) h = 12;
    return m === 0 ? (h + ' ' + ap) : (h + ':' + pad(m) + ' ' + ap);
  }
  function addPill(container, cls, text) {
    var p = document.createElement('span');
    p.className = 'pill ' + cls;
    p.textContent = text;
    container.appendChild(p);
  }

  function renderCalendar() {
    var data = window.CALENDAR_DATA;
    var calDate = document.getElementById('calDate');
    var alldayEl = document.getElementById('allday');
    var eventsEl = document.getElementById('events');
    var staleEl = document.getElementById('stale');
    if (!alldayEl || !eventsEl) return;
    alldayEl.innerHTML = ''; eventsEl.innerHTML = '';

    if (!data || !Array.isArray(data.events)) {
      eventsEl.innerHTML = '<div class="empty">Calendar data unavailable</div>';
      return;
    }

    var now = new Date();
    calDate.textContent = now.toLocaleDateString([], { month: 'short', day: 'numeric' });

    var allday = data.events.filter(function (e) { return e.isAllDay; });
    var timed = data.events.filter(function (e) { return !e.isAllDay; })
                           .sort(function (a, b) { return new Date(a.start) - new Date(b.start); });

    for (var i = 0; i < allday.length; i++) {
      var e = allday[i];
      var chip = document.createElement('div');
      chip.className = 'chip';
      chip.textContent = e.subject;
      if (e.location) chip.title = e.location;
      alldayEl.appendChild(chip);
    }

    if (timed.length === 0) {
      eventsEl.innerHTML = '<div class="empty">No more meetings today \u2728</div>';
    }

    // First upcoming (not-yet-started, not cancelled) is "next".
    var nextIdx = timed.findIndex(function (e) {
      return !e.isCancelled && new Date(e.end) > now && new Date(e.start) > now;
    });

    timed.forEach(function (e, idx) {
      var start = new Date(e.start), end = new Date(e.end);
      var isNow = !e.isCancelled && start <= now && end > now;
      var isPast = end <= now;
      var isNext = idx === nextIdx;

      var row = document.createElement('div');
      row.className = 'ev' + (e.isCancelled ? ' cancelled' : isNow ? ' now' : isPast ? ' past' : isNext ? ' next' : '');

      var when = document.createElement('div');
      when.className = 'when';
      when.innerHTML = fmtTime(e.start) + '<span class="end">' + fmtTime(e.end) + '</span>';

      var body = document.createElement('div');
      body.className = 'body';

      var subj = document.createElement('div');
      subj.className = 'subject';
      subj.textContent = e.subject;

      var meta = document.createElement('div');
      meta.className = 'meta';
      var dot = document.createElement('span');
      dot.className = 'dot ' + (e.isOnline ? 'online' : 'inperson');
      meta.appendChild(dot);
      var loc = document.createElement('span');
      loc.className = 'loc';
      loc.textContent = e.isOnline
        ? (e.location && !/teams meeting/i.test(e.location) ? e.location : 'Online')
        : (e.location || 'In person');
      meta.appendChild(loc);

      if (isNow) addPill(meta, 'now', 'Now');
      else if (isNext) addPill(meta, 'next', 'Next');
      else if (e.showAs === 'tentative') addPill(meta, 'tent', 'Tentative');

      body.appendChild(subj); body.appendChild(meta);
      row.appendChild(when); row.appendChild(body);
      eventsEl.appendChild(row);
    });

    if (data.generatedAt) {
      var gen = new Date(data.generatedAt);
      var ageH = (now - gen) / 3.6e6;
      var genStr = gen.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
      staleEl.textContent = 'Updated ' + genStr;
      staleEl.className = ageH > 30 ? 'stale warn' : 'stale';
    }
  }

  /* ---------- Data loading (cache-busted, no page reload) ---------- */
  function loadData() {
    var s = document.createElement('script');
    s.src = base + 'data.js?t=' + Date.now();
    s.onload = function () { renderCalendar(); s.remove(); };
    s.onerror = function () { s.remove(); };
    document.body.appendChild(s);
  }

  /* ---------- On/off state (driven by the module loader) ---------- */
  function applyState(on) {
    var cal = document.getElementById('cal');
    if (cal) cal.style.display = on ? '' : 'none';
  }

  /* ---------- Boot ---------- */
  injectPanel();
  loadData();
  if (window.Wallpaper && window.Wallpaper.onStateChange) {
    window.Wallpaper.onStateChange(MODULE_ID, applyState);
  }
  setInterval(renderCalendar, 60 * 1000);       // advance now/next/past highlighting
  setInterval(loadData, 5 * 60 * 1000);         // pull fresh data written by the host

  // Re-read right after wake from sleep / long idle so a resumed machine doesn't
  // show a stale calendar until the next interval tick.
  var lastTick = Date.now();
  setInterval(function () {
    var now = Date.now();
    if (now - lastTick > 90 * 1000) loadData();
    lastTick = now;
  }, 30 * 1000);
})();
