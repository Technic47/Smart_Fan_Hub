// Оценка общего состояния: зелёный / жёлтый / красный + причина
using System;
using System.Collections.Generic;

namespace MksFanHub
{
    public enum Health { Ok, Warn, Bad }

    public class HealthResult
    {
        public Health Level;
        public string Reason;   // короткое пояснение для подсказки и всплывающего уведомления
    }

    public static class HealthEvaluator
    {
        public static HealthResult Evaluate(ConnState link, HubState st, DateTime statusAt,
                                            Dictionary<string, float> lastSent, Usage usage)
        {
            return Evaluate(link, st, statusAt, lastSent, usage, 8.0);
        }

        // staleSeconds - через сколько секунд без свежего STATUS считаем, что плата не отвечает
        // (в свёрнутом режиме опрос редкий, порог больше: HubLink.StaleSeconds)
        public static HealthResult Evaluate(ConnState link, HubState st, DateTime statusAt,
                                            Dictionary<string, float> lastSent, Usage usage, double staleSeconds)
        {
            HealthResult r = new HealthResult();
            r.Level = Health.Ok;
            r.Reason = "всё в порядке";

            if (link != ConnState.Connected)
            {
                r.Level = Health.Bad;
                r.Reason = link == ConnState.Searching ? "плата не найдена" : "связь с платой потеряна";
                return r;
            }
            if (st == null)
            {
                r.Level = Health.Warn;
                r.Reason = "ждём данные от платы";
                return r;
            }
            if ((DateTime.Now - statusAt).TotalSeconds > staleSeconds)
            {
                r.Level = Health.Bad;
                r.Reason = "плата не отвечает";
                return r;
            }
            if (st.WatchdogTripped)
            {
                r.Level = Health.Bad;
                r.Reason = "FAILSAFE: плата не получает температуры";
                return r;
            }

            // ошибка автозапуска: вентилятор не раскрутился несколько раз подряд (выход канала 0%)
            foreach (SpinInfo sp in st.Spins)
                if (sp.State == "ERR")
                {
                    r.Level = Health.Bad;
                    r.Reason = usage.ChannelName(sp.Name) + ": вентилятор не раскрутился, канал выключен";
                    return r;
                }

            List<string> warns = new List<string>();

            // Термисторы: ругаемся только на те, что считаются подключёнными
            Dictionary<string, ThermInfo> therms = new Dictionary<string, ThermInfo>();
            foreach (ThermInfo t in st.Therms)
            {
                therms[t.Name] = t;
                if (t.Enabled && t.State == "FAULT" && usage.ThermUsed(t))
                    warns.Add("датчик " + usage.ThermShort(t.Name) + " потерян");
            }

            foreach (ChannelInfo c in st.Channels)
            {
                if (!c.Active) continue;
                TachInfo tc = st.FindTach(c.Name);
                if (!usage.ChannelUsed(c, tc)) continue;
                string nm = usage.ChannelName(c.Name);
                if (tc != null && tc.State == "STALL") warns.Add(nm + " не вращается");

                if (c.SourceType == "THERM")
                {
                    ThermInfo bound;
                    if (therms.TryGetValue(c.SourceName, out bound) && bound.State != "OK")
                        warns.Add(nm + ": нет датчика " + usage.ThermShort(bound.Name));
                }
                else if (c.SourceType == "SERIAL" && lastSent != null && !lastSent.ContainsKey(c.SourceName))
                {
                    warns.Add("нет температуры " + c.SourceName + " для " + nm);
                }
            }

            // убираем повторы (один и тот же датчик мог попасть дважды)
            List<string> uniq = new List<string>();
            foreach (string w in warns) if (!uniq.Contains(w)) uniq.Add(w);

            if (uniq.Count > 0)
            {
                r.Level = Health.Warn;
                r.Reason = string.Join("; ", uniq.ToArray());
            }
            return r;
        }
    }
}
