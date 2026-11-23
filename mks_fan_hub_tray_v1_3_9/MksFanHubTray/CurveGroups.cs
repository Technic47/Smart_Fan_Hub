// Связанные кривые ("склеивание"): каналы с одной группой всегда имеют одинаковую кривую.
// Группы хранятся в настройках программы (Link.<канал>=<группа>), плата о них ничего не знает:
// при правке одного канала программа сама отправляет такую же кривую остальным членам группы.
using System;
using System.Collections.Generic;

namespace MksFanHub
{
    public static class CurveGroups
    {
        public static string GroupOf(Settings s, string channel)
        {
            string g;
            return s.CurveGroups.TryGetValue(channel, out g) ? g : null;
        }

        // Все члены группы gid в порядке order (порядок каналов на плате)
        public static List<string> Members(Settings s, string gid, IEnumerable<string> order)
        {
            List<string> res = new List<string>();
            if (gid == null) return res;
            foreach (string ch in order)
            {
                string g;
                if (s.CurveGroups.TryGetValue(ch, out g) && g == gid) res.Add(ch);
            }
            return res;
        }

        // Подпись для списка: "гр. A" или пусто
        public static string Label(Settings s, string channel)
        {
            string g = GroupOf(s, channel);
            return g == null ? "—" : "гр. " + g;
        }

        private static string NewGroupId(Settings s)
        {
            HashSet<string> used = new HashSet<string>(s.CurveGroups.Values);
            for (char c = 'A'; c <= 'Z'; c++) if (!used.Contains(c.ToString())) return c.ToString();
            for (int i = 1; ; i++) if (!used.Contains("G" + i)) return "G" + i;
        }

        // Связать primary с каналами others. Группы, в которых уже состоят primary или кто-то из others, сливаются в одну
        // (вся прежняя группа присоединённого канала переезжает вместе с ним) - так можно связать сколько угодно каналов.
        // Имя берётся у группы primary, иначе у первой найденной группы из others, иначе заводится новая.
        public static string Link(Settings s, string primary, IEnumerable<string> others)
        {
            List<string> all = new List<string>();
            all.Add(primary);
            foreach (string o in others) if (o != primary && !all.Contains(o)) all.Add(o);
            string gid = null;
            HashSet<string> merge = new HashSet<string>();
            foreach (string ch in all)
            {
                string g = GroupOf(s, ch);
                if (g == null) continue;
                merge.Add(g);
                if (gid == null) gid = g;
            }
            if (gid == null) gid = NewGroupId(s);
            List<string> move = new List<string>();
            foreach (KeyValuePair<string, string> kv in s.CurveGroups) if (merge.Contains(kv.Value)) move.Add(kv.Key);
            foreach (string ch in move) s.CurveGroups[ch] = gid;
            foreach (string ch in all) s.CurveGroups[ch] = gid;
            return gid;
        }

        // Убрать канал из группы. Если в группе остался один - группа распадается.
        public static void Unlink(Settings s, string channel)
        {
            string gid = GroupOf(s, channel);
            if (gid == null) return;
            s.CurveGroups.Remove(channel);
            DropSingleton(s, gid);
        }

        private static void DropSingleton(Settings s, string gid)
        {
            List<string> left = new List<string>();
            foreach (KeyValuePair<string, string> kv in s.CurveGroups) if (kv.Value == gid) left.Add(kv.Key);
            if (left.Count == 1) s.CurveGroups.Remove(left[0]);
        }

        // Выбросить каналы, которых нет на плате, и группы из одного канала
        public static void Cleanup(Settings s, ICollection<string> known)
        {
            List<string> bad = new List<string>();
            foreach (string ch in s.CurveGroups.Keys) if (!known.Contains(ch)) bad.Add(ch);
            foreach (string ch in bad) s.CurveGroups.Remove(ch);
            HashSet<string> gids = new HashSet<string>(s.CurveGroups.Values);
            foreach (string g in gids) DropSingleton(s, g);
        }

        public static CurvePoint[] CloneCurve(CurvePoint[] src)
        {
            CurvePoint[] a = new CurvePoint[src.Length];
            for (int i = 0; i < a.Length; i++) a[i] = new CurvePoint(src[i].Temp, src[i].Percent);
            return a;
        }

        public static bool SameCurve(CurvePoint[] a, CurvePoint[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i].Temp != b[i].Temp || a[i].Percent != b[i].Percent) return false;
            return true;
        }

        // Кривую канала src (в work) разнести на всех остальных членов его группы. Возвращает, кому разнесли.
        public static List<string> Propagate(Settings s, Dictionary<string, CurvePoint[]> work, string src)
        {
            List<string> changed = new List<string>();
            string gid = GroupOf(s, src);
            CurvePoint[] from;
            if (gid == null || !work.TryGetValue(src, out from)) return changed;
            List<string> targets = new List<string>();
            foreach (KeyValuePair<string, string> kv in s.CurveGroups)
                if (kv.Value == gid && kv.Key != src && work.ContainsKey(kv.Key)) targets.Add(kv.Key);
            foreach (string t in targets)
            {
                if (!SameCurve(work[t], from)) changed.Add(t);
                work[t] = CloneCurve(from);
            }
            return changed;
        }

        // Группы, у которых кривые на плате различаются (кто-то поменял кривую мимо программы)
        public static List<string> Diverged(Settings s, Dictionary<string, CurvePoint[]> board)
        {
            Dictionary<string, CurvePoint[]> first = new Dictionary<string, CurvePoint[]>();
            List<string> bad = new List<string>();
            foreach (KeyValuePair<string, string> kv in s.CurveGroups)
            {
                CurvePoint[] c;
                if (!board.TryGetValue(kv.Key, out c)) continue;
                CurvePoint[] f;
                if (!first.TryGetValue(kv.Value, out f)) { first[kv.Value] = c; continue; }
                if (!SameCurve(f, c) && !bad.Contains(kv.Value)) bad.Add(kv.Value);
            }
            return bad;
        }
    }

    // Диалог "отметь галочками каналы"
    public static class PickChannelsDialog
    {
        // items: (идентификатор канала, подпись). Возвращает выбранные идентификаторы или null при отмене.
        public static List<string> Pick(System.Windows.Forms.IWin32Window owner, string title, string prompt,
                                        List<KeyValuePair<string, string>> items, ICollection<string> preChecked)
        {
            System.Windows.Forms.Form f = UiKit.NewDialog(title, 420, 380);
            System.Windows.Forms.Label l = new System.Windows.Forms.Label();
            l.Text = prompt;
            l.SetBounds(12, 10, 396, 56);
            System.Windows.Forms.CheckedListBox box = new System.Windows.Forms.CheckedListBox();
            box.SetBounds(12, 70, 396, 240);
            box.CheckOnClick = true;
            foreach (KeyValuePair<string, string> kv in items)
                box.Items.Add(kv.Value, preChecked != null && preChecked.Contains(kv.Key));
            System.Windows.Forms.Button all = UiKit.NewButton("Все", 70); all.SetBounds(12, 340, 70, 28);
            System.Windows.Forms.Button none = UiKit.NewButton("Снять", 70); none.SetBounds(88, 340, 70, 28);
            System.Windows.Forms.Button ok = UiKit.NewButton("OK", 90); ok.SetBounds(228, 340, 90, 28);
            ok.DialogResult = System.Windows.Forms.DialogResult.OK;
            System.Windows.Forms.Button cancel = UiKit.NewButton("Отмена", 90); cancel.SetBounds(324, 340, 90, 28);
            cancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            all.Click += delegate { for (int i = 0; i < box.Items.Count; i++) box.SetItemChecked(i, true); };
            none.Click += delegate { for (int i = 0; i < box.Items.Count; i++) box.SetItemChecked(i, false); };
            f.Controls.Add(l); f.Controls.Add(box); f.Controls.Add(all); f.Controls.Add(none); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            List<string> res = null;
            if (f.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
            {
                res = new List<string>();
                for (int i = 0; i < items.Count; i++) if (box.GetItemChecked(i)) res.Add(items[i].Key);
            }
            f.Dispose();
            return res;
        }
    }
}
