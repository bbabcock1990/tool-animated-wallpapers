/* Shared base animation for the live wallpaper: aurora ribbons, a particle
   constellation network, and a live clock/date/greeting. Expects these elements
   to exist in the host page: #aurora, #network, #hm, #ss, #date, #greet.
   Wrapped in an IIFE so it can coexist with optional overlay modules. */
(function () {
  "use strict";
  const DPR = Math.min(window.devicePixelRatio || 1, 1.5);

  /* ---------- Aurora ribbons ---------- */
  const aur = document.getElementById('aurora');
  const ax = aur.getContext('2d');
  const bands = [
    { hue: 165, amp: 0.10, y: 0.42, speed: 0.00022, len: 1.4, alpha: 0.35 },
    { hue: 200, amp: 0.14, y: 0.50, speed: 0.00017, len: 1.1, alpha: 0.32 },
    { hue: 275, amp: 0.11, y: 0.58, speed: 0.00026, len: 1.7, alpha: 0.28 },
    { hue: 300, amp: 0.16, y: 0.64, speed: 0.00013, len: 0.9, alpha: 0.22 },
  ];
  function drawAurora(t) {
    const w = aur.width, h = aur.height;
    ax.clearRect(0, 0, w, h);
    ax.globalCompositeOperation = 'lighter';
    for (const b of bands) {
      const grad = ax.createLinearGradient(0, 0, w, 0);
      grad.addColorStop(0.0, `hsla(${b.hue},90%,60%,0)`);
      grad.addColorStop(0.5, `hsla(${b.hue},90%,62%,${b.alpha})`);
      grad.addColorStop(1.0, `hsla(${b.hue + 40},90%,60%,0)`);
      ax.fillStyle = grad;
      ax.beginPath();
      ax.moveTo(0, h);
      const baseY = h * b.y;
      for (let x = 0; x <= w; x += 12) {
        const p = x / w;
        const y = baseY
          + Math.sin(p * Math.PI * 2 * b.len + t * b.speed) * h * b.amp
          + Math.sin(p * Math.PI * 5 + t * b.speed * 1.7) * h * b.amp * 0.35;
        ax.lineTo(x, y);
      }
      ax.lineTo(w, h);
      ax.closePath();
      ax.fill();
    }
    ax.globalCompositeOperation = 'source-over';
  }

  /* ---------- Particle network ---------- */
  const net = document.getElementById('network');
  const nx = net.getContext('2d');
  let nodes = [];
  function seedNodes() {
    const count = Math.round((net.width * net.height) / (26000 * DPR * DPR));
    nodes = Array.from({ length: count }, () => ({
      x: Math.random() * net.width,
      y: Math.random() * net.height,
      vx: (Math.random() - 0.5) * 0.25 * DPR,
      vy: (Math.random() - 0.5) * 0.25 * DPR,
      r: (Math.random() * 1.6 + 0.6) * DPR,
    }));
  }
  function drawNetwork() {
    const w = net.width, h = net.height;
    nx.clearRect(0, 0, w, h);
    const LINK = 150 * DPR;
    for (let i = 0; i < nodes.length; i++) {
      const a = nodes[i];
      a.x += a.vx; a.y += a.vy;
      if (a.x < 0 || a.x > w) a.vx *= -1;
      if (a.y < 0 || a.y > h) a.vy *= -1;
      for (let j = i + 1; j < nodes.length; j++) {
        const b = nodes[j];
        const dx = a.x - b.x, dy = a.y - b.y;
        const d = Math.hypot(dx, dy);
        if (d < LINK) {
          const o = (1 - d / LINK) * 0.5;
          nx.strokeStyle = `rgba(120,180,255,${o})`;
          nx.lineWidth = DPR;
          nx.beginPath(); nx.moveTo(a.x, a.y); nx.lineTo(b.x, b.y); nx.stroke();
        }
      }
      nx.beginPath();
      nx.fillStyle = 'rgba(190,220,255,0.9)';
      nx.arc(a.x, a.y, a.r, 0, Math.PI * 2);
      nx.fill();
    }
  }

  /* ---------- Clock ---------- */
  function pad(n) { return String(n).padStart(2, '0'); }
  function tick() {
    const d = new Date();
    document.getElementById('hm').textContent = pad(d.getHours()) + ':' + pad(d.getMinutes());
    document.getElementById('ss').textContent = pad(d.getSeconds());
    document.getElementById('date').textContent =
      d.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' });
    const hr = d.getHours();
    const greet = hr < 12 ? 'Good morning' : hr < 18 ? 'Good afternoon' : 'Good evening';
    document.getElementById('greet').textContent = greet;
  }

  /* ---------- Resize & loop ---------- */
  function resize() {
    for (const c of [aur, net]) {
      c.width = Math.floor(innerWidth * DPR);
      c.height = Math.floor(innerHeight * DPR);
      c.style.width = innerWidth + 'px';
      c.style.height = innerHeight + 'px';
    }
    updateLayoutMode();
    seedNodes();
  }
  // Detect a multi-monitor span: a very wide aspect ratio means the single
  // WebView canvas covers two (or more) side-by-side monitors. Toggle 'span' so
  // the clock centers on one monitor instead of the bezel gap.
  function updateLayoutMode() {
    document.body.classList.toggle('span', (innerWidth / innerHeight) > 2.5);
  }
  addEventListener('resize', resize);
  resize(); tick(); setInterval(tick, 1000);

  function frame(t) {
    drawAurora(t);
    drawNetwork();
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
})();
