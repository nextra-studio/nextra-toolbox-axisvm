using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Small 3D sketch of the axes the loads are reported in: the global X / Y / Z as grey dashed arrows over a ground
    /// grid, the active axes as coloured arrows (x red, y green, z blue, as drawn on the model), and for the custom
    /// system the two rotations as curved arrows with their angles. Drag to orbit; Iso / Top / Front reset the view.
    /// </summary>
    public class AxisPreviewControl : Control
    {
        private LoadSystem system = LoadSystem.Global;
        private CustomAxes custom = new CustomAxes(0, 0);
        private LocalFrame localSample;               // a member's local axes, shown for the Local system

        // Camera: azimuth about Z and elevation, in degrees
        private double camAz = 45, camEl = 30;
        private Point dragFrom;
        private bool dragging;
        private Rectangle[] viewButtons = new Rectangle[0];
        private static readonly string[] ViewNames = { "Iso", "Top", "Front" };

        public AxisPreviewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Cursor = Cursors.SizeAll;
        }

        /// <summary>Shows the given system; <paramref name="sample"/> is the member frame drawn for Local (may be null).</summary>
        public void SetAxes(LoadSystem system, CustomAxes custom, LocalFrame sample)
        {
            this.system = system;
            this.custom = custom ?? new CustomAxes(0, 0);
            localSample = sample;
            Invalidate();
        }

        // ------------------------------------------------------------------
        // Camera / projection
        // ------------------------------------------------------------------

        private Vec3 camRight, camUp, camToward; // screen right, screen up, toward the viewer

        private void SetCamera()
        {
            double az = camAz * Math.PI / 180, el = Math.Min(89.9, Math.Max(-89.9, camEl)) * Math.PI / 180;
            camToward = new Vec3(Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el));
            camRight = Vec3.Cross(-camToward, new Vec3(0, 0, 1)).Normalized();
            camUp = Vec3.Cross(camRight, -camToward);
        }

        private float scale;
        private PointF centre;

        private PointF Screen(Vec3 p)
        {
            return new PointF(centre.X + scale * (float)Vec3.Dot(p, camRight), centre.Y - scale * (float)Vec3.Dot(p, camUp));
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            using (Pen border = new Pen(Color.FromArgb(200, 205, 212))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            SetCamera();
            int textBlock = 54;                                  // room for the direction cosines at the bottom
            scale = Math.Min(Width, Height - textBlock) * 0.39f;
            centre = new PointF(Width / 2f, (Height - textBlock) / 2f + 10);

            DrawGround(g);

            // Global axes, thin and dashed, always there as the reference
            Vec3[] global = { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            string[] gNames = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++) DrawArrow(g, new Vec3(0, 0, 0), 1.05 * global[i], Color.FromArgb(150, 150, 150), 1.3f, true, gNames[i], 8.5f);

            LocalFrame f = ActiveFrame();
            if (system == LoadSystem.Local && f != null)
            {
                // The member itself: a thick bar along its local x, the axes sit at its start node
                using (Pen bar = new Pen(Color.FromArgb(120, 130, 145, 160), 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(bar, Screen(new Vec3(0, 0, 0)), Screen(1.45 * f.Ex));
            }
            if (system == LoadSystem.Custom) DrawRotationArcs(g, false);

            if (f != null)
            {
                string[] names = Labels();
                Vec3[] axes = { f.Ex, f.Ey, f.Ez };
                // Farthest first so the nearest arrow is drawn on top
                foreach (int i in Enumerable.Range(0, 3).OrderBy(i => Vec3.Dot(axes[i], camToward)))
                    DrawArrow(g, new Vec3(0, 0, 0), 0.85 * axes[i], AxisColour(i), 3.2f, false, names[i], 10f);
            }

            if (system == LoadSystem.Custom) DrawRotationArcs(g, true); // angle values on top of the arrows
            DrawCaption(g, f);
            DrawViewButtons(g);
        }

        private LocalFrame ActiveFrame()
        {
            if (system == LoadSystem.Custom) return custom.Frame;
            if (system == LoadSystem.Local) return localSample;
            return LocalFrame.GlobalAxes;
        }

        private string[] Labels()
        {
            if (system == LoadSystem.Custom) return new[] { "x'", "y'", "z'" };
            if (system == LoadSystem.Local) return new[] { "x", "y", "z" };
            return new[] { "X", "Y", "Z" };
        }

        public static Color AxisColour(int i)
        {
            int rgb = AxisTriadDrawer.Rgb[i];
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>A light grid in the global XY plane, so "up" (Z) is never in doubt.</summary>
        private void DrawGround(Graphics g)
        {
            PointF[] quad = { Screen(new Vec3(-1, -1, 0)), Screen(new Vec3(1, -1, 0)), Screen(new Vec3(1, 1, 0)), Screen(new Vec3(-1, 1, 0)) };
            using (Brush fill = new SolidBrush(Color.FromArgb(244, 246, 249))) g.FillPolygon(fill, quad);
            using (Pen grid = new Pen(Color.FromArgb(222, 226, 232), 1f))
            {
                for (double t = -1; t <= 1.001; t += 0.5)
                {
                    g.DrawLine(grid, Screen(new Vec3(t, -1, 0)), Screen(new Vec3(t, 1, 0)));
                    g.DrawLine(grid, Screen(new Vec3(-1, t, 0)), Screen(new Vec3(1, t, 0)));
                }
            }
        }

        private void DrawArrow(Graphics g, Vec3 from, Vec3 to, Color colour, float width, bool dashed, string label, float fontSize)
        {
            PointF a = Screen(from), b = Screen(to);
            float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            using (Pen pen = new Pen(colour, width))
            {
                if (dashed) pen.DashStyle = DashStyle.Dash;
                if (len > 6)
                {
                    // Arrowhead drawn as a filled triangle; the shaft stops at its base
                    float ux = dx / len, uy = dy / len, head = Math.Min(len * 0.45f, 6 + 2.4f * width);
                    PointF neck = new PointF(b.X - ux * head, b.Y - uy * head);
                    g.DrawLine(pen, a, neck);
                    float w = head * 0.45f;
                    PointF[] tri = { b, new PointF(neck.X - uy * w, neck.Y + ux * w), new PointF(neck.X + uy * w, neck.Y - ux * w) };
                    using (Brush br = new SolidBrush(colour)) g.FillPolygon(br, tri);
                }
                else
                {
                    // Axis points (almost) straight at the viewer: a dot, so it is not lost
                    using (Brush br = new SolidBrush(colour)) g.FillEllipse(br, b.X - width - 2, b.Y - width - 2, 2 * width + 4, 2 * width + 4);
                }
            }

            // Label just past the tip, in the arrow's direction
            Vec3 dir = (to - from).Normalized();
            PointF at = Screen(to + 0.14 * dir);
            using (Font font = new Font("Segoe UI", fontSize, dashed ? FontStyle.Regular : FontStyle.Bold))
            using (Brush br = new SolidBrush(colour))
            {
                SizeF s = g.MeasureString(label, font);
                g.DrawString(label, font, br, at.X - s.Width / 2, at.Y - s.Height / 2);
            }
        }

        /// <summary>Curved arrows: αz in the XY plane from X to x', then αx about x' from the turned y to y'.</summary>
        private void DrawRotationArcs(Graphics g, bool labels)
        {
            LocalFrame afterZ = LocalFrame.GlobalAxes.RotatedZX(custom.AzDeg, 0);
            if (Math.Abs(custom.AzDeg) > 0.05)
                DrawArc(g, new Vec3(1, 0, 0), new Vec3(0, 1, 0), custom.AzDeg, 0.62, Color.FromArgb(225, 120, 20), "αz", labels);
            if (Math.Abs(custom.AxDeg) > 0.05)
            {
                // The y axis after the first turn, faint, so the start of the second turn is visible
                if (!labels)
                    using (Pen faint = new Pen(Color.FromArgb(120, AxisColour(1)), 1.2f) { DashStyle = DashStyle.Dot })
                        g.DrawLine(faint, Screen(new Vec3(0, 0, 0)), Screen(0.85 * afterZ.Ey));
                DrawArc(g, afterZ.Ey, afterZ.Ez, custom.AxDeg, 0.5, Color.FromArgb(150, 60, 190), "αx", labels);
            }
        }

        /// <summary>An arc of <paramref name="deg"/> from <paramref name="u"/> toward <paramref name="v"/>, with an arrowhead and its value.</summary>
        private void DrawArc(Graphics g, Vec3 u, Vec3 v, double deg, double r, Color colour, string name, bool label)
        {
            if (label) { DrawArcLabel(g, u, v, deg, r, colour, name); return; }
            int n = Math.Max(8, (int)(Math.Abs(deg) / 4));
            PointF[] pts = new PointF[n + 1];
            for (int k = 0; k <= n; k++)
            {
                double t = deg * k / n * Math.PI / 180;
                pts[k] = Screen(r * (Math.Cos(t) * u + Math.Sin(t) * v));
            }
            using (Pen pen = new Pen(colour, 2f) { EndCap = LineCap.Custom, CustomEndCap = new AdjustableArrowCap(4, 5) })
                g.DrawLines(pen, pts);
        }

        /// <summary>The angle value next to the middle of its arc, on a light box so it stays readable over the arrows.</summary>
        private void DrawArcLabel(Graphics g, Vec3 u, Vec3 v, double deg, double r, Color colour, string name)
        {
            double mid = deg / 2 * Math.PI / 180;
            PointF at = Screen((r + 0.2) * (Math.Cos(mid) * u + Math.Sin(mid) * v));
            string text = name + " = " + deg.ToString("0.#", CultureInfo.InvariantCulture) + "°";
            using (Font font = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (Brush br = new SolidBrush(colour))
            {
                SizeF s = g.MeasureString(text, font);
                RectangleF box = new RectangleF(at.X - s.Width / 2, at.Y - s.Height / 2, s.Width, s.Height);
                using (Brush bg = new SolidBrush(Color.FromArgb(215, Color.White))) g.FillRectangle(bg, box);
                g.DrawString(text, font, br, box.X, box.Y);
            }
        }

        /// <summary>Title top left; the active axes as global direction cosines at the bottom.</summary>
        private void DrawCaption(Graphics g, LocalFrame f)
        {
            string title = system == LoadSystem.Custom ? custom.Describe()
                         : system == LoadSystem.Local ? "Local axes, first member"
                         : "Global axes";
            using (Font bold = new Font("Segoe UI", 8.5f, FontStyle.Bold)) g.DrawString(title, bold, Brushes.DimGray, 6, 5);
            string hint = system == LoadSystem.Custom ? "+ angle = right-hand rule"
                        : system == LoadSystem.Local ? "member drawn grey, axes at its start"
                        : "";
            using (Font small = new Font("Segoe UI", 8f)) g.DrawString(hint, small, Brushes.Gray, 6, 21);
            if (f == null) return;

            string[] names = Labels();
            Vec3[] axes = { f.Ex, f.Ey, f.Ez };
            using (Font mono = new Font("Consolas", 8.5f))
            {
                float y = Height - 52;
                for (int i = 0; i < 3; i++, y += 15)
                {
                    string s = string.Format(CultureInfo.InvariantCulture, "{0,-2} = ({1,6:0.000}, {2,6:0.000}, {3,6:0.000})  in X, Y, Z",
                        names[i], axes[i].X, axes[i].Y, axes[i].Z);
                    using (Brush br = new SolidBrush(AxisColour(i))) g.DrawString(s, mono, br, 6, y);
                }
            }
        }

        private void DrawViewButtons(Graphics g)
        {
            viewButtons = new Rectangle[ViewNames.Length];
            using (Font font = new Font("Segoe UI", 8f))
            {
                int x = Width - 6;
                for (int i = ViewNames.Length - 1; i >= 0; i--)
                {
                    int w = (int)g.MeasureString(ViewNames[i], font).Width + 8;
                    x -= w;
                    viewButtons[i] = new Rectangle(x, 5, w, 18);
                    x -= 3;
                    using (Brush bg = new SolidBrush(Color.FromArgb(236, 240, 245))) g.FillRectangle(bg, viewButtons[i]);
                    g.DrawRectangle(Pens.Silver, viewButtons[i]);
                    g.DrawString(ViewNames[i], font, Brushes.DimGray, viewButtons[i].X + 4, viewButtons[i].Y + 2);
                }
            }
        }

        // ------------------------------------------------------------------
        // Mouse: view buttons and drag to orbit
        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < viewButtons.Length; i++)
            {
                if (!viewButtons[i].Contains(e.Location)) continue;
                if (i == 0) { camAz = 45; camEl = 30; }        // iso: X to the left, Y to the right, Z up
                else if (i == 1) { camAz = -90; camEl = 90; }  // top: X right, Y up
                else { camAz = -90; camEl = 0; }               // front: X right, Z up, looking along +Y
                Invalidate();
                return;
            }
            dragging = true;
            dragFrom = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            camAz -= (e.X - dragFrom.X) * 0.6;
            camEl = Math.Max(-89, Math.Min(90, camEl + (e.Y - dragFrom.Y) * 0.6));
            dragFrom = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }
    }

    /// <summary>
    /// The two angles of the custom system with quick buttons and the 3D preview underneath. Used by the input dialog and
    /// the result window. The angle fields are only enabled while the Custom system is chosen; the preview always shows
    /// the chosen system.
    /// </summary>
    public class AxisRotationEditor : Panel
    {
        private readonly NumericUpDown nudZ, nudX;
        private readonly Control[] customOnly;
        private readonly AxisPreviewControl preview;
        private LoadSystem system = LoadSystem.Global;
        private LocalFrame sample;
        private bool updating;

        /// <summary>Raised when an angle changes (typed, spun or via a quick button).</summary>
        public event EventHandler ValueChanged;

        public AxisRotationEditor(int width, int previewHeight)
        {
            Size = new Size(width, 96 + previewHeight);

            Label lblZ = new Label { Text = "1. Rotate about global Z  (αz):", Location = new Point(0, 4), AutoSize = true };
            nudZ = MakeAngle(new Point(width - 92, 0));
            Label lblX = new Label { Text = "2. then about the new x'  (αx):", Location = new Point(0, 34), AutoSize = true };
            nudX = MakeAngle(new Point(width - 92, 30));
            Label degZ = new Label { Text = "°", Location = new Point(width - 14, 4), AutoSize = true };
            Label degX = new Label { Text = "°", Location = new Point(width - 14, 34), AutoSize = true };

            // Quick buttons: add to the current angle, so 90° turns can be stacked
            Button bReset = Quick("Reset", 0, () => SetAngles(0, 0));
            Button bZ45 = Quick("z +45°", 1, () => SetAngles(Wrap(nudZ.Value + 45), nudX.Value));
            Button bZ90 = Quick("z +90°", 2, () => SetAngles(Wrap(nudZ.Value + 90), nudX.Value));
            Button bX90 = Quick("x' +90°", 3, () => SetAngles(nudZ.Value, Wrap(nudX.Value + 90)));

            preview = new AxisPreviewControl { Location = new Point(0, 96), Size = new Size(width, previewHeight) };

            customOnly = new Control[] { lblZ, nudZ, lblX, nudX, degZ, degX, bReset, bZ45, bZ90, bX90 };
            Controls.AddRange(customOnly);
            Controls.Add(preview);
            nudZ.ValueChanged += delegate { Changed(); };
            nudX.ValueChanged += delegate { Changed(); };
            Apply();
        }

        /// <summary>The angles as typed.</summary>
        public CustomAxes Value
        {
            get { return new CustomAxes((double)nudZ.Value, (double)nudX.Value); }
            set { SetAngles(Clamp(value.AzDeg), Clamp(value.AxDeg)); }
        }

        /// <summary>The system the preview shows (and whether the angle fields are live).</summary>
        public void SetSystem(LoadSystem system, LocalFrame localSample)
        {
            this.system = system;
            sample = localSample;
            Apply();
        }

        private NumericUpDown MakeAngle(Point at)
        {
            return new NumericUpDown
            {
                Location = at, Size = new Size(76, 24), Minimum = -360, Maximum = 360,
                DecimalPlaces = 1, Increment = 5, TextAlign = HorizontalAlignment.Right
            };
        }

        private Button Quick(string text, int slot, Action click)
        {
            int w = (Width - 3 * 6) / 4;
            Button b = new Button { Text = text, Location = new Point(slot * (w + 6), 62), Size = new Size(w, 26) };
            b.Click += delegate { click(); };
            return b;
        }

        private void SetAngles(decimal z, decimal x)
        {
            updating = true;
            nudZ.Value = z;
            nudX.Value = x;
            updating = false;
            Changed();
        }

        private void Changed()
        {
            if (updating) return;
            Apply();
            EventHandler h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void Apply()
        {
            foreach (Control c in customOnly) c.Enabled = system == LoadSystem.Custom;
            preview.SetAxes(system, Value, sample);
        }

        /// <summary>Keeps a stacked angle within -180 .. 180.</summary>
        private static decimal Wrap(decimal a)
        {
            while (a > 180) a -= 360;
            while (a <= -180) a += 360;
            return a;
        }

        private static decimal Clamp(double a) { return (decimal)Math.Max(-360, Math.Min(360, Math.Round(a, 1))); }
    }
}
