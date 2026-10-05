"use strict";

const $ = (id) => document.getElementById(id);
const views = ["loading", "signed-out", "pending", "dash"];
let pollTimer = null;

function show(view) {
  for (const v of views) $(v).hidden = v !== view;
}

function error(msg) {
  $("error").textContent = msg || "";
  $("error").hidden = !msg;
}

async function api(path, body) {
  const res = await fetch(path, body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(data.error || `HTTP ${res.status}`), { status: res.status });
  return data;
}

// --- base64url <-> ArrayBuffer, for browsers without the JSON helpers ---
const toBuf = (s) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(s.length / 4) * 4, "=")), (c) => c.charCodeAt(0)).buffer;
const toB64 = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

function creationOptions(o) {
  if (PublicKeyCredential.parseCreationOptionsFromJSON) return PublicKeyCredential.parseCreationOptionsFromJSON(o);
  return { ...o, challenge: toBuf(o.challenge), user: { ...o.user, id: toBuf(o.user.id) },
    excludeCredentials: (o.excludeCredentials || []).map((c) => ({ ...c, id: toBuf(c.id) })) };
}

function requestOptions(o) {
  if (PublicKeyCredential.parseRequestOptionsFromJSON) return PublicKeyCredential.parseRequestOptionsFromJSON(o);
  return { ...o, challenge: toBuf(o.challenge), allowCredentials: (o.allowCredentials || []).map((c) => ({ ...c, id: toBuf(c.id) })) };
}

function credentialJSON(c) {
  if (typeof c.toJSON === "function") return c.toJSON();
  const r = c.response;
  const response = { clientDataJSON: toB64(r.clientDataJSON) };
  if (r.attestationObject) {
    response.attestationObject = toB64(r.attestationObject);
    if (r.getTransports) response.transports = r.getTransports();
  } else {
    response.authenticatorData = toB64(r.authenticatorData);
    response.signature = toB64(r.signature);
    if (r.userHandle) response.userHandle = toB64(r.userHandle);
  }
  return { id: c.id, rawId: toB64(c.rawId), type: c.type, response,
    clientExtensionResults: c.getClientExtensionResults(), authenticatorAttachment: c.authenticatorAttachment };
}

async function withButton(btn, fn) {
  error("");
  btn.disabled = true;
  try { await fn(); } catch (e) {
    if (e.name === "NotAllowedError") error("Passkey prompt was cancelled or timed out.");
    else error(e.message || String(e));
  } finally { btn.disabled = false; }
}

async function login() {
  const { flow, options } = await api("/api/login/options", {});
  const cred = await navigator.credentials.get({ publicKey: requestOptions(options) });
  await api("/api/login/verify", { flow, credential: credentialJSON(cred) });
  await refresh();
}

async function register() {
  const { flow, options } = await api("/api/register/options", { label: $("label").value });
  const cred = await navigator.credentials.create({ publicKey: creationOptions(options) });
  await api("/api/register/verify", { flow, credential: credentialJSON(cred) });
  await refresh();
}

async function logout() {
  await api("/api/logout", {});
  await refresh();
}

function ago(seconds) {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
}

function renderStatus(st) {
  $("count").textContent = st.online;
  const pill = $("server");
  pill.textContent = st.server === "active" ? "Server up" : `Server ${st.server}`;
  pill.className = `pill ${st.server === "active" ? "ok" : "bad"}`;
  $("updated").textContent = `Updated ${new Date().toLocaleTimeString()}`;

  const list = $("players");
  list.replaceChildren();
  if (!st.players.length) {
    const li = document.createElement("li");
    li.className = "muted";
    li.textContent = "Nobody is online.";
    list.append(li);
  }
  for (const p of st.players) {
    const li = document.createElement("li");
    const who = document.createElement("div");
    const name = document.createElement("div");
    name.textContent = p.name || "Joining…";
    const sid = document.createElement("div");
    sid.className = "sid";
    sid.textContent = `Steam ${p.steam_id}`;
    who.append(name, sid);
    const since = document.createElement("div");
    since.className = "muted small";
    since.textContent = `on for ${ago(st.now - p.since)}`;
    li.append(who, since);
    list.append(li);
  }

  const snap = st.snapshot;
  $("snapshot").textContent = snap
    ? `Server's own count: ${snap.count} as of ${ago(st.now - snap.at)} ago (it reports every 10 min) · ${snap.objects.toLocaleString()} world objects`
    : "";
}

async function poll() {
  try {
    renderStatus(await api("/api/status"));
    error("");
  } catch (e) {
    if (e.status === 401 || e.status === 403) return refresh();
    error(`Couldn't refresh: ${e.message}`);
  }
}

async function refresh() {
  clearInterval(pollTimer);
  stopMetrics();
  stopVisitors();
  stopConsole();
  const me = await api("/api/me");
  if (!me.signed_in) return show("signed-out");
  if (!me.approved) {
    $("pending-id").textContent = me.cred_id;
    $("pending-line-wrap").hidden = !me.line;
    $("pending-line").textContent = me.line || "";
    $("pending-unverified").hidden = me.verified;
    return show("pending");
  }
  $("me-id").textContent = me.cred_id;
  show("dash");
  await poll();
  pollTimer = setInterval(poll, 10000);
  startMetrics();
  startVisitors();
  startConsole();
}

document.addEventListener("DOMContentLoaded", () => {
  if (!window.PublicKeyCredential) {
    show("signed-out");
    error("This browser doesn't support passkeys.");
    return;
  }
  $("btn-login").addEventListener("click", (e) => withButton(e.target, login));
  $("btn-register").addEventListener("click", (e) => withButton(e.target, register));
  for (const b of document.querySelectorAll(".logout")) b.addEventListener("click", (e) => withButton(e.target, logout));
  for (const el of document.querySelectorAll("pre.copy")) {
    el.addEventListener("click", () => navigator.clipboard && navigator.clipboard.writeText(el.textContent.trim()));
  }
  wireConsole();
  wireVisitors();
  refresh().catch((e) => { show("signed-out"); error(e.message); });
});

// --- Metrics charts ---------------------------------------------------------

const SVG_NS = "http://www.w3.org/2000/svg";
const GB = 1024 ** 3;
const fmtPct = (v) => (Number.isInteger(v) ? `${v}%` : `${v.toFixed(v < 10 ? 1 : 0)}%`);
function fmtGB(v) {
  if (Number.isInteger(v / GB)) return `${v / GB} GB`;
  if (v < GB) return `${Math.round(v / 1024 ** 2)} MB`;
  return `${(v / GB).toFixed(v < 10 * GB ? 2 : 1)} GB`;
}
const fmtInt = (v) => String(Math.round(v));
function fmtRate(v) {
  const [unit, name] = v >= 1024 ** 2 ? [1024 ** 2, "MB/s"] : v >= 1024 ? [1024, "KB/s"] : [1, "B/s"];
  const n = v / unit;
  return `${Number.isInteger(n) || n >= 10 ? Math.round(n) : n.toFixed(1)} ${name}`;
}

// One chart per measure, so every chart has a single y-axis in one unit.
const CHARTS = [
  { id: "players", title: "Players online", desc: "Peak in each interval",
    series: [{ key: "players", label: "Players" }], fmt: fmtInt, step: true, minMax: 2, integer: true },
  { id: "cpu", title: "CPU", desc: "Valheim's simulation runs on one thread, so watch the busiest core, not the average.",
    series: [{ key: "core_max", label: "Busiest core", long: "Busiest core (peak)" },
             { key: "cpu", label: "Server", long: "Whole server (avg)" },
             { key: "vh_cpu", label: "Valheim", long: "Valheim process (avg)" }],
    fmt: fmtPct, fixedMax: 100 },
  { id: "mem", title: "Memory", desc: "Used = total minus what the kernel can reclaim. Dashed line is total RAM.",
    series: [{ key: "mem_used", label: "Used", long: "Used" },
             { key: "vh_rss", label: "Valheim", long: "Valheim (RSS)" },
             { key: "swap_used", label: "Swap", long: "Swap used" }],
    fmt: fmtGB, ref: { key: "mem_total", label: "Total RAM" } },
  { id: "net", title: "Network", desc: "Public interface throughput",
    series: [{ key: "net_rx", label: "In", long: "In (download)" }, { key: "net_tx", label: "Out", long: "Out (upload)" }],
    fmt: fmtRate },
  { id: "disk", title: "Disk used", desc: "", series: [{ key: "disk_used", label: "Used" }], fmt: fmtGB },
];

const metricsState = { range: "6h", data: null, timer: null, hoverTs: null, charts: {}, observer: null };
try { metricsState.range = localStorage.getItem("valpanel.range") || "6h"; } catch (e) { /* storage blocked */ }

function el(tag, attrs = {}, ...kids) {
  const n = tag.startsWith("svg:") ? document.createElementNS(SVG_NS, tag.slice(4)) : document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "text") n.textContent = v;
    else if (k === "class") n.setAttribute("class", v);
    else n.setAttribute(k, v);
  }
  n.append(...kids);
  return n;
}

function niceTicks(max, count = 4, integer = false) {
  if (!(max > 0)) max = 1;
  let step = max / count;
  const mag = 10 ** Math.floor(Math.log10(step));
  step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= step);
  if (integer) step = Math.max(1, Math.ceil(step));
  const top = Math.ceil(max / step) * step;
  const ticks = [];
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(v);
  return ticks;
}

function unitTicks(max, unit) {
  // Byte axes read better on round steps of the unit they're labelled in.
  return niceTicks(max / unit, 4).map((v) => v * unit);
}

function timeTicks(from, to, width) {
  const span = to - from;
  const steps = [300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800].map((s) => s * 1000);
  const want = Math.max(2, Math.floor(width / 90));
  const step = steps.find((s) => span / s <= want) || steps[steps.length - 1];
  const tz = new Date().getTimezoneOffset() * 60000;
  const ticks = [];
  for (let t = Math.ceil((from - tz) / step) * step + tz; t <= to; t += step) ticks.push(t);
  const daily = step >= 86400000;
  const fmt = (t) => daily
    ? new Date(t).toLocaleDateString([], { month: "short", day: "numeric" })
    : new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return { ticks, fmt };
}

function whenLabel(t) {
  return new Date(t).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function buildChartCard(spec) {
  const card = el("div", { class: "chart-card", id: `chart-${spec.id}` });
  card.append(el("h3", { text: spec.title }));
  if (spec.desc) card.append(el("p", { class: "desc", text: spec.desc }));
  if (spec.series.length > 1) {
    const legend = el("div", { class: "legend" });
    spec.series.forEach((s, i) => legend.append(el("span", { style: `--c: var(--series-${i + 1})`, text: s.long || s.label })));
    card.append(legend);
  }
  const plot = el("div", { class: "plot" });
  const svg = el("svg:svg", { tabindex: "0", role: "img", "aria-label": `${spec.title} chart. Use left and right arrow keys to read values.` });
  const tip = el("div", { class: "tip", hidden: "" });
  plot.append(svg, tip);
  card.append(plot);
  const details = el("details");
  details.append(el("summary", { text: "Show as table" }), el("div", { class: "table-wrap" }));
  details.addEventListener("toggle", () => details.open && renderTable(spec, details.lastChild));
  card.append(details);

  const chart = { spec, card, svg, tip, details, layout: null };
  svg.addEventListener("pointermove", (e) => {
    if (!chart.layout) return;
    const r = svg.getBoundingClientRect();
    setHover(chart.layout.nearest(e.clientX - r.left), chart);
  });
  svg.addEventListener("pointerleave", () => setHover(null));
  svg.addEventListener("blur", () => setHover(null));
  svg.addEventListener("focus", () => chart.layout && setHover(chart.layout.lastTs(), chart));
  svg.addEventListener("keydown", (e) => {
    if (!chart.layout) return;
    const xs = chart.layout.xs;
    let i = xs.indexOf(metricsState.hoverTs);
    if (e.key === "ArrowLeft") i = Math.max(0, (i < 0 ? xs.length : i) - 1);
    else if (e.key === "ArrowRight") i = Math.min(xs.length - 1, i + 1);
    else if (e.key === "Home") i = 0;
    else if (e.key === "End") i = xs.length - 1;
    else if (e.key === "Escape") return setHover(null);
    else return;
    e.preventDefault();
    setHover(xs[i], chart);
  });
  return chart;
}

function seriesData(data, key) {
  const col = data.columns.indexOf(key);
  return data.rows.map((r) => r[col]);
}

function renderChart(chart, data) {
  const { spec, svg } = chart;
  const width = svg.clientWidth || svg.parentNode.clientWidth;
  if (!width) return;
  const height = 180;
  const multi = spec.series.length > 1;
  const m = { top: 10, right: multi ? (width < 480 ? 64 : 84) : 12, bottom: 22, left: 52 };
  const w = width - m.left - m.right, h = height - m.top - m.bottom;
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  svg.replaceChildren();

  const xs = data.rows.map((r) => r[0] * 1000);
  const from = data.from * 1000, to = data.to * 1000;
  const values = spec.series.map((s) => seriesData(data, s.key));
  const refVals = spec.ref ? seriesData(data, spec.ref.key).filter((v) => v != null) : [];
  const ref = refVals.length ? refVals[refVals.length - 1] : null;
  let max = Math.max(spec.minMax || 0, ref || 0, ...values.flat().filter((v) => v != null));
  const ticks = spec.fixedMax ? [0, 25, 50, 75, 100]
    : spec.fmt === fmtGB ? unitTicks(max, max >= GB ? GB : 1024 ** 2)
    : spec.fmt === fmtRate ? unitTicks(max, max >= 1024 ** 2 ? 1024 ** 2 : max >= 1024 ? 1024 : 1)
    : niceTicks(max, 4, spec.integer);
  max = ticks[ticks.length - 1];
  const X = (t) => m.left + ((t - from) / (to - from)) * w;
  const Y = (v) => m.top + h - (v / max) * h;

  // Grid and y labels
  for (const t of ticks) {
    svg.append(el("svg:line", { class: t === 0 ? "baseline" : "grid", x1: m.left, x2: m.left + w, y1: Y(t), y2: Y(t) }));
    svg.append(el("svg:text", { x: m.left - 8, y: Y(t) + 4, "text-anchor": "end", text: spec.fmt(t) }));
  }
  const tt = timeTicks(from, to, w);
  for (const t of tt.ticks) svg.append(el("svg:text", { x: X(t), y: height - 4, "text-anchor": "middle", text: tt.fmt(t) }));
  if (ref != null) {
    svg.append(el("svg:line", { class: "ref", x1: m.left, x2: m.left + w, y1: Y(ref), y2: Y(ref) }));
    svg.append(el("svg:text", { x: m.left + 4, y: Y(ref) - 4, text: `${spec.ref.label} ${spec.fmt(ref)}` }));
  }

  // Lines, broken wherever samples are missing (panel or server down).
  const gap = data.bucket * 1000 * 2.5;
  const ends = [];
  values.forEach((vals, i) => {
    let d = "", prevT = null, prevY = null, last = null;
    vals.forEach((v, j) => {
      if (v == null) { prevT = null; return; }
      const x = X(xs[j]), y = Y(v);
      if (prevT == null || xs[j] - prevT > gap) d += `M${x},${y}`;
      else d += spec.step ? `H${x}V${y}` : `L${x},${y}`;
      prevT = xs[j]; prevY = y; last = { x, y };
    });
    if (d) svg.append(el("svg:path", { class: "series", d, stroke: `var(--series-${i + 1})` }));
    if (multi && last) ends.push({ y: last.y, label: spec.series[i].label, i });
  });

  // Direct labels at the right edge, nudged apart so they don't collide.
  ends.sort((a, b) => a.y - b.y);
  for (let k = 1; k < ends.length; k++) ends[k].y = Math.max(ends[k].y, ends[k - 1].y + 13);
  for (let k = ends.length - 2; k >= 0; k--) ends[k].y = Math.min(ends[k].y, ends[k + 1].y - 13);
  for (const e of ends) svg.append(el("svg:text", { class: "end-label", x: m.left + w + 6, y: e.y + 4, text: e.label }));

  if (!xs.length) {
    svg.append(el("svg:text", { x: m.left + w / 2, y: m.top + h / 2, "text-anchor": "middle", text: "No samples in this range yet" }));
  }

  const hover = el("svg:g");
  svg.append(hover);
  chart.layout = {
    xs, X, Y, m, w, values, hover,
    nearest(px) {
      if (!xs.length) return null;
      let best = 0;
      for (let j = 1; j < xs.length; j++) if (Math.abs(X(xs[j]) - px) < Math.abs(X(xs[best]) - px)) best = j;
      return xs[best];
    },
    lastTs: () => xs[xs.length - 1] ?? null,
  };
  drawHover(chart);
}

function setHover(ts, source) {
  metricsState.hoverTs = ts;
  metricsState.hoverSource = source || null;
  for (const c of Object.values(metricsState.charts)) drawHover(c);
}

function drawHover(chart) {
  const L = chart.layout;
  if (!L) return;
  L.hover.replaceChildren();
  const ts = metricsState.hoverTs;
  const j = ts == null ? -1 : L.xs.indexOf(ts);
  if (j < 0) { chart.tip.hidden = true; return; }
  const x = L.X(ts);
  L.hover.append(el("svg:line", { class: "cross", x1: x, x2: x, y1: L.m.top, y2: L.m.top + (L.Y(0) - L.m.top) }));
  L.values.forEach((vals, i) => {
    if (vals[j] != null) L.hover.append(el("svg:circle", { class: "dot", cx: x, cy: L.Y(vals[j]), r: 4, fill: `var(--series-${i + 1})` }));
  });
  if (metricsState.hoverSource !== chart) { chart.tip.hidden = true; return; }

  const { spec, tip } = chart;
  tip.replaceChildren(el("div", { class: "when", text: whenLabel(ts) }));
  spec.series.forEach((s, i) => {
    const v = L.values[i][j];
    const row = el("div", { class: "row" });
    row.append(el("i", { style: `--c: var(--series-${i + 1})` }), el("b", { text: v == null ? "–" : spec.fmt(v) }), el("span", { text: s.long || s.label }));
    tip.append(row);
  });
  tip.hidden = false;
  const plotW = chart.svg.clientWidth;
  const left = x + 12 + tip.offsetWidth > plotW ? x - 12 - tip.offsetWidth : x + 12;
  tip.style.left = `${Math.max(0, left)}px`;
}

function renderTable(spec, wrap) {
  const data = metricsState.data;
  if (!data) return;
  const table = el("table");
  const head = el("tr", {}, el("th", { text: "Time" }));
  spec.series.forEach((s) => head.append(el("th", { text: s.long || s.label })));
  table.append(el("thead", {}, head));
  const body = el("tbody");
  const cols = spec.series.map((s) => data.columns.indexOf(s.key));
  for (const r of [...data.rows].reverse()) {
    const tr = el("tr", {}, el("td", { text: whenLabel(r[0] * 1000) }));
    cols.forEach((c) => tr.append(el("td", { text: r[c] == null ? "–" : spec.fmt(r[c]) })));
    body.append(tr);
  }
  table.append(body);
  wrap.replaceChildren(table);
}

function tile(label, value, sub) {
  const t = el("div", { class: "tile" });
  t.append(el("div", { class: "label", text: label }), el("div", { class: "value", text: value }));
  if (sub) t.append(el("div", { class: "sub", text: sub }));
  return t;
}

function renderTiles(data) {
  const l = data.latest;
  const box = $("tiles");
  if (!l) { box.replaceChildren(); return; }
  const n = (v, f) => (v == null ? "–" : f(v));
  box.replaceChildren(
    tile("CPU", n(l.cpu, fmtPct), `busiest core ${n(l.core_max, fmtPct)} · Valheim ${n(l.vh_cpu, fmtPct)}`),
    tile("Memory", `${n(l.mem_used, fmtGB)}`, `of ${n(l.mem_total, fmtGB)} · Valheim ${n(l.vh_rss, fmtGB)} · swap ${n(l.swap_used, fmtGB)}`),
    tile("Disk", n(l.disk_used, fmtGB), `of ${n(l.disk_total, fmtGB)} (${l.disk_total ? Math.round((100 * l.disk_used) / l.disk_total) : "–"}% used)`),
    tile("Network", `↓ ${n(l.net_rx, fmtRate)}`, `↑ ${n(l.net_tx, fmtRate)}`),
  );
}

function renderMetrics() {
  const data = metricsState.data;
  if (!data) return;
  renderTiles(data);
  const mins = (s) => (s < 3600 ? `${Math.round(s / 60)}-minute` : `${Math.round(s / 3600)}-hour`);
  const disk = CHARTS.find((c) => c.id === "disk");
  disk.desc = data.latest ? `Root filesystem, ${fmtGB(data.latest.disk_total)} total` : "";
  let note = `Sampled every ${data.interval} s; each point is a ${data.bucket < 60 ? `${data.bucket}-second` : mins(data.bucket)} average (players and busiest core show the peak).`;
  if (data.collecting_since) note += ` Collecting since ${whenLabel(data.collecting_since * 1000)}.`;
  $("metrics-note").textContent = note;
  for (const c of Object.values(metricsState.charts)) {
    if (c.spec.id === "disk") {
      const d = c.card.querySelector(".desc") || c.card.insertBefore(el("p", { class: "desc" }), c.card.children[1]);
      d.textContent = disk.desc;
    }
    renderChart(c, data);
    c.card.classList.remove("stale");
    if (c.details.open) renderTable(c.spec, c.details.lastChild);
  }
}

async function loadMetrics(rangeChanged = false) {
  // Dim the old render only when the range changed; background refreshes swap in place.
  if (rangeChanged) for (const c of Object.values(metricsState.charts)) c.card.classList.add("stale");
  try {
    metricsState.data = await api(`/api/metrics?range=${encodeURIComponent(metricsState.range)}`);
    renderMetrics();
  } catch (e) {
    if (e.status === 401 || e.status === 403) return refresh();
    error(`Couldn't load metrics: ${e.message}`);
  }
}

function startMetrics() {
  if (!Object.keys(metricsState.charts).length) {
    const box = $("charts");
    for (const spec of CHARTS) {
      const chart = buildChartCard(spec);
      metricsState.charts[spec.id] = chart;
      box.append(chart.card);
    }
    for (const b of $("ranges").querySelectorAll("button")) {
      b.addEventListener("click", () => {
        metricsState.range = b.dataset.range;
        try { localStorage.setItem("valpanel.range", metricsState.range); } catch (e) { /* storage blocked */ }
        markRange();
        loadMetrics(true);
      });
    }
    let frame = 0;
    metricsState.observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(renderMetrics);
    });
    metricsState.observer.observe(box);
  }
  markRange();
  loadMetrics();
  metricsState.timer = setInterval(() => loadMetrics(), 30000);
}

function markRange() {
  for (const b of $("ranges").querySelectorAll("button")) b.setAttribute("aria-pressed", String(b.dataset.range === metricsState.range));
}

function stopMetrics() {
  clearInterval(metricsState.timer);
  metricsState.timer = null;
}

// --- Server console ---------------------------------------------------------
// Polls /api/log for whatever the server has printed since the last cursor. Lines are kept
// in the DOM and filtered by hiding, which is cheap enough at this volume and means a filter
// can be cleared without re-fetching anything.

const LOG_KEEP = 1500;
let logTimer = null;
let logCursor = 0;
let logPaused = false;

function logEl() { return $("log"); }

function atBottom(box) {
  return box.scrollHeight - box.scrollTop - box.clientHeight < 40;
}

function logMatches(p) {
  const needle = $("log-filter").value.trim().toLowerCase();
  return !needle || p.textContent.toLowerCase().includes(needle);
}

function logAppend(text, at, cls) {
  const box = logEl();
  const stick = atBottom(box);

  const p = document.createElement("p");
  if (cls) p.className = cls;
  if (at) {
    const stamp = document.createElement("span");
    stamp.className = "at";
    stamp.textContent = new Date(at * 1000).toLocaleTimeString();
    p.appendChild(stamp);
  }
  p.appendChild(document.createTextNode(text));
  if (!logMatches(p)) p.classList.add("hidden");
  box.appendChild(p);

  while (box.childElementCount > LOG_KEEP) box.removeChild(box.firstChild);
  if (stick) box.scrollTop = box.scrollHeight;
}

async function pollLog() {
  if (logPaused) return;
  try {
    const data = await api(`/api/log?after=${logCursor}`);
    logCursor = data.next;
    if (data.dropped) logAppend(`… ${data.dropped} earlier line(s) not shown`, 0, "note");
    for (const line of data.lines) {
      const bad = /error|exception|failed|warning/i.test(line.text);
      logAppend(line.text, line.at, bad ? "bad" : "");
    }
  } catch (e) {
    if (e.status === 401 || e.status === 403) return refresh();
    // A console that stops is better than one that fills with its own complaints.
    stopConsole();
    logAppend(`console stopped: ${e.message}`, 0, "note");
  }
}

function startConsole() {
  stopConsole();
  logCursor = 0;
  logEl().textContent = "";
  pollLog();
  logTimer = setInterval(pollLog, 2000);
}

function stopConsole() {
  clearInterval(logTimer);
  logTimer = null;
}

function wireConsole() {
  $("log-filter").addEventListener("input", () => {
    for (const p of logEl().children) p.classList.toggle("hidden", !logMatches(p));
  });
  $("log-pause").addEventListener("click", (e) => {
    logPaused = !logPaused;
    e.target.setAttribute("aria-pressed", String(logPaused));
    e.target.textContent = logPaused ? "Resume" : "Pause";
    // Paused only stops the *display*: the cursor stays put, so resuming catches up
    // rather than skipping whatever happened in between.
    if (!logPaused) pollLog();
  });
  $("log-clear").addEventListener("click", () => { logEl().textContent = ""; });
}

// --- Visitor history --------------------------------------------------------
// One row per Steam ID seen in the window, with every character name it connected under.
// Paging is server-side, so the table only ever holds the page being looked at.

const WINDOW_LABELS = { "24h": "last 24 hours", "7d": "last week", "30d": "last month" };
const visitorState = { window: "24h", page: 1, timer: null };
try { visitorState.window = localStorage.getItem("valpanel.window") || "24h"; } catch (e) { /* storage blocked */ }

// `ago` tops out in hours, which reads badly across a month.
function since(seconds) {
  const s = Math.max(0, Math.round(seconds));
  if (s < 86400) return ago(s);
  return `${Math.floor(s / 86400)}d ${Math.floor((s % 86400) / 3600)}h`;
}

function visitorRow(v, now) {
  const tr = el("tr");

  const who = el("td", {}, el("span", { class: "sid", text: v.steam_id }));
  if (v.online) who.append(el("span", { class: "pill ok live", text: "on now" }));
  tr.append(who);

  const chars = el("td");
  if (!v.names.length) {
    // A connection that dropped before the character line arrived, so there is no name to show.
    chars.append(el("span", { class: "muted", text: "never finished joining" }));
  }
  for (const n of v.names) {
    const chip = el("span", { class: "chip", title: `last seen ${whenLabel(n.last_seen * 1000)}` },
      el("span", { text: n.name }));
    if (n.sessions > 1) chip.append(el("span", { class: "times", text: ` \u00d7${n.sessions}` }));
    chars.append(chip);
  }
  tr.append(chars);

  tr.append(el("td", { text: String(v.sessions) }));
  tr.append(el("td", { title: `${v.seconds} s`, text: v.seconds ? since(v.seconds) : "–" }));
  tr.append(el("td", { title: whenLabel(v.last_seen * 1000), text: `${since(now - v.last_seen)} ago` }));
  return tr;
}

function renderVisitors(data) {
  const box = $("visitors");
  if (!data.rows.length) {
    box.replaceChildren(el("p", { class: "empty", text: `Nobody connected in the ${WINDOW_LABELS[data.window]}.` }));
  } else {
    const table = el("table");
    const head = el("tr");
    for (const h of ["Steam ID", "Characters", "Sessions", "Time on", "Last seen"]) head.append(el("th", { text: h }));
    table.append(el("thead", {}, head));
    const body = el("tbody");
    for (const v of data.rows) body.append(visitorRow(v, data.now));
    table.append(body);
    box.replaceChildren(table);
  }

  $("visitors-page").textContent = data.total
    ? `Page ${data.page} of ${data.pages}`
    : "";
  $("visitors-prev").disabled = data.page <= 1;
  $("visitors-next").disabled = data.page >= data.pages;

  let note = `${data.total} Steam ID${data.total === 1 ? "" : "s"} in the ${WINDOW_LABELS[data.window]}.`;
  if (data.recorded_since) {
    note += ` History starts ${whenLabel(data.recorded_since * 1000)} - anything earlier had already left the journal.`;
  }
  note += " The log never ties a character to a Steam ID, so two people joining in the same second can swap names.";
  $("visitors-note").textContent = note;
}

async function loadVisitors() {
  try {
    const data = await api(`/api/visitors?window=${encodeURIComponent(visitorState.window)}&page=${visitorState.page}`);
    visitorState.page = data.page;  // the server clamps a page past the end
    renderVisitors(data);
  } catch (e) {
    if (e.status === 401 || e.status === 403) return refresh();
    error(`Couldn't load visitors: ${e.message}`);
  }
}

function markWindow() {
  for (const b of $("windows").querySelectorAll("button")) {
    b.setAttribute("aria-pressed", String(b.dataset.window === visitorState.window));
  }
}

function startVisitors() {
  markWindow();
  loadVisitors();
  visitorState.timer = setInterval(loadVisitors, 60000);
}

function stopVisitors() {
  clearInterval(visitorState.timer);
  visitorState.timer = null;
}

function wireVisitors() {
  for (const b of $("windows").querySelectorAll("button")) {
    b.addEventListener("click", () => {
      visitorState.window = b.dataset.window;
      visitorState.page = 1;
      try { localStorage.setItem("valpanel.window", visitorState.window); } catch (e) { /* storage blocked */ }
      markWindow();
      loadVisitors();
    });
  }
  $("visitors-prev").addEventListener("click", () => { visitorState.page = Math.max(1, visitorState.page - 1); loadVisitors(); });
  $("visitors-next").addEventListener("click", () => { visitorState.page += 1; loadVisitors(); });
}
