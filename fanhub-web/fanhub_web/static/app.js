/* MKS Fan Hub — общий каркас: вкладки, WebSocket, мониторинг, журнал.
   Модули вкладок (curves/binds/control/console) подключаются ниже и читают
   window.FanHub. */
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

  const listeners = [];
  let lastStatus = null;   // {hub, host_temps}
  let lastLog = [];

  function onStatus(fn) { listeners.push(fn); }
  function emit() { for (const fn of listeners) fn(lastStatus); }

  // ---------- вкладки ----------
  document.querySelectorAll("#tabs button").forEach((b) => {
    b.addEventListener("click", () => {
      document.querySelectorAll("#tabs button").forEach((x) => x.classList.toggle("active", x === b));
      document.querySelectorAll(".tab-pane").forEach((p) => p.classList.toggle("active", p.id === "tab-" + b.dataset.tab));
      document.dispatchEvent(new CustomEvent("tabchange", { detail: b.dataset.tab }));
    });
  });

  // ---------- рендер мониторинга ----------
  function renderHeader(hub) {
    const link = $("link");
    if (hub.link.connected) {
      link.className = "chip ok";
      link.textContent = "плата: " + hub.link.port;
    } else {
      link.className = "chip bad";
      link.textContent = "нет связи";
    }
    $("mode").textContent = "режим: " + hub.mode;
    const wd = $("watchdog");
    if (hub.watchdog === "TRIPPED") {
      wd.style.display = "";
      wd.className = "chip bad";
      wd.textContent = "FAILSAFE — плата на полных оборотах, нет температур от ПК";
    } else {
      wd.style.display = "none";
    }
  }

  function renderHostTemps(temps) {
    $("temp-cards").innerHTML = Object.entries(temps).map(([key, t]) => {
      const cls = t.value === null ? "lost" : (t.age > 5 ? "stale" : "");
      const val = t.value === null
        ? "—"
        : `${t.value.toFixed(1)}<small>°C</small>`;
      const stale = t.value !== null && t.age > 5 ? ` <small>(+${Math.round(t.age)}с)</small>` : "";
      return `<div class="card ${cls}"><div class="label">${esc(t.label)}${stale}</div>` +
             `<div class="value">${val}</div></div>`;
    }).join("");
  }

  function renderTherms(therms) {
    const tb = document.querySelector("#therms tbody");
    tb.innerHTML = therms.map((t) => {
      const v = t.value === null ? "—" : t.value.toFixed(1) + " °C";
      return `<tr><td>${esc(t.name)}</td><td>${v}</td>` +
             `<td class="state-${esc(t.state)}">${esc(t.state)}</td></tr>`;
    }).join("");
  }

  function renderFans(channels) {
    const tb = document.querySelector("#fans tbody");
    tb.innerHTML = channels.map((c) => {
      const src = c.source_type === "THERM"
        ? "терм. " + esc(c.source_name)
        : "ПК: " + esc(c.source_name);
      const spin = c.spin_state
        ? `<span class="state-${esc(c.spin_state)}">${esc(c.spin_state)}${c.spin_state === "ERR" ? " (" + c.spin_tries + ")" : ""}</span>`
        : "";
      const flags = [
        c.locked ? "закреплён" : "",
        c.active ? "" : "не активен",
      ].filter(Boolean).join(", ");
      return `<tr><td>${esc(c.label)}${flags ? ` <span class="hint">(${flags})</span>` : ""}</td>` +
             `<td class="out">${c.pct}%</td><td>${src}</td>` +
             `<td class="state-${esc(c.tach_state)}">${esc(c.tach_state)}</td>` +
             `<td class="rpm">${c.rpm}</td><td>${spin}</td></tr>`;
    }).join("");
  }

  async function loadLog() {
    try {
      const r = await fetch("/api/status");
      const d = await r.json();
      lastLog = d.log;
      $("log").innerHTML = d.log.map((e) =>
        `<div><span class="t">${e.t}</span> <span class="${e.kind}">${esc(e.text)}</span></div>`
      ).join("");
      $("log").scrollTop = $("log").scrollHeight;
    } catch (e) { /* сервер ещё не поднялся */ }
  }

  // ---------- WebSocket ----------
  function connectWs() {
    const ws = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws");
    ws.onmessage = (ev) => {
      const d = JSON.parse(ev.data);
      lastStatus = d;
      renderHeader(d.hub);
      renderHostTemps(d.host_temps);
      renderTherms(d.hub.thermistors);
      renderFans(d.hub.channels);
      emit();
    };
    ws.onclose = () => setTimeout(connectWs, 2000);
  }

  async function api(path, body) {
    const r = await fetch(path, body === undefined ? undefined : {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(d.error || ("HTTP " + r.status));
    return d;
  }

  window.FanHub = { $, esc, api, onStatus, getStatus: () => lastStatus, getLog: () => lastLog };

  connectWs();
  loadLog();
  setInterval(loadLog, 5000);
})();
