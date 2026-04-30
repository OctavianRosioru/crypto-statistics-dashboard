// ============================================================
// NEW_STATISTIC — Frontend
// ============================================================

const INITIAL_OPEN_PCT = 0.3;

function fmtPct(n) {
  const sign = n >= 0 ? "+" : "";
  return sign + n.toFixed(2) + "%";
}

// Compute actual gain/loss % from stored prices (no hardcoded ratios needed)
function shotGainPct(sim) {
  if (!sim.openPrice) return 0;
  return Math.abs(sim.takeProfitPrice - sim.openPrice) / sim.openPrice * 100;
}

function shotLossPct(sim) {
  if (!sim.openPrice) return 0;
  return Math.abs(sim.openPrice - sim.stopLossPrice) / sim.openPrice * 100;
}

// --- UI refs ---
const symbolsInput    = document.getElementById("symbolsInput");
const blacklistCheck  = document.getElementById("blacklistCheck");
const distanceInput   = document.getElementById("distanceInput");
const stepInput       = document.getElementById("stepInput");
const horizonSelect   = document.getElementById("horizonSelect");
const showTpCheck     = document.getElementById("showTpCheck");
const showSlCheck     = document.getElementById("showSlCheck");
const showNoneCheck   = document.getElementById("showNoneCheck");
const periodSelect    = document.getElementById("periodSelect");
const minDensityInput = document.getElementById("minDensityInput");
const activityCheck   = document.getElementById("activityModeCheck");
const applyBtn        = document.getElementById("applyBtn");
const statusEl        = document.getElementById("status");
const resultsEl       = document.getElementById("results");

// ============================================================
// Utilities
// ============================================================

function msToUtc(ms) {
  return new Date(ms).toISOString().replace("T", " ").slice(0, 19) + " UTC";
}

function round2(n) { return Math.round(n * 100) / 100; }

function getParams() {
  const periodDays = parseInt(periodSelect.value, 10);
  return {
    symbols:    symbolsInput.value.trim(),
    blacklist:  blacklistCheck.checked,
    distance:   parseFloat(distanceInput.value) || 2,
    step:       parseFloat(stepInput.value)     || 0.5,
    direction:  document.querySelector('input[name="direction"]:checked').value,
    horizon:    parseInt(horizonSelect.value, 10),
    showTp:     showTpCheck.checked,
    showSl:     showSlCheck.checked,
    showNone:   showNoneCheck.checked,
    since:      periodDays > 0 ? Date.now() - periodDays * 86_400_000 : null,
    minDensity: parseFloat(minDensityInput.value) || 0,
    side:       document.querySelector('input[name="side"]:checked').value,
  };
}

function buildQueryString(p) {
  const qs = new URLSearchParams();
  if (p.symbols)        qs.set("symbols",   p.symbols);
  if (p.blacklist)      qs.set("blacklist", "true");
  if (p.since !== null) qs.set("since",     String(Math.round(p.since)));
  return qs.toString() ? "?" + qs.toString() : "";
}

// ============================================================
// Clipboard
// ============================================================

function copyToClipboard(text) {
  navigator.clipboard.writeText(text).then(() => {
    setStatus("Copied: " + text.slice(0, 100) + (text.length > 100 ? "…" : ""));
  });
}

function makeCopyBtn(text, title) {
  const btn = document.createElement("button");
  btn.className = "copy-btn";
  btn.title = title || "Copy symbols";
  btn.textContent = "📋";
  btn.addEventListener("click", (e) => {
    e.stopPropagation();
    copyToClipboard(text);
  });
  return btn;
}

// ============================================================
// Accordion toggle
// ============================================================

function toggleSection(el) {
  el.classList.toggle("open");
}

// ============================================================
// MARKET ACTIVITY MODE
// ============================================================

async function loadActivity(p) {
  setStatus("Loading market activity…");
  const url = "/api/symbols/activity" + buildQueryString(p);
  const data = await fetchJson(url);

  if (!data || data.length === 0) {
    setStatus("No symbols found.");
    resultsEl.innerHTML = "";
    return;
  }

  setStatus(`${data.length} symbols`);

  const allSymbols = data.map(r => r.symbol).join(",");

  const toolbar = document.createElement("div");
  toolbar.className = "table-toolbar";
  const toolbarLabel = document.createElement("span");
  toolbarLabel.textContent = `${data.length} symbols`;
  toolbar.appendChild(toolbarLabel);
  toolbar.appendChild(makeCopyBtn(allSymbols, "Copy all symbols"));

  const table = document.createElement("table");
  table.className = "activity-table";
  table.innerHTML = `
    <thead>
      <tr>
        <th>#</th>
        <th>Symbol</th>
        <th>Shots</th>
        <th>Max diff%</th>
        <th>Avg diff%</th>
        <th>Total quote USDT</th>
        <th>Last shot (UTC)</th>
        <th></th>
      </tr>
    </thead>
    <tbody></tbody>
  `;

  const tbody = table.querySelector("tbody");
  data.forEach((row, i) => {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${i + 1}</td>
      <td><strong>${esc(row.symbol)}</strong></td>
      <td>${row.candleCount}</td>
      <td>${row.maxDiff.toFixed(2)}%</td>
      <td>${row.avgDiff.toFixed(2)}%</td>
      <td>${formatUsdt(row.totalQuoteUsdt)}</td>
      <td style="font-size:0.78rem">${msToUtc(row.lastTriggerMs)}</td>
      <td class="copy-cell"></td>
    `;
    tr.querySelector(".copy-cell").appendChild(makeCopyBtn(row.symbol, "Copy symbol"));
    tbody.appendChild(tr);
  });

  resultsEl.innerHTML = "";
  resultsEl.appendChild(toolbar);
  resultsEl.appendChild(table);
}

// ============================================================
// BUCKET MODE — server-side aggregation
// ============================================================

async function loadBuckets(p) {
  setStatus("Loading…");

  const qs = new URLSearchParams();
  if (p.symbols)        qs.set("symbols",     p.symbols);
  if (p.blacklist)      qs.set("blacklist",   "true");
  if (p.since !== null) qs.set("since",       String(Math.round(p.since)));
  if (p.side !== "all") qs.set("side",        p.side);
  if (p.minDensity > 0) qs.set("minDensity",  String(p.minDensity));
  qs.set("step",    String(p.step));
  qs.set("horizon", String(p.horizon));

  if (p.direction === "from") {
    qs.set("distanceMin", String(p.distance));
  } else {
    qs.set("distanceMax", String(round2(p.distance + p.step)));
  }

  const buckets = await fetchJson("/api/candles/buckets?" + qs.toString());

  if (!buckets || buckets.length === 0) {
    setStatus("No data found.");
    resultsEl.innerHTML = "";
    return;
  }

  const totalShots = buckets.reduce((s, b) => s + b.total, 0);
  const note = [
    p.side !== "all"  ? `side=${p.side}`                       : "",
    p.minDensity > 0  ? `min ≥ ${formatUsdt(p.minDensity)} USDT` : "",
  ].filter(Boolean).join(" · ");

  setStatus(
    `${totalShots} shots · ${buckets.length} buckets · step=${p.step}% horizon=${p.horizon}s` +
    (note ? ` · ${note}` : "")
  );

  renderBuckets(buckets, p);
}

// -------------------------------------------------------
// Render buckets (date agregate server-side)
// -------------------------------------------------------
function renderBuckets(buckets, p) {
  resultsEl.innerHTML = "";
  const container = document.createElement("div");
  container.className = "buckets-container";

  for (const b of buckets) {
    const { offsetPct, total, tp, sl, none, tpPnl, slPnl, netPnl, symbols } = b;
    if (total === 0) continue;

    const tpEach   = tp > 0 ? (tpPnl / tp).toFixed(2) : "0.00";
    const slEach   = sl > 0 ? (slPnl / sl).toFixed(2) : "0.00";
    const pnlClass = netPnl > 0 ? "badge-net-pos" : netPnl < 0 ? "badge-net-neg" : "badge-net-zero";

    const group = document.createElement("div");
    group.className = "bucket-group";

    const header = document.createElement("div");
    header.className = "bucket-header";
    header.innerHTML = `
      <span class="bucket-title">${offsetPct.toFixed(2)}% offset</span>
      <span class="bucket-summary">
        <span class="badge badge-total">${total} shots</span>
        ${tp   ? `<span class="badge badge-tp"   title="+${tpEach}% each">TP ${tp} → +${tpPnl.toFixed(2)}%</span>` : ""}
        ${sl   ? `<span class="badge badge-sl"   title="-${slEach}% each">SL ${sl} → -${slPnl.toFixed(2)}%</span>` : ""}
        ${none ? `<span class="badge badge-none">— ${none}</span>` : ""}
        <span class="badge ${pnlClass}">P&amp;L ${fmtPct(netPnl)}</span>
      </span>
      <span class="bucket-chevron">▼</span>
    `;
    header.addEventListener("click", () => toggleSection(group));
    group.appendChild(header);

    const body = document.createElement("div");
    body.className = "bucket-body";

    const netSummary = buildNetSummaryFromData(symbols);
    if (netSummary.children.length > 0) body.appendChild(netSummary);

    const sections = document.createElement("div");
    sections.className = "outcome-sections";

    if (p.showTp && tp > 0)
      sections.appendChild(buildOutcomeSectionFromData("tp",   "TP",   tp,   symbols, b, p));
    if (p.showSl && sl > 0)
      sections.appendChild(buildOutcomeSectionFromData("sl",   "SL",   sl,   symbols, b, p));
    if (p.showNone && none > 0)
      sections.appendChild(buildOutcomeSectionFromData("none", "None", none, symbols, b, p));

    body.appendChild(sections);
    group.appendChild(body);
    container.appendChild(group);
  }

  resultsEl.appendChild(container);
}

// -------------------------------------------------------
// Net summary — profitable / losing symbols
// -------------------------------------------------------
function buildNetSummaryFromData(symbols) {
  const profitable = symbols.filter(x => x.netPnl > 0).sort((a, b) => b.netPnl - a.netPnl);
  const losing     = symbols.filter(x => x.netPnl < 0).sort((a, b) => a.netPnl - b.netPnl);

  const el = document.createElement("div");
  el.className = "net-summary";

  if (profitable.length > 0) {
    const row = document.createElement("div");
    row.className = "net-group net-profitable";
    const inner = document.createElement("span");
    inner.className = "net-symbols";
    inner.innerHTML =
      `<span class="net-label net-label-pos">Profitable (${profitable.length})</span> ` +
      profitable.map(x =>
        `<span class="net-sym">${esc(x.symbol)} <small>TP:${x.tp}/SL:${x.sl} ${fmtPct(x.netPnl)}</small></span>`
      ).join(" ");
    row.appendChild(inner);
    row.appendChild(makeCopyBtn(profitable.map(x => x.symbol).join(","), "Copy profitable symbols"));
    el.appendChild(row);
  }

  if (losing.length > 0) {
    const row = document.createElement("div");
    row.className = "net-group net-losing";
    const inner = document.createElement("span");
    inner.className = "net-symbols";
    inner.innerHTML =
      `<span class="net-label net-label-neg">Loss (${losing.length})</span> ` +
      losing.map(x =>
        `<span class="net-sym">${esc(x.symbol)} <small>TP:${x.tp}/SL:${x.sl} ${fmtPct(x.netPnl)}</small></span>`
      ).join(" ");
    row.appendChild(inner);
    row.appendChild(makeCopyBtn(losing.map(x => x.symbol).join(","), "Copy loss symbols"));
    el.appendChild(row);
  }

  return el;
}

// -------------------------------------------------------
// Outcome section — TP / SL / None cu shots lazy per simbol
// -------------------------------------------------------
function buildOutcomeSectionFromData(type, label, count, allSymbols, bucket, p) {
  const section = document.createElement("div");
  section.className = "outcome-section";

  const headerClass = type === "tp" ? "tp-header" : type === "sl" ? "sl-header" : "none-header";
  const badgeClass  = type === "tp" ? "badge-tp"  : type === "sl" ? "badge-sl"  : "badge-none";

  const header = document.createElement("div");
  header.className = `outcome-header ${headerClass}`;
  header.innerHTML = `
    <span class="badge ${badgeClass}">${label}</span>
    <span>${count} shot${count !== 1 ? "s" : ""}</span>
    <span class="outcome-chevron">▼</span>
  `;
  header.addEventListener("click", () => toggleSection(section));
  section.appendChild(header);

  const body = document.createElement("div");
  body.className = "outcome-body";

  const symbolList = document.createElement("div");
  symbolList.className = "symbol-list";

  const filteredSyms = allSymbols
    .filter(x => (type === "tp" ? x.tp : type === "sl" ? x.sl : x.none) > 0)
    .sort((a, b) => {
      const ca = type === "tp" ? a.tp : type === "sl" ? a.sl : a.none;
      const cb = type === "tp" ? b.tp : type === "sl" ? b.sl : b.none;
      return cb - ca;
    });

  for (const sym of filteredSyms) {
    const cnt = type === "tp" ? sym.tp : type === "sl" ? sym.sl : sym.none;

    const symRow = document.createElement("div");
    symRow.className = "symbol-row";

    const nameSpan = document.createElement("span");
    nameSpan.className = "symbol-name";
    nameSpan.textContent = sym.symbol;

    const cntSpan = document.createElement("span");
    cntSpan.className = "symbol-count";
    cntSpan.textContent = `${label} × ${cnt}`;

    const shotsToggle = document.createElement("span");
    shotsToggle.className = "shots-toggle-sym";
    shotsToggle.textContent = "▶ shots";
    shotsToggle.title = "Show individual shots";

    symRow.appendChild(nameSpan);
    symRow.appendChild(cntSpan);
    symRow.appendChild(shotsToggle);
    symbolList.appendChild(symRow);

    const shotsContainer = document.createElement("div");
    shotsContainer.className = "shots-list shots-sym";
    shotsContainer.style.display = "none";
    symbolList.appendChild(shotsContainer);

    let loaded = false;
    shotsToggle.addEventListener("click", async (e) => {
      e.stopPropagation();
      const open = shotsContainer.style.display === "none";
      shotsContainer.style.display = open ? "block" : "none";
      shotsToggle.textContent       = open ? "▼ shots" : "▶ shots";
      if (open && !loaded) {
        loaded = true;
        shotsContainer.textContent = "Loading…";
        try {
          const shots = await fetchShotsForSymbol(sym.symbol, bucket.offsetPct, p);
          renderShotsInline(shotsContainer, shots, type);
        } catch (ex) {
          shotsContainer.textContent = "Error: " + (ex?.message ?? String(ex));
        }
      }
    });
  }

  body.appendChild(symbolList);
  section.appendChild(body);

  return section;
}

// -------------------------------------------------------
// Shots lazy — fetch + render
// -------------------------------------------------------
async function fetchShotsForSymbol(symbol, offsetPct, p) {
  const qs = new URLSearchParams();
  qs.set("symbol",    symbol);
  qs.set("offsetPct", String(offsetPct));
  qs.set("horizon",   String(p.horizon));
  if (p.since !== null) qs.set("since",      String(Math.round(p.since)));
  if (p.side !== "all") qs.set("side",       p.side);
  if (p.minDensity > 0) qs.set("minDensity", String(p.minDensity));
  if (p.direction !== "from") qs.set("distanceMax", String(round2(p.distance + p.step)));
  return fetchJson("/api/candles/shots?" + qs.toString());
}

function renderShotsInline(container, shots, type) {
  container.innerHTML = "";
  const outcomeCode = type === "tp" ? 1 : type === "sl" ? 2 : 0;
  const filtered    = shots.filter(s => s.outcome === outcomeCode);

  if (filtered.length === 0) {
    container.textContent = "No shots.";
    return;
  }

  for (const shot of filtered) {
    const row = document.createElement("div");
    row.className = "shot-detail";
    const sideClass = shot.side === "Buy" ? "shot-side-buy" : "shot-side-sell";
    const pnlSign   = type === "tp" ? "+" : type === "sl" ? "-" : "";
    const slipNote  = shot.slipPct > 0.0001
      ? ` <span class="shot-slip">slip +${shot.slipPct.toFixed(3)}%</span>` : "";

    row.innerHTML = `
      <span class="${sideClass}">${esc(shot.side)}</span>
      <span class="shot-diff">${shot.diffPct.toFixed(2)}%</span>
      <span class="shot-quote">${formatUsdt(shot.totalQuoteUsdt)} USDT</span>
      <span class="shot-time">${msToUtc(shot.triggerTimeMs)}</span>
      <span class="shot-outcome ${type}">${pnlSign}${shot.pnlPct.toFixed(3)}%${slipNote}</span>
    `;
    container.appendChild(row);
  }
}

// ============================================================
// Helpers
// ============================================================

async function fetchJson(url) {
  const res = await fetch(url, { headers: { Accept: "application/json" } });
  if (!res.ok) throw new Error(`HTTP ${res.status} — ${url}`);
  return res.json();
}

function esc(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function formatUsdt(n) {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(2) + "M";
  if (n >= 1_000)     return (n / 1_000).toFixed(1) + "k";
  return n.toFixed(0);
}

function setStatus(msg) { statusEl.textContent = msg; }

// ============================================================
// Entry point
// ============================================================

applyBtn.addEventListener("click", async () => {
  const p = getParams();
  resultsEl.innerHTML = "";
  try {
    if (activityCheck.checked) {
      await loadActivity(p);
    } else {
      await loadBuckets(p);
    }
  } catch (e) {
    setStatus("Error: " + (e?.message ?? String(e)));
  }
});
