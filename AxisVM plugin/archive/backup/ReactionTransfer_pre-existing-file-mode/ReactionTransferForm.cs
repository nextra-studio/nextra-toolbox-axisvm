using System;
using System.Drawing;
using System.Windows.Forms;
using AxisVM;

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
        public ComboBox cbEnvComponent;
        public ComboBox cbEnvMinMax;

        public CheckBox chkTidyUpLines;
        public CheckBox chkTransferSupports;

        public Button btnTransfer;
        public Button btnCancel;

        // Order matches the entries added to cbEnvComponent / cbEnvMinMax below.
        private static readonly ENodalSupportForce[] EnvComponents = new ENodalSupportForce[]
        {
            ENodalSupportForce.nsfRx, ENodalSupportForce.nsfRy, ENodalSupportForce.nsfRz,
            ENodalSupportForce.nsfRxx, ENodalSupportForce.nsfRyy, ENodalSupportForce.nsfRzz
        };

        private static readonly EMinMaxType[] EnvMinMaxTypes = new EMinMaxType[]
        {
            EMinMaxType.mtMax, EMinMaxType.mtMin
        };

        /// <summary>Envelope component the user picked. Only meaningful when Result Type is "Envelopes".</summary>
        public ENodalSupportForce SelectedEnvelopeComponent
        {
            get
            {
                int i = cbEnvComponent.SelectedIndex;
                return (i >= 0 && i < EnvComponents.Length) ? EnvComponents[i] : ENodalSupportForce.nsfRz;
            }
        }

        /// <summary>Envelope Min/Max the user picked. Only meaningful when Result Type is "Envelopes".</summary>
        public EMinMaxType SelectedEnvelopeMinMax
        {
            get
            {
                int i = cbEnvMinMax.SelectedIndex;
                return (i >= 0 && i < EnvMinMaxTypes.Length) ? EnvMinMaxTypes[i] : EMinMaxType.mtMax;
            }
        }

        public ReactionTransferForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Reaction Transfer";
            this.Size = new Size(480, 520);
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
            GroupBox gbResultType = new GroupBox() { Text = "Result Type to Transfer", Location = new Point(xPos, yPos), Size = new Size(420, 135) };
            Label lblResultType = new Label() { Text = "Type:", Location = new Point(20, 25), AutoSize = true };
            cbResultType = new ComboBox() { Location = new Point(70, 22), Size = new Size(250, 20), DropDownStyle = ComboBoxStyle.DropDownList };
            cbResultType.Items.AddRange(new string[] { "Linear Static (Individual Cases)", "Non-Linear Results", "Envelopes" });
            cbResultType.SelectedIndex = 0; // Default to Linear Static
            cbResultType.SelectedIndexChanged += CbResultType_SelectedIndexChanged;

            Label lblEnvComponent = new Label() { Text = "Envelope of:", Location = new Point(20, 55), AutoSize = true };
            cbEnvComponent = new ComboBox() { Location = new Point(95, 52), Size = new Size(90, 20), DropDownStyle = ComboBoxStyle.DropDownList };
            cbEnvComponent.Items.AddRange(new string[] { "Rx", "Ry", "Rz", "Rxx", "Ryy", "Rzz" });
            cbEnvComponent.SelectedIndex = 2; // Rz

            Label lblEnvMinMax = new Label() { Text = "Min/Max:", Location = new Point(205, 55), AutoSize = true };
            cbEnvMinMax = new ComboBox() { Location = new Point(265, 52), Size = new Size(90, 20), DropDownStyle = ComboBoxStyle.DropDownList };
            cbEnvMinMax.Items.AddRange(new string[] { "Max", "Min" });
            cbEnvMinMax.SelectedIndex = 0;

            Label lblGravityWarn = new Label() {
                Text = "Note: Self-weight load cases cannot have loads added; reactions from them are skipped.\nEnvelope results are written to a new load case, 'Transferred Reactions (Env)'.",
                Location = new Point(20, 82),
                AutoSize = false,
                Size = new Size(380, 40),
                ForeColor = Color.Red
            };

            gbResultType.Controls.AddRange(new Control[] { lblResultType, cbResultType, lblEnvComponent, cbEnvComponent, lblEnvMinMax, cbEnvMinMax, lblGravityWarn });
            this.Controls.Add(gbResultType);
            yPos += 145;

            UpdateEnvelopeControls();

            // Load Application
            GroupBox gbApplication = new GroupBox() { Text = "Reaction Application Method", Location = new Point(xPos, yPos), Size = new Size(420, 110) };
            rbNodePointLoads = new RadioButton() { Text = "Apply as Node Point Loads", Location = new Point(20, 25), AutoSize = true };
            rbBeamPointLoads = new RadioButton() { Text = "Apply as Beam Point Loads", Location = new Point(200, 25), Checked = true, AutoSize = true };
            
            chkTidyUpLines = new CheckBox() { Text = "Tidy Up (merge divided foundation beams) - not implemented", Location = new Point(20, 55), Checked = false, Enabled = false, AutoSize = true };
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

        private void CbResultType_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateEnvelopeControls();
        }

        /// <summary>The envelope component and Min/Max only apply to the "Envelopes" result type.</summary>
        private void UpdateEnvelopeControls()
        {
            bool isEnvelope = cbResultType.SelectedIndex == 2;
            cbEnvComponent.Enabled = isEnvelope;
            cbEnvMinMax.Enabled = isEnvelope;
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
