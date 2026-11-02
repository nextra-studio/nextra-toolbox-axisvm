using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Plan / elevation of the checked spans, every segment coloured by its utilisation (doc §9). Wheel zooms,
    /// dragging pans, double-click fits, a click selects the nearest span.
    /// </summary>
    public class SlsPlanView : Control
    {
        public enum Projection { TopXY, FrontXZ, SideYZ }

        private List<TimberSlsBackend.Piece> pieces = new List<TimberSlsBackend.Piece>();
        private List<SpanInfo> spans = new List<SpanInfo>();
        private Dictionary<SpanInfo, double> spanUc = new Dictionary<SpanInfo, double>();
        private double scale = 1, offX, offY;
        private Point dragStart;
        private bool dragging, moved;
        private string caption = "";

        public Projection View = Projection.TopXY;
        public int SelectedIndex = -1;
        public bool ShowHint = true;   // the mouse hint; off for exported images
        public DisplaySettings Display = new DisplaySettings();
        public event EventHandler<int> SpanClicked;

        public SlsPlanView()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public void SetData(List<SpanInfo> allSpans, List<TimberSlsBackend.Piece> colouredPieces, Dictionary<SpanInfo, double> ucPerSpan, string what)
        {
            spans = allSpans ?? new List<SpanInfo>();
            pieces = colouredPieces ?? new List<TimberSlsBackend.Piece>();
            spanUc = ucPerSpan ?? new Dictionary<SpanInfo, double>();
            caption = what;
            Fit();
        }

        private PointF Project(Vec v)
        {
            switch (View)
            {
                case Projection.FrontXZ: return new PointF((float)v.X, (float)v.Z);
                case Projection.SideYZ: return new PointF((float)v.Y, (float)v.Z);
                default: return new PointF((float)v.X, (float)v.Y);
            }
        }

        private PointF Screen(Vec v)
        {
            PointF p = Project(v);
            return new PointF((float)(p.X * scale + offX), (float)(-p.Y * scale + offY));
        }

        public void Fit()
        {
            if (spans.Count == 0 || Width < 20 || Height < 20) { Invalidate(); return; }
            List<PointF> pts = spans.SelectMany(s => new[] { Project(s.Start), Project(s.End) }).ToList();
            float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double w = Math.Max(maxX - minX, 0.1), h = Math.Max(maxY - minY, 0.1);
            // Room for the caption (top) and the legend (bottom)
            scale = Math.Min((Width - 80) / w, (Height - 110) / h);
            offX = Width / 2.0 - (minX + maxX) / 2.0 * scale;
            offY = 50 + (Height - 50 - 40) / 2.0 + (minY + maxY) / 2.0 * scale;
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            double f = e.Delta > 0 ? 1.2 : 1 / 1.2;
            offX = e.X - (e.X - offX) * f;
            offY = e.Y - (e.Y - offY) * f;
            scale *= f;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); dragging = true; moved = false; dragStart = e.Location; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) moved = true;
            if (!moved) return;
            offX += dx; offY += dy;
            dragStart = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            if (moved) return;
            int best = -1;
            double bestD = 10;
            foreach (SpanInfo s in spans)
            {
                double d = DistanceToSegment(e.Location, Screen(s.Start), Screen(s.End));
                if (d < bestD) { bestD = d; best = s.Index; }
            }
            if (best >= 0 && SpanClicked != null) SpanClicked(this, best);
        }

        protected override void OnDoubleClick(EventArgs e) { Fit(); }

        private static double DistanceToSegment(Point p, PointF a, PointF b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2)) : 0;
            double dx = p.X - (a.X + t * vx), dy = p.Y - (a.Y + t * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static Color UcColor(double uc)
        {
            int rgb = TimberSlsBackend.UcRgb(uc);
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>
        /// One continuous colour bar from 0 to <paramref name="max"/> (gradient), or the steps, with tick values
        /// below it: the same scale as the members and the AxisVM overlay.
        /// </summary>
        public static void DrawLegend(Graphics g, Font f, Rectangle bar, double max)
        {
            max = Math.Max(1.0, max);
            int n = Math.Max(1, bar.Width);
            for (int i = 0; i < n; i++)
            {
                double uc = max * (i + 0.5) / n;
                using (Pen p = new Pen(UcColor(uc))) g.DrawLine(p, bar.Left + i, bar.Top, bar.Left + i, bar.Bottom);
            }
            using (Pen frame = new Pen(Color.FromArgb(150, 160, 170))) g.DrawRectangle(frame, bar);
            List<double> ticks = new List<double> { 0, 0.5, 0.8, 1.0 };
            if (max > 1.0) ticks.Add(max);
            foreach (double u in ticks)
            {
                int x = bar.Left + (int)Math.Round(u / max * bar.Width);
                using (Pen tp = new Pen(u == 1.0 ? Color.FromArgb(36, 53, 72) : Color.FromArgb(150, 160, 170), u == 1.0 ? 2 : 1))
                    g.DrawLine(tp, x, bar.Top - 3, x, bar.Bottom + 3);
                string t = (u == max && max > 1.0 ? "≥ " : "") + TimberSlsMath.Num(u, u == 0 || u == 1 ? 1 : 2);
                Size sz = TextRenderer.MeasureText(t, f);
                TextRenderer.DrawText(g, t, f, new Point(Math.Max(bar.Left - 2, Math.Min(bar.Right - sz.Width + 4, x - sz.Width / 2)), bar.Bottom + 3), Color.FromArgb(70, 84, 99));
            }
            TextRenderer.DrawText(g, "UC", f, new Point(bar.Right + 6, bar.Top - 2), Color.FromArgb(70, 84, 99));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (Font small = new Font("Segoe UI", 8F))
            using (Font bold = new Font("Segoe UI", (float)Math.Max(5, Display.LabelSize), FontStyle.Bold))
            {
                if (spans.Count == 0)
                {
                    TextRenderer.DrawText(g, "Run the check to see the members coloured by utilisation.", Font, ClientRectangle, Color.Gray,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                // Base lines (members without pieces stay grey)
                using (Pen grey = new Pen(Color.FromArgb(200, 206, 214), 3))
                    foreach (SpanInfo s in spans) g.DrawLine(grey, Screen(s.Start), Screen(s.End));
                // Selected span outline
                SpanInfo sel = spans.FirstOrDefault(s => s.Index == SelectedIndex);
                if (sel != null)
                    using (Pen outline = new Pen(Color.FromArgb(20, 42, 65), (float)Display.LineWidth + 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(outline, Screen(sel.Start), Screen(sel.End));
                // Coloured pieces
                foreach (TimberSlsBackend.Piece p in pieces)
                    using (Pen pen = new Pen(UcColor(p.Uc), (float)Display.LineWidth + (p.Span == sel ? 1.5f : 0)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                        g.DrawLine(pen, Screen(p.Span.PointAt(p.X0)), Screen(p.Span.PointAt(p.X1)));
                // Labels
                // Labels: the selected member first, then the others; each tries a few places along and around its
                // member and is left out when every place would cover a label already drawn
                List<Rectangle> placed = new List<Rectangle>();
                IEnumerable<SpanInfo> order = sel == null ? spans : new[] { sel }.Concat(spans.Where(s => s != sel));
                foreach (SpanInfo s in order)
                {
                    if (!Display.Labels && s != sel) continue;
                    double uc;
                    if (!spanUc.TryGetValue(s, out uc)) continue;
                    string t = double.IsNaN(uc) ? "-" : TimberSlsMath.Num(uc, 2);
                    Size sz = TextRenderer.MeasureText(t, bold);
                    int gap = (int)(Display.LineWidth / 2 + 3);
                    Rectangle? spot = null;
                    foreach (double f in new[] { 0.5, 0.3, 0.7, 0.15, 0.85 })
                    {
                        PointF m = Screen(s.PointAt(s.Length * f));
                        foreach (int dy in new[] { -sz.Height - gap, gap })
                        {
                            Rectangle r = new Rectangle((int)m.X - sz.Width / 2, (int)m.Y + dy, sz.Width, sz.Height);
                            if (!placed.Any(p => p.IntersectsWith(Rectangle.Inflate(r, 2, 1)))) { spot = r; break; }
                        }
                        if (spot.HasValue) break;
                    }
                    if (!spot.HasValue)
                    {
                        if (s != sel) continue;
                        PointF m = Screen(s.PointAt(s.Length / 2));
                        spot = new Rectangle((int)m.X - sz.Width / 2, (int)m.Y - sz.Height - gap, sz.Width, sz.Height);
                    }
                    placed.Add(spot.Value);
                    using (Brush b = new SolidBrush(Color.FromArgb(235, Color.White))) g.FillRectangle(b, spot.Value);
                    TextRenderer.DrawText(g, t, bold, spot.Value, uc > 1 ? Color.FromArgb(180, 20, 20) : Color.FromArgb(36, 53, 72));
                }
                // Caption and legend
                TextRenderer.DrawText(g, caption + "   (" + (View == Projection.TopXY ? "top view XY" : View == Projection.FrontXZ ? "front XZ" : "side YZ") +
                    (ShowHint ? "; wheel = zoom, drag = pan, double-click = fit" : "") + ")", small, new Point(8, 6), Color.FromArgb(70, 84, 99));
                DrawLegend(g, small, new Rectangle(10, Height - 34, Math.Min(420, Width - 20), 12), Display.LegendMax);
            }
        }
    }

    /// <summary>The utilisation colour scale on its own: the same gradient as the AxisVM colouring and the report.</summary>
    public class SlsLegendView : Control
    {
        public DisplaySettings Display = new DisplaySettings();

        public SlsLegendView() { DoubleBuffered = true; BackColor = Color.White; SetStyle(ControlStyles.ResizeRedraw, true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            using (Font f = new Font("Segoe UI", 8.5F))
            {
                TextRenderer.DrawText(g, "Utilisation colour scale (the members are coloured in AxisVM: 'Colour AxisVM model')", f, new Point(10, 4), Color.FromArgb(70, 84, 99));
                SlsPlanView.DrawLegend(g, f, new Rectangle(12, 26, Math.Max(100, Width - 60), 14), Display.LegendMax);
            }
        }
    }

    /// <summary>
    /// The build-up of the deflection of one span for one combination, drawn downward like the sagging beam
    /// (EC5 fig. 7.1): G instantaneous, Q instantaneous, creep of G, creep of Q, the precamber, the checked
    /// quantity and its limit.
    /// </summary>
    public class SlsDiagramView : Control
    {
        private SpanResult result;
        private SlsCombination combo;
        private RowResult row;
        private Dictionary<int, SlsLoadCase> cases;

        public SlsDiagramView() { DoubleBuffered = true; BackColor = Color.White; SetStyle(ControlStyles.ResizeRedraw, true); }

        public void SetData(SpanResult r, SlsCombination c, RowResult governingRow, Dictionary<int, SlsLoadCase> allCases)
        {
            result = r; combo = c; row = governingRow; cases = allCases;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            if (result == null || combo == null || result.Defl == null || result.Defl.X.Length < 2)
            {
                TextRenderer.DrawText(g, result != null && result.Error != null ? result.Error : "Select a span in the table or the view.", Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            SpanDeflections s = result.Defl;
            int n = s.X.Length;
            double kdef = result.Kdef, wc = result.Settings.PrecamberMm;

            // Components, signs normalised like TimberSlsMath.Evaluate
            double[] gI = new double[n], qI = new double[n], gC = new double[n], qC = new double[n];
            foreach (SlsTerm t in combo.Terms)
            {
                double[] w;
                if (!s.W.TryGetValue(t.CaseId, out w)) continue;
                SlsLoadCase lc;
                bool perm = cases != null && cases.TryGetValue(t.CaseId, out lc) && lc.Role == LoadRole.Permanent;
                for (int i = 0; i < n; i++)
                {
                    if (perm) { gI[i] += t.Inst * w[i]; gC[i] += kdef * t.Creep * w[i]; }
                    else { qI[i] += t.Inst * w[i]; qC[i] += kdef * t.Creep * w[i]; }
                }
            }
            double big = 0;
            for (int i = 0; i < n; i++) { double f = gI[i] + qI[i] + gC[i] + qC[i]; if (Math.Abs(f) > Math.Abs(big)) big = f; }
            double sign = big < 0 ? -1 : 1;
            double[] c1 = new double[n], c2 = new double[n], c3 = new double[n], c4 = new double[n], net = new double[n], cam = new double[n];
            for (int i = 0; i < n; i++)
            {
                c1[i] = sign * gI[i];
                c2[i] = c1[i] + sign * qI[i];
                c3[i] = c2[i] + sign * gC[i];
                c4[i] = c3[i] + sign * qC[i];
                cam[i] = -TimberSlsMath.Precamber(s.X[i], s.Length, s.Cantilever, wc);
                net[i] = c4[i] + cam[i];
            }
            double[] q = row != null ? TimberSlsMath.Evaluate(s, combo, row.Row.Quantity, kdef, wc) : null;
            double limit = row != null ? row.Limit : double.NaN;

            double maxW = new[] { c1, c2, c3, c4, net, cam }.SelectMany(a => a).Concat(q ?? new double[0]).Select(Math.Abs).Max();
            if (!double.IsNaN(limit) && !double.IsInfinity(limit)) maxW = Math.Max(maxW, limit);
            if (maxW <= 0) maxW = 1;
            double minW = Math.Min(0, new[] { cam, net }.SelectMany(a => a).Concat(q ?? new double[0]).Min());

            Rectangle plot = new Rectangle(58, 46, Math.Max(50, Width - 58 - 190), Math.Max(40, Height - 46 - 40));
            Func<double, float> X = x => (float)(plot.Left + x / s.Length * plot.Width);
            Func<double, float> Y = w => (float)(plot.Top + (w - minW) / (maxW * 1.08 - minW) * plot.Height);

            using (Font small = new Font("Segoe UI", 8F))
            using (Font bold = new Font("Segoe UI", 9F, FontStyle.Bold))
            {
                // Title
                string title = result.Span.Group + "  " + result.Span.Label + "   " + (s.Cantilever ? "cantilever" : "span") +
                    " l = " + TimberSlsMath.Num(s.Length, 3) + " m   kdef = " + TimberSlsMath.Num(kdef, 2);
                TextRenderer.DrawText(g, title, bold, new Point(8, 4), Color.FromArgb(36, 53, 72));
                string sub = combo.Name + (row != null ? "   |   " + row.Row.Describe() + ":  " + TimberSlsMath.Num(row.W, 2) + " / " +
                    TimberSlsMath.Num(row.Limit, 2) + " mm  →  UC " + TimberSlsMath.Num(row.Uc, 2) : "");
                TextRenderer.DrawText(g, sub, small, new Rectangle(8, 22, Width - 16, 18), Color.FromArgb(70, 84, 99), TextFormatFlags.EndEllipsis);

                // Axes and grid
                using (Pen gridPen = new Pen(Color.FromArgb(232, 236, 241)))
                using (Pen axis = new Pen(Color.FromArgb(120, 132, 145)))
                {
                    double step = NiceStep((maxW * 1.08 - minW) / 5);
                    for (double w = Math.Ceiling(minW / step) * step; w <= maxW * 1.08; w += step)
                    {
                        g.DrawLine(gridPen, plot.Left, Y(w), plot.Right, Y(w));
                        TextRenderer.DrawText(g, TimberSlsMath.Sig(w), small, new Rectangle(0, (int)Y(w) - 8, plot.Left - 4, 16), Color.Gray, TextFormatFlags.Right);
                    }
                    double xs = NiceStep(s.Length / 6);
                    for (double x = 0; x <= s.Length + 1e-9; x += xs)
                        TextRenderer.DrawText(g, TimberSlsMath.Sig(x), small, new Point((int)X(x) - 8, plot.Bottom + 4), Color.Gray);
                    TextRenderer.DrawText(g, "x [m]" + (s.Cantilever ? " from root" : ""), small, new Point(plot.Right - 60, plot.Bottom + 18), Color.Gray);
                    TextRenderer.DrawText(g, "w [mm] ↓", small, new Point(4, plot.Top - 16), Color.Gray);
                    // The undeformed beam
                    using (Pen beam = new Pen(Color.FromArgb(36, 53, 72), 3)) g.DrawLine(beam, plot.Left, Y(0), plot.Right, Y(0));
                    DrawSupport(g, X(0), Y(0), s.Cantilever);
                    if (!s.Cantilever) DrawSupport(g, X(s.Length), Y(0), false);
                }

                // Bands of the build-up
                Band(g, s.X, null, c1, X, Y, Color.FromArgb(120, 0, 102, 180));
                Band(g, s.X, c1, c2, X, Y, Color.FromArgb(90, 86, 160, 220));
                Band(g, s.X, c2, c3, X, Y, Color.FromArgb(110, 230, 126, 34));
                Band(g, s.X, c3, c4, X, Y, Color.FromArgb(80, 245, 176, 65));

                Curve(g, s.X, c4, X, Y, new Pen(Color.FromArgb(36, 53, 72), 1.6f));
                if (wc != 0)
                {
                    Curve(g, s.X, cam, X, Y, new Pen(Color.Gray, 1.2f) { DashStyle = DashStyle.Dash });
                    Curve(g, s.X, net, X, Y, new Pen(Color.FromArgb(36, 53, 72), 1.4f) { DashStyle = DashStyle.Dot });
                }
                if (q != null) Curve(g, s.X, q, X, Y, new Pen(Color.FromArgb(200, 30, 30), 2.4f));
                if (!double.IsNaN(limit) && !double.IsInfinity(limit))
                    using (Pen lp = new Pen(Color.FromArgb(200, 30, 30), 1.2f) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(lp, plot.Left, Y(limit), plot.Right, Y(limit));
                        TextRenderer.DrawText(g, "limit " + TimberSlsMath.Num(limit, 1), small, new Point(plot.Right - 70, (int)Y(limit) - 15), Color.FromArgb(200, 30, 30));
                    }
                if (row != null)
                    using (Brush dot = new SolidBrush(Color.FromArgb(200, 30, 30)))
                        g.FillEllipse(dot, X(row.X) - 4, Y(TimberSlsMath.Evaluate(s, combo, row.Row.Quantity, kdef, wc)[Nearest(s.X, row.X)]) - 4, 8, 8);

                // Legend (EC5 fig. 7.1 names)
                int lx = plot.Right + 16, ly = plot.Top;
                Legend(g, small, ref lx, ref ly, Color.FromArgb(120, 0, 102, 180), "w_inst,G", true);
                Legend(g, small, ref lx, ref ly, Color.FromArgb(90, 86, 160, 220), "w_inst,Q  (Σ = w_inst)", true);
                Legend(g, small, ref lx, ref ly, Color.FromArgb(110, 230, 126, 34), "w_creep,G = kdef·G", true);
                Legend(g, small, ref lx, ref ly, Color.FromArgb(80, 245, 176, 65), "w_creep,Q = kdef·ψ2·Q", true);
                Legend(g, small, ref lx, ref ly, Color.FromArgb(36, 53, 72), "w_fin", false);
                if (wc != 0) Legend(g, small, ref lx, ref ly, Color.Gray, "−w_c, w_net,fin (dotted)", false);
                if (row != null) Legend(g, small, ref lx, ref ly, Color.FromArgb(200, 30, 30), TimberSlsMath.QuantityName(row.Row.Quantity) + " + limit", false);
                int k = TimberSlsMath.ArgMaxAbs(c4);
                ly += 8;
                foreach (string line in new[]
                {
                    "at x = " + TimberSlsMath.Num(s.X[k], 2) + " m:",
                    "w_inst,G  " + TimberSlsMath.Num(c1[k], 2) + " mm",
                    "w_inst    " + TimberSlsMath.Num(c2[k], 2) + " mm",
                    "w_creep   " + TimberSlsMath.Num(c4[k] - c2[k], 2) + " mm",
                    "w_fin     " + TimberSlsMath.Num(c4[k], 2) + " mm",
                    "w_net,fin " + TimberSlsMath.Num(net[k], 2) + " mm"
                })
                {
                    TextRenderer.DrawText(g, line, small, new Point(lx, ly), Color.FromArgb(70, 84, 99));
                    ly += 15;
                }
            }
        }

        private static int Nearest(double[] xs, double x)
        {
            int k = 0;
            for (int i = 1; i < xs.Length; i++) if (Math.Abs(xs[i] - x) < Math.Abs(xs[k] - x)) k = i;
            return k;
        }

        private static void Legend(Graphics g, Font f, ref int x, ref int y, Color c, string text, bool box)
        {
            if (box) using (Brush b = new SolidBrush(c)) g.FillRectangle(b, x, y + 3, 16, 10);
            else using (Pen p = new Pen(c, 2)) g.DrawLine(p, x, y + 8, x + 16, y + 8);
            TextRenderer.DrawText(g, text, f, new Point(x + 20, y), Color.FromArgb(70, 84, 99));
            y += 17;
        }

        private static void Band(Graphics g, double[] xs, double[] top, double[] bottom, Func<double, float> X, Func<double, float> Y, Color c)
        {
            List<PointF> pts = new List<PointF>();
            for (int i = 0; i < xs.Length; i++) pts.Add(new PointF(X(xs[i]), Y(bottom[i])));
            for (int i = xs.Length - 1; i >= 0; i--) pts.Add(new PointF(X(xs[i]), Y(top == null ? 0 : top[i])));
            using (Brush b = new SolidBrush(c)) g.FillPolygon(b, pts.ToArray());
        }

        private static void Curve(Graphics g, double[] xs, double[] w, Func<double, float> X, Func<double, float> Y, Pen pen)
        {
            using (pen)
            {
                PointF[] pts = xs.Select((x, i) => new PointF(X(x), Y(w[i]))).ToArray();
                if (pts.Length > 1) g.DrawLines(pen, pts);
            }
        }

        private static void DrawSupport(Graphics g, float x, float y, bool clamp)
        {
            using (Pen p = new Pen(Color.FromArgb(70, 84, 99), 1.5f))
            {
                if (clamp)
                {
                    g.DrawLine(p, x, y - 14, x, y + 14);
                    for (int i = -12; i <= 12; i += 6) g.DrawLine(p, x, y + i, x - 6, y + i + 5);
                }
                else g.DrawPolygon(p, new[] { new PointF(x, y + 2), new PointF(x - 7, y + 14), new PointF(x + 7, y + 14) });
            }
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double m = raw / p;
            return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p;
        }
    }

    /// <summary>
    /// Explanation drawings: the deflection terms (EC5 fig. 7.1 and NEN-EN 1990/NB fig. A1.1, with the selected
    /// limit row highlighted) or how a member is measured (span types and span splitting).
    /// </summary>
    public class SlsGuideView : Control
    {
        public enum GuideMode { Terms, Types }
        private GuideMode mode;
        private SlsQuantity? highlight;

        public SlsGuideView(GuideMode mode) { this.mode = mode; DoubleBuffered = true; BackColor = Color.White; SetStyle(ControlStyles.ResizeRedraw, true); }

        public SlsQuantity? Highlight { get { return highlight; } set { highlight = value; Invalidate(); } }

        private static readonly Color Ink = Color.FromArgb(36, 53, 72), Muted = Color.FromArgb(100, 114, 128), Hot = Color.FromArgb(200, 30, 30),
            Blue = Color.FromArgb(0, 102, 180), Orange = Color.FromArgb(230, 126, 34);

        public static string Explain(SlsQuantity q)
        {
            switch (q)
            {
                case SlsQuantity.Winst: return "w_inst, instantaneous: the elastic deflection under the characteristic combination with E_mean, measured from the unloaded (cambered) beam.";
                case SlsQuantity.Wcreep: return "w_creep, creep: what is added over the service life, kdef × (G + ψ2·Q). Only the permanent load and the quasi-permanent share of Q creep.";
                case SlsQuantity.Wfin: return "w_fin, final: w_inst + w_creep, measured from the unloaded (cambered) beam (EC5 2.2.3(5)).";
                case SlsQuantity.WnetFin: return "w_net,fin = w_eind, net final: w_fin minus the precamber w_c, measured from the straight line between the supports. NL default ≤ l/250.";
                case SlsQuantity.Wbij: return "w_bij = w2 + w3, additional deflection: what happens after the finishes and partitions are in place: creep (w2) plus the rest of the variable load (w3). Protects finishes, partitions and glazing. NL default ≤ 3/1000·l (l/333), brittle partitions l/500. Precamber does not help.";
                case SlsQuantity.WbijFreq: return "w_bij, NA frequent reading: kdef·(G + ψ2·Q) + ψ1·Q (w1 read as G only). Larger than Harff's reading for categories C, D and E; tick it to check both.";
                default: return "w_max, appearance: the quasi-permanent final deflection (G + ψ2·Q)(1 + kdef) minus the precamber, measured from the line between the supports. NL default ≤ l/250.";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (Font f = new Font("Segoe UI", 8.5F))
            using (Font b = new Font("Segoe UI", 9F, FontStyle.Bold))
            {
                if (mode == GuideMode.Terms) Terms(g, f, b); else Types(g, f, b);
            }
        }

        // ------------------------------------------------------------------ deflection terms

        /// <summary>One row of the terms table: the symbol, what it means, its formula and the NL default limit.</summary>
        private class TermRow
        {
            public SlsQuantity? Q; public string Symbol, Meaning, Formula, Limit;
            public TermRow(SlsQuantity? q, string symbol, string meaning, string formula, string limit) { Q = q; Symbol = symbol; Meaning = meaning; Formula = formula; Limit = limit; }
        }

        private static readonly TermRow[] TermRows =
        {
            new TermRow(SlsQuantity.Winst, "w_{inst}", "elastic, characteristic comb. (E_{mean})", "w_{G} + w_{Q,1} + Σ ψ_{0,i}·w_{Q,i}", "EC5: l/300 – l/500"),
            new TermRow(SlsQuantity.Wcreep, "w_{creep}", "creep over the service life", "k_{def}·(w_{G} + Σ ψ_{2,i}·w_{Q,i})", "-"),
            new TermRow(SlsQuantity.Wfin, "w_{fin}", "final = instantaneous + creep", "w_{inst} + w_{creep}", "EC5: l/150 – l/300"),
            new TermRow(SlsQuantity.WnetFin, "w_{net,fin} = w_{eind}", "net final, from the support line", "w_{fin} − w_{c}", "NL: l/250"),
            new TermRow(SlsQuantity.Wbij, "w_{bij} = w_{2} + w_{3}", "additional, after finishes (Harff)", "k_{def}·w_{G} + w_{Q}·(1 + ψ_{2}k_{def} − ψ_{2})", "NL: 3l/1000; l/500 brittle"),
            new TermRow(SlsQuantity.WbijFreq, "w_{bij} (NA frequent)", "additional, NA reading (w_{1} = G)", "k_{def}·(w_{G} + ψ_{2}w_{Q}) + ψ_{1}w_{Q}", "NL: 3l/1000"),
            new TermRow(SlsQuantity.WqpFin, "w_{max}", "appearance: quasi-permanent sag", "(w_{G} + ψ_{2}w_{Q})·(1 + k_{def}) − w_{c}", "NL: l/250")
        };

        /// <summary>
        /// Draws text with subscripts: "w_{net,fin}" sets "net,fin" small and lowered, "w_c" a single character.
        /// Returns the width. With <paramref name="draw"/> false it only measures.
        /// </summary>
        public static float Rich(Graphics g, string text, Font f, Color col, float x, float y, bool draw = true)
        {
            float x0 = x;
            using (Font sub = new Font(f.FontFamily, f.Size * 0.74f, f.Style))
            using (Brush br = new SolidBrush(col))
            {
                StringFormat sf = StringFormat.GenericTypographic;
                float subDy = f.GetHeight(g) * 0.38f;
                int i = 0;
                StringBuilder run = new StringBuilder();
                Action flush = () =>
                {
                    if (run.Length == 0) return;
                    string s = run.ToString();
                    if (draw) g.DrawString(s, f, br, x, y, sf);
                    x += g.MeasureString(s.Replace(' ', ' '), f, 10000, sf).Width;
                    run.Clear();
                };
                while (i < text.Length)
                {
                    if (text[i] == '_' && i + 1 < text.Length)
                    {
                        flush();
                        string s;
                        if (text[i + 1] == '{')
                        {
                            int end = text.IndexOf('}', i + 2);
                            if (end < 0) end = text.Length;
                            s = text.Substring(i + 2, end - i - 2);
                            i = end + 1;
                        }
                        else { s = text.Substring(i + 1, 1); i += 2; }
                        if (draw) g.DrawString(s, sub, br, x, y + subDy, sf);
                        x += g.MeasureString(s, sub, 10000, sf).Width + 1;
                        continue;
                    }
                    run.Append(text[i]);
                    i++;
                }
                flush();
            }
            return x - x0;
        }

        private void Terms(Graphics g, Font f, Font b)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(g, "What each check measures   (EC5 fig. 7.1 left bars, NEN-EN 1990/NB fig. A1.1 right bars)", b, new Point(10, 4), Ink);
            // Drawing on top, table below
            int tableRows = TermRows.Length + 1;
            int rowH = Math.Max(19, (int)(f.GetHeight(g) * 1.5));
            int tableH = tableRows * rowH + 22;
            int drawH = Math.Max(130, Height - 30 - tableH);
            Rectangle area = new Rectangle(10, 26, Math.Max(240, Width - 20), drawH);

            int dimW = Math.Min(330, Math.Max(250, area.Width * 2 / 5));
            int left = area.Left + 14, right = area.Right - dimW;
            float scale = (area.Height - 40) / 1.15f, y0 = area.Top + 12;
            float wc = 0.16f, wInst = 0.52f, wFin = 0.9f, w1 = 0.38f, w2 = 0.70f;
            Func<float, float, float> Curve = (t, peak) => (float)(4 * t * (1 - t)) * peak;
            float chordY = y0 + wc * scale;

            using (Pen chord = new Pen(Muted, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(chord, left, chordY, right, chordY);
            Support(g, left, chordY); Support(g, right, chordY);
            Shape(g, left, right, chordY, t => -Curve(t, wc) * scale, new Pen(Muted, 1.5f) { DashStyle = DashStyle.Dot });
            Shape(g, left, right, chordY, t => (Curve(t, w1) - Curve(t, wc)) * scale, new Pen(Color.FromArgb(150, Blue), 1) { DashStyle = DashStyle.Dash });
            Shape(g, left, right, chordY, t => (Curve(t, wInst) - Curve(t, wc)) * scale, new Pen(Blue, 2));
            Shape(g, left, right, chordY, t => (Curve(t, wFin) - Curve(t, wc)) * scale, new Pen(Orange, 2.4f));
            // midspan guide line where the bars measure
            float mid = (left + right) / 2f;
            using (Pen guide = new Pen(Color.FromArgb(210, 216, 222), 1) { DashStyle = DashStyle.Dot }) g.DrawLine(guide, mid, y0 - 4, right + 10, y0 - 4);

            // Curve legend under the drawing (no labels on the curves themselves)
            float ly = area.Bottom - 16, lx = left;
            Action<Pen, string> legend = (pen, text) =>
            {
                using (pen) g.DrawLine(pen, lx, ly + 7, lx + 22, ly + 7);
                lx += 26;
                lx += Rich(g, text, f, Ink, lx, ly) + 14;
            };
            legend(new Pen(Muted, 1.5f) { DashStyle = DashStyle.Dot }, "unloaded (precamber w_{c})");
            legend(new Pen(Color.FromArgb(150, Blue), 1) { DashStyle = DashStyle.Dash }, "w_{1}");
            legend(new Pen(Blue, 2), "instantaneous");
            legend(new Pen(Orange, 2.4f), "final");

            // Dimension bars at midspan: EC5 group, NL group
            int col = Math.Max(26, (dimW - 40) / 7);
            int x1 = right + 22, x2 = x1 + 4 * col + 6;
            float top = y0;
            Func<float, float> At = d => top + d * scale;
            Dim(g, f, x1, top, chordY, "w_{c}", null);
            Dim(g, f, x1 + col, top, At(wInst), "w_{inst}", SlsQuantity.Winst);
            Dim(g, f, x1 + col, At(wInst), At(wFin), "w_{creep}", SlsQuantity.Wcreep);
            Dim(g, f, x1 + 2 * col, top, At(wFin), "w_{fin}", SlsQuantity.Wfin);
            Dim(g, f, x1 + 3 * col, chordY, At(wFin), "w_{net,fin}", SlsQuantity.WnetFin);
            Dim(g, f, x2, top, At(w1), "w_{1}", null);
            Dim(g, f, x2, At(w1), At(w2), "w_{2}", null);
            Dim(g, f, x2, At(w2), At(wFin), "w_{3}", null);
            Dim(g, f, x2 + col, At(w1), At(wFin), "w_{bij}", SlsQuantity.Wbij, SlsQuantity.WbijFreq);
            Dim(g, f, x2 + 2 * col, chordY, At(wFin), "w_{max}", SlsQuantity.WqpFin, SlsQuantity.WnetFin);
            TextRenderer.DrawText(g, "EC5", b, new Point(x1, area.Bottom - 16), Ink);
            TextRenderer.DrawText(g, "NL NA", b, new Point(x2, area.Bottom - 16), Ink);

            // Table
            int ty = area.Bottom + 6, tx = 10, tw = Width - 20;
            int c1 = tx, c2 = tx + (int)(tw * 0.18), c3 = tx + (int)(tw * 0.45), c4 = tx + (int)(tw * 0.79);
            using (Brush head = new SolidBrush(Color.FromArgb(234, 241, 248))) g.FillRectangle(head, tx, ty, tw, rowH);
            foreach (var h in new[] { Tuple.Create(c1, "Check"), Tuple.Create(c2, "What it is"), Tuple.Create(c3, "Formula (per span)"), Tuple.Create(c4, "NL default") })
                TextRenderer.DrawText(g, h.Item2, b, new Point(h.Item1 + 4, ty + (rowH - b.Height) / 2), Ink);
            for (int i = 0; i < TermRows.Length; i++)
            {
                TermRow r = TermRows[i];
                int yy = ty + (i + 1) * rowH;
                bool hot = highlight.HasValue && r.Q == highlight;
                if (hot) using (Brush hb = new SolidBrush(Color.FromArgb(253, 232, 230))) g.FillRectangle(hb, tx, yy, tw, rowH);
                using (Pen line = new Pen(Color.FromArgb(222, 228, 234))) g.DrawLine(line, tx, yy + rowH, tx + tw, yy + rowH);
                Color ink = hot ? Hot : Ink;
                float ry = yy + (rowH - f.GetHeight(g)) / 2;
                using (Font sym = new Font(f, hot ? FontStyle.Bold : FontStyle.Regular))
                    Rich(g, r.Symbol, sym, ink, c1 + 4, ry);
                ClipRich(g, r.Meaning, f, hot ? Hot : Muted, c2 + 4, ry, c3 - c2 - 8);
                ClipRich(g, r.Formula, f, ink, c3 + 4, ry, c4 - c3 - 8);
                ClipRich(g, r.Limit, f, hot ? Hot : Muted, c4 + 4, ry, tx + tw - c4 - 8);
            }
            int fy = ty + tableRows * rowH + 3;
            Rich(g, "w_{1} = elastic under G + ψ_{2}Q, w_{2} = its creep, w_{3} = rest of Q.   l = span; cantilever l_{rep} = factor × length.   Precamber w_{c} never helps w_{bij}.", f, Muted, tx + 2, fy);
        }

        /// <summary>Rich text that is cut off with an ellipsis when it does not fit.</summary>
        private static void ClipRich(Graphics g, string text, Font f, Color col, float x, float y, float width)
        {
            if (Rich(g, text, f, col, x, y, false) <= width) { Rich(g, text, f, col, x, y); return; }
            GraphicsState s = g.Save();
            g.SetClip(new RectangleF(x, y - 2, width - 10, f.GetHeight(g) * 1.6f));
            Rich(g, text, f, col, x, y);
            g.Restore(s);
            TextRenderer.DrawText(g, "…", f, new Point((int)(x + width - 12), (int)y), col);
        }

        private void Dim(Graphics g, Font f, int x, float y1, float y2, string label, SlsQuantity? q, SlsQuantity? q2 = null)
        {
            bool hot = highlight.HasValue && (q == highlight || q2 == highlight);
            using (Pen p = new Pen(hot ? Hot : Ink, hot ? 3 : 1.2f))
            {
                g.DrawLine(p, x, y1, x, y2);
                g.DrawLine(p, x - 4, y1, x + 4, y1);
                g.DrawLine(p, x - 4, y2, x + 4, y2);
            }
            // The label runs vertically along its bar (subscripts kept), so neighbouring bars never collide
            using (Font bf = new Font(f, hot ? FontStyle.Bold : FontStyle.Regular))
            {
                float w = Rich(g, label, bf, Ink, 0, 0, false);
                GraphicsState state = g.Save();
                g.TranslateTransform(x + 4, (y1 + y2) / 2 + w / 2);
                g.RotateTransform(-90);
                Rich(g, label, bf, hot ? Hot : Ink, 0, 0);
                g.Restore(state);
            }
        }

        private static void Shape(Graphics g, int left, int right, float y0, Func<float, float> dy, Pen pen)
        {
            using (pen)
            {
                PointF[] pts = Enumerable.Range(0, 41).Select(i => { float t = i / 40f; return new PointF(left + t * (right - left), y0 + dy(t)); }).ToArray();
                g.DrawLines(pen, pts);
            }
        }

        private static void Support(Graphics g, float x, float y)
        {
            using (Pen p = new Pen(Muted, 1.5f)) g.DrawPolygon(p, new[] { new PointF(x, y), new PointF(x - 7, y + 12), new PointF(x + 7, y + 12) });
        }

        // ------------------------------------------------------------------ span types

        private void Types(Graphics g, Font f, Font b)
        {
            int w = (Width - 30) / 2, h = (Height - 30) / 2;
            Panel(g, f, b, new Rectangle(10, 10, w, h), "Span: relative to both ends", "The line between the two span ends is removed, so a sagging supporting beam is not counted. l_rep = l.", 0);
            Panel(g, f, b, new Rectangle(20 + w, 10, w, h), "Cantilever: relative to the root", "Tip minus root (option: also the root rotation). l_rep = 2 × l. The root is the end that moves least.", 1);
            Panel(g, f, b, new Rectangle(10, 20 + h, w, h), "Auto: middle, left and right, critical one", "Each span is checked as a span and as a cantilever from either end; the highest UC is used and shown as 'Checked as'.", 2);
            Panel(g, f, b, new Rectangle(20 + w, 20 + h, w, h), "Whole beam: where spans start and end", "Collinear lines form one beam; it is split at supports, columns and non-parallel members (▲). 'Each line' checks every AxisVM line alone.", 3);
        }

        private void Panel(Graphics g, Font f, Font b, Rectangle r, string title, string text, int kind)
        {
            using (Pen frame = new Pen(Color.FromArgb(214, 222, 230))) g.DrawRectangle(frame, r);
            TextRenderer.DrawText(g, title, b, new Point(r.Left + 8, r.Top + 6), Ink);
            int textH;
            using (Font sf = new Font("Segoe UI", 8F))
            {
                textH = TextRenderer.MeasureText(g, text, sf, new Size(r.Width - 16, 1000), TextFormatFlags.WordBreak).Height + 6;
                TextRenderer.DrawText(g, text, sf, new Rectangle(r.Left + 8, r.Bottom - textH, r.Width - 16, textH), Muted, TextFormatFlags.WordBreak);
            }
            Rectangle d = new Rectangle(r.Left + 26, r.Top + 36, r.Width - 52, Math.Max(24, r.Bottom - textH - 8 - (r.Top + 36)));
            float y = d.Top + d.Height * 0.3f, sag = d.Height * 0.55f;
            using (Pen beam = new Pen(Ink, 2))
            using (Pen bent = new Pen(Blue, 2.2f))
            using (Pen dash = new Pen(Muted, 1) { DashStyle = DashStyle.Dash })
            using (Pen arrow = new Pen(Hot, 2) { EndCap = LineCap.ArrowAnchor })
            {
                switch (kind)
                {
                    case 0:
                    {
                        // Ends settle (flexible supports): the chord moves, w is measured from it
                        float yl = y + sag * 0.15f, yr = y + sag * 0.3f;
                        g.DrawLine(beam, d.Left, y, d.Right, y);
                        Support(g, d.Left, yl); Support(g, d.Right, yr);
                        g.DrawLine(dash, d.Left, yl, d.Right, yr);
                        PointF[] pts = Enumerable.Range(0, 31).Select(i => { float t = i / 30f; return new PointF(d.Left + t * d.Width, yl + (yr - yl) * t + 4 * t * (1 - t) * sag * 0.6f); }).ToArray();
                        g.DrawLines(bent, pts);
                        float mx = d.Left + d.Width / 2f, my = (yl + yr) / 2;
                        g.DrawLine(arrow, mx, my, mx, my + sag * 0.6f);
                        TextRenderer.DrawText(g, "w", f, new Point((int)mx + 4, (int)(my + sag * 0.2f)), Hot);
                        break;
                    }
                    case 1:
                    {
                        g.DrawLine(beam, d.Left, y, d.Right, y);
                        using (Pen clamp = new Pen(Muted, 1.5f))
                        {
                            g.DrawLine(clamp, d.Left, y - 14, d.Left, y + 14);
                            for (int i = -12; i <= 12; i += 6) g.DrawLine(clamp, d.Left, y + i, d.Left - 6, y + i + 5);
                        }
                        PointF[] pts = Enumerable.Range(0, 31).Select(i => { float t = i / 30f; return new PointF(d.Left + t * d.Width, y + t * t * sag); }).ToArray();
                        g.DrawLines(bent, pts);
                        g.DrawLine(arrow, d.Right - 2, y, d.Right - 2, y + sag);
                        TextRenderer.DrawText(g, "w", f, new Point(d.Right - 18, (int)(y + sag * 0.3f)), Hot);
                        TextRenderer.DrawText(g, "root", f, new Point(d.Left + 4, (int)y - 18), Muted);
                        break;
                    }
                    case 2:
                    {
                        float sx = d.Left + d.Width * 0.62f;
                        g.DrawLine(beam, d.Left, y, d.Right, y);
                        Support(g, d.Left, y); Support(g, sx, y);
                        PointF[] pts = Enumerable.Range(0, 41).Select(i =>
                        {
                            float t = i / 40f, x = d.Left + t * d.Width;
                            float dy = x <= sx ? (float)(4 * ((x - d.Left) / (sx - d.Left)) * (1 - (x - d.Left) / (sx - d.Left))) * sag * 0.25f
                                               : ((x - sx) / (d.Right - sx)) * ((x - sx) / (d.Right - sx)) * sag * 0.9f;
                            return new PointF(x, y + dy);
                        }).ToArray();
                        g.DrawLines(bent, pts);
                        TextRenderer.DrawText(g, "span", f, new Point(d.Left + (int)((sx - d.Left) / 2) - 14, (int)y - 20), Hot);
                        TextRenderer.DrawText(g, "cantilever →", f, new Point((int)sx + 6, (int)y - 20), Hot);
                        break;
                    }
                    default:
                    {
                        float a = d.Left + d.Width * 0.33f, c2 = d.Left + d.Width * 0.66f;
                        g.DrawLine(beam, d.Left, y, d.Right, y);
                        Support(g, d.Left, y); Support(g, d.Right, y);
                        Support(g, a, y);
                        using (Pen col = new Pen(Muted, 3)) g.DrawLine(col, c2, y, c2, y + sag);
                        using (Pen cross = new Pen(Muted, 3)) g.DrawLine(cross, d.Left + d.Width * 0.85f, y - sag * 0.6f, d.Left + d.Width * 0.85f, y + 2);
                        foreach (float x in new[] { d.Left, a, c2, d.Right })
                            g.FillPolygon(Brushes.OrangeRed, new[] { new PointF(x, y - 3), new PointF(x - 5, y - 12), new PointF(x + 5, y - 12) });
                        TextRenderer.DrawText(g, "column", f, new Point((int)c2 + 5, (int)(y + sag * 0.45f)), Muted);
                        TextRenderer.DrawText(g, "beam ⟂", f, new Point((int)(d.Left + d.Width * 0.85f) + 5, (int)(y - sag * 0.6f)), Muted);
                        TextRenderer.DrawText(g, "span 1", f, new Point(d.Left + 8, (int)y - 26), Ink);
                        TextRenderer.DrawText(g, "span 2", f, new Point((int)a + 8, (int)y - 26), Ink);
                        TextRenderer.DrawText(g, "span 3", f, new Point((int)c2 + 8, (int)y - 26), Ink);
                        break;
                    }
                }
            }
        }
    }
}
