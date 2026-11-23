// Что считать подключённым, как называть каналы/термисторы, русские подписи состояний
using System;
using System.Collections.Generic;

namespace MksFanHub
{
    public static class Ru
    {
        public static string Tach(string state, string pin)
        {
            if (pin == "-") return "нет входа";
            switch (state)
            {
                case "OK": return "вращается";
                case "STALL": return "СТОИТ";
                case "NOSIG": return "нет сигнала";
                case "PWM": return "ШИМ <100%";
                case "NONE": return "нет входа";
                default: return state;
            }
        }

        // Событие платы (текст после "EVENT=") -> строка для журнала. null - не показывать (пульс HB).
        // MCUSR платы: P питание, E внешний сброс, B просадка питания, W watchdog, J JTAG, ? неизвестно
        public static string ResetCause(string flags)
        {
            if (string.IsNullOrEmpty(flags) || flags == "?") return "неизвестна (загрузчик сбросил флаги)";
            List<string> r = new List<string>();
            foreach (char c in flags)
            {
                switch (c)
                {
                    case 'P': r.Add("включение питания"); break;
                    case 'E': r.Add("внешний сброс (USB/кнопка)"); break;
                    case 'B': r.Add("ПРОСАДКА ПИТАНИЯ (brown-out)"); break;
                    case 'W': r.Add("watchdog"); break;
                    case 'J': r.Add("JTAG"); break;
                    case '?': break;
                    default: r.Add(c.ToString()); break;
                }
            }
            return r.Count == 0 ? flags : string.Join(", ", r.ToArray());
        }

        public static string Event(string text, Usage usage)
        {
            if (text == null) return null;
            string[] p = text.Split(',');
            switch (p[0])
            {
                case "HB": return null;
                case "BOOT":
                    return "Плата перезагрузилась" + (p.Length > 1 ? " (прошивка " + p[1] + ")" : "") + (p.Length > 2 ? ", причина: " + ResetCause(p[2]) : "");
                case "FAILSAFE":
                    return p.Length > 1 && p[1] == "ON"
                        ? "Плата: FAILSAFE — нет температур ПК больше 8 с, зависимые вентиляторы на 100%"
                        : "Плата: FAILSAFE снят, температуры снова поступают";
                case "MODE":
                    return "Плата: режим " + (p.Length > 1 && p[1] == "1" ? "TMP-MAN" : "TMP-SRL");
                case "BTN":
                    return "Кнопка на плате: " + (p.Length > 1 && p[1] == "LONG" ? "долгое" : "короткое") + " нажатие";
                case "THERM":
                    if (p.Length < 3) break;
                    return "Термистор " + usage.ThermShort(p[1]) + ": " + Therm(p[2], true);
                case "TACHGUARD":
                    if (p.Length < 2) break;
                    return "Плата: на тахо-входе «" + usage.ChannelName(p[1]) + "» шторм помех, вход отключён на 3 с " +
                           "(обычно тахо-провод вентилятора воткнут не в тот канал или линию рубит ШИМ другого вентилятора)";
                case "STALL":
                    if (p.Length < 2) break;
                    return "Плата: вентилятор «" + usage.ChannelName(p[1]) + "» остановился" +
                           (p.Length > 2 && p[2] != "0" ? " (неудачная попытка запуска " + p[2] + ")" : "") + " — пробую раскрутить";
                case "FANOK":
                    if (p.Length < 2) break;
                    return "Плата: вентилятор «" + usage.ChannelName(p[1]) + "» раскручен, работает по кривой";
                case "FANFAIL":
                    if (p.Length < 2) break;
                    return "ОШИБКА: вентилятор «" + usage.ChannelName(p[1]) + "» не раскрутился, канал выключен (сброс — «Автозапуск DC…» → «Сбросить»)";
                case "KICK":
                    if (p.Length < 2) break;
                    return "Плата: DC-вентилятор «" + usage.ChannelName(p[1]) + "» запускается толчком (100% на 3 с, потом по кривой)";
                case "HOLD":
                    if (p.Length < 3) break;
                    return "Удержание оборотов «" + usage.ChannelName(p[1]) + "» " +
                           (p[2] == "FAILSAFE" ? "снято из-за FAILSAFE" : "закончилось, канал вернулся к кривой");
            }
            return "Событие платы: " + text;
        }

        public static string Therm(string state, bool enabled)
        {
            if (!enabled) return "выключен";
            switch (state)
            {
                case "OK": return "норма";
                case "ABSENT": return "не подключён";
                case "FAULT": return "ПОТЕРЯН";
                case "OFF": return "выключен";
                default: return state;
            }
        }
    }

    public class Usage
    {
        // Сколько секунд подряд датчик/тахо должны быть в норме, чтобы считать их подключёнными.
        // Один моргнувший отсчёт на пустом входе подключением не считается.
        public const int AutoDetectSeconds = 20;

        private readonly Settings s;
        private readonly Dictionary<string, DateTime> okSince = new Dictionary<string, DateTime>();

        // Временно показывать всё (пункт меню): нужно, чтобы добраться до скрытых строк и отметить их
        public bool ShowAllTemporarily;

        public Usage(Settings settings) { s = settings; }

        public static string ChKey(string id) { return "CH:" + id; }
        public static string ThKey(string id) { return "TH:" + id; }

        // ---------- названия ----------
        private string Custom(string key)
        {
            string n;
            if (s.Names.TryGetValue(key, out n) && n.Length > 0) return n;
            return null;
        }

        public bool HasCustomName(string key) { return Custom(key) != null; }

        public string ChannelName(string id) { return Custom(ChKey(id)) ?? id; }

        // Короткое имя термистора (для столбца "Датчик" у канала и в сообщениях)
        public string ThermShort(string id) { return Custom(ThKey(id)) ?? id; }

        // Имя в списке температур
        public string ThermTitle(string id) { return Custom(ThKey(id)) ?? ("Термистор " + id); }

        public void SetName(string key, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) s.Names.Remove(key); else s.Names[key] = name;
            s.Save();
        }

        // ---------- подключение ----------
        public int GetConn(string key)
        {
            int v;
            return s.Conn.TryGetValue(key, out v) ? v : 0;
        }

        public void SetConn(string key, int v)
        {
            if (v == 0) s.Conn.Remove(key); else s.Conn[key] = v;
            s.Save();
        }

        private bool Decide(string key, bool okNow)
        {
            int c = GetConn(key);
            if (c == 1) return true;
            if (c == 2) return false;
            return s.Seen.Contains(key) || HasCustomName(key) || (okNow && IsOkLongEnough(key));
        }

        private bool IsOkLongEnough(string key)
        {
            DateTime t;
            return okSince.TryGetValue(key, out t) && (DateTime.Now - t).TotalSeconds >= AutoDetectSeconds;
        }

        public bool ChannelUsed(ChannelInfo c, TachInfo t)
        {
            return Decide(ChKey(c.Name), t != null && t.State == "OK");
        }

        public bool ThermUsed(ThermInfo t)
        {
            return Decide(ThKey(t.Name), t.Enabled && t.State == "OK");
        }

        // Вызывать раз в секунду из UI-потока
        public void Update(HubState st)
        {
            if (st == null) return;
            bool changed = false;
            foreach (ThermInfo t in st.Therms) changed |= Track(ThKey(t.Name), t.Enabled && t.State == "OK");
            foreach (ChannelInfo c in st.Channels)
            {
                TachInfo tc = st.FindTach(c.Name);
                changed |= Track(ChKey(c.Name), tc != null && tc.State == "OK");
            }
            if (changed) s.Save();
        }

        private bool Track(string key, bool ok)
        {
            if (!ok) { okSince.Remove(key); return false; }
            if (!okSince.ContainsKey(key)) okSince[key] = DateTime.Now;
            if (!s.Seen.Contains(key) && IsOkLongEnough(key))
            {
                s.Seen.Add(key);
                Log.Write("Обнаружено подключение: " + key.Substring(3));
                return true;
            }
            return false;
        }

        // Стоит ли писать событие в журнал. События пустых (неподключённых) термисторов - шум.
        public bool EventRelevant(string text, HubState st)
        {
            if (text == null || !text.StartsWith("THERM,", StringComparison.Ordinal)) return true;
            string[] p = text.Split(',');
            if (p.Length < 3 || st == null) return false;
            foreach (ThermInfo t in st.Therms)
                if (t.Name == p[1]) return ThermUsed(t);
            return false;
        }

        // Показывать ли строку: used + режим показа
        public bool ShouldShow(bool used, UnusedMode mode)
        {
            if (used || ShowAllTemporarily) return true;
            return mode != UnusedMode.Hide;
        }

        // Строка показывается приглушённой?
        public bool IsDimmed(bool used, UnusedMode mode)
        {
            return !used && (mode == UnusedMode.Dim || (ShowAllTemporarily && mode == UnusedMode.Hide));
        }

        // Принудительно отметить "замечен в работе" (например, по результату TACHTEST)
        public void MarkSeen(string key)
        {
            if (s.Seen.Add(key)) { Log.Write("Обнаружено подключение: " + key.Substring(3)); s.Save(); }
        }

        public void ResetUserData()
        {
            s.Names.Clear();
            s.Conn.Clear();
            s.Seen.Clear();
            okSince.Clear();
            s.Save();
        }
    }
}
