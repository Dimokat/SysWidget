// SysWidget homepage: live demos of the widget. The drawing is a port of the app's own drawing code (C#/GDI+)
// to canvas, fed with made-up numbers, so what you see behaves like the real widget. Nothing here measures your
// computer.
(function () {
  'use strict';

  var FONT = '"Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif';
  var reduceMotion = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ------------------------------------------------------------------ colours (the widget's defaults)
  function rgb(r, g, b) { return { r: r, g: g, b: b }; }
  function css(c, a) { return 'rgba(' + c.r + ',' + c.g + ',' + c.b + ',' + (a === undefined ? 1 : a / 255) + ')'; }
  function mix(a, b, t) { return rgb(Math.round(a.r + (b.r - a.r) * t), Math.round(a.g + (b.g - a.g) * t), Math.round(a.b + (b.b - a.b) * t)); }
  var COL = {
    cpu: rgb(0x4F, 0xC3, 0xF7), fps: rgb(0xFF, 0xD5, 0x4F), ram: rgb(0xFF, 0xB7, 0x4D), net: rgb(0xBA, 0x68, 0xC8),
    'net.up': rgb(0xF0, 0x62, 0x92), disk: rgb(0x90, 0xA4, 0xAE), ping: rgb(0x4D, 0xD0, 0xE1), battery: rgb(0xAE, 0xD5, 0x81),
    media: rgb(0xCE, 0x93, 0xD8), 'gpu:0': rgb(0x81, 0xC7, 0x84), 'gpu:1': rgb(0x4D, 0xB6, 0xAC)
  };
  var WARN = rgb(0xFF, 0xA7, 0x26), HOT = rgb(0xEF, 0x53, 0x50);
  function lum(c) {
    function f(v) { var x = v / 255; return x <= 0.04045 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); }
    return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
  }
  // Keeps the hue but darkens a light accent until white text has about 10:1 contrast on it (as the app does).
  function panelFromAccent(a) {
    for (var i = 0; i < 60 && lum(a) > 0.055; i++) a = rgb(Math.floor(a.r * 0.93), Math.floor(a.g * 0.93), Math.floor(a.b * 0.93));
    return a;
  }
  function makeTheme(kind) { // 0 dark, 1 accent (Windows' default blue), 2 light
    var light = kind === 2, ink = light ? rgb(24, 26, 30) : rgb(255, 255, 255);
    var bg = kind === 1 ? panelFromAccent(rgb(0, 120, 215)) : light ? rgb(243, 244, 246) : rgb(18, 19, 22);
    return {
      kind: kind, light: light, bg: bg, ink: ink,
      label: css(ink, light ? 175 : 150), value: css(ink, light ? 240 : 235), dim: css(ink, light ? 130 : 105),
      warn: css(light ? rgb(0xD9, 0x74, 0x00) : WARN), hot: css(light ? rgb(0xC6, 0x28, 0x28) : HOT),
      inkA: function (a) { return css(ink, a); },
      shade: function (c) { return light ? rgb(Math.floor(c.r * 0.62), Math.floor(c.g * 0.62), Math.floor(c.b * 0.62)) : c; }
    };
  }

  // ------------------------------------------------------------------ made-up numbers, once a second
  var CAP = 60;
  function push(ring, v) { ring.push(v); if (ring.length > CAP) ring.shift(); }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function wander(v, target, step, lo, hi) { return clamp(v + (target - v) * 0.35 + (Math.random() - 0.5) * step, lo, hi); }
  function maxOf(a) { var m = 0; for (var i = 0; i < a.length; i++) if (a[i] > m) m = a[i]; return m; }

  var TRACKS = [
    { title: 'Midnight Drive', artist: 'Neon Harbor', album: 'City Lights', dur: 214, hue: 268 },
    { title: 'Paper Moons', artist: 'Lumen Fields', album: 'Afterglow', dur: 187, hue: 18 },
    { title: 'Low Tide', artist: 'Coastline Radio', album: 'Low Tide', dur: 242, hue: 190 }
  ];
  var PLAYER = 'Spotify';
  var PROCS = {
    game: [['starfall', 22, 3.1e9], ['chrome', 3.2, 1.6e9], ['Discord', 1.4, 420e6], ['obs64', 2.6, 380e6], ['dwm', 1.8, 140e6], ['Spotify', 0.6, 310e6], ['SysWidget', 0.03, 38e6], ['explorer', 0.3, 160e6]],
    browse: [['chrome', 6.5, 2.2e9], ['Code', 2.4, 900e6], ['Discord', 1.1, 410e6], ['dwm', 1.2, 130e6], ['Spotify', 0.7, 300e6], ['explorer', 0.5, 170e6], ['SysWidget', 0.03, 38e6], ['MsMpEng', 0.4, 210e6]]
  };

  var sim = {
    t: 0, phase: 'browse',
    cpu: 8, cpuTemp: 50, hCpu: [],
    gpus: [
      { label: 'GPU', name: 'RTX 4070', integrated: false, load: 3, temp: 40, mem: 1.2, memTotal: 12, power: 14, hist: [] },
      { label: 'iGPU', name: 'Radeon', integrated: true, load: 2, temp: 45, mem: 0.4, memTotal: 8.2, power: NaN, hist: [] }
    ],
    fps: 60, fpsApp: 'chrome', hFps: [],
    ram: 11.4, ramTotal: 31.9,
    down: 40e3, up: 6e3, hDown: [], hUp: [],
    ping: 14, hPing: [], host: '1.1.1.1',
    diskR: 120e3, diskW: 40e3, drives: [{ name: 'C:', free: 312, total: 931 }, { name: 'D:', free: 1210, total: 1863 }],
    track: 0, playing: true, pos: 129, apps: { cpu: [], mem: [] }, songChanged: 0
  };
  function gpu(id) { return sim.gpus[+id.split(':')[1]]; }

  // Alternates between browsing and a game every half minute, with the odd download and CPU spike.
  function tick() {
    sim.t++;
    if (sim.t % 30 === 0) sim.phase = sim.phase === 'game' ? 'browse' : 'game';
    var game = sim.phase === 'game', burst = Math.random() < 0.12 ? 25 : 0;
    sim.cpu = wander(sim.cpu, (game ? 34 : 9) + burst, 8, 1, 100);
    sim.cpuTemp = wander(sim.cpuTemp, game ? 71 : 52, 2, 35, 99);
    var d = sim.gpus[0], i = sim.gpus[1];
    d.load = wander(d.load, game ? 96 : 4, game ? 5 : 3, 0, 100); d.temp = wander(d.temp, game ? 68 : 41, 1.5, 30, 95);
    d.mem = wander(d.mem, game ? 8.9 : 1.3, 0.2, 0.3, 12); d.power = wander(d.power, game ? 182 : 15, game ? 12 : 3, 5, 220);
    i.load = wander(i.load, game ? 6 : 3, 3, 0, 100); i.temp = wander(i.temp, game ? 58 : 46, 1, 30, 95); i.mem = wander(i.mem, 0.5, 0.05, 0.2, 8);
    sim.fps = wander(sim.fps, game ? 142 : 60, game ? 14 : 2, 1, 240); sim.fpsApp = game ? 'starfall' : 'chrome';
    sim.ram = wander(sim.ram, game ? 18.2 : 11.6, 0.3, 4, 31);
    sim.down = wander(sim.down, Math.random() < 0.15 ? 4.8e6 : (game ? 180e3 : 60e3), game ? 60e3 : 30e3, 0, 2e7);
    sim.up = wander(sim.up, game ? 90e3 : 9e3, 8e3, 0, 5e6);
    sim.ping = wander(sim.ping, Math.random() < 0.08 ? 48 : 14, 3, 3, 400);
    sim.diskR = wander(sim.diskR, Math.random() < 0.1 ? 38e6 : 120e3, 60e3, 0, 2e9);
    sim.diskW = wander(sim.diskW, Math.random() < 0.1 ? 12e6 : 50e3, 30e3, 0, 2e9);
    push(sim.hCpu, sim.cpu); push(d.hist, d.load); push(i.hist, i.load); push(sim.hFps, sim.fps);
    push(sim.hDown, sim.down); push(sim.hUp, sim.up); push(sim.hPing, sim.ping);
    if (sim.playing && ++sim.pos >= TRACKS[sim.track].dur) { sim.track = (sim.track + 1) % TRACKS.length; sim.pos = 0; sim.songChanged++; }
    var list = PROCS[sim.phase], scale = sim.cpu / (game ? 34 : 9);
    sim.apps.cpu = list.map(function (p) { return [p[0], Math.max(0.01, p[1] * (0.7 + Math.random() * 0.6) * scale)]; })
      .sort(function (a, b) { return b[1] - a[1]; }).slice(0, 5);
    sim.apps.mem = list.map(function (p) { return [p[0], p[2] * (0.97 + Math.random() * 0.06)]; })
      .sort(function (a, b) { return b[1] - a[1]; }).slice(0, 5);
  }
  for (var k = 0; k < CAP; k++) tick();
  sim.songChanged = 0;

  // ------------------------------------------------------------------ text formats (English, as in lang/en.txt)
  function pct(v) { return Math.round(v) + '%'; }
  function tempText(t) { return Math.round(t) + '°C'; }
  function rate(b) {
    if (b < 1024) return b.toFixed(0) + ' B/s';
    if (b < 1048576) return (b / 1024).toFixed(b < 10240 ? 1 : 0) + ' KB/s';
    return (b / 1048576).toFixed(b < 10485760 ? 1 : 0) + ' MB/s';
  }
  function bytes(v) { return v >= 1073741824 ? (v / 1073741824).toFixed(1) + ' GB' : (v / 1048576).toFixed(0) + ' MB'; }
  function timeText(sec) { var t = Math.max(0, Math.floor(sec)); return Math.floor(t / 60) + ':' + ('0' + t % 60).slice(-2); }
  var LABEL = { cpu: 'CPU', fps: 'FPS', ram: 'RAM', net: 'NET', disk: 'DISK', ping: 'PING', media: 'MEDIA' };

  // ------------------------------------------------------------------ paths (GDI+ GraphicsPath rules)
  function Path() { this.ops = []; }
  Path.prototype.arc = function (x, y, w, h, st, sw) { this.ops.push([0, x, y, w, h, st, sw]); return this; };
  Path.prototype.line = function (x1, y1, x2, y2) { this.ops.push([1, x1, y1, x2, y2]); return this; };
  Path.prototype.close = function () { this.ops.push([2]); return this; };
  Path.prototype.figure = function () { this.ops.push([3]); return this; };
  Path.prototype.trace = function (g) {
    g.beginPath(); var fresh = true;
    for (var i = 0; i < this.ops.length; i++) {
      var o = this.ops[i];
      if (o[0] === 0) {
        var rx = Math.max(o[3] / 2, 0.001), ry = Math.max(o[4] / 2, 0.001), cx = o[1] + o[3] / 2, cy = o[2] + o[4] / 2;
        var a0 = o[5] * Math.PI / 180, a1 = (o[5] + o[6]) * Math.PI / 180;
        if (fresh) { g.moveTo(cx + rx * Math.cos(a0), cy + ry * Math.sin(a0)); fresh = false; }
        g.ellipse(cx, cy, rx, ry, 0, a0, a1, o[6] < 0);
      } else if (o[0] === 1) {
        if (fresh) { g.moveTo(o[1], o[2]); fresh = false; } else g.lineTo(o[1], o[2]);
        g.lineTo(o[3], o[4]);
      } else if (o[0] === 2) { g.closePath(); fresh = true; }
      else fresh = true;
    }
  };
  function roundPath(x, y, w, h, rad) {
    var d = Math.max(1, Math.min(rad * 2, Math.min(w, h)));
    return new Path().arc(x, y, d, d, 180, 90).arc(x + w - d, y, d, d, 270, 90).arc(x + w - d, y + h - d, d, d, 0, 90).arc(x, y + h - d, d, d, 90, 90).close();
  }
  // The app's outline rules: rounded when free, a concave fillet into the screen edge where it's docked.
  function shapePath(r, rad, fillet, dL, dT, dR, dB) {
    var d = Math.min(rad * 2, Math.min(r.w, r.h)), fd = fillet * 2;
    var L = r.x + (dL ? -0.5 : 0.5), T = r.y + (dT ? -0.5 : 0.5), R = r.x + r.w - 0.5, B = r.y + r.h - 0.5, p = new Path();
    if (dT && dL) p.line(L, T, L, T); else if (dT) p.arc(L - fd, T, fd, fd, 0, -90); else if (dL) p.arc(L, T - fd, fd, fd, 180, -90); else p.arc(L, T, d, d, 180, 90);
    if (dT && dR) p.line(R, T, R, T); else if (dT) p.arc(R, T, fd, fd, 270, -90); else if (dR) p.arc(R - fd, T - fd, fd, fd, 90, -90); else p.arc(R - d, T, d, d, 270, 90);
    if (dB && dR) p.line(R, B, R, B); else if (dR) p.arc(R - fd, B, fd, fd, 0, -90); else if (dB) p.arc(R, B - fd, fd, fd, 180, -90); else p.arc(R - d, B - d, d, d, 0, 90);
    if (dB && dL) p.line(L, B, L, B); else if (dB) p.arc(L - fd, B - fd, fd, fd, 90, -90); else if (dL) p.arc(L, B, fd, fd, 270, -90); else p.arc(L, B - d, d, d, 90, 90);
    return p.close();
  }
  function cardPath(r, rad, open, skip, allRound) {
    var p = new Path(), d = Math.max(0.1, rad * 2), R = r.x + r.w, B = r.y + r.h;
    if (allRound) return p.arc(r.x, r.y, d, d, 180, 90).arc(R - d, r.y, d, d, 270, 90).arc(R - d, B - d, d, d, 0, 90).arc(r.x, B - d, d, d, 90, 90).close();
    p.line(r.x, r.y + skip, r.x, B - rad).arc(r.x, B - d, d, d, 180, -90).arc(R - d, B - d, d, d, 90, -90).line(R, B - rad, R, r.y + skip);
    if (!open) p.close();
    return p;
  }

  // ------------------------------------------------------------------ the painter: the app's drawing code
  var ascents = {};
  function Font(weight, px) { this.px = px; this.css = weight + ' ' + px + 'px ' + FONT; }
  function Painter(g, th, s) {
    this.g = g; this.th = th; this.s = s; this.pad = 12 * s; this.W = Math.round(250 * s); this.H = 0;
    this.hdrH = 17 * s; this.smallH = 14 * s;
    this.fLabel = new Font(600, Math.max(7, Math.round(10.5 * s)));
    this.fValue = new Font(400, Math.max(8, Math.round(12.5 * s)));
    this.fSmall = new Font(400, Math.max(7, Math.round(10.5 * s)));
    this.rects = {}; this.colW = 0; this.mediaBtns = []; this.cardBtns = []; this.draw = true; this.y = 0;
  }
  var P = Painter.prototype;
  P.color = function (id) { return this.th.shade(COL[id] || COL.cpu); };
  P.asc = function (f) {
    var a = ascents[f.css];
    if (a === undefined) { this.g.font = f.css; var m = this.g.measureText('Hg'); a = ascents[f.css] = m.fontBoundingBoxAscent || f.px * 1.08; }
    return a;
  };
  P.measure = function (t, f) { this.g.font = f.css; return this.g.measureText(t).width; };
  P.txt = function (t, f, color, x, y) {
    if (!this.draw || !t) return;
    var g = this.g; g.font = f.css; g.fillStyle = color; g.textBaseline = 'alphabetic';
    g.fillText(t, Math.round(x), Math.round(Math.round(y) + this.asc(f)));
  };
  P.fit = function (t, f, width) {
    if (!t) return '';
    if (this.measure(t, f) <= width) return t;
    var lo = 0, hi = t.length;
    while (lo < hi) { var mid = (lo + hi + 1) >> 1; if (this.measure(t.substring(0, mid) + '…', f) <= width) lo = mid; else hi = mid - 1; }
    return t.substring(0, lo).replace(/\s+$/, '') + '…';
  };
  P.fill = function (path, color) { if (!this.draw) return; path.trace(this.g); this.g.fillStyle = color; this.g.fill(); };
  P.dot = function (x, y, d, color) { if (!this.draw) return; var g = this.g; g.beginPath(); g.arc(x + d / 2, y + d / 2, d / 2, 0, Math.PI * 2); g.fillStyle = css(color); g.fill(); };
  P.tempBrush = function (t) { return t >= 90 ? this.th.hot : t >= 80 ? this.th.warn : this.th.value; };

  P.header = function (label, accent, v1, b1, v2, b2) {
    var s = this.s, pad = this.pad;
    this.dot(pad, this.y + 5.5 * s, 6 * s, accent);
    this.txt(label, this.fLabel, this.th.label, pad + 11 * s, this.y + 1.5 * s);
    var right = this.W - pad;
    if (v2) { var w2 = this.measure(v2, this.fValue); this.txt(v2, this.fValue, b2, right - w2, this.y); right -= w2 + 10 * s; }
    if (v1) { var w1 = this.measure(v1, this.fValue); this.txt(v1, this.fValue, b1, right - w1, this.y); }
    this.y += this.hdrH;
  };
  P.small = function (left, right) {
    this.txt(left, this.fSmall, this.th.dim, this.pad, this.y);
    this.txt(right, this.fSmall, this.th.label, this.W - this.pad - this.measure(right, this.fSmall), this.y);
    this.y += this.smallH;
  };
  P.graph = function (x, y, w, h, a, max, ca, b, cb) {
    if (!this.draw) return;
    var g = this.g, bg = roundPath(x, y, w, h, 4 * this.s);
    this.fill(bg, this.th.inkA(this.th.light ? 10 : 14));
    g.save(); bg.trace(g); g.clip();
    this.series(x, y, w, h, a, max, ca, true);
    if (b) this.series(x, y, w, h, b, max, cb, false);
    g.restore();
  };
  P.series = function (x, y, w, h, r, max, c, fill) {
    var n = r.length; if (n < 2 || max <= 0) return;
    var g = this.g, step = w / (CAP - 1), pts = [], k;
    for (k = 0; k < n; k++) { var v = clamp(r[k] / max, 0, 1); pts.push([x + w - (n - 1 - k) * step, y + h - 1 - v * (h - 2)]); }
    if (fill) {
      g.beginPath(); g.moveTo(pts[0][0], pts[0][1]);
      for (k = 1; k < n; k++) g.lineTo(pts[k][0], pts[k][1]);
      g.lineTo(pts[n - 1][0], y + h); g.lineTo(pts[0][0], y + h); g.closePath();
      g.fillStyle = css(c, 55); g.fill();
    }
    g.beginPath(); g.moveTo(pts[0][0], pts[0][1]);
    for (k = 1; k < n; k++) g.lineTo(pts[k][0], pts[k][1]);
    g.strokeStyle = css(c, fill ? 230 : 200); g.lineWidth = 1.3 * this.s; g.lineJoin = 'round'; g.stroke();
  };
  P.bar = function (x, y, w, h, frac, c) {
    frac = clamp(frac, 0, 1);
    this.fill(roundPath(x, y, w, h, h / 2), this.th.inkA(26));
    if (frac > 0) this.fill(roundPath(x, y, Math.max(h, w * frac), h, h / 2), css(c));
  };

  // One section, as in the full panel and the island's cards. Returns the gap after it.
  P.section = function (id, x, w, gh, sgap) {
    var th = this.th, c = this.color(id), s = this.s;
    switch (id) {
      case 'cpu':
        this.header(LABEL.cpu, c, pct(sim.cpu), th.value, tempText(sim.cpuTemp), this.tempBrush(sim.cpuTemp));
        this.y += 3 * s; this.graph(x, this.y, w, gh, sim.hCpu, 100, c); this.y += gh;
        return sgap;
      case 'fps':
        this.header(LABEL.fps, c, String(Math.round(sim.fps)), th.value);
        this.small(sim.fpsApp, '');
        this.y += 3 * s; this.graph(x, this.y, w, gh, sim.hFps, Math.max(60, maxOf(sim.hFps)), c); this.y += gh;
        return sgap;
      case 'ram':
        this.header(LABEL.ram, c, sim.ram.toFixed(1) + ' / ' + sim.ramTotal.toFixed(1) + ' GB', th.value);
        this.y += 5 * s; this.bar(x, this.y, w, 5 * s, sim.ram / sim.ramTotal, c); this.y += 5 * s;
        return sgap + s;
      case 'net':
        this.header(LABEL.net, c, '↓ ' + rate(sim.down), th.value, '↑ ' + rate(sim.up), th.label);
        this.y += 3 * s;
        this.graph(x, this.y, w, gh, sim.hDown, Math.max(64 * 1024, maxOf(sim.hDown), maxOf(sim.hUp)), c, sim.hUp, this.color('net.up')); this.y += gh;
        return sgap;
      case 'ping':
        var ms = sim.ping;
        this.header(LABEL.ping, c, Math.round(ms) + ' ms', ms >= 150 ? th.hot : ms >= 80 ? th.warn : th.value);
        this.small(sim.host, '');
        this.y += 3 * s; this.graph(x, this.y, w, gh, sim.hPing, Math.max(50, maxOf(sim.hPing)), c); this.y += gh;
        return sgap;
      case 'disk':
        this.header(LABEL.disk, c, 'R ' + rate(sim.diskR), th.value, 'W ' + rate(sim.diskW), th.value);
        this.y += 4 * s; var dg = 0;
        for (var i = 0; i < sim.drives.length; i++) {
          var dr = sim.drives[i];
          this.small(dr.name, Math.round(dr.free) + ' GB free of ' + Math.round(dr.total));
          this.y += s;
          this.bar(x, this.y, w, 4 * s, 1 - dr.free / dr.total, c);
          dg = 7 * s; this.y += 4 * s + dg;
        }
        this.y -= dg; return sgap;
      case 'media':
        var y0 = this.y, t = TRACKS[sim.track];
        this.header(LABEL.media, c);
        this.mediaButtons(y0);
        this.txt(this.fit(t.title, this.fValue, w), this.fValue, th.value, this.pad, this.y);
        this.y += this.hdrH;
        this.txt(this.fit(t.artist + '  ·  ' + PLAYER, this.fSmall, w), this.fSmall, th.dim, this.pad, this.y);
        this.y += this.smallH;
        return sgap;
    }
    var gp = gpu(id);
    this.header(gp.label + ' · ' + gp.name, c, pct(gp.load), th.value, tempText(gp.temp), this.tempBrush(gp.temp));
    this.small((gp.integrated ? 'Memory ' : 'VRAM ') + gp.mem.toFixed(1) + ' / ' + gp.memTotal.toFixed(1) + ' GB', isNaN(gp.power) ? '' : gp.power.toFixed(1) + ' W');
    this.y += 3 * s; this.graph(x, this.y, w, gh, gp.hist, 100, c); this.y += gh;
    return sgap;
  };

  P.playIcon = function (i, cx, cy, a, tiny) {
    var g = this.g; g.fillStyle = this.th.value;
    if (i === 1) {
      if (sim.playing) { g.fillRect(cx - a * 0.75, cy - a * 0.9, a * 0.55, a * 1.8); g.fillRect(cx + a * 0.2, cy - a * 0.9, a * 0.55, a * 1.8); }
      else { var l = tiny ? 0.6 : 0.55, r = tiny ? 0.9 : 0.95; g.beginPath(); g.moveTo(cx - a * l, cy - a); g.lineTo(cx + a * r, cy); g.lineTo(cx - a * l, cy + a); g.closePath(); g.fill(); }
      return;
    }
    var dir = i === 0 ? -1 : 1, bw = tiny ? 1.6 * this.s : Math.max(1.5 * this.s, a * 0.3);
    g.beginPath(); g.moveTo(cx - dir * a * 0.6, cy - a * 0.85); g.lineTo(cx + dir * a * 0.5, cy); g.lineTo(cx - dir * a * 0.6, cy + a * 0.85); g.closePath(); g.fill();
    g.fillRect(dir > 0 ? cx + a * 0.5 : cx - a * 0.5 - bw, cy - a * 0.85, bw, a * 1.7);
  };
  P.mediaButtons = function (top) {
    var s = this.s, bs = 18 * s, gap = 4 * s, cy = top + 8.5 * s, right = this.W - this.pad + 3 * s, a = 4.2 * s, rects = [];
    for (var i = 2; i >= 0; i--) { rects[i] = { x: right - bs, y: cy - bs / 2, w: bs, h: bs }; right -= bs + gap; }
    this.mediaBtns = rects;
    if (this.draw) for (i = 0; i < 3; i++) this.playIcon(i, rects[i].x + bs / 2, cy, a, true);
  };

  // The island's card for a stat (300 px wide at 100%).
  P.card = function (id, cw) {
    var oldW = this.W, s = this.s; this.W = cw; this.hdrH = 17 * s; this.smallH = 14 * s;
    var x = this.pad, w = cw - 2 * this.pad; this.y = this.pad * 0.8;
    if (id === 'media') this.mediaCard(x, w);
    else {
      this.section(id, x, w, 26 * s, 11 * s);
      if (id === 'cpu' || id === 'ram') this.appsList(id === 'cpu', x, w, this.y + 9 * s);
    }
    this.W = oldW;
    return this.y + this.pad * 0.8;
  };
  P.appsList = function (isCpu, x, w, y) {
    var s = this.s, th = this.th, list = isCpu ? sim.apps.cpu : sim.apps.mem, max = 0, i;
    this.txt((isCpu ? 'Using the most CPU' : 'Using the most memory').toUpperCase(), this.fSmall, th.dim, x, y);
    y += this.smallH + 3 * s;
    for (i = 0; i < list.length; i++) max = Math.max(max, list[i][1]);
    var c = this.color(isCpu ? 'cpu' : 'ram');
    for (i = 0; i < list.length; i++) {
      var v = list[i][1], val = isCpu ? v.toFixed(v < 10 ? 1 : 0) + '%' : bytes(v), vw = this.measure(val, this.fSmall);
      this.txt(this.fit(list[i][0], this.fSmall, w - vw - 10 * s), this.fSmall, th.value, x, y);
      this.txt(val, this.fSmall, th.label, x + w - vw, y);
      y += this.smallH + s;
      if (this.draw) {
        this.g.fillStyle = th.inkA(22); this.g.fillRect(x, y, w, 2.5 * s);
        this.g.fillStyle = css(c); this.g.fillRect(x, y, Math.max(2 * s, w * (max > 0 ? v / max : 0)), 2.5 * s);
      }
      y += 7 * s;
    }
    this.y = y - 4 * s;
  };
  P.mediaCard = function (x, w) {
    var s = this.s, th = this.th, t = TRACKS[sim.track], y = this.y, art = 64 * s, g = this.g;
    this.cardBtns = [];
    if (this.draw) { g.save(); roundPath(x, y, art, art, 8 * s).trace(g); g.clip(); g.drawImage(albumArt(sim.track), x, y, art, art); g.restore(); }
    var tx = x + art + 12 * s, tw = w - art - 12 * s, ty = y + 5 * s;
    this.txt(this.fit(t.title, this.fValue, tw), this.fValue, th.value, tx, ty);
    ty += this.hdrH + 2 * s;
    this.txt(this.fit(t.artist, this.fSmall, tw), this.fSmall, th.label, tx, ty); ty += this.smallH;
    this.txt(this.fit(t.album + '  ·  ' + PLAYER, this.fSmall, tw), this.fSmall, th.dim, tx, ty);
    y += art + 10 * s;
    this.bar(x, y, w, 4 * s, sim.pos / t.dur, this.color('media'));
    y += 7 * s;
    var b2 = timeText(t.dur);
    this.txt(timeText(sim.pos), this.fSmall, th.dim, x, y);
    this.txt(b2, this.fSmall, th.dim, x + w - this.measure(b2, this.fSmall), y);
    y += this.smallH + 2 * s;
    var bs = 34 * s, gap = 16 * s, bx = x + (w - (3 * bs + 2 * gap)) / 2;
    for (var i = 0; i < 3; i++) {
      var r = { x: bx + i * (bs + gap), y: y, w: bs, h: bs };
      this.cardBtns.push(r);
      if (!this.draw) continue;
      if (i === 1) { g.beginPath(); g.arc(r.x + bs / 2, r.y + bs / 2, bs / 2, 0, Math.PI * 2); g.fillStyle = th.inkA(24); g.fill(); }
      this.playIcon(i, r.x + bs / 2, r.y + bs / 2, (i === 1 ? 6.5 : 5.5) * s, false);
    }
    this.y = y + bs;
  };

  // Small line icons in the section's colour (mini mode).
  P.icon = function (kind, cx, cy, sz, col) {
    if (!this.draw) return;
    var g = this.g, s = this.s, u = sz / 2, c = css(col), w, h, r, i;
    g.save(); g.strokeStyle = c; g.fillStyle = c; g.lineWidth = Math.max(1, 1.3 * s); g.lineJoin = 'round'; g.lineCap = 'round';
    function line(x1, y1, x2, y2) { g.beginPath(); g.moveTo(x1, y1); g.lineTo(x2, y2); g.stroke(); }
    function circle(x, y, rr, fill) { g.beginPath(); g.arc(x, y, rr, 0, Math.PI * 2); if (fill) g.fill(); else g.stroke(); }
    switch (kind) {
      case 'cpu': // a chip with pins
        h = u * 0.62; roundPath(cx - h, cy - h, 2 * h, 2 * h, 1.5 * s).trace(g); g.stroke();
        g.fillRect(cx - h * 0.4, cy - h * 0.4, h * 0.8, h * 0.8);
        for (i = -1; i <= 1; i++) {
          var o = i * h * 0.6;
          line(cx + o, cy - h, cx + o, cy - u); line(cx + o, cy + h, cx + o, cy + u);
          line(cx - h, cy + o, cx - u, cy + o); line(cx + h, cy + o, cx + u, cy + o);
        }
        break;
      case 'gpu': case 'igpu': // a graphics card with its fan; integrated: a smaller board
        w = kind === 'gpu' ? u * 0.95 : u * 0.8; h = kind === 'gpu' ? u * 0.62 : u * 0.55;
        roundPath(cx - w, cy - h, 2 * w, 2 * h, 1.5 * s).trace(g); g.stroke();
        r = h * 0.6;
        if (kind === 'gpu') { circle(cx + w * 0.3, cy, r); line(cx - w * 0.6, cy - h * 0.3, cx - w * 0.6, cy + h * 0.3); }
        else g.fillRect(cx - w * 0.35, cy - h * 0.35, w * 0.7, h * 0.7);
        break;
      case 'fps': // a gauge
        g.beginPath(); g.arc(cx, cy + u * 0.1, u * 0.9, 150 * Math.PI / 180, 390 * Math.PI / 180); g.stroke();
        line(cx, cy + u * 0.1, cx + u * 0.5, cy - u * 0.4);
        circle(cx, cy + u * 0.1, 1.5 * s, true);
        break;
      case 'ram': // a memory stick
        w = u * 0.95; h = u * 0.45; g.strokeRect(cx - w, cy - h, 2 * w, 2 * h);
        for (i = -1; i <= 1; i++) g.fillRect(cx + i * w * 0.55 - w * 0.15, cy - h * 0.5, w * 0.3, h);
        for (i = -2; i <= 2; i++) line(cx + i * w * 0.4, cy + h, cx + i * w * 0.4, cy + h + u * 0.3);
        break;
      case 'net': // down and up arrows
        var x1 = cx - u * 0.4, x2 = cx + u * 0.4, t = cy - u * 0.85, bt = cy + u * 0.85, a = u * 0.35;
        line(x1, t, x1, bt); g.beginPath(); g.moveTo(x1 - a, bt - a); g.lineTo(x1, bt); g.lineTo(x1 + a, bt - a); g.stroke();
        line(x2, bt, x2, t); g.beginPath(); g.moveTo(x2 - a, t + a); g.lineTo(x2, t); g.lineTo(x2 + a, t + a); g.stroke();
        break;
      case 'ping': // signal waves
        var by = cy + u * 0.7; circle(cx, by, 1.6 * s, true);
        for (i = 1; i <= 2; i++) { r = u * 0.55 * i; g.beginPath(); g.arc(cx, by, r, 225 * Math.PI / 180, 315 * Math.PI / 180); g.stroke(); }
        break;
      case 'disk': // a drive
        w = u * 0.9; h = u * 0.6; roundPath(cx - w, cy - h, 2 * w, 2 * h, 2 * s).trace(g); g.stroke();
        line(cx - w, cy + h * 0.15, cx + w, cy + h * 0.15); circle(cx + w * 0.5, cy + h * 0.55, 1.3 * s, true);
        break;
      case 'media': // a music note
        g.beginPath(); g.ellipse(cx - u * 0.45, cy + u * 0.55, u * 0.4, u * 0.3, 0, 0, Math.PI * 2); g.fill();
        g.fillRect(cx - u * 0.12, cy - u * 0.85, Math.max(1.4 * s, u * 0.16), u * 1.4);
        g.beginPath(); g.moveTo(cx - u * 0.1, cy - u * 0.85); g.lineTo(cx + u * 0.75, cy - u * 0.5); g.lineTo(cx + u * 0.75, cy - u * 0.2); g.lineTo(cx - u * 0.1, cy - u * 0.5); g.closePath(); g.fill();
        break;
    }
    g.restore();
  };
  function iconKind(id) { return id.indexOf('gpu:') === 0 ? (gpu(id).integrated ? 'igpu' : 'gpu') : id; }

  // ---- mini mode as a row: fixed-width slots, so the strip doesn't jitter as numbers change
  P.slot = function (x, y, v, b, t) {
    var tw = this.measure(t, this.fValue);
    if (v == null) return tw;
    this.txt(v, this.fValue, b, x, y);
    return Math.max(tw, this.measure(v, this.fValue));
  };
  P.seg = function (kind, label, accent, v1, b1, t1, v2, b2, t2) {
    var s = this.s, y = this.my;
    if (this.any) this.x += 14 * s;
    this.any = true;
    this.icon(kind, this.x + 6.5 * s, y + 8.5 * s, 13 * s, accent); this.x += 17 * s;
    this.txt(label, this.fLabel, this.th.label, this.x, y + 1.5 * s);
    this.x += this.measure(label, this.fLabel) + 6 * s;
    this.x += this.slot(this.x, y, v1, b1, t1);
    if (t2) { this.x += 6 * s; this.x += this.slot(this.x, y, v2, b2, t2); }
  };
  P.miniRow = function (ids) {
    var s = this.s, th = this.th; this.x = this.pad * 0.9; this.my = this.pad * 0.6; this.any = false; this.rects = {};
    for (var i = 0; i < ids.length; i++) {
      var id = ids[i], c = this.color(id), x0 = this.x;
      switch (id) {
        case 'cpu': this.seg('cpu', LABEL.cpu, c, pct(sim.cpu), th.value, '100%', tempText(sim.cpuTemp), this.tempBrush(sim.cpuTemp), '100°C'); break;
        case 'fps': this.seg('fps', LABEL.fps, c, String(Math.round(sim.fps)), th.value, '999'); break;
        case 'ram': this.seg('ram', LABEL.ram, c, sim.ram.toFixed(1) + ' GB', th.value, '99.9 GB'); break;
        case 'net': this.seg('net', LABEL.net, c, '↓ ' + rate(sim.down), th.value, '↓ 999 MB/s', '↑ ' + rate(sim.up), th.label, '↑ 999 MB/s'); break;
        case 'ping': this.seg('ping', LABEL.ping, c, Math.round(sim.ping) + ' ms', th.value, '999 ms'); break;
        case 'disk': this.seg('disk', LABEL.disk, c, 'R ' + rate(sim.diskR), th.value, 'R 999 MB/s', 'W ' + rate(sim.diskW), th.value, 'W 999 MB/s'); break;
        case 'media':
          if (this.any) this.x += 14 * s;
          this.any = true;
          this.icon('media', this.x + 6.5 * s, this.my + 8.5 * s, 13 * s, c); this.x += 17 * s;
          var mt = TRACKS[sim.track].title, mw = Math.min(150 * s, this.measure(mt, this.fValue));
          this.txt(this.fit(mt, this.fValue, mw + 1), this.fValue, th.value, this.x, this.my);
          this.x += mw;
          break;
        default:
          var gp = gpu(id);
          this.seg(iconKind(id), gp.label, c, pct(gp.load), th.value, '100%', tempText(gp.temp), this.tempBrush(gp.temp), '100°C');
      }
      this.rects[id] = { x: x0, y: 0, w: this.x - x0 + 7 * s, h: this.H };
    }
    return this.x;
  };

  // ---- mini mode as a column: a centred tile per stat (icon, value, second value)
  P.center = function (t, f, b, y) { this.txt(t, f, b, (this.W - this.measure(t, f)) / 2, y); };
  P.tile = function (icon, tag, accent, v1, b1, t1, v2, b2, t2) {
    var s = this.s, isz = 13 * s, tw = tag ? this.measure(tag, this.fLabel) + 3 * s : 0, x0 = (this.W - isz - tw) / 2;
    this.any = true; this.colW = Math.max(this.colW, isz + tw);
    this.icon(icon, x0 + isz / 2, this.y + 7 * s, isz, accent);
    if (tag) this.txt(tag, this.fLabel, this.th.label, x0 + isz + 3 * s, this.y + 0.5 * s);
    this.y += 16 * s;
    if (v1 != null || t1 != null) {
      if (t1) this.colW = Math.max(this.colW, this.measure(t1, this.fValue));
      if (v1 != null) this.center(this.fit(v1, this.fValue, this.W - 6 * s), this.fValue, b1, this.y);
      this.y += 16 * s;
    }
    if (v2 != null) {
      if (t2) this.colW = Math.max(this.colW, this.measure(t2, this.fSmall));
      this.center(this.fit(v2, this.fSmall, this.W - 6 * s), this.fSmall, b2, this.y);
      this.y += 14 * s;
    }
    this.y += 8 * s;
  };
  P.miniCol = function (ids) {
    var s = this.s, th = this.th; this.y = 9 * s; this.any = false; this.rects = {};
    for (var i = 0; i < ids.length; i++) {
      var id = ids[i], c = this.color(id), y0 = this.y;
      switch (id) {
        case 'cpu': this.tile('cpu', null, c, pct(sim.cpu), th.value, '100%', tempText(sim.cpuTemp), this.tempBrush(sim.cpuTemp), '100°C'); break;
        case 'fps': this.tile('fps', null, c, String(Math.round(sim.fps)), th.value, '999'); break;
        case 'ram': this.tile('ram', null, c, sim.ram.toFixed(1) + ' GB', th.value, '99.9 GB'); break;
        case 'net': this.tile('net', null, c, '↓ ' + rate(sim.down), th.value, '↓ 999 MB/s', '↑ ' + rate(sim.up), th.label, '↑ 999 MB/s'); break;
        case 'ping': this.tile('ping', null, c, Math.round(sim.ping) + ' ms', th.value, '999 ms'); break;
        case 'disk': this.tile('disk', 'C:', c, Math.round(sim.drives[0].free) + ' GB', th.value, '9999 GB'); break;
        case 'media':
          var t = TRACKS[sim.track], w = this.W - 12 * s;
          this.tile('media', null, c);
          this.y -= 8 * s;
          this.center(this.fit(t.title, this.fSmall, w), this.fSmall, th.value, this.y);
          this.center(this.fit(t.artist, this.fSmall, w), this.fSmall, th.dim, this.y + 14 * s);
          this.y += 28 * s + 8 * s;
          break;
        default:
          var gp = gpu(id);
          this.tile(iconKind(id), null, c, pct(gp.load), th.value, '100%', tempText(gp.temp), this.tempBrush(gp.temp), '100°C');
      }
      this.rects[id] = { x: 0, y: y0 - 4 * s, w: this.W, h: this.y - y0 };
    }
    return this.y - (this.any ? 8 * s : 0) + 9 * s;
  };

  // ------------------------------------------------------------------ album covers (drawn here, one per track)
  var arts = {};
  function albumArt(i) {
    if (arts[i]) return arts[i];
    var c = document.createElement('canvas'), g, hue = TRACKS[i].hue, k;
    c.width = c.height = 160; g = c.getContext('2d');
    var gr = g.createLinearGradient(0, 0, 160, 160);
    gr.addColorStop(0, 'hsl(' + hue + ',70%,22%)'); gr.addColorStop(1, 'hsl(' + (hue + 50) + ',80%,52%)');
    g.fillStyle = gr; g.fillRect(0, 0, 160, 160);
    g.globalAlpha = 0.85; g.fillStyle = 'hsl(' + (hue + 160) + ',90%,70%)';
    g.beginPath(); g.arc(110, 58, 26, 0, Math.PI * 2); g.fill();
    g.globalAlpha = 0.5; g.fillStyle = 'hsl(' + (hue + 20) + ',60%,12%)';
    for (k = 0; k < 4; k++) { g.beginPath(); g.moveTo(0, 112 + k * 12); g.bezierCurveTo(50, 92 + k * 12, 100, 132 + k * 12, 160, 104 + k * 12); g.lineTo(160, 160); g.lineTo(0, 160); g.closePath(); g.fill(); }
    return (arts[i] = c);
  }

  function hiDpiCanvas(c, w, h) {
    var dpr = window.devicePixelRatio || 1, pw = Math.round(w * dpr), ph = Math.round(h * dpr);
    if (c.width !== pw || c.height !== ph) { c.width = pw; c.height = ph; }
    c.style.width = w + 'px'; c.style.height = h + 'px';
    var g = c.getContext('2d'); g.setTransform(dpr, 0, 0, dpr, 0, 0);
    return g;
  }
  function inRect(r, x, y) { return !!r && x >= r.x && y >= r.y && x < r.x + r.w && y < r.y + r.h; }
  function canHover() { return !window.matchMedia || matchMedia('(hover: hover)').matches; }
  function mediaClick(i) {
    if (i === 1) sim.playing = !sim.playing;
    else { sim.track = (sim.track + (i === 0 ? TRACKS.length - 1 : 1)) % TRACKS.length; sim.pos = 0; }
    refreshAll();
  }

  // ------------------------------------------------------------------ the full panel (+ the busiest-apps popup)
  function FullDemo(host) {
    var self = this;
    this.host = host; this.theme = makeTheme(0); this.s = 1.25; this.btns = []; this.rects = {};
    this.ids = (host.getAttribute('data-sections') || 'cpu gpu:0 fps ram net disk media').split(' ');
    this.canvas = document.createElement('canvas'); this.canvas.className = 'sw-canvas';
    this.canvas.setAttribute('role', 'img'); this.canvas.setAttribute('aria-label', 'SysWidget panel, a live demo with sample numbers');
    host.innerHTML = ''; host.appendChild(this.canvas);
    this.pop = document.createElement('canvas'); this.pop.className = 'sw-popup'; this.pop.hidden = true; host.appendChild(this.pop);
    this.hoverId = null; this.hoverMs = 0; this.mouse = null;
    this.canvas.addEventListener('pointermove', function (e) { self.mouse = self.local(e); self.cursor(); });
    this.canvas.addEventListener('pointerleave', function () { self.mouse = null; self.hoverId = null; self.hidePopup(); self.canvas.style.cursor = ''; });
    this.canvas.addEventListener('click', function (e) {
      var m = self.local(e);
      for (var i = 0; i < self.btns.length; i++) if (inRect(self.btns[i], m[0], m[1])) { mediaClick(i); return; }
      if (!canHover()) { var id = self.idAt(m[0], m[1]); if (id === 'cpu' || id === 'ram') { self.hoverId = id; self.showPopup(id); } else self.hidePopup(); }
    });
    setInterval(function () { self.hover(); }, 40);
    this.render();
  }
  var F = FullDemo.prototype;
  F.local = function (e) { var r = this.canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  F.setTheme = function (k) { this.theme = makeTheme(k); this.refresh(); };
  F.idAt = function (x, y) { for (var id in this.rects) if (inRect(this.rects[id], x, y)) return id; return null; };
  F.cursor = function () {
    var m = this.mouse, over = false;
    if (m) for (var i = 0; i < this.btns.length; i++) if (inRect(this.btns[i], m[0], m[1])) over = true;
    this.canvas.style.cursor = over ? 'pointer' : '';
  };
  F.layout = function (p) {
    var s = p.s, x = p.pad, w = p.W - 2 * p.pad, gh = 26 * s, sgap = 11 * s, gap = 0; p.y = p.pad; p.rects = {};
    for (var i = 0; i < this.ids.length; i++) {
      var top = p.y; gap = p.section(this.ids[i], x, w, gh, sgap);
      p.rects[this.ids[i]] = { x: 0, y: top - sgap / 2, w: p.W, h: p.y - top + sgap };
      p.y += gap;
    }
    return p.y - gap + p.pad;
  };
  F.render = function () {
    var th = this.theme, p = new Painter(this.canvas.getContext('2d'), th, this.s); p.draw = false;
    var H = Math.ceil(this.layout(p)), W = p.W, g = hiDpiCanvas(this.canvas, W, H);
    g.clearRect(0, 0, W, H);
    p = new Painter(g, th, this.s);
    var d = 20 * this.s, R = W - 0.5, B = H - 0.5;
    var panel = new Path().arc(0.5, 0.5, d, d, 180, 90).arc(R - d, 0.5, d, d, 270, 90).arc(R - d, B - d, d, d, 0, 90).arc(0.5, B - d, d, d, 90, 90).close();
    panel.trace(g); g.fillStyle = css(th.bg); g.fill(); g.strokeStyle = th.inkA(34); g.lineWidth = 1; g.stroke();
    this.layout(p);
    this.rects = p.rects; this.btns = p.mediaBtns;
  };
  // Like the app: point at CPU or RAM for a moment and the busiest apps show next to the panel.
  F.hover = function () {
    if (!this.mouse) return;
    var id = this.idAt(this.mouse[0], this.mouse[1]);
    if (id !== 'cpu' && id !== 'ram') id = null;
    if (id !== this.hoverId) { this.hoverId = id; this.hoverMs = 0; this.hidePopup(); }
    if (!id) return;
    this.hoverMs += 40;
    var t = this.hoverMs - 450;
    if (t >= 0 && t < 40) this.showPopup(id);
  };
  F.showPopup = function (id) {
    if (!id) return;
    var d = 1.25, S = function (v) { return Math.round(v * d); }, isCpu = id === 'cpu', list = isCpu ? sim.apps.cpu : sim.apps.mem, i;
    var w = S(240), h = S(10) + S(22) + Math.max(1, list.length) * S(27) + S(6), g = hiDpiCanvas(this.pop, w, h);
    var base = this.theme.light ? rgb(18, 19, 22) : this.theme.bg; // menus and popups stay dark with the light panel
    this.pop.style.borderColor = css(mix(base, rgb(255, 255, 255), 0.15));
    g.fillStyle = css(mix(base, rgb(255, 255, 255), 0.015)); g.fillRect(0, 0, w, h);
    var x = S(12), cw = w - 2 * x, y = S(10), max = 0, dim = 'rgb(150,152,160)';
    g.textBaseline = 'top'; g.font = '600 ' + (11 * d).toFixed(2) + 'px ' + FONT; g.fillStyle = dim;
    g.fillText((isCpu ? 'Using the most CPU' : 'Using the most memory').toUpperCase(), x, y);
    y += S(22);
    for (i = 0; i < list.length; i++) max = Math.max(max, list[i][1]);
    g.font = (12 * d).toFixed(2) + 'px ' + FONT;
    for (i = 0; i < list.length; i++) {
      var v = list[i][1], val = isCpu ? v.toFixed(v < 10 ? 1 : 0) + '%' : bytes(v), vw = g.measureText(val).width;
      g.fillStyle = 'rgb(232,233,236)'; g.fillText(list[i][0], x, y); g.fillStyle = dim; g.fillText(val, x + cw - vw, y);
      g.fillStyle = 'rgba(255,255,255,0.086)'; g.fillRect(x, y + S(19), cw, S(3));
      g.fillStyle = css(COL[id]); g.fillRect(x, y + S(19), Math.max(S(2), cw * (max > 0 ? v / max : 0)), S(3));
      y += S(27);
    }
    // next to the panel, level with the section: on the right if the window has room, else on the left
    var r = this.rects[id], box = this.canvas.getBoundingClientRect(), gap = S(8);
    var right = box.right + gap + w <= document.documentElement.clientWidth - 8;
    this.pop.style.left = (right ? this.canvas.offsetLeft + this.canvas.offsetWidth + gap : this.canvas.offsetLeft - gap - w) + 'px';
    this.pop.style.top = (this.canvas.offsetTop + Math.max(0, r.y)) + 'px';
    this.pop.hidden = false;
  };
  F.hidePopup = function () { this.pop.hidden = true; };
  F.refresh = function () { this.render(); if (!this.pop.hidden) this.showPopup(this.hoverId); this.cursor(); };

  // ------------------------------------------------------------------ the island (mini mode): a row or a column
  var SPRING = 30, FADE = 130;
  function IslandDemo(host) {
    var self = this;
    this.host = host; this.vertical = host.getAttribute('data-demo') === 'column';
    this.allIds = (host.getAttribute('data-sections') || 'cpu gpu:0 fps ram net ping media').split(' ');
    this.theme = makeTheme(0);
    this.canvas = document.createElement('canvas'); this.canvas.className = 'sw-canvas sw-island';
    this.canvas.setAttribute('role', 'img');
    this.canvas.setAttribute('aria-label', 'SysWidget mini mode, a live demo with sample numbers: point at a stat to open its card');
    host.appendChild(this.canvas);
    this.open = false; this.id = null; this.curH = 0; this.targetH = 0; this.a = 0; this.aT = 0; this.L = 0; this.segLen = 0;
    this.vH = 0; this.vA = 0; this.vL = 0; this.fresh = false; this.switching = false; this.prev = null; this.fadeAt = 0;
    this.content = null; this.hoverId = null; this.hoverMs = 0; this.leaveMs = 0; this.auto = false; this.autoUntil = 0;
    this.mouse = null; this.anim = false; this.lastSong = sim.songChanged; this.visible = true; this.cAt = [0, 0]; this.rects = {};
    this.canvas.addEventListener('pointermove', function (e) { self.mouse = self.local(e); self.cursor(); });
    this.canvas.addEventListener('pointerdown', function (e) {
      self.mouse = self.local(e);
      if (e.pointerType !== 'mouse') { // touch: tap a stat to open it right away
        var id = self.segAt(self.mouse[0], self.mouse[1]);
        if (id) { self.auto = false; if (id !== self.id || !self.open || self.targetH === 0) self.openCard(id); }
      }
    });
    this.canvas.addEventListener('click', function (e) { var m = self.local(e), b = self.buttonAt(m[0], m[1]); if (b >= 0) mediaClick(b); });
    this.canvas.addEventListener('pointerleave', function (e) { if (e.pointerType === 'mouse') self.mouse = null; });
    document.addEventListener('pointerdown', function (e) { if (!self.host.contains(e.target)) self.mouse = null; });
    setInterval(function () { self.hover(); }, 40);
    var rw = 0;
    window.addEventListener('resize', function () { if (self.host.clientWidth !== rw) { rw = self.host.clientWidth; self.layout(); self.render(); } });
    this.layout(); this.render();
  }
  var I = IslandDemo.prototype;
  I.local = function (e) { var r = this.canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  // Sizes the strip for the stage (a smaller scale, then fewer stats, when it's narrow) and docks it to its edge.
  I.layout = function () {
    var sw = this.host.clientWidth, sh = this.host.clientHeight, g = this.canvas.getContext('2d'), ids = this.allIds.slice(), s = 1.25, W, H;
    this.sw = sw; this.sh = sh;
    for (;;) {
      var p = new Painter(g, this.theme, s); p.draw = false;
      if (this.vertical) { p.W = 100; p.miniCol(ids); W = Math.ceil(Math.max(46 * s, p.colW + 16 * s)); p.W = W; H = Math.ceil(p.miniCol(ids)); }
      else { H = Math.ceil(p.pad * 1.2 + 17 * s); p.H = H; W = Math.ceil(p.miniRow(ids) + p.pad * 0.9); }
      // the column needs room beside it for a card and must fit the height; the row must fit the width
      var wideOk = this.vertical ? W + Math.round(300 * s) + 24 <= sw : W <= sw - 16, highOk = !this.vertical || H <= sh;
      if (wideOk && highOk) break;
      if (!wideOk && s > 0.84) { s -= 0.05; continue; }
      if (wideOk === false && this.vertical) break; // a narrow phone: the card may overhang, that's fine
      var drop = ['ping', 'fps', 'gpu:0', 'net'].filter(function (x) { return ids.indexOf(x) >= 0; })[0];
      if (!drop) break;
      ids.splice(ids.indexOf(drop), 1);
    }
    this.s = s; this.ids = ids; this.cw = Math.round(300 * s);
    this.home = this.vertical ? { x: sw - W, y: Math.round((sh - H) / 2), w: W, h: H } : { x: Math.round((sw - W) / 2), y: 0, w: W, h: H };
    this.dir = this.vertical ? 3 : 0; // the row is docked to the top (cards open down), the column to the right (they open left)
    hiDpiCanvas(this.canvas, sw, sh);
    if (this.open) this.closeNow();
  };
  // ---- geometry in strip coordinates (a = along the strip, b = across it, the card growing toward +b)
  I.vert = function () { return this.dir >= 2; };
  I.stripLen = function () { return this.vert() ? this.home.h : this.home.w; };
  I.thick = function () { return this.vert() ? this.home.w : this.home.h; };
  I.rad = function () { return this.vert() ? Math.min(14 * this.s, this.home.w / 2) : Math.max(10 * this.s, this.home.h / 2); };
  I.fillet = function () { return 8 * this.s; };
  I.along = function () { return this.vert() ? (this.content ? this.content.h : 0) : this.cw; };
  I.depth = function () { return this.vert() ? this.cw : (this.content ? this.content.h : 0); };
  I.fits = function () { return this.stripLen() >= this.along() + 2 * (this.rad() + this.fillet()); };
  I.matrix = function () {
    var h = this.home;
    switch (this.dir) {
      case 1: return [1, 0, 0, -1, h.x, h.y + h.h];
      case 2: return [0, 1, 1, 0, h.x, h.y];
      case 3: return [0, 1, -1, 0, h.x + h.w, h.y];
      default: return [1, 0, 0, 1, h.x, h.y];
    }
  };
  I.map = function (r) {
    var m = this.matrix(), xs = [], ys = [];
    [[r.x, r.y], [r.x + r.w, r.y], [r.x, r.y + r.h], [r.x + r.w, r.y + r.h]].forEach(function (q) {
      xs.push(m[0] * q[0] + m[2] * q[1] + m[4]); ys.push(m[1] * q[0] + m[3] * q[1] + m[5]);
    });
    var x0 = Math.min.apply(null, xs), y0 = Math.min.apply(null, ys);
    return { x: x0, y: y0, w: Math.max.apply(null, xs) - x0, h: Math.max.apply(null, ys) - y0 };
  };
  I.aTarget = function (id) {
    var seg = this.rects[id], len = this.stripLen(), w = this.along();
    var c = seg ? (this.vert() ? seg.y + seg.h / 2 : seg.x + seg.w / 2) : len / 2, lim = this.rad() + this.fillet() + w / 2;
    if (lim * 2 <= len) return clamp(c, lim, len - lim);
    var lo = (this.vert() ? -this.home.y : -this.home.x) + w / 2, hi = (this.vert() ? this.sh - this.home.y : this.sw - this.home.x) - w / 2;
    return clamp(c, lo, hi); // overhanging: keep the card on the "screen"
  };
  I.cardCanon = function (a, depth) {
    var along = this.L > 0 ? this.L : this.along(), full = this.depth(), frac = full > 0 ? Math.min(1, depth / full) : 0;
    var w0 = Math.min(along, Math.max(this.segLen, 40 * this.s)), w = w0 + (along - w0) * frac;
    return { x: a - w / 2, y: this.thick() - 1, w: w, h: depth };
  };
  I.over = function (x, y) {
    if (inRect(this.home, x, y)) return true;
    return this.open && this.curH > 0.5 && inRect(this.map(this.cardCanon(this.a, this.curH)), x, y);
  };
  I.segAt = function (x, y) {
    if (!inRect(this.home, x, y)) return null;
    for (var id in this.rects) if (inRect(this.rects[id], x - this.home.x, y - this.home.y)) return id;
    return null;
  };
  I.buttonAt = function (x, y) {
    if (!this.open || this.id !== 'media' || !this.content || this.curH < this.depth() - 1) return -1;
    for (var i = 0; i < this.content.btns.length; i++) if (inRect(this.content.btns[i], x - this.cAt[0], y - this.cAt[1])) return i;
    return -1;
  };
  I.cursor = function () { var m = this.mouse; this.canvas.style.cursor = m && this.buttonAt(m[0], m[1]) >= 0 ? 'pointer' : ''; };

  // ---- the card's picture (drawn once a second, or when it changes)
  I.build = function () {
    var p = new Painter(this.canvas.getContext('2d'), this.theme, this.s); p.draw = false;
    var h = Math.max(1, Math.ceil(p.card(this.id, this.cw))), c = document.createElement('canvas'), g = hiDpiCanvas(c, this.cw, h);
    g.fillStyle = css(this.theme.bg); g.fillRect(0, 0, this.cw, h);
    p = new Painter(g, this.theme, this.s); p.card(this.id, this.cw);
    var oldH = this.content ? this.content.h : -1;
    this.content = { c: c, w: this.cw, h: h, btns: p.cardBtns };
    if (this.open && this.targetH > 0 && h !== oldH) { this.targetH = this.depth(); this.aT = this.aTarget(this.id); this.start(); }
  };
  // ---- open / switch / close: the app's logic and timings
  I.openCard = function (id) {
    var first = !this.open;
    if (first) { this.open = true; this.curH = 0; var seg = this.rects[id]; this.segLen = seg ? (this.vert() ? seg.h : seg.w) : 0; }
    this.switching = this.curH > 0.5;
    if (this.switching && id !== this.id && this.content) { this.prev = this.content; this.content = null; this.fadeAt = performance.now(); }
    this.id = id;
    this.targetH = -1; this.build();
    this.targetH = this.depth(); this.aT = this.aTarget(id);
    if (first || this.curH < 0.5) { this.a = this.aT; this.vA = 0; this.L = this.along(); this.vL = 0; this.fresh = true; } else this.fresh = false;
    if (reduceMotion) { this.curH = this.targetH; this.a = this.aT; this.L = this.along(); this.prev = null; this.fresh = false; this.render(); return; }
    this.start();
  };
  I.close = function () {
    if (!this.open || this.targetH === 0) return;
    this.switching = false; this.auto = false; this.targetH = 0;
    if (reduceMotion) this.closeNow(); else this.start();
  };
  I.closeNow = function () {
    this.open = false; this.id = null; this.auto = false; this.curH = this.targetH = 0; this.vH = this.vA = this.vL = this.L = 0;
    this.fresh = false; this.content = null; this.prev = null; this.hoverId = null; this.hoverMs = 0; this.anim = false;
    this.render();
  };
  I.fade = function () {
    if (!this.prev) return 1;
    var k = (performance.now() - this.fadeAt) / FADE;
    if (k < 1) return Math.max(0, k * k * (3 - 2 * k));
    this.prev = null; return 1;
  };
  I.start = function () {
    if (this.anim) return;
    var self = this; this.anim = true; this.last = performance.now();
    requestAnimationFrame(function step(now) { if (self.anim && self.stepAnim(now)) requestAnimationFrame(step); });
  };
  // A critically damped spring on the clock: soft start and stop, no overshoot, keeps its speed when retargeted.
  I.stepAnim = function (now) {
    var dt = Math.min(50, Math.max(0, now - this.last)) / 1000, A = this.along(), n = Math.max(1, Math.ceil(dt / 0.004)), h = dt / n;
    this.last = now;
    for (var i = 0; i < n; i++) {
      this.vH += (-SPRING * SPRING * (this.curH - this.targetH) - 2 * SPRING * this.vH) * h; this.curH += this.vH * h;
      this.vA += (-SPRING * SPRING * (this.a - this.aT) - 2 * SPRING * this.vA) * h; this.a += this.vA * h;
      this.vL += (-SPRING * SPRING * (this.L - A) - 2 * SPRING * this.vL) * h; this.L += this.vL * h;
    }
    if (this.targetH === 0 && this.curH < 0) { this.curH = 0; this.vH = 0; }
    var settled = Math.abs(this.targetH - this.curH) < 0.5 && Math.abs(this.aT - this.a) < 0.5 && Math.abs(A - this.L) < 0.5 &&
      Math.abs(this.vH) < 30 && Math.abs(this.vA) < 30 && Math.abs(this.vL) < 30;
    if (settled) {
      this.curH = this.targetH; this.a = this.aT; this.L = A; this.vH = this.vA = this.vL = 0; this.fresh = false;
      if (this.targetH === 0) { this.closeNow(); return false; }
      if (!this.prev || this.fade() >= 1) { this.anim = false; this.switching = false; this.render(); return false; }
    }
    this.render();
    return true;
  };
  // ---- the mouse: point at a stat to open it; leave the island to close it
  I.hover = function () {
    var m = this.mouse;
    if (!m || !this.over(m[0], m[1])) {
      if (this.open && !this.auto) { this.leaveMs += 40; if (this.leaveMs >= 300) this.close(); }
      this.hoverId = null; this.hoverMs = 0;
      return;
    }
    this.leaveMs = 0;
    var id = this.segAt(m[0], m[1]);
    if (!id) { if (!this.open) { this.hoverId = null; this.hoverMs = 0; } return; }
    if (this.open) { this.auto = false; if (id !== this.id || this.targetH === 0) this.openCard(id); return; }
    if (id !== this.hoverId) { this.hoverId = id; this.hoverMs = 0; }
    this.hoverMs += 40;
    if (this.hoverMs >= 250) this.openCard(id);
  };
  I.refresh = function () {
    if (this.open && this.targetH !== 0) this.build();
    if (this.auto && performance.now() > this.autoUntil && !(this.mouse && this.over(this.mouse[0], this.mouse[1]))) this.close();
    // A new song: the media card pops up for a few seconds (the app's "pop up when the song changes").
    if (sim.songChanged !== this.lastSong) {
      this.lastSong = sim.songChanged;
      if (!this.open && this.visible && this.ids.indexOf('media') >= 0) { this.openCard('media'); this.auto = true; this.autoUntil = performance.now() + 4000; }
    }
    if (!this.anim) this.render();
    this.cursor();
  };

  // ---- drawing: the strip stays; the card hangs from the open stat, joined by two small inward curves
  I.render = function () {
    var g = this.canvas.getContext('2d'), dpr = window.devicePixelRatio || 1, th = this.theme, s = this.s, self = this;
    var home = this.home, strip = home, sw = this.sw, sh = this.sh;
    g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, sw, sh);
    var dl = home.x <= 0, dt = home.y <= 0, dr = home.x + home.w >= sw, db = home.y + home.h >= sh;
    var cc = this.cardCanon(this.a, this.curH), showCard = this.open && this.curH > 0.5 && !!this.content, overhang = !this.fits();
    var f = overhang ? 0 : Math.min(this.fillet(), cc.h / 2), cRad = Math.min(14 * s, Math.min(cc.h / 2, cc.w / 2)), jb = this.thick() - 1;
    var m = this.matrix(), bg = css(th.bg), L = cc.x, R = cc.x + cc.w;
    function withM(fn) { g.setTransform(dpr * m[0], dpr * m[1], dpr * m[2], dpr * m[3], dpr * m[4], dpr * m[5]); fn(); g.setTransform(dpr, 0, 0, dpr, 0, 0); }
    var sp = shapePath(strip, this.rad(), 10 * s, dl, dt, dr, db);
    var cp = cardPath(cc, cRad, false, 0, overhang), outline = cardPath(cc, cRad, true, f, overhang);
    sp.trace(g); g.fillStyle = bg; g.fill();
    if (showCard) {
      withM(function () {
        cp.trace(g); g.fillStyle = bg; g.fill();
        if (f > 0.5) {
          var fl = new Path().line(L, jb, L - f, jb).arc(L - 2 * f, jb, 2 * f, 2 * f, 270, 90).close()
            .figure().line(R, jb, R + f, jb).arc(R, jb, 2 * f, 2 * f, 270, -90).close();
          fl.trace(g); g.fill();
        }
      });
      g.save();
      withM(function () { cp.trace(g); }); g.clip();
      var alpha = this.fresh || this.targetH === 0 ? Math.min(1, this.curH / Math.max(1, this.depth())) : 1, k = this.fade();
      var place = function (c) {
        switch (self.dir) {
          case 1: return [strip.x + self.a - c.w / 2, strip.y + 1 - c.h];
          case 2: return [strip.x + strip.w - 1, strip.y + self.a - c.h / 2];
          case 3: return [strip.x + 1 - c.w, strip.y + self.a - c.h / 2];
          default: return [strip.x + self.a - c.w / 2, strip.y + strip.h - 1];
        }
      };
      var at = place(this.content); this.cAt = [Math.round(at[0]), Math.round(at[1])];
      if (this.prev) { var pa = place(this.prev); g.globalAlpha = alpha * (1 - k); g.drawImage(this.prev.c, Math.round(pa[0]), Math.round(pa[1]), this.prev.w, this.prev.h); }
      g.globalAlpha = alpha * k; g.drawImage(this.content.c, this.cAt[0], this.cAt[1], this.content.w, this.content.h);
      g.globalAlpha = 1; g.restore();
    }
    // the strip's stats, with the open one highlighted
    var p = new Painter(g, th, s); p.W = home.w; p.H = home.h;
    var seg = this.id ? this.rects[this.id] : null;
    if (seg && this.open && this.curH > 0.5) {
      var hr = this.vert()
        ? { x: strip.x + 4 * s, y: strip.y + seg.y + s, w: strip.w - 8 * s, h: seg.h - 2 * s }
        : { x: strip.x + seg.x - 2 * s, y: strip.y + 4 * s, w: seg.w - 3 * s, h: strip.h - 8 * s };
      p.fill(roundPath(hr.x, hr.y, hr.w, hr.h, this.vert() ? Math.min(8 * s, hr.h / 2) : hr.h / 2), th.inkA(th.light ? 14 : 20));
    }
    g.save(); g.translate(strip.x, strip.y);
    if (this.vertical) p.miniCol(this.ids); else p.miniRow(this.ids);
    g.restore();
    this.rects = p.rects;
    // outline: the strip's (minus the edge it's docked to and where the card joins it), then the card's
    g.strokeStyle = th.inkA(34); g.lineWidth = 1;
    g.save(); g.beginPath(); g.rect(-10, -10, sw + 20, sh + 20);
    if (dt) g.rect(strip.x - 20, strip.y - 20, strip.w + 40, 21);
    if (db) g.rect(strip.x - 20, strip.y + strip.h - 1, strip.w + 40, 21);
    if (dl) g.rect(strip.x - 20, strip.y - 20, 21, strip.h + 40);
    if (dr) g.rect(strip.x + strip.w - 1, strip.y - 20, 21, strip.h + 40);
    if (showCard) { var band = this.map({ x: L - f, y: jb - 2, w: R - L + 2 * f, h: 4 }); g.rect(band.x, band.y, band.w, band.h); }
    g.clip('evenodd');
    sp.trace(g); g.stroke();
    g.restore();
    if (showCard) withM(function () {
      outline.trace(g); g.stroke();
      if (f > 0.5) { new Path().arc(L - 2 * f, jb, 2 * f, 2 * f, 270, 90).figure().arc(R, jb, 2 * f, 2 * f, 180, 90).trace(g); g.stroke(); }
    });
  };

  // ------------------------------------------------------------------ start
  var demos = [];
  function refreshAll() { demos.forEach(function (d) { d.refresh(); }); }
  function boot() {
    var nodes = document.querySelectorAll('[data-demo]');
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i], kind = n.getAttribute('data-demo'), d;
      try { d = kind === 'full' ? new FullDemo(n.querySelector('.sw-stage')) : new IslandDemo(n); }
      catch (e) { if (window.console) console.error(e); continue; } // the picture stays
      n.classList.add('live'); demos.push(d);
      if (kind === 'full') hookThemes(n, d);
      if ('IntersectionObserver' in window) (function (d, n) {
        new IntersectionObserver(function (es) { d.visible = es[0].isIntersecting; }).observe(n);
      })(d, n);
    }
    setInterval(function () {
      if (document.hidden || !demos.some(function (d) { return d.visible !== false; })) return;
      tick(); refreshAll();
    }, 1000);
  }
  function hookThemes(n, d) {
    var chips = n.querySelectorAll('[data-theme-pick]');
    Array.prototype.forEach.call(chips, function (chip) {
      chip.addEventListener('click', function () {
        Array.prototype.forEach.call(chips, function (c) { c.setAttribute('aria-pressed', c === chip ? 'true' : 'false'); });
        d.setTheme(+chip.getAttribute('data-theme-pick'));
      });
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
