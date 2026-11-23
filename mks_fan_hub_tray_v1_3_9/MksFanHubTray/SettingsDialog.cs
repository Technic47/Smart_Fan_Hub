// Окно настроек (Меню -> Настройки)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace MksFanHub
{
    public class SettingsDialog : Form
    {
        private readonly Settings s;
        private readonly Usage usage;

        private RadioButton[] fanRb = new RadioButton[3];
        private RadioButton[] thRb = new RadioButton[3];
        private ComboBox portBox;
        private NumericUpDown pollBox, statusBox, lightPollBox, lightStatusBox;
        private CheckBox lightBox, pcBox, logBox, topBox, startShowBox, minTrayBox, notifyBad, notifyWarn, autostartBox;
        private bool autostartInitial;

        public bool PortChanged;
        public bool ResetDone;

        public SettingsDialog(Settings settings, Usage u)
        {
            s = settings; usage = u;
            Text = "Настройки";
            ClientSize = new Size(560, 652);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            GroupBox g1 = Group("Неподключённые вентиляторы", 12, 10, 266, 108);
            fanRb = Radios(g1, new string[] { "Не показывать", "Показывать неактивными (серым)", "Показывать как есть" }, (int)s.UnusedFans);
            GroupBox g2 = Group("Неподключённые термисторы", 284, 10, 266, 108);
            thRb = Radios(g2, new string[] { "Не показывать", "Показывать неактивными (серым)", "Показывать как есть" }, (int)s.UnusedTherms);

            Label hint = new Label();
            hint.SetBounds(12, 122, 538, 50); hint.ForeColor = Color.DimGray;
            hint.Text = "Подключённым считается канал/термистор, у которого есть своё название, который стабильно работал " +
                        "(" + Usage.AutoDetectSeconds + " с подряд) или который отмечен вручную (правый клик по строке → «Подключение»).";
            Controls.Add(hint);

            GroupBox g3 = Group("Связь с платой", 12, 176, 538, 176);
            Label l1 = new Label(); l1.Text = "COM-порт:"; l1.SetBounds(12, 26, 120, 20);
            portBox = new ComboBox(); portBox.DropDownStyle = ComboBoxStyle.DropDown; portBox.SetBounds(140, 22, 130, 24);
            portBox.Items.Add("AUTO");
            foreach (string p in HubLink.ListPorts()) portBox.Items.Add(p);
            portBox.Text = s.ComPort;
            Label l1h = new Label(); l1h.Text = "AUTO — найти плату самому"; l1h.ForeColor = Color.DimGray; l1h.SetBounds(280, 26, 250, 20);
            Label l2 = new Label(); l2.Text = "Отправка температур, с:"; l2.SetBounds(12, 56, 160, 20);
            pollBox = Dec(180, 52, 0.2m, 5m, s.PollMs / 1000m);
            Label l3 = new Label(); l3.Text = "Опрос платы, с:"; l3.SetBounds(290, 56, 120, 20);
            statusBox = Dec(414, 52, 0.5m, 10m, s.StatusMs / 1000m);
            lightBox = new CheckBox(); lightBox.Text = "Окно скрыто — опрашивать реже:"; lightBox.Checked = s.LightMode; lightBox.SetBounds(12, 86, 250, 22);
            Label ll1 = new Label(); ll1.Text = "отправка, с"; ll1.SetBounds(268, 90, 74, 20);
            lightPollBox = Dec(344, 86, 1m, 6m, s.LightPollMs / 1000m); lightPollBox.Width = 60;
            Label ll2 = new Label(); ll2.Text = "опрос, с"; ll2.SetBounds(418, 90, 56, 20);
            lightStatusBox = Dec(476, 86, 2m, 60m, s.LightStatusMs / 1000m); lightStatusBox.Width = 52; lightStatusBox.Increment = 1m;
            g3.Controls.Add(lightBox); g3.Controls.Add(ll1); g3.Controls.Add(lightPollBox); g3.Controls.Add(ll2); g3.Controls.Add(lightStatusBox);
            Label l4 = new Label(); l4.Text = "Если плата не получает температуры ПК 8 секунд, каналы, зависящие от них (GPU), уходят в FAILSAFE (100%). Поэтому период отправки не больше 5 с " +
                "(в свёрнутом режиме — не больше 6 с). Прошивка 0.3.15+ сама присылает события, поэтому в свёрнутом режиме опрос можно разредить."; l4.SetBounds(12, 112, 514, 62); l4.ForeColor = Color.DimGray;
            g3.Controls.Add(l1); g3.Controls.Add(portBox); g3.Controls.Add(l1h); g3.Controls.Add(l2); g3.Controls.Add(pollBox);
            g3.Controls.Add(l3); g3.Controls.Add(statusBox); g3.Controls.Add(l4);

            GroupBox g4 = Group("Окно", 12, 358, 538, 142);
            pcBox = Check(g4, "Показывать температуры ПК (P102, CPU, SSD…)", 12, 22, s.ShowPcTemps);
            logBox = Check(g4, "Показывать журнал и консоль в окне", 12, 46, s.ShowLog);
            topBox = Check(g4, "Окно поверх остальных", 12, 70, s.AlwaysOnTop);
            startShowBox = Check(g4, "Показывать окно при запуске программы", 12, 94, s.ShowWindowOnStart);
            minTrayBox = Check(g4, "Сворачивать в трей (кнопка «свернуть» прячет окно)", 12, 118, s.HideOnMinimize);

            GroupBox g5 = Group("Уведомления и запуск", 12, 506, 538, 96);
            notifyBad = Check(g5, "Всплывающее уведомление при проблеме (красный)", 12, 22, s.NotifyProblems);
            notifyWarn = Check(g5, "Всплывающее уведомление при предупреждении (жёлтый)", 12, 46, s.NotifyWarnings);
            autostartInitial = Autostart.IsEnabled();
            autostartBox = Check(g5, "Запускать при входе в Windows (задача планировщика)", 12, 70, autostartInitial);

            Button reset = UiKit.NewButton("Сбросить названия…", 150); reset.SetBounds(12, 614, 150, 28);
            reset.Click += OnResetNames;
            Button ok = UiKit.NewButton("OK", 90); ok.SetBounds(362, 614, 90, 28); ok.DialogResult = DialogResult.OK;
            Button cancel = UiKit.NewButton("Отмена", 90); cancel.SetBounds(458, 614, 90, 28); cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(reset); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += OnOk;
        }

        private GroupBox Group(string title, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = title; g.SetBounds(x, y, w, h);
            Controls.Add(g);
            return g;
        }

        private static RadioButton[] Radios(GroupBox g, string[] texts, int selected)
        {
            RadioButton[] r = new RadioButton[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                r[i] = new RadioButton();
                r[i].Text = texts[i];
                r[i].SetBounds(12, 22 + i * 26, 240, 22);
                r[i].Checked = i == selected;
                g.Controls.Add(r[i]);
            }
            return r;
        }

        private static CheckBox Check(GroupBox g, string text, int x, int y, bool v)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Checked = v; c.SetBounds(x, y, 510, 22);
            g.Controls.Add(c);
            return c;
        }

        private static NumericUpDown Dec(int x, int y, decimal min, decimal max, decimal v)
        {
            NumericUpDown n = new NumericUpDown();
            n.DecimalPlaces = 1; n.Increment = 0.5m; n.Minimum = min; n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, v));
            n.SetBounds(x, y, 80, 24);
            return n;
        }

        private static UnusedMode Pick(RadioButton[] r)
        {
            for (int i = 0; i < r.Length; i++) if (r[i].Checked) return (UnusedMode)i;
            return UnusedMode.Hide;
        }

        private void OnResetNames(object sender, EventArgs e)
        {
            if (!UiKit.Confirm(this, "Стереть все названия, отметки «подключён / не подключён» и авто-обнаружение?")) return;
            usage.ResetUserData();
            ResetDone = true;
            UiKit.Info(this, "Сброшено.");
        }

        private void OnOk(object sender, EventArgs e)
        {
            string port = portBox.Text.Trim();
            if (port.Length == 0) port = "AUTO";
            PortChanged = !port.Equals(s.ComPort, StringComparison.OrdinalIgnoreCase);
            s.ComPort = port;
            s.PollMs = (int)(pollBox.Value * 1000m);
            s.StatusMs = (int)(statusBox.Value * 1000m);
            s.LightMode = lightBox.Checked;
            s.LightPollMs = (int)(lightPollBox.Value * 1000m);
            s.LightStatusMs = (int)(lightStatusBox.Value * 1000m);
            s.UnusedFans = Pick(fanRb);
            s.UnusedTherms = Pick(thRb);
            s.ShowPcTemps = pcBox.Checked;
            s.ShowLog = logBox.Checked;
            s.AlwaysOnTop = topBox.Checked;
            s.ShowWindowOnStart = startShowBox.Checked;
            s.HideOnMinimize = minTrayBox.Checked;
            s.NotifyProblems = notifyBad.Checked;
            s.NotifyWarnings = notifyWarn.Checked;
            s.Save();

            if (autostartBox.Checked != autostartInitial)
            {
                string err;
                bool ok = autostartBox.Checked ? Autostart.Enable(out err) : Autostart.Disable(out err);
                if (!ok) UiKit.Error(this, "Автозапуск не изменён: " + err);
                else Log.Write(autostartBox.Checked ? "Автозапуск включён" : "Автозапуск выключен");
            }
        }
    }
}
