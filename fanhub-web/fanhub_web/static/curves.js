/* Модальное окно кривой канала.
   Группировка: канал можно присоединить к другому — кривая становится общей,
   редактирует её владелец группы (первый канал по порядку платы),
   у присоединённых каналов редактирование заблокировано. */
(function () {
  const { $, api, getStatus, getCurves, refreshCurves } = window.FanHub;

  const NS = "http://www.w3.org/2000/svg";
  const L = 46, R = 14, T = 14, B = 30, W = 640, H = 360;
  const PW = W - L - R, PH = H - T - B;
  const T_MAX = 110;
  const N_PTS = 5;

  let points = [];
  let channel = "";
  let dragIdx = -1;

  const svg = $("cm-svg");
  const statusEl = $("cm-status");

  const x = (t) => L + (t / T_MAX) * PW;
  const y = (p) => T + PH - (p / 100) * PH;
  const invX = (px) => ((px - L) / PW) * T_MAX;
  const invY = (py) => ((T + PH - py) / PH) * 100;

  function el(tag, attrs, parent) {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    (parent || svg).appendChild(e);
    return e;
  }

  function sorted() { points = points.slice().sort((a, b) => a[0] - b[0]); }

  function groups() {
    const st = getStatus();
    return (st && st.curve_groups) || {};
  }

  function boardOrder() {
    const st = getStatus();
    return st ? st.hub.channels.map((c) => c.name) : [];
  }

  function groupInfo(name) {
    const g = groups()[name];
    if (!g) return null;
    const order = boardOrder();
    const mem = Object.keys(groups()).filter((c) => groups()[c] === g);
    mem.sort((a, b) => order.indexOf(a) - order.indexOf(b));
    return { gid: g, members: mem, primary: mem[0] || null };
  }

  function channelLabel(name) {
    const st = getStatus();
    const c = st && st.hub.channels.find((ch) => ch.name === name);
    return c ? c.label : name;
  }

  function isLocked(name) {
    const gi = groupInfo(name);
    return gi !== null && gi.primary !== name;
  }

  function renderPointsList(editable) {
    $("cm-points").innerHTML = points.map((pt, i) =>
      `<span class="pt">точка ${i + 1}: <input type="number" min="0" max="125" value="${pt[0]}" data-i="${i}" data-k="0" style="width:58px" ${editable ? "" : "disabled"}>°C` +
      ` <input type="number" min="0" max="100" value="${pt[1]}" data-i="${i}" data-k="1" style="width:52px" ${editable ? "" : "disabled"}>%</span>`
    ).join(" ");
    document.querySelectorAll("#cm-points input").forEach((inp) => {
      inp.addEventListener("change", () => {
        const i = +inp.dataset.i, k = +inp.dataset.k;
        let v = parseFloat(inp.value);
        if (isNaN(v)) v = points[i][k];
        if (k === 0) v = Math.min(125, Math.max(0, v));
        else v = Math.min(100, Math.max(0, Math.round(v)));
        points[i][k] = v;
        sorted();
        draw();
      });
    });
  }

  function liveMarker() {
    const st = getStatus();
    if (!st || !st.hub.link.connected) return null;
    const ch = st.hub.channels.find((c) => c.name === channel);
    if (!ch) return null;
    let temp = null;
    if (ch.source_type === "SERIAL") {
      const t = st.host_temps[ch.source_name];
      if (t && t.value !== null) temp = t.value;
    } else {
      const rec = (st.hub.thermistors || []).find((t) => t.name === ch.source_name);
      if (rec && rec.value !== null) temp = rec.value;
    }
    if (temp === null) return null;
    let pct = points.length ? points[points.length - 1][1] : 0;
    for (let i = 0; i + 1 < points.length; i++) {
      const [t0, p0] = points[i], [t1, p1] = points[i + 1];
      if (temp >= t0 && temp <= t1) {
        pct = t1 === t0 ? p1 : p0 + (p1 - p0) * (temp - t0) / (t1 - t0);
        break;
      }
      if (temp < t0) { pct = p0; break; }
    }
    return { temp, pct };
  }

  function draw() {
    const editable = !isLocked(channel);
    svg.innerHTML = "";
    for (let t = 0; t <= T_MAX; t += 10) {
      el("line", { x1: x(t), y1: T, x2: x(t), y2: T + PH, stroke: t % 50 ? "#20262e" : "#2c3540", "stroke-width": 1 });
      const tx = el("text", { x: x(t), y: H - 14, "text-anchor": "middle", class: "axis" });
      tx.textContent = t;
    }
    for (let p = 0; p <= 100; p += 20) {
      el("line", { x1: L, y1: y(p), x2: L + PW, y2: y(p), stroke: "#20262e", "stroke-width": 1 });
      const ty = el("text", { x: L - 8, y: y(p) + 4, "text-anchor": "end", class: "axis" });
      ty.textContent = p + "%";
    }
    const xl = el("text", { x: L + PW / 2, y: H - 2, "text-anchor": "middle", class: "axis" });
    xl.textContent = "температура, °C";
    const yl = el("text", { x: 12, y: T + PH / 2, "text-anchor": "middle", class: "axis", transform: `rotate(-90 12 ${T + PH / 2})` });
    yl.textContent = "мощность";

    if (points.length === N_PTS) {
      const lm = liveMarker();
      if (lm) {
        el("line", { x1: x(lm.temp), y1: T, x2: x(lm.temp), y2: T + PH, stroke: "#4aa3ff", "stroke-width": 1, "stroke-dasharray": "4 3", opacity: 0.7 });
        el("circle", { cx: x(lm.temp), cy: y(lm.pct), r: 5, fill: "none", stroke: "#4aa3ff", "stroke-width": 2 });
      }
      const d = points.map((pt, i) => (i ? "L" : "M") + x(pt[0]).toFixed(1) + " " + y(pt[1]).toFixed(1)).join(" ");
      el("path", { d, fill: "none", stroke: editable ? "#4aa3ff" : "#5a6778", "stroke-width": 2 });
      points.forEach((pt, i) => {
        const c = el("circle", {
          cx: x(pt[0]), cy: y(pt[1]), r: 9, class: "pt-handle",
          fill: dragIdx === i ? "#e2b93d" : (editable ? "#4aa3ff" : "#5a6778"),
          stroke: "#0d1117", "stroke-width": 2,
          style: editable ? "cursor:grab" : "cursor:not-allowed",
        });
        if (editable) {
          c.addEventListener("pointerdown", (ev) => { dragIdx = i; c.setPointerCapture(ev.pointerId); ev.preventDefault(); });
          c.addEventListener("pointermove", (ev) => {
            if (dragIdx !== i) return;
            const rect = svg.getBoundingClientRect();
            const sx = (ev.clientX - rect.left) * (W / rect.width);
            const sy = (ev.clientY - rect.top) * (H / rect.height);
            points[i][0] = Math.min(125, Math.max(0, Math.round(invX(sx))));
            points[i][1] = Math.min(100, Math.max(0, Math.round(invY(sy))));
            draw();
          });
          c.addEventListener("pointerup", () => { dragIdx = -1; sorted(); renderPointsList(true); draw(); });
        }
        const label = el("text", { x: x(pt[0]), y: y(pt[1]) - 14, "text-anchor": "middle", class: "axis" });
        label.textContent = `${pt[0]}°/${pt[1]}%`;
      });
    }
    renderPointsList(editable);
  }

  function fillGroupSelect() {
    const st = getStatus();
    if (!st) return;
    const gi = groupInfo(channel);
    const opts = [];
    if (gi && gi.primary === channel) {
      opts.push(`<option value="__self__" selected>владелец гр. ${gi.gid}</option>`);
      opts.push(`<option value="">— отдельно —</option>`);
    } else {
      opts.push(`<option value="">— отдельно —</option>`);
    }
    for (const c of st.hub.channels) {
      if (c.name === channel) continue;
      const sel = gi && gi.primary === c.name ? "selected" : "";
      opts.push(`<option value="${c.name}" ${sel}>с «${esc(c.label)}»</option>`);
    }
    $("cm-group").innerHTML = opts.join("");
    $("cm-group").value = gi ? (gi.primary === channel ? "__self__" : gi.primary) : "";
  }

  function lockHint() {
    const gi = groupInfo(channel);
    if (gi && gi.primary !== channel) {
      statusEl.textContent = `кривая общая (гр. ${gi.gid}), редактируется у «${channelLabel(gi.primary)}»`;
      $("cm-save").disabled = true;
    } else {
      $("cm-save").disabled = false;
    }
  }

  async function loadFromBoard() {
    statusEl.textContent = "читаю кривую…";
    try {
      const d = await api("/api/curves");
      const pts = d.curves[channel];
      if (pts) {
        points = pts.map((p) => [Math.round(p[0]), p[1]]);
        sorted();
        statusEl.textContent = "загружено из платы";
      } else {
        statusEl.textContent = "кривая не найдена";
      }
      lockHint();
      draw();
    } catch (e) {
      statusEl.textContent = "ошибка: " + e.message;
    }
  }

  async function save() {
    if (isLocked(channel)) return;
    statusEl.textContent = "сохраняю…";
    try {
      sorted();
      const d = await api("/api/curves", { channel, points });
      statusEl.textContent = d.ok
        ? "сохранено и проверено чтением ✔"
        : "несовпадение после записи! На плате: " + JSON.stringify(d.board);
      refreshCurves();
    } catch (e) {
      statusEl.textContent = "ошибка: " + e.message;
    }
    draw();
  }

  function open(name) {
    channel = name;
    const st = getStatus();
    const label = st && st.hub.channels.find((c) => c.name === name);
    $("cm-title").textContent = "Кривая: " + (label ? label.label : name);
    fillGroupSelect();
    const cached = getCurves()[name];
    if (cached) {
      points = cached.map((p) => [Math.round(p[0]), p[1]]);
      sorted();
      draw();
    }
    $("curve-modal").style.display = "";
    lockHint();
    loadFromBoard();
  }

  function close() { $("curve-modal").style.display = "none"; }

  $("cm-close").addEventListener("click", close);
  $("cm-save").addEventListener("click", save);
  $("cm-reload").addEventListener("click", loadFromBoard);
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });

  $("cm-group").addEventListener("change", async () => {
    const withCh = $("cm-group").value;
    if (withCh === "__self__") return;   // владелец: перегруппировка не нужна
    statusEl.textContent = withCh ? "группирую…" : "открепляю…";
    try {
      await api("/api/curve-group", { channel, with: withCh });
      statusEl.textContent = withCh
        ? "сгруппировано, кривая заменена кривой владельца"
        : "откреплено, кривая осталась прежней";
      refreshCurves();
      fillGroupSelect();
      await loadFromBoard();
    } catch (e) {
      statusEl.textContent = "ошибка: " + e.message;
      fillGroupSelect();
    }
  });

  window.CurveEditor = {
    open,
    reload: loadFromBoard,
    isOpenFor: (name) => $("curve-modal").style.display !== "none" && channel === name,
  };
})();
