/* HtmlWallpaper module loader.
 *
 * Included once by the base wallpaper page. It reads the generated
 * modules/registry.js (window.WALLPAPER_REGISTRY), injects the CSS/JS of every
 * enabled module, and exposes a tiny SDK (window.Wallpaper) that modules use to
 * react to being toggled on/off. It re-reads the registry every couple of
 * seconds so tray/CLI enable/disable takes effect live, on every monitor,
 * without reloading the page (which would restart the base animation).
 *
 * A <script> tag is used to read the registry (not fetch/XHR) because WebView2
 * blocks file:// XHR by CORS. */
(function () {
  "use strict";

  var listeners = {};        // id -> [cb]
  var injected = {};         // id -> true once a module's assets are added
  var lastEnabled = {};      // id -> bool (last known state, to fire change events)

  function registry() { return window.WALLPAPER_REGISTRY || { modules: [] }; }
  function modules() { return registry().modules || []; }
  function find(id) { for (var i = 0; i < modules().length; i++) if (modules()[i].id === id) return modules()[i]; return null; }

  function isEnabled(id) { var m = find(id); return !!(m && m.enabled); }

  // ---- Public SDK exposed to modules -------------------------------------
  window.Wallpaper = {
    // Returns this module's folder URL (e.g. "modules/calendar/").
    base: function (id) { return "modules/" + id + "/"; },
    // Returns the module's settings object (from its manifest).
    settings: function (id) { var m = find(id); return (m && m.settings) || {}; },
    // Is the module currently enabled?
    isEnabled: isEnabled,
    // Subscribe to enable/disable changes. Fires immediately with the current state.
    onStateChange: function (id, cb) {
      (listeners[id] = listeners[id] || []).push(cb);
      try { cb(isEnabled(id)); } catch (e) {}
    }
  };

  // ---- Asset injection ---------------------------------------------------
  function injectModule(m) {
    if (injected[m.id]) return;
    injected[m.id] = true;
    (m.css || []).forEach(function (href) {
      var link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = href;
      document.head.appendChild(link);
    });
    // Load module scripts in declared order.
    (m.js || []).forEach(function (src) {
      var s = document.createElement("script");
      s.src = src;
      document.body.appendChild(s);
    });
  }

  function applyState() {
    modules().forEach(function (m) {
      var on = !!m.enabled;
      // Inject a module's assets the first time it becomes enabled.
      if (on) injectModule(m);
      // Fire change listeners when a module's state flips.
      if (lastEnabled[m.id] !== on) {
        lastEnabled[m.id] = on;
        (listeners[m.id] || []).forEach(function (cb) { try { cb(on); } catch (e) {} });
      }
    });
  }

  // ---- Registry polling (react to tray/CLI toggles) ----------------------
  function reloadRegistry() {
    var s = document.createElement("script");
    s.src = "modules/registry.js?t=" + Date.now();
    s.onload = function () { applyState(); s.remove(); };
    s.onerror = function () { s.remove(); }; // no registry yet => no modules
    document.body.appendChild(s);
  }

  // Boot: the registry.js already loaded via its own tag before us if present,
  // so apply immediately, then keep polling for changes.
  applyState();
  reloadRegistry();
  setInterval(reloadRegistry, 2000);
})();
