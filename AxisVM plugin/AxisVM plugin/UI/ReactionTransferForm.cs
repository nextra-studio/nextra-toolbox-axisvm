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

        public ComboBox cbResultType;
        public ComboBox cbEnvComponent;
        public ComboBox cbEnvMinMax;

        public CheckBox chkTransferSupports;
        public TextBox txtMatchTolerance;

        public Button btnTransfer;
        public Button btnCancel;

        private Label lblTargetNote;
        private Label lblMatchTolerance;
        private Label lblMatchNote;

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

        /// <summary>True when the reactions go into an existing model instead of a fresh trimmed clone.</summary>
        public bool ModifyExistingFile
        {
            get { return rbExistingFile.Checked; }
        }

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

        /// <summary>How far from a reaction's coordinates an existing target node may sit and still count as the same point, in metres.</summary>
        public double NodeMatchToleranceM { get; private set; }

        public ReactionTransferForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Reaction Transfer";
            // ClientSize rather than Size, so the button row is positioned against real client coordinates.
            this.ClientSize = new Size(460, 445);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int xPos = 20;
            int yPos = 20;

            // ---------------------------------------------------------------
            // Target model
            // ---------------------------------------------------------------
            GroupBox gbTarget = new GroupBox() { Text = "Target Model", Location = new Point(xPos, yPos), Size = new Size(420, 105) };

            rbNewFile = new RadioButton() { Text = "Create New File", Location = new Point(20, 20), AutoSize = true, Checked = true };
            rbExistingFile = new RadioButton() { Text = "Modify Existing File", Location = new Point(170, 20), AutoSize = true };
            rbNewFile.CheckedChanged += TargetMode_CheckedChanged;

            txtFilePath = new TextBox() { Location = new Point(20, 46), Size = new Size(300, 20), ReadOnly = true };
            btnBrowse = new Button() { Text = "Browse...", Location = new Point(330, 44), Size = new Size(75, 23) };
            btnBrowse.Click += BtnBrowse_Click;

            lblTargetNote = new Label()
            {
                Location = new Point(20, 72),
                Size = new Size(385, 28),
                ForeColor = SystemColors.GrayText
            };

            gbTarget.Controls.AddRange(new Control[] { rbNewFile, rbExistingFile, txtFilePath, btnBrowse, lblTargetNote });
            this.Controls.Add(gbTarget);
            yPos += 115;

            // ---------------------------------------------------------------
            // Result type
            // ---------------------------------------------------------------
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

            Label lblGravityWarn = new Label()
            {
                Text = "Note: Self-weight load cases cannot have loads added; reactions from them are skipped.\nEnvelope results are written to a load case named 'Transferred Reactions (Env)'.",
                Location = new Point(20, 82),
                AutoSize = false,
                Size = new Size(385, 40),
                ForeColor = Color.Red
            };

            gbResultType.Controls.AddRange(new Control[] { lblResultType, cbResultType, lblEnvComponent, cbEnvComponent, lblEnvMinMax, cbEnvMinMax, lblGravityWarn });
            this.Controls.Add(gbResultType);
            yPos += 145;

            UpdateEnvelopeControls();

            // ---------------------------------------------------------------
            // Options
            // ---------------------------------------------------------------
            GroupBox gbOptions = new GroupBox() { Text = "Options", Location = new Point(xPos, yPos), Size = new Size(420, 115) };

            chkTransferSupports = new CheckBox() { Text = "Keep Existing Supports (new file only)", Location = new Point(20, 24), Checked = true, AutoSize = true };

            lblMatchTolerance = new Label() { Text = "Node match tolerance (mm):", Location = new Point(20, 54), AutoSize = true };
            txtMatchTolerance = new TextBox() { Text = "1.0", Location = new Point(180, 51), Size = new Size(70, 20) };

            lblMatchNote = new Label()
            {
                Text = "Reactions are always applied as nodal point loads.",
                Location = new Point(20, 80),
                Size = new Size(385, 28),
                ForeColor = SystemColors.GrayText
            };

            gbOptions.Controls.AddRange(new Control[] { chkTransferSupports, lblMatchTolerance, txtMatchTolerance, lblMatchNote });
            this.Controls.Add(gbOptions);
            yPos += 125;

            // ---------------------------------------------------------------
            // Action buttons
            // ---------------------------------------------------------------
            btnTransfer = new Button() { Text = "Transfer", Location = new Point(280, yPos), Size = new Size(80, 30), DialogResult = DialogResult.OK };
            btnCancel = new Button() { Text = "Cancel", Location = new Point(370, yPos), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            btnTransfer.Click += BtnTransfer_Click;

            this.Controls.Add(btnTransfer);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnTransfer;
            this.CancelButton = btnCancel;

            NodeMatchToleranceM = 0.001;
            UpdateTargetModeControls();
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

        private void TargetMode_CheckedChanged(object sender, EventArgs e)
        {
            // Path semantics differ per mode (save-as vs open), so a path chosen for one mode is not
            // meaningful in the other.
            txtFilePath.Text = string.Empty;
            UpdateTargetModeControls();
        }

        /// <summary>
        /// "Keep Existing Supports" only steers the trimming pass, which is exclusive to the new-file mode.
        /// The coordinate tolerance is the opposite: it only matters when matching reactions onto the nodes
        /// of a model that was never a copy of the source.
        /// </summary>
        private void UpdateTargetModeControls()
        {
            bool existing = rbExistingFile.Checked;

            chkTransferSupports.Enabled = !existing;
            lblMatchTolerance.Enabled = existing;
            txtMatchTolerance.Enabled = existing;

            lblTargetNote.Text = existing
                ? "The chosen model is backed up next to itself, then opened and added to. Nothing is deleted."
                : "The current model is cloned to this path, then everything unselected is deleted from the clone.";

            lblMatchNote.Text = existing
                ? "Reactions are applied as nodal point loads. Existing nodes, load cases and load groups are reused when they match; missing ones are created."
                : "Reactions are applied as nodal point loads.";
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            if (rbNewFile.Checked)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "AxisVM Files (*.axs)|*.axs";
                    sfd.Title = "Save New Target Model As";
                    if (sfd.ShowDialog() == DialogResult.OK) txtFilePath.Text = sfd.FileName;
                }
            }
            else
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "AxisVM Files (*.axs)|*.axs";
                    ofd.Title = "Select Existing Target Model";
                    ofd.CheckFileExists = true;
                    if (ofd.ShowDialog() == DialogResult.OK) txtFilePath.Text = ofd.FileName;
                }
            }
        }

        private void BtnTransfer_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtFilePath.Text))
            {
                MessageBox.Show("Please specify a target file path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            if (ModifyExistingFile)
            {
                double mm;
                if (!double.TryParse(txtMatchTolerance.Text, out mm) || mm <= 0)
                {
                    MessageBox.Show("Please enter a positive node match tolerance in millimetres.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
                NodeMatchToleranceM = mm / 1000.0;
            }
            else
            {
                NodeMatchToleranceM = 0.001;
            }
        }
    }
}
