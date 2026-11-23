// Иконка в трее, меню, периодическое обновление
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MksFanHub
{
    public class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly Settings settings;
        private readonly Usage usage;
        private readonly ISensorSource sensors;
        private readonly HubLink link;
        private readonly NotifyIcon tray;
        private readonly MainForm form;
        private readonly Timer timer;
        private readonly ToolStripMenuItem autostartItem;

        private Health? shownLevel;
        private Health lastLevel = Health.Ok;
        private string lastReason = "";
        private IntPtr iconHandle = IntPtr.Zero;
        private bool exiting;
        private DateTime quietUntil = DateTime.MinValue;   // после сна не пугаем уведомлениями, пока связь восстанавливается

        public TrayContext(Settings s, ISensorSource src)
        {
            settings = s;
            sensors = src;
            usage = new Usage(settings);
            link = new HubLink(settings, sensors);
            form = new MainForm(link, settings, usage);
            form.ExitRequested += ExitApp;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Открыть окно", null, delegate { ShowWindow(); });
            autostartItem = new ToolStripMenuItem("Запускать при входе в Windows");
            autostartItem.Click += OnToggleAutostart;
            menu.Items.Add(autostartItem);
            menu.Items.Add("Открыть журнал", null, delegate { Log.Open(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApp(); });
            menu.Opening += delegate { autostartItem.Checked = Autostart.IsEnabled(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };
            SetIcon(Health.Warn);
            tray.Text = "MKS Fan Hub: запуск…";

            link.BoardEvent += OnBoardEvent;
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode; }
            catch (Exception ex) { Log.Write("Слежение за сном Windows недоступно: " + ex.Message); }

            Log.Write("Запуск MKS Fan Hub Tray " + global::MksFanHub.MainForm.AppVersion);
            link.Start();

            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += OnTick;
            timer.Start();

            if (settings.ShowWindowOnStart) ShowWindow();
        }

        private void ShowWindow()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            OnTick(null, EventArgs.Empty);
        }

        // Событие платы (рабочий поток HubLink) -> журнал по-русски
        private void OnBoardEvent(string text)
        {
            if (!usage.EventRelevant(text, link.Last)) return;
            string msg = Ru.Event(text, usage);
            if (msg != null) Log.Write(msg);
        }

        // Сон / пробуждение Windows: на время сна порт закрываем, после - переподключаемся и переоткрываем датчики
        private void OnPowerMode(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                Log.Write("Windows уходит в сон: связь с платой приостановлена");
                link.Pause();
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Log.Write("Windows проснулась: восстанавливаю связь с платой");
                quietUntil = DateTime.Now.AddSeconds(45);
                link.Resume();
            }
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            string err;
            bool now = Autostart.IsEnabled();
            bool ok = now ? Autostart.Disable(out err) : Autostart.Enable(out err);
            if (!ok)
            {
                Log.Write("Автозапуск: ошибка — " + err);
                tray.ShowBalloonTip(5000, "MKS Fan Hub", "Не удалось изменить автозапуск: " + err, ToolTipIcon.Error);
            }
            else
            {
                Log.Write(now ? "Автозапуск выключен" : "Автозапуск включён");
                tray.ShowBalloonTip(3000, "MKS Fan Hub", now ? "Автозапуск выключен" : "Автозапуск включён", ToolTipIcon.Info);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            usage.Update(link.Last);
            HealthResult h = HealthEvaluator.Evaluate(link.State, link.Last, link.LastStatusAt, link.LastSent, usage, link.StaleSeconds);
            bool quiet = link.IsPaused || (DateTime.Now < quietUntil && h.Level == Health.Bad);
            if (quiet)
            {
                h = new HealthResult();
                h.Level = Health.Warn;
                h.Reason = link.IsPaused ? "Windows в режиме сна" : "восстанавливаю связь с платой после сна";
            }

            if (shownLevel == null || shownLevel.Value != h.Level) { SetIcon(h.Level); shownLevel = h.Level; }
            tray.Text = TrimTip("MKS Fan Hub: " + h.Reason);

            // уведомления при смене уровня (во время сна и сразу после него - без них)
            if (quiet) { /* только значок и подсказка */ }
            else if (h.Level != lastLevel)
            {
                if (h.Level == Health.Bad)
                {
                    Log.Write("ПРОБЛЕМА: " + h.Reason);
                    if (settings.NotifyProblems) tray.ShowBalloonTip(8000, "MKS Fan Hub", h.Reason, ToolTipIcon.Error);
                }
                else if (h.Level == Health.Warn)
                {
                    Log.Write("Внимание: " + h.Reason);
                    if (settings.NotifyWarnings) tray.ShowBalloonTip(6000, "MKS Fan Hub", h.Reason, ToolTipIcon.Warning);
                }
                else Log.Write("Состояние: норма");
                lastLevel = h.Level;
                lastReason = h.Reason;
            }
            else if (h.Reason != lastReason)
            {
                if (h.Level != Health.Ok) Log.Write((h.Level == Health.Bad ? "ПРОБЛЕМА: " : "Внимание: ") + h.Reason);
                lastReason = h.Reason;
            }

            form.RefreshView(h);
        }

        private static string TrimTip(string s)
        {
            return s.Length > 63 ? s.Substring(0, 60) + "..." : s;   // лимит NotifyIcon.Text
        }

        private void SetIcon(Health level)
        {
            Color c = level == Health.Ok ? Color.FromArgb(40, 190, 80)
                    : level == Health.Warn ? Color.FromArgb(240, 180, 0) : Color.FromArgb(220, 40, 40);
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 13, 13);
                    using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1f)) g.DrawEllipse(p, 1, 1, 13, 13);
                    // простая "лопасть": белый крест в центре
                    using (Pen w = new Pen(Color.White, 2f)) { g.DrawLine(w, 4, 8, 11, 8); g.DrawLine(w, 8, 4, 8, 11); }
                }
                IntPtr h = bmp.GetHicon();
                Icon ic = Icon.FromHandle(h);
                Icon old = tray.Icon;
                IntPtr oldHandle = iconHandle;
                tray.Icon = ic;
                iconHandle = h;
                if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
            }
        }

        private void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            Log.Write("Выход");
            timer.Stop();
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            tray.Visible = false;
            link.Stop();
            sensors.Close();
            form.AllowClose = true;
            form.Close();
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            ExitThread();
        }
    }
}
