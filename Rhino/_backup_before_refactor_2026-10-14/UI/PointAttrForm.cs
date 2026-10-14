using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Attributes;

namespace AxisBridge.UI
{
    public class PointAttrForm : Form
    {
        public Dictionary<string, string> ResultAttributes { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private ComboBox cboSupportPreset;
        private TextBox txtCustomSupport;
        private Label lblCustom;
        private Label lblSupHint;
        private TextBox txtParts;

        // Dedicated Nodal Load Controls
        private CheckBox chkEnableNL;
        private TextBox txtNlLc;
        private TextBox txtNlFx;
        private TextBox txtNlFy;
        private TextBox txtNlFz;
        private TextBox txtNlMx;
        private TextBox txtNlMy;
        private TextBox txtNlMz;

        private Button btnOk;
        private Button btnCancel;

        public PointAttrForm(Dictionary<string, string> initialAttrs)
        {
            InitializeComponent();
            Populate(initialAttrs);
        }

        private void InitializeComponent()
        {
            this.Text = "AxisBridge - Point and Nodal Attributes";
            this.ClientSize = new Size(900, 680);
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

            // 1. Support Group
            GroupBox grpSupport = new GroupBox
            {
                Text = "Nodal Support (Ax_Support)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 200)
            };

            int gy = 24;
            Label lblPreset = new Label { Text = "Support Preset:", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            cboSupportPreset = new ComboBox
            {
                Location = new Point(inputX, gy),
                Size = new Size(inputW, 26),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cboSupportPreset.Items.AddRange(new object[] { "None (Free in all directions)", "Pinned (Rigid Tx, Ty, Tz translation; free rotation)", "Fixed (Rigid in all 6 DOFs)", "Roller-Z (Rigid Tz only; slides horizontally)", "Custom (Specify 6 individual stiffness values)" });
            cboSupportPreset.SelectedIndex = 0;
            cboSupportPreset.SelectedIndexChanged += CboSupportPreset_SelectedIndexChanged;

            grpSupport.Controls.Add(lblPreset);
            grpSupport.Controls.Add(cboSupportPreset);

            gy += 38;
            lblCustom = new Label { Text = "Stiffness [kx, ky, kz, kxx, kyy, kzz]:", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtCustomSupport = new TextBox
            {
                Location = new Point(inputX, gy),
                Size = new Size(inputW, 24),
                Text = "1e10,1e10,1e10,0,0,0",
                Enabled = false
            };
            lblSupHint = new Label
            {
                Text = "6 stiffnesses in kN/m (translations) and kNm/rad (rotations). 1e10 = rigid, 0 = free, or spring stiffness in kNm/rad (e.g. 50000).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };

            grpSupport.Controls.Add(lblCustom);
            grpSupport.Controls.Add(txtCustomSupport);
            grpSupport.Controls.Add(lblSupHint);

            grpSupport.Height = gy + 60;
            this.Controls.Add(grpSupport);

            y += grpSupport.Height + 12;

            // 2. Nodal Load Group (Spacious Layout)
            GroupBox grpLoad = new GroupBox
            {
                Text = "Nodal Load (Ax_NodalLoad)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 160)
            };

            chkEnableNL = new CheckBox
            {
                Text = "Enable Nodal Load on Point",
                Location = new Point(15, 22),
                Size = new Size(350, 22),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            chkEnableNL.CheckedChanged += (s, e) => UpdateNodalLoadFieldsState();
            grpLoad.Controls.Add(chkEnableNL);

            int nly = 52;
            Label lblNlLc = new Label { Text = "Load Case:", Location = new Point(15, nly + 3), Size = new Size(80, 20) };
            txtNlLc = new TextBox { Location = new Point(95, nly), Size = new Size(115, 24), Text = "Dead", Enabled = false };
            grpLoad.Controls.Add(lblNlLc);
            grpLoad.Controls.Add(txtNlLc);

            nly += 34;
            Label lblNlF = new Label { Text = "Forces [kN]:", Location = new Point(15, nly + 3), Size = new Size(95, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblFx = new Label { Text = "Fx:", Location = new Point(120, nly + 3), Size = new Size(25, 20) };
            txtNlFx = new TextBox { Location = new Point(148, nly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFy = new Label { Text = "Fy:", Location = new Point(230, nly + 3), Size = new Size(25, 20) };
            txtNlFy = new TextBox { Location = new Point(258, nly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFz = new Label { Text = "Fz:", Location = new Point(340, nly + 3), Size = new Size(25, 20) };
            txtNlFz = new TextBox { Location = new Point(368, nly), Size = new Size(65, 24), Text = "-10", Enabled = false };

            grpLoad.Controls.Add(lblNlF);
            grpLoad.Controls.Add(lblFx);
            grpLoad.Controls.Add(txtNlFx);
            grpLoad.Controls.Add(lblFy);
            grpLoad.Controls.Add(txtNlFy);
            grpLoad.Controls.Add(lblFz);
            grpLoad.Controls.Add(txtNlFz);

            nly += 34;
            Label lblNlM = new Label { Text = "Moments [kNm]:", Location = new Point(15, nly + 3), Size = new Size(130, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblMx = new Label { Text = "Mx:", Location = new Point(155, nly + 3), Size = new Size(28, 20) };
            txtNlMx = new TextBox { Location = new Point(185, nly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblMy = new Label { Text = "My:", Location = new Point(265, nly + 3), Size = new Size(28, 20) };
            txtNlMy = new TextBox { Location = new Point(295, nly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblMz = new Label { Text = "Mz:", Location = new Point(375, nly + 3), Size = new Size(28, 20) };
            txtNlMz = new TextBox { Location = new Point(405, nly), Size = new Size(65, 24), Text = "0", Enabled = false };

            grpLoad.Controls.Add(lblNlM);
            grpLoad.Controls.Add(lblMx);
            grpLoad.Controls.Add(txtNlMx);
            grpLoad.Controls.Add(lblMy);
            grpLoad.Controls.Add(txtNlMy);
            grpLoad.Controls.Add(lblMz);
            grpLoad.Controls.Add(txtNlMz);

            grpLoad.Height = nly + 45;
            this.Controls.Add(grpLoad);

            y += grpLoad.Height + 12;

            // 3. Parts Group
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
                Text = "Semicolon-separated part names: e.g. Column Bases; Foundations; Substructure (creates AxisVM Parts).",
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
            btnOk = new Button
            {
                Text = "Apply",
                DialogResult = DialogResult.OK,
                Location = new Point(marginX + grpW - 235, y),
                Size = new Size(110, 34),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            btnOk.Click += BtnOk_Click;

            btnCancel = new Button
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
        }

        private void UpdateNodalLoadFieldsState()
        {
            bool on = chkEnableNL.Checked;
            txtNlLc.Enabled = on;
            txtNlFx.Enabled = on;
            txtNlFy.Enabled = on;
            txtNlFz.Enabled = on;
            txtNlMx.Enabled = on;
            txtNlMy.Enabled = on;
            txtNlMz.Enabled = on;
        }

        private void CboSupportPreset_SelectedIndexChanged(object sender, EventArgs e)
        {
            txtCustomSupport.Enabled = (cboSupportPreset.SelectedIndex == 4);
            if (cboSupportPreset.SelectedIndex == 1) txtCustomSupport.Text = "pinned";
            else if (cboSupportPreset.SelectedIndex == 2) txtCustomSupport.Text = "fixed";
            else if (cboSupportPreset.SelectedIndex == 3) txtCustomSupport.Text = "roller-z";
            else if (cboSupportPreset.SelectedIndex == 0) txtCustomSupport.Text = "";
            else if (cboSupportPreset.SelectedIndex == 4 && (string.IsNullOrEmpty(txtCustomSupport.Text) || txtCustomSupport.Text == "pinned" || txtCustomSupport.Text == "fixed"))
            {
                txtCustomSupport.Text = "1e10,1e10,1e10,0,0,0";
            }
        }

        private void Populate(Dictionary<string, string> attrs)
        {
            if (attrs == null || attrs.Count == 0) return;

            if (attrs.TryGetValue(AxKeys.Support, out string sup))
            {
                sup = sup.Trim();
                if (sup.Equals("pinned", StringComparison.OrdinalIgnoreCase) || sup.Equals("pin", StringComparison.OrdinalIgnoreCase))
                {
                    cboSupportPreset.SelectedIndex = 1;
                }
                else if (sup.Equals("fixed", StringComparison.OrdinalIgnoreCase) || sup.Equals("fix", StringComparison.OrdinalIgnoreCase))
                {
                    cboSupportPreset.SelectedIndex = 2;
                }
                else if (sup.Equals("roller-z", StringComparison.OrdinalIgnoreCase) || sup.Equals("rollerz", StringComparison.OrdinalIgnoreCase))
                {
                    cboSupportPreset.SelectedIndex = 3;
                }
                else
                {
                    cboSupportPreset.SelectedIndex = 4;
                    txtCustomSupport.Text = sup;
                }
            }

            if (attrs.TryGetValue(AxKeys.NodalLoad, out string nl) && !string.IsNullOrWhiteSpace(nl) && nl.Trim() != "-")
            {
                chkEnableNL.Checked = true;
                ParseAndPopulateNodalLoad(nl);
            }

            if (attrs.TryGetValue(AxKeys.Parts, out string prt))
            {
                txtParts.Text = prt;
            }
        }

        private void ParseAndPopulateNodalLoad(string raw)
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

                    if (k == "LC" || k == "LOADCASE" || k == "CASE") txtNlLc.Text = v;
                    else if (k == "F" || k == "FORCE")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtNlFx.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtNlFy.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtNlFz.Text = nums[2].Trim();
                        if (nums.Length >= 4) txtNlMx.Text = nums[3].Trim();
                        if (nums.Length >= 5) txtNlMy.Text = nums[4].Trim();
                        if (nums.Length >= 6) txtNlMz.Text = nums[5].Trim();
                    }
                }
            }
            catch { }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            string supVal = txtCustomSupport.Text.Trim();
            ResultAttributes[AxKeys.Support] = CleanValue(supVal);

            // Assemble Nodal Load
            if (chkEnableNL.Checked)
            {
                string lc = string.IsNullOrWhiteSpace(txtNlLc.Text) ? "Dead" : txtNlLc.Text.Trim();
                string fx = CleanNum(txtNlFx.Text);
                string fy = CleanNum(txtNlFy.Text);
                string fz = CleanNum(txtNlFz.Text);
                string mx = CleanNum(txtNlMx.Text);
                string my = CleanNum(txtNlMy.Text);
                string mz = CleanNum(txtNlMz.Text);
                ResultAttributes[AxKeys.NodalLoad] = $"LC={lc}; F={fx},{fy},{fz},{mx},{my},{mz}";
            }
            else
            {
                ResultAttributes[AxKeys.NodalLoad] = "-";
            }

            ResultAttributes[AxKeys.Parts] = CleanValue(txtParts.Text);
        }

        private static string CleanNum(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "0";
            return val.Trim();
        }

        private static string CleanValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "-";
            return val.Trim();
        }
    }
}
