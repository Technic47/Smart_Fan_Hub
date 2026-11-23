// MKS Fan Hub Tray: точка входа. Одна копия, права администратора (см. app.manifest).
using System;
using System.Threading;
using System.Windows.Forms;

namespace MksFanHub
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Global\\MksFanHubTray_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("MKS Fan Hub Tray уже запущен (смотри значок в трее).",
                        "MKS Fan Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("Ошибка UI: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("Необработанная ошибка: " + e.ExceptionObject);
                };

                Settings settings = Settings.Load();
                ISensorSource sensors = new LhmSensorSource();
                Application.Run(new TrayContext(settings, sensors));
            }
        }
    }
}
