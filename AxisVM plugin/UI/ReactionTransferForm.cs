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
        
        public CheckBox chkTransferSupports;
        public CheckBox chkTransferLoadCases;
        public CheckBox chkTransferLoadGroups;
        public CheckBox chkTransferLoadCombinations;
        
        public RadioButton rbMergeLoads;
        public RadioButton rbReplaceLoads;
        
        public RadioButton rbTransferSelectedElements;
        public RadioButton rbTransferParts;
        
        public CheckBox chkTransferBottomBeams;
        
        public RadioButton rbNodePointLoads;
        public RadioButton rbBeamPointLoads;
        
        public CheckBox chkTidyUpLines;
        
        public Button btnTransfer;
        public Button btnCancel;

        public ReactionTransferForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Reaction Transfer";
            this.Size = new Size(400, 600);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int yPos = 20;
            int xPos = 20;

            // Target File Selection
            GroupBox gbTarget = new GroupBox() { Text = "Target Model", Location = new Point(xPos, yPos), Size = new Size(340, 100) };
            rbNewFile = new RadioButton() { Text = "Create New File", Location = new Point(20, 20), Checked = true };
            rbExistingFile = new RadioButton() { Text = "Modify Existing File", Location = new Point(160, 20) };
            
            txtFilePath = new TextBox() { Location = new Point(20, 50), Size = new Size(220, 20), ReadOnly = true };
            btnBrowse = new Button() { Text = "Browse...", Location = new Point(250, 48), Size = new Size(75, 23) };
            btnBrowse.Click += BtnBrowse_Click;
            
            gbTarget.Controls.AddRange(new Control[] { rbNewFile, rbExistingFile, txtFilePath, btnBrowse });
            this.Controls.Add(gbTarget);
            yPos += 110;

            // Source Selection
            GroupBox gbSource = new GroupBox() { Text = "Transfer Source", Location = new Point(xPos, yPos), Size = new Size(340, 60) };
            rbTransferSelectedElements = new RadioButton() { Text = "Selected Elements", Location = new Point(20, 25), Checked = true };
            rbTransferParts = new RadioButton() { Text = "Specific Parts", Location = new Point(160, 25) };
            
            gbSource.Controls.AddRange(new Control[] { rbTransferSelectedElements, rbTransferParts });
            this.Controls.Add(gbSource);
            yPos += 70;

            // What to Transfer
            GroupBox gbItems = new GroupBox() { Text = "Items to Transfer", Location = new Point(xPos, yPos), Size = new Size(340, 100) };
            chkTransferSupports = new CheckBox() { Text = "Transfer Supports", Location = new Point(20, 20), Checked = true };
            chkTransferBottomBeams = new CheckBox() { Text = "Transfer Bottom Foundation Beams", Location = new Point(20, 45), Checked = true };
            chkTransferLoadCases = new CheckBox() { Text = "Load Cases", Location = new Point(20, 70), Checked = true };
            chkTransferLoadGroups = new CheckBox() { Text = "Load Groups", Location = new Point(120, 70), Checked = true };
            chkTransferLoadCombinations = new CheckBox() { Text = "Combinations", Location = new Point(220, 70), Checked = true };
            
            gbItems.Controls.AddRange(new Control[] { chkTransferSupports, chkTransferBottomBeams, chkTransferLoadCases, chkTransferLoadGroups, chkTransferLoadCombinations });
            this.Controls.Add(gbItems);
            yPos += 110;

            // Load Case Conflict Resolution
            GroupBox gbLoads = new GroupBox() { Text = "Load Case Conflicts (Existing Target)", Location = new Point(xPos, yPos), Size = new Size(340, 60) };
            rbMergeLoads = new RadioButton() { Text = "Merge (Add/Update)", Location = new Point(20, 25), Checked = true };
            rbReplaceLoads = new RadioButton() { Text = "Replace All", Location = new Point(160, 25) };
            
            gbLoads.Controls.AddRange(new Control[] { rbMergeLoads, rbReplaceLoads });
            this.Controls.Add(gbLoads);
            yPos += 70;

            // Load Application
            GroupBox gbApplication = new GroupBox() { Text = "Reaction Application Method", Location = new Point(xPos, yPos), Size = new Size(340, 90) };
            rbNodePointLoads = new RadioButton() { Text = "Apply as Node Point Loads", Location = new Point(20, 25) };
            rbBeamPointLoads = new RadioButton() { Text = "Apply as Beam Point Loads", Location = new Point(20, 50), Checked = true };
            
            chkTidyUpLines = new CheckBox() { Text = "Tidy Up (Merge divided horizontal foundation beams)", Location = new Point(20, 75), Checked = true, AutoSize = true };
            
            gbApplication.Controls.AddRange(new Control[] { rbNodePointLoads, rbBeamPointLoads, chkTidyUpLines });
            this.Controls.Add(gbApplication);
            yPos += 110;

            // Action Buttons
            btnTransfer = new Button() { Text = "Transfer", Location = new Point(200, yPos), Size = new Size(80, 30), DialogResult = DialogResult.OK };
            btnCancel = new Button() { Text = "Cancel", Location = new Point(290, yPos), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            
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
