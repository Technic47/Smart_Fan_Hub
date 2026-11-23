// Скриншоты окон v1.3: GuiShot2.exe <порт> <main|curve|settings|diag|diagrun>
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MksFanHub;

static class GuiShot2
{
    [STAThread]
    static void Main(string[] args)
    {
        string what = args[1];
        Settings s = new Settings();
        s.ComPort = args[0]; s.PollMs = 500; s.StatusMs = 700;
        s.CurveGroups["FUN_PWM_1"] = "A"; s.CurveGroups["FUN_PWM_2"] = "A";
        Usage u = new Usage(s);
        u.SetName("CH:FUN_PWM_0", "P102 GPU");
        u.SetName("CH:FUN_PWM_1", "Intake 1");
        u.SetName("CH:FUN_PWM_2", "Intake 2");
        u.SetName("CH:FUN_DC_1", "Exhaust 1");
        u.SetName("CH:FUN_DC_0", "Exhaust 2,3");
        u.SetName("TH:CASE", "Корпус");
        HubLink link = new HubLink(s, new LhmSensorSource());
        link.PortFactory = delegate (string n) { return new FilePortLink(n); };
        link.Start();
        MainForm f = new MainForm(link, s, u);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new System.Drawing.Point(0, 0);
        Log.Write("Плата найдена на COM3 (эмулятор)");
        Log.Write("Плата: FAILSAFE — нет температур ПК больше 8 с, зависимые вентиляторы на 100%");
        Log.Write("Плата: FAILSAFE снят, температуры снова поступают");
        Log.Write("Windows уходит в сон: связь с платой приостановлена");
        Log.Write("Windows проснулась: восстанавливаю связь с платой");
        f.Show();

        Timer tick = new Timer(); tick.Interval = 1000;
        tick.Tick += delegate {
            HealthResult h = HealthEvaluator.Evaluate(link.State, link.Last, link.LastStatusAt, link.LastSent, u, link.StaleSeconds);
            u.Update(link.Last);
            f.RefreshView(h);
        };
        tick.Start();

        Timer go = new Timer(); go.Interval = 3000;
        Form open = null;
        go.Tick += delegate {
            go.Stop();
            if (what == "pick")
            {
                var items = new List<KeyValuePair<string, string>>();
                items.Add(new KeyValuePair<string, string>("FUN_PWM_0", "P102 GPU  [FUN_PWM_0]"));
                items.Add(new KeyValuePair<string, string>("FUN_PWM_2", "Intake 2  [FUN_PWM_2]"));
                items.Add(new KeyValuePair<string, string>("FUN_DC_1", "Exhaust 1  [FUN_DC_1]"));
                items.Add(new KeyValuePair<string, string>("FUN_DC_0", "Exhaust 2,3  [FUN_DC_0]"));
                PickChannelsDialog.Pick(f, "Связать кривые", "Отметь каналы, у которых кривая всегда должна быть такой же, как у «Intake 1». Правка кривой любого из них будет менять все. Снятая галочка разрывает связь.", items, new string[] { "FUN_PWM_2" });
                return;
            }
            if (what == "curve") { open = new CurveDialog(link, u, s, "FUN_PWM_1"); }
            else if (what == "manual")
            {
                open = new ManualDialog(link, u, "FUN_DC_1");
                Timer drive = new Timer(); drive.Interval = 800; int step = 0;
                drive.Tick += delegate {
                    step++;
                    NumericUpDown nud = null, fnud = null;
                    foreach (Control c in open.Controls) if (c is NumericUpDown) { if (nud == null) nud = (NumericUpDown)c; else fnud = (NumericUpDown)c; }
                    if (step == 1) nud.Value = 55;
                    else if (step == 2 && fnud != null) fnud.Value = 100;
                    else if (step == 3) nud.Value = 40;
                    else if (step == 9) { drive.Stop(); open.Close(); }
                };
                drive.Start();
            }
            else if (what == "spin") { open = new SpinDialog(link, u); }
            else if (what == "settings") { open = new SettingsDialog(s, u); }
            else if (what == "diag" || what == "diagrun") { open = new TachDiagDialog(link, u); }
            if (open != null) { open.Show(f); open.Location = new System.Drawing.Point(60, 20); }
            if (what == "diagrun")
            {
                TachDiag.PathOverride = "/tmp/traybuild/tachdump_gui.log";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                    string err;
                    TachDiag.Capture(link, "FUN_DC_1", false, "положение 1", new int[] { 30, 50 }, 0.5, delegate (string m) { }, delegate { return false; }, out err);
                });
            }
        };
        go.Start();
        Timer end = new Timer(); end.Interval = 14000;
        end.Tick += delegate { Application.Exit(); };
        end.Start();
        Application.Run(f);
        link.Stop();
    }
}
