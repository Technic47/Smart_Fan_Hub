// Консоль: ручная отправка команд в плату, быстрые кнопки запросов, тест тахометров (TACHTEST)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace MksFanHub
{
    public class ConsolePanel : Panel
    {
        private const int KindOut = 0, KindIn = 1, KindInfo = 2, KindResult = 3, KindError = 4;

        private class Item
        {
            public int Kind;
            public string Text;
        }

        private class TachRow
        {
            public string Channel, Pin, State;
            public int Rpm;
        }

        private readonly HubLink link;
        private readonly Usage usage;

        private ListBox output;
        private TextBox input;
        private Button sendBtn, tachTestBtn;
        private FlowLayoutPanel quick;
        private Timer pump;

        // трафик из рабочего потока -> UI-поток (через очередь и таймер: у вкладки может ещё не быть handle)
        private readonly Queue<Item> pending = new Queue<Item>();
        private readonly List<string> history = new List<string>();
        private int historyPos = 0;

        // тест тахо
        private bool tachTesting;
        private DateTime tachStartedAt;
        private List<TachRow> tachRows = new List<TachRow>();

        // Только для автотестов: не показывать окна подтверждения
        public static bool AutoConfirmForTests;

        public event Action TachTestFinished;

        public ConsolePanel(HubLink l, Usage u)
        {
            link = l; usage = u;
            Dock = DockStyle.Fill;

            // ---- быстрые кнопки (запросы данных) ----
            quick = new FlowLayoutPanel();
            quick.Dock = DockStyle.Top;
            quick.AutoSize = true;
            quick.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            quick.Padding = new Padding(2, 2, 2, 0);
            ToolTip tip = new ToolTip();
            AddQuick(tip, "STATUS?", "STATUS?", "Всё сразу: режим, температуры, каналы, тахо, watchdog");
            AddQuick(tip, "THERMS?", "THERMS?", "Термисторы: имя, включён, значение, состояние");
            AddQuick(tip, "TACH?", "TACH?", "Тахометры: канал, пин, состояние, обороты");
            AddQuick(tip, "CURVES?", "CURVES?", "Кривые всех каналов");
            AddQuick(tip, "BINDS?", "BINDS?", "Привязки каналов к датчикам");
            AddQuick(tip, "MODE?", "MODE?", "Режим платы: 0 = TMP-SRL, 1 = TMP-MAN");
            tachTestBtn = UiKit.NewButton("Тест тахо", 96);
            tachTestBtn.Height = 26;
            tachTestBtn.Margin = new Padding(8, 2, 2, 2);
            tip.SetToolTip(tachTestBtn, "TACHTEST: 3-pin вентиляторы на 5 с уходят на 100%, замеряются обороты, потом всё возвращается");
            tachTestBtn.Click += delegate { RunTachTest(); };
            quick.Controls.Add(tachTestBtn);
            Button help = UiKit.NewButton("Справка", 80);
            help.Height = 26;
            help.Margin = new Padding(2);
            help.Click += delegate { ShowHelp(); };
            quick.Controls.Add(help);

            // ---- строка ввода ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 32;
            sendBtn = UiKit.NewButton("Отправить", 96);
            sendBtn.Dock = DockStyle.Right;
            Button clear = UiKit.NewButton("Очистить", 80);
            clear.Dock = DockStyle.Right;
            Button copy = UiKit.NewButton("Копировать", 90);
            copy.Dock = DockStyle.Right;
            input = new TextBox();
            input.Dock = DockStyle.Fill;
            input.Font = new Font("Consolas", 10f);
            input.MaxLength = 200;
            bottom.Controls.Add(input);
            bottom.Controls.Add(copy);
            bottom.Controls.Add(clear);
            bottom.Controls.Add(sendBtn);
            input.KeyDown += OnInputKey;
            sendBtn.Click += delegate { SubmitInput(); };
            clear.Click += delegate { output.Items.Clear(); };
            copy.Click += delegate { CopyAll(); };
            tip.SetToolTip(input, "Команда платы, например SET=1,60 или TACHRAW=4. Enter — отправить, ↑/↓ — история");

            // ---- вывод ----
            output = new ListBox();
            output.Dock = DockStyle.Fill;
            output.Font = new Font("Consolas", 9f);
            output.IntegralHeight = false;
            output.HorizontalScrollbar = true;
            output.DrawMode = DrawMode.OwnerDrawFixed;
            output.ItemHeight = 19;
            output.DrawItem += OnDrawItem;

            Controls.Add(output);
            Controls.Add(bottom);
            Controls.Add(quick);

            link.Traffic += OnTraffic;

            pump = new Timer();
            pump.Interval = 150;
            pump.Tick += delegate { Drain(); CheckTachTimeout(); };
            pump.Start();

            Add(KindInfo, "Консоль платы. Кнопки сверху — запросы данных, ниже можно ввести любую команду. «Справка» — список команд.");
        }

        private void AddQuick(ToolTip tip, string text, string cmd, string hint)
        {
            Button b = UiKit.NewButton(text, 84);
            b.Height = 26;
            b.Margin = new Padding(2);
            tip.SetToolTip(b, hint);
            b.Click += delegate { Send(cmd); };
            quick.Controls.Add(b);
        }

        // ---------- вывод ----------
        private void OnTraffic(bool outgoing, string line)
        {
            Item it = new Item();
            it.Kind = outgoing ? KindOut : KindIn;
            it.Text = outgoing ? "> " + line : line;
            lock (pending) { pending.Enqueue(it); }
        }

        private void Drain()
        {
            List<Item> batch = new List<Item>();
            lock (pending) { while (pending.Count > 0) batch.Add(pending.Dequeue()); }
            if (batch.Count == 0) return;
            output.BeginUpdate();
            foreach (Item it in batch)
            {
                Add(it.Kind, it.Text);
                if (it.Kind == KindIn) FeedTachTest(it.Text);
            }
            output.EndUpdate();
            output.TopIndex = Math.Max(0, output.Items.Count - 1);
        }

        private void Add(int kind, string text)
        {
            Item it = new Item();
            it.Kind = kind;
            it.Text = text;
            output.Items.Add(it);
            if (output.Items.Count > 1000) output.Items.RemoveAt(0);
            output.TopIndex = Math.Max(0, output.Items.Count - 1);
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Item it = output.Items[e.Index] as Item;
            if (it == null) return;
            Color c;
            switch (it.Kind)
            {
                case KindOut: c = Color.RoyalBlue; break;
                case KindInfo: c = Color.Gray; break;
                case KindResult: c = Color.FromArgb(0, 130, 60); break;
                case KindError: c = Color.Firebrick; break;
                default: c = SystemColors.WindowText; break;
            }
            e.DrawBackground();
            using (Brush b = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : c))
                e.Graphics.DrawString(it.Text, e.Font, b, e.Bounds.Left + 2, e.Bounds.Top + 2);
            e.DrawFocusRectangle();
        }

        private void CopyAll()
        {
            StringBuilder sb = new StringBuilder();
            foreach (object o in output.Items) { Item it = o as Item; if (it != null) sb.AppendLine(it.Text); }
            try { if (sb.Length > 0) Clipboard.SetText(sb.ToString()); } catch { }
        }

        // ---------- ввод ----------
        private void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitInput(); }
            else if (e.KeyCode == Keys.Up && history.Count > 0)
            {
                e.SuppressKeyPress = true;
                if (historyPos > 0) historyPos--;
                input.Text = history[historyPos];
                input.SelectionStart = input.TextLength;
            }
            else if (e.KeyCode == Keys.Down && history.Count > 0)
            {
                e.SuppressKeyPress = true;
                if (historyPos < history.Count - 1) { historyPos++; input.Text = history[historyPos]; }
                else { historyPos = history.Count; input.Text = ""; }
                input.SelectionStart = input.TextLength;
            }
        }

        private void SubmitInput()
        {
            string cmd = input.Text.Trim();
            if (cmd.Length == 0) return;
            if (Send(cmd))
            {
                if (history.Count == 0 || history[history.Count - 1] != cmd) history.Add(cmd);
                if (history.Count > 50) history.RemoveAt(0);
                historyPos = history.Count;
                input.Text = "";
            }
        }

        // Команды, меняющие сохранённые настройки платы, - с подтверждением
        private static bool NeedsConfirm(string cmd)
        {
            string c = cmd.ToUpperInvariant();
            return c.EndsWith("=SAVE", StringComparison.Ordinal) || c.EndsWith("=RESET", StringComparison.Ordinal);
        }

        public bool Send(string cmd)
        {
            cmd = cmd.Replace("\r", "").Replace("\n", "").Trim();
            if (cmd.Length == 0) return false;
            if (NeedsConfirm(cmd) && !AutoConfirmForTests &&
                MessageBox.Show(this, "Команда " + cmd + " меняет сохранённые настройки платы. Отправить?", "MKS Fan Hub",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            string err;
            if (!link.ConsoleSend(cmd, out err))
            {
                Add(KindError, "Не отправлено: " + err);
                return false;
            }
            return true;
        }

        private void ShowHelp()
        {
            Add(KindInfo, "Запросы данных:  STATUS?  THERMS?  TACH?  CURVES?  BINDS?  MODE?");
            Add(KindInfo, "TACHRAW=n   сырые интервалы между импульсами tach канала n (отладка)");
            Add(KindInfo, "TACHTEST    тест тахо 3-pin (то же, что кнопка «Тест тахо»)");
            Add(KindInfo, "HOLD=<канал>,<%>[,<сек>]   удержать обороты канала (кривая не трогает; по умолчанию 30 с, максимум 300 с); HOLD=<канал>,OFF; HOLD=ALL,OFF; HOLD?");
            Add(KindInfo, "SPIN?   автозапуск DC (прошивка 0.3.24+): точки раскрутки и параметры; SPIN=<канал|ALL>,<%>; SPIN=PARAM,<добавка>,<шаг>,<мс>,<проверка_с>,<попыток>; SPIN=SAVE|RESET; SPIN=CLEAR[,<канал>] — сбросить ошибку; SPIN=TEST,<канал> — проверить сейчас");
            Add(KindInfo, "DCFREQ?   частоты ШИМ DC-каналов (прошивка 0.3.22+); DCFREQ=<канал|ALL>,<Гц 15..300> — сменить на лету; DCFREQ=SAVE — запомнить в плате; DCFREQ=RESET — заводские 31 Гц");
            Add(KindInfo, "TACHDUMP=<канал>   сырая запись tach-пина (1000 бинов по 1 мс); TACHFAST=<канал> — по 50 мкс. Удобнее через Меню → «Диагностика тахо…»");
            Add(KindInfo, "Плата сама присылает события EVENT=BOOT / FAILSAFE / MODE / BTN / THERM / HOLD / TACHGUARD (пульс EVENT=HB здесь скрыт)."); 
            Add(KindInfo, "MODE=0|1    режим платы: 0 = TMP-SRL, 1 = TMP-MAN");
            Add(KindInfo, "CURVE=<канал>,<точка 0-4>,<темп>,<%>   правка точки кривой (CURVE=SAVE / CURVE=RESET)");
            Add(KindInfo, "BIND=<канал>,THERM|SERIAL,<имя>        привязка (BIND=SAVE / BIND=RESET)");
            Add(KindInfo, "SET=<индекс>,<%>   выставить обороты. Внимание: кривая канала перебьёт значение примерно через секунду.");
            HubState st = link.Last;
            if (st != null)
            {
                StringBuilder sb = new StringBuilder("Индексы каналов для SET: ");
                foreach (ChannelInfo c in st.Channels)
                {
                    string nm = usage.ChannelName(c.Name);
                    sb.Append(c.Index).Append(" = ").Append(c.Name);
                    if (nm != c.Name) sb.Append(" (").Append(nm).Append(")");
                    sb.Append(";  ");
                }
                Add(KindInfo, sb.ToString());
            }
        }

        // ---------- тест тахометров ----------
        public void RunTachTest()
        {
            if (tachTesting) return;
            if (link.State != ConnState.Connected) { Add(KindError, "Нет связи с платой."); return; }
            if (!AutoConfirmForTests && MessageBox.Show(this, "Все 3-pin вентиляторы примерно на 5 секунд разгонятся до 100% (будет шумно), " +
                                "после замера вернутся к прежним оборотам. Запустить тест тахометров?",
                                "Тест тахо", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string err;
            if (!link.ConsoleSend("TACHTEST", out err)) { Add(KindError, "Не отправлено: " + err); return; }
            // флаг ставим сразу: ответ START придёт через долю секунды
            BeginTachTest();
        }

        private void BeginTachTest()
        {
            tachTesting = true;
            tachStartedAt = DateTime.Now;
            tachRows.Clear();
            tachTestBtn.Enabled = false;
            tachTestBtn.Text = "Идёт тест…";
        }

        private void EndTachTest()
        {
            tachTesting = false;
            tachTestBtn.Enabled = true;
            tachTestBtn.Text = "Тест тахо";
        }

        private void CheckTachTimeout()
        {
            if (tachTesting && (DateTime.Now - tachStartedAt).TotalSeconds > 20)
            {
                EndTachTest();
                Add(KindError, "Тест тахо не завершился за 20 с. Проверь связь с платой.");
            }
        }

        private void FeedTachTest(string line)
        {
            if (!line.StartsWith("TACHTEST=", StringComparison.Ordinal)) return;
            string body = line.Substring(9);
            if (body == "START") { if (!tachTesting) BeginTachTest(); return; }
            if (body == "END") { FinishTachTest(); return; }
            if (!tachTesting) return;
            string[] p = body.Split(',');
            if (p.Length < 4) return;
            TachRow r = new TachRow();
            r.Channel = p[0]; r.Pin = p[1]; r.State = p[2];
            int rpm;
            int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rpm);
            r.Rpm = rpm;
            tachRows.Add(r);
        }

        private void FinishTachTest()
        {
            List<string> summary = new List<string>();
            Add(KindResult, "Результат теста тахометров:");
            if (tachRows.Count == 0) Add(KindResult, "  (плата не вернула ни одного 3-pin канала)");
            foreach (TachRow r in tachRows)
            {
                string nm = usage.ChannelName(r.Channel);
                string label = nm == r.Channel ? r.Channel : nm + " (" + r.Channel + ")";
                string text;
                if (r.State == "OK")
                {
                    text = r.Rpm.ToString(CultureInfo.InvariantCulture) + " об/мин";
                    usage.MarkSeen(Usage.ChKey(r.Channel));      // вентилятор крутится - значит подключён
                }
                else if (r.State == "NOSIG") text = "нет сигнала (вентилятор не подключён или без тахо-провода)";
                else if (r.State == "STALL") text = "СТОИТ (есть сигнал, но нет вращения)";
                else text = Ru.Tach(r.State, r.Pin);
                Add(KindResult, "  " + label + ": " + text);
                summary.Add(label + " — " + text);
            }
            Log.Write("Тест тахо: " + (summary.Count == 0 ? "нет данных" : string.Join("; ", summary.ToArray())));
            EndTachTest();
            Action h = TachTestFinished;
            if (h != null) h();
        }
    }
}
