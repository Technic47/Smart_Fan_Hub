// Источники температур: nvidia-smi (видеокарты) + LibreHardwareMonitor (CPU, SSD)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace MksFanHub
{
    public interface ISensorSource
    {
        // Ключи: P102, GTX, CPU, SSD (только те, что удалось прочитать)
        Dictionary<string, float> Read();
        string Info { get; }
        void Reset();   // переоткрыть источники (после сна Windows драйверы датчиков могли "уснуть")
        void Close();
    }

    public static class NvidiaSmi
    {
        public static Dictionary<string, float> ReadGpuTemps(out string error)
        {
            Dictionary<string, float> res = new Dictionary<string, float>();
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=index,name,temperature.gpu --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } error = "nvidia-smi завис"; return res; }
                    ParseGpuLines(output, res);
                }
            }
            catch (Exception ex) { error = "nvidia-smi: " + ex.Message; }
            return res;
        }

        // Отдельно для тестов: разбор вывода nvidia-smi
        public static void ParseGpuLines(string output, Dictionary<string, float> res)
        {
            foreach (string raw in output.Split('\n'))
            {
                string[] f = raw.Trim().Split(',');
                if (f.Length < 3) continue;
                string name = f[1].Trim();
                float t;
                if (!float.TryParse(f[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                if (name.IndexOf("P102", StringComparison.OrdinalIgnoreCase) >= 0) res["P102"] = t;
                else if (name.IndexOf("1060", StringComparison.OrdinalIgnoreCase) >= 0) res["GTX"] = t;
            }
        }
    }
}
