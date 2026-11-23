// Окно мониторинга и управления
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace MksFanHub
{
    public class MainForm : Form
    {
        public const string AppVersion = "1.3.9";

        private class Row
        {
            public string Key;          // "CH:..", "TH:..", "PC:.." (PC - без меню)
            public string[] Cells;
            public Color Color;
        }

        private readonly HubLink link;
        private readonly Settings settings;
        private readonly Usage usage;

        private Label statusLabel;
        private ListView tempList, chList;
        private GroupBox tempGroup, chGroup;
        private TabControl bottomTabs;
        private ConsolePanel console;
        private ListBox logBox;
        private TableLayoutPanel main;
        private Button menuBtn, bindBtn, curveBtn;
        private ContextMenuStrip menu;
        private ToolStripMenuItem showAllItem, modeSrlItem, modeManItem, autostartItem;
        private HealthResult lastHealth;

        public ConsolePanel Console { get { return console; } }
        public void ShowConsoleTab() { if (!settings.ShowLog) { settings.ShowLog = true; ApplySettings(); } bottomTabs.SelectedIndex = 1; }

        public bool AllowClose;
        public event Action ExitRequested;

        public MainForm(HubLink l, Settings s, Usage u)
        {
            link = l; settings = s; usage = u;
            Text = "MKS Fan Hub";
            Width = settings.WindowW;
            Height = settings.WindowH;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            // ---- панель кнопок ----
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 40;
            bar.Padding = new Padding(8, 6, 8, 0);
            menuBtn = UiKit.NewButton("Меню  ▾", 96);
            bindBtn = UiKit.NewButton("Привязки…", 110);
            curveBtn = UiKit.NewButton("Кривые…", 100);
            bar.Controls.Add(menuBtn); bar.Controls.Add(bindBtn); bar.Controls.Add(curveBtn);
            ToolTip tip = new ToolTip();
            tip.SetToolTip(bindBtn, "Какой датчик управляет каким вентилятором");
            tip.SetToolTip(curveBtn, "Кривая «температура → обороты» для каждого канала");
            tip.SetToolTip(menuBtn, "Настройки, режим платы, автозапуск");
            menuBtn.Click += delegate { menu.Show(menuBtn, new Point(0, menuBtn.Height)); };
            bindBtn.Click += delegate { OpenBindings(); };
            curveBtn.Click += delegate { OpenCurves(null); };
            BuildMenu();

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Top;
            statusLabel.Height = 30;
            statusLabel.Padding = new Padding(8, 6, 8, 0);
            statusLabel.Font = new Font(Font, FontStyle.Bold);

            tempList = MakeList(new string[] { "Датчик", "°C", "Состояние" }, new int[] { 150, 60, 110 });
            chList = MakeList(new string[] { "Название", "Выход", "%", "Датчик", "Кривая", "Тахо", "RPM" }, new int[] { 130, 85, 42, 150, 62, 85, 50 });
            tempList.Dock = DockStyle.Fill;
            chList.Dock = DockStyle.Fill;
            WireListMenus();

            logBox = new ListBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Font = new Font("Consolas", 9f);
            logBox.IntegralHeight = false;
            logBox.HorizontalScrollbar = true;
            logBox.SelectionMode = SelectionMode.One;

            tempGroup = Group("Температуры", tempList);
            chGroup = Group("Вентиляторы", chList);

            // нижняя панель: вкладки "Журнал" и "Консоль"
            bottomTabs = new TabControl();
            bottomTabs.Dock = DockStyle.Fill;
            TabPage pageLog = new TabPage("Журнал");
            pageLog.Controls.Add(logBox);
            console = new ConsolePanel(link, usage);
            console.TachTestFinished += delegate { RefreshNow(); };
            TabPage pageCon = new TabPage("Консоль");
            pageCon.Controls.Add(console);
            bottomTabs.TabPages.Add(pageLog);
            bottomTabs.TabPages.Add(pageCon);

            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.ColumnCount = 2;
            top.RowCount = 1;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64f));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            top.Controls.Add(tempGroup, 0, 0);
            top.Controls.Add(chGroup, 1, 0);

            main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.ColumnCount = 1;
            main.RowCount = 2;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));
            main.Controls.Add(top, 0, 0);
            main.Controls.Add(bottomTabs, 0, 1);

            Controls.Add(main);
            Controls.Add(statusLabel);
            Controls.Add(bar);

            ApplySettings();
            Log.Added += OnLog;
            FormClosing += OnClosing;
            Resize += OnResizeForm;
            VisibleChanged += delegate { UpdateLightMode(); };
        }

        // ---------- построение ----------
        private static GroupBox Group(string title, Control inner)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Dock = DockStyle.Fill;
            g.Padding = new Padding(6);
            g.Controls.Add(inner);
            return g;
        }

        private static ListView MakeList(string[] cols, int[] widths)
        {
            ListView lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            return lv;
        }

        // Свёрнутый режим: окно скрыто или свёрнуто -> плату опрашиваем реже (она сама присылает события)
        public void UpdateLightMode()
        {
            bool light = settings.LightMode && (!Visible || WindowState == FormWindowState.Minimized);
            link.SetLight(light);
        }

        public void ApplySettings()
        {
            TopMost = settings.AlwaysOnTop;
            UpdateLightMode();
            if (settings.ShowLog)
            {
                main.RowStyles[0] = new RowStyle(SizeType.Percent, 62f);
                main.RowStyles[1] = new RowStyle(SizeType.Percent, 38f);
                bottomTabs.Visible = true;
            }
            else
            {
                main.RowStyles[0] = new RowStyle(SizeType.Percent, 100f);
                main.RowStyles[1] = new RowStyle(SizeType.Absolute, 0f);
                bottomTabs.Visible = false;
            }
        }

        private void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Items.Add("Настройки…", null, delegate { OpenSettings(); });
            showAllItem = new ToolStripMenuItem("Показывать скрытые строки");
            showAllItem.Click += delegate
            {
                usage.ShowAllTemporarily = !usage.ShowAllTemporarily;
                RefreshNow();
            };
            menu.Items.Add(showAllItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mode = new ToolStripMenuItem("Режим платы");
            modeSrlItem = new ToolStripMenuItem("TMP-SRL — GPU ← P102, вдув/выдув ← CASE (по умолчанию)");
            modeManItem = new ToolStripMenuItem("TMP-MAN — привязка любых каналов");
            modeSrlItem.Click += delegate { SwitchMode(0); };
            modeManItem.Click += delegate { SwitchMode(1); };
            mode.DropDownItems.Add(modeSrlItem);
            mode.DropDownItems.Add(modeManItem);
            menu.Items.Add(mode);
            menu.Items.Add(new ToolStripSeparator());

            autostartItem = new ToolStripMenuItem("Запускать при входе в Windows");
            autostartItem.Click += delegate { ToggleAutostart(); };
            menu.Items.Add(autostartItem);
            menu.Items.Add("Тест тахометров (TACHTEST)…", null, delegate
            {
                ShowConsoleTab();
                console.RunTachTest();
            });
            menu.Items.Add("Диагностика тахо…", null, delegate { OpenTachDiag(); });
            menu.Items.Add("Автозапуск DC-вентиляторов…", null, delegate { OpenSpin(); });
            menu.Items.Add("Открыть файл журнала", null, delegate { Log.Open(); });
            menu.Items.Add("О программе", null, delegate { ShowAbout(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Свернуть в трей", null, delegate { Hide(); });
            menu.Items.Add("Выход", null, delegate { Action h = ExitRequested; if (h != null) h(); });

            menu.Opening += delegate
            {
                showAllItem.Checked = usage.ShowAllTemporarily;
                autostartItem.Checked = Autostart.IsEnabled();
                HubState st = link.Last;
                bool conn = link.State == ConnState.Connected && st != null;
                modeSrlItem.Enabled = modeManItem.Enabled = conn;
                modeSrlItem.Checked = conn && st.Mode == "TMP-SRL";
                modeManItem.Checked = conn && st.Mode == "TMP-MAN";
            };
        }

        // ---------- действия ----------
        private void OpenSettings()
        {
            using (SettingsDialog d = new SettingsDialog(settings, usage))
            {
                DialogResult r = d.ShowDialog(this);
                if (r == DialogResult.OK)
                {
                    ApplySettings();
                    if (d.PortChanged) link.RequestReconnect();
                }
                if (r == DialogResult.OK || d.ResetDone) RefreshNow();
            }
        }

        private void OpenBindings()
        {
            using (BindingsDialog d = new BindingsDialog(link, usage)) d.ShowDialog(this);
            RefreshNow();
        }

        private void OpenCurves(string channelId)
        {
            using (CurveDialog d = new CurveDialog(link, usage, settings, channelId)) d.ShowDialog(this);
            RefreshNow();
        }

        private void OpenManual(string channelId)
        {
            if (link.State != ConnState.Connected) { UiKit.Error(this, "Нет связи с платой."); return; }
            using (ManualDialog d = new ManualDialog(link, usage, channelId)) d.ShowDialog(this);
            RefreshNow();
        }

        private void OpenSpin()
        {
            if (link.State != ConnState.Connected) { UiKit.Error(this, "Нет связи с платой."); return; }
            using (SpinDialog d = new SpinDialog(link, usage)) d.ShowDialog(this);
            RefreshNow();
        }

        private void OpenTachDiag()
        {
            if (link.State != ConnState.Connected) { UiKit.Error(this, "Нет связи с платой."); return; }
            using (TachDiagDialog d = new TachDiagDialog(link, usage)) d.ShowDialog(this);
            RefreshNow();
        }

        private void SwitchMode(int m)
        {
            if (m == 0 && !UiKit.Confirm(this, "В режиме TMP-SRL привязка GPU, вдува и выдува вернётся к стандартной. Переключить?")) return;
            string err;
            if (!link.Send("MODE=" + m, out err)) { UiKit.Error(this, "Не удалось переключить режим: " + err); return; }
            Log.Write("Режим платы: MODE=" + m);
        }

        private void ToggleAutostart()
        {
            string err;
            bool now = Autostart.IsEnabled();
            bool ok = now ? Autostart.Disable(out err) : Autostart.Enable(out err);
            if (!ok) { Log.Write("Автозапуск: ошибка — " + err); UiKit.Error(this, "Не удалось изменить автозапуск:\n" + err); }
            else Log.Write(now ? "Автозапуск выключен" : "Автозапуск включён");
        }

        private void ShowAbout()
        {
            UiKit.Info(this, "MKS Fan Hub Tray " + AppVersion + "\nУправление и мониторинг USB-хаба вентиляторов (MKS Gen_L, прошивка 0.3.x).\n\nНастройки и журнал лежат рядом с программой:\n" +
                AppDomain.CurrentDomain.BaseDirectory);
        }

        // ---------- переименование / подключение ----------
        private void Rename(string key)
        {
            string id = key.Substring(3);
            bool ch = key.StartsWith("CH:", StringComparison.Ordinal);
            string current = ch ? usage.ChannelName(id) : usage.ThermShort(id);
            string label = (ch ? "Название вентилятора (выход " : "Название термистора (вход ") + id + "). Пусто — вернуть стандартное:";
            string v = UiKit.Ask(this, ch ? "Название канала" : "Название термистора", label, current == id ? "" : current);
            if (v == null) return;
            usage.SetName(key, v);
            RefreshNow();
        }

        private void SetConn(string key, int v)
        {
            usage.SetConn(key, v);
            RefreshNow();
        }

        private void WireListMenus()
        {
            tempList.ContextMenuStrip = MakeRowMenu(tempList, false);
            chList.ContextMenuStrip = MakeRowMenu(chList, true);
            tempList.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.F2) RenameSelected(tempList); };
            chList.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.F2) RenameSelected(chList); };
            tempList.DoubleClick += delegate { RenameSelected(tempList); };
            chList.DoubleClick += delegate
            {
                string key = SelectedKey(chList);
                if (key != null) OpenCurves(key.Substring(3));
            };
        }

        private static string SelectedKey(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return null;
            string k = lv.SelectedItems[0].Tag as string;
            if (k == null || k.StartsWith("PC:", StringComparison.Ordinal)) return null;
            return k;
        }

        private void RenameSelected(ListView lv)
        {
            string k = SelectedKey(lv);
            if (k != null) Rename(k);
        }

        private ContextMenuStrip MakeRowMenu(ListView lv, bool isChannel)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                // правый клик выделяет строку под курсором
                Point p = lv.PointToClient(Cursor.Position);
                ListViewItem hit = lv.GetItemAt(Math.Max(1, Math.Min(p.X, lv.Width - 2)), p.Y);
                if (hit != null) { hit.Selected = true; }
                string key = SelectedKey(lv);
                if (key == null) { e.Cancel = true; return; }

                m.Items.Clear();
                m.Items.Add("Переименовать…   F2", null, delegate { Rename(key); });
                if (isChannel)
                {
                    string chId = key.Substring(3);
                    m.Items.Add("Ручное тестирование…", null, delegate { OpenManual(chId); });
                    m.Items.Add("Кривая…", null, delegate { OpenCurves(chId); });
                    m.Items.Add("Привязка к датчику…", null, delegate { OpenBindings(); });
                }
                m.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem conn = new ToolStripMenuItem("Подключение");
                int cur = usage.GetConn(key);
                ToolStripMenuItem a = new ToolStripMenuItem("Авто (определять самому)"); a.Checked = cur == 0; a.Click += delegate { SetConn(key, 0); };
                ToolStripMenuItem y = new ToolStripMenuItem("Подключён"); y.Checked = cur == 1; y.Click += delegate { SetConn(key, 1); };
                ToolStripMenuItem n = new ToolStripMenuItem("Не подключён"); n.Checked = cur == 2; n.Click += delegate { SetConn(key, 2); };
                conn.DropDownItems.Add(a); conn.DropDownItems.Add(y); conn.DropDownItems.Add(n);
                m.Items.Add(conn);
            };
            return m;
        }

        // ---------- журнал ----------
        private void OnLog(string line)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); }
                else AppendLog(line);
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            logBox.Items.Add(line);
            if (logBox.Items.Count > 500) logBox.Items.RemoveAt(0);
            logBox.TopIndex = Math.Max(0, logBox.Items.Count - 1);
        }

        // ---------- закрытие / сворачивание ----------
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            SaveSize();
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // крестик прячет окно в трей
                Hide();
            }
        }

        private void OnResizeForm(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && settings.HideOnMinimize)
            {
                WindowState = FormWindowState.Normal;
                Hide();
            }
            UpdateLightMode();
        }

        private void SaveSize()
        {
            if (WindowState != FormWindowState.Normal) return;
            if (Width != settings.WindowW || Height != settings.WindowH)
            {
                settings.WindowW = Width;
                settings.WindowH = Height;
                settings.Save();
            }
        }

        // ---------- обновление ----------
        public void RefreshNow()
        {
            if (lastHealth != null) RefreshView(lastHealth);
        }

        private static Color RowColor(string state, bool strongOk)
        {
            switch (state)
            {
                case "OK": return Color.FromArgb(0, 140, 60);
                case "FAULT":
                case "STALL": return Color.Firebrick;
                case "ABSENT":
                case "NOSIG": return Color.DarkOrange;
                default: return SystemColors.WindowText;
            }
        }

        private static string Fmt(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }

        private string SourceText(HubState st, ChannelInfo c)
        {
            float? v = UiKit.SourceTemp(st, c);
            string name = c.SourceType == "THERM" ? usage.ThermShort(c.SourceName) : "ПК: " + c.SourceName;
            string val = v.HasValue ? " · " + Fmt(v.Value) + "°" : " · нет данных";
            return name + val + (c.Locked ? " (фикс.)" : "");
        }

        public void RefreshView(HealthResult health)
        {
            lastHealth = health;
            if (!Visible || WindowState == FormWindowState.Minimized) return;

            HubState st = link.Last;
            string port = link.State == ConnState.Connected ? link.PortName : "—";
            string mode = st != null ? st.Mode : "?";
            string age = link.LastStatusAt == DateTime.MinValue ? "нет" :
                ((int)(DateTime.Now - link.LastStatusAt).TotalSeconds).ToString(CultureInfo.InvariantCulture) + " с назад";
            statusLabel.Text = "Связь: " + LinkName(link.State) + " (" + port + ")   Режим: " + mode +
                               "   Данные: " + age + "   — " + health.Reason;
            statusLabel.ForeColor = health.Level == Health.Ok ? Color.FromArgb(0, 120, 50)
                                  : health.Level == Health.Warn ? Color.DarkOrange : Color.Firebrick;

            bool ready = link.State == ConnState.Connected && st != null;
            bindBtn.Enabled = ready;
            curveBtn.Enabled = ready;

            // ---- температуры ----
            List<Row> tRows = new List<Row>();
            int hiddenT = 0;
            if (settings.ShowPcTemps)
            {
                Dictionary<string, float> sent = link.LastSent;
                if (sent != null)
                    foreach (KeyValuePair<string, float> kv in sent)
                        tRows.Add(MakeRow("PC:" + kv.Key, new string[] { "ПК: " + kv.Key, Fmt(kv.Value), "→ в плату" }, RowColor("OK", false)));
            }
            if (st != null)
            {
                foreach (ThermInfo t in st.Therms)
                {
                    bool used = usage.ThermUsed(t);
                    if (!usage.ShouldShow(used, settings.UnusedTherms)) { hiddenT++; continue; }
                    string v = t.Value.HasValue ? Fmt(t.Value.Value) : "—";
                    bool dim = usage.IsDimmed(used, settings.UnusedTherms);
                    string stateText = dim ? "не используется" : Ru.Therm(t.State, t.Enabled);
                    Color col = dim ? SystemColors.GrayText : RowColor(t.Enabled ? t.State : "OFF", false);
                    tRows.Add(MakeRow(Usage.ThKey(t.Name), new string[] { usage.ThermTitle(t.Name), dim ? "—" : v, stateText }, col));
                }
            }
            tempGroup.Text = "Температуры" + (hiddenT > 0 ? "  (скрыто термисторов: " + hiddenT + ")" : "");
            SyncList(tempList, tRows);

            // ---- вентиляторы ----
            List<Row> cRows = new List<Row>();
            int hiddenC = 0;
            if (st != null)
            {
                foreach (ChannelInfo c in st.Channels)
                {
                    TachInfo tc = st.FindTach(c.Name);
                    bool used = c.Active && usage.ChannelUsed(c, tc);
                    if (!usage.ShouldShow(used, settings.UnusedFans)) { hiddenC++; continue; }
                    bool dim = usage.IsDimmed(used, settings.UnusedFans);
                    string tach = tc == null ? "—" : Ru.Tach(tc.State, tc.Pin);
                    string rpm = (tc != null && tc.State == "OK") ? tc.Rpm.ToString(CultureInfo.InvariantCulture) : "—";
                    string outName = c.Name + (c.Active ? "" : " (занят)");
                    Color col = dim ? SystemColors.GrayText : RowColor(tc == null ? "" : tc.State, false);
                    string grp = CurveGroups.Label(settings, c.Name);
                    string[] cells = dim
                        ? new string[] { usage.ChannelName(c.Name), outName, c.Percent.ToString(CultureInfo.InvariantCulture) + "%", "не используется", "—", "—", "—" }
                        : new string[] { usage.ChannelName(c.Name), outName, c.Percent.ToString(CultureInfo.InvariantCulture) + "%", SourceText(st, c), grp, tach, rpm };
                    cRows.Add(MakeRow(Usage.ChKey(c.Name), cells, col));
                }
            }
            chGroup.Text = "Вентиляторы" + (hiddenC > 0 ? "  (скрыто каналов: " + hiddenC + ")" : "");
            SyncList(chList, cRows);
        }

        private static Row MakeRow(string key, string[] cells, Color c)
        {
            Row r = new Row();
            r.Key = key; r.Cells = cells; r.Color = c;
            return r;
        }

        // Обновляем список на месте, чтобы не терять выделение и не мигать
        private static void SyncList(ListView lv, List<Row> rows)
        {
            bool same = lv.Items.Count == rows.Count;
            if (same)
                for (int i = 0; i < rows.Count; i++)
                    if ((string)lv.Items[i].Tag != rows[i].Key) { same = false; break; }

            if (same)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ListViewItem it = lv.Items[i];
                    for (int j = 0; j < rows[i].Cells.Length; j++)
                        if (it.SubItems[j].Text != rows[i].Cells[j]) it.SubItems[j].Text = rows[i].Cells[j];
                    if (it.ForeColor != rows[i].Color) it.ForeColor = rows[i].Color;
                }
                return;
            }

            string selected = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as string : null;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (Row r in rows)
            {
                ListViewItem it = new ListViewItem(r.Cells[0]);
                for (int j = 1; j < r.Cells.Length; j++) it.SubItems.Add(r.Cells[j]);
                it.ForeColor = r.Color;
                it.Tag = r.Key;
                if (selected != null && r.Key == selected) it.Selected = true;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
        }

        private static string LinkName(ConnState s)
        {
            switch (s)
            {
                case ConnState.Connected: return "есть";
                case ConnState.Searching: return "поиск платы";
                default: return "потеряна";
            }
        }
    }
}
