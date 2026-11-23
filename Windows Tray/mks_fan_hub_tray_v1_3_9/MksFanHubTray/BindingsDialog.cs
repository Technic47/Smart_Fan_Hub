// Привязка каналов вентиляторов к источникам температуры (термистор / температура ПК)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MksFanHub
{
    public class BindingsDialog : Form
    {
        private class Row
        {
            public string Channel;
            public bool Locked;
            public Label Name;
            public Label Lock;
            public ComboBox Combo;
            public string BoardType, BoardName;   // что сейчас на плате
        }

        private readonly HubLink link;
        private readonly Usage usage;
        private List<Row> rows = new List<Row>();
        private Panel rowsPanel;
        private Label modeLabel;
        private Button modeBtn, applyBtn, saveBtn, resetBtn;
        private CheckBox allBox;
        private Label status;

        public BindingsDialog(HubLink l, Usage u)
        {
            link = l; usage = u;
            Text = "Привязка каналов к датчикам";
            ClientSize = new Size(640, 500);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            modeLabel = new Label(); modeLabel.SetBounds(12, 8, 470, 58);
            modeBtn = UiKit.NewButton("", 140); modeBtn.SetBounds(488, 12, 140, 28);
            modeBtn.Click += OnModeClick;
            Controls.Add(modeLabel); Controls.Add(modeBtn);

            Label h1 = new Label(); h1.Text = "Вентилятор"; h1.SetBounds(12, 70, 200, 18);
            Label h2 = new Label(); h2.Text = "Берёт температуру от"; h2.SetBounds(240, 70, 300, 18);
            Controls.Add(h1); Controls.Add(h2);

            rowsPanel = new Panel(); rowsPanel.SetBounds(8, 90, 624, 318); rowsPanel.AutoScroll = true;
            rowsPanel.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(rowsPanel);

            allBox = new CheckBox(); allBox.Text = "показать все каналы"; allBox.SetBounds(12, 414, 220, 22);
            allBox.CheckedChanged += delegate { Rebuild(); };
            status = new Label(); status.SetBounds(240, 414, 388, 36); status.ForeColor = Color.DimGray;
            Controls.Add(allBox); Controls.Add(status);

            applyBtn = UiKit.NewButton("Применить", 110); applyBtn.SetBounds(12, 460, 110, 30);
            saveBtn = UiKit.NewButton("Применить и сохранить", 170); saveBtn.SetBounds(128, 460, 170, 30);
            resetBtn = UiKit.NewButton("Заводские…", 110); resetBtn.SetBounds(304, 460, 110, 30);
            Button close = UiKit.NewButton("Закрыть", 90); close.SetBounds(538, 460, 90, 30);
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;
            Controls.Add(applyBtn); Controls.Add(saveBtn); Controls.Add(resetBtn); Controls.Add(close);
            applyBtn.Click += delegate { Apply(false); };
            saveBtn.Click += delegate { Apply(true); };
            resetBtn.Click += OnReset;

            Shown += delegate { Rebuild(); };
        }

        private string ModeName() { HubState st = link.Last; return st == null ? "?" : st.Mode; }

        private void UpdateModeUi()
        {
            string m = ModeName();
            if (m == "TMP-SRL")
            {
                modeLabel.Text = "Режим платы: TMP-SRL. Привязка GPU, вдува и выдува здесь зафиксирована (GPU ← температура P102, вдув/выдув ← CASE). Остальные каналы можно менять.";
                modeBtn.Text = "В режим TMP-MAN";
                modeBtn.Visible = true;
            }
            else if (m == "TMP-MAN")
            {
                modeLabel.Text = "Режим платы: TMP-MAN. Привязку можно менять у любого канала. При возврате в TMP-SRL GPU, вдув и выдув снова станут стандартными.";
                modeBtn.Text = "В режим TMP-SRL";
                modeBtn.Visible = true;
            }
            else { modeLabel.Text = "Режим платы: " + m; modeBtn.Visible = false; }
        }

        private void OnModeClick(object sender, EventArgs e)
        {
            string target = ModeName() == "TMP-SRL" ? "MODE=1" : "MODE=0";
            if (target == "MODE=0" && !UiKit.Confirm(this, "В режиме TMP-SRL привязка GPU, вдува и выдува вернётся к стандартной. Переключить?")) return;
            string err;
            if (!link.Send(target, out err)) { UiKit.Error(this, "Не удалось переключить режим: " + err); return; }
            Log.Write("Режим платы: " + target);
            Cursor = Cursors.WaitCursor;
            // ждём свежий STATUS с новым режимом
            DateTime until = DateTime.Now.AddSeconds(5);
            string want = target == "MODE=1" ? "TMP-MAN" : "TMP-SRL";
            while (DateTime.Now < until && ModeName() != want) { System.Threading.Thread.Sleep(100); Application.DoEvents(); }
            Cursor = Cursors.Default;
            Rebuild();
        }

        // ---------- построение строк ----------
        private void Rebuild()
        {
            HubState st = link.Last;
            List<string> lines; string err;
            Cursor = Cursors.WaitCursor;
            bool ok = link.Query("BINDS?", delegate (string x) { return x.StartsWith("BIND=END", StringComparison.Ordinal); }, 3000, out lines, out err);
            Cursor = Cursors.Default;
            UpdateModeUi();
            foreach (Row r in rows) { rowsPanel.Controls.Remove(r.Name); rowsPanel.Controls.Remove(r.Lock); rowsPanel.Controls.Remove(r.Combo); }
            rows.Clear();
            if (!ok || st == null) { status.Text = "Не удалось прочитать привязки: " + (err ?? "нет данных"); return; }

            List<BindInfo> binds = ProtocolParsers.ParseBinds(lines);
            List<SourceItem> sources = SourceItem.Build(st, usage);

            // какие каналы показывать
            List<BindInfo> shown = new List<BindInfo>();
            foreach (BindInfo b in binds)
            {
                ChannelInfo c = null;
                foreach (ChannelInfo x in st.Channels) if (x.Name == b.Channel) c = x;
                if (c == null || !c.Active) continue;
                if (allBox.Checked || usage.ChannelUsed(c, st.FindTach(c.Name))) shown.Add(b);
            }
            if (shown.Count == 0 && !allBox.Checked) { allBox.Checked = true; return; }

            int y = 6;
            foreach (BindInfo b in shown)
            {
                Row r = new Row();
                r.Channel = b.Channel; r.Locked = b.Locked; r.BoardType = b.SourceType; r.BoardName = b.SourceName;

                r.Name = new Label();
                string nm = usage.ChannelName(b.Channel);
                r.Name.Text = nm == b.Channel ? b.Channel : nm + "  [" + b.Channel + "]";
                r.Name.SetBounds(6, y + 4, 170, 20);

                r.Lock = new Label();
                r.Lock.Text = b.Locked ? "(фикс.)" : "";
                r.Lock.SetBounds(176, y + 4, 56, 20);
                r.Lock.ForeColor = Color.DimGray;

                r.Combo = new ComboBox();
                r.Combo.DropDownStyle = ComboBoxStyle.DropDownList;
                r.Combo.SetBounds(236, y, 360, 24);
                int sel = -1;
                for (int i = 0; i < sources.Count; i++)
                {
                    r.Combo.Items.Add(sources[i]);
                    if (sources[i].Same(b.SourceType, b.SourceName)) sel = i;
                }
                if (sel >= 0) r.Combo.SelectedIndex = sel;
                r.Combo.Enabled = !b.Locked;
                r.Combo.SelectedIndexChanged += delegate { UpdateButtons(); };

                rowsPanel.Controls.Add(r.Name); rowsPanel.Controls.Add(r.Lock); rowsPanel.Controls.Add(r.Combo);
                rows.Add(r);
                y += 32;
            }
            status.Text = "Привязка определяет, по какому датчику канал выбирает обороты (по кривой этого канала).";
            UpdateButtons();
        }

        private List<Row> Changed()
        {
            List<Row> res = new List<Row>();
            foreach (Row r in rows)
            {
                SourceItem it = r.Combo.SelectedItem as SourceItem;
                if (r.Locked || it == null) continue;
                if (!it.Same(r.BoardType, r.BoardName)) res.Add(r);
            }
            return res;
        }

        private void UpdateButtons()
        {
            applyBtn.Enabled = Changed().Count > 0;
        }

        // ---------- обмен с платой ----------
        private void Apply(bool save)
        {
            List<Row> ch = Changed();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (Row r in ch)
                {
                    SourceItem it = (SourceItem)r.Combo.SelectedItem;
                    string cmd = "BIND=" + r.Channel + "," + it.Type + "," + it.Name;
                    string chName = r.Channel;
                    List<string> lines; string err;
                    bool ok = link.Query(cmd, delegate (string x) {
                        return x.StartsWith("BIND=OK," + chName, StringComparison.Ordinal) || x.StartsWith("BIND=LOCKED," + chName, StringComparison.Ordinal);
                    }, 1500, out lines, out err);
                    if (!ok) { Cursor = Cursors.Default; UiKit.Error(this, "Ошибка при привязке " + r.Channel + ": " + err); return; }
                    if (lines.Count > 0 && lines[lines.Count - 1].StartsWith("BIND=LOCKED", StringComparison.Ordinal))
                    {
                        Cursor = Cursors.Default;
                        UiKit.Error(this, "Канал " + r.Channel + " зафиксирован в режиме TMP-SRL. Переключи плату в TMP-MAN.");
                        return;
                    }
                    Log.Write("Привязка " + r.Channel + " → " + it.Type + ":" + it.Name);
                }
                if (save)
                {
                    List<string> lines; string err;
                    if (!link.Query("BIND=SAVE", delegate (string x) { return x.StartsWith("BIND=SAVED", StringComparison.Ordinal); }, 2000, out lines, out err))
                    {
                        Cursor = Cursors.Default;
                        UiKit.Error(this, "Плата не подтвердила сохранение: " + err);
                        return;
                    }
                    Log.Write("Привязки сохранены в память платы");
                }
            }
            finally { Cursor = Cursors.Default; }
            Rebuild();
            status.Text = save ? "Применено и сохранено в память платы." : "Применено (до перезагрузки платы).";
        }

        private void OnReset(object sender, EventArgs e)
        {
            if (!UiKit.Confirm(this, "Вернуть заводские привязки для всех каналов?\nСохранённые в плате будут стёрты.")) return;
            List<string> lines; string err;
            Cursor = Cursors.WaitCursor;
            bool ok = link.Query("BIND=RESET", delegate (string x) { return x.StartsWith("BIND=RESET_OK", StringComparison.Ordinal); }, 2000, out lines, out err);
            Cursor = Cursors.Default;
            if (!ok) { UiKit.Error(this, "Не удалось сбросить: " + err); return; }
            Log.Write("Привязки сброшены к заводским");
            Rebuild();
            status.Text = "Заводские привязки загружены.";
        }
    }
}
