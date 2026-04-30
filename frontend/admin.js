"use strict";
// Pagina admin folosește Basic Auth — browser-ul cere user/parolă o singură dată,
// apoi reutilizează automat pentru toate cererile către același origin.

const $ = (id) => document.getElementById(id);
const status = $("status");

function setStatus(text, isError = false) {
  status.textContent = text;
  status.style.color = isError ? "#b91c1c" : "#374151";
}

async function api(path, method = "GET", body = null) {
  const opts = { method, headers: { "Content-Type": "application/json" } };
  if (body !== null) opts.body = JSON.stringify(body);
  const r = await fetch(path, opts);
  if (r.status === 401) {
    // browser-ul ar fi cerut deja credentials. Dacă tot suntem 401 — credențiale greșite.
    throw new Error("Unauthorized");
  }
  if (!r.ok) {
    const err = await r.text().catch(() => "");
    throw new Error(`${r.status}: ${err || r.statusText}`);
  }
  if (r.status === 204) return null;
  const ct = r.headers.get("content-type") || "";
  return ct.includes("application/json") ? r.json() : r.text();
}

// ────────────────────────────────────────────────────────────────────
// Listă canale
// ────────────────────────────────────────────────────────────────────
async function loadChannels() {
  setStatus("Loading…");
  try {
    const channels = await api("/api/admin/telegram/channels");
    renderChannels(channels);
    setStatus(`${channels.length} channel(s) loaded.`);
  } catch (e) {
    setStatus(`Load failed: ${e.message}`, true);
  }
}

function renderChannels(channels) {
  const root = $("channels");
  root.innerHTML = "";
  if (channels.length === 0) {
    root.innerHTML = `<p class="status">No channels yet. Click <b>+ Add channel</b> to create one.</p>`;
    return;
  }
  for (const ch of channels) {
    const card = document.createElement("div");
    card.className = "channel-card" + (ch.enabled ? "" : " disabled");
    const modeBadge = `<span class="badge ${ch.mode === "Trigger" ? "trigger" : "statistic"}">${ch.mode}</span>`;
    const enabledBadge = ch.enabled ? "" : `<span class="badge disabled">disabled</span>`;
    let meta = "";
    if (ch.mode === "Trigger" && ch.trigger) {
      const t = ch.trigger;
      const range = t.distanceMax > 0 ? `${t.distanceMin}–${t.distanceMax}%` : `≥${t.distanceMin}%`;
      const syms = t.symbols && t.symbols.length ? t.symbols.join(", ") : "all";
      meta = `<code>${t.exchange}</code> · <code>${t.side}</code> · ${range} · ≥${t.minShots} in ${t.windowSeconds}s · TP≤${t.maxTpAgeMs}ms · cd ${t.cooldownSeconds}s · syms: ${syms}`;
    } else if (ch.mode === "Statistic" && ch.statistic) {
      const s = ch.statistic;
      const range = `${(s.skip ?? 0) + 1}–${(s.skip ?? 0) + (s.topN ?? 20)}`;
      const syms = (s.symbols && s.symbols.length) ? `whitelist: ${s.symbols.length}` : "all symbols";
      meta = `<code>${s.category}</code> · period ${s.periodHours}h · every ${frequencyLabel(s.frequencyHours)} · range ${range} · horizon ${s.horizonSec}s · ${syms}`;
    }
    card.innerHTML = `
      <div class="head">
        <div>${modeBadge}${enabledBadge}<h3 style="display:inline">${escapeHtml(ch.name)}</h3>
          <span class="muted" style="margin-left:.4rem">→ ${escapeHtml(ch.chatId)}</span>
        </div>
      </div>
      <div class="meta">${meta}</div>
      <div class="actions">
        <button data-act="edit">Edit</button>
        <button data-act="test">Test</button>
        ${ch.mode === "Statistic" ? `<button data-act="run">Run now</button>` : ""}
        <button data-act="delete" class="danger">Delete</button>
      </div>
    `;
    card.querySelector('[data-act="edit"]').onclick = () => openEdit(ch);
    card.querySelector('[data-act="test"]').onclick = () => testChannel(ch.id);
    const runBtn = card.querySelector('[data-act="run"]');
    if (runBtn) runBtn.onclick = () => runNow(ch.id);
    card.querySelector('[data-act="delete"]').onclick = () => deleteChannel(ch);
    root.appendChild(card);
  }
}

function frequencyLabel(hours) {
  return Number(hours) === 0 ? "30s test" : `${hours ?? 1}h`;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}

// ────────────────────────────────────────────────────────────────────
// Modal
// ────────────────────────────────────────────────────────────────────
const modal = $("modal");

function openAdd() {
  $("modalTitle").textContent = "Add channel";
  $("f_id").value = "";
  $("f_name").value = "";
  $("f_chatId").value = "";
  $("f_enabled").checked = true;
  $("m_trigger").checked = true;
  resetTriggerForm();
  resetStatForm();
  toggleMode();
  modal.classList.remove("hidden");
}

function openEdit(ch) {
  $("modalTitle").textContent = "Edit channel";
  $("f_id").value = ch.id;
  $("f_name").value = ch.name;
  $("f_chatId").value = ch.chatId;
  $("f_enabled").checked = !!ch.enabled;
  if (ch.mode === "Statistic") $("m_statistic").checked = true; else $("m_trigger").checked = true;
  if (ch.trigger) fillTriggerForm(ch.trigger); else resetTriggerForm();
  if (ch.statistic) fillStatForm(ch.statistic); else resetStatForm();
  toggleMode();
  modal.classList.remove("hidden");
}

function resetTriggerForm() {
  $("t_exchange").value = "*";
  $("t_symbols").value = "";
  $("t_side").value = "Any";
  $("t_dmin").value = "0.5";
  $("t_dmax").value = "0";
  $("t_minTp").value = "2";
  $("t_window").value = "60";
  $("t_posNet").checked = true;
  $("t_maxTpAge").value = "1500";
  $("t_cooldown").value = "30";
  $("t_msg").value = "#{exchange} #{symbol} {side} #{distance}";
}

function fillTriggerForm(t) {
  $("t_exchange").value = t.exchange || "*";
  $("t_symbols").value = (t.symbols || []).join(", ");
  $("t_side").value = t.side || "Any";
  $("t_dmin").value = t.distanceMin ?? 0;
  $("t_dmax").value = t.distanceMax ?? 0;
  $("t_minTp").value = t.minTpCount ?? 2;
  $("t_window").value = t.windowSeconds ?? 60;
  $("t_posNet").checked = t.requirePositiveNet !== false;
  $("t_maxTpAge").value = t.maxTpAgeMs ?? 1500;
  $("t_cooldown").value = t.cooldownSeconds ?? 30;
  $("t_msg").value = t.messageFormat || "#{exchange} #{symbol} {side} #{distance}";
}

function resetStatForm() {
  $("s_period").value = "24";
  $("s_freq").value = "1";
  $("s_skip").value = "0";
  $("s_topN").value = "20";
  $("s_category").value = "Profitable";
  $("s_side").value = "Any";
  $("s_dmin").value = "0";
  $("s_dmax").value = "0";
  $("s_minQ").value = "0";
  $("s_horizon").value = "300";
  $("s_symbols").value = "";
  $("s_msg").value = "#{symbol} {exchange} {side}";
  $("s_delay").value = "1000";
  updateRangePreview();
}

function fillStatForm(s) {
  $("s_period").value = String(s.periodHours ?? 24);
  $("s_freq").value = String(s.frequencyHours ?? 1);
  $("s_skip").value = s.skip ?? 0;
  $("s_topN").value = s.topN ?? 20;
  $("s_category").value = s.category || "Profitable";
  $("s_side").value = s.side || "Any";
  $("s_dmin").value = s.distanceMin ?? 0;
  $("s_dmax").value = s.distanceMax ?? 0;
  $("s_minQ").value = s.minQuoteUsdt ?? 0;
  $("s_horizon").value = String(s.horizonSec ?? 300);
  $("s_symbols").value = (s.symbols || []).join(", ");
  $("s_msg").value = s.messageFormat || "#{symbol} {exchange} {side}";
  $("s_delay").value = s.delayBetweenMessagesMs ?? 1000;
  updateRangePreview();
}

function updateRangePreview() {
  const skip = Number($("s_skip").value) || 0;
  const top  = Number($("s_topN").value) || 0;
  const el   = $("s_rangePreview");
  if (el) el.textContent = top > 0 ? `→ poziții ${skip + 1}–${skip + top}` : "";
}
document.addEventListener("input", (e) => {
  if (e.target && (e.target.id === "s_skip" || e.target.id === "s_topN")) updateRangePreview();
});

function toggleMode() {
  const isTrigger = $("m_trigger").checked;
  $("triggerBlock").style.display = isTrigger ? "" : "none";
  $("statBlock").style.display = isTrigger ? "none" : "";
}

document.querySelectorAll('input[name="mode"]').forEach(r => r.onchange = toggleMode);

function readForm() {
  const isTrigger = $("m_trigger").checked;
  const ch = {
    id: $("f_id").value || undefined,
    name: $("f_name").value.trim(),
    chatId: $("f_chatId").value.trim(),
    enabled: $("f_enabled").checked,
    mode: isTrigger ? "Trigger" : "Statistic",
    trigger: null,
    statistic: null,
  };
  if (isTrigger) {
    ch.trigger = {
      exchange: $("t_exchange").value,
      symbols: $("t_symbols").value.split(",").map(s => s.trim().toUpperCase()).filter(Boolean),
      side: $("t_side").value,
      distanceMin: Number($("t_dmin").value) || 0,
      distanceMax: Number($("t_dmax").value) || 0,
      minTpCount: Number($("t_minTp").value) || 1,
      windowSeconds: Number($("t_window").value) || 60,
      requirePositiveNet: $("t_posNet").checked,
      maxTpAgeMs: Number($("t_maxTpAge").value) || 1500,
      cooldownSeconds: Number($("t_cooldown").value) || 0,
      messageFormat: $("t_msg").value || "#{exchange} #{symbol} {side} #{distance}",
    };
  } else {
    ch.statistic = {
      periodHours: Number($("s_period").value) || 24,
      frequencyHours: Number($("s_freq").value),
      skip: Number($("s_skip").value) || 0,
      topN: Number($("s_topN").value) || 20,
      category: $("s_category").value,
      side: $("s_side").value,
      distanceMin: Number($("s_dmin").value) || 0,
      distanceMax: Number($("s_dmax").value) || 0,
      minQuoteUsdt: Number($("s_minQ").value) || 0,
      horizonSec: Number($("s_horizon").value) || 300,
      symbols: $("s_symbols").value.split(",").map(s => s.trim().toUpperCase()).filter(Boolean),
      messageFormat: $("s_msg").value || "#{symbol} {exchange} {side}",
      delayBetweenMessagesMs: Number($("s_delay").value) || 0,
    };
  }
  return ch;
}

$("form").onsubmit = async (e) => {
  e.preventDefault();
  const ch = readForm();
  if (!ch.name || !ch.chatId) { setStatus("Name and Chat ID required", true); return; }
  try {
    if (ch.id) {
      await api(`/api/admin/telegram/channels/${ch.id}`, "PUT", ch);
      setStatus("Channel updated.");
    } else {
      delete ch.id;
      await api("/api/admin/telegram/channels", "POST", ch);
      setStatus("Channel created.");
    }
    modal.classList.add("hidden");
    await loadChannels();
  } catch (e) {
    setStatus(`Save failed: ${e.message}`, true);
  }
};

$("cancelBtn").onclick = () => modal.classList.add("hidden");
$("addBtn").onclick = openAdd;
$("reloadBtn").onclick = loadChannels;

async function testChannel(id) {
  try {
    const r = await api(`/api/admin/telegram/channels/${id}/test`, "POST");
    setStatus(r.sent ? "Test message sent." : "Test failed (check Worker logs).", !r.sent);
  } catch (e) { setStatus(`Test failed: ${e.message}`, true); }
}

async function runNow(id) {
  try {
    const r = await api(`/api/admin/telegram/channels/${id}/run-now`, "POST");
    setStatus(r.ranNow ? "Statistic report sent." : "Run-now failed.", !r.ranNow);
  } catch (e) { setStatus(`Run-now failed: ${e.message}`, true); }
}

async function deleteChannel(ch) {
  if (!confirm(`Delete channel "${ch.name}"?`)) return;
  try {
    await api(`/api/admin/telegram/channels/${ch.id}`, "DELETE");
    setStatus("Channel deleted.");
    await loadChannels();
  } catch (e) { setStatus(`Delete failed: ${e.message}`, true); }
}

loadChannels();
