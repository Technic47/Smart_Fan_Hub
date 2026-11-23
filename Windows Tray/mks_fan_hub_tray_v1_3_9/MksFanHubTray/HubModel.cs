// Модель состояния платы и разбор ответа STATUS? (см. прошивку MKS Fan Hub v0.3.x)
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MksFanHub
{
    public class ThermInfo
    {
        public string Name;
        public bool Enabled;
        public float? Value;   // null - нет значения (NaN)
        public string State;   // OFF / ABSENT / OK / FAULT
    }

    public class ChannelInfo
    {
        public int Index;
        public string Name;
        public int Percent;
        public string SourceType;  // THERM / SERIAL
        public string SourceName;
        public bool Locked;
        public bool Active;
    }

    public class TachInfo
    {
        public string Name;
        public string Pin;     // "-" если не задан
        public string State;   // NONE / NOSIG / OK / STALL / PWM
        public int Rpm;
    }

    public class SpinInfo
    {
        public string Name;
        public string State;   // OK / KICK / BOOST / RAMP / PROBE / VERIFY / ERR
        public int Tries;
    }

    public class HubState
    {
        public List<SpinInfo> Spins = new List<SpinInfo>();
        public string Mode = "?";
        public List<KeyValuePair<string, float>> SerialValues = new List<KeyValuePair<string, float>>();
        public List<ThermInfo> Therms = new List<ThermInfo>();
        public List<ChannelInfo> Channels = new List<ChannelInfo>();
        public List<TachInfo> Tachs = new List<TachInfo>();
        public bool WatchdogTripped;
        public DateTime Received = DateTime.Now;

        public TachInfo FindTach(string channelName)
        {
            foreach (TachInfo t in Tachs) if (t.Name == channelName) return t;
            return null;
        }
    }

    // Собирает HubState из строк "STATUS=...". Блок заканчивается строкой STATUS=END.
    public class StatusParser
    {
        private HubState cur = new HubState();
        private int chIndex = 0;

        // Возвращает готовое состояние, когда пришла строка STATUS=END, иначе null
        public HubState Feed(string line)
        {
            if (line == null) return null;
            line = line.Trim();
            if (!line.StartsWith("STATUS=", StringComparison.Ordinal)) return null;
            string body = line.Substring(7);
            string[] p = body.Split(',');
            string kind = p[0];

            if (kind == "END")
            {
                HubState done = cur;
                done.Channels.Sort(CompareChannelNames);      // прошивка отдаёт в порядке выходов (idx), показываем по названиям
                done.Received = DateTime.Now;
                cur = new HubState();
                chIndex = 0;
                return done;
            }
            if (kind == "MODE" && p.Length >= 2)
            {
                cur.Mode = p[1];
            }
            else if (kind == "SERIAL" && p.Length >= 3)
            {
                float v;
                if (TryFloat(p[2], out v)) cur.SerialValues.Add(new KeyValuePair<string, float>(p[1], v));
            }
            else if (kind == "THERM" && p.Length >= 5)
            {
                ThermInfo t = new ThermInfo();
                t.Name = p[1];
                t.Enabled = p[2] == "1";
                float v;
                t.Value = (TryFloat(p[3], out v) && !float.IsNaN(v)) ? (float?)v : null;
                t.State = p[4];
                cur.Therms.Add(t);
            }
            else if (kind == "CH" && p.Length >= 7)
            {
                ChannelInfo c = new ChannelInfo();
                c.Index = chIndex++;
                c.Name = p[1];
                int pct;
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pct);
                c.Percent = pct;
                c.SourceType = p[3];
                c.SourceName = p[4];
                c.Locked = p[5] == "LOCKED";
                c.Active = p[6] == "ACTIVE";
                cur.Channels.Add(c);
            }
            else if (kind == "TACH" && p.Length >= 5)
            {
                TachInfo t = new TachInfo();
                t.Name = p[1];
                t.Pin = p[2];
                t.State = p[3];
                int rpm;
                int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out rpm);
                t.Rpm = rpm;
                cur.Tachs.Add(t);
            }
            else if (kind == "SPIN" && p.Length >= 4)
            {
                SpinInfo sp = new SpinInfo();
                sp.Name = p[1]; sp.State = p[2];
                int tr; int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out tr);
                sp.Tries = tr;
                cur.Spins.Add(sp);
            }
            else if (kind == "WATCHDOG" && p.Length >= 2)
            {
                cur.WatchdogTripped = p[1] == "TRIPPED";
            }
            return null;
        }

        // PWM-каналы по номеру, затем DC-каналы по номеру
        public static int CompareChannelNames(ChannelInfo a, ChannelInfo b)
        {
            return CompareChannelNames(a.Name, b.Name);
        }

        public static int CompareChannelNames(string a, string b)
        {
            int ka = ChannelSortKey(a), kb = ChannelSortKey(b);
            if (ka != kb) return ka.CompareTo(kb);
            return string.CompareOrdinal(a, b);
        }

        private static int ChannelSortKey(string name)
        {
            int kind = 2;
            if (name.StartsWith("FUN_PWM_", StringComparison.Ordinal)) kind = 0;
            else if (name.StartsWith("FUN_DC_", StringComparison.Ordinal)) kind = 1;
            int us = name.LastIndexOf('_');
            int num;
            if (us < 0 || !int.TryParse(name.Substring(us + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out num)) num = 99;
            return kind * 100 + num;
        }

        private static bool TryFloat(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    public class CurvePoint
    {
        public int Temp;
        public int Percent;
        public CurvePoint() { }
        public CurvePoint(int t, int p) { Temp = t; Percent = p; }
    }

    public class BindInfo
    {
        public string Channel;
        public string SourceType;   // THERM / SERIAL
        public string SourceName;
        public bool Locked;
    }

    // Разбор ответов CURVES? и BINDS?
    public static class ProtocolParsers
    {
        public const int CurvePointCount = 5;

        // CURVE=FUN_PWM_0,0,30,20 ... CURVE=END  ->  канал -> 5 точек
        public static Dictionary<string, CurvePoint[]> ParseCurves(IEnumerable<string> lines)
        {
            Dictionary<string, CurvePoint[]> res = new Dictionary<string, CurvePoint[]>();
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (!l.StartsWith("CURVE=", StringComparison.Ordinal)) continue;
                string[] p = l.Substring(6).Split(',');
                if (p.Length != 4) continue;     // CURVE=END, CURVE=OK,... (5 полей) пропускаем
                int idx, t, pct;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) continue;
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) continue;
                if (!int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out pct)) continue;
                if (idx < 0 || idx >= CurvePointCount) continue;
                CurvePoint[] arr;
                if (!res.TryGetValue(p[0], out arr))
                {
                    arr = new CurvePoint[CurvePointCount];
                    for (int i = 0; i < CurvePointCount; i++) arr[i] = new CurvePoint(0, 0);
                    res[p[0]] = arr;
                }
                arr[idx] = new CurvePoint(t, pct);
            }
            return res;
        }

        // BIND=FUN_PWM_0,SERIAL,P102,LOCKED ... BIND=END
        public static List<BindInfo> ParseBinds(IEnumerable<string> lines)
        {
            List<BindInfo> res = new List<BindInfo>();
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (!l.StartsWith("BIND=", StringComparison.Ordinal)) continue;
                string[] p = l.Substring(5).Split(',');
                if (p.Length < 4) continue;       // BIND=END, BIND=OK,x, BIND=LOCKED,x
                BindInfo b = new BindInfo();
                b.Channel = p[0];
                b.SourceType = p[1];
                b.SourceName = p[2];
                b.Locked = p[3] == "LOCKED";
                res.Add(b);
            }
            res.Sort(delegate (BindInfo x, BindInfo y) { return StatusParser.CompareChannelNames(x.Channel, y.Channel); });
            return res;
        }
    }
}
