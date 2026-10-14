using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.UI
{
    public class SettingsForm : Form
    {
        public IAxisVMModel Model { get; set; }
        
        // Panel
        public ComboBox cmbPanelMaterial;
        public NumericUpDown numPanelThickness;
        
        // Studs
        public NumericUpDown numStudSpacing;
        public NumericUpDown numStudOffset;
        public NumericUpDown numSlipModulus;
        public NumericUpDown numFastenerSpacing;
        public ComboBox cmbStudMaterial;
        public ComboBox cmbStudSection;

        // Top Beam
        public ComboBox cmbTopBeamMaterial;
        public ComboBox cmbTopBeamSection;

        // Bottom Beam
        public ComboBox cmbBottomBeamMaterial;
        public ComboBox cmbBottomBeamSection;

        private Button btnOk;
        private Button btnCancel;

        public SettingsForm(IAxisVMModel model, List<string> materials, List<string> sections)
        {
            this.Model = model;
            InitializeComponent();
            
            PopulateCombos(materials, sections);
            SelectDefaultMaterialsAndSections();
        }

        public void PopulateCombos(List<string> materials, List<string> sections)
        {
            PopulateCombo(cmbPanelMaterial, materials);
            PopulateCombo(cmbStudMaterial, materials);
            PopulateCombo(cmbTopBeamMaterial, materials);
            PopulateCombo(cmbBottomBeamMaterial, materials);
            
            PopulateCombo(cmbStudSection, sections);
            PopulateCombo(cmbTopBeamSection, sections);
            PopulateCombo(cmbBottomBeamSection, sections);
        }

        private void PopulateCombo(ComboBox cmb, List<string> items)
        {
            cmb.Items.Clear();
            foreach (var item in items) cmb.Items.Add(item);
            if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
        }
        
        public void SelectDefaultMaterialsAndSections()
        {
            TrySelect(cmbPanelMaterial, "OSB/3");
            TrySelect(cmbStudMaterial, "C24");
            TrySelect(cmbTopBeamMaterial, "C24");
            TrySelect(cmbBottomBeamMaterial, "C24");
            
            TrySelect(cmbStudSection, "71x146");
            TrySelect(cmbTopBeamSection, "146x71");
            TrySelect(cmbBottomBeamSection, "146x71");
        }

        private void TrySelect(ComboBox cmb, string name)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i].ToString().Contains(name))
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
        }

        private void InitializeComponent()
        {
            this.Text = "AxisVM Studs Wall Generator";
            this.Size = new Size(460, 780);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

        // --- Catalog Group ---
        GroupBox gbCatalog = new GroupBox() { Text = "Library", Location = new Point(15, 15), Size = new Size(410, 60) };
        Label lblCatalog = new Label() { Text = "Catalog items generated automatically.", Location = new Point(15, 25), AutoSize = true };
        gbCatalog.Controls.Add(lblCatalog);

            // --- Panel Group ---
            GroupBox gbPanel = new GroupBox() { Text = "1. Panel (Domain)", Location = new Point(15, 85), Size = new Size(410, 90) };
            
            gbPanel.Controls.Add(new Label() { Text = "Material:", Location = new Point(15, 25), AutoSize = true });
            cmbPanelMaterial = new ComboBox() { Location = new Point(190, 22), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbPanel.Controls.Add(cmbPanelMaterial);

            gbPanel.Controls.Add(new Label() { Text = "Thickness (mm):", Location = new Point(15, 55), AutoSize = true });
            numPanelThickness = new NumericUpDown() { Location = new Point(190, 53), Width = 200, DecimalPlaces = 1, Increment = 1.0M, Minimum = 1.0M, Maximum = 1000.0M, Value = 20.0M };
            gbPanel.Controls.Add(numPanelThickness);

            // --- Studs Group ---
            GroupBox gbStuds = new GroupBox() { Text = "2. Vertical Studs & Connection", Location = new Point(15, 185), Size = new Size(410, 210) };
            
            gbStuds.Controls.Add(new Label() { Text = "Spacing (mm):", Location = new Point(15, 25), AutoSize = true });
            numStudSpacing = new NumericUpDown() { Location = new Point(190, 22), Width = 200, DecimalPlaces = 1, Increment = 50.0M, Minimum = 10.0M, Maximum = 5000.0M, Value = 600.0M };
            gbStuds.Controls.Add(numStudSpacing);

            gbStuds.Controls.Add(new Label() { Text = "Material:", Location = new Point(15, 55), AutoSize = true });
            cmbStudMaterial = new ComboBox() { Location = new Point(190, 52), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbStuds.Controls.Add(cmbStudMaterial);

            gbStuds.Controls.Add(new Label() { Text = "Cross-Section:", Location = new Point(15, 85), AutoSize = true });
            cmbStudSection = new ComboBox() { Location = new Point(190, 82), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbStuds.Controls.Add(cmbStudSection);

            gbStuds.Controls.Add(new Label() { Text = "Stud Offset (mm):", Location = new Point(15, 115), AutoSize = true });
            numStudOffset = new NumericUpDown() { Location = new Point(190, 112), Width = 200, DecimalPlaces = 1, Increment = 5.0M, Minimum = -5000.0M, Maximum = 5000.0M, Value = 50.0M };
            gbStuds.Controls.Add(numStudOffset);

            gbStuds.Controls.Add(new Label() { Text = "Slip Modulus (N/mm):", Location = new Point(15, 145), AutoSize = true });
            numSlipModulus = new NumericUpDown() { Location = new Point(190, 142), Width = 200, DecimalPlaces = 1, Increment = 100M, Minimum = 0.0M, Maximum = 100000.0M, Value = 800.0M };
            gbStuds.Controls.Add(numSlipModulus);

            gbStuds.Controls.Add(new Label() { Text = "Fastener Spacing (mm):", Location = new Point(15, 175), AutoSize = true });
            numFastenerSpacing = new NumericUpDown() { Location = new Point(190, 172), Width = 200, DecimalPlaces = 1, Increment = 10M, Minimum = 1.0M, Maximum = 1000.0M, Value = 150.0M };
            gbStuds.Controls.Add(numFastenerSpacing);

            // --- Top Beam Group ---
            GroupBox gbTopBeam = new GroupBox() { Text = "3. Top Beam", Location = new Point(15, 405), Size = new Size(410, 90) };
            
            gbTopBeam.Controls.Add(new Label() { Text = "Material:", Location = new Point(15, 25), AutoSize = true });
            cmbTopBeamMaterial = new ComboBox() { Location = new Point(190, 22), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbTopBeam.Controls.Add(cmbTopBeamMaterial);

            gbTopBeam.Controls.Add(new Label() { Text = "Cross-Section:", Location = new Point(15, 55), AutoSize = true });
            cmbTopBeamSection = new ComboBox() { Location = new Point(190, 52), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbTopBeam.Controls.Add(cmbTopBeamSection);

            // --- Bottom Beam Group ---
            GroupBox gbBottomBeam = new GroupBox() { Text = "4. Bottom Beam", Location = new Point(15, 505), Size = new Size(410, 90) };
            
            gbBottomBeam.Controls.Add(new Label() { Text = "Material:", Location = new Point(15, 25), AutoSize = true });
            cmbBottomBeamMaterial = new ComboBox() { Location = new Point(190, 22), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbBottomBeam.Controls.Add(cmbBottomBeamMaterial);

            gbBottomBeam.Controls.Add(new Label() { Text = "Cross-Section:", Location = new Point(15, 55), AutoSize = true });
            cmbBottomBeamSection = new ComboBox() { Location = new Point(190, 52), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            gbBottomBeam.Controls.Add(cmbBottomBeamSection);

            // --- Buttons ---
            btnOk = new Button() { Text = "Generate", Location = new Point(260, 615), Width = 80, DialogResult = DialogResult.OK, BackColor = Color.LightGreen, Font = new Font(this.Font, FontStyle.Bold) };
            btnCancel = new Button() { Text = "Cancel", Location = new Point(345, 615), Width = 80, DialogResult = DialogResult.Cancel };

            this.Controls.Add(gbCatalog);
            this.Controls.Add(gbPanel);
            this.Controls.Add(gbStuds);
            this.Controls.Add(gbTopBeam);
            this.Controls.Add(gbBottomBeam);
            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);
            
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

    }
}
