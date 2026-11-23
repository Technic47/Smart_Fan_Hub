/* MKS Fan Hub — главная страница: вкладки, WebSocket, таблица каналов
   с чекбоксом «Подключён», кликабельной привязкой, иконками кривых,
   фильтром неактивных и переименованием каналов. */
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

  const listeners = [];
  let lastStatus = null;
  let disabledChannels = [];
  let disabledTherms = [];
  let curvesCache = {};
  let curvesStamp = 0;
  let bindEditCh = null;      // канал, у которого открыт список привязки
  let nameEditCh = null;      // канал, у которого открыто переименование
  let hideOff = localStorage.getItem("fanhub.hideOff") === "1";
  let hideThermOff = localStorage.getItem("fanhub.hideThermOff") === "1";

  function onStatus(fn) { listeners.push(fn); }
  function emit() { for (const fn of listeners) fn(lastStatus); }

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

  // ---------- вкладки ----------
  document.querySelectorAll("#tabs button").forEach((b) => {
    b.addEventListener("click", () => {
      document.querySelectorAll("#tabs button").forEach((x) => x.classList.toggle("active", x === b));
      document.querySelectorAll(".tab-pane").forEach((p) => p.classList.toggle("active", p.id === "tab-" + b.dataset.tab));
      document.dispatchEvent(new CustomEvent("tabchange", { detail: b.dataset.tab }));
    });
  });

  // ---------- кривые (кэш для спарклайнов) ----------
  async function refreshCurves() {
    try {
      const d = await api("/api/curves");
      curvesCache = d.curves;
      curvesStamp = Date.now();
      renderFans(lastStatus ? lastStatus.hub.channels : []);
    } catch (e) { /* плата офлайн */ }
  }
  setInterval(() => { if (Date.now() - curvesStamp > 30000) refreshCurves(); }, 30000);

  function sparkline(name) {
    const pts = curvesCache[name];
    if (!pts) return `<svg viewBox="0 0 44 26" width="44" height="26" class="spark"><rect width="44" height="26" fill="none"/></svg>`;
    let maxT = 60;
    for (const p of pts) if (p[0] > maxT) maxT = Math.ceil(p[0] / 10) * 10;
    const xy = pts.map(([t, p]) =>
      `${(4 + (t / maxT) * 36).toFixed(1)},${(22 - (p / 100) * 20).toFixed(1)}`).join(" ");
    return `<svg viewBox="0 0 44 26" width="44" height="26" class="spark" title="Редактировать кривую">` +
           `<polyline points="${xy}" fill="none" stroke="#4aa3ff" stroke-width="1.6"/>` +
           pts.map(([t, p]) => `<circle cx="${(4 + (t / maxT) * 36).toFixed(1)}" cy="${(22 - (p / 100) * 20).toFixed(1)}" r="1.4" fill="#4aa3ff"/>`).join("") +
           `</svg>`;
  }

  // ---------- источники для привязки ----------
  function sourceOptions(curType, curName) {
    const opts = [];
    const therms = lastStatus ? (lastStatus.hub.thermistors || []).map((t) => t.name) : [];
    for (const t of therms) opts.push({ v: "THERM:" + t, label: "терм. " + t });
    const serials = lastStatus ? (lastStatus.serial_display_names || {}) : {};
    for (const [k, label] of Object.entries(serials)) opts.push({ v: "SERIAL:" + k, label: "ПК: " + label });
    return opts.map((o) => {
      const sel = o.v === curType + ":" + curName ? "selected" : "";
      return `<option value="${o.v}" ${sel}>${esc(o.label)}</option>`;
    }).join("");
  }

  // ---------- рендер ----------
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
      const val = t.value === null ? "—" : `${t.value.toFixed(1)}<small>°C</small>`;
      const stale = t.value !== null && t.age > 5 ? ` <small>(+${Math.round(t.age)}с)</small>` : "";
      return `<div class="card ${cls}"><div class="label">${esc(t.label)}${stale}</div>` +
             `<div class="value">${val}</div></div>`;
    }).join("");
  }

  function renderTherms(therms) {
    const visible = hideThermOff ? therms.filter((t) => !disabledTherms.includes(t.name)) : therms;
    document.querySelector("#therms tbody").innerHTML = visible.map((t) => {
      const off = disabledTherms.includes(t.name);
      const v = t.value === null ? "—" : t.value.toFixed(1) + " °C";
      return `<tr data-th="${t.name}" class="${off ? "disabled-row" : ""}">
        <td>${esc(t.name)}</td><td>${v}</td>
        <td class="state-${esc(t.state)}">${esc(t.state)}</td>
        <td class="chk"><input type="checkbox" class="th-conn" ${off ? "" : "checked"} title="Подключён ли термистор"></td>
      </tr>`;
    }).join("");
    document.querySelectorAll("#therms input.th-conn").forEach((cb) => {
      cb.addEventListener("change", async () => {
        const tr = cb.closest("tr");
        try {
          const d = await api("/api/therm", { therm: tr.dataset.th, enabled: cb.checked });
          disabledTherms = d.disabled;
          if (hideThermOff && !cb.checked) renderTherms(lastStatus.hub.thermistors);
          else tr.classList.toggle("disabled-row", !cb.checked);
        } catch (e) {
          cb.checked = !cb.checked;
          alert("Не сохранилось: " + e.message);
        }
      });
    });
  }

  function bindLabel(c) {
    if (c.source_type === "THERM") return "терм. " + c.source_name;
    const names = lastStatus ? lastStatus.serial_display_names || {} : {};
    return "ПК: " + (names[c.source_name] || c.source_name);
  }

  function groupLabel(c) {
    const g = lastStatus && lastStatus.curve_groups ? lastStatus.curve_groups[c.name] : null;
    return g ? "гр. " + g : "";
  }

  function renderFans(channels) {
    if (bindEditCh || nameEditCh) return;   // не рвём открытое редактирование
    const visible = hideOff ? channels.filter((c) => !disabledChannels.includes(c.name)) : channels;
    const tb = document.querySelector("#fans tbody");
    tb.innerHTML = visible.map((c) => {
      const off = disabledChannels.includes(c.name);
      const spin = c.spin_state
        ? `<span class="state-${esc(c.spin_state)}">${esc(c.spin_state)}${c.spin_state === "ERR" ? " (" + c.spin_tries + ")" : ""}</span>`
        : "";
      const flags = [c.locked ? "закреплён" : "", c.active ? "" : "не активен"].filter(Boolean).join(", ");
      return `<tr data-ch="${c.name}" class="${off ? "disabled-row" : ""}">
        <td><span class="ch-name" title="Нажми, чтобы переименовать">${esc(c.label)}</span>${flags ? ` <span class="hint">(${flags})</span>` : ""}</td>
        <td class="out">${c.pct}%</td>
        <td class="state-${esc(c.tach_state)}">${esc(c.tach_state)}</td>
        <td class="rpm">${c.rpm}</td>
        <td>${spin}</td>
        <td class="chk"><input type="checkbox" class="conn" ${off ? "" : "checked"} title="Подключён ли вентилятор"></td>
        <td class="bind-cell" title="Нажми, чтобы перепривязать"><span class="bind-text">${esc(bindLabel(c))}</span></td>
        <td class="curve-cell" title="Редактировать кривую">${sparkline(c.name)} <span class="hint group-label">${esc(groupLabel(c))}</span></td>
      </tr>`;
    }).join("");

    // чекбокс «Подключён»
    tb.querySelectorAll("input.conn").forEach((cb) => {
      cb.addEventListener("change", async () => {
        const tr = cb.closest("tr");
        try {
          const d = await api("/api/channel", { channel: tr.dataset.ch, enabled: cb.checked });
          disabledChannels = d.disabled;
          if (hideOff && !cb.checked) renderFans(lastStatus.hub.channels);
          else tr.classList.toggle("disabled-row", !cb.checked);
        } catch (e) {
          cb.checked = !cb.checked;
          alert("Не сохранилось: " + e.message);
        }
      });
    });

    // переименование канала
    tb.querySelectorAll(".ch-name").forEach((span) => {
      span.addEventListener("click", () => {
        const tr = span.closest("tr");
        const name = tr.dataset.ch;
        const old = span.textContent;
        nameEditCh = name;
        const td = span.parentElement;
        td.innerHTML = `<input type="text" class="name-inp" value="${esc(old)}" maxlength="40" style="width:150px">`;
        const inp = td.querySelector("input");
        inp.focus(); inp.select();
        const finish = async (commit) => {
          if (nameEditCh !== name) return;
          const label = inp.value.trim();
          nameEditCh = null;
          if (commit && label && label !== old) {
            try {
              await api("/api/channel-name", { channel: name, label });
            } catch (e) {
              alert("Не сохранилось: " + e.message);
            }
          }
          renderFans(lastStatus ? lastStatus.hub.channels : []);
        };
        inp.addEventListener("keydown", (e) => {
          if (e.key === "Enter") finish(true);
          else if (e.key === "Escape") finish(false);
          e.stopPropagation();
        });
        inp.addEventListener("blur", () => finish(true));
      });
    });

    // клик по привязке -> выбор
    tb.querySelectorAll(".bind-cell").forEach((cell) => {
      cell.addEventListener("click", () => {
        if (cell.querySelector("select")) return;
        const tr = cell.closest("tr");
        const name = tr.dataset.ch;
        const c = channels.find((x) => x.name === name);
        if (!c) return;
        const old = bindLabel(c);
        bindEditCh = name;
        cell.innerHTML = `<select>${sourceOptions(c.source_type, c.source_name)}</select>`;
        const selEl = cell.querySelector("select");
        let committed = false;
        selEl.addEventListener("change", async () => {
          committed = true;
          const [type, source] = selEl.value.split(":");
          try {
            const r = await api("/api/bind", { channel: name, type, source });
            if (!r.ok) {
              alert("Плата отказала: " + r.lines.join("; ") + "\n(для закреплённых каналов переключи режим в TMP-MAN)");
            }
            await refreshCurves();          // кривая заменена на кривую новой группы
            if (window.CurveEditor && window.CurveEditor.isOpenFor(name)) {
              window.CurveEditor.reload();
            }
          } catch (e) {
            alert("Ошибка: " + e.message);
          }
          bindEditCh = null;
          renderFans(lastStatus ? lastStatus.hub.channels : []);
        });
        selEl.addEventListener("blur", () => {
          setTimeout(() => {
            if (!committed) {
              bindEditCh = null;
              renderFans(lastStatus ? lastStatus.hub.channels : []);
            }
          }, 150);
        });
        selEl.focus();
      });
    });

    // клик по кривой -> редактор
    tb.querySelectorAll(".curve-cell").forEach((cell) => {
      cell.addEventListener("click", () => {
        if (window.CurveEditor) window.CurveEditor.open(cell.closest("tr").dataset.ch);
      });
    });
  }

  async function loadLog() {
    try {
      const r = await fetch("/api/status");
      const d = await r.json();
      disabledChannels = d.disabled_channels || [];
      disabledTherms = d.disabled_thermistors || [];
      $("log").innerHTML = d.log.map((e) =>
        `<div><span class="t">${e.t}</span> <span class="${e.kind}">${esc(e.text)}</span></div>`
      ).join("");
      $("log").scrollTop = $("log").scrollHeight;
    } catch (e) { /* сервер ещё не поднялся */ }
  }

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

  // чекбокс «Скрыть неактивные» (каналы)
  const hideOffEl = $("hide-off");
  hideOffEl.checked = hideOff;
  hideOffEl.addEventListener("change", () => {
    hideOff = hideOffEl.checked;
    localStorage.setItem("fanhub.hideOff", hideOff ? "1" : "0");
    renderFans(lastStatus ? lastStatus.hub.channels : []);
  });

  // чекбокс «Скрыть неподключённые» (термисторы)
  const hideThermOffEl = $("hide-therm-off");
  hideThermOffEl.checked = hideThermOff;
  hideThermOffEl.addEventListener("change", () => {
    hideThermOff = hideThermOffEl.checked;
    localStorage.setItem("fanhub.hideThermOff", hideThermOff ? "1" : "0");
    renderTherms(lastStatus ? lastStatus.hub.thermistors : []);
  });

  window.FanHub = {
    $, esc, api, onStatus,
    getStatus: () => lastStatus,
    getCurves: () => curvesCache,
    refreshCurves,
  };

  connectWs();
  loadLog();
  setInterval(loadLog, 5000);
  refreshCurves();
})();
