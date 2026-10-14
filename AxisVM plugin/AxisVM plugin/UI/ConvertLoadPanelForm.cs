using System;
using System.Drawing;
using System.Windows.Forms;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Dialog for the "Convert Load Panel -> One-Way" addon. Same inputs as the line-based
    /// One-Way dialog (span direction, edge spacing, angle tolerance) - kept as its own type so the
    /// two addons stay fully independent.
    /// </summary>
    public class ConvertLoadPanelForm : Form
    {
        public int SpanningDirection { get; private set; } // 0=Auto, 1=Dir1, 2=Dir2
        public bool NoSpacing { get; private set; }
        public double EdgeSpacing { get; private set; }
        public double AngleTolerance { get; private set; }

        private RadioButton radAuto;
        private RadioButton radDir1;
        private RadioButton radDir2;
        private CheckBox chkNoSpacing;
        private TextBox txtEdgeSpacing;
        private Label lblEdgeSpacing;
        private TextBox txtAngleTolerance;
        private Label lblAngleTolerance;
        private Button btnOk;
        private Button btnCancel;
        private PictureBox picPreview;

        public ConvertLoadPanelForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Convert Load Panel -> One-Way";
            this.Size = new Size(320, 420);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Direction Group
            GroupBox grpDirection = new GroupBox();
            grpDirection.Text = "Spanning Direction";
            grpDirection.Location = new Point(15, 15);
            grpDirection.Size = new Size(270, 60);
            this.Controls.Add(grpDirection);

            radAuto = new RadioButton { Text = "Auto (Short)", Location = new Point(15, 25), AutoSize = true, Checked = true };
            radDir1 = new RadioButton { Text = "Dir 1", Location = new Point(110, 25), AutoSize = true };
            radDir2 = new RadioButton { Text = "Dir 2", Location = new Point(180, 25), AutoSize = true };
            grpDirection.Controls.Add(radAuto);
            grpDirection.Controls.Add(radDir1);
            grpDirection.Controls.Add(radDir2);

            // Spacing
            chkNoSpacing = new CheckBox();
            chkNoSpacing.Text = "No Spacing (Single Panel)";
            chkNoSpacing.Location = new Point(20, 90);
            chkNoSpacing.AutoSize = true;
            chkNoSpacing.CheckedChanged += ChkNoSpacing_CheckedChanged;
            this.Controls.Add(chkNoSpacing);

            lblEdgeSpacing = new Label();
            lblEdgeSpacing.Text = "Edge Spacing (mm):";
            lblEdgeSpacing.Location = new Point(20, 120);
            lblEdgeSpacing.AutoSize = true;
            this.Controls.Add(lblEdgeSpacing);

            txtEdgeSpacing = new TextBox();
            txtEdgeSpacing.Text = "300";
            txtEdgeSpacing.Location = new Point(140, 117);
            txtEdgeSpacing.Size = new Size(100, 20);
            this.Controls.Add(txtEdgeSpacing);

            // Angle Tolerance
            lblAngleTolerance = new Label();
            lblAngleTolerance.Text = "Angle Tol (deg):";
            lblAngleTolerance.Location = new Point(20, 150);
            lblAngleTolerance.AutoSize = true;
            this.Controls.Add(lblAngleTolerance);

            txtAngleTolerance = new TextBox();
            txtAngleTolerance.Text = "15.0";
            txtAngleTolerance.Location = new Point(140, 147);
            txtAngleTolerance.Size = new Size(100, 20);
            this.Controls.Add(txtAngleTolerance);

            // Preview Box
            picPreview = new PictureBox();
            picPreview.Location = new Point(15, 185);
            picPreview.Size = new Size(270, 100);
            picPreview.BorderStyle = BorderStyle.FixedSingle;
            picPreview.Paint += PicPreview_Paint;
            this.Controls.Add(picPreview);

            // Buttons
            btnOk = new Button();
            btnOk.Text = "Convert";
            btnOk.DialogResult = DialogResult.OK;
            btnOk.Location = new Point(15, 300);
            btnOk.Size = new Size(130, 30);
            btnOk.Click += BtnOk_Click;
            this.Controls.Add(btnOk);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(155, 300);
            btnCancel.Size = new Size(130, 30);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void ChkNoSpacing_CheckedChanged(object sender, EventArgs e)
        {
            txtEdgeSpacing.Enabled = !chkNoSpacing.Checked;
            picPreview.Invalidate();
        }

        private void PicPreview_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            using (Pen border = new Pen(Color.Black, 2))
            using (Pen loadLine = new Pen(Color.Blue, 2))
            using (Pen loadArrow = new Pen(Color.Red, 2))
            using (var arrowCap = new System.Drawing.Drawing2D.AdjustableArrowCap(4, 4))
            {
                loadArrow.CustomEndCap = arrowCap;
                loadArrow.CustomStartCap = arrowCap;

                Rectangle rect = new Rectangle(30, 20, 210, 60);
                g.DrawRectangle(border, rect);

                if (!chkNoSpacing.Checked)
                {
                    g.DrawLine(loadLine, 60, 20, 60, 80);
                    g.DrawLine(loadLine, 210, 20, 210, 80);
                    g.DrawLine(loadArrow, 135, 30, 135, 70);
                }
                else
                {
                    g.DrawLine(loadArrow, 135, 30, 135, 70);
                }
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            SpanningDirection = radAuto.Checked ? 0 : (radDir1.Checked ? 1 : 2);
            NoSpacing = chkNoSpacing.Checked;

            if (!NoSpacing)
            {
                double s;
                if (!OneWayPanelOptions.TryParseNumber(txtEdgeSpacing.Text, out s) || s <= 0.001)
                {
                    MessageBox.Show("Please enter a finite spacing greater than 0.001 mm.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                EdgeSpacing = s;
            }
            else
            {
                EdgeSpacing = 0;
            }

            double tol;
            if (!OneWayPanelOptions.TryParseNumber(txtAngleTolerance.Text, out tol) || tol < 0 || tol >= 45)
            {
                MessageBox.Show("Please enter an angle tolerance from 0 up to, but below, 45 degrees.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            AngleTolerance = tol;
        }
    }
}
