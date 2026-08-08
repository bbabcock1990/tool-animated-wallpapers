/* HtmlWallpaper interactive bridge.
 *
 * Loaded only by overlay-interactive.html (the clickable overlay window). It is the
 * generic, module-agnostic half of the "interactive panels" engine feature:
 *
 *   1. Region reporting — it measures every element a module marks with
 *      `data-wp-panel` and posts the rectangles (in device pixels) to the C# host,
 *      which clips the overlay window to just those rectangles. Everything outside a
 *      panel stays click-through to the desktop.
 *
 *   2. Link routing — a click on any element carrying `data-wp-href="https://…"`
 *      (or a descendant of one) is posted to the host, which opens it in the browser.
 *
 * Modules opt in purely by tagging their DOM; they need no host-specific code. The
 * same assets render identically in the ambient wallpaper (where these tags are inert
 * because that window can't receive clicks) and here.
 */
(function () {
  "use strict";

  var host = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
  if (!host) return; // not running inside the interactive overlay host

  function post(msg) {
    try { host.postMessage(JSON.stringify(msg)); } catch (e) {}
  }

  // ---- Region reporting --------------------------------------------------
  var lastSig = "__init__";

  function visible(el) {
    if (!el) return false;
    var s = window.getComputedStyle(el);
    if (s.display === "none" || s.visibility === "hidden" || parseFloat(s.opacity) === 0) return false;
    var r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1;
  }

  function collectRects() {
    var dpr = window.devicePixelRatio || 1;
    var els = document.querySelectorAll("[data-wp-panel]");
    var rects = [];
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      // Only clip/claim panels that actually have something clickable. Purely
      // informational panels (e.g. the calendar) are left to the ambient wallpaper,
      // so they keep their full-fidelity look and don't block the desktop.
      if (!el.querySelector("[data-wp-href]") && !el.hasAttribute("data-wp-href")) continue;
      if (!visible(el)) continue;
      var r = el.getBoundingClientRect();
      rects.push({
        x: Math.floor(r.left * dpr),
        y: Math.floor(r.top * dpr),
        w: Math.ceil(r.width * dpr),
        h: Math.ceil(r.height * dpr)
      });
    }
    return rects;
  }

  function reportRegions() {
    var rects = collectRects();
    var sig = JSON.stringify(rects);
    if (sig === lastSig) return; // nothing moved — don't thrash SetWindowRgn
    lastSig = sig;
    post({ type: "regions", rects: rects });
  }

  // Poll (covers relative-time re-renders, data refreshes, position changes) plus
  // observe DOM mutations and viewport resizes for immediate response.
  setInterval(reportRegions, 500);
  window.addEventListener("resize", reportRegions);
  try {
    new MutationObserver(reportRegions).observe(document.body, {
      childList: true, subtree: true, attributes: true
    });
  } catch (e) {}
  // First measure once panels have had a chance to render.
  setTimeout(reportRegions, 300);
  setTimeout(reportRegions, 1200);

  // ---- Link routing ------------------------------------------------------
  document.addEventListener("click", function (e) {
    var el = e.target;
    while (el && el !== document.body) {
      if (el.getAttribute && el.getAttribute("data-wp-href")) {
        var url = el.getAttribute("data-wp-href");
        if (url) {
          e.preventDefault();
          e.stopPropagation();
          post({ type: "open", url: url });
        }
        return;
      }
      el = el.parentNode;
    }
  }, true);
})();
