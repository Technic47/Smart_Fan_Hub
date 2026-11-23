// Ручное тестирование канала: ползунок % (HOLD= на плате), толчок 100%, живые обороты
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace MksFanHub
{
    public class ManualDialog : Form
    {
        private const int HoldSeconds = 30;      // плата сама вернёт кривую, если окно/программа пропали
        private const int KeepAliveMs = 10000;   // удержание продлевается
        private const int KickMs = 3000;

        private readonly HubLink link;
        private readonly Usage usage;
        private readonly string ch;

        private TrackBar bar;
        private NumericUpDown num;
        private Label stateLbl, resultLbl, warnLbl, holdLbl;
        private System.Windows.Forms.Timer debounce, uiTimer, keepTimer, kickTimer;
        private bool updating;
        private volatile bool holding;
        private volatile int pending = -1;
        private int sendBusy;
        private volatile string result = "";
        private volatile string warn = "";

        // подбор частоты ШИМ (только DC-каналы, прошивка 0.3.22+)
        private static readonly int[] FreqPresets = { 15, 20, 25, 31, 40, 50, 60, 80, 100, 150, 200 };
        private const int FreqMin = 15, FreqMax = 300, FreqDefault = 31;
        private readonly bool isDc;
        private NumericUpDown freqNum;
        private Label freqInfo, freqResult;
        private Control[] freqControls = new Control[0];
        private System.Windows.Forms.Timer freqDebounce;
        private bool freqUpdating;
        private volatile bool freqSupported;
        private volatile bool freqChecked;
        private volatile string freqMsg = "";
        private volatile int freqPending = -1;
        private int freqBusy;
        private volatile int freqActualHz;
        private volatile int freqPeriodTicks;

        public ManualDialog(HubLink l, Usage u, string channelId)
        {
            link = l; usage = u; ch = channelId;
            isDc = ch.StartsWith("FUN_DC_", StringComparison.Ordinal);
            int ex = isDc ? 130 : 0;     // место под блок частоты ШИМ
            string nm = usage.ChannelName(ch);
            Text = "Ручное тестирование — " + (nm == ch ? ch : nm + " [" + ch + "]");
            ClientSize = new Size(540, 372 + ex);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            Label intro = new Label();
            intro.SetBounds(12, 10, 516, 52);
            intro.ForeColor = Color.DimGray;
            intro.Text = "Ползунок задаёт заполнение ШИМ канала в процентах. Пока окно открыто, кривая канала не действует; " +
                         "при закрытии канал возвращается к кривой. DC-вентилятор с места на малых % может не стартовать — используй «Толчок».";
            Controls.Add(intro);

            Label cl = new Label();
            cl.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            cl.Text = (nm == ch ? ch : nm + "  [" + ch + "]");
            cl.SetBounds(12, 82, 516, 24);
            Controls.Add(cl);

            bar = new TrackBar();
            bar.Minimum = 0; bar.Maximum = 100; bar.TickFrequency = 10; bar.LargeChange = 10; bar.SmallChange = 1;
            bar.SetBounds(8, 112, 408, 45);
            num = new NumericUpDown();
            num.Minimum = 0; num.Maximum = 100; num.SetBounds(428, 116, 62, 24);
            Label pct = new Label(); pct.Text = "%"; pct.SetBounds(494, 119, 30, 20);
            Controls.Add(bar); Controls.Add(num); Controls.Add(pct);

            int[] quick = { 0, 20, 30, 40, 50, 60, 70, 80, 100 };
            int x = 12;
            foreach (int q in quick)
            {
                int v = q;
                Button b = UiKit.NewButton(q + "%", 52);
                b.SetBounds(x, 162, 52, 28);
                b.Click += delegate { SetValue(v); SendNow(); };
                Controls.Add(b);
                x += 57;
            }

            Button kick = UiKit.NewButton("Толчок: 100% на 3 с, затем выбранный %", 330);
            kick.SetBounds(12, 202, 330, 30);
            kick.Click += delegate { Kick(); };
            Controls.Add(kick);
            Label kh = new Label(); kh.ForeColor = Color.DimGray; kh.SetBounds(350, 208, 180, 20);
            kh.Text = "так запускаем DC с места";
            Controls.Add(kh);

            if (isDc) BuildFreqBlock();

            stateLbl = new Label(); stateLbl.SetBounds(12, 248 + ex, 516, 22); stateLbl.Font = new Font("Segoe UI", 10f);
            holdLbl = new Label(); holdLbl.SetBounds(12, 274 + ex, 516, 20); holdLbl.ForeColor = Color.SteelBlue;
            resultLbl = new Label(); resultLbl.SetBounds(12, 296 + ex, 516, 20); resultLbl.ForeColor = Color.DimGray;
            warnLbl = new Label(); warnLbl.SetBounds(12, 318 + ex, 516, 20); warnLbl.ForeColor = Color.Firebrick; warnLbl.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            Controls.Add(stateLbl); Controls.Add(holdLbl); Controls.Add(resultLbl); Controls.Add(warnLbl);

            Button release = UiKit.NewButton("Вернуть кривую", 140);
            release.SetBounds(12, 338 + ex, 140, 28);
            release.Click += delegate { Release(); };
            Button close = UiKit.NewButton("Закрыть", 90);
            close.SetBounds(438, 338 + ex, 90, 28);
            close.DialogResult = DialogResult.Cancel; CancelButton = close;
            Controls.Add(release); Controls.Add(close);

            debounce = new System.Windows.Forms.Timer(); debounce.Interval = 150;
            debounce.Tick += delegate { debounce.Stop(); SendNow(); };
            uiTimer = new System.Windows.Forms.Timer(); uiTimer.Interval = 500;
            uiTimer.Tick += delegate { RefreshState(); };
            keepTimer = new System.Windows.Forms.Timer(); keepTimer.Interval = KeepAliveMs;
            keepTimer.Tick += delegate { if (holding && Interlocked.CompareExchange(ref sendBusy, 0, 0) == 0) Enqueue((int)num.Value); };
            kickTimer = new System.Windows.Forms.Timer(); kickTimer.Interval = KickMs;
            kickTimer.Tick += delegate { kickTimer.Stop(); Enqueue((int)num.Value); };

            bar.ValueChanged += delegate { if (updating) return; updating = true; num.Value = bar.Value; updating = false; Changed(); };
            num.ValueChanged += delegate { if (updating) return; updating = true; bar.Value = (int)num.Value; updating = false; Changed(); };

            if (isDc)
            {
                freqDebounce = new System.Windows.Forms.Timer(); freqDebounce.Interval = 250;
                freqDebounce.Tick += delegate { freqDebounce.Stop(); FreqApply(); };
            }

            link.BoardEvent += OnBoardEvent;
            FormClosing += OnClosing;
            Shown += delegate
            {
                int cur = CurrentPercent();
                updating = true; bar.Value = cur; num.Value = cur; updating = false;
                uiTimer.Start(); keepTimer.Start();
                if (isDc) ThreadPool.QueueUserWorkItem(delegate { FreqLoad(); });
                RefreshState();
            };
        }

        private void BuildFreqBlock()
        {
            Label head = new Label();
            head.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            head.Text = "Частота ШИМ канала (подбор на слух, действует сразу)";
            head.SetBounds(12, 246, 516, 20);
            Controls.Add(head);

            Label l1 = new Label(); l1.Text = "Частота:"; l1.SetBounds(12, 273, 60, 20);
            freqNum = new NumericUpDown();
            freqNum.Minimum = FreqMin; freqNum.Maximum = FreqMax; freqNum.Value = FreqDefault;
            freqNum.SetBounds(74, 270, 70, 24);
            Label l2 = new Label(); l2.Text = "Гц"; l2.SetBounds(148, 273, 24, 20);
            freqInfo = new Label(); freqInfo.ForeColor = Color.DimGray; freqInfo.SetBounds(180, 273, 348, 20);
            Controls.Add(l1); Controls.Add(freqNum); Controls.Add(l2); Controls.Add(freqInfo);
            freqNum.ValueChanged += delegate { if (freqUpdating) return; freqDebounce.Stop(); freqDebounce.Start(); };

            List<Control> fc = new List<Control>();
            fc.Add(freqNum);
            for (int i = 0; i < FreqPresets.Length; i++)
            {
                int v = FreqPresets[i];
                Button b = UiKit.NewButton(v.ToString(CultureInfo.InvariantCulture), 44);
                b.SetBounds(12 + i * 47, 300, 44, 26);
                b.Click += delegate { freqUpdating = true; freqNum.Value = v; freqUpdating = false; freqDebounce.Stop(); FreqApply(); };
                Controls.Add(b); fc.Add(b);
            }

            Button save = UiKit.NewButton("Сохранить частоты в плату", 200);
            save.SetBounds(12, 332, 200, 28);
            save.Click += delegate { FreqCommand("DCFREQ=SAVE", "DCFREQ=SAVED", "Сохранено в плату: частоты всех DC-каналов (сохраняются и остальные каналы)"); };
            Button all = UiKit.NewButton("Всем DC-каналам", 140);
            all.SetBounds(218, 332, 140, 28);
            all.Click += delegate { FreqApply(true); };
            Button def = UiKit.NewButton("Заводская (" + FreqDefault + " Гц)", 150);
            def.SetBounds(364, 332, 150, 28);
            def.Click += delegate { FreqReset(); };
            Controls.Add(save); Controls.Add(all); Controls.Add(def);
            fc.Add(save); fc.Add(all); fc.Add(def);

            freqResult = new Label(); freqResult.ForeColor = Color.DimGray; freqResult.SetBounds(12, 364, 516, 20);
            Controls.Add(freqResult);

            freqControls = fc.ToArray();
            foreach (Control c in freqControls) c.Enabled = false;
            freqMsg = "Проверяю поддержку частоты в прошивке…";
        }

        // читает DCFREQ? и находит строку своего канала; на старой прошивке ответа нет
        private void FreqLoad()
        {
            List<string> lines; string err;
            bool ok = link.Query("DCFREQ?",
                delegate (string s) { return s.StartsWith("DCFREQ=END", StringComparison.Ordinal) || s.StartsWith("DCFREQ=ERR", StringComparison.Ordinal); },
                1500, out lines, out err);
            bool found = false;
            if (ok)
                foreach (string raw in lines)
                {
                    string[] a = raw.Trim().Split(',');
                    if (a.Length >= 3 && a[0] == "DCFREQ=" + ch)
                    {
                        int hz, per;
                        if (int.TryParse(a[1], out hz) && int.TryParse(a[2], out per))
                        {
                            freqActualHz = hz; freqPeriodTicks = per; found = true;
                        }
                    }
                }
            freqSupported = found; freqChecked = true;
            freqMsg = found ? "Частота канала прочитана из платы" :
                "Нужна прошивка 0.3.22 или новее (на DCFREQ нет ответа)";
        }

        private void FreqApply() { FreqApply(false); }

        private void FreqApply(bool allChannels)
        {
            if (!freqSupported) return;
            int hz = (int)freqNum.Value;
            freqPending = hz;
            ThreadPool.QueueUserWorkItem(delegate { FreqPump(allChannels); });
        }

        private void FreqPump(bool allChannels)
        {
            if (Interlocked.CompareExchange(ref freqBusy, 1, 0) != 0) return;   // текущий обмен подхватит последнее значение
            try
            {
                while (true)
                {
                    int hz = freqPending;
                    if (hz < 0) break;
                    freqPending = -1;
                    List<string> lines; string err;
                    string target = allChannels ? "ALL" : ch;
                    bool ok = link.Query("DCFREQ=" + target + "," + hz,
                        delegate (string s) { return s.StartsWith("DCFREQ=", StringComparison.Ordinal); }, 1500, out lines, out err);
                    string last = (ok && lines.Count > 0) ? lines[lines.Count - 1].Trim() : null;
                    if (last != null && last.StartsWith("DCFREQ=OK,", StringComparison.Ordinal))
                    {
                        string[] a = last.Split(',');
                        int h2, per;
                        if (a.Length >= 4 && int.TryParse(a[2], out h2) && int.TryParse(a[3], out per)) { freqActualHz = h2; freqPeriodTicks = per; }
                        freqMsg = (allChannels ? "Всем DC-каналам: " : "Применено: ") + hz + " Гц (не сохранено в плату)";
                        Log.Write("Ручное тестирование " + ch + ": частота ШИМ " + hz + " Гц" + (allChannels ? " (всем DC)" : ""));
                    }
                    else freqMsg = "Плата не приняла частоту: " + (last ?? err ?? "нет ответа");
                    allChannels = false;
                }
            }
            finally { Interlocked.Exchange(ref freqBusy, 0); }
        }

        private void FreqCommand(string cmd, string okPrefix, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> lines; string err;
                bool ok = link.Query(cmd, delegate (string s) { return s.StartsWith("DCFREQ=", StringComparison.Ordinal); }, 1500, out lines, out err);
                string last = (ok && lines.Count > 0) ? lines[lines.Count - 1].Trim() : null;
                if (last != null && last.StartsWith(okPrefix, StringComparison.Ordinal)) { freqMsg = okText; Log.Write("Ручное тестирование: " + cmd); }
                else freqMsg = "Плата не приняла команду: " + (last ?? err ?? "нет ответа");
            });
        }

        private void FreqReset()
        {
            if (!freqSupported) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> lines; string err;
                bool ok = link.Query("DCFREQ=RESET", delegate (string s) { return s.StartsWith("DCFREQ=", StringComparison.Ordinal); }, 1500, out lines, out err);
                string last = (ok && lines.Count > 0) ? lines[lines.Count - 1].Trim() : null;
                if (last != null && last.StartsWith("DCFREQ=RESET_OK", StringComparison.Ordinal))
                {
                    Log.Write("Ручное тестирование: DCFREQ=RESET");
                    FreqLoad();
                    freqMsg = "Всем DC-каналам возвращена заводская частота " + FreqDefault + " Гц";
                    freqReload = true;
                }
                else freqMsg = "Плата не приняла команду: " + (last ?? err ?? "нет ответа");
            });
        }
        private volatile bool freqReload;

        private void FreqRefreshUi()
        {
            foreach (Control c in freqControls) c.Enabled = freqSupported;
            if (freqSupported && (freqReload || !freqUiInit) && freqActualHz >= FreqMin && freqActualHz <= FreqMax)
            {
                freqUpdating = true; freqNum.Value = freqActualHz; freqUpdating = false;
                freqUiInit = true; freqReload = false;
            }
            if (freqSupported && freqPeriodTicks > 0 && freqActualHz > 0)
            {
                double ms = freqPeriodTicks * 0.25;   // тик 4 кГц = 0,25 мс
                freqInfo.Text = "период " + ms.ToString("0.##", CultureInfo.InvariantCulture) + " мс, " + freqPeriodTicks +
                                " ш. заполнения (шаг ~" + (100.0 / freqPeriodTicks).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
            }
            else freqInfo.Text = "";
            freqResult.Text = freqMsg;
        }
        private bool freqUiInit;

        private int CurrentPercent()
        {
            HubState st = link.Last;
            if (st != null)
                foreach (ChannelInfo c in st.Channels)
                    if (c.Name == ch) return Math.Max(0, Math.Min(100, c.Percent));
            return 0;
        }

        private void SetValue(int v)
        {
            kickTimer.Stop();
            updating = true; bar.Value = v; num.Value = v; updating = false;
        }

        private void Changed()
        {
            kickTimer.Stop();            // ручное движение отменяет толчок
            debounce.Stop(); debounce.Start();
        }

        private void SendNow()
        {
            debounce.Stop();
            kickTimer.Stop();
            Enqueue((int)num.Value);
        }

        private void Kick()
        {
            debounce.Stop();
            Enqueue(100);
            kickTimer.Stop(); kickTimer.Start();
            result = "Толчок: 100% на " + (KickMs / 1000) + " с, затем " + (int)num.Value + "%";
        }

        // последнее значение побеждает: пока идёт обмен с платой, новое просто заменяет ожидающее
        private void Enqueue(int pct)
        {
            holding = true;
            pending = pct;
            if (Interlocked.CompareExchange(ref sendBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate { Pump(); });
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    int v = pending;
                    if (v < 0) break;
                    pending = -1;
                    List<string> lines; string err;
                    bool ok = link.Query("HOLD=" + ch + "," + v + "," + HoldSeconds,
                        delegate (string s) { return s.StartsWith("HOLD=", StringComparison.Ordinal); }, 1500, out lines, out err);
                    string last = (ok && lines.Count > 0) ? lines[lines.Count - 1].Trim() : null;
                    if (last != null && last.StartsWith("HOLD=OK,", StringComparison.Ordinal))
                    {
                        result = "Отправлено: " + v + "%";
                        Log.Write("Ручное тестирование " + ch + ": " + v + "%");
                    }
                    else if (last != null && last.StartsWith("HOLD=ERR,failsafe", StringComparison.Ordinal))
                    {
                        holding = false;
                        result = "Плата в аварийном режиме (FAILSAFE) — удержание не принимается. Проверь связь программы с платой.";
                    }
                    else
                    {
                        result = "Плата не приняла команду: " + (last ?? err ?? "нет ответа");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref sendBusy, 0);
                if (pending >= 0 && Interlocked.CompareExchange(ref sendBusy, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(delegate { Pump(); });
            }
        }

        private void Release()
        {
            kickTimer.Stop(); debounce.Stop();
            pending = -1;
            holding = false;
            string err;
            link.Send("HOLD=" + ch + ",OFF", out err);
            result = "Канал возвращён к кривой";
            RefreshState();
        }

        // вызывается из рабочего потока HubLink
        private void OnBoardEvent(string text)
        {
            if (text.StartsWith("BOOT", StringComparison.Ordinal))
            {
                holding = false; pending = -1;
                warn = "Плата перезагрузилась в " + DateTime.Now.ToString("HH:mm:ss") + " (" + text + ") — удержание сброшено.";
            }
            else if (text.StartsWith("FAILSAFE,ON", StringComparison.Ordinal))
            {
                holding = false;
                warn = "FAILSAFE в " + DateTime.Now.ToString("HH:mm:ss") + " — плата не получала температуры, все каналы 100%.";
            }
            else if (text.StartsWith("HOLD," + ch + ",", StringComparison.Ordinal))
            {
                holding = false;
                result = text.EndsWith("END") ? "Удержание закончилось по времени" : "Удержание снято платой (" + text + ")";
            }
        }

        private void RefreshState()
        {
            HubState st = link.Last;
            string s = "Нет данных от платы";
            if (st != null)
            {
                foreach (ChannelInfo c in st.Channels)
                {
                    if (c.Name != ch) continue;
                    TachInfo tc = st.FindTach(ch);
                    s = "Выход сейчас: " + c.Percent + "%";
                    if (tc != null)
                    {
                        s += "     тахо: " + Ru.Tach(tc.State, tc.Pin);
                        if (tc.State == "OK") s += ", " + tc.Rpm.ToString(CultureInfo.InvariantCulture) + " об/мин";
                        else if (tc.State == "PWM") s += " (обороты на 3-pin меряются только при 100%)";
                    }
                    break;
                }
            }
            stateLbl.Text = s;
            holdLbl.Text = holding ? "Удержание включено (продлевается каждые " + (KeepAliveMs / 1000) + " с)" : "Удержание не включено — канал работает по кривой";
            resultLbl.Text = result;
            warnLbl.Text = warn;
            if (isDc) FreqRefreshUi();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            uiTimer.Stop(); keepTimer.Stop(); kickTimer.Stop(); debounce.Stop();
            if (freqDebounce != null) freqDebounce.Stop();
            link.BoardEvent -= OnBoardEvent;
            pending = -1;
            if (holding)
            {
                holding = false;
                string err;
                link.Send("HOLD=" + ch + ",OFF", out err);
            }
        }
    }
}
