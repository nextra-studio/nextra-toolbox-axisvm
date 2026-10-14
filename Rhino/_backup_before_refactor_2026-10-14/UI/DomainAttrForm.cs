using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AxisBridge.Attributes;

namespace AxisBridge.UI
{
    public class DomainAttrForm : Form
    {
        public Dictionary<string, string> ResultAttributes { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private ComboBox cboFeType;
        private TextBox txtMaterial;
        private TextBox txtThickness;
        private TextBox txtLocalAxis;
        private TextBox txtEccentricity;
        private ComboBox cboSpanDir;
        private TextBox txtEdgeSpacing;
        private TextBox txtParts;

        // Uniform Area Load Dedicated Controls
        private CheckBox chkEnableAreaLoad;
        private TextBox txtAreaLc;
        private TextBox txtAreaQx;
        private TextBox txtAreaQy;
        private TextBox txtAreaQz;

        // Surface Point Load Dedicated Controls
        private CheckBox chkEnablePL;
        private TextBox txtPlLc;
        private TextBox txtPlX;
        private TextBox txtPlY;
        private TextBox txtPlZ;
        private TextBox txtPlFx;
        private TextBox txtPlFy;
        private TextBox txtPlFz;

        private GroupBox grpDomainProps;
        private GroupBox grpLoadPanelProps;
        private GroupBox grpAreaLoad;
        private GroupBox grpPointLoad;

        private bool isInitializing = true;

        public DomainAttrForm(Dictionary<string, string> initialAttrs)
        {
            InitializeComponent();
            Populate(initialAttrs);
            isInitializing = false;
        }

        private void InitializeComponent()
        {
            this.Text = "AxisBridge - Domain and Surface Attributes";
            this.ClientSize = new Size(900, 1060);
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

            // FE Type
            Label lblFeType = new Label
            {
                Text = "FE Type (Ax_FEType):",
                Location = new Point(marginX + 5, y + 3),
                Size = new Size(lblW, 24),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            cboFeType = new ComboBox
            {
                Location = new Point(marginX + inputX, y),
                Size = new Size(inputW - 5, 26),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cboFeType.Items.AddRange(new object[] { "Shell", "Plate", "Membrane", "LoadPanel", "OneWayLoadPanel" });
            cboFeType.SelectedIndex = 0;
            cboFeType.SelectedIndexChanged += CboFeType_SelectedIndexChanged;

            this.Controls.Add(lblFeType);
            this.Controls.Add(cboFeType);

            y += 45;

            // 1. Domain Properties Group
            grpDomainProps = new GroupBox
            {
                Text = "Domain Structural Properties",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 290)
            };

            int gy = 24;

            // Material
            Label lblMat = new Label { Text = "Material (Ax_Material):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtMaterial = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "C25/30" };
            Label lblMatHint = new Label
            {
                Text = "Concrete or Timber material name: e.g. C25/30, C30/37, GL24h",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpDomainProps.Controls.Add(lblMat);
            grpDomainProps.Controls.Add(txtMaterial);
            grpDomainProps.Controls.Add(lblMatHint);

            gy += 64;

            // Thickness
            Label lblThk = new Label { Text = "Thickness (Ax_Thickness):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtThickness = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "200" };
            Label lblThkHint = new Label
            {
                Text = "Domain thickness in document units (e.g. 200 for mm or 0.20 for m).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpDomainProps.Controls.Add(lblThk);
            grpDomainProps.Controls.Add(txtThickness);
            grpDomainProps.Controls.Add(lblThkHint);

            gy += 64;

            // Local Axis
            Label lblAxis = new Label { Text = "Local Axis (Ax_LocalAxis):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtLocalAxis = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "Auto" };
            Label lblAxisHint = new Label
            {
                Text = "Reference vector defining local x orientation on the surface (default: Auto for AxisVM automatic orientation, or specify e.g. 1,0,0).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpDomainProps.Controls.Add(lblAxis);
            grpDomainProps.Controls.Add(txtLocalAxis);
            grpDomainProps.Controls.Add(lblAxisHint);

            gy += 64;

            // Eccentricity
            Label lblEcc = new Label { Text = "Eccentricity (Ax_Ecc):", Location = new Point(lblX, gy + 3), Size = new Size(lblW, 20) };
            txtEccentricity = new TextBox { Location = new Point(inputX, gy), Size = new Size(inputW, 24), Text = "0,0,0" };
            Label lblEccHint = new Label
            {
                Text = "Midplane offset vector [ex, ey, ez] in document units (e.g. 0,0,-0.10).",
                Location = new Point(inputX, gy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpDomainProps.Controls.Add(lblEcc);
            grpDomainProps.Controls.Add(txtEccentricity);
            grpDomainProps.Controls.Add(lblEccHint);

            grpDomainProps.Height = gy + 60;
            this.Controls.Add(grpDomainProps);

            y += grpDomainProps.Height + 12;

            // 2. Load Panel One-Way Options Group
            grpLoadPanelProps = new GroupBox
            {
                Text = "One-Way Load Panel Configuration (Ax_SpanDirection, Ax_EdgeSpacing)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 150)
            };

            int lpy = 24;
            Label lblSpan = new Label { Text = "Span Direction:", Location = new Point(lblX, lpy + 3), Size = new Size(lblW, 20) };
            cboSpanDir = new ComboBox
            {
                Location = new Point(inputX, lpy),
                Size = new Size(inputW, 26),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cboSpanDir.Items.AddRange(new object[] { "Auto (Short Span)", "Direction 1 (Along Edge 1 / Local X)", "Direction 2 (Perpendicular to Edge 1)" });
            cboSpanDir.SelectedIndex = 0;
            grpLoadPanelProps.Controls.Add(lblSpan);
            grpLoadPanelProps.Controls.Add(cboSpanDir);

            lpy += 38;
            Label lblEdgeSp = new Label { Text = "Edge Strip Spacing (Ax_EdgeSpacing):", Location = new Point(lblX, lpy + 3), Size = new Size(lblW, 20) };
            txtEdgeSpacing = new TextBox { Location = new Point(inputX, lpy), Size = new Size(inputW, 24), Text = "0" };
            Label lblEdgeSpHint = new Label
            {
                Text = "Tributary edge-strip width in document units (0 = single full-bay panel, e.g. 500 for mm).",
                Location = new Point(inputX, lpy + 27),
                Size = new Size(inputW, hintH),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font(this.Font.FontFamily, 8.25F)
            };
            grpLoadPanelProps.Controls.Add(lblEdgeSp);
            grpLoadPanelProps.Controls.Add(txtEdgeSpacing);
            grpLoadPanelProps.Controls.Add(lblEdgeSpHint);

            grpLoadPanelProps.Height = lpy + 60;
            this.Controls.Add(grpLoadPanelProps);

            y += grpLoadPanelProps.Height + 12;

            // 3. Uniform Surface Load (Spacious Layout)
            grpAreaLoad = new GroupBox
            {
                Text = "Uniform Surface Area Load (Ax_DistLoad)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 125)
            };

            chkEnableAreaLoad = new CheckBox
            {
                Text = "Enable Uniform Surface Load",
                Location = new Point(15, 22),
                Size = new Size(350, 22),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            chkEnableAreaLoad.CheckedChanged += (s, e) => UpdateAreaLoadFieldsState();
            grpAreaLoad.Controls.Add(chkEnableAreaLoad);

            int aly = 52;
            Label lblAreaLc = new Label { Text = "Load Case:", Location = new Point(15, aly + 3), Size = new Size(80, 20) };
            txtAreaLc = new TextBox { Location = new Point(95, aly), Size = new Size(115, 24), Text = "Dead", Enabled = false };

            Label lblAreaQ = new Label { Text = "Pressure [kN/m²]:", Location = new Point(230, aly + 3), Size = new Size(140, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblQx = new Label { Text = "Qx:", Location = new Point(380, aly + 3), Size = new Size(28, 20) };
            txtAreaQx = new TextBox { Location = new Point(410, aly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQy = new Label { Text = "Qy:", Location = new Point(490, aly + 3), Size = new Size(28, 20) };
            txtAreaQy = new TextBox { Location = new Point(520, aly), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblQz = new Label { Text = "Qz:", Location = new Point(600, aly + 3), Size = new Size(28, 20) };
            txtAreaQz = new TextBox { Location = new Point(630, aly), Size = new Size(65, 24), Text = "-2.5", Enabled = false };

            grpAreaLoad.Controls.Add(lblAreaLc);
            grpAreaLoad.Controls.Add(txtAreaLc);
            grpAreaLoad.Controls.Add(lblAreaQ);
            grpAreaLoad.Controls.Add(lblQx);
            grpAreaLoad.Controls.Add(txtAreaQx);
            grpAreaLoad.Controls.Add(lblQy);
            grpAreaLoad.Controls.Add(txtAreaQy);
            grpAreaLoad.Controls.Add(lblQz);
            grpAreaLoad.Controls.Add(txtAreaQz);

            grpAreaLoad.Height = aly + 45;
            this.Controls.Add(grpAreaLoad);

            y += grpAreaLoad.Height + 12;

            // 4. Surface Point Load (Spacious Layout)
            grpPointLoad = new GroupBox
            {
                Text = "Surface Point Load (Ax_PointLoad)",
                Location = new Point(marginX, y),
                Size = new Size(grpW, 160)
            };

            chkEnablePL = new CheckBox
            {
                Text = "Enable Surface Point Load",
                Location = new Point(15, 22),
                Size = new Size(350, 22),
                Font = new Font(this.Font, FontStyle.Bold)
            };
            chkEnablePL.CheckedChanged += (s, e) => UpdatePointLoadFieldsState();
            grpPointLoad.Controls.Add(chkEnablePL);

            int sply = 52;
            Label lblPlLc = new Label { Text = "Load Case:", Location = new Point(15, sply + 3), Size = new Size(80, 20) };
            txtPlLc = new TextBox { Location = new Point(95, sply), Size = new Size(115, 24), Text = "Dead", Enabled = false };

            Label lblPtLoc = new Label { Text = "Location in doc units:", Location = new Point(230, sply + 3), Size = new Size(150, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblPtX = new Label { Text = "X:", Location = new Point(385, sply + 3), Size = new Size(22, 20) };
            txtPlX = new TextBox { Location = new Point(410, sply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblPtY = new Label { Text = "Y:", Location = new Point(490, sply + 3), Size = new Size(22, 20) };
            txtPlY = new TextBox { Location = new Point(515, sply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblPtZ = new Label { Text = "Z:", Location = new Point(595, sply + 3), Size = new Size(22, 20) };
            txtPlZ = new TextBox { Location = new Point(620, sply), Size = new Size(65, 24), Text = "0", Enabled = false };

            grpPointLoad.Controls.Add(lblPlLc);
            grpPointLoad.Controls.Add(txtPlLc);
            grpPointLoad.Controls.Add(lblPtLoc);
            grpPointLoad.Controls.Add(lblPtX);
            grpPointLoad.Controls.Add(txtPlX);
            grpPointLoad.Controls.Add(lblPtY);
            grpPointLoad.Controls.Add(txtPlY);
            grpPointLoad.Controls.Add(lblPtZ);
            grpPointLoad.Controls.Add(txtPlZ);

            sply += 34;
            Label lblPlF = new Label { Text = "Forces [kN]:", Location = new Point(15, sply + 3), Size = new Size(95, 20), Font = new Font(this.Font, FontStyle.Bold) };
            Label lblFx = new Label { Text = "Fx:", Location = new Point(120, sply + 3), Size = new Size(25, 20) };
            txtPlFx = new TextBox { Location = new Point(148, sply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFy = new Label { Text = "Fy:", Location = new Point(230, sply + 3), Size = new Size(25, 20) };
            txtPlFy = new TextBox { Location = new Point(258, sply), Size = new Size(65, 24), Text = "0", Enabled = false };
            Label lblFz = new Label { Text = "Fz:", Location = new Point(340, sply + 3), Size = new Size(25, 20) };
            txtPlFz = new TextBox { Location = new Point(368, sply), Size = new Size(65, 24), Text = "-10", Enabled = false };

            grpPointLoad.Controls.Add(lblPlF);
            grpPointLoad.Controls.Add(lblFx);
            grpPointLoad.Controls.Add(txtPlFx);
            grpPointLoad.Controls.Add(lblFy);
            grpPointLoad.Controls.Add(txtPlFy);
            grpPointLoad.Controls.Add(lblFz);
            grpPointLoad.Controls.Add(txtPlFz);

            grpPointLoad.Height = sply + 45;
            this.Controls.Add(grpPointLoad);

            y += grpPointLoad.Height + 12;

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
                Text = "Semicolon-separated part names: e.g. Floor Slabs; Level 1; Concrete Core (creates AxisVM Parts).",
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

        private void UpdateAreaLoadFieldsState()
        {
            bool on = chkEnableAreaLoad.Checked;
            txtAreaLc.Enabled = on;
            txtAreaQx.Enabled = on;
            txtAreaQy.Enabled = on;
            txtAreaQz.Enabled = on;
        }

        private void UpdatePointLoadFieldsState()
        {
            bool on = chkEnablePL.Checked;
            txtPlLc.Enabled = on;
            txtPlX.Enabled = on;
            txtPlY.Enabled = on;
            txtPlZ.Enabled = on;
            txtPlFx.Enabled = on;
            txtPlFy.Enabled = on;
            txtPlFz.Enabled = on;
        }

        private void CboFeType_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateFieldStates();

            if (!isInitializing)
            {
                string t = cboFeType.SelectedItem.ToString();
                if (t != "LoadPanel" && t != "OneWayLoadPanel")
                {
                    if (string.IsNullOrEmpty(txtMaterial.Text)) txtMaterial.Text = "C25/30";
                    if (string.IsNullOrEmpty(txtThickness.Text)) txtThickness.Text = "200";
                }
            }
        }

        private void UpdateFieldStates()
        {
            string t = cboFeType.SelectedItem != null ? cboFeType.SelectedItem.ToString() : "Shell";
            bool isLoadPanel = (t == "LoadPanel" || t == "OneWayLoadPanel");
            bool isOneWay = (t == "OneWayLoadPanel");

            txtMaterial.Enabled = !isLoadPanel;
            txtThickness.Enabled = !isLoadPanel;
            grpLoadPanelProps.Enabled = isLoadPanel;
            cboSpanDir.Enabled = isLoadPanel;
            txtEdgeSpacing.Enabled = isOneWay;
        }

        private void Populate(Dictionary<string, string> attrs)
        {
            if (attrs == null || attrs.Count == 0)
            {
                txtMaterial.Text = "C25/30";
                txtThickness.Text = "200";
                txtLocalAxis.Text = "Auto";
                txtEccentricity.Text = "0,0,0";
                return;
            }

            if (attrs.TryGetValue(AxKeys.FeType, out string fet))
            {
                for (int i = 0; i < cboFeType.Items.Count; i++)
                {
                    if (cboFeType.Items[i].ToString().Equals(fet, StringComparison.OrdinalIgnoreCase))
                    {
                        cboFeType.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (attrs.TryGetValue(AxKeys.Material, out string mat)) txtMaterial.Text = mat;
            if (attrs.TryGetValue(AxKeys.Thickness, out string thk)) txtThickness.Text = thk;
            if (attrs.TryGetValue(AxKeys.LocalAxis, out string la))
            {
                txtLocalAxis.Text = string.IsNullOrWhiteSpace(la) || la.Trim() == "-" ? "Auto" : la.Trim();
            }
            else
            {
                txtLocalAxis.Text = "Auto";
            }
            if (attrs.TryGetValue(AxKeys.Eccentricity, out string ecc)) txtEccentricity.Text = ecc;

            if (attrs.TryGetValue(AxKeys.SpanDirection, out string sd))
            {
                if (sd == "1" || sd.Equals("dir1", StringComparison.OrdinalIgnoreCase)) cboSpanDir.SelectedIndex = 1;
                else if (sd == "2" || sd.Equals("dir2", StringComparison.OrdinalIgnoreCase)) cboSpanDir.SelectedIndex = 2;
                else cboSpanDir.SelectedIndex = 0;
            }

            if (attrs.TryGetValue(AxKeys.EdgeSpacing, out string esp)) txtEdgeSpacing.Text = esp;

            // Area Load
            if (attrs.TryGetValue(AxKeys.DistLoad, out string dl) && !string.IsNullOrWhiteSpace(dl) && dl.Trim() != "-")
            {
                chkEnableAreaLoad.Checked = true;
                ParseAndPopulateAreaLoad(dl);
            }

            // Surface Point Load
            if (attrs.TryGetValue(AxKeys.PointLoad, out string pl) && !string.IsNullOrWhiteSpace(pl) && pl.Trim() != "-")
            {
                chkEnablePL.Checked = true;
                ParseAndPopulatePointLoad(pl);
            }

            if (attrs.TryGetValue(AxKeys.Parts, out string prt)) txtParts.Text = prt;
        }

        private void ParseAndPopulateAreaLoad(string raw)
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

                    if (k == "LC" || k == "LOADCASE" || k == "CASE") txtAreaLc.Text = v;
                    else if (k == "Q" || k == "Q1")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtAreaQx.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtAreaQy.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtAreaQz.Text = nums[2].Trim();
                    }
                }
            }
            catch { }
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
                    else if (k == "AT" || k == "POS")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtPlX.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtPlY.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtPlZ.Text = nums[2].Trim();
                    }
                    else if (k == "F" || k == "FORCE")
                    {
                        string[] nums = v.Split(',');
                        if (nums.Length >= 1) txtPlFx.Text = nums[0].Trim();
                        if (nums.Length >= 2) txtPlFy.Text = nums[1].Trim();
                        if (nums.Length >= 3) txtPlFz.Text = nums[2].Trim();
                    }
                }
            }
            catch { }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            string fe = cboFeType.SelectedItem.ToString();
            ResultAttributes[AxKeys.FeType] = fe;
            ResultAttributes[AxKeys.Material] = CleanValue(txtMaterial.Text);
            ResultAttributes[AxKeys.Thickness] = CleanValue(txtThickness.Text);
            ResultAttributes[AxKeys.LocalAxis] = CleanValue(txtLocalAxis.Text);
            ResultAttributes[AxKeys.Eccentricity] = CleanValue(txtEccentricity.Text);

            if (fe == "OneWayLoadPanel" || fe == "LoadPanel")
            {
                ResultAttributes[AxKeys.SpanDirection] = cboSpanDir.SelectedIndex.ToString();
                if (fe == "OneWayLoadPanel" && !string.IsNullOrWhiteSpace(txtEdgeSpacing.Text) && txtEdgeSpacing.Text.Trim() != "0")
                {
                    ResultAttributes[AxKeys.EdgeSpacing] = txtEdgeSpacing.Text.Trim();
                }
                else
                {
                    ResultAttributes[AxKeys.EdgeSpacing] = "-";
                }
            }
            else
            {
                ResultAttributes[AxKeys.SpanDirection] = "-";
                ResultAttributes[AxKeys.EdgeSpacing] = "-";
            }

            // Assemble Area Load
            if (chkEnableAreaLoad.Checked)
            {
                string lc = string.IsNullOrWhiteSpace(txtAreaLc.Text) ? "Dead" : txtAreaLc.Text.Trim();
                string qx = CleanNum(txtAreaQx.Text);
                string qy = CleanNum(txtAreaQy.Text);
                string qz = CleanNum(txtAreaQz.Text);
                ResultAttributes[AxKeys.DistLoad] = $"LC={lc}; q={qx},{qy},{qz}";
            }
            else
            {
                ResultAttributes[AxKeys.DistLoad] = "-";
            }

            // Assemble Surface Point Load
            if (chkEnablePL.Checked)
            {
                string lc = string.IsNullOrWhiteSpace(txtPlLc.Text) ? "Dead" : txtPlLc.Text.Trim();
                string x = CleanNum(txtPlX.Text);
                string y = CleanNum(txtPlY.Text);
                string z = CleanNum(txtPlZ.Text);
                string fx = CleanNum(txtPlFx.Text);
                string fy = CleanNum(txtPlFy.Text);
                string fz = CleanNum(txtPlFz.Text);
                ResultAttributes[AxKeys.PointLoad] = $"LC={lc}; at={x},{y},{z}; F={fx},{fy},{fz},0,0,0";
            }
            else
            {
                ResultAttributes[AxKeys.PointLoad] = "-";
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
            if (string.IsNullOrWhiteSpace(val) || val.Trim() == "0,0,0") return "-";
            return val.Trim();
        }
    }
}
