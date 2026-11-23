/* Вкладка «Консоль»: произвольные команды плате с историей. */
(function () {
  const { $, api } = window.FanHub;

  const out = $("console-out");
  const inp = $("console-in");
  const history = [];
  let hIdx = -1;

  function print(lines, cmd) {
    if (cmd !== undefined) {
      const c = document.createElement("div");
      c.className = "console-cmd";
      c.textContent = "> " + cmd;
      out.appendChild(c);
    }
    for (const ln of lines) {
      const d = document.createElement("div");
      d.textContent = ln;
      out.appendChild(d);
    }
    out.scrollTop = out.scrollHeight;
  }

  async function send(command) {
    const cmd = (command !== undefined ? command : inp.value).trim();
    if (!cmd) return;
    inp.value = "";
    history.push(cmd);
    hIdx = history.length;
    print(["…"]);
    try {
      const d = await api("/api/console", { cmd });
      out.removeChild(out.lastChild);
      print(d.lines.length ? d.lines : ["(тишина — команда без ответа или таймаут)"], cmd);
    } catch (e) {
      out.removeChild(out.lastChild);
      print(["ошибка: " + e.message], cmd);
    }
  }

  inp.addEventListener("keydown", (e) => {
    if (e.key === "Enter") send();
    else if (e.key === "ArrowUp") {
      if (hIdx > 0) { hIdx--; inp.value = history[hIdx] || ""; }
      e.preventDefault();
    } else if (e.key === "ArrowDown") {
      if (hIdx < history.length - 1) { hIdx++; inp.value = history[hIdx]; }
      else { hIdx = history.length; inp.value = ""; }
      e.preventDefault();
    }
  });
  $("console-send").addEventListener("click", () => send());

  const quick = ["STATUS?", "CURVES?", "BINDS?", "MODE?", "HOLD?", "TACH?", "THERMS?", "SPIN?", "DCFREQ?"];
  $("console-quick").innerHTML = quick.map((q) => `<button class="btn small" data-q="${q}">${q}</button>`).join(" ");
  document.querySelectorAll("#console-quick button").forEach((b) => {
    b.addEventListener("click", () => send(b.dataset.q));
  });

  print(["Консоль платы. Вводи команды протокола (STATUS?, CURVES?, MODE=1, …), ↑/↓ — история."]);
})();
