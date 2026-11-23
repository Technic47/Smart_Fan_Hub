// Общие элементы интерфейса: диалог ввода текста, график кривой, список источников
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace MksFanHub
{
    public static class UiKit
    {
        public static Form NewDialog(string title, int w, int h)
        {
            Form f = new Form();
            f.Text = title;
            f.ClientSize = new Size(w, h);
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.Font = new Font("Segoe UI", 9f);
            return f;
        }

        public static Button NewButton(string text, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = w;
            b.Height = 28;
            b.UseVisualStyleBackColor = true;
            return b;
        }

        // Простой запрос строки. Возвращает null при отмене.
        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            Form f = NewDialog(title, 380, 130);
            Label l = new Label();
            l.Text = label;
            l.SetBounds(12, 12, 356, 20);
            TextBox tb = new TextBox();
            tb.Text = initial ?? "";
            tb.SetBounds(12, 36, 356, 24);
            tb.MaxLength = 40;
            Button ok = NewButton("OK", 90);
            ok.SetBounds(182, 86, 90, 28);
            ok.DialogResult = DialogResult.OK;
            Button cancel = NewButton("Отмена", 90);
            cancel.SetBounds(278, 86, 90, 28);
            cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(l); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.Shown += delegate { tb.SelectAll(); tb.Focus(); };
            string res = null;
            if (f.ShowDialog(owner) == DialogResult.OK) res = tb.Text.Trim();
            f.Dispose();
            return res;
        }

        // Показать ошибку / сообщение
        public static void Error(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "MKS Fan Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static void Info(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "MKS Fan Hub", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return MessageBox.Show(owner, text, "MKS Fan Hub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // Температура и % текущего источника канала (для маркера на графике)
        public static float? SourceTemp(HubState st, ChannelInfo ch)
        {
            if (st == null || ch == null) return null;
            if (ch.SourceType == "THERM")
            {
                foreach (ThermInfo t in st.Therms)
                    if (t.Name == ch.SourceName) return t.Value;
                return null;
            }
            foreach (KeyValuePair<string, float> kv in st.SerialValues)
                if (kv.Key == ch.SourceName) return kv.Value;
            return null;
        }
    }

    // Элемент выпадающего списка источников температуры
    public class SourceItem
    {
        public string Type;    // THERM / SERIAL
        public string Name;
        public string Label;
        public override string ToString() { return Label; }
        public bool Same(string type, string name) { return Type == type && Name == name; }

        public static List<SourceItem> Build(HubState st, Usage usage)
        {
            List<SourceItem> items = new List<SourceItem>();
            if (st == null) return items;
            foreach (ThermInfo t in st.Therms)
            {
                SourceItem it = new SourceItem();
                it.Type = "THERM";
                it.Name = t.Name;
                string note = usage.ThermUsed(t) ? "" : "  (не подключён)";
                it.Label = "Термистор: " + usage.ThermShort(t.Name) + (usage.HasCustomName(Usage.ThKey(t.Name)) ? " [" + t.Name + "]" : "") + note;
                items.Add(it);
            }
            foreach (KeyValuePair<string, float> kv in st.SerialValues)
            {
                SourceItem it = new SourceItem();
                it.Type = "SERIAL";
                it.Name = kv.Key;
                it.Label = "ПК: " + kv.Key;
                items.Add(it);
            }
            return items;
        }
    }

    // График кривой "температура -> %" с маркером текущего значения
    public class CurveChart : Control
    {
        private CurvePoint[] pts = new CurvePoint[0];
        private float? curTemp;
        private int? curPercent;

        public CurveChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void SetData(CurvePoint[] points, float? currentTemp, int? currentPercent)
        {
            pts = points ?? new CurvePoint[0];
            curTemp = currentTemp;
            curPercent = currentPercent;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            ControlPaint.DrawBorder(g, ClientRectangle, Color.Silver, ButtonBorderStyle.Solid);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(44, 12, Math.Max(10, Width - 44 - 14), Math.Max(10, Height - 12 - 30));

            int tMin = 20, tMax = 80;
            if (pts.Length > 0)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                foreach (CurvePoint p in pts) { if (p.Temp < lo) lo = p.Temp; if (p.Temp > hi) hi = p.Temp; }
                if (curTemp.HasValue) { lo = Math.Min(lo, (int)curTemp.Value); hi = Math.Max(hi, (int)Math.Ceiling(curTemp.Value)); }
                tMin = Math.Max(0, (lo - 5) / 10 * 10);
                tMax = Math.Max(tMin + 20, ((hi + 5) / 10 + 1) * 10);
            }

            using (Font small = new Font("Segoe UI", 8f))
            using (Pen grid = new Pen(Color.Gainsboro))
            using (Brush txt = new SolidBrush(Color.DimGray))
            {
                // сетка по %
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    float y = Y(r, pct);
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                    g.DrawString(pct + "%", small, txt, 4, y - 7);
                }
                // сетка по температуре
                int step = (tMax - tMin) > 60 ? 20 : 10;
                for (int t = tMin; t <= tMax; t += step)
                {
                    float x = X(r, t, tMin, tMax);
                    g.DrawLine(grid, x, r.Top, x, r.Bottom);
                    g.DrawString(t + "°", small, txt, x - 8, r.Bottom + 3);
                }
                using (Pen axis = new Pen(Color.Gray)) g.DrawRectangle(axis, r);

                if (pts.Length >= 2)
                {
                    List<PointF> line = new List<PointF>();
                    line.Add(new PointF(r.Left, Y(r, pts[0].Percent)));              // слева от первой точки - плато
                    foreach (CurvePoint p in pts) line.Add(new PointF(X(r, p.Temp, tMin, tMax), Y(r, p.Percent)));
                    line.Add(new PointF(r.Right, Y(r, pts[pts.Length - 1].Percent))); // справа от последней - плато
                    using (Pen curve = new Pen(Color.SteelBlue, 2.2f)) g.DrawLines(curve, line.ToArray());
                    using (Brush dot = new SolidBrush(Color.SteelBlue))
                    using (Brush white = new SolidBrush(Color.White))
                    {
                        for (int i = 0; i < pts.Length; i++)
                        {
                            float x = X(r, pts[i].Temp, tMin, tMax), y = Y(r, pts[i].Percent);
                            g.FillEllipse(dot, x - 4, y - 4, 8, 8);
                            g.FillEllipse(white, x - 2, y - 2, 4, 4);
                            g.DrawString((i + 1).ToString(CultureInfo.InvariantCulture), small, txt, x + 5, y - 16);
                        }
                    }
                }

                if (curTemp.HasValue)
                {
                    float x = X(r, curTemp.Value, tMin, tMax);
                    using (Pen cur = new Pen(Color.Firebrick, 1.5f) { DashStyle = DashStyle.Dash })
                        g.DrawLine(cur, x, r.Top, x, r.Bottom);
                    string label = curTemp.Value.ToString("0.0", CultureInfo.InvariantCulture) + "°" +
                                   (curPercent.HasValue ? " → " + curPercent.Value + "%" : "");
                    using (Brush red = new SolidBrush(Color.Firebrick))
                    {
                        SizeF sz = g.MeasureString(label, small);
                        float lx = x + 4;
                        if (lx + sz.Width > r.Right) lx = x - 4 - sz.Width;
                        g.DrawString(label, small, red, lx, r.Top + 2);
                    }
                }
            }
        }

        private static float X(Rectangle r, float t, int tMin, int tMax)
        {
            float f = (t - tMin) / (float)(tMax - tMin);
            if (f < 0) f = 0; if (f > 1) f = 1;
            return r.Left + f * r.Width;
        }

        private static float Y(Rectangle r, float pct)
        {
            return r.Bottom - (pct / 100f) * r.Height;
        }
    }
}
