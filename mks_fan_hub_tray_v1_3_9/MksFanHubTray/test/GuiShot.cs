using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MksFanHub;

static class GuiShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Settings s = new Settings();
        s.ComPort = args[0]; s.PollMs = 500; s.StatusMs = 700;
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
        f.Show();

        Timer tick = new Timer(); tick.Interval = 1000;
        tick.Tick += delegate {
            HealthResult h = HealthEvaluator.Evaluate(link.State, link.Last, link.LastStatusAt, link.LastSent, u);
            u.Update(link.Last);
            f.RefreshView(h);
        };
        tick.Start();

        int step = 0;
        Timer seq = new Timer(); seq.Interval = 1000;
        Form open = null;
        seq.Tick += delegate {
            step++;
            if (open != null && (step == 9 || step == 15 || step == 21)) { open.Close(); open = null; return; }
            if (step == 4) { seq.Stop(); open = new CurveDialog(link, u, s, "FUN_PWM_1"); open.Shown += delegate { seq.Start(); }; open.Show(f); open.Location = new System.Drawing.Point(100, 60); }
            if (step == 10) { seq.Stop(); open = new BindingsDialog(link, u); open.Shown += delegate { seq.Start(); }; open.Show(f); open.Location = new System.Drawing.Point(100, 60); }
            if (step == 16) { seq.Stop(); open = new SettingsDialog(s, u); open.Shown += delegate { seq.Start(); }; open.Show(f); open.Location = new System.Drawing.Point(100, 30); }
            if (step == 22) { s.UnusedFans = UnusedMode.Dim; s.UnusedTherms = UnusedMode.AsIs; f.RefreshNow(); }
            if (step == 26) { Application.Exit(); }
        };
        seq.Start();
        Application.Run(f);
        link.Stop();
    }
}
