/* Outlook "Today" calendar overlay — an optional, self-contained module for the
   HTML live wallpaper.

   Turn it ON for ANY wallpaper by adding a single line before </body>:
       <script src="calendar/calendar-overlay.js"></script>
   Turn it OFF by removing that line. It injects its own stylesheet and panel DOM,
   loads the generated data file (calendar-events.js) next to itself, renders a
   top-right "Today" panel, and keeps it current during the day.

   Data is produced by Update-Calendar.ps1 (WorkIQ CLI) and refreshed by the
   HtmlWallpaper-CalendarRefresh scheduled task (see Register-CalendarTask.ps1).
   The panel re-reads that file periodically WITHOUT a full page reload, so the
   base animation never restarts. A <script> load is used (not fetch/XHR) because
   WebView2 blocks file:// XHR by CORS. */
(function () {
  "use strict";

  // Resolve this module's own folder so its sibling resources (CSS + data) load
  // correctly regardless of where the host wallpaper HTML lives.
  const self = document.currentScript;
  const baseUrl = self ? self.src.replace(/[^/]*$/, '') : '';

  function injectCss() {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = baseUrl + 'calendar-overlay.css';
    document.head.appendChild(link);
  }

  function injectPanel() {
    const cal = document.createElement('div');
    cal.className = 'cal';
    cal.id = 'cal';
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
    const d = new Date(dt);
    let h = d.getHours(); const m = d.getMinutes();
    const ap = h < 12 ? 'AM' : 'PM';
    h = h % 12; if (h === 0) h = 12;
    return m === 0 ? `${h} ${ap}` : `${h}:${pad(m)} ${ap}`;
  }
  function addPill(container, cls, text) {
    const p = document.createElement('span');
    p.className = 'pill ' + cls;
    p.textContent = text;
    container.appendChild(p);
  }

  function renderCalendar() {
    const data = window.CALENDAR_DATA;
    const calDate = document.getElementById('calDate');
    const alldayEl = document.getElementById('allday');
    const eventsEl = document.getElementById('events');
    const staleEl = document.getElementById('stale');
    if (!alldayEl || !eventsEl) return;
    alldayEl.innerHTML = ''; eventsEl.innerHTML = '';

    if (!data || !Array.isArray(data.events)) {
      eventsEl.innerHTML = '<div class="empty">Calendar data unavailable</div>';
      return;
    }

    const now = new Date();
    calDate.textContent = now.toLocaleDateString([], { month: 'short', day: 'numeric' });

    const allday = data.events.filter(e => e.isAllDay);
    const timed  = data.events.filter(e => !e.isAllDay)
                              .sort((a, b) => new Date(a.start) - new Date(b.start));

    for (const e of allday) {
      const chip = document.createElement('div');
      chip.className = 'chip';
      chip.textContent = e.subject;
      if (e.location) chip.title = e.location;
      alldayEl.appendChild(chip);
    }

    if (timed.length === 0) {
      eventsEl.innerHTML = '<div class="empty">No more meetings today \u2728</div>';
    }

    // First upcoming (not-yet-started, not cancelled) is "next".
    const nextIdx = timed.findIndex(e => !e.isCancelled && new Date(e.end) > now && new Date(e.start) > now);

    timed.forEach((e, i) => {
      const start = new Date(e.start), end = new Date(e.end);
      const isNow  = !e.isCancelled && start <= now && end > now;
      const isPast = end <= now;
      const isNext = i === nextIdx;

      const row = document.createElement('div');
      row.className = 'ev' + (e.isCancelled ? ' cancelled' : isNow ? ' now' : isPast ? ' past' : isNext ? ' next' : '');

      const when = document.createElement('div');
      when.className = 'when';
      when.innerHTML = `${fmtTime(e.start)}<span class="end">${fmtTime(e.end)}</span>`;

      const body = document.createElement('div');
      body.className = 'body';

      const subj = document.createElement('div');
      subj.className = 'subject';
      subj.textContent = e.subject;

      const meta = document.createElement('div');
      meta.className = 'meta';
      const dot = document.createElement('span');
      dot.className = 'dot ' + (e.isOnline ? 'online' : 'inperson');
      meta.appendChild(dot);
      const loc = document.createElement('span');
      loc.className = 'loc';
      loc.textContent = e.isOnline ? (e.location && !/teams meeting/i.test(e.location) ? e.location : 'Online') : (e.location || 'In person');
      meta.appendChild(loc);

      if (isNow)                         addPill(meta, 'now', 'Now');
      else if (isNext)                   addPill(meta, 'next', 'Next');
      else if (e.showAs === 'tentative') addPill(meta, 'tent', 'Tentative');

      body.appendChild(subj); body.appendChild(meta);
      row.appendChild(when); row.appendChild(body);
      eventsEl.appendChild(row);
    });

    // Staleness indicator based on generatedAt.
    if (data.generatedAt) {
      const gen = new Date(data.generatedAt);
      const ageH = (now - gen) / 3.6e6;
      const genStr = gen.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
      staleEl.textContent = 'Updated ' + genStr;
      staleEl.className = ageH > 30 ? 'stale warn' : 'stale';
    }
  }

  /* ---------- Data loading (cache-busted, no page reload) ---------- */
  function loadData() {
    const s = document.createElement('script');
    s.src = baseUrl + 'calendar-events.js?t=' + Date.now();
    s.onload = () => { renderCalendar(); s.remove(); };
    s.onerror = () => { s.remove(); };
    document.body.appendChild(s);
  }

  /* ---------- Boot ---------- */
  injectCss();
  injectPanel();
  loadData();                                   // initial data
  setInterval(renderCalendar, 60 * 1000);       // advance now/next/past highlighting
  setInterval(loadData, 5 * 60 * 1000);         // pull fresh data written by the task

  // Re-read right after wake from sleep / long idle so a resumed machine doesn't
  // show a stale calendar until the next interval tick.
  let lastTick = Date.now();
  setInterval(() => {
    const now = Date.now();
    if (now - lastTick > 90 * 1000) loadData();  // clock jumped => was asleep
    lastTick = now;
  }, 30 * 1000);
})();
