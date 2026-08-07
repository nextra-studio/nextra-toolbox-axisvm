using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public enum PinTarget
    {
        ChainBothEnds,
        ChainStartNode,
        ChainEndNode
    }

    public class EndPinAssignForm : Form
    {
        public bool ReleaseTorsion { get; private set; }
        public bool CheckModelContinuity { get; private set; }
        public bool IsSemiRigid { get; private set; }
        public double Stiffness { get; private set; }
        public PinTarget Target { get; private set; }
        public double AngleTolerance { get; private set; }

        private CheckBox chkReleaseTorsion;
        private CheckBox chkModelContinuity;
        private ComboBox cmbPinType;
        private ComboBox cmbPinTarget;
        private TextBox txtAngleTolerance;
        private Label lblAngleTolerance;
        private TextBox txtStiffness;
        private Label lblStiffness;
        private Button btnOk;
        private Button btnCancel;

        public EndPinAssignForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "End Pin Assign Settings";
            this.Size = new Size(380, 320);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Release Torsion Checkbox
            chkReleaseTorsion = new CheckBox();
            chkReleaseTorsion.Text = "Release Torsion (xx)";
            chkReleaseTorsion.Location = new Point(20, 20);
            chkReleaseTorsion.AutoSize = true;
            this.Controls.Add(chkReleaseTorsion);

            // Check Model Continuity Checkbox
            chkModelContinuity = new CheckBox();
            chkModelContinuity.Text = "Check continuity against unselected lines";
            chkModelContinuity.Location = new Point(20, 40);
            chkModelContinuity.AutoSize = true;
            this.Controls.Add(chkModelContinuity);

            // Pin Type Label & ComboBox
            Label lblPinType = new Label();
            lblPinType.Text = "Pin Type:";
            lblPinType.Location = new Point(20, 70);
            lblPinType.AutoSize = true;
            this.Controls.Add(lblPinType);

            cmbPinType = new ComboBox();
            cmbPinType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPinType.Items.AddRange(new string[] { "Complete Pin", "Semi-rigid Pin" });
            cmbPinType.SelectedIndex = 0;
            cmbPinType.Location = new Point(130, 67);
            cmbPinType.Size = new Size(200, 21);
            cmbPinType.SelectedIndexChanged += CmbPinType_SelectedIndexChanged;
            this.Controls.Add(cmbPinType);

            // Pin Target Label & ComboBox
            Label lblPinTarget = new Label();
            lblPinTarget.Text = "Apply To:";
            lblPinTarget.Location = new Point(20, 110);
            lblPinTarget.AutoSize = true;
            this.Controls.Add(lblPinTarget);

            cmbPinTarget = new ComboBox();
            cmbPinTarget.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPinTarget.Items.AddRange(new string[] { 
                "Chain: Both Ends", 
                "Chain: Start Node Only", 
                "Chain: End Node Only"
            });
            cmbPinTarget.SelectedIndex = 0;
            cmbPinTarget.Location = new Point(130, 107);
            cmbPinTarget.Size = new Size(200, 21);
            cmbPinTarget.SelectedIndexChanged += CmbPinTarget_SelectedIndexChanged;
            this.Controls.Add(cmbPinTarget);

            // Angle Tolerance Label & TextBox
            lblAngleTolerance = new Label();
            lblAngleTolerance.Text = "Angle Tol (deg):";
            lblAngleTolerance.Location = new Point(20, 150);
            lblAngleTolerance.AutoSize = true;
            this.Controls.Add(lblAngleTolerance);

            txtAngleTolerance = new TextBox();
            txtAngleTolerance.Text = "15.0";
            txtAngleTolerance.Location = new Point(160, 147);
            txtAngleTolerance.Size = new Size(120, 20);
            this.Controls.Add(txtAngleTolerance);

            // Stiffness Label & TextBox
            lblStiffness = new Label();
            lblStiffness.Text = "Stiffness (kNm/rad):";
            lblStiffness.Location = new Point(20, 190);
            lblStiffness.AutoSize = true;
            lblStiffness.Visible = false;
            this.Controls.Add(lblStiffness);

            txtStiffness = new TextBox();
            txtStiffness.Text = "1.0";
            txtStiffness.Location = new Point(160, 187);
            txtStiffness.Size = new Size(120, 20);
            txtStiffness.Visible = false;
            this.Controls.Add(txtStiffness);

            // Buttons
            btnOk = new Button();
            btnOk.Text = "OK";
            btnOk.DialogResult = DialogResult.OK;
            btnOk.Location = new Point(140, 240);
            btnOk.Click += BtnOk_Click;
            this.Controls.Add(btnOk);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(225, 240);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void CmbPinType_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool isSemiRigid = cmbPinType.SelectedIndex == 1;
            lblStiffness.Visible = isSemiRigid;
            txtStiffness.Visible = isSemiRigid;
        }

        private void CmbPinTarget_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool isChainTarget = cmbPinTarget.SelectedIndex <= 2;
            lblAngleTolerance.Visible = isChainTarget;
            txtAngleTolerance.Visible = isChainTarget;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            ReleaseTorsion = chkReleaseTorsion.Checked;
            CheckModelContinuity = chkModelContinuity.Checked;
            IsSemiRigid = cmbPinType.SelectedIndex == 1;
            Target = (PinTarget)cmbPinTarget.SelectedIndex;

            double tol;
            if (Target == PinTarget.ChainBothEnds || Target == PinTarget.ChainStartNode || Target == PinTarget.ChainEndNode)
            {
                if (!double.TryParse(txtAngleTolerance.Text, out tol) || tol < 0)
                {
                    MessageBox.Show("Please enter a valid positive number for angle tolerance.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                AngleTolerance = tol;
            }
            else
            {
                AngleTolerance = 0;
            }

            if (IsSemiRigid)
            {
                double s;
                if (!double.TryParse(txtStiffness.Text, out s) || s < 0)
                {
                    MessageBox.Show("Please enter a valid positive number for stiffness.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                Stiffness = s;
            }
            else
            {
                Stiffness = 0.0;
            }
        }
    }
}
