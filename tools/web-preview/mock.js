// Procedural stand-ins for things only Windows can provide in the real app:
// the desktop behind the overlay, live window captures and app icons.
// They exist so the real display lists can be reviewed visually on any machine.

const TAU = Math.PI * 2;

function canvas(w, h) {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(w));
  c.height = Math.max(1, Math.round(h));
  return c;
}

function rr(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, r);
}

function fill(ctx, color, x, y, w, h, r = 0) {
  ctx.fillStyle = color;
  if (r) { rr(ctx, x, y, w, h, r); ctx.fill(); } else ctx.fillRect(x, y, w, h);
}

function lines(ctx, x, y, w, count, gap, colors, rand, height = 6) {
  for (let i = 0; i < count; i++) {
    const lw = w * (0.35 + 0.6 * rand());
    fill(ctx, colors[i % colors.length], x, y + i * gap, lw, height, height / 2);
  }
}

function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

const FONT = 'Inter, "Segoe UI", system-ui, sans-serif';

// ─────────────────────────────── desktop ───────────────────────────────

export function paintDesktop(w, h) {
  const c = canvas(w, h);
  const ctx = c.getContext('2d');
  // Windows 11 "bloom"-like wallpaper.
  const g = ctx.createLinearGradient(0, 0, w, h);
  g.addColorStop(0, '#0b2b6b');
  g.addColorStop(0.5, '#1d5fd1');
  g.addColorStop(1, '#0a1f52');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, w, h);
  for (let i = 0; i < 7; i++) {
    const a = (i / 7) * TAU;
    const rg = ctx.createRadialGradient(w * 0.5 + Math.cos(a) * w * 0.08, h * 0.55 + Math.sin(a) * h * 0.1, 0,
      w * 0.5, h * 0.55, w * 0.32);
    rg.addColorStop(0, 'rgba(120,190,255,0.35)');
    rg.addColorStop(1, 'rgba(120,190,255,0)');
    ctx.fillStyle = rg;
    ctx.fillRect(0, 0, w, h);
  }
  // The foreground window (VS Code) and a second window behind it.
  const s = w / 2560;
  paintWindowFrame(ctx, 1320 * s, 180 * s, 1080 * s, 760 * s, 'chrome', s);
  paintWindowFrame(ctx, 160 * s, 110 * s, 1800 * s, 1110 * s, 'code', s);
  // Taskbar.
  fill(ctx, 'rgba(28,32,40,0.92)', 0, h - 72 * s, w, 72 * s);
  const icons = ['start', 'explorer', 'chrome', 'code', 'discord', 'spotify', 'telegram', 'figma', 'terminal'];
  const total = icons.length * 58 * s;
  icons.forEach((id, i) => {
    const x = w / 2 - total / 2 + i * 58 * s + 8 * s;
    const ic = id === 'start' ? startIcon(64) : paintIcon(id, 64);
    ctx.drawImage(ic, x, h - 60 * s, 44 * s, 44 * s);
  });
  ctx.fillStyle = 'rgba(255,255,255,0.85)';
  ctx.font = `500 ${20 * s}px ${FONT}`;
  ctx.textAlign = 'right';
  ctx.fillText('14:32', w - 40 * s, h - 40 * s);
  return c;
}

function startIcon(size) {
  const c = canvas(size, size);
  const ctx = c.getContext('2d');
  const q = size * 0.2, g = size * 0.04;
  ['#4cc2ff', '#4cc2ff', '#4cc2ff', '#4cc2ff'].forEach((col, i) => {
    fill(ctx, col, q + (i % 2) * (size * 0.3 + g), q + Math.floor(i / 2) * (size * 0.3 + g), size * 0.28, size * 0.28, 2);
  });
  return c;
}

function paintWindowFrame(ctx, x, y, w, h, kind, s) {
  ctx.save();
  ctx.shadowColor = 'rgba(0,0,0,0.45)';
  ctx.shadowBlur = 60 * s;
  ctx.shadowOffsetY = 20 * s;
  fill(ctx, '#202020', x, y, w, h, 10 * s);
  ctx.restore();
  const content = paintApp(kind, Math.round(w), Math.round(h), 7);
  ctx.save();
  rr(ctx, x, y, w, h, 10 * s);
  ctx.clip();
  ctx.drawImage(content, x, y, w, h);
  ctx.restore();
}

// ─────────────────────────────── app previews ───────────────────────────────

export function appKind(exe) {
  const e = exe.toLowerCase().replace('.exe', '');
  const map = {
    code: 'code', discord: 'discord', chrome: 'chrome', spotify: 'spotify', telegram: 'telegram',
    explorer: 'explorer', figma: 'figma', windowsterminal: 'terminal', steam: 'steam', photoshop: 'photoshop',
    notepad: 'notepad', excel: 'excel', obsidian: 'obsidian',
  };
  return map[e] ?? 'generic';
}

export function paintApp(kind, w, h, seed = 1, title = '') {
  const c = canvas(w, h);
  const ctx = c.getContext('2d');
  const r = rng(seed * 7919 + w);
  const u = w / 1280;
  const P = { code: paintCode, discord: paintDiscord, chrome: paintChrome, spotify: paintSpotify, telegram: paintTelegram,
    explorer: paintExplorer, figma: paintFigma, terminal: paintTerminal, steam: paintSteam, photoshop: paintPhotoshop,
    notepad: paintNotepad, excel: paintExcel, obsidian: paintObsidian }[kind] ?? paintGeneric;
  P(ctx, w, h, u, r, title);
  return c;
}

function titleBar(ctx, w, u, bg, text, color = '#ddd') {
  fill(ctx, bg, 0, 0, w, 34 * u);
  ctx.fillStyle = color;
  ctx.font = `400 ${13 * u}px ${FONT}`;
  ctx.textAlign = 'left';
  ctx.fillText(text, 16 * u, 22 * u);
  ctx.fillStyle = color;
  ['—', '▢', '✕'].forEach((g, i) => ctx.fillText(g, w - (130 - i * 45) * u, 22 * u));
}

function paintCode(ctx, w, h, u, r) {
  fill(ctx, '#1f1f1f', 0, 0, w, h);
  titleBar(ctx, w, u, '#181818', 'SceneComposer.cs — FlowSwitch — Visual Studio Code');
  fill(ctx, '#181818', 0, 34 * u, 52 * u, h);
  fill(ctx, '#1b1b1b', 52 * u, 34 * u, 250 * u, h);
  for (let i = 0; i < 6; i++) fill(ctx, i === 0 ? '#d7d7d7' : '#6e6e6e', 14 * u, (60 + i * 52) * u, 24 * u, 24 * u, 5 * u);
  lines(ctx, 72 * u, 70 * u, 190 * u, 22, 26 * u, ['#9d9d9d', '#7a7a7a', '#c5c5c5'], r, 8 * u);
  fill(ctx, '#1f1f1f', 302 * u, 34 * u, w, 38 * u);
  fill(ctx, '#2b2b2b', 302 * u, 34 * u, 190 * u, 38 * u);
  const palette = ['#569cd6', '#4ec9b0', '#ce9178', '#dcdcaa', '#9cdcfe', '#c586c0', '#6a9955', '#d4d4d4'];
  for (let i = 0; i < 34; i++) {
    const y = (96 + i * 25) * u;
    ctx.fillStyle = '#5a5a5a';
    ctx.font = `400 ${12 * u}px ${FONT}`;
    ctx.fillText(String(i + 1), 318 * u, y + 9 * u);
    let x = (360 + (i % 7 === 0 ? 0 : 24 + (i % 3) * 24)) * u;
    const tokens = 2 + Math.floor(r() * 5);
    for (let t = 0; t < tokens; t++) {
      const tw = (30 + r() * 110) * u;
      fill(ctx, palette[Math.floor(r() * palette.length)], x, y, tw, 9 * u, 4 * u);
      x += tw + 10 * u;
    }
  }
  fill(ctx, '#0078d4', 0, h - 24 * u, w, 24 * u);
}

function paintDiscord(ctx, w, h, u, r) {
  fill(ctx, '#313338', 0, 0, w, h);
  fill(ctx, '#1e1f22', 0, 0, 72 * u, h);
  for (let i = 0; i < 9; i++) {
    const cols = ['#5865f2', '#23a55a', '#f0b232', '#eb459e', '#3ba55c', '#5865f2', '#9b84ee', '#f47b67', '#45ddc0'];
    ctx.fillStyle = cols[i];
    ctx.beginPath();
    ctx.arc(36 * u, (40 + i * 60) * u, 22 * u, 0, TAU);
    ctx.fill();
  }
  fill(ctx, '#2b2d31', 72 * u, 0, 240 * u, h);
  ctx.fillStyle = '#f2f3f5';
  ctx.font = `600 ${16 * u}px ${FONT}`;
  ctx.fillText('FlowSwitch', 90 * u, 32 * u);
  lines(ctx, 96 * u, 70 * u, 170 * u, 14, 34 * u, ['#80848e', '#b5bac1'], r, 10 * u);
  fill(ctx, '#313338', 312 * u, 0, w, 48 * u);
  ctx.fillStyle = '#f2f3f5';
  ctx.fillText('#  design', 330 * u, 30 * u);
  for (let i = 0; i < 8; i++) {
    const y = (80 + i * 76) * u;
    const cols = ['#5865f2', '#eb459e', '#23a55a', '#f0b232'];
    ctx.fillStyle = cols[i % 4];
    ctx.beginPath(); ctx.arc(356 * u, y + 18 * u, 20 * u, 0, TAU); ctx.fill();
    fill(ctx, '#f2f3f5', 392 * u, y + 2 * u, (90 + r() * 60) * u, 10 * u, 5 * u);
    fill(ctx, '#b5bac1', 392 * u, y + 22 * u, (300 + r() * 450) * u, 9 * u, 4 * u);
    if (i === 3) fill(ctx, '#4e5058', 392 * u, y + 40 * u, 320 * u, 180 * u * 0.2, 8 * u);
  }
  fill(ctx, '#383a40', 330 * u, h - 70 * u, w - 350 * u, 46 * u, 10 * u);
}

function paintChrome(ctx, w, h, u, r) {
  fill(ctx, '#0f0f0f', 0, 0, w, h);
  fill(ctx, '#202124', 0, 0, w, 40 * u);
  fill(ctx, '#35363a', 12 * u, 6 * u, 250 * u, 34 * u, 8 * u);
  fill(ctx, '#35363a', 0, 40 * u, w, 44 * u);
  fill(ctx, '#202124', 120 * u, 48 * u, w - 240 * u, 30 * u, 15 * u);
  // Video.
  const vx = 40 * u, vy = 110 * u, vw = w * 0.66, vh = vw * 9 / 16;
  const g = ctx.createLinearGradient(vx, vy, vx + vw, vy + vh);
  g.addColorStop(0, '#0b1026'); g.addColorStop(0.55, '#311b5c'); g.addColorStop(1, '#0c3a66');
  fill(ctx, g, vx, vy, vw, vh, 12 * u);
  for (let i = 0; i < 3; i++) {
    ctx.strokeStyle = `rgba(170,190,255,${0.35 - i * 0.08})`;
    ctx.lineWidth = 2 * u;
    ctx.beginPath();
    ctx.ellipse(vx + vw / 2, vy + vh / 2, (120 + i * 90) * u, (46 + i * 34) * u, -0.12, 0, TAU);
    ctx.stroke();
  }
  const cg = ctx.createRadialGradient(vx + vw / 2, vy + vh / 2, 0, vx + vw / 2, vy + vh / 2, 60 * u);
  cg.addColorStop(0, '#ffe7b0'); cg.addColorStop(0.3, '#ff9d4a'); cg.addColorStop(1, 'rgba(255,120,40,0)');
  ctx.fillStyle = cg;
  ctx.fillRect(vx, vy, vw, vh);
  fill(ctx, '#ff0000', vx, vy + vh - 5 * u, vw * 0.38, 5 * u);
  ctx.fillStyle = '#f1f1f1';
  ctx.font = `600 ${22 * u}px ${FONT}`;
  ctx.fillText('Orbital mechanics, explained visually', vx, vy + vh + 40 * u);
  fill(ctx, '#aaa', vx, vy + vh + 60 * u, 260 * u, 10 * u, 5 * u);
  for (let i = 0; i < 6; i++) {
    const x = vx + vw + 24 * u, y = vy + i * 96 * u;
    const tg = ctx.createLinearGradient(x, y, x + 160 * u, y + 90 * u);
    tg.addColorStop(0, ['#1d3b6e', '#6e1d4a', '#1d6e5a', '#6e5a1d', '#3b1d6e', '#1d556e'][i]);
    tg.addColorStop(1, '#111');
    fill(ctx, tg, x, y, 160 * u, 88 * u, 8 * u);
    fill(ctx, '#e8e8e8', x + 172 * u, y + 8 * u, (120 + r() * 80) * u, 9 * u, 4 * u);
    fill(ctx, '#8a8a8a', x + 172 * u, y + 28 * u, 90 * u, 8 * u, 4 * u);
  }
}

function paintSpotify(ctx, w, h, u, r) {
  const g = ctx.createLinearGradient(0, 0, 0, h);
  g.addColorStop(0, '#1f5a3a'); g.addColorStop(0.45, '#121212'); g.addColorStop(1, '#121212');
  fill(ctx, '#000', 0, 0, w, h);
  fill(ctx, '#121212', 8 * u, 8 * u, 300 * u, h - 110 * u, 10 * u);
  fill(ctx, g, 316 * u, 8 * u, w - 324 * u, h - 110 * u, 10 * u);
  const ag = ctx.createLinearGradient(350 * u, 60 * u, 560 * u, 270 * u);
  ag.addColorStop(0, '#f6c343'); ag.addColorStop(1, '#e8488a');
  fill(ctx, ag, 350 * u, 60 * u, 210 * u, 210 * u, 6 * u);
  ctx.fillStyle = '#fff';
  ctx.font = `800 ${56 * u}px ${FONT}`;
  ctx.fillText('Deep Focus', 590 * u, 210 * u);
  for (let i = 0; i < 8; i++) {
    const y = (320 + i * 44) * u;
    fill(ctx, '#2a2a2a', 350 * u, y, 36 * u, 36 * u, 4 * u);
    fill(ctx, '#e8e8e8', 400 * u, y + 6 * u, (160 + r() * 140) * u, 10 * u, 5 * u);
    fill(ctx, '#8a8a8a', 400 * u, y + 24 * u, 110 * u, 8 * u, 4 * u);
  }
  lines(ctx, 30 * u, 60 * u, 240 * u, 12, 46 * u, ['#b3b3b3', '#6a6a6a'], r, 10 * u);
  fill(ctx, '#000', 0, h - 96 * u, w, 96 * u);
  ctx.fillStyle = '#fff';
  ctx.beginPath(); ctx.arc(w / 2, h - 58 * u, 18 * u, 0, TAU); ctx.fill();
  fill(ctx, '#4d4d4d', w / 2 - 260 * u, h - 24 * u, 520 * u, 5 * u, 3 * u);
  fill(ctx, '#1ed760', w / 2 - 260 * u, h - 24 * u, 190 * u, 5 * u, 3 * u);
}

function paintTelegram(ctx, w, h, u, r) {
  fill(ctx, '#0e1621', 0, 0, w, h);
  fill(ctx, '#17212b', 0, 0, 360 * u, h);
  for (let i = 0; i < 10; i++) {
    const y = (20 + i * 72) * u;
    if (i === 1) fill(ctx, '#2b5278', 0, y - 8 * u, 360 * u, 72 * u);
    ctx.fillStyle = ['#e17076', '#7bc862', '#65aadd', '#a695e7', '#ee7aae', '#6ec9cb', '#faa774'][i % 7];
    ctx.beginPath(); ctx.arc(40 * u, y + 26 * u, 26 * u, 0, TAU); ctx.fill();
    fill(ctx, '#e9eef3', 80 * u, y + 10 * u, (110 + r() * 90) * u, 10 * u, 5 * u);
    fill(ctx, '#708499', 80 * u, y + 32 * u, (160 + r() * 80) * u, 9 * u, 4 * u);
  }
  const bg = ctx.createLinearGradient(360 * u, 0, w, h);
  bg.addColorStop(0, '#0e1621'); bg.addColorStop(1, '#13233a');
  fill(ctx, bg, 360 * u, 56 * u, w, h);
  fill(ctx, '#17212b', 360 * u, 0, w, 56 * u);
  for (let i = 0; i < 7; i++) {
    const mine = i % 3 === 1;
    const bw = (220 + r() * 260) * u, bh = (44 + (i % 2) * 26) * u;
    const x = mine ? w - bw - 30 * u : 390 * u;
    const y = (90 + i * 92) * u;
    fill(ctx, mine ? '#2b5278' : '#182533', x, y, bw, bh, 14 * u);
    fill(ctx, '#e9eef3', x + 16 * u, y + 16 * u, bw * 0.7, 9 * u, 4 * u);
  }
}

function paintExplorer(ctx, w, h, u, r) {
  fill(ctx, '#202020', 0, 0, w, h);
  fill(ctx, '#2b2b2b', 0, 0, w, 90 * u);
  fill(ctx, '#1c1c1c', 12 * u, 50 * u, w * 0.6, 30 * u, 6 * u);
  fill(ctx, '#1c1c1c', 0, 90 * u, 240 * u, h);
  lines(ctx, 24 * u, 120 * u, 170 * u, 12, 36 * u, ['#cfcfcf', '#9a9a9a'], r, 10 * u);
  for (let i = 0; i < 18; i++) {
    const x = (270 + (i % 6) * 160) * u, y = (120 + Math.floor(i / 6) * 150) * u;
    fill(ctx, '#e8b33c', x + 20 * u, y + 20 * u, 100 * u, 72 * u, 6 * u);
    fill(ctx, '#f6c94f', x + 20 * u, y + 32 * u, 100 * u, 62 * u, 6 * u);
    fill(ctx, '#d6d6d6', x + 22 * u, y + 110 * u, 96 * u, 9 * u, 4 * u);
  }
}

function paintFigma(ctx, w, h, u, r) {
  fill(ctx, '#1e1e1e', 0, 0, w, h);
  fill(ctx, '#2c2c2c', 0, 0, w, 44 * u);
  fill(ctx, '#2c2c2c', 0, 44 * u, 240 * u, h);
  fill(ctx, '#2c2c2c', w - 260 * u, 44 * u, 260 * u, h);
  lines(ctx, 20 * u, 70 * u, 180 * u, 16, 30 * u, ['#9a9a9a', '#6f6f6f'], r, 8 * u);
  lines(ctx, w - 240 * u, 70 * u, 200 * u, 14, 34 * u, ['#8a8a8a', '#bdbdbd'], r, 8 * u);
  const frames = [['#0b0f1c', 300, 110, 420, 260], ['#10152a', 760, 110, 240, 260], ['#0b0f1c', 300, 410, 700, 240]];
  frames.forEach(([col, x, y, fw, fh], i) => {
    fill(ctx, col, x * u, y * u, fw * u, fh * u, 6 * u);
    const cx = (x + fw / 2) * u, cy = (y + fh / 2) * u;
    for (let k = 0; k < 3; k++) {
      ctx.strokeStyle = `rgba(150,170,255,${0.5 - k * 0.12})`;
      ctx.lineWidth = 1.5 * u;
      ctx.beginPath(); ctx.ellipse(cx, cy, (60 + k * 50) * u * (fw / 420), (24 + k * 20) * u, -0.1, 0, TAU); ctx.stroke();
    }
    const gg = ctx.createRadialGradient(cx, cy, 0, cx, cy, 40 * u);
    gg.addColorStop(0, ['#a259ff', '#1abcfe', '#0acf83'][i]); gg.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = gg; ctx.fillRect(cx - 50 * u, cy - 50 * u, 100 * u, 100 * u);
  });
}

function paintTerminal(ctx, w, h, u, r) {
  fill(ctx, '#0c0c0c', 0, 0, w, h);
  fill(ctx, '#1f1f1f', 0, 0, w, 40 * u);
  fill(ctx, '#0c0c0c', 8 * u, 6 * u, 180 * u, 34 * u, 6 * u);
  ctx.font = `500 ${15 * u}px "DejaVu Sans Mono", monospace`;
  const rowsTxt = [
    ['#3b78ff', 'PS C:\\src\\FlowSwitch> ', '#cccccc', 'dotnet build -c Release'],
    ['#cccccc', '  Determining projects to restore...', '', ''],
    ['#cccccc', '  FlowSwitch.Core -> bin\\Release\\net10.0\\FlowSwitch.Core.dll', '', ''],
    ['#cccccc', '  FlowSwitch -> bin\\Release\\FlowSwitch.dll', '', ''],
    ['#13a10e', 'Build succeeded.', '', ''],
    ['#cccccc', '    0 Warning(s)', '', ''],
    ['#cccccc', '    0 Error(s)', '', ''],
    ['#3b78ff', 'PS C:\\src\\FlowSwitch> ', '#cccccc', 'dotnet test'],
    ['#13a10e', 'Passed!  - Failed: 0, Passed: 64, Skipped: 0', '', ''],
    ['#3b78ff', 'PS C:\\src\\FlowSwitch> ', '#cccccc', '█'],
  ];
  rowsTxt.forEach(([c1, t1, c2, t2], i) => {
    const y = (74 + i * 26) * u;
    ctx.fillStyle = c1; ctx.fillText(t1, 20 * u, y);
    if (t2) { ctx.fillStyle = c2; ctx.fillText(t2, 20 * u + ctx.measureText(t1).width, y); }
  });
}

function paintSteam(ctx, w, h, u, r) {
  const g = ctx.createLinearGradient(0, 0, 0, h);
  g.addColorStop(0, '#1b2838'); g.addColorStop(1, '#0e141b');
  fill(ctx, g, 0, 0, w, h);
  fill(ctx, '#171a21', 0, 0, w, 70 * u);
  for (let i = 0; i < 8; i++) fill(ctx, ['#2a475e', '#66c0f4', '#c7d5e0'][i % 3], (60 + i * 150) * u, 200 * u, 130 * u, 180 * u, 4 * u);
}

function paintPhotoshop(ctx, w, h, u, r) {
  fill(ctx, '#252525', 0, 0, w, h);
  fill(ctx, '#323232', 0, 0, w, 40 * u);
  fill(ctx, '#323232', 0, 40 * u, 44 * u, h);
  fill(ctx, '#323232', w - 280 * u, 40 * u, 280 * u, h);
  const x = 120 * u, y = 90 * u, cw = w - 480 * u, chh = h - 160 * u;
  const g = ctx.createLinearGradient(x, y, x + cw, y + chh);
  g.addColorStop(0, '#050816'); g.addColorStop(0.6, '#1a1045'); g.addColorStop(1, '#083a5c');
  fill(ctx, g, x, y, cw, chh);
  for (let i = 0; i < 80; i++) {
    ctx.fillStyle = `rgba(255,255,255,${0.2 + r() * 0.6})`;
    ctx.fillRect(x + r() * cw, y + r() * chh, 2 * u, 2 * u);
  }
  const ng = ctx.createRadialGradient(x + cw * 0.55, y + chh * 0.45, 0, x + cw * 0.55, y + chh * 0.45, cw * 0.35);
  ng.addColorStop(0, 'rgba(255,120,200,0.7)'); ng.addColorStop(0.5, 'rgba(90,80,255,0.35)'); ng.addColorStop(1, 'rgba(0,0,0,0)');
  ctx.fillStyle = ng; ctx.fillRect(x, y, cw, chh);
  lines(ctx, w - 260 * u, 70 * u, 220 * u, 16, 34 * u, ['#8a8a8a', '#5a5a5a'], r, 10 * u);
}

function paintNotepad(ctx, w, h, u, r) {
  fill(ctx, '#272727', 0, 0, w, h);
  fill(ctx, '#202020', 0, 0, w, 80 * u);
  lines(ctx, 30 * u, 110 * u, w * 0.8, 20, 30 * u, ['#e0e0e0', '#bdbdbd'], r, 9 * u);
}

function paintExcel(ctx, w, h, u, r) {
  fill(ctx, '#1f1f1f', 0, 0, w, h);
  fill(ctx, '#185c37', 0, 0, w, 40 * u);
  fill(ctx, '#2a2a2a', 0, 40 * u, w, 100 * u);
  ctx.strokeStyle = '#3a3a3a';
  for (let i = 0; i < 30; i++) { ctx.beginPath(); ctx.moveTo(0, (150 + i * 24) * u); ctx.lineTo(w, (150 + i * 24) * u); ctx.stroke(); }
  for (let j = 0; j < 14; j++) { ctx.beginPath(); ctx.moveTo((50 + j * 90) * u, 150 * u); ctx.lineTo((50 + j * 90) * u, h); ctx.stroke(); }
  for (let i = 0; i < 20; i++) for (let j = 0; j < 6; j++) if (r() > 0.3) fill(ctx, '#c8c8c8', (60 + j * 90) * u, (158 + i * 24) * u, (30 + r() * 40) * u, 8 * u, 3 * u);
}

function paintObsidian(ctx, w, h, u, r) {
  fill(ctx, '#1e1e1e', 0, 0, w, h);
  fill(ctx, '#262626', 0, 0, 260 * u, h);
  lines(ctx, 24 * u, 60 * u, 190 * u, 16, 32 * u, ['#a8a8a8', '#7c7c7c'], r, 9 * u);
  ctx.fillStyle = '#e6e6e6';
  ctx.font = `700 ${34 * u}px ${FONT}`;
  ctx.fillText('Motion language', 320 * u, 110 * u);
  lines(ctx, 320 * u, 150 * u, w * 0.6, 16, 30 * u, ['#cfcfcf', '#9a9a9a', '#a882ff'], r, 9 * u);
}

function paintGeneric(ctx, w, h, u, r) {
  fill(ctx, '#202020', 0, 0, w, h);
  lines(ctx, 40 * u, 80 * u, w * 0.8, 20, 30 * u, ['#9a9a9a'], r, 9 * u);
}

// ─────────────────────────────── icons ───────────────────────────────

export function paintIcon(kind, size = 256) {
  const c = canvas(size, size);
  const ctx = c.getContext('2d');
  const s = size / 256;
  ctx.save();
  ctx.scale(s, s);
  const circle = (x, y, r, col) => { ctx.fillStyle = col; ctx.beginPath(); ctx.arc(x, y, r, 0, TAU); ctx.fill(); };
  switch (kind) {
    case 'code': {
      const g = ctx.createLinearGradient(0, 0, 256, 256);
      g.addColorStop(0, '#3ab6ff'); g.addColorStop(1, '#0065a9');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.moveTo(186, 24); ctx.lineTo(232, 46); ctx.lineTo(232, 210); ctx.lineTo(186, 232); ctx.lineTo(80, 136);
      ctx.lineTo(40, 168); ctx.lineTo(24, 158); ctx.lineTo(24, 98); ctx.lineTo(40, 88); ctx.lineTo(80, 120); ctx.closePath();
      ctx.fill();
      ctx.fillStyle = '#ffffff';
      ctx.beginPath(); ctx.moveTo(186, 70); ctx.lineTo(186, 186); ctx.lineTo(112, 128); ctx.closePath(); ctx.fill();
      break;
    }
    case 'discord': {
      fill(ctx, '#5865f2', 16, 16, 224, 224, 56);
      ctx.fillStyle = '#fff';
      ctx.beginPath(); ctx.ellipse(128, 136, 72, 50, 0, 0, TAU); ctx.fill();
      ctx.beginPath(); ctx.ellipse(76, 104, 22, 30, -0.6, 0, TAU); ctx.fill();
      ctx.beginPath(); ctx.ellipse(180, 104, 22, 30, 0.6, 0, TAU); ctx.fill();
      circle(102, 132, 13, '#5865f2'); circle(154, 132, 13, '#5865f2');
      break;
    }
    case 'chrome': {
      circle(128, 128, 116, '#db4437');
      ctx.fillStyle = '#0f9d58';
      ctx.beginPath(); ctx.moveTo(128, 128); ctx.arc(128, 128, 116, Math.PI * 0.5, Math.PI * 1.17); ctx.closePath(); ctx.fill();
      ctx.fillStyle = '#f4b400';
      ctx.beginPath(); ctx.moveTo(128, 128); ctx.arc(128, 128, 116, -Math.PI * 0.17, Math.PI * 0.5); ctx.closePath(); ctx.fill();
      circle(128, 128, 54, '#ffffff'); circle(128, 128, 43, '#4285f4');
      break;
    }
    case 'spotify': {
      circle(128, 128, 116, '#1ed760');
      ctx.strokeStyle = '#121212'; ctx.lineCap = 'round';
      [[0, 18, 78], [36, 15, 62], [68, 12, 48]].forEach(([dy, lw, r]) => {
        ctx.lineWidth = lw;
        ctx.beginPath(); ctx.ellipse(128, 150 + dy * 0.6, r * 1.1, r * 0.5, 0, Math.PI * 1.12, Math.PI * 1.88); ctx.stroke();
      });
      break;
    }
    case 'telegram': {
      const g = ctx.createLinearGradient(0, 0, 0, 256);
      g.addColorStop(0, '#37bbfe'); g.addColorStop(1, '#007dbb');
      circle(128, 128, 116, g);
      ctx.fillStyle = '#fff';
      ctx.beginPath(); ctx.moveTo(58, 124); ctx.lineTo(186, 72); ctx.lineTo(164, 186); ctx.lineTo(122, 152); ctx.lineTo(104, 172); ctx.lineTo(100, 138); ctx.closePath(); ctx.fill();
      break;
    }
    case 'explorer': {
      fill(ctx, '#e8a820', 24, 60, 208, 150, 18);
      fill(ctx, '#e8a820', 24, 44, 92, 40, 12);
      const g = ctx.createLinearGradient(0, 90, 0, 210);
      g.addColorStop(0, '#ffd75e'); g.addColorStop(1, '#f5b72f');
      fill(ctx, g, 24, 88, 208, 124, 18);
      break;
    }
    case 'figma': {
      circle(100, 60, 36, '#f24e1e'); circle(156, 60, 36, '#ff7262'); fill(ctx, '#f24e1e', 64, 24, 36, 72);
      circle(100, 128, 36, '#a259ff'); circle(156, 128, 36, '#1abcfe'); circle(100, 196, 36, '#0acf83');
      fill(ctx, '#ff7262', 100, 24, 56, 72); fill(ctx, '#a259ff', 64, 92, 72, 72, 36); fill(ctx, '#0acf83', 64, 160, 36, 36);
      break;
    }
    case 'terminal': {
      fill(ctx, '#2d2d2d', 20, 32, 216, 192, 30);
      fill(ctx, '#3c3c3c', 20, 32, 216, 40, 30);
      ctx.strokeStyle = '#fff'; ctx.lineWidth = 16; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      ctx.beginPath(); ctx.moveTo(62, 112); ctx.lineTo(100, 142); ctx.lineTo(62, 172); ctx.stroke();
      fill(ctx, '#fff', 116, 164, 72, 16, 8);
      break;
    }
    case 'steam': {
      const g = ctx.createLinearGradient(0, 0, 0, 256);
      g.addColorStop(0, '#1b2e5a'); g.addColorStop(1, '#0d1a36');
      circle(128, 128, 116, g);
      circle(164, 100, 38, '#fff'); circle(164, 100, 24, '#1b2e5a'); circle(96, 164, 26, '#fff');
      ctx.strokeStyle = '#fff'; ctx.lineWidth = 16; ctx.beginPath(); ctx.moveTo(34, 150); ctx.lineTo(96, 164); ctx.stroke();
      break;
    }
    case 'photoshop': {
      fill(ctx, '#001e36', 16, 24, 224, 208, 44);
      ctx.fillStyle = '#31a8ff'; ctx.font = `700 110px ${FONT}`; ctx.textAlign = 'center'; ctx.fillText('Ps', 128, 168);
      break;
    }
    case 'notepad': {
      fill(ctx, '#4aa3df', 40, 20, 176, 216, 24);
      fill(ctx, '#ffffff', 56, 52, 144, 168, 12);
      [80, 112, 144, 176].forEach(y => fill(ctx, '#9ecfee', 76, y, 104, 10, 5));
      break;
    }
    case 'excel': {
      fill(ctx, '#107c41', 20, 20, 216, 216, 36);
      ctx.fillStyle = '#fff'; ctx.font = `800 130px ${FONT}`; ctx.textAlign = 'center'; ctx.fillText('X', 128, 176);
      break;
    }
    case 'obsidian': {
      const g = ctx.createLinearGradient(40, 20, 220, 236);
      g.addColorStop(0, '#c4a7ff'); g.addColorStop(1, '#5a2fc2');
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.moveTo(140, 16); ctx.lineTo(214, 96); ctx.lineTo(176, 236); ctx.lineTo(66, 220); ctx.lineTo(44, 110); ctx.closePath(); ctx.fill();
      break;
    }
    default: {
      fill(ctx, '#5b6b8c', 24, 24, 208, 208, 44);
      break;
    }
  }
  ctx.restore();
  return c;
}

// ─────────────────────────────── text ───────────────────────────────

const GLYPHS = { '\uE8BB': '✕', '\uE718': '⊙', '\uE77A': '⊘', '\uE721': '⌕', '\uE7F4': '▭' };

export function paintText(spec) {
  const families = ['Inter', 'Inter', 'DejaVu Sans'];
  let text = spec.text;
  if (spec.family === 2) text = [...text].map(ch => GLYPHS[ch] ?? ch).join('');
  const weight = spec.weight;
  const size = spec.size;
  const font = `${weight} ${size}px ${families[spec.family]}, ${FONT}`;
  const probe = canvas(4, 4).getContext('2d');
  probe.font = font;
  let width = probe.measureText(text).width;
  if (width > spec.maxWidth && spec.maxWidth > 0) {
    while (text.length > 1 && probe.measureText(text + '…').width > spec.maxWidth) text = text.slice(0, -1);
    text = text.trimEnd() + '…';
    width = probe.measureText(text).width;
  }
  const c = canvas(Math.ceil(width) + 4, Math.ceil(size * 1.32) + 2);
  const ctx = c.getContext('2d');
  ctx.font = font;
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, 2, c.height / 2 + size * 0.04);
  return c;
}
