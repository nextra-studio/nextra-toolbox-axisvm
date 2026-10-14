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
        public bool AutoOrient { get; private set; }
        public bool IsSemiRigid { get; private set; }
        public double Stiffness { get; private set; }
        public PinTarget Target { get; private set; }
        public double AngleTolerance { get; private set; }

        private CheckBox chkReleaseTorsion;
        private CheckBox chkModelContinuity;
        private CheckBox chkAutoOrient;
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
            // ClientSize, not Size: Size includes the caption bar and border, so sizing by it left the
            // OK/Cancel row hanging below the bottom edge of the dialog.
            this.ClientSize = new Size(360, 332);
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
            chkModelContinuity.Location = new Point(20, 45);
            chkModelContinuity.AutoSize = true;
            this.Controls.Add(chkModelContinuity);

            // Auto-orient Checkbox - on by default: without it a chain drawn in mixed directions has no
            // well-defined Start and End, so "Start / End Node Only" cannot be reasoned about.
            chkAutoOrient = new CheckBox();
            chkAutoOrient.Text = "Auto-orient chain (align local x)";
            chkAutoOrient.Location = new Point(20, 70);
            chkAutoOrient.AutoSize = true;
            chkAutoOrient.Checked = true;
            this.Controls.Add(chkAutoOrient);

            // Pin Type Label & ComboBox
            Label lblPinType = new Label();
            lblPinType.Text = "Pin Type:";
            lblPinType.Location = new Point(20, 100);
            lblPinType.AutoSize = true;
            this.Controls.Add(lblPinType);

            cmbPinType = new ComboBox();
            cmbPinType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPinType.Items.AddRange(new string[] { "Complete Pin", "Semi-rigid Pin" });
            cmbPinType.SelectedIndex = 1;
            cmbPinType.Location = new Point(130, 97);
            cmbPinType.Size = new Size(200, 21);
            cmbPinType.SelectedIndexChanged += CmbPinType_SelectedIndexChanged;
            this.Controls.Add(cmbPinType);

            // Pin Target Label & ComboBox
            Label lblPinTarget = new Label();
            lblPinTarget.Text = "Apply To:";
            lblPinTarget.Location = new Point(20, 140);
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
            cmbPinTarget.Location = new Point(130, 137);
            cmbPinTarget.Size = new Size(200, 21);
            cmbPinTarget.SelectedIndexChanged += CmbPinTarget_SelectedIndexChanged;
            this.Controls.Add(cmbPinTarget);

            // Angle Tolerance Label & TextBox
            lblAngleTolerance = new Label();
            lblAngleTolerance.Text = "Angle Tol (deg):";
            lblAngleTolerance.Location = new Point(20, 180);
            lblAngleTolerance.AutoSize = true;
            this.Controls.Add(lblAngleTolerance);

            txtAngleTolerance = new TextBox();
            txtAngleTolerance.Text = "15.0";
            txtAngleTolerance.Location = new Point(160, 177);
            txtAngleTolerance.Size = new Size(120, 20);
            this.Controls.Add(txtAngleTolerance);

            // Stiffness Label & TextBox
            lblStiffness = new Label();
            lblStiffness.Text = "Stiffness (kNm/rad):";
            lblStiffness.Location = new Point(20, 220);
            lblStiffness.AutoSize = true;
            lblStiffness.Visible = false;
            this.Controls.Add(lblStiffness);

            txtStiffness = new TextBox();
            txtStiffness.Text = "1.0";
            txtStiffness.Location = new Point(160, 217);
            txtStiffness.Size = new Size(120, 20);
            txtStiffness.Visible = false;
            this.Controls.Add(txtStiffness);

            // Direction note - explains what unchecking auto-orient costs.
            Label lblDirectionNote = new Label();
            lblDirectionNote.Text = "Auto-orient re-aligns the local x of the selected beams/ribs along their chain. Unchecked, local axes are preserved; releases are still assigned.";
            lblDirectionNote.Location = new Point(20, 246);
            lblDirectionNote.Size = new Size(320, 42);
            lblDirectionNote.ForeColor = SystemColors.GrayText;
            this.Controls.Add(lblDirectionNote);

            // Buttons
            btnOk = new Button();
            btnOk.Text = "OK";
            btnOk.DialogResult = DialogResult.OK;
            btnOk.Location = new Point(180, 296);
            btnOk.Size = new Size(75, 26);
            btnOk.Click += BtnOk_Click;
            this.Controls.Add(btnOk);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(265, 296);
            btnCancel.Size = new Size(75, 26);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            // Sync the conditional rows with the initial selections: SelectedIndex is assigned before the
            // handlers are wired up, so no event fires and the stiffness row stayed hidden even though
            // "Semi-rigid Pin" is the default.
            CmbPinType_SelectedIndexChanged(cmbPinType, EventArgs.Empty);
            CmbPinTarget_SelectedIndexChanged(cmbPinTarget, EventArgs.Empty);
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

        // Accept the user's decimal separator and the dot used in the dialog defaults.
        // Thousands separators are deliberately excluded to avoid misreading 1.5 as 15.
        internal static bool TryFiniteNonNegative(string text, out double value)
        {
            const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
            bool parsed = double.TryParse(text, style, System.Globalization.CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out value);
            return parsed && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            ReleaseTorsion = chkReleaseTorsion.Checked;
            CheckModelContinuity = chkModelContinuity.Checked;
            AutoOrient = chkAutoOrient.Checked;
            IsSemiRigid = cmbPinType.SelectedIndex == 1;
            Target = (PinTarget)cmbPinTarget.SelectedIndex;

            double tol;
            if (Target == PinTarget.ChainBothEnds || Target == PinTarget.ChainStartNode || Target == PinTarget.ChainEndNode)
            {
                if (!TryFiniteNonNegative(txtAngleTolerance.Text, out tol) || tol > 90)
                {
                    MessageBox.Show("Please enter a finite angle tolerance between 0 and 90 degrees.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                if (!TryFiniteNonNegative(txtStiffness.Text, out s))
                {
                    MessageBox.Show("Please enter a finite, non-negative stiffness.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
