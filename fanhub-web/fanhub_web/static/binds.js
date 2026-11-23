/* Вкладка «Привязки»: режим контроллера + таблица канал -> датчик. */
(function () {
  const { $, api, onStatus } = window.FanHub;

  const tbody = () => document.querySelector("#binds tbody");
  let thermNames = [];
  let serialKeys = {};   // key -> label

  function sourcesFor(channel) {
    const opts = [];
    for (const t of thermNames) opts.push({ v: "THERM:" + t, label: "терм. " + t });
    for (const [k, label] of Object.entries(serialKeys)) opts.push({ v: "SERIAL:" + k, label: "ПК: " + label });
    return opts;
  }

  function fillSourcesFromStatus() {
    const st = window.FanHub.getStatus();
    if (!st) return;
    thermNames = (st.hub.thermistors || []).map((t) => t.name);
    serialKeys = st.serial_display_names || {};
  }

  async function load() {
    fillSourcesFromStatus();
    try {
      const d = await api("/api/binds");
      const st = window.FanHub.getStatus();
      const labels = {};
      if (st) for (const c of st.hub.channels) labels[c.name] = c.label;
      tbody().innerHTML = d.binds.map((b) => {
        const cur = b.type + ":" + b.source;
        const opts = sourcesFor(b)
          .map((o) => `<option value="${o.v}" ${o.v === cur ? "selected" : ""}>${o.label}</option>`)
          .join("");
        return `<tr data-ch="${b.channel}">
          <td>${labels[b.channel] || b.channel}${b.locked ? ' <span class="chip warn">закреплён</span>' : ""}</td>
          <td><select ${b.locked ? "disabled" : ""}>${opts}</select></td>
          <td><button class="btn" data-act="save" ${b.locked ? "disabled" : ""}>Сохранить</button></td>
          <td class="row-status hint"></td>
        </tr>`;
      }).join("");
      tbody().querySelectorAll("button[data-act=save]").forEach((btn) => {
        btn.addEventListener("click", async () => {
          const tr = btn.closest("tr");
          const [type, source] = tr.querySelector("select").value.split(":");
          const stEl = tr.querySelector(".row-status");
          stEl.textContent = "запись…";
          try {
            const r = await api("/api/bind", { channel: tr.dataset.ch, type, source });
            stEl.textContent = r.ok ? "записано в EEPROM ✔" : ("отказ: " + r.lines.join("; "));
          } catch (e) {
            stEl.textContent = "ошибка: " + e.message;
          }
        });
      });
    } catch (e) {
      tbody().innerHTML = `<tr><td colspan="4" class="hint">ошибка: ${e.message}</td></tr>`;
    }
  }

  async function loadMode() {
    try {
      const d = await api("/api/console", { cmd: "MODE?" });
      const m = (d.lines[0] || "").split(",")[1];
      const radio = document.querySelector(`input[name=mode][value="${m}"]`);
      if (radio) radio.checked = true;
    } catch (e) { /* плата офлайн */ }
  }

  $("mode-apply").addEventListener("click", async () => {
    const radio = document.querySelector("input[name=mode]:checked");
    if (!radio) return;
    $("mode-status").textContent = "запись…";
    try {
      await api("/api/mode", { mode: +radio.value });
      $("mode-status").textContent = "применено ✔";
    } catch (e) {
      $("mode-status").textContent = "ошибка: " + e.message;
    }
  });

  document.addEventListener("tabchange", (e) => {
    if (e.detail === "binds") { load(); loadMode(); }
  });
  onStatus(() => { /* подхватим свежие имена при следующем открытии */ });
})();
