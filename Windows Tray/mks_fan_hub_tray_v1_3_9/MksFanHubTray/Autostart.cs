// Автозапуск через планировщик задач Windows (вход пользователя, наивысшие права - без окна UAC)
using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace MksFanHub
{
    public static class Autostart
    {
        private const string TaskName = "MksFanHubTray";

        private static int Run(string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode;
            }
        }

        public static bool IsEnabled()
        {
            try { string o; return Run("/Query /TN " + TaskName, out o) == 0; }
            catch { return false; }
        }

        public static bool Enable(out string error)
        {
            error = null;
            try
            {
                string exe = Application.ExecutablePath;
                string user = Environment.UserDomainName + "\\" + Environment.UserName;
                // /TR в кавычках, внутри - путь к exe в экранированных кавычках
                string args = "/Create /F /TN " + TaskName +
                              " /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /RU \"" + user + "\"" +
                              " /TR \"\\\"" + exe + "\\\"\"";
                string o;
                int code = Run(args, out o);
                if (code != 0) { error = o.Trim(); return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Disable(out string error)
        {
            error = null;
            try
            {
                string o;
                int code = Run("/Delete /F /TN " + TaskName, out o);
                if (code != 0) { error = o.Trim(); return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
