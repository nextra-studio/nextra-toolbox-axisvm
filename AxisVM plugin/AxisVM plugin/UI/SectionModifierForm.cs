using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public class SectionModifierForm : Form
    {
        public bool ModifyWidth { get; private set; }
        public bool ModifyDepth { get; private set; }
        public double NewWidth { get; private set; }
        public double NewDepth { get; private set; }

        private CheckBox chkWidth;
        private TextBox txtWidth;
        private CheckBox chkDepth;
        private TextBox txtDepth;
        
        private Button btnOk;
        private Button btnCancel;

        public SectionModifierForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Modify Section Width/Depth";
            this.Size = new Size(300, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            chkWidth = new CheckBox { Text = "Modify Width (mm):", Location = new Point(20, 30), AutoSize = true };
            txtWidth = new TextBox { Text = "100", Location = new Point(140, 27), Size = new Size(100, 20), Enabled = false };
            chkWidth.CheckedChanged += (s, e) => txtWidth.Enabled = chkWidth.Checked;

            chkDepth = new CheckBox { Text = "Modify Depth (mm):", Location = new Point(20, 70), AutoSize = true };
            txtDepth = new TextBox { Text = "200", Location = new Point(140, 67), Size = new Size(100, 20), Enabled = false };
            chkDepth.CheckedChanged += (s, e) => txtDepth.Enabled = chkDepth.Checked;

            this.Controls.Add(chkWidth);
            this.Controls.Add(txtWidth);
            this.Controls.Add(chkDepth);
            this.Controls.Add(txtDepth);

            btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(100, 120) };
            btnOk.Click += BtnOk_Click;
            this.Controls.Add(btnOk);

            btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(185, 120) };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            ModifyWidth = chkWidth.Checked;
            ModifyDepth = chkDepth.Checked;

            if (!ModifyWidth && !ModifyDepth)
            {
                MessageBox.Show("Please select at least one dimension to modify.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            if (ModifyWidth)
            {
                double w;
                if (!double.TryParse(txtWidth.Text, out w) || w <= 0)
                {
                    MessageBox.Show("Please enter a valid positive number for Width.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                NewWidth = w;
            }

            if (ModifyDepth)
            {
                double d;
                if (!double.TryParse(txtDepth.Text, out d) || d <= 0)
                {
                    MessageBox.Show("Please enter a valid positive number for Depth.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                NewDepth = d;
            }
        }
    }
}
