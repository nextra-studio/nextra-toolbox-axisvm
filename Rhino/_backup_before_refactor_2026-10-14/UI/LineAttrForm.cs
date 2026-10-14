using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AxisBridge.Attributes;

namespace AxisBridge.UI
{
    public class LineAttrForm : Form
    {
        public Dictionary<string, string> ResultAttributes { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private ComboBox cboType;
        private TextBox txtMaterial;
        private TextBox txtSection;
        private ComboBox cboRelease;
        private TextBox txtStartRelease;
        private TextBox txtEndRelease;
        private Label lblStartRel;
        private Label lblEndRel;
        private TextBox txtLocalAxis;
        private TextBox txtEccentricity;
        private TextBox txtStiffness;
        private TextBox txtHingeDomains;
        private TextBox txtCoupledDof;
        private TextBox txtParts;

        // Point Load Controls
        private CheckBox chkEnablePL;
        private TextBox txtPlLc;
        private ComboBox cboPlMode;
        private TextBox txtPlPos;
        private TextBox txtPlFx;
        private TextBox txtPlFy;
        private TextBox txtPlFz;
        private TextBox txtPlMx;
        private TextBox txtPlMy;
        private TextBox txtPlMz;

        // Distributed Load Controls
        private CheckBox chkEnableDL;
        private TextBox txtDlLc;
        private ComboBox cboDlMode;
        private TextBox txtDlFrom;
        private TextBox txtDlTo;
        private TextBox txtDlQx1;
        private TextBox txtDlQy1;
        private TextBox txtDlQz1;
        private TextBox txtDlQx2;
        private TextBox txtDlQy2;
        private TextBox txtDlQz2;
        private CheckBox chkDlUniform;

        private GroupBox grpStructural;
        private GroupBox grpSpringHinge;
        private GroupBox grpPL;
        private GroupBox grpDL;

        private bool isInitializing = true;

        public LineAttrForm(Dictionary<string, string> initialAttrs)
        {
            InitializeComponent();
            Populate(initialAttrs);
            isInitializing = false;
        }

        private void InitializeComponent()
        {
            this.Text = "AxisBridge - Line and Member Attributes";
            this.ClientSize = new Size(900, 1080);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.AutoScroll = true;

            int marginX = 20;
            int lblX = 15;
            int lblW = 230;
            int inputX = 255;
            int inputW = 580;
            int grpW = 850;
            int hintH = 34;

            int y = 18;

            // Element Type
            Label lblType = new Label
            {
                Text = "Element Type (Ax_Type):",
                Location = new Point(marginX + 5, y + 3),
                Size = new Size(lblW, 24),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            cboType = new ComboBox
            {
                Location = new Point(marginX + inputX, y),
                Size = new Size(inputW - 5, 26),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cboType.Items.AddRange(new object[] { "Beam", "Rib", "Truss", "Spring", "RigidLink", "NodeLink", "EdgeHinge", "Simple" });
            cboType.SelectedIndex = 0;
            cboType.SelectedIndexChanged += CboType_SelectedIndexChanged;

            this.Controls.Add(lblType);
            this.Controls.Add(cboType);

            y += 45;

            // 1. Structural Properties Group
            grpStructural = new GroupBox
            {
                Text = "Structural Profile and Releases",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 400)
            };

            int gy = 24;

            // Material
            Label lblMat = new Label { Text = "Material (Ax_Material):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtMaterial = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "S355" };
            Label lblMatHint = new Label
            {
                Text = "Catalog material name: e.g. S235, S355, C25/30, C30/37, GL24h",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpStructural.Controls.Add(lblMat);
            grpStructural.Controls.Add(txtMaterial);
            grpStructural.Controls.Add(lblMatHint);

            gy += 64;

            // Section
            Label lblSec = new Label { Text = "Section (Ax_Section):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtSection = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "IPE300" };
            Label lblSecHint = new Label
            {
                Text = "Catalog profile (IPE300, HEA200, UPN200) or Dimensions (500x500, 300x500, 100x300 in mm).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpStructural.Controls.Add(lblSec);
            grpStructural.Controls.Add(txtSection);
            grpStructural.Controls.Add(lblSecHint);

            gy += 64;

            // Releases Dropdown
            Label lblRel = new Label { Text = "End Releases (Ax_Release):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            cboRelease = new ComboBox
            {
                Location = new Point(inputX, gy),
                Size = new Size(inputW, 26),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cboRelease.Items.AddRange(new object[] { "Fix (Rigid at both ends)", "Pin (Hinged at both ends)", "Semi-pin (1 Nm/rad at both ends)", "Custom (Specify Start and End DOFs separately)" });
            cboRelease.SelectedIndex = 0;
            cboRelease.SelectedIndexChanged += CboRelease_SelectedIndexChanged;
            grpStructural.Controls.Add(lblRel);
            grpStructural.Controls.Add(cboRelease);

            gy += 32;

            // Start Release
            lblStartRel = new Label { Text = "  • Start DOFs [x, y, z, xx, yy, zz]:", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtStartRelease = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "1e10,1e10,1e10,1e10,0,0", Enabled = false };
            grpStructural.Controls.Add(lblStartRel);
            grpStructural.Controls.Add(txtStartRelease);

            gy += 30;

            // End Release
            lblEndRel = new Label { Text = "  • End DOFs [x, y, z, xx, yy, zz]:", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtEndRelease = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "1e10,1e10,1e10,1e10,0,0", Enabled = false };
            Label lblRelHint = new Label
            {
                Text = "0 = Free/Hinge, 1e10 = Rigid/Fixed, or spring stiffness in kNm/rad (e.g. 50000).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpStructural.Controls.Add(lblEndRel);
            grpStructural.Controls.Add(txtEndRelease);
            grpStructural.Controls.Add(lblRelHint);

            gy += 64;

            // Local Axis
            Label lblAxis = new Label { Text = "Local Vector (Ax_LocalAxis):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtLocalAxis = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "Auto" };
            Label lblAxisHint = new Label
            {
                Text = "Reference vector defining local z/y orientation (default: Auto for AxisVM automatic orientation, or specify e.g. 0,0,1).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpStructural.Controls.Add(lblAxis);
            grpStructural.Controls.Add(txtLocalAxis);
            grpStructural.Controls.Add(lblAxisHint);

            gy += 64;

            // Eccentricity
            Label lblEcc = new Label { Text = "Eccentricity (Ax_Eccentricity):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtEccentricity = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "0,0,0" };
            Label lblEccHint = new Label
            {
                Text = "Centerline offset vector [ex, ey, ez] in document units (e.g. 0,0,-0.15).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpStructural.Controls.Add(lblEcc);
            grpStructural.Controls.Add(txtEccentricity);
            grpStructural.Controls.Add(lblEccHint);

            grpStructural.Height = gy + 60;
            this.Controls.Add(grpStructural);

            y += grpStructural.Height + 12;

            // 2. Spring, Link and Edge Hinge Group
            grpSpringHinge = new GroupBox
            {
                Text = "Spring, Link and Edge Hinge Properties",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 220)
            };

            gy = 24;
            Label lblStiff = new Label { Text = "Stiffness (Ax_Stiffness):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtStiffness = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "1e6,1e6,1e6,0,0,0" };
            Label lblStiffHint = new Label
            {
                Text = "6 stiffness values: [kx, ky, kz, kxx, kyy, kzz] in kN/m for translations, kNm/rad for rotations.",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpSpringHinge.Controls.Add(lblStiff);
            grpSpringHinge.Controls.Add(txtStiffness);
            grpSpringHinge.Controls.Add(lblStiffHint);

            gy += 64;
            Label lblHinge = new Label { Text = "Hinge Domains (Ax_HingeDomains):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtHingeDomains = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24) };
            Label lblHingeHint = new Label
            {
                Text = "Attached domain GUIDs (semicolon-separated) for edge hinges.",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpSpringHinge.Controls.Add(lblHinge);
            grpSpringHinge.Controls.Add(txtHingeDomains);
            grpSpringHinge.Controls.Add(lblHingeHint);

            gy += 64;
            Label lblCoupled = new Label { Text = "Coupled DOFs (Ax_CoupledDOF):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtCoupledDof = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "1,1,1,1,1,1" };
            Label lblCoupledHint = new Label
            {
                Text = "6 values: [x, y, z, xx, yy, zz] where 1 = coupled / rigid link, 0 = free.",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpSpringHinge.Controls.Add(lblCoupled);
            grpSpringHinge.Controls.Add(txtCoupledDof);
            grpSpringHinge.Controls.Add(lblCoupledHint);

            grpSpringHinge.Height = gy + 60;
            this.Controls.Add(grpSpringHinge);

            y += grpSpringHinge.Height + 12;

            // 3. Member Point Load (Spacious Layout)
            grpPL = new GroupBox
            {
                Text = "Member Point Load (Ax_PointLoad)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 160)
            };

            chkEnablePL = new CheckBox
            {
                Text = "Enable Point Load on Member",
                Location = new Point(15, 22),
                Size = new Size(350, 22),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            chkEnablePL.CheckedChanged += (s, e) => UpdatePointLoadFieldsState();
            grpPL.Controls.Add(chkEnablePL);

            int ply = 52;
            Label lblPlLc = new Label { Text = "Load Case:", Location = new Point(15, ply + 3), Size = new Size(80, 20) };
            txtPlLc = new TextBox { Location = new Point(95, ply), Size = new Size(115, 24), Text = "Dead", Enabled = false };
            Label lblPlMode = new Label { Text = "Position Mode:", Location = new Point(230, ply + 3), Size = new Size(105, 20) };
            cboPlMode = new ComboBox
            {
                Location = new Point(340, ply),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false
            };
            cboPlMode.Items.AddRange(new object[] { "Ratio (0.0 to 1.0)", "Length in Doc Units" });
            cboPlMode.SelectedIndex = 0;

            Label lblPlPos = new Label { Text = "Value:", Location = new Point(510, ply + 3), Size = new Size(50, 20) };
            txtPlPos = new TextBox { Location = new Point(565, ply), Size = new Size(75, 24), Text = "0.5", Enabled = false };

            grpPL.Controls.Add(lblPlLc);
            grpPL.Controls.Add(txtPlLc);
            grpPL.Controls.Add(lblPlMode);
            grpPL.Controls.Add(cboPlMode);
            grpPL.Controls.Add(lblPlPos);
            grpPL.Controls.Add(txtPlPos);

            ply += 34;
            Label lblPlF = new Label { Text = "Forces [kN]:", Location = new Point(15, ply + 3), Size = new Size(95, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblFx = new Label { Text = "Fx:", Location = new Point(120, ply + 3), Size = new Size(25, 20) };
            txtPlFx = new TextBox { Location = new Point(148, ply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFy = new Label { Text = "Fy:", Location = new Point(230, ply + 3), Size = new Size(25, 20) };
            txtPlFy = new TextBox { Location = new Point(258, ply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFz = new Label { Text = "Fz:", Location = new Point(340, ply + 3), Size = new Size(25, 20) };
            txtPlFz = new TextBox { Location = new Point(368, ply), Size = new Size(65, 24), Text = "-10", Enabled = false };

            grpPL.Controls.Add(lblPlF);
            grpPL.Controls.Add(lblFx);
            grpPL.Controls.Add(txtPlFx);
            grpPL.Controls.Add(lblFy);
            grpPL.Controls.Add(txtPlFy);
            grpPL.Controls.Add(lblFz);
            grpPL.Controls.Add(txtPlFz);

            ply += 34;
            Label lblPlM = new Label { Text = "Moments [kNm]:", Location = new Point(15, ply + 3), Size = new Size(130, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblMx = new Label { Text = "Mx:", Location = new Point(155, ply + 3), Size = new Size(28, 20) };
            txtPlMx = new TextBox { Location = new Point(185, ply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblMy = new Label { Text = "My:", Location = new Point(265, ply + 3), Size = new Size(28, 20) };
            txtPlMy = new TextBox { Location = new Point(295, ply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblMz = new Label { Text = "Mz:", Location = new Point(375, ply + 3), Size = new Size(28, 20) };
            txtPlMz = new TextBox { Location = new Point(405, ply), Size = new Size(65, 24), Text = "0", Enabled = false };

            grpPL.Controls.Add(lblPlM);
            grpPL.Controls.Add(lblMx);
            grpPL.Controls.Add(txtPlMx);
            grpPL.Controls.Add(lblMy);
            grpPL.Controls.Add(txtPlMy);
            grpPL.Controls.Add(lblMz);
            grpPL.Controls.Add(txtPlMz);

            grpPL.Height = ply + 45;
            this.Controls.Add(grpPL);

            y += grpPL.Height + 12;

            // 4. Member Distributed Load (Spacious Layout)
            grpDL = new GroupBox
            {
                Text = "Member Distributed Load (Ax_DistLoad)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 160)
            };

            chkEnableDL = new CheckBox
            {
                Text = "Enable Distributed Load on Member",
                Location = new Point(15, 22),
                Size = new Size(350, 22),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            chkEnableDL.CheckedChanged += (s, e) => UpdateDistLoadFieldsState();
            grpDL.Controls.Add(chkEnableDL);

            int dly = 52;
            Label lblDlLc = new Label { Text = "Load Case:", Location = new Point(15, dly + 3), Size = new Size(80, 20) };
            txtDlLc = new TextBox { Location = new Point(95, dly), Size = new Size(115, 24), Text = "Dead", Enabled = false };
            Label lblDlMode = new Label { Text = "Position Mode:", Location = new Point(230, dly + 3), Size = new Size(105, 20) };
            cboDlMode = new ComboBox
            {
                Location = new Point(340, dly),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false
            };
            cboDlMode.Items.AddRange(new object[] { "Ratio (0.0 to 1.0)", "Length in Doc Units" });
            cboDlMode.SelectedIndex = 0;

            Label lblDlFrom = new Label { Text = "From:", Location = new Point(505, dly + 3), Size = new Size(45, 20) };
            txtDlFrom = new TextBox { Location = new Point(555, dly), Size = new Size(60, 24), Text = "0.0", Enabled = false };
            Label lblDlTo = new Label { Text = "To:", Location = new Point(630, dly + 3), Size = new Size(30, 20) };
            txtDlTo = new TextBox { Location = new Point(665, dly), Size = new Size(60, 24), Text = "1.0", Enabled = false };

            grpDL.Controls.Add(lblDlLc);
            grpDL.Controls.Add(txtDlLc);
            grpDL.Controls.Add(lblDlMode);
            grpDL.Controls.Add(cboDlMode);
            grpDL.Controls.Add(lblDlFrom);
            grpDL.Controls.Add(txtDlFrom);
            grpDL.Controls.Add(lblDlTo);
            grpDL.Controls.Add(txtDlTo);

            dly += 34;
            Label lblDlQ1 = new Label { Text = "Start Intensity q1 [kN/m]:", Location = new Point(15, dly + 3), Size = new Size(180, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblQx1 = new Label { Text = "qx:", Location = new Point(205, dly + 3), Size = new Size(25, 20) };
            txtDlQx1 = new TextBox { Location = new Point(232, dly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQy1 = new Label { Text = "qy:", Location = new Point(310, dly + 3), Size = new Size(25, 20) };
            txtDlQy1 = new TextBox { Location = new Point(337, dly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQz1 = new Label { Text = "qz:", Location = new Point(415, dly + 3), Size = new Size(25, 20) };
            txtDlQz1 = new TextBox { Location = new Point(442, dly), Size = new Size(65, 24), Text = "-5", Enabled = false };

            chkDlUniform = new CheckBox
            {
                Text = "Uniform load (q2 = q1)",
                Location = new Point(530, dly + 2),
                Size = new Size(240, 22),
                Checked = true,
                Enabled = false
            };
            chkDlUniform.CheckedChanged += (s, e) => UpdateDistLoadUniformState();

            grpDL.Controls.Add(lblDlQ1);
            grpDL.Controls.Add(lblQx1);
            grpDL.Controls.Add(txtDlQx1);
            grpDL.Controls.Add(lblQy1);
            grpDL.Controls.Add(txtDlQy1);
            grpDL.Controls.Add(lblQz1);
            grpDL.Controls.Add(txtDlQz1);
            grpDL.Controls.Add(chkDlUniform);

            dly += 34;
            Label lblDlQ2 = new Label { Text = "End Intensity q2 [kN/m]:", Location = new Point(15, dly + 3), Size = new Size(180, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblQx2 = new Label { Text = "qx:", Location = new Point(205, dly + 3), Size = new Size(25, 20) };
            txtDlQx2 = new TextBox { Location = new Point(232, dly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQy2 = new Label { Text = "qy:", Location = new Point(310, dly + 3), Size = new Size(25, 20) };
            txtDlQy2 = new TextBox { Location = new Point(337, dly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQz2 = new Label { Text = "qz:", Location = new Point(415, dly + 3), Size = new Size(25, 20) };
            txtDlQz2 = new TextBox { Location = new Point(442, dly), Size = new Size(65, 24), Text = "-5", Enabled = false };

            grpDL.Controls.Add(lblDlQ2);
            grpDL.Controls.Add(lblQx2);
            grpDL.Controls.Add(txtDlQx2);
            grpDL.Controls.Add(lblQy2);
            grpDL.Controls.Add(txtDlQy2);
            grpDL.Controls.Add(lblQz2);
            grpDL.Controls.Add(txtDlQz2);

            grpDL.Height = dly + 45;
            this.Controls.Add(grpDL);

            y += grpDL.Height + 12;

            // 5. Parts Group
            GroupBox grpParts = new GroupBox
            {
                Text = "AxisVM Parts and Layer Assignment",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 90)
            };
            Label lblParts = new Label { Text = "Custom Parts (Ax_Parts):", Location = new Point(lblX, 24), Size = new Size(lblW, 20) };
            txtParts = new TextBox { Location = new Point(inputX, 21), Size = new Size(inputW, 24) };
            Label lblPartsHint = new Label
            {
                Text = "Semicolon-separated part names: e.g. Beams; Level 2; Steel Framing (creates AxisVM Parts).",
                Location = new Point(inputX, 48),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpParts.Controls.Add(lblParts);
            grpParts.Controls.Add(txtParts);
            grpParts.Controls.Add(lblPartsHint);
            this.Controls.Add(grpParts);

            y += grpParts.Height + 20;

            // Buttons
            Button btnOk = new Button
            {
                Text = "Apply",
                DialogResult = DialogResult.OK,
                Location = new Point(marginX + grpW - 235, y),
                Size = new Size(110, 34),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            btnOk.Click += BtnOk_Click;

            Button btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(marginX + grpW - 115, y),
                Size = new Size(110, 34)
            };

            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            this.ClientSize = new Size(900, y + 55);

            UpdateFieldStates();
        }

        private void UpdatePointLoadFieldsState()
        {
            bool on = chkEnablePL.Checked;
            txtPlLc.Enabled = on;
            cboPlMode.Enabled = on;
            txtPlPos.Enabled = on;
            txtPlFx.Enabled = on;
            txtPlFy.Enabled = on;
            txtPlFz.Enabled = on;
            txtPlMx.Enabled = on;
            txtPlMy.Enabled = on;
            txtPlMz.Enabled = on;
        }

        private void UpdateDistLoadFieldsState()
        {
            bool on = chkEnableDL.Checked;
            txtDlLc.Enabled = on;
            cboDlMode.Enabled = on;
            txtDlFrom.Enabled = on;
            txtDlTo.Enabled = on;
            chkDlUniform.Enabled = on;
            txtDlQx1.Enabled = on;
            txtDlQy1.Enabled = on;
            txtDlQz1.Enabled = on;
            txtDlQx2.Enabled = on && !chkDlUniform.Checked;
            txtDlQy2.Enabled = on && !chkDlUniform.Checked;
            txtDlQz2.Enabled = on && !chkDlUniform.Checked;
        }

        private void UpdateDistLoadUniformState()
        {
            bool uniform = chkDlUniform.Checked;
            txtDlQx2.Enabled = chkEnableDL.Checked && !uniform;
            txtDlQy2.Enabled = chkEnableDL.Checked && !uniform;
            txtDlQz2.Enabled = chkEnableDL.Checked && !uniform;
            if (uniform)
            {
                txtDlQx2.Text = txtDlQx1.Text;
                txtDlQy2.Text = txtDlQy1.Text;
                txtDlQz2.Text = txtDlQz1.Text;
            }
        }

        private void CboRelease_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool isCustom = (cboRelease.SelectedIndex == 3);
            txtStartRelease.Enabled = isCustom;
            txtEndRelease.Enabled = isCustom;

            if (cboRelease.SelectedIndex == 0) // Fix
            {
                txtStartRelease.Text = "1e10,1e10,1e10,1e10,1e10,1e10";
                txtEndRelease.Text = "1e10,1e10,1e10,1e10,1e10,1e10";
            }
            else if (cboRelease.SelectedIndex == 1) // Pin
            {
                txtStartRelease.Text = "1e10,1e10,1e10,1e10,0,0";
                txtEndRelease.Text = "1e10,1e10,1e10,1e10,0,0";
            }
            else if (cboRelease.SelectedIndex == 2) // Semi-pin
            {
                txtStartRelease.Text = "1e10,1e10,1e10,1e10,1.0,1.0";
                txtEndRelease.Text = "1e10,1e10,1e10,1e10,1.0,1.0";
            }
            else if (isCustom)
            {
                if (string.IsNullOrEmpty(txtStartRelease.Text)) txtStartRelease.Text = "1e10,1e10,1e10,1e10,0,0";
                if (string.IsNullOrEmpty(txtEndRelease.Text)) txtEndRelease.Text = "1e10,1e10,1e10,1e10,0,0";
            }
        }

        private void CboType_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateFieldStates();

            if (!isInitializing)
            {
                ApplyTypeDefaults(cboType.SelectedItem.ToString());
            }
        }

        private void UpdateFieldStates()
        {
            string t = cboType.SelectedItem != null ? cboType.SelectedItem.ToString() : "Beam";
            bool isBeamOrRib = (t == "Beam" || t == "Rib" || t == "Truss");

            txtMaterial.Enabled = isBeamOrRib;
            txtSection.Enabled = isBeamOrRib;
            cboRelease.Enabled = isBeamOrRib;
            txtStartRelease.Enabled = isBeamOrRib && (cboRelease.SelectedIndex == 3);
            txtEndRelease.Enabled = isBeamOrRib && (cboRelease.SelectedIndex == 3);
            txtLocalAxis.Enabled = isBeamOrRib;
            txtEccentricity.Enabled = isBeamOrRib;

            txtStiffness.Enabled = (t == "Spring" || t == "EdgeHinge");
            txtHingeDomains.Enabled = (t == "EdgeHinge");
            txtCoupledDof.Enabled = (t == "RigidLink" || t == "NodeLink");
        }

        private void ApplyTypeDefaults(string t)
        {
            switch (t)
            {
                case "Beam":
                    if (string.IsNullOrEmpty(txtMaterial.Text) || txtMaterial.Text == "C25/30") txtMaterial.Text = "S355";
                    if (string.IsNullOrEmpty(txtSection.Text) || txtSection.Text == "300x500" || txtSection.Text == "RO88.9x5") txtSection.Text = "IPE300";
                    cboRelease.SelectedIndex = 0; // Fix
                    txtLocalAxis.Text = "Auto";
                    txtEccentricity.Text = "0,0,0";
                    break;
                case "Rib":
                    if (string.IsNullOrEmpty(txtMaterial.Text) || txtMaterial.Text == "S355") txtMaterial.Text = "C25/30";
                    if (string.IsNullOrEmpty(txtSection.Text) || txtSection.Text == "IPE300" || txtSection.Text == "RO88.9x5") txtSection.Text = "300x500";
                    cboRelease.SelectedIndex = 0; // Fix
                    txtLocalAxis.Text = "Auto";
                    txtEccentricity.Text = "0,0,0";
                    break;
                case "Truss":
                    if (string.IsNullOrEmpty(txtMaterial.Text) || txtMaterial.Text == "C25/30") txtMaterial.Text = "S355";
                    if (string.IsNullOrEmpty(txtSection.Text) || txtSection.Text == "IPE300" || txtSection.Text == "300x500") txtSection.Text = "RO88.9x5";
                    cboRelease.SelectedIndex = 1; // Pin
                    txtLocalAxis.Text = "Auto";
                    txtEccentricity.Text = "0,0,0";
                    break;
                case "Spring":
                    if (string.IsNullOrEmpty(txtStiffness.Text)) txtStiffness.Text = "1e6,1e6,1e6,0,0,0";
                    break;
                case "EdgeHinge":
                    if (string.IsNullOrEmpty(txtStiffness.Text)) txtStiffness.Text = "1e6,1e6,1e6,0,0,0";
                    break;
                case "RigidLink":
                    if (string.IsNullOrEmpty(txtCoupledDof.Text)) txtCoupledDof.Text = "1,1,1,1,1,1";
                    break;
                case "NodeLink":
                    if (string.IsNullOrEmpty(txtCoupledDof.Text)) txtCoupledDof.Text = "1,1,1,0,0,0";
                    break;
            }
        }

        private void Populate(Dictionary<string, string> attrs)
        {
            if (attrs == null || attrs.Count == 0)
            {
                ApplyTypeDefaults("Beam");
                return;
            }

            if (attrs.TryGetValue(AxKeys.Type, out string t))
            {
                for (int i = 0; i < cboType.Items.Count; i++)
                {
                    if (cboType.Items[i].ToString().Equals(t, StringComparison.OrdinalIgnoreCase))
                    {
                        cboType.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (attrs.TryGetValue(AxKeys.Material, out string mat)) txtMaterial.Text = mat;
            if (attrs.TryGetValue(AxKeys.Section, out string sec)) txtSection.Text = sec;

            if (attrs.TryGetValue(AxKeys.Release, out string rel))
            {
                rel = rel.Trim();
                if (rel.Equals("pin", StringComparison.OrdinalIgnoreCase) || rel.Equals("pinned", StringComparison.OrdinalIgnoreCase))
                {
                    cboRelease.SelectedIndex = 1;
                }
                else if (rel.Equals("semi-pin", StringComparison.OrdinalIgnoreCase) || rel.Equals("semipin", StringComparison.OrdinalIgnoreCase))
                {
                    cboRelease.SelectedIndex = 2;
                }
                else if (rel.Equals("fix", StringComparison.OrdinalIgnoreCase) || rel.Equals("fixed", StringComparison.OrdinalIgnoreCase))
                {
                    cboRelease.SelectedIndex = 0;
                }
                else
                {
                    cboRelease.SelectedIndex = 3;
                    string[] parts = rel.Split(';');
                    foreach (string part in parts)
                    {
                        string p = part.Trim();
                        if (p.StartsWith("start=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
                        {
                            txtStartRelease.Text = p.Substring(p.IndexOf('=') + 1).Trim();
                        }
                        else if (p.StartsWith("end=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("e=", StringComparison.OrdinalIgnoreCase))
                        {
                            txtEndRelease.Text = p.Substring(p.IndexOf('=') + 1).Trim();
                        }
                        else if (p.Length > 0 && !p.Contains("="))
                        {
                            txtStartRelease.Text = p;
                            txtEndRelease.Text = p;
                        }
                    }
                }
            }

            if (attrs.TryGetValue(AxKeys.LocalAxis, out string la))
            {
                txtLocalAxis.Text = string.IsNullOrWhiteSpace(la) || la.Trim() == "-" ? "Auto" : la.Trim();
            }
            else
            {
                txtLocalAxis.Text = "Auto";
            }
            if (attrs.TryGetValue(AxKeys.Eccentricity, out string ecc)) txtEccentricity.Text = ecc;

            // Populate Point Load
            if (attrs.TryGetValue(AxKeys.PointLoad, out string pl) && !string.IsNullOrWhiteSpace(pl) && pl.Trim() != "-")
            {
                chkEnablePL.Checked = true;
                ParseAndPopulatePointLoad(pl);
            }

            // Populate Distributed Load
            if (attrs.TryGetValue(AxKeys.DistLoad, out string dl) && !string.IsNullOrWhiteSpace(dl) && dl.Trim() != "-")
            {
                chkEnableDL.Checked = true;
                ParseAndPopulateDistLoad(dl);
            }

            if (attrs.TryGetValue(AxKeys.Stiffness, out string stf)) txtStiffness.Text = stf;
            if (attrs.TryGetValue(AxKeys.HingeDomains, out string hd)) txtHingeDomains.Text = hd;
            if (attrs.TryGetValue(AxKeys.CoupledDof, out string cd)) txtCoupledDof.Text = cd;
            if (attrs.TryGetValue(AxKeys.Parts, out string prt)) txtParts.Text = prt;
        }

        private void ParseAndPopulatePointLoad(string raw)
        {
            try
            {
                string[] parts = raw.Split(';');
                foreach (string p in parts)
                {
                    int eq = p.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = p.Substring(0, eq).Trim().ToUpperInvariant();
                    string v = p.Substring(eq + 1).Trim();

                    if (k == "LC" || k == "LOADCASE" || k == "CASE") txtPlLc.Text = v;
                    else if (k == "AT" || k == "POS" || k == "POSITION")
                    {
                        if (v.StartsWith("ratio:", StringComparison.OrdinalIgnoreCase))
                        {
                            cboPlMode.SelectedIndex = 0;
                            txtPlPos.Text = v.Substring(6).Trim();
                        }
                        else if (v.StartsWith("len:", StringComparison.OrdinalIgnoreCase))
                        {
                            cboPlMode.SelectedIndex = 1;
                            txtPlPos.Text = v.Substring(4).Trim();
                        }
                        else
                        {
                            cboPlMode.SelectedIndex = 0;
                            txtPlPos.Text = v;
                        }
                    }
                    else if (k == "F" || k == "FORCE")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtPlFx.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtPlFy.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtPlFz.Text = nums[2].Trim();
                        if (nums.Length >= 4) txtPlMx.Text = nums[3].Trim();
                        if (nums.Length >= 5) txtPlMy.Text = nums[4].Trim();
                        if (nums.Length >= 6) txtPlMz.Text = nums[5].Trim();
                    }
                }
            }
            catch { }
        }

        private void ParseAndPopulateDistLoad(string raw)
        {
            try
            {
                string[] parts = raw.Split(';');
                foreach (string p in parts)
                {
                    int eq = p.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = p.Substring(0, eq).Trim().ToUpperInvariant();
                    string v = p.Substring(eq + 1).Trim();

                    if (k == "LC" || k == "LOADCASE" || k == "CASE") txtDlLc.Text = v;
                    else if (k == "FROM")
                    {
                        if (v.StartsWith("ratio:", StringComparison.OrdinalIgnoreCase))
                        {
                            cboDlMode.SelectedIndex = 0;
                            txtDlFrom.Text = v.Substring(6).Trim();
                        }
                        else if (v.StartsWith("len:", StringComparison.OrdinalIgnoreCase))
                        {
                            cboDlMode.SelectedIndex = 1;
                            txtDlFrom.Text = v.Substring(4).Trim();
                        }
                        else
                        {
                            cboDlMode.SelectedIndex = 0;
                            txtDlFrom.Text = v;
                        }
                    }
                    else if (k == "TO")
                    {
                        if (v.StartsWith("ratio:", StringComparison.OrdinalIgnoreCase))
                        {
                            txtDlTo.Text = v.Substring(6).Trim();
                        }
                        else if (v.StartsWith("len:", StringComparison.OrdinalIgnoreCase))
                        {
                            txtDlTo.Text = v.Substring(4).Trim();
                        }
                        else
                        {
                            txtDlTo.Text = v;
                        }
                    }
                    else if (k == "Q1" || k == "Q")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtDlQx1.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtDlQy1.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtDlQz1.Text = nums[2].Trim();
                    }
                    else if (k == "Q2")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtDlQx2.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtDlQy2.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtDlQz2.Text = nums[2].Trim();
                        chkDlUniform.Checked = false;
                    }
                }
            }
            catch { }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            ResultAttributes[AxKeys.Type] = cboType.SelectedItem.ToString();
            ResultAttributes[AxKeys.Material] = CleanValue(txtMaterial.Text);
            ResultAttributes[AxKeys.Section] = CleanValue(txtSection.Text);

            if (cboRelease.SelectedIndex == 0) ResultAttributes[AxKeys.Release] = "fix";
            else if (cboRelease.SelectedIndex == 1) ResultAttributes[AxKeys.Release] = "pin";
            else if (cboRelease.SelectedIndex == 2) ResultAttributes[AxKeys.Release] = "semi-pin";
            else if (cboRelease.SelectedIndex == 3)
            {
                string s = CleanValue(txtStartRelease.Text);
                string end = CleanValue(txtEndRelease.Text);
                if (s == "-" && end == "-") ResultAttributes[AxKeys.Release] = "-";
                else ResultAttributes[AxKeys.Release] = $"start={s}; end={end}";
            }

            ResultAttributes[AxKeys.LocalAxis] = CleanValue(txtLocalAxis.Text);
            ResultAttributes[AxKeys.Eccentricity] = CleanValue(txtEccentricity.Text);

            // Assemble Point Load
            if (chkEnablePL.Checked)
            {
                string lc = string.IsNullOrWhiteSpace(txtPlLc.Text) ? "Dead" : txtPlLc.Text.Trim();
                string pos = string.IsNullOrWhiteSpace(txtPlPos.Text) ? "0.5" : txtPlPos.Text.Trim();
                string prefix = cboPlMode.SelectedIndex == 1 ? "len:" : "ratio:";
                string fx = CleanNum(txtPlFx.Text);
                string fy = CleanNum(txtPlFy.Text);
                string fz = CleanNum(txtPlFz.Text);
                string mx = CleanNum(txtPlMx.Text);
                string my = CleanNum(txtPlMy.Text);
                string mz = CleanNum(txtPlMz.Text);
                ResultAttributes[AxKeys.PointLoad] = $"LC={lc}; at={prefix}{pos}; F={fx},{fy},{fz},{mx},{my},{mz}";
            }
            else
            {
                ResultAttributes[AxKeys.PointLoad] = "-";
            }

            // Assemble Distributed Load
            if (chkEnableDL.Checked)
            {
                string lc = string.IsNullOrWhiteSpace(txtDlLc.Text) ? "Dead" : txtDlLc.Text.Trim();
                string from = string.IsNullOrWhiteSpace(txtDlFrom.Text) ? "0.0" : txtDlFrom.Text.Trim();
                string to = string.IsNullOrWhiteSpace(txtDlTo.Text) ? "1.0" : txtDlTo.Text.Trim();
                string prefix = cboDlMode.SelectedIndex == 1 ? "len:" : "ratio:";
                string qx1 = CleanNum(txtDlQx1.Text);
                string qy1 = CleanNum(txtDlQy1.Text);
                string qz1 = CleanNum(txtDlQz1.Text);

                if (chkDlUniform.Checked)
                {
                    ResultAttributes[AxKeys.DistLoad] = $"LC={lc}; from={prefix}{from}; to={prefix}{to}; q1={qx1},{qy1},{qz1},0,0,0; q2={qx1},{qy1},{qz1},0,0,0";
                }
                else
                {
                    string qx2 = CleanNum(txtDlQx2.Text);
                    string qy2 = CleanNum(txtDlQy2.Text);
                    string qz2 = CleanNum(txtDlQz2.Text);
                    ResultAttributes[AxKeys.DistLoad] = $"LC={lc}; from={prefix}{from}; to={prefix}{to}; q1={qx1},{qy1},{qz1},0,0,0; q2={qx2},{qy2},{qz2},0,0,0";
                }
            }
            else
            {
                ResultAttributes[AxKeys.DistLoad] = "-";
            }

            ResultAttributes[AxKeys.Stiffness] = CleanValue(txtStiffness.Text);
            ResultAttributes[AxKeys.HingeDomains] = CleanValue(txtHingeDomains.Text);
            ResultAttributes[AxKeys.CoupledDof] = CleanValue(txtCoupledDof.Text);
            ResultAttributes[AxKeys.Parts] = CleanValue(txtParts.Text);
        }

        private static string CleanNum(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "0";
            return val.Trim();
        }

        private static string CleanValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val) || val.Trim() == "0,0,0") return "-";
            return val.Trim();
        }
    }
}
