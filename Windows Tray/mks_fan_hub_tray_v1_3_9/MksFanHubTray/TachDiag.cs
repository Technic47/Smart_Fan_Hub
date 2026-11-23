// Диагностика тахометра 3-pin вентиляторов: снятие "сырых" записей tach-пина (TACHDUMP/TACHFAST, прошивка 0.3.15+)
// на разных уровнях ШИМ в файл tachdump.log рядом с программой. Нужны для разбора, как отличать
// крутящийся вентилятор от остановленного, когда ШИМ рубит tach-линию.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

namespace MksFanHub
{
    public static class TachDiag
    {
        public static string PathOverride;   // для тестов
        public static string FilePath
        {
            get { return PathOverride ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tachdump.log"); }
        }

        private static Func<string, bool> Starts(string a, string b)
        {
            return delegate (string x) { return x.StartsWith(a, StringComparison.Ordinal) || (b != null && x.StartsWith(b, StringComparison.Ordinal)); };
        }

        private static bool Wait(double sec, Func<bool> cancelled)
        {
            DateTime until = DateTime.Now.AddSeconds(sec);
            while (DateTime.Now < until)
            {
                if (cancelled()) return false;
                Thread.Sleep(100);
            }
            return !cancelled();
        }

        // Одна запись: TACHDUMP (1 мс на бин) или TACHFAST (50 мкс на бин)
        private static bool Dump(HubLink link, string cmd, out List<string> lines, out string error)
        {
            error = null;
            string err;
            if (!link.Query(cmd, Starts("TACHDUMP=END", "TACHDUMP=ERR"), 8000, out lines, out err))
            {
                error = err + " (нужна прошивка 0.3.15 или новее)";
                return false;
            }
            string last = lines.Count > 0 ? lines[lines.Count - 1] : "";
            if (last.StartsWith("TACHDUMP=ERR", StringComparison.Ordinal))
            {
                error = "плата отказалась снимать запись: " + last;
                return false;
            }
            return true;
        }

        // Одна повторная попытка: на случай, если ответ платы потерялся
        private static bool DumpRetry(HubLink link, string cmd, out List<string> lines, out string error)
        {
            if (Dump(link, cmd, out lines, out error)) return true;
            Thread.Sleep(1200);
            return Dump(link, cmd, out lines, out error);
        }

        // Снять записи канала на уровнях duties. blocked - вентилятор придерживают рукой (только метка в файле).
        public static bool Capture(HubLink link, string channel, bool blocked, string note, int[] duties, double settleSec,
                                   Action<string> progress, Func<bool> cancelled, out string error)
        {
            error = null;
            string file = FilePath;
            List<string> lines;
            string err;
            int holdSec = (int)Math.Ceiling(settleSec) + 10;
            bool any = false;
            bool first = true;
            try
            {
                string head = "### SESSION " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                              " channel=" + channel + " state=" + (blocked ? "BLOCKED" : "RUN") +
                              " note=" + (note ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;

                foreach (int duty in duties)
                {
                    if (cancelled()) break;
                    progress("Уровень " + duty + "%: удерживаю канал…");
                    if (!link.Query("HOLD=" + channel + "," + duty + "," + holdSec, Starts("HOLD=", null), 1500, out lines, out err))
                    {
                        error = err + " (нужна прошивка 0.3.15 или новее)";
                        return false;
                    }
                    string ack = lines[lines.Count - 1];
                    if (!ack.StartsWith("HOLD=OK", StringComparison.Ordinal)) { error = "плата не приняла удержание: " + ack; return false; }

                    double wait = (first && !blocked) ? settleSec + 2.0 : settleSec;   // с места первый раз раскручиваем дольше
                    first = false;
                    progress("Уровень " + duty + "%: жду " + wait.ToString("0.#", CultureInfo.InvariantCulture) + " с, пока обороты установятся…");
                    if (!Wait(wait, cancelled)) break;

                    progress("Уровень " + duty + "%: запись 1 мс × 1000…");
                    List<string> slow;
                    if (!DumpRetry(link, "TACHDUMP=" + channel, out slow, out error)) return false;

                    string tachLine = "";
                    List<string> t;
                    if (link.Query("TACH?", Starts("TACH=END", null), 1500, out t, out err))
                        foreach (string x in t) if (x.StartsWith("TACH=" + channel + ",", StringComparison.Ordinal)) tachLine = x;

                    progress("Уровень " + duty + "%: запись 50 мкс × 1000…");
                    List<string> fast;
                    if (!DumpRetry(link, "TACHFAST=" + channel, out fast, out error)) return false;

                    StringBuilder sb = new StringBuilder();
                    if (!any) sb.Append(head);     // заголовок сессии - только когда есть хотя бы одна запись
                    sb.AppendLine("### CAPTURE duty=" + duty + " kind=SLOW fw_tach=" + tachLine);
                    foreach (string x in slow) sb.AppendLine(x);
                    sb.AppendLine("### CAPTURE duty=" + duty + " kind=FAST");
                    foreach (string x in fast) sb.AppendLine(x);
                    File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                    any = true;
                    progress("Уровень " + duty + "%: записано.");
                }
            }
            catch (IOException ex) { error = "не удалось записать файл: " + ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = "нет доступа к файлу: " + ex.Message; return false; }
            finally
            {
                string e2;
                List<string> l2;
                link.Query("HOLD=" + channel + ",OFF", Starts("HOLD=", null), 1500, out l2, out e2);
            }
            if (!any && !cancelled()) { error = "ничего не записано"; return false; }
            return true;
        }

        // Сколько сессий уже в файле: крутится / заблокирован
        public static void CountSessions(out int run, out int blocked)
        {
            run = 0; blocked = 0;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string l in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (!l.StartsWith("### SESSION", StringComparison.Ordinal)) continue;
                    if (l.IndexOf(" state=BLOCKED", StringComparison.Ordinal) >= 0) blocked++;
                    else run++;
                }
            }
            catch { }
        }

        public static int[] ParseDuties(string text, out string error)
        {
            error = null;
            List<int> res = new List<int>();
            foreach (string raw in text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0 || v > 100)
                {
                    error = "Уровни — числа от 0 до 100 через запятую (например 30, 50, 70, 100). Не понял: «" + raw + "»";
                    return null;
                }
                res.Add(v);
            }
            if (res.Count == 0) { error = "Укажи хотя бы один уровень, %."; return null; }
            if (res.Count > 12) { error = "Не больше 12 уровней за раз."; return null; }
            return res.ToArray();
        }
    }

    public class TachDiagDialog : Form
    {
        private class ChItem
        {
            public string Name, Label;
            public override string ToString() { return Label; }
        }

        private const string DutiesRun = "100, 70, 50, 30";
        private const string DutiesBlocked = "40, 70, 100";

        private readonly HubLink link;
        private readonly Usage usage;
        private ComboBox chCombo;
        private RadioButton runRb, blockRb;
        private TextBox noteBox, dutyBox;
        private Button startBtn, stopBtn, openBtn;
        private ListBox log;
        private Label countLabel;
        private volatile bool cancel;
        private volatile bool running;
        private Thread worker;

        public TachDiagDialog(HubLink l, Usage u)
        {
            link = l; usage = u;
            Text = "Диагностика тахометра";
            ClientSize = new Size(660, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            Label intro = new Label();
            intro.SetBounds(12, 10, 636, 112);   // 5-6 строк
            intro.ForeColor = Color.DimGray;
            intro.Text = "Снимаем «сырые» записи tach-пина, чтобы научиться отличать крутящийся 3-pin вентилятор от остановленного " +
                         "при неполном ШИМ.\n" +
                         "1) «Крутится»: вентилятор вращается свободно. Уровни идут СВЕРХУ ВНИЗ (100 → 30): сначала раскрутка на 100%, потом вентилятор уже крутится на малых оборотах — с места на 30–70% DC-вентилятор не стартует.\n" +
                         "2) «Заблокирован»: придержи крыльчатку (мягко, на несколько секунд на каждый уровень) и нажми «Снять». " +
                         "Повтори в 10+ разных положениях лопастей — между запусками чуть поворачивай крыльчатку.\n" +
                         "Записи копятся в файле tachdump.log рядом с программой. Пришли его целиком.";
            Controls.Add(intro);

            Label l1 = new Label(); l1.Text = "Канал:"; l1.SetBounds(12, 132, 90, 20);
            chCombo = new ComboBox(); chCombo.DropDownStyle = ComboBoxStyle.DropDownList; chCombo.SetBounds(110, 128, 330, 24);
            Label l2 = new Label(); l2.Text = "Состояние:"; l2.SetBounds(12, 164, 90, 20);
            runRb = new RadioButton(); runRb.Text = "Крутится"; runRb.SetBounds(110, 162, 120, 22); runRb.Checked = true;
            blockRb = new RadioButton(); blockRb.Text = "Заблокирован"; blockRb.SetBounds(236, 162, 150, 22);
            Label l3 = new Label(); l3.Text = "Уровни, %:"; l3.SetBounds(12, 196, 90, 20);
            dutyBox = new TextBox(); dutyBox.SetBounds(110, 192, 330, 24); dutyBox.Text = DutiesRun;
            Label l4 = new Label(); l4.Text = "Заметка:"; l4.SetBounds(12, 228, 90, 20);
            noteBox = new TextBox(); noteBox.SetBounds(110, 224, 538, 24); noteBox.MaxLength = 80;
            Label l4h = new Label(); l4h.ForeColor = Color.DimGray; l4h.SetBounds(110, 250, 538, 18);
            l4h.Text = "например: «положение 3, лопасть у датчика» — пригодится при разборе";
            Controls.Add(l1); Controls.Add(chCombo); Controls.Add(l2); Controls.Add(runRb); Controls.Add(blockRb);
            Controls.Add(l3); Controls.Add(dutyBox); Controls.Add(l4); Controls.Add(noteBox); Controls.Add(l4h);
            runRb.CheckedChanged += delegate { SwapDefaultDuties(); };

            startBtn = UiKit.NewButton("Снять записи", 140); startBtn.SetBounds(12, 278, 140, 32);
            stopBtn = UiKit.NewButton("Стоп", 90); stopBtn.SetBounds(158, 278, 90, 32); stopBtn.Enabled = false;
            countLabel = new Label(); countLabel.SetBounds(262, 284, 386, 22); countLabel.ForeColor = Color.SteelBlue;
            Controls.Add(startBtn); Controls.Add(stopBtn); Controls.Add(countLabel);

            log = new ListBox();
            log.SetBounds(12, 320, 636, 190);
            log.Font = new Font("Consolas", 9f);
            log.IntegralHeight = false;
            log.HorizontalScrollbar = true;
            Controls.Add(log);

            openBtn = UiKit.NewButton("Открыть tachdump.log", 170); openBtn.SetBounds(12, 520, 170, 30);
            Button copyPath = UiKit.NewButton("Копировать путь", 130); copyPath.SetBounds(188, 520, 130, 30);
            Button close = UiKit.NewButton("Закрыть", 90); close.SetBounds(558, 520, 90, 30);
            close.DialogResult = DialogResult.Cancel; CancelButton = close;
            Controls.Add(openBtn); Controls.Add(copyPath); Controls.Add(close);

            startBtn.Click += OnStart;
            stopBtn.Click += delegate { cancel = true; stopBtn.Enabled = false; AddLog("Останавливаю…"); };
            openBtn.Click += delegate { OpenFile(); };
            copyPath.Click += delegate { try { Clipboard.SetText(TachDiag.FilePath); AddLog("Путь скопирован: " + TachDiag.FilePath); } catch { } };
            FormClosing += OnClosing;
            Shown += delegate { FillChannels(); RefreshCount(); };
        }

        private void SwapDefaultDuties()
        {
            string t = dutyBox.Text.Trim();
            if (runRb.Checked && t == DutiesBlocked) dutyBox.Text = DutiesRun;
            else if (blockRb.Checked && t == DutiesRun) dutyBox.Text = DutiesBlocked;
        }

        private void FillChannels()
        {
            chCombo.Items.Clear();
            HubState st = link.Last;
            if (st == null) return;
            int sel = -1;
            foreach (ChannelInfo c in st.Channels)
            {
                if (!c.Active) continue;
                ChItem it = new ChItem();
                it.Name = c.Name;
                string nm = usage.ChannelName(c.Name);
                TachInfo tc = st.FindTach(c.Name);
                string tach = tc == null ? "" : "   тахо: " + Ru.Tach(tc.State, tc.Pin);
                it.Label = (nm == c.Name ? c.Name : nm + "  [" + c.Name + "]") + tach;
                int idx = chCombo.Items.Add(it);
                if (sel < 0 && tc != null && tc.State == "PWM") sel = idx;     // 3-pin канал, которым занимаемся
            }
            if (chCombo.Items.Count > 0) chCombo.SelectedIndex = sel >= 0 ? sel : 0;
        }

        private void RefreshCount()
        {
            int run, blocked;
            TachDiag.CountSessions(out run, out blocked);
            countLabel.Text = "В файле сессий: крутится " + run + ", заблокирован " + blocked;
        }

        private void AddLog(string text)
        {
            log.Items.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
            log.TopIndex = Math.Max(0, log.Items.Count - 1);
        }

        private void Ui(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (running) return;
            ChItem ch = chCombo.SelectedItem as ChItem;
            if (ch == null) { UiKit.Error(this, "Нет выбранного канала (нет связи с платой?)."); return; }
            string err;
            int[] duties = TachDiag.ParseDuties(dutyBox.Text, out err);
            if (duties == null) { UiKit.Error(this, err); return; }
            if (link.State != ConnState.Connected) { UiKit.Error(this, "Нет связи с платой."); return; }

            bool blocked = blockRb.Checked;
            string msg = blocked
                ? "Канал «" + usage.ChannelName(ch.Name) + "» будет по очереди держаться на уровнях " + string.Join(", ", Array.ConvertAll(duties, delegate (int d) { return d.ToString(CultureInfo.InvariantCulture); })) +
                  "%. Придержи крыльчатку и не отпускай до конца записи (около " + (duties.Length * 4) + " с). Начать?"
                : "Канал «" + usage.ChannelName(ch.Name) + "» будет по очереди держаться на уровнях " + string.Join(", ", Array.ConvertAll(duties, delegate (int d) { return d.ToString(CultureInfo.InvariantCulture); })) +
                  "%, кривая на это время отключается (максимум на 30 с). Начать?";
            if (!UiKit.Confirm(this, msg)) return;

            running = true; cancel = false;
            startBtn.Enabled = false; stopBtn.Enabled = true; chCombo.Enabled = false;
            string note = noteBox.Text.Trim();
            string channel = ch.Name;
            double settle = blocked ? 1.5 : 4.0;
            worker = new Thread(delegate ()
            {
                string error;
                bool ok = TachDiag.Capture(link, channel, blocked, note, duties, settle,
                    delegate (string s) { Ui(delegate { AddLog(s); }); },
                    delegate { return cancel; }, out error);
                Ui(delegate
                {
                    running = false;
                    startBtn.Enabled = true; stopBtn.Enabled = false; chCombo.Enabled = true;
                    RefreshCount();
                    if (ok) AddLog(cancel ? "Остановлено." : "Готово. Записи добавлены в " + TachDiag.FilePath);
                    else { AddLog("ОШИБКА: " + error); UiKit.Error(this, "Не удалось снять записи:\n" + error); }
                });
            });
            worker.IsBackground = true;
            worker.Name = "TachDiag";
            worker.Start();
        }

        private void OpenFile()
        {
            try
            {
                if (File.Exists(TachDiag.FilePath)) Process.Start(new ProcessStartInfo(TachDiag.FilePath) { UseShellExecute = true });
                else UiKit.Info(this, "Файла ещё нет: сначала сними хотя бы одну запись.");
            }
            catch (Exception ex) { UiKit.Error(this, "Не открыть файл: " + ex.Message); }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!running) return;
            cancel = true;
            try { if (worker != null) worker.Join(8000); } catch { }
        }
    }
}
