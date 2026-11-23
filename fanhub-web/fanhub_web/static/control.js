/* Вкладка «Управление»: ручной HOLD, тест тахо, TACHFAST-осциллограмма,
   параметры SPIN и частота DCFREQ. */
(function () {
  const { $, api, onStatus } = window.FanHub;

  const out = $("spin-dc-out");

  function fillChannelSelects() {
    const st = window.FanHub.getStatus();
    if (!st) return;
    const prevHold = $("hold-channel").value;
    const prevTach = $("tach-channel").value;
    const opts = st.hub.channels.map((c) => `<option value="${c.name}">${c.label}</option>`).join("");
    $("hold-channel").innerHTML = opts;
    $("tach-channel").innerHTML = opts;
    const dc = st.hub.channels.filter((c) => c.name.startsWith("FUN_DC"));
    $("dc-channel").innerHTML = dc.map((c) => `<option value="${c.name}">${c.label}</option>`).join("");
    if (prevHold) $("hold-channel").value = prevHold;
    if (prevTach) $("tach-channel").value = prevTach;
  }

  async function cmd(command) {
    return api("/api/console", { cmd: command });
  }

  // ---------- HOLD ----------
  $("hold-pct").addEventListener("input", () => {
    $("hold-pct-val").textContent = $("hold-pct").value + "%";
  });
  $("hold-apply").addEventListener("click", async () => {
    const ch = $("hold-channel").value;
    const pct = $("hold-pct").value;
    const sec = $("hold-sec").value || "300";
    $("hold-status").textContent = "запись…";
    try {
      const d = await cmd(`HOLD=${ch},${pct},${sec}`);
      $("hold-status").textContent = d.lines.join("; ") || "ок";
    } catch (e) {
      $("hold-status").textContent = "ошибка: " + e.message;
    }
  });
  $("hold-release-all").addEventListener("click", async () => {
    $("hold-status").textContent = "снимаю…";
    try {
      const d = await cmd("HOLD=ALL,OFF");
      $("hold-status").textContent = d.lines.join("; ") || "ок";
    } catch (e) {
      $("hold-status").textContent = "ошибка: " + e.message;
    }
  });
  $("hold-refresh").addEventListener("click", async () => {
    try {
      const d = await cmd("HOLD?");
      const active = d.lines.filter((l) => l.startsWith("HOLD=") && l !== "HOLD=END");
      $("hold-status").textContent = active.length ? active.join("; ") : "активных удержаний нет";
    } catch (e) {
      $("hold-status").textContent = "ошибка: " + e.message;
    }
  });

  // ---------- Тахо ----------
  $("tachtest").addEventListener("click", async () => {
    $("tach-status").textContent = "плата гоняет тест…";
    try {
      const d = await cmd("TACHTEST");
      $("tach-status").textContent = d.lines.join("; ") || "запущено (результат — в журнале платы)";
    } catch (e) {
      $("tach-status").textContent = "ошибка: " + e.message;
    }
  });
  $("tachfast").addEventListener("click", async () => {
    const ch = $("tach-channel").value;
    $("tach-status").textContent = "снимаю 50 мс сигнала…";
    try {
      const d = await cmd(`TACHFAST=${ch}`);
      drawTach(d.lines);
    } catch (e) {
      $("tach-status").textContent = "ошибка: " + e.message;
    }
  });

  function drawTach(lines) {
    const svg = $("tach-svg");
    const NS = "http://www.w3.org/2000/svg";
    svg.innerHTML = "";
    let begin = null, maxT = 1, maxH = 1;
    const bins = [];   // [trans, high]
    for (const ln of lines) {
      if (ln.startsWith("TACHDUMP=BEGIN,")) begin = ln.split(",");
      if (!ln.startsWith("TACHDUMP=D,")) continue;
      const hex = ln.split(",")[2] || "";
      for (let i = 0; i + 3 < hex.length + 1 && i + 4 <= hex.length; i += 4) {
        const tr = parseInt(hex.substr(i, 2), 16);
        const hi = parseInt(hex.substr(i + 2, 2), 16);
        bins.push([tr, hi]);
        if (tr > maxT) maxT = tr;
        if (hi > maxH) maxH = hi;
      }
    }
    if (!begin || !bins.length) {
      $("tach-status").textContent = "нет данных: " + lines.join("; ").slice(0, 120);
      svg.style.display = "none";
      return;
    }
    const W = 1000, H = 240, T = 10, B = 24, PW = W - 20, PH = H - T - B;
    const bw = PW / bins.length;
    const el = (tag, attrs) => {
      const e = document.createElementNS(NS, tag);
      for (const k in attrs) e.setAttribute(k, attrs[k]);
      svg.appendChild(e);
      return e;
    };
    bins.forEach((b, i) => {
      const hT = (b[0] / maxT) * PH;
      const hH = (b[1] / maxH) * PH;
      if (hT > 0.5) el("rect", { x: 10 + i * bw, y: T + PH - hT, width: Math.max(1, bw - 0.4), height: hT, fill: "#4aa3ff", opacity: 0.85 });
      if (hH > 0.5) el("rect", { x: 10 + i * bw, y: T + PH - hH, width: Math.max(1, bw - 0.4), height: hH, fill: "#34c77b", opacity: 0.35 });
    });
    const lbl = el("text", { x: 10, y: H - 6, class: "axis" });
    lbl.textContent = `${begin[2]} · пин ${begin[3]} · выход ${begin[4]}% · ${begin[5]} мкс/бин · ${begin[6]} бинов`;
    svg.style.display = "";
    $("tach-legend").style.display = "";
    $("tach-status").textContent = `бинов: ${bins.length}`;
  }

  // ---------- SPIN / DCFREQ ----------
  function showOut(lines) {
    out.textContent = (out.textContent ? out.textContent + "\n" : "") + "> " + lines.join("\n");
    out.scrollTop = out.scrollHeight;
  }

  $("spin-read").addEventListener("click", async () => {
    try {
      const d = await cmd("SPIN?");
      showOut(d.lines);
      for (const ln of d.lines) {
        if (ln.startsWith("SPIN=PARAM,")) {
          const [, boost, step, ramp, probe, tries] = ln.split(",");
          $("spin-boost").value = boost; $("spin-step").value = step;
          $("spin-ramp").value = ramp; $("spin-probe").value = probe; $("spin-tries").value = tries;
        }
      }
    } catch (e) { showOut(["ошибка: " + e.message]); }
  });
  $("spin-save").addEventListener("click", async () => {
    const v = ["spin-boost", "spin-step", "spin-ramp", "spin-probe", "spin-tries"].map((id) => $(id).value);
    $("spin-status").textContent = "запись…";
    try {
      let d = await cmd(`SPIN=PARAM,${v.join(",")}`);
      showOut(d.lines);
      d = await cmd("SPIN=SAVE");
      showOut(d.lines);
      $("spin-status").textContent = "сохранено ✔";
    } catch (e) {
      $("spin-status").textContent = "ошибка: " + e.message;
    }
  });
  $("dc-read").addEventListener("click", async () => {
    try { showOut((await cmd("DCFREQ?")).lines); } catch (e) { showOut(["ошибка: " + e.message]); }
  });
  $("dc-apply").addEventListener("click", async () => {
    const ch = $("dc-channel").value, hz = $("dc-hz").value;
    if (!ch || !hz) return;
    try {
      let d = await cmd(`DCFREQ=${ch},${hz}`);
      showOut(d.lines);
      d = await cmd("DCFREQ=SAVE");
      showOut(d.lines);
    } catch (e) { showOut(["ошибка: " + e.message]); }
  });

  onStatus(fillChannelSelects);
  document.addEventListener("tabchange", (e) => { if (e.detail === "control") fillChannelSelects(); });
})();
