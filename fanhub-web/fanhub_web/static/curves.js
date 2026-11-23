/* Вкладка «Кривые»: SVG-редактор 5 точек с перетаскиванием,
   сохранение в плату с верификацией чтением. */
(function () {
  const { $, api, onStatus } = window.FanHub;

  const NS = "http://www.w3.org/2000/svg";
  const L = 46, R = 14, T = 14, B = 30, W = 640, H = 360;
  const PW = W - L - R, PH = H - T - B;
  const T_MAX = 110;          // отображаемый диапазон температур
  const N_PTS = 5;

  let points = [];            // [[t, p] x5], отсортированы по t
  let channels = [];          // [{name, label}]
  let dragIdx = -1;

  const svg = $("curve-svg");
  const sel = $("curve-channel");
  const statusEl = $("curve-status");

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

  function sorted() {
    points = points.slice().sort((a, b) => a[0] - b[0]);
  }

  function renderPointsList() {
    $("curve-points").innerHTML = points.map((pt, i) =>
      `<span class="pt">точка ${i + 1}: <input type="number" min="0" max="125" value="${pt[0]}" data-i="${i}" data-k="0" style="width:58px">°C` +
      ` <input type="number" min="0" max="100" value="${pt[1]}" data-i="${i}" data-k="1" style="width:52px">%</span>`
    ).join(" ");
    document.querySelectorAll("#curve-points input").forEach((inp) => {
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
    const st = window.FanHub.getStatus();
    if (!st || !st.hub.link.connected) return null;
    const name = sel.value;
    const ch = st.hub.channels.find((c) => c.name === name);
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
    // интерполяция кривой в точке temp
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
    svg.innerHTML = "";
    // сетка
    for (let t = 0; t <= T_MAX; t += 10) {
      el("line", { x1: x(t), y1: T, x2: x(t), y2: T + PH, stroke: t % 50 ? "#20262e" : "#2c3540", "stroke-width": 1 });
      const tx = el("text", { x: x(t), y: H - 10, "text-anchor": "middle", class: "axis" }, svg);
      tx.textContent = t;
    }
    for (let p = 0; p <= 100; p += 20) {
      el("line", { x1: L, y1: y(p), x2: L + PW, y2: y(p), stroke: "#20262e", "stroke-width": 1 });
      const ty = el("text", { x: L - 8, y: y(p) + 4, "text-anchor": "end", class: "axis" }, svg);
      ty.textContent = p + "%";
    }
    const tx = el("text", { x: L + PW / 2, y: H - 10 + 0, "text-anchor": "middle", class: "axis" }, svg);
    // подписи осей
    const xl = el("text", { x: L + PW / 2, y: H - 0.5, "text-anchor": "middle", class: "axis" }, svg);
    xl.textContent = "температура, °C";
    const yl = el("text", { x: 12, y: T + PH / 2, "text-anchor": "middle", class: "axis", transform: `rotate(-90 12 ${T + PH / 2})` }, svg);
    yl.textContent = "мощность";

    if (points.length === N_PTS) {
      const lm = liveMarker();
      if (lm) {
        el("line", { x1: x(lm.temp), y1: T, x2: x(lm.temp), y2: T + PH, stroke: "#4aa3ff", "stroke-width": 1, "stroke-dasharray": "4 3", opacity: 0.7 });
        el("circle", { cx: x(lm.temp), cy: y(lm.pct), r: 5, fill: "none", stroke: "#4aa3ff", "stroke-width": 2 });
      }
      const d = points.map((pt, i) => (i ? "L" : "M") + x(pt[0]).toFixed(1) + " " + y(pt[1]).toFixed(1)).join(" ");
      el("path", { d, fill: "none", stroke: "#4aa3ff", "stroke-width": 2 });
      points.forEach((pt, i) => {
        const c = el("circle", {
          cx: x(pt[0]), cy: y(pt[1]), r: 9, class: "pt-handle",
          fill: dragIdx === i ? "#e2b93d" : "#4aa3ff", stroke: "#0d1117", "stroke-width": 2,
          style: "cursor:grab",
        });
        c.addEventListener("pointerdown", (ev) => {
          dragIdx = i;
          c.setPointerCapture(ev.pointerId);
          ev.preventDefault();
        });
        c.addEventListener("pointermove", (ev) => {
          if (dragIdx !== i) return;
          const rect = svg.getBoundingClientRect();
          const sx = (ev.clientX - rect.left) * (W / rect.width);
          const sy = (ev.clientY - rect.top) * (H / rect.height);
          points[i][0] = Math.min(125, Math.max(0, Math.round(invX(sx))));
          points[i][1] = Math.min(100, Math.max(0, Math.round(invY(sy))));
          draw();
        });
        c.addEventListener("pointerup", () => {
          dragIdx = -1;
          sorted();
          renderPointsList();
          draw();
        });
        const label = el("text", { x: x(pt[0]), y: y(pt[1]) - 14, "text-anchor": "middle", class: "axis" }, svg);
        label.textContent = `${pt[0]}°/${pt[1]}%`;
      });
    }
    renderPointsList();
  }

  async function loadFromBoard() {
    statusEl.textContent = "читаю кривые…";
    try {
      const d = await api("/api/curves");
      const pts = d.curves[sel.value];
      if (pts) {
        points = pts.map((p) => [Math.round(p[0]), p[1]]);
        sorted();
        statusEl.textContent = "загружено из платы";
      } else {
        statusEl.textContent = "у канала нет кривой?";
      }
      draw();
    } catch (e) {
      statusEl.textContent = "ошибка: " + e.message;
    }
  }

  async function save() {
    statusEl.textContent = "сохраняю…";
    try {
      sorted();
      const d = await api("/api/curves", { channel: sel.value, points });
      if (d.ok) {
        statusEl.textContent = "сохранено и проверено чтением ✔";
      } else {
        statusEl.textContent = "несовпадение после записи! На плате: " + JSON.stringify(d.board);
      }
    } catch (e) {
      statusEl.textContent = "ошибка: " + e.message;
    }
    draw();
  }

  function fillChannels() {
    const st = window.FanHub.getStatus();
    if (!st) return;
    const prev = sel.value;
    channels = st.hub.channels;
    sel.innerHTML = channels.map((c) => `<option value="${c.name}">${c.label}</option>`).join("");
    if (channels.some((c) => c.name === prev)) sel.value = prev;
  }

  sel.addEventListener("change", loadFromBoard);
  $("curve-reload").addEventListener("click", loadFromBoard);
  $("curve-save").addEventListener("click", save);
  onStatus(() => { const n = sel.options.length; fillChannels(); if (!n && sel.options.length) loadFromBoard(); });
  document.addEventListener("tabchange", (e) => {
    if (e.detail === "curves") {
      fillChannels();
      if (!points.length) loadFromBoard(); else draw();
    }
  });
  draw();
})();
