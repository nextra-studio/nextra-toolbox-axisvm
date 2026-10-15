using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Settings for "N. One-Way Load Panel". The top line says what the run will do with the current
    /// selection (convert panels, or create panels from lines); the preview redraws with every change
    /// so the receiving members and edge strips can be checked before anything is built. Settings are
    /// remembered for the rest of the AxisVM session.
    /// </summary>
    public class OneWayLoadPanelForm : Form
    {
        // Span direction choices.
        private const int DirAuto = 0, DirX = 1, DirY = 2, DirAngle = 3, DirAutoLong = 4;

        // Remembered between runs (per AxisVM session).
        private static int lastDir = DirAuto;
        private static string lastAngle = "0";
        private static bool lastStrips = true;
        private static string lastStripWidth = "300";
        private static string lastTolerance = "15";
        private static bool lastApplyLoad;
        private static int lastLoadCase;
        private static string lastLoad = "1.5";

        private static readonly Color Accent = Color.FromArgb(0, 102, 180);
        private static readonly Color StripColour = Color.FromArgb(230, 126, 34);

        internal OneWayPanelOptions Options { get; private set; }
        public bool ApplyLoad { get; private set; }
        public int LoadCaseId { get; private set; }   // 1-based AxisVM load case index
        public double LoadValue { get; private set; } // kN/m2, positive downward

        private readonly bool convert;
        private RadioButton radAuto, radAutoLong, radX, radY, radAngle;
        private TextBox txtAngle, txtStripWidth, txtTolerance, txtLoad;
        private CheckBox chkStrips, chkLoad;
        private ComboBox cboLoadCase;
        private PictureBox picPreview;

        public OneWayLoadPanelForm(bool convertPanels, string selectionText, List<string> loadCases)
        {
            convert = convertPanels;
            InitializeComponent(selectionText, loadCases);
            RestoreSettings(loadCases.Count);
            UpdateEnabled();
        }

        private void InitializeComponent(string selectionText, List<string> loadCases)
        {
            Theme.Initialize(this);
            Text = "One-Way Load Panel";
            ClientSize = new Size(560, 680);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // ---- header: what will happen -----------------------------------
            Panel header = new Panel { Location = new Point(0, 0), Size = new Size(470, 64), BackColor = Accent };
            header.Controls.Add(new Label
            {
                Text = convert ? "Convert load panels to one-way" : "Create one-way load panels",
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(14, 8),
                AutoSize = true
            });
            header.Controls.Add(new Label
            {
                Text = selectionText,
                ForeColor = Color.White,
                Location = new Point(14, 32),
                Size = new Size(445, 30)
            });
            Controls.Add(header);

            // ---- span direction --------------------------------------------
            GroupBox grpSpan = new GroupBox { Text = "Span direction (the slab spans this way)", Location = new Point(12, 74), Size = new Size(536, 100) };
            // Auto follows each panel's own edges (not global X/Y), so it also suits rotated bays.
            radAuto = new RadioButton { Text = "Auto - shorter span", Location = new Point(12, 22), AutoSize = true };
            radAutoLong = new RadioButton { Text = "Auto - longer span", Location = new Point(170, 22), AutoSize = true };
            radX = new RadioButton { Text = "Global X", Location = new Point(12, 50), AutoSize = true };
            radY = new RadioButton { Text = "Global Y", Location = new Point(100, 50), AutoSize = true };
            radAngle = new RadioButton { Text = "Angle [deg]", Location = new Point(190, 50), AutoSize = true };
            txtAngle = new TextBox { Location = new Point(285, 48), Size = new Size(60, 23) };
            grpSpan.Controls.AddRange(new Control[] { radAuto, radAutoLong, radX, radY, radAngle, txtAngle });
            foreach (RadioButton r in new[] { radAuto, radAutoLong, radX, radY, radAngle }) r.CheckedChanged += OnChanged;
            txtAngle.TextChanged += OnChanged;
            Controls.Add(grpSpan);

            // ---- edge strips -------------------------------------------------
            GroupBox grpStrips = new GroupBox { Text = "Edge strips", Location = new Point(12, 182), Size = new Size(536, 100) };
            chkStrips = new CheckBox { Text = "Split off a strip along each edge beam", Location = new Point(12, 22), AutoSize = true };
            Label lblWidth = new Label { Text = "Strip width = joist spacing [mm]", Location = new Point(30, 50), AutoSize = true };
            txtStripWidth = new TextBox { Location = new Point(260, 47), Size = new Size(70, 23) };
            Label lblStripHint = new Label
            {
                Text = "The strip loads the edge beam; the centre spans to the supports.",
                ForeColor = SystemColors.GrayText,
                Location = new Point(30, 70),
                AutoSize = true
            };
            grpStrips.Controls.AddRange(new Control[] { chkStrips, lblWidth, txtStripWidth, lblStripHint });
            chkStrips.CheckedChanged += OnChanged;
            txtStripWidth.TextChanged += OnChanged;
            Controls.Add(grpStrips);

            // ---- load ----------------------------------------------------------
            GroupBox grpLoad = new GroupBox { Text = "Load", Location = new Point(12, 290), Size = new Size(536, 100) };
            if (convert)
            {
                grpLoad.Controls.Add(new Label
                {
                    Text = "The loads on each panel (area, polygon and point loads) are moved\nonto its new one-way panels, same load case and intensity.",
                    Location = new Point(12, 24),
                    Size = new Size(508, 52)
                });
            }
            else
            {
                chkLoad = new CheckBox { Text = "Apply an area load", Location = new Point(12, 22), AutoSize = true };
                cboLoadCase = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(190, 20), Size = new Size(334, 23) };
                foreach (string name in loadCases) cboLoadCase.Items.Add(name);
                Label lblQ = new Label { Text = "q [kN/m2] (+ = downward)", Location = new Point(30, 52), AutoSize = true };
                txtLoad = new TextBox { Location = new Point(230, 49), Size = new Size(70, 23) };
                grpLoad.Controls.AddRange(new Control[] { chkLoad, cboLoadCase, lblQ, txtLoad });
                chkLoad.CheckedChanged += OnChanged;
                if (loadCases.Count == 0)
                {
                    chkLoad.Enabled = false;
                    chkLoad.Text = "No load case available";
                }
            }
            Controls.Add(grpLoad);

            // ---- preview -------------------------------------------------------
            picPreview = new PictureBox
            {
                Location = new Point(12, 400),
                Size = new Size(536, 200),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White
            };
            picPreview.Paint += PicPreview_Paint;
            Controls.Add(picPreview);

            // ---- advanced + buttons --------------------------------------------
            Controls.Add(new Label { Text = "Angle tolerance [deg]", Location = new Point(12, 620), AutoSize = true });
            txtTolerance = new TextBox { Location = new Point(158, 617), Size = new Size(50, 23) };
            Controls.Add(txtTolerance);
            Controls.Add(new Label
            {
                Text = "Edges and inner beams at 90 +/- this\nangle to the span take load.",
                ForeColor = SystemColors.GrayText,
                Location = new Point(224, 612),
                AutoSize = true
            });

            Button btnOk = new Button { Text = convert ? "Convert" : "Create", Location = new Point(262, 600), Size = new Size(96, 30) };
            Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(362, 600), Size = new Size(96, 30) };
            btnOk.Click += BtnOk_Click;
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Theme.Apply(this, convert ? "Convert to one-way panels" : "Create one-way panels", selectionText, true);
        }

        private void RestoreSettings(int loadCaseCount)
        {
            (lastDir == DirX ? radX : lastDir == DirY ? radY : lastDir == DirAngle ? radAngle : lastDir == DirAutoLong ? radAutoLong : radAuto).Checked = true;
            txtAngle.Text = lastAngle;
            chkStrips.Checked = lastStrips;
            txtStripWidth.Text = lastStripWidth;
            txtTolerance.Text = lastTolerance;
            if (!convert)
            {
                chkLoad.Checked = lastApplyLoad && loadCaseCount > 0;
                if (loadCaseCount > 0) cboLoadCase.SelectedIndex = Math.Min(lastLoadCase, loadCaseCount - 1);
                txtLoad.Text = lastLoad;
            }
        }

        private void OnChanged(object sender, EventArgs e)
        {
            UpdateEnabled();
            picPreview.Invalidate();
        }

        private void UpdateEnabled()
        {
            txtAngle.Enabled = radAngle.Checked;
            txtStripWidth.Enabled = chkStrips.Checked;
            if (!convert)
            {
                cboLoadCase.Enabled = chkLoad.Checked;
                txtLoad.Enabled = chkLoad.Checked;
            }
        }

        private int Direction
        {
            get { return radX.Checked ? DirX : radY.Checked ? DirY : radAngle.Checked ? DirAngle : radAutoLong.Checked ? DirAutoLong : DirAuto; }
        }

        // ------------------------------------------------------------------
        // Preview
        // ------------------------------------------------------------------

        private void PicPreview_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Draw in the original preview coordinate system, so DPI scaling also scales its legend.
            GraphicsState saved = g.Save();
            float scale = Math.Min(picPreview.ClientSize.Width / 536f, picPreview.ClientSize.Height / 200f);
            g.ScaleTransform(scale, scale);
            using (Font previewFont = new Font("Segoe UI", 9F))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // A 2:1 bay, X to the right, Y up. Auto picks its shorter span (Y).
                Rectangle bay = new Rectangle(70, 28, 300, 130);
                double spanDeg = 90.0;
                double angle;
                if (Direction == DirX || Direction == DirAutoLong) spanDeg = 0.0;
                else if (Direction == DirAngle && OneWayPanelOptions.TryParseNumber(txtAngle.Text, out angle)) spanDeg = angle;
                double folded = ((spanDeg % 180.0) + 180.0) % 180.0;
                bool spanAlongX = folded < 45.0 || folded > 135.0;

                // Receiving supports are the sides across the span; the other two are edge beams.
                Point tl = new Point(bay.Left, bay.Top), tr = new Point(bay.Right, bay.Top);
                Point bl = new Point(bay.Left, bay.Bottom), br = new Point(bay.Right, bay.Bottom);
                Point[][] supports = spanAlongX ? new[] { new[] { tl, bl }, new[] { tr, br } } : new[] { new[] { tl, tr }, new[] { bl, br } };
                Point[][] beams = spanAlongX ? new[] { new[] { tl, tr }, new[] { bl, br } } : new[] { new[] { tl, bl }, new[] { tr, br } };

                bool strips = chkStrips.Checked;
                const int strip = 18;
                if (strips)
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(60, StripColour)))
                    {
                        if (spanAlongX)
                        {
                            g.FillRectangle(fill, bay.Left, bay.Top, bay.Width, strip);
                            g.FillRectangle(fill, bay.Left, bay.Bottom - strip, bay.Width, strip);
                        }
                        else
                        {
                            g.FillRectangle(fill, bay.Left, bay.Top, strip, bay.Height);
                            g.FillRectangle(fill, bay.Right - strip, bay.Top, strip, bay.Height);
                        }
                    }
                    using (Pen inset = new Pen(Color.Gray, 1) { DashStyle = DashStyle.Dash })
                    {
                        if (spanAlongX)
                        {
                            g.DrawLine(inset, bay.Left, bay.Top + strip, bay.Right, bay.Top + strip);
                            g.DrawLine(inset, bay.Left, bay.Bottom - strip, bay.Right, bay.Bottom - strip);
                        }
                        else
                        {
                            g.DrawLine(inset, bay.Left + strip, bay.Top, bay.Left + strip, bay.Bottom);
                            g.DrawLine(inset, bay.Right - strip, bay.Top, bay.Right - strip, bay.Bottom);
                        }
                    }
                }

                using (Pen support = new Pen(Accent, 5))
                using (Pen beam = new Pen(strips ? StripColour : Color.DarkGray, strips ? 5 : 2))
                {
                    foreach (Point[] s in beams) g.DrawLine(beam, s[0], s[1]);
                    foreach (Point[] s in supports) g.DrawLine(support, s[0], s[1]);
                }

                // Span arrows in the centre region.
                double rad = spanDeg * Math.PI / 180.0;
                float ux = (float)Math.Cos(rad), uy = -(float)Math.Sin(rad);
                using (Pen arrow = new Pen(Color.Firebrick, 2))
                using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 4))
                {
                    arrow.CustomStartCap = cap;
                    arrow.CustomEndCap = cap;
                    float cx = bay.Left + bay.Width / 2f, cy = bay.Top + bay.Height / 2f;
                    float half = spanAlongX ? 70f : 38f;
                    float px = -uy, py = ux; // across the span
                    for (int k = -1; k <= 1; k++)
                    {
                        float ox = cx + px * 32f * k, oy = cy + py * 32f * k;
                        g.DrawLine(arrow, ox - ux * half, oy - uy * half, ox + ux * half, oy + uy * half);
                    }
                }

                // Global axes and legend.
                using (Pen axis = new Pen(Color.DimGray, 1))
                using (AdjustableArrowCap cap = new AdjustableArrowCap(3, 3))
                {
                    axis.CustomEndCap = cap;
                    g.DrawLine(axis, 18, 178, 44, 178);
                    g.DrawLine(axis, 18, 178, 18, 152);
                }
                using (Brush text = new SolidBrush(Color.DimGray))
                {
                    g.DrawString("X", previewFont, text, 46, 170);
                    g.DrawString("Y", previewFont, text, 12, 136);
                    DrawLegend(g, strips, previewFont);
                    if (Direction == DirAuto) g.DrawString("auto: shorter span", previewFont, text, 300, 4);
                    if (Direction == DirAutoLong) g.DrawString("auto: longer span", previewFont, text, 300, 4);
                }
            }
            g.Restore(saved);
        }

        private void DrawLegend(Graphics g, bool strips, Font previewFont)
        {
            int x = 80, y = 172;
            using (Brush b = new SolidBrush(Accent)) g.FillRectangle(b, x, y + 5, 16, 5);
            g.DrawString("receiving support", previewFont, Brushes.DimGray, x + 20, y);
            x += 140;
            if (strips)
            {
                using (Brush b = new SolidBrush(StripColour)) g.FillRectangle(b, x, y + 5, 16, 5);
                g.DrawString("edge strip -> edge beam", previewFont, Brushes.DimGray, x + 20, y);
                x += 170;
            }
            using (Pen p = new Pen(Color.Firebrick, 2)) g.DrawLine(p, x, y + 8, x + 16, y + 8);
            g.DrawString("span", previewFont, Brushes.DimGray, x + 20, y);
        }

        // ------------------------------------------------------------------
        // OK
        // ------------------------------------------------------------------

        private void BtnOk_Click(object sender, EventArgs e)
        {
            OneWayPanelOptions opt = new OneWayPanelOptions();
            switch (Direction)
            {
                case DirX: opt.UseSpanAngle = true; opt.SpanAngleDeg = 0.0; break;
                case DirY: opt.UseSpanAngle = true; opt.SpanAngleDeg = 90.0; break;
                case DirAngle:
                    double a;
                    if (!OneWayPanelOptions.TryParseNumber(txtAngle.Text, out a)) { Invalid(txtAngle, "Enter the span angle in degrees from global X."); return; }
                    opt.UseSpanAngle = true;
                    opt.SpanAngleDeg = a;
                    break;
                case DirAutoLong: opt.SpanningDirection = 3; break;
                default: opt.SpanningDirection = 0; break;
            }

            opt.NoSpacing = !chkStrips.Checked;
            if (chkStrips.Checked)
            {
                double w;
                if (!OneWayPanelOptions.TryParseNumber(txtStripWidth.Text, out w) || w <= 0.001) { Invalid(txtStripWidth, "Enter a strip width greater than 0 mm."); return; }
                opt.EdgeSpacingMm = w;
            }

            double tol;
            if (!OneWayPanelOptions.TryParseNumber(txtTolerance.Text, out tol) || tol < 0 || tol >= 45) { Invalid(txtTolerance, "Enter a tolerance from 0 up to, but below, 45 degrees."); return; }
            opt.AngleToleranceDeg = tol;

            if (!convert && chkLoad.Checked)
            {
                double q;
                if (!OneWayPanelOptions.TryParseNumber(txtLoad.Text, out q) || q == 0) { Invalid(txtLoad, "Enter a non-zero area load in kN/m2 (positive = downward)."); return; }
                if (cboLoadCase.SelectedIndex < 0) { Invalid(cboLoadCase, "Choose a load case."); return; }
                ApplyLoad = true;
                LoadCaseId = cboLoadCase.SelectedIndex + 1;
                LoadValue = q;
            }

            Options = opt;

            lastDir = Direction;
            lastAngle = txtAngle.Text;
            lastStrips = chkStrips.Checked;
            lastStripWidth = txtStripWidth.Text;
            lastTolerance = txtTolerance.Text;
            if (!convert)
            {
                lastApplyLoad = chkLoad.Checked;
                lastLoadCase = Math.Max(0, cboLoadCase.SelectedIndex);
                lastLoad = txtLoad.Text;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void Invalid(Control field, string message)
        {
            MessageBox.Show(this, message, "One-Way Load Panel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            field.Focus();
        }
    }
}
