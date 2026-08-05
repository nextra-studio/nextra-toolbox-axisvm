using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public class ReactionTransferForm : Form
    {
        // UI Controls
        public TextBox txtFilePath;
        public Button btnBrowse;
        public RadioButton rbNewFile;
        public RadioButton rbExistingFile;
        
        public RadioButton rbNodePointLoads;
        public RadioButton rbBeamPointLoads;
        
        public ComboBox cbResultType;
        
        public CheckBox chkTidyUpLines;
        public CheckBox chkTransferSupports;
        
        public Button btnTransfer;
        public Button btnCancel;

        public ReactionTransferForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Reaction Transfer";
            this.Size = new Size(480, 480);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int yPos = 20;
            int xPos = 20;

            // Target File Selection
            GroupBox gbTarget = new GroupBox() { Text = "Target Model", Location = new Point(xPos, yPos), Size = new Size(420, 100) };
            rbNewFile = new RadioButton() { Text = "Create New File", Location = new Point(20, 20), Checked = true };
            rbExistingFile = new RadioButton() { Text = "Modify Existing File", Location = new Point(160, 20), AutoSize = true, Enabled = false }; // Disable existing file for now to prevent overwriting
            
            txtFilePath = new TextBox() { Location = new Point(20, 50), Size = new Size(300, 20), ReadOnly = true };
            btnBrowse = new Button() { Text = "Browse...", Location = new Point(330, 48), Size = new Size(75, 23) };
            btnBrowse.Click += BtnBrowse_Click;
            
            gbTarget.Controls.AddRange(new Control[] { rbNewFile, rbExistingFile, txtFilePath, btnBrowse });
            this.Controls.Add(gbTarget);
            yPos += 110;

            // Result Type Selection
            GroupBox gbResultType = new GroupBox() { Text = "Result Type to Transfer", Location = new Point(xPos, yPos), Size = new Size(420, 80) };
            Label lblResultType = new Label() { Text = "Type:", Location = new Point(20, 25), AutoSize = true };
            cbResultType = new ComboBox() { Location = new Point(70, 22), Size = new Size(250, 20), DropDownStyle = ComboBoxStyle.DropDownList };
            cbResultType.Items.AddRange(new string[] { "Linear Static (Individual Cases)", "Non-Linear Results", "Envelopes" });
            cbResultType.SelectedIndex = 0; // Default to Linear Static
            
            Label lblGravityWarn = new Label() { 
                Text = "Note: Gravity (G) load cases cannot have loads added. Please choose/setup load cases without 'G'.", 
                Location = new Point(20, 50), 
                AutoSize = false,
                Size = new Size(380, 30),
                ForeColor = Color.Red
            };
            
            gbResultType.Controls.AddRange(new Control[] { lblResultType, cbResultType, lblGravityWarn });
            this.Controls.Add(gbResultType);
            yPos += 90;

            // Load Application
            GroupBox gbApplication = new GroupBox() { Text = "Reaction Application Method", Location = new Point(xPos, yPos), Size = new Size(420, 110) };
            rbNodePointLoads = new RadioButton() { Text = "Apply as Node Point Loads", Location = new Point(20, 25), AutoSize = true };
            rbBeamPointLoads = new RadioButton() { Text = "Apply as Beam Point Loads", Location = new Point(200, 25), Checked = true, AutoSize = true };
            
            chkTidyUpLines = new CheckBox() { Text = "Tidy Up (Merge divided horizontal foundation beams)", Location = new Point(20, 55), Checked = true, AutoSize = true };
            chkTransferSupports = new CheckBox() { Text = "Keep Existing Supports", Location = new Point(20, 80), Checked = true, AutoSize = true };
            
            gbApplication.Controls.AddRange(new Control[] { rbNodePointLoads, rbBeamPointLoads, chkTidyUpLines, chkTransferSupports });
            this.Controls.Add(gbApplication);
            yPos += 130;

            // Action Buttons
            // Action Buttons
            btnTransfer = new Button() { Text = "Transfer", Location = new Point(260, yPos), Size = new Size(80, 30), DialogResult = DialogResult.OK };
            btnCancel = new Button() { Text = "Cancel", Location = new Point(360, yPos), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            
            this.Controls.Add(btnTransfer);
            this.Controls.Add(btnCancel);
            
            this.AcceptButton = btnTransfer;
            this.CancelButton = btnCancel;
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            if (rbNewFile.Checked)
            {
                SaveFileDialog sfd = new SaveFileDialog();
                sfd.Filter = "AxisVM Files (*.axs)|*.axs";
                sfd.Title = "Save New Target Model As";
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    txtFilePath.Text = sfd.FileName;
                }
            }
            else
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "AxisVM Files (*.axs)|*.axs";
                ofd.Title = "Select Existing Target Model";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtFilePath.Text = ofd.FileName;
                }
            }
        }
    }
}
