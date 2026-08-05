using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public class AdvanceViewControlForm : Form
    {
        public enum ActionType
        {
            ShowOnlySelected,
            HideSelected,
            ShowAll,
            FilterByAttribute
        }

        public ActionType SelectedAction { get; private set; }
        public string SelectedMaterial { get; private set; }
        public double MinWidth { get; private set; }

        public bool ExcludeDomains { get; private set; }
        public bool ExcludeLoadPanels { get; private set; }
        public bool ExcludeSurfaces { get; private set; }

        private RadioButton rbShowSelected;
        private RadioButton rbHideSelected;
        private RadioButton rbShowAll;
        private RadioButton rbFilter;
        
        private ComboBox cmbMaterials;
        private TextBox txtMinWidth;
        
        private CheckBox chkExcludeDomains;
        private CheckBox chkExcludeLoadPanels;
        private CheckBox chkExcludeSurfaces;

        private Button btnOk;
        private Button btnCancel;

        public AdvanceViewControlForm(List<string> materials)
        {
            InitializeComponent(materials);
        }

        private void InitializeComponent(List<string> materials)
        {
            this.Text = "Advance View Control";
            this.Size = new Size(380, 420);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Radio Buttons
            rbShowSelected = new RadioButton { Text = "Show Only Selected (Isolate)", Location = new Point(20, 20), AutoSize = true, Checked = true };
            rbHideSelected = new RadioButton { Text = "Hide Selected", Location = new Point(20, 50), AutoSize = true };
            rbShowAll = new RadioButton { Text = "Show All", Location = new Point(20, 80), AutoSize = true };
            rbFilter = new RadioButton { Text = "Attribute Based Filter (Lines only)", Location = new Point(20, 110), AutoSize = true };
            
            rbFilter.CheckedChanged += RbFilter_CheckedChanged;

            this.Controls.Add(rbShowSelected);
            this.Controls.Add(rbHideSelected);
            this.Controls.Add(rbShowAll);
            this.Controls.Add(rbFilter);

            // Filter Panel
            Label lblMat = new Label { Text = "Material:", Location = new Point(40, 140), AutoSize = true };
            cmbMaterials = new ComboBox { Location = new Point(130, 137), Size = new Size(150, 21), DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
            cmbMaterials.Items.Add("Any");
            foreach (var m in materials) cmbMaterials.Items.Add(m);
            cmbMaterials.SelectedIndex = 0;
            
            this.Controls.Add(lblMat);
            this.Controls.Add(cmbMaterials);

            Label lblWidth = new Label { Text = "Min Width (mm):", Location = new Point(40, 170), AutoSize = true };
            txtMinWidth = new TextBox { Text = "0", Location = new Point(130, 167), Size = new Size(150, 20), Enabled = false };
            
            this.Controls.Add(lblWidth);
            this.Controls.Add(txtMinWidth);

            // Strict Mode Group
            GroupBox gbStrict = new GroupBox { Text = "Strict Mode (Forcefully Unselect Surfaces)", Location = new Point(20, 210), Size = new Size(320, 110) };
            
            chkExcludeDomains = new CheckBox { Text = "Exclude Domains", Location = new Point(20, 25), AutoSize = true, Checked = true };
            chkExcludeLoadPanels = new CheckBox { Text = "Exclude Load Panels", Location = new Point(20, 50), AutoSize = true, Checked = true };
            chkExcludeSurfaces = new CheckBox { Text = "Exclude Surfaces", Location = new Point(20, 75), AutoSize = true, Checked = true };

            gbStrict.Controls.Add(chkExcludeDomains);
            gbStrict.Controls.Add(chkExcludeLoadPanels);
            gbStrict.Controls.Add(chkExcludeSurfaces);

            this.Controls.Add(gbStrict);

            // Buttons
            btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(180, 340) };
            btnOk.Click += BtnOk_Click;
            this.Controls.Add(btnOk);

            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(265, 340) };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void RbFilter_CheckedChanged(object sender, EventArgs e)
        {
            cmbMaterials.Enabled = rbFilter.Checked;
            txtMinWidth.Enabled = rbFilter.Checked;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (rbShowSelected.Checked) SelectedAction = ActionType.ShowOnlySelected;
            else if (rbHideSelected.Checked) SelectedAction = ActionType.HideSelected;
            else if (rbShowAll.Checked) SelectedAction = ActionType.ShowAll;
            else SelectedAction = ActionType.FilterByAttribute;

            if (SelectedAction == ActionType.FilterByAttribute)
            {
                SelectedMaterial = cmbMaterials.SelectedItem.ToString();
                double w;
                if (!double.TryParse(txtMinWidth.Text, out w) || w < 0)
                {
                    MessageBox.Show("Please enter a valid positive number for Min Width.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                MinWidth = w; // Now handled as mm
            }

            ExcludeDomains = chkExcludeDomains.Checked;
            ExcludeLoadPanels = chkExcludeLoadPanels.Checked;
            ExcludeSurfaces = chkExcludeSurfaces.Checked;
        }
    }
}
