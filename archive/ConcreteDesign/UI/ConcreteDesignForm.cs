using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public class ConcreteDesignForm : Form
    {
        public double TargetReinforcementPct { get; private set; }
        public double SupportRegionPct { get; private set; }
        public double MidspanRegionPct { get; private set; }
        public double HoggingRegionPct { get; private set; }
        public double ForceAngle { get; private set; }
        public double AngleTolerance { get; private set; }

        private TextBox txtTargetReinf;
        private TextBox txtSupportPct;
        private TextBox txtMidspanPct;
        private TextBox txtHoggingPct;
        private TextBox txtForceAngle;
        private TextBox txtAngleTolerance;
        private Button btnRun;
        private Button btnCancel;

        public ConcreteDesignForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Concrete Design Optimizer";
            this.Size = new Size(350, 340);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            int y = 20;

            // Target Reinf
            this.Controls.Add(new Label { Text = "Target Reinforcement (%):", Location = new Point(20, y), AutoSize = true });
            txtTargetReinf = new TextBox { Text = "1.5", Location = new Point(200, y - 3), Size = new Size(100, 20) };
            this.Controls.Add(txtTargetReinf);
            y += 35;

            // Regions
            GroupBox grpRegions = new GroupBox { Text = "Zoning Lengths (%)", Location = new Point(15, y), Size = new Size(300, 120) };
            this.Controls.Add(grpRegions);

            grpRegions.Controls.Add(new Label { Text = "Support Region:", Location = new Point(15, 25), AutoSize = true });
            txtSupportPct = new TextBox { Text = "15", Location = new Point(185, 22), Size = new Size(100, 20) };
            grpRegions.Controls.Add(txtSupportPct);

            grpRegions.Controls.Add(new Label { Text = "Midspan Region:", Location = new Point(15, 55), AutoSize = true });
            txtMidspanPct = new TextBox { Text = "70", Location = new Point(185, 52), Size = new Size(100, 20) };
            grpRegions.Controls.Add(txtMidspanPct);

            grpRegions.Controls.Add(new Label { Text = "Continuous Hogging:", Location = new Point(15, 85), AutoSize = true });
            txtHoggingPct = new TextBox { Text = "25", Location = new Point(185, 82), Size = new Size(100, 20) };
            grpRegions.Controls.Add(txtHoggingPct);
            
            y += 130;

            // Angles
            GroupBox grpAngles = new GroupBox { Text = "Load Angle Logic", Location = new Point(15, y), Size = new Size(300, 85) };
            this.Controls.Add(grpAngles);

            grpAngles.Controls.Add(new Label { Text = "Force Angle (deg):", Location = new Point(15, 25), AutoSize = true });
            txtForceAngle = new TextBox { Text = "90", Location = new Point(185, 22), Size = new Size(100, 20) };
            grpAngles.Controls.Add(txtForceAngle);

            grpAngles.Controls.Add(new Label { Text = "Angle Tolerance (deg):", Location = new Point(15, 55), AutoSize = true });
            txtAngleTolerance = new TextBox { Text = "10", Location = new Point(185, 52), Size = new Size(100, 20) };
            grpAngles.Controls.Add(txtAngleTolerance);

            y += 100;

            // Buttons
            btnRun = new Button { Text = "Run Iteration", DialogResult = DialogResult.OK, Location = new Point(40, y), Size = new Size(120, 30) };
            btnRun.Click += BtnRun_Click;
            this.Controls.Add(btnRun);

            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(170, y), Size = new Size(120, 30) };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnRun;
            this.CancelButton = btnCancel;
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            double temp;
            if (!double.TryParse(txtTargetReinf.Text, out temp)) return; TargetReinforcementPct = temp;
            if (!double.TryParse(txtSupportPct.Text, out temp)) return; SupportRegionPct = temp;
            if (!double.TryParse(txtMidspanPct.Text, out temp)) return; MidspanRegionPct = temp;
            if (!double.TryParse(txtHoggingPct.Text, out temp)) return; HoggingRegionPct = temp;
            if (!double.TryParse(txtForceAngle.Text, out temp)) return; ForceAngle = temp;
            if (!double.TryParse(txtAngleTolerance.Text, out temp)) return; AngleTolerance = temp;
        }
    }
}
