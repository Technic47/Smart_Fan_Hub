// Реальный источник температур: nvidia-smi + LibreHardwareMonitorLib (CPU, SSD).
// Для проверки сборки под Mono этот файл заменяется заглушкой (см. test/StubSensors.cs).
using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace MksFanHub
{
    public class LhmSensorSource : ISensorSource
    {
        private Computer computer;
        private string info = "";

        public string Info { get { return info; } }

        public LhmSensorSource()
        {
            OpenComputer();
        }

        private void OpenComputer()
        {
            computer = new Computer();
            computer.IsCpuEnabled = true;
            computer.IsStorageEnabled = true;
            computer.IsMotherboardEnabled = false;
            computer.IsGpuEnabled = false;       // видеокарты читаем через nvidia-smi
            try { computer.Open(); }
            catch (Exception ex) { Log.Write("LibreHardwareMonitor не открылся: " + ex.Message); }
        }

        public void Reset()
        {
            try { computer.Close(); } catch { }
            OpenComputer();
        }

        public Dictionary<string, float> Read()
        {
            Dictionary<string, float> res = new Dictionary<string, float>();
            string err;
            Dictionary<string, float> gpu = NvidiaSmi.ReadGpuTemps(out err);
            foreach (KeyValuePair<string, float> kv in gpu) res[kv.Key] = kv.Value;

            float? cpu = null, ssd = null;
            try
            {
                foreach (IHardware hw in computer.Hardware) Collect(hw, ref cpu, ref ssd);
            }
            catch (Exception ex) { err = (err == null ? "" : err + "; ") + "LHM: " + ex.Message; }
            if (cpu.HasValue) res["CPU"] = cpu.Value;
            if (ssd.HasValue) res["SSD"] = ssd.Value;

            info = err ?? "";
            return res;
        }

        private static void Collect(IHardware hw, ref float? cpu, ref float? ssd)
        {
            hw.Update();
            if (hw.HardwareType == HardwareType.Cpu)
            {
                cpu = FindTemp(hw, "Package") ?? FindTemp(hw, null);
            }
            else if (hw.HardwareType == HardwareType.Storage && ssd == null)
            {
                ssd = FindTemp(hw, null);
            }
            foreach (IHardware sub in hw.SubHardware) Collect(sub, ref cpu, ref ssd);
        }

        private static float? FindTemp(IHardware hw, string nameContains)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                if (nameContains == null || s.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s.Value;
            }
            return null;
        }

        public void Close()
        {
            try { computer.Close(); } catch { }
        }
    }
}
