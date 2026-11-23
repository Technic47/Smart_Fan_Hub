// Редактор кривых: 5 точек (температура -> %) на канал, график, применение и сохранение в плату
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace MksFanHub
{
    public class CurveDialog : Form
    {
        private class ChItem
        {
            public ChannelInfo Ch;
            public string Label;
            public override string ToString() { return Label; }
        }

        private readonly HubLink link;
        private readonly Usage usage;
        private readonly Settings settings;

        private Button copyBtn, linkBtn;
        private Label linkLabel;
        private List<string> driftGroups = new List<string>();
        private ComboBox chCombo;
        private CheckBox allBox;
        private NumericUpDown[] tempBox = new NumericUpDown[ProtocolParsers.CurvePointCount];
        private NumericUpDown[] pctBox = new NumericUpDown[ProtocolParsers.CurvePointCount];
        private CurveChart chart;
        private Label info;
        private Label dirtyLabel;
        private Button applyBtn, saveBtn, resetBtn, reloadBtn;

        // что сейчас на плате / что редактируем
        private Dictionary<string, CurvePoint[]> board = new Dictionary<string, CurvePoint[]>();
        private Dictionary<string, CurvePoint[]> work = new Dictionary<string, CurvePoint[]>();
        private bool loading;
        private string initialChannel;

        public CurveDialog(HubLink l, Usage u, Settings s, string channelId)
        {
            link = l; usage = u; settings = s; initialChannel = channelId;

            Text = "Кривые вентиляторов";
            ClientSize = new Size(760, 512);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            Label cl = new Label(); cl.Text = "Канал:"; cl.SetBounds(12, 14, 50, 20);
            chCombo = new ComboBox(); chCombo.DropDownStyle = ComboBoxStyle.DropDownList; chCombo.SetBounds(64, 10, 260, 24);
            chCombo.SelectedIndexChanged += delegate { LoadSelected(); };
            allBox = new CheckBox(); allBox.Text = "все каналы"; allBox.SetBounds(334, 12, 110, 22);
            allBox.CheckedChanged += delegate { FillChannels(null); };

            Label h1 = new Label(); h1.Text = "Точка"; h1.SetBounds(12, 52, 50, 18);
            Label h2 = new Label(); h2.Text = "Темп., °C"; h2.SetBounds(70, 52, 90, 18);
            Label h3 = new Label(); h3.Text = "Обороты, %"; h3.SetBounds(166, 52, 90, 18);
            copyBtn = UiKit.NewButton("Копировать на…", 140); copyBtn.SetBounds(452, 8, 140, 28);
            linkBtn = UiKit.NewButton("Связать…", 150); linkBtn.SetBounds(598, 8, 150, 28);
            copyBtn.Click += OnCopy;
            linkBtn.Click += OnLink;
            ToolTip tt = new ToolTip();
            tt.SetToolTip(copyBtn, "Один раз скопировать эту кривую на другие каналы");
            tt.SetToolTip(linkBtn, "Связать каналы: правка кривой одного будет менять и остальные");
            Controls.Add(cl); Controls.Add(chCombo); Controls.Add(allBox); Controls.Add(copyBtn); Controls.Add(linkBtn);
            Controls.Add(h1); Controls.Add(h2); Controls.Add(h3);

            for (int i = 0; i < ProtocolParsers.CurvePointCount; i++)
            {
                int y = 76 + i * 34;
                Label n = new Label(); n.Text = (i + 1).ToString(CultureInfo.InvariantCulture); n.SetBounds(12, y + 3, 40, 20);
                tempBox[i] = Num(70, y, -20, 125);
                pctBox[i] = Num(166, y, 0, 100);
                tempBox[i].ValueChanged += OnEdit;
                pctBox[i].ValueChanged += OnEdit;
                Controls.Add(n); Controls.Add(tempBox[i]); Controls.Add(pctBox[i]);
            }

            linkLabel = new Label(); linkLabel.SetBounds(12, 248, 250, 72); linkLabel.ForeColor = Color.DimGray;
            Controls.Add(linkLabel);
            info = new Label(); info.SetBounds(12, 324, 250, 84); info.ForeColor = Color.DimGray;
            info.Text = "Между точками обороты считаются по прямой. Ниже первой точки — её %, выше последней — её %. " +
                        "Температуры должны идти по возрастанию.";
            dirtyLabel = new Label(); dirtyLabel.SetBounds(12, 410, 250, 20); dirtyLabel.ForeColor = Color.DarkOrange;
            Controls.Add(info); Controls.Add(dirtyLabel);

            chart = new CurveChart();
            chart.SetBounds(274, 40, 474, 386);
            Controls.Add(chart);

            applyBtn = UiKit.NewButton("Применить", 120); applyBtn.SetBounds(12, 472, 120, 30);
            saveBtn = UiKit.NewButton("Применить и сохранить", 170); saveBtn.SetBounds(138, 472, 170, 30);
            reloadBtn = UiKit.NewButton("Загрузить с платы", 140); reloadBtn.SetBounds(314, 472, 140, 30);
            resetBtn = UiKit.NewButton("Заводские…", 110); resetBtn.SetBounds(460, 472, 110, 30);
            Button close = UiKit.NewButton("Закрыть", 90); close.SetBounds(658, 472, 90, 30);
            close.DialogResult = DialogResult.Cancel;
            Label hint = new Label(); hint.SetBounds(12, 436, 736, 22); hint.ForeColor = Color.DimGray;
            hint.Text = "«Применить» действует до перезагрузки платы. «Применить и сохранить» записывает все кривые в память платы.";
            Controls.Add(hint);
            Controls.Add(applyBtn); Controls.Add(saveBtn); Controls.Add(reloadBtn); Controls.Add(resetBtn); Controls.Add(close);
            CancelButton = close;

            applyBtn.Click += delegate { Apply(false); };
            saveBtn.Click += delegate { Apply(true); };
            reloadBtn.Click += delegate { if (Dirty().Count == 0 || UiKit.Confirm(this, "Несохранённые правки будут потеряны. Загрузить с платы?")) LoadFromBoard(); };
            resetBtn.Click += OnReset;

            Shown += delegate { LoadFromBoard(); };
        }

        // колесо мыши над полем случайно меняло значения точек - здесь оно отключено (меняй стрелками, клавишами или вводом)
        private class NoWheelNumeric : NumericUpDown
        {
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                HandledMouseEventArgs h = e as HandledMouseEventArgs;
                if (h != null) h.Handled = true;
            }
        }

        private static NumericUpDown Num(int x, int y, int min, int max)
        {
            NumericUpDown n = new NoWheelNumeric();
            n.Minimum = min; n.Maximum = max;
            n.SetBounds(x, y, 86, 24);
            return n;
        }

        // ---------- список каналов ----------
        private void FillChannels(string select)
        {
            string keep = select;
            if (keep == null && chCombo.SelectedItem != null) keep = ((ChItem)chCombo.SelectedItem).Ch.Name;
            HubState st = link.Last;
            chCombo.Items.Clear();
            if (st == null) return;

            List<ChItem> list = new List<ChItem>();
            foreach (ChannelInfo c in st.Channels)
            {
                if (!c.Active) continue;
                bool used = usage.ChannelUsed(c, st.FindTach(c.Name));
                if (!used && !allBox.Checked && c.Name != keep) continue;
                ChItem it = new ChItem();
                it.Ch = c;
                string nm = usage.ChannelName(c.Name);
                it.Label = nm == c.Name ? c.Name : nm + "  [" + c.Name + "]";
                list.Add(it);
            }
            if (list.Count == 0 && !allBox.Checked) { allBox.Checked = true; return; }   // перезапустит FillChannels
            int sel = 0;
            for (int i = 0; i < list.Count; i++)
            {
                chCombo.Items.Add(list[i]);
                if (keep != null && list[i].Ch.Name == keep) sel = i;
            }
            if (chCombo.Items.Count > 0) chCombo.SelectedIndex = sel;
        }

        private ChItem Current { get { return chCombo.SelectedItem as ChItem; } }

        // ---------- обмен с платой ----------
        private void LoadFromBoard()
        {
            List<string> lines; string err;
            Cursor = Cursors.WaitCursor;
            bool ok = link.Query("CURVES?", delegate (string x) { return x.StartsWith("CURVE=END", StringComparison.Ordinal); }, 3000, out lines, out err);
            Cursor = Cursors.Default;
            if (!ok) { UiKit.Error(this, "Не удалось прочитать кривые: " + err); return; }
            board = ProtocolParsers.ParseCurves(lines);
            work = Clone(board);
            CurveGroups.Cleanup(settings, board.Keys);
            driftGroups = CurveGroups.Diverged(settings, board);
            FillChannels(initialChannel);
            initialChannel = null;
            LoadSelected();
            SetStatus("Кривые загружены с платы.");
        }

        private void Apply(bool save)
        {
            List<string> dirty = Dirty();
            string problem = Validate(dirty);
            if (problem != null) { UiKit.Error(this, problem); return; }

            Cursor = Cursors.WaitCursor;
            try
            {
                // что именно уйдёт в плату: в журнал (старое -> новое), чтобы любая неожиданная правка была видна
                foreach (string ch in dirty)
                {
                    CurvePoint[] old;
                    board.TryGetValue(ch, out old);
                    for (int i = 0; i < work[ch].Length; i++)
                    {
                        CurvePoint n = work[ch][i];
                        if (old != null && i < old.Length && old[i].Temp == n.Temp && old[i].Percent == n.Percent) continue;
                        Log.Write("Кривая " + ch + ", точка " + (i + 1) + ": " +
                                  (old != null && i < old.Length ? old[i].Temp + "°/" + old[i].Percent + "%" : "—") + " -> " + n.Temp + "°/" + n.Percent + "%");
                    }
                }
                foreach (string ch in dirty)
                {
                    CurvePoint[] p = work[ch];
                    CurvePoint[] orig;
                    board.TryGetValue(ch, out orig);
                    for (int i = 0; i < p.Length; i++)
                    {
                        // отправляем только изменённые точки: нетронутые не должны рисковать из-за сбоя связи
                        if (orig != null && i < orig.Length && orig[i].Temp == p[i].Temp && orig[i].Percent == p[i].Percent) continue;
                        string problem2 = SendPoint(ch, i, p[i]);
                        if (problem2 != null) { Cursor = Cursors.Default; UiKit.Error(this, problem2); return; }
                    }
                }
                // проверка: читаем кривые обратно и сверяем с тем, что отправляли; расхождения досылаем (до 2 раз)
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    List<string> bad = VerifyOnBoard(dirty);
                    if (bad == null) break;                                   // не удалось прочитать - проверим после перезагрузки списка
                    if (bad.Count == 0) { if (attempt > 0) Log.Write("Кривые: расхождения после отправки устранены повторной отправкой"); break; }
                    Log.Write("Кривые: плата вернула не то, что отправили (" + string.Join("; ", bad.ToArray()) + ") - досылаю");
                    if (attempt == 2)
                    {
                        Cursor = Cursors.Default;
                        UiKit.Error(this, "Плата хранит не те значения, что отправила программа, даже после повторов:\n" + string.Join("\n", bad.ToArray()) +
                                          "\n\nНе сохраняю. Нажми «Загрузить с платы» и проверь; подробности — в журнале.");
                        LoadFromBoard();
                        return;
                    }
                    foreach (string ch in dirty)
                        for (int i = 0; i < work[ch].Length; i++)
                        {
                            CurvePoint[] have;
                            if (lastRead != null && lastRead.TryGetValue(ch, out have) && have[i].Temp == work[ch][i].Temp && have[i].Percent == work[ch][i].Percent) continue;
                            string pr = SendPoint(ch, i, work[ch][i]);
                            if (pr != null) { Cursor = Cursors.Default; UiKit.Error(this, pr); return; }
                        }
                }
                foreach (string ch in dirty) Log.Write("Кривая " + ch + " применена: " + Describe(work[ch]));
                if (save)
                {
                    List<string> lines; string err;
                    if (!link.Query("CURVE=SAVE", delegate (string x) { return x.StartsWith("CURVE=SAVED", StringComparison.Ordinal); }, 2000, out lines, out err))
                    {
                        Cursor = Cursors.Default;
                        UiKit.Error(this, "Плата не подтвердила сохранение: " + err);
                        return;
                    }
                    Log.Write("Кривые сохранены в память платы");
                }
            }
            finally { Cursor = Cursors.Default; }

            // перечитываем с платы - показываем то, что там реально лежит
            string keep = Current != null ? Current.Ch.Name : null;
            initialChannel = keep;
            LoadFromBoard();
            SetStatus(save ? "Применено и сохранено в память платы." : "Применено (до перезагрузки платы).");
        }

        // отправляет одну точку; подтверждение CURVE=OK сверяется с отправленными значениями, при потере/искажении - до 3 попыток
        private string SendPoint(string ch, int i, CurvePoint pt)
        {
            string cmd = "CURVE=" + ch + "," + i + "," + pt.Temp + "," + pt.Percent;
            string prefix = "CURVE=OK," + ch + "," + i + ",";
            string want = prefix + pt.Temp + "," + pt.Percent;
            string last = null, errText = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                List<string> lines; string err;
                bool ok = link.Query(cmd, delegate (string x) { return x.StartsWith(prefix, StringComparison.Ordinal) || x.StartsWith("CURVE=ERR", StringComparison.Ordinal); }, 1500, out lines, out err);
                if (!ok) { errText = err; last = null; continue; }
                last = lines.Count > 0 ? lines[lines.Count - 1].Trim() : "";
                if (last.StartsWith("CURVE=ERR", StringComparison.Ordinal))
                    return "Плата отвергла точку " + (i + 1) + " канала " + ch + ": " + last;
                if (last == want) return null;
                Log.Write("Кривая " + ch + ", точка " + (i + 1) + ": отправлено «" + cmd + "», плата ответила «" + last + "» - повторяю");
            }
            if (last == null)
                return "Ошибка при отправке точки " + (i + 1) + " канала " + ch + ": " + errText +
                       "\n\nСмотри в журнале строку «Плата перезагрузилась…» — если она появилась в этот момент, плата сбрасывалась (причина указана там).";
            return "Плата принимает точку " + (i + 1) + " канала " + ch + " с искажением: отправлено " + pt.Temp + "°/" + pt.Percent + "%, подтверждено «" + last + "». Связь с платой портит данные.";
        }

        private Dictionary<string, CurvePoint[]> lastRead;

        // читает CURVES? и сверяет каналы channels с work. null - не удалось прочитать; иначе список расхождений (пусто = всё совпало)
        private List<string> VerifyOnBoard(List<string> channels)
        {
            List<string> lines; string err;
            if (!link.Query("CURVES?", delegate (string x) { return x.StartsWith("CURVE=END", StringComparison.Ordinal); }, 3000, out lines, out err)) return null;
            lastRead = ProtocolParsers.ParseCurves(lines);
            List<string> bad = new List<string>();
            foreach (string ch in channels)
            {
                CurvePoint[] have;
                if (!lastRead.TryGetValue(ch, out have) || have.Length != work[ch].Length) { bad.Add(usage.ChannelName(ch) + ": нет кривой"); continue; }
                for (int i = 0; i < have.Length; i++)
                    if (have[i].Temp != work[ch][i].Temp || have[i].Percent != work[ch][i].Percent)
                        bad.Add(usage.ChannelName(ch) + " точка " + (i + 1) + ": хотели " + work[ch][i].Temp + "°/" + work[ch][i].Percent + "%, на плате " + have[i].Temp + "°/" + have[i].Percent + "%");
            }
            return bad;
        }

        private void OnReset(object sender, EventArgs e)
        {
            if (!UiKit.Confirm(this, "Вернуть заводские кривые для ВСЕХ каналов?\nЗаводские значения вступят в силу сразу, а сохранённые в плате будут стёрты.")) return;
            List<string> lines; string err;
            Cursor = Cursors.WaitCursor;
            bool ok = link.Query("CURVE=RESET", delegate (string x) { return x.StartsWith("CURVE=RESET_OK", StringComparison.Ordinal); }, 2000, out lines, out err);
            Cursor = Cursors.Default;
            if (!ok) { UiKit.Error(this, "Не удалось сбросить: " + err); return; }
            Log.Write("Кривые сброшены к заводским");
            initialChannel = Current != null ? Current.Ch.Name : null;
            LoadFromBoard();
            SetStatus("Заводские кривые загружены.");
        }

        // ---------- правки ----------
        private static Dictionary<string, CurvePoint[]> Clone(Dictionary<string, CurvePoint[]> src)
        {
            Dictionary<string, CurvePoint[]> d = new Dictionary<string, CurvePoint[]>();
            foreach (KeyValuePair<string, CurvePoint[]> kv in src)
            {
                CurvePoint[] a = new CurvePoint[kv.Value.Length];
                for (int i = 0; i < a.Length; i++) a[i] = new CurvePoint(kv.Value[i].Temp, kv.Value[i].Percent);
                d[kv.Key] = a;
            }
            return d;
        }

        private static bool Same(CurvePoint[] a, CurvePoint[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i].Temp != b[i].Temp || a[i].Percent != b[i].Percent) return false;
            return true;
        }

        private List<string> Dirty()
        {
            List<string> d = new List<string>();
            foreach (KeyValuePair<string, CurvePoint[]> kv in work)
            {
                CurvePoint[] orig;
                if (!board.TryGetValue(kv.Key, out orig) || !Same(orig, kv.Value)) d.Add(kv.Key);
            }
            return d;
        }

        private string Validate(List<string> channels)
        {
            foreach (string ch in channels)
            {
                CurvePoint[] p = work[ch];
                for (int i = 0; i < p.Length; i++)
                {
                    if (p[i].Percent < 0 || p[i].Percent > 100) return "Канал " + ch + ": обороты должны быть от 0 до 100%.";
                    if (i > 0 && p[i].Temp < p[i - 1].Temp)
                        return "Канал " + usage.ChannelName(ch) + ": температура точки " + (i + 1) + " (" + p[i].Temp + "°) меньше, чем у точки " + i + " (" + p[i - 1].Temp + "°).\nТемпературы должны идти по возрастанию.";
                }
            }
            return null;
        }

        private static string Describe(CurvePoint[] p)
        {
            List<string> s = new List<string>();
            foreach (CurvePoint c in p) s.Add(c.Temp + "°=" + c.Percent + "%");
            return string.Join(" ", s.ToArray());
        }

        private void LoadSelected()
        {
            ChItem it = Current;
            if (it == null) return;
            CurvePoint[] p;
            if (!work.TryGetValue(it.Ch.Name, out p)) return;
            loading = true;
            for (int i = 0; i < p.Length; i++)
            {
                tempBox[i].Value = Math.Max(tempBox[i].Minimum, Math.Min(tempBox[i].Maximum, p[i].Temp));
                pctBox[i].Value = Math.Max(0, Math.Min(100, p[i].Percent));
            }
            loading = false;
            MarkChanged();
            RefreshChart();
            RefreshDirtyLabel();
            RefreshLinkLabel();
        }

        // подсвечиваем поля, которые отличаются от того, что сейчас лежит в плате
        private void MarkChanged()
        {
            ChItem it = Current;
            CurvePoint[] w = null, b = null;
            if (it != null) { work.TryGetValue(it.Ch.Name, out w); board.TryGetValue(it.Ch.Name, out b); }
            for (int i = 0; i < tempBox.Length; i++)
            {
                bool ct = w != null && b != null && i < w.Length && i < b.Length && w[i].Temp != b[i].Temp;
                bool cp = w != null && b != null && i < w.Length && i < b.Length && w[i].Percent != b[i].Percent;
                tempBox[i].BackColor = ct ? Color.LightGoldenrodYellow : SystemColors.Window;
                pctBox[i].BackColor = cp ? Color.LightGoldenrodYellow : SystemColors.Window;
            }
        }

        // ---------- копирование и связь кривых ----------
        private List<string> ChannelOrder()
        {
            List<string> o = new List<string>();
            HubState st = link.Last;
            if (st != null) foreach (ChannelInfo c in st.Channels) o.Add(c.Name);
            return o;
        }

        private string LabelOf(string name)
        {
            string nm = usage.ChannelName(name);
            return nm == name ? name : nm + "  [" + name + "]";
        }

        private List<KeyValuePair<string, string>> OtherChannels(string except)
        {
            List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
            HubState st = link.Last;
            if (st == null) return items;
            foreach (ChannelInfo c in st.Channels)
            {
                if (c.Name == except || !c.Active || !work.ContainsKey(c.Name)) continue;
                items.Add(new KeyValuePair<string, string>(c.Name, LabelOf(c.Name)));
            }
            return items;
        }

        private void OnCopy(object sender, EventArgs e)
        {
            ChItem cur = Current;
            if (cur == null) return;
            string name = cur.Ch.Name;
            List<KeyValuePair<string, string>> items = OtherChannels(name);
            List<string> sel = PickChannelsDialog.Pick(this, "Копировать кривую",
                "Кривая канала «" + usage.ChannelName(name) + "» будет скопирована на отмеченные каналы. " +
                "Это разовое копирование: связь между каналами не создаётся. Изменения вступят в силу после «Применить».",
                items, null);
            if (sel == null || sel.Count == 0) return;
            foreach (string t in sel)
            {
                work[t] = CurveGroups.CloneCurve(work[name]);
                CurveGroups.Propagate(settings, work, t);     // если у получателя есть связанные - они тоже
            }
            RefreshDirtyLabel();
            RefreshLinkLabel();
            SetStatus("Кривая скопирована на каналов: " + sel.Count + ". Нажми «Применить».");
        }

        private void OnLink(object sender, EventArgs e)
        {
            ChItem cur = Current;
            if (cur == null) return;
            string name = cur.Ch.Name;
            List<string> order = ChannelOrder();
            List<string> oldMembers = CurveGroups.Members(settings, CurveGroups.GroupOf(settings, name), order);
            oldMembers.Remove(name);
            List<string> sel = PickChannelsDialog.Pick(this, "Связать кривые",
                "Отметь каналы, у которых кривая всегда должна быть такой же, как у «" + usage.ChannelName(name) + "». " +
                "Правка кривой любого из них будет менять все. Снятая галочка разрывает связь. " +
                "Если отмеченный канал уже связан с другими, вся его группа присоединится к этой. " +
                "Кривые всех связанных каналов станут такими же, как у текущего.",
                OtherChannels(name), oldMembers);
            if (sel == null) return;
            foreach (string m in oldMembers) if (!sel.Contains(m)) CurveGroups.Unlink(settings, m);
            if (sel.Count == 0) CurveGroups.Unlink(settings, name);
            else CurveGroups.Link(settings, name, sel);
            CurveGroups.Cleanup(settings, order);
            CurveGroups.Propagate(settings, work, name);
            settings.Save();
            driftGroups = CurveGroups.Diverged(settings, board);
            RefreshDirtyLabel();
            RefreshLinkLabel();
            string g = CurveGroups.GroupOf(settings, name);
            SetStatus(g == null ? "Связь снята." : "Каналы связаны (гр. " + g + "). Нажми «Применить», чтобы кривая ушла в плату.");
        }

        private void RefreshLinkLabel()
        {
            ChItem it = Current;
            if (it == null) { linkLabel.Text = ""; return; }
            string g = CurveGroups.GroupOf(settings, it.Ch.Name);
            if (g == null)
            {
                linkLabel.ForeColor = Color.DimGray;
                linkLabel.Text = "Кривая этого канала сама по себе (не связана с другими).";
                linkBtn.Text = "Связать…";
                return;
            }
            List<string> mates = CurveGroups.Members(settings, g, ChannelOrder());
            mates.Remove(it.Ch.Name);
            List<string> names = new List<string>();
            foreach (string m in mates) names.Add(usage.ChannelName(m));
            string text = "Связана (гр. " + g + ") с: " + string.Join(", ", names.ToArray()) + ". Правка меняет все.";
            linkLabel.ForeColor = Color.SteelBlue;
            if (driftGroups.Contains(g))
            {
                text += "\nНа плате кривые группы различаются — любая правка их выровняет.";
                linkLabel.ForeColor = Color.DarkOrange;
            }
            linkLabel.Text = text;
            linkBtn.Text = "Связь: гр. " + g + "…";
        }

        private void OnEdit(object sender, EventArgs e)
        {
            if (loading) return;
            ChItem it = Current;
            if (it == null) return;
            CurvePoint[] p = work[it.Ch.Name];
            for (int i = 0; i < p.Length; i++)
            {
                p[i].Temp = (int)tempBox[i].Value;
                p[i].Percent = (int)pctBox[i].Value;
            }
            // связанные каналы получают ту же кривую
            if (CurveGroups.Propagate(settings, work, it.Ch.Name).Count > 0 || driftGroups.Count > 0) { driftGroups.Remove(CurveGroups.GroupOf(settings, it.Ch.Name)); }
            MarkChanged();
            RefreshChart();
            RefreshDirtyLabel();
            RefreshLinkLabel();
        }

        private void RefreshChart()
        {
            ChItem it = Current;
            if (it == null) { chart.SetData(null, null, null); return; }
            CurvePoint[] p = work[it.Ch.Name];
            // актуальные данные канала (источник мог смениться)
            HubState st = link.Last;
            ChannelInfo live = it.Ch;
            if (st != null) foreach (ChannelInfo c in st.Channels) if (c.Name == it.Ch.Name) live = c;
            float? t = UiKit.SourceTemp(st, live);
            chart.SetData(p, t, live.Percent);
        }

        private void RefreshDirtyLabel()
        {
            List<string> d = Dirty();
            dirtyLabel.Text = d.Count == 0 ? "" : "Не применено правок: каналов " + d.Count + ".";
            applyBtn.Enabled = d.Count > 0;
            saveBtn.Enabled = true;
        }

        private void SetStatus(string text)
        {
            info.Text = text + "\nМежду точками — прямая. Ниже первой точки — её %, выше последней — её %. Температуры по возрастанию.";
        }
    }
}
