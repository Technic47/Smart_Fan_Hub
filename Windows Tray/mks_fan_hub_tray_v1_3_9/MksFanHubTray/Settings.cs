// Настройки (файл MksFanHubTray.ini рядом с exe) и лог
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MksFanHub
{
    // Что делать с неподключёнными каналами / термисторами
    public enum UnusedMode { Hide = 0, Dim = 1, AsIs = 2 }

    public class Settings
    {
        // --- связь ---
        public string ComPort = "AUTO";   // AUTO или имя порта (COM3)
        public int PollMs = 1000;         // период отправки температур в плату
        public int StatusMs = 2000;       // период запроса STATUS?

        // --- свёрнутый режим (окно скрыто или свёрнуто) ---
        public bool LightMode = true;     // реже опрашивать плату, когда окно не видно
        public int LightStatusMs = 10000; // период STATUS? в свёрнутом режиме (плата сама присылает события)
        public int LightPollMs = 3000;    // период отправки температур в свёрнутом режиме (не больше 6 с: watchdog платы 8 с)

        // --- отображение ---
        public UnusedMode UnusedFans = UnusedMode.Hide;
        public UnusedMode UnusedTherms = UnusedMode.Hide;
        public bool ShowPcTemps = true;
        public bool ShowLog = true;
        public bool AlwaysOnTop = false;
        public bool ShowWindowOnStart = true;
        public bool HideOnMinimize = false;
        public int WindowW = 960;
        public int WindowH = 680;

        // --- уведомления ---
        public bool NotifyProblems = true;   // красный
        public bool NotifyWarnings = true;   // жёлтый

        // --- пользовательские данные ---
        // ключи: "CH:FUN_PWM_0", "TH:CASE"
        public Dictionary<string, string> Names = new Dictionary<string, string>();
        public Dictionary<string, int> Conn = new Dictionary<string, int>();   // 0 авто, 1 подключён, 2 не подключён
        public HashSet<string> Seen = new HashSet<string>();                   // замечены в работе (авто-определение)
        public Dictionary<string, string> CurveGroups = new Dictionary<string, string>();   // канал -> группа связанных кривых

        // Схема имён каналов: 2 = DC-каналы по порядку разъёмов (прошивка 0.3.17+). Нет в файле = старая схема 1.
        public const int ChannelMapCurrent = 2;
        public int ChannelMap = ChannelMapCurrent;

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MksFanHubTray.ini"); }
        }

        // старое имя DC-канала -> новое (прошивка 0.3.17: DC_1 -> DC_0, DC_2 -> DC_1, DC_3 -> DC_2, DC_0 -> DC_3)
        public static string MapDcName(string key)
        {
            if (key == null) return null;
            int i = key.LastIndexOf("FUN_DC_", StringComparison.Ordinal);
            if (i < 0 || i + 7 >= key.Length + 0 || key.Length != i + 8) return key;
            char d = key[i + 7];
            char n;
            switch (d)
            {
                case '1': n = '0'; break;
                case '2': n = '1'; break;
                case '3': n = '2'; break;
                case '0': n = '3'; break;
                default: return key;
            }
            return key.Substring(0, i + 7) + n;
        }

        // перенос пользовательских данных на новые названия DC-каналов
        public void RemapDcChannels()
        {
            Dictionary<string, string> nn = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in Names) nn[MapDcName(kv.Key)] = kv.Value;
            Names = nn;
            Dictionary<string, int> nc = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in Conn) nc[MapDcName(kv.Key)] = kv.Value;
            Conn = nc;
            HashSet<string> ns = new HashSet<string>();
            foreach (string k in Seen) ns.Add(MapDcName(k));
            Seen = ns;
            Dictionary<string, string> ng = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in CurveGroups) ng[MapDcName(kv.Key)] = kv.Value;
            CurveGroups = ng;
            ChannelMap = ChannelMapCurrent;
        }

        private static bool B(string v) { return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); }

        private static UnusedMode M(string v, UnusedMode def)
        {
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 2) return (UnusedMode)n;
            return def;
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            bool sawMap = false;
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    if (key == "ChannelMap" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { s.ChannelMap = n; sawMap = true; }
                    else if (key == "ComPort" && val.Length > 0) s.ComPort = val;
                    else if (key == "PollMs" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 200) s.PollMs = n;
                    else if (key == "StatusMs" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 500) s.StatusMs = n;
                    else if (key == "LightMode") s.LightMode = B(val);
                    else if (key == "LightStatusMs" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 2000 && n <= 60000) s.LightStatusMs = n;
                    else if (key == "LightPollMs" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1000 && n <= 6000) s.LightPollMs = n;
                    else if (key.StartsWith("Link.", StringComparison.Ordinal) && val.Length > 0) s.CurveGroups[key.Substring(5)] = val;
                    else if (key == "UnusedFans") s.UnusedFans = M(val, s.UnusedFans);
                    else if (key == "UnusedTherms") s.UnusedTherms = M(val, s.UnusedTherms);
                    else if (key == "ShowPcTemps") s.ShowPcTemps = B(val);
                    else if (key == "ShowLog") s.ShowLog = B(val);
                    else if (key == "AlwaysOnTop") s.AlwaysOnTop = B(val);
                    else if (key == "ShowWindowOnStart") s.ShowWindowOnStart = B(val);
                    else if (key == "HideOnMinimize") s.HideOnMinimize = B(val);
                    else if (key == "NotifyProblems") s.NotifyProblems = B(val);
                    else if (key == "NotifyWarnings") s.NotifyWarnings = B(val);
                    else if (key == "WindowW" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 500 && n <= 4000) s.WindowW = n;
                    else if (key == "WindowH" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 400 && n <= 3000) s.WindowH = n;
                    else if (key.StartsWith("Name.", StringComparison.Ordinal) && val.Length > 0) s.Names[key.Substring(5)] = val;
                    else if (key.StartsWith("Conn.", StringComparison.Ordinal) && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 2) s.Conn[key.Substring(5)] = n;
                    else if (key == "Seen")
                    {
                        foreach (string k in val.Split(';')) if (k.Length > 0) s.Seen.Add(k);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Не удалось прочитать настройки: " + ex.Message); }
            if (File.Exists(FilePath) && (!sawMap || s.ChannelMap < ChannelMapCurrent))
            {
                // файл от версии до 1.3.3: имена DC-каналов в прошивке 0.3.17 поменялись - переносим свои названия/связи
                try { File.Copy(FilePath, FilePath + ".bak_1.3.2", true); } catch { }
                s.RemapDcChannels();
                s.Save();
                Log.Write("Настройки перенесены на новые названия DC-каналов (прошивка 0.3.17): копия старого файла - MksFanHubTray.ini.bak_1.3.2");
            }
            return s;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# MKS Fan Hub Tray: настройки. Файл можно править вручную при закрытой программе.");
                lines.Add("# ComPort = AUTO или имя порта, например COM3");
                lines.Add("# ChannelMap=2: названия DC-каналов по порядку разъёмов (прошивка 0.3.17+). Не менять.");
                lines.Add("ChannelMap=" + ChannelMap.ToString(CultureInfo.InvariantCulture));
                lines.Add("ComPort=" + ComPort);
                lines.Add("PollMs=" + PollMs.ToString(CultureInfo.InvariantCulture));
                lines.Add("StatusMs=" + StatusMs.ToString(CultureInfo.InvariantCulture));
                lines.Add("# Свёрнутый режим: LightMode=1 - когда окно скрыто, опрос платы раз в LightStatusMs, температуры раз в LightPollMs");
                lines.Add("LightMode=" + (LightMode ? "1" : "0"));
                lines.Add("LightStatusMs=" + LightStatusMs.ToString(CultureInfo.InvariantCulture));
                lines.Add("LightPollMs=" + LightPollMs.ToString(CultureInfo.InvariantCulture));
                lines.Add("# UnusedFans / UnusedTherms: 0 = не показывать, 1 = показывать неактивными, 2 = показывать как есть");
                lines.Add("UnusedFans=" + (int)UnusedFans);
                lines.Add("UnusedTherms=" + (int)UnusedTherms);
                lines.Add("ShowPcTemps=" + (ShowPcTemps ? "1" : "0"));
                lines.Add("ShowLog=" + (ShowLog ? "1" : "0"));
                lines.Add("AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0"));
                lines.Add("ShowWindowOnStart=" + (ShowWindowOnStart ? "1" : "0"));
                lines.Add("HideOnMinimize=" + (HideOnMinimize ? "1" : "0"));
                lines.Add("NotifyProblems=" + (NotifyProblems ? "1" : "0"));
                lines.Add("NotifyWarnings=" + (NotifyWarnings ? "1" : "0"));
                lines.Add("WindowW=" + WindowW.ToString(CultureInfo.InvariantCulture));
                lines.Add("WindowH=" + WindowH.ToString(CultureInfo.InvariantCulture));
                lines.Add("# Названия: Name.<CH|TH>:<идентификатор>=текст");
                foreach (KeyValuePair<string, string> kv in Names)
                    lines.Add("Name." + kv.Key + "=" + kv.Value.Replace('\r', ' ').Replace('\n', ' '));
                lines.Add("# Подключение: 1 = подключён, 2 = не подключён (нет записи = авто)");
                foreach (KeyValuePair<string, int> kv in Conn)
                    lines.Add("Conn." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                lines.Add("# Связанные кривые: Link.<канал>=<группа>");
                foreach (KeyValuePair<string, string> kv in CurveGroups)
                    lines.Add("Link." + kv.Key + "=" + kv.Value);
                lines.Add("Seen=" + string.Join(";", new List<string>(Seen).ToArray()));
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Не удалось сохранить настройки: " + ex.Message); }
        }
    }

    public static class Log
    {
        private static readonly object sync = new object();
        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MksFanHubTray.log"); }
        }

        public static event Action<string> Added;

        public static void Write(string text)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            lock (sync)
            {
                try
                {
                    FileInfo fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(FilePath);   // простая ротация
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* лог не критичен */ }
            }
            Action<string> h = Added;
            if (h != null) { try { h(line); } catch { } }
        }

        public static void Open()
        {
            try
            {
                if (File.Exists(FilePath)) Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            }
            catch (Exception ex) { Write("Не открыть журнал: " + ex.Message); }
        }
    }
}
