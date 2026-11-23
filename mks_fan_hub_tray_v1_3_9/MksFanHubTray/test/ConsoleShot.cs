using System;
using System.Windows.Forms;
using MksFanHub;

static class ConsoleShot
{
    [STAThread]
    static void Main(string[] args)
    {
        Settings s = new Settings();
        s.ComPort = args[0]; s.PollMs = 500; s.StatusMs = 700;
        Usage u = new Usage(s);
        u.SetName("CH:FUN_PWM_0", "P102 GPU");
        u.SetName("CH:FUN_PWM_1", "Intake 1");
        u.SetName("CH:FUN_DC_1", "Exhaust 1");     // DC_0 - без имени: проверим пометку по результату теста
        u.SetName("TH:CASE", "Корпус");
        HubLink link = new HubLink(s, new LhmSensorSource());
        link.PortFactory = delegate (string n) { return new FilePortLink(n); };
        link.Start();
        ConsolePanel.AutoConfirmForTests = true;
        MainForm f = new MainForm(link, s, u);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new System.Drawing.Point(0, 0);
        f.Height = 760;
        f.Show();

        Timer tick = new Timer(); tick.Interval = 1000;
        tick.Tick += delegate {
            u.Update(link.Last);
            f.RefreshView(HealthEvaluator.Evaluate(link.State, link.Last, link.LastStatusAt, link.LastSent, u));
        };
        tick.Start();

        int step = 0;
        Timer seq = new Timer(); seq.Interval = 1000;
        seq.Tick += delegate {
            step++;
            if (step == 4) { f.ShowConsoleTab(); f.Console.Send("THERMS?"); }
            if (step == 5) { f.Console.Send("TACH?"); f.Console.Send("SET=1,60"); }
            if (step == 6) f.Console.RunTachTest();
            if (step == 13)
            {
                Console.WriteLine("SEEN_DC_0=" + s.Seen.Contains("CH:FUN_DC_0") + " SEEN_DC_2=" + s.Seen.Contains("CH:FUN_DC_2"));
                Application.Exit();
            }
        };
        seq.Start();
        Application.Run(f);
        link.Stop();
    }
}
