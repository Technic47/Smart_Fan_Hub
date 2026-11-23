// Автозапуск DC-вентиляторов: точки раскрутки по каналам и параметры (прошивка 0.3.24+)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace MksFanHub
{
    public class SpinDialog : Form
    {
        private class Row
        {
            public string Channel;
            public Label Name, State;
            public NumericUpDown Start;
            public Button Test, Clear;
        }

        private readonly HubLink link;
        private readonly Usage usage;
        private readonly List<Row> rows = new List<Row>();
        private NumericUpDown nBoost, nStep, nRamp, nProbe, nTries;
        private Button applyBtn, saveBtn, resetBtn;
        private Label status;
        private System.Windows.Forms.Timer poll;
        private int busy;
        private bool supported;
        private bool closing;

        public SpinDialog(HubLink l, Usage u)
        {
            link = l; usage = u;
            Text = "Автозапуск DC-вентиляторов";
            ClientSize = new Size(620, 470);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            Label intro = new Label();
            intro.SetBounds(12, 8, 596, 62);
            intro.ForeColor = Color.DimGray;
            intro.Text = "Если DC-вентилятор остановился, плата даёт «точку раскрутки» + добавку на 2 с, потом плавно снижает до нужных оборотов " +
                         "и проверяет, что он крутится. Не получилось несколько раз подряд — канал получает ошибку (выход 0%). " +
                         "На частичном ШИМ тахо не читается, поэтому плата периодически (раз в N секунд) на 0,7 с даёт 100% и считает импульсы.";
            Controls.Add(intro);

            Label h1 = new Label(); h1.Text = "Канал"; h1.SetBounds(12, 76, 150, 18);
            Label h2 = new Label(); h2.Text = "Точка раскрутки"; h2.SetBounds(196, 76, 110, 18);
            Label h3 = new Label(); h3.Text = "Состояние"; h3.SetBounds(310, 76, 150, 18);
            Controls.Add(h1); Controls.Add(h2); Controls.Add(h3);

            nBoost = Num(0, 50, 5); nStep = Num(1, 50, 5); nRamp = Num(200, 10000, 1000); nProbe = Num(0, 3600, 60); nTries = Num(1, 9, 3);
            nRamp.Increment = 100; nProbe.Increment = 10;

            status = new Label(); status.SetBounds(12, 400, 596, 20); status.ForeColor = Color.DimGray;
            Controls.Add(status);

            applyBtn = UiKit.NewButton("Применить", 110); applyBtn.SetBounds(12, 430, 110, 30);
            saveBtn = UiKit.NewButton("Применить и сохранить в плату", 220); saveBtn.SetBounds(128, 430, 220, 30);
            resetBtn = UiKit.NewButton("Заводские", 100); resetBtn.SetBounds(354, 430, 100, 30);
            Button close = UiKit.NewButton("Закрыть", 90); close.SetBounds(518, 430, 90, 30);
            close.DialogResult = DialogResult.Cancel; CancelButton = close;
            applyBtn.Click += delegate { ApplyAll(false); };
            saveBtn.Click += delegate { ApplyAll(true); };
            resetBtn.Click += delegate { ResetAll(); };
            Controls.Add(applyBtn); Controls.Add(saveBtn); Controls.Add(resetBtn); Controls.Add(close);
            SetEnabled(false);

            poll = new System.Windows.Forms.Timer(); poll.Interval = 2000;
            poll.Tick += delegate { if (supported) Load(false); };
            FormClosing += delegate { closing = true; poll.Stop(); };
            Shown += delegate { status.Text = "Читаю настройки из платы…"; Load(true); poll.Start(); };
        }

        private NumericUpDown Num(int min, int max, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = val;
            return n;
        }

        private void SetEnabled(bool on)
        {
            applyBtn.Enabled = on; saveBtn.Enabled = on; resetBtn.Enabled = on;
            nBoost.Enabled = on; nStep.Enabled = on; nRamp.Enabled = on; nProbe.Enabled = on; nTries.Enabled = on;
            foreach (Row r in rows) { r.Start.Enabled = on; r.Test.Enabled = on; r.Clear.Enabled = on; }
        }

        private static string StateText(string st, string tries)
        {
            switch (st)
            {
                case "OK": return "в норме";
                case "KICK": return "толчок при пуске";
                case "BOOST": return "раскрутка";
                case "RAMP": return "снижение до нужных %";
                case "PROBE": return "проверка";
                case "VERIFY": return "проверка после запуска";
                case "ERR": return "ОШИБКА: не раскрутился";
            }
            return st;
        }

        private void RunUi(Action a)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // читает SPIN?; first - первое чтение: строит строки и заполняет поля
        private void Load(bool first)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> lines; string err;
                bool ok = link.Query("SPIN?", delegate (string s) { return s.StartsWith("SPIN=END", StringComparison.Ordinal) || s.StartsWith("SPIN=ERR", StringComparison.Ordinal); },
                                     1500, out lines, out err);
                Interlocked.Exchange(ref busy, 0);
                bool good = ok && lines.Count > 0 && lines[lines.Count - 1].StartsWith("SPIN=END", StringComparison.Ordinal);
                RunUi(delegate { Fill(good, lines, first); });
            });
        }

        private void Fill(bool good, List<string> lines, bool first)
        {
            if (!good)
            {
                if (first) status.Text = "Нужна прошивка 0.3.24 или новее (на SPIN? нет ответа).";
                return;
            }
            supported = true;
            if (rows.Count == 0)
            {
                int y = 98;
                foreach (string raw in lines)
                {
                    string[] a = raw.Trim().Split(',');
                    if (a.Length < 4 || !a[0].StartsWith("SPIN=FUN_", StringComparison.Ordinal)) continue;
                    Row r = new Row();
                    r.Channel = a[0].Substring(5);
                    string nm = usage.ChannelName(r.Channel);
                    r.Name = new Label(); r.Name.Text = nm == r.Channel ? r.Channel : nm + " [" + r.Channel + "]"; r.Name.SetBounds(12, y + 4, 180, 20);
                    r.Start = Num(1, 100, 37); r.Start.SetBounds(196, y, 62, 24);
                    Label pc = new Label(); pc.Text = "%"; pc.SetBounds(262, y + 4, 18, 20);
                    r.State = new Label(); r.State.SetBounds(310, y + 4, 134, 20);
                    string ch = r.Channel;
                    r.Test = UiKit.NewButton("Проверить", 84); r.Test.SetBounds(446, y - 2, 84, 28);
                    r.Clear = UiKit.NewButton("Сбросить", 78); r.Clear.SetBounds(534, y - 2, 78, 28);
                    r.Test.Click += delegate { Simple("SPIN=TEST," + ch, "Проверка запущена (плата на ~0,7 с даст 100%), результат — в журнале и состоянии."); };
                    r.Clear.Click += delegate { Simple("SPIN=CLEAR," + ch, "Ошибка канала сброшена."); };
                    Controls.Add(r.Name); Controls.Add(r.Start); Controls.Add(pc); Controls.Add(r.State); Controls.Add(r.Test); Controls.Add(r.Clear);
                    rows.Add(r);
                    y += 32;
                }
                int py = y + 12;
                AddParam("Добавка к точке раскрутки при запуске, %", nBoost, 12, py);
                AddParam("Шаг снижения до нужных оборотов, %", nStep, 12, py + 30);
                AddParam("Пауза между шагами снижения, мс", nRamp, 12, py + 60);
                AddParam("Проверка остановки раз в, с (0 = выключено)", nProbe, 12, py + 90);
                AddParam("Попыток подряд до ошибки", nTries, 12, py + 120);
                first = true;
            }
            foreach (string raw in lines)
            {
                string[] a = raw.Trim().Split(',');
                if (a.Length >= 6 && a[0] == "SPIN=PARAM")
                {
                    if (first) { SetNum(nBoost, a[1]); SetNum(nStep, a[2]); SetNum(nRamp, a[3]); SetNum(nProbe, a[4]); SetNum(nTries, a[5]); }
                    continue;
                }
                if (a.Length < 4 || !a[0].StartsWith("SPIN=FUN_", StringComparison.Ordinal)) continue;
                foreach (Row r in rows)
                {
                    if (r.Channel != a[0].Substring(5)) continue;
                    if (first) SetNum(r.Start, a[1]);
                    r.State.Text = StateText(a[2], a[3]) + (a[2] != "OK" && a[3] != "0" ? " (попыток: " + a[3] + ")" : "");
                    r.State.ForeColor = a[2] == "ERR" ? Color.Firebrick : (a[2] == "OK" ? Color.SeaGreen : Color.SteelBlue);
                }
            }
            SetEnabled(true);
            if (first) status.Text = "Настройки прочитаны из платы.";
        }

        private void AddParam(string text, NumericUpDown n, int x, int y)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y + 3, 360, 20);
            n.SetBounds(380, y, 90, 24);
            Controls.Add(l); Controls.Add(n);
        }

        private static void SetNum(NumericUpDown n, string v)
        {
            int x;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out x) && x >= n.Minimum && x <= n.Maximum) n.Value = x;
        }

        private void Simple(string cmd, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string reply = Ask(cmd);
                bool ok = reply != null && reply.StartsWith("SPIN=OK", StringComparison.Ordinal);
                RunUi(delegate { status.Text = ok ? okText : "Плата не приняла команду: " + (reply ?? "нет ответа"); });
                if (ok) Log.Write("Автозапуск DC: " + cmd);
            });
        }

        // отправляет команду и возвращает последнюю строку ответа SPIN=…
        private string Ask(string cmd)
        {
            List<string> lines; string err;
            bool ok = link.Query(cmd, delegate (string s) { return s.StartsWith("SPIN=", StringComparison.Ordinal); }, 1500, out lines, out err);
            return (ok && lines.Count > 0) ? lines[lines.Count - 1].Trim() : null;
        }

        private void ApplyAll(bool save)
        {
            List<KeyValuePair<string, int>> starts = new List<KeyValuePair<string, int>>();
            foreach (Row r in rows) starts.Add(new KeyValuePair<string, int>(r.Channel, (int)r.Start.Value));
            string param = "SPIN=PARAM," + (int)nBoost.Value + "," + (int)nStep.Value + "," + (int)nRamp.Value + "," + (int)nProbe.Value + "," + (int)nTries.Value;
            applyBtn.Enabled = false; saveBtn.Enabled = false;
            status.Text = "Отправляю…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string bad = null;
                foreach (KeyValuePair<string, int> kv in starts)
                {
                    string rep = Ask("SPIN=" + kv.Key + "," + kv.Value);
                    if (rep == null || !rep.StartsWith("SPIN=OK", StringComparison.Ordinal)) { bad = kv.Key + ": " + (rep ?? "нет ответа"); break; }
                }
                if (bad == null)
                {
                    string rep = Ask(param);
                    if (rep == null || !rep.StartsWith("SPIN=OK", StringComparison.Ordinal)) bad = "параметры: " + (rep ?? "нет ответа");
                }
                if (bad == null && save)
                {
                    string rep = Ask("SPIN=SAVE");
                    if (rep == null || !rep.StartsWith("SPIN=SAVED", StringComparison.Ordinal)) bad = "сохранение: " + (rep ?? "нет ответа");
                }
                if (bad == null) Log.Write("Автозапуск DC: настройки " + (save ? "применены и сохранены в плату" : "применены (не сохранены)"));
                RunUi(delegate
                {
                    status.Text = bad == null ? (save ? "Применено и сохранено в плату." : "Применено (после перезагрузки платы вернётся сохранённое).") : "Плата не приняла: " + bad;
                    applyBtn.Enabled = true; saveBtn.Enabled = true;
                });
            });
        }

        private void ResetAll()
        {
            if (!UiKit.Confirm(this, "Вернуть заводские настройки автозапуска (точка раскрутки 37%, добавка 5%, шаг 5%, проверка раз в 60 с, 3 попытки)?")) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string rep = Ask("SPIN=RESET");
                bool ok = rep != null && rep.StartsWith("SPIN=RESET_OK", StringComparison.Ordinal);
                if (ok) Log.Write("Автозапуск DC: заводские настройки");
                RunUi(delegate
                {
                    status.Text = ok ? "Заводские настройки возвращены." : "Плата не приняла команду: " + (rep ?? "нет ответа");
                });
                if (ok) { Interlocked.Exchange(ref busy, 0); Load(true); }
            });
        }
    }
}
