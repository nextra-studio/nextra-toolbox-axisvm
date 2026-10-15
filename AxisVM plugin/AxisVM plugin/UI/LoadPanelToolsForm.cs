using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    public enum LoadPanelOperation
    {
        PanelToDomain,
        DomainToPanel,
        SplitPanelByLines,
        MergePanels
    }

    public class LoadPanelToolsForm : Form
    {
        public LoadPanelOperation Operation { get; private set; }

        /// <summary>Whether the converted source object is removed once its counterpart exists.</summary>
        public bool DeleteOriginal { get; private set; }

        private RadioButton radPanelToDomain;
        private RadioButton radDomainToPanel;
        private RadioButton radSplit;
        private RadioButton radMerge;
        private CheckBox chkDeleteOriginal;
        private Label lblWhatToSelect;
        private Label lblDefaults;
        private Button btnOk;
        private Button btnCancel;

        /// <summary>Description of the placeholder attributes, filled in by the caller from the live model.</summary>
        private readonly string domainDefaults;

        public LoadPanelToolsForm(string domainDefaults)
        {
            this.domainDefaults = domainDefaults;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Load Panel Tools";
            // ClientSize rather than Size, so the button row sits where it is put.
            this.ClientSize = new Size(420, 345);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            GroupBox grpOperation = new GroupBox
            {
                Text = "Operation",
                Location = new Point(15, 15),
                Size = new Size(390, 135)
            };

            radPanelToDomain = new RadioButton { Text = "Load Panel  →  Domain", Location = new Point(20, 25), AutoSize = true, Checked = true };
            radDomainToPanel = new RadioButton { Text = "Domain  →  Load Panel", Location = new Point(20, 50), AutoSize = true };
            radSplit = new RadioButton { Text = "Split Load Panel by selected lines", Location = new Point(20, 75), AutoSize = true };
            radMerge = new RadioButton { Text = "Merge selected Load Panels", Location = new Point(20, 100), AutoSize = true };

            radPanelToDomain.CheckedChanged += Operation_CheckedChanged;
            radDomainToPanel.CheckedChanged += Operation_CheckedChanged;
            radSplit.CheckedChanged += Operation_CheckedChanged;
            radMerge.CheckedChanged += Operation_CheckedChanged;

            grpOperation.Controls.AddRange(new Control[] { radPanelToDomain, radDomainToPanel, radSplit, radMerge });
            this.Controls.Add(grpOperation);

            lblWhatToSelect = new Label
            {
                Location = new Point(18, 160),
                Size = new Size(390, 34),
                ForeColor = SystemColors.GrayText
            };
            this.Controls.Add(lblWhatToSelect);

            lblDefaults = new Label
            {
                Location = new Point(18, 200),
                Size = new Size(390, 58),
                ForeColor = SystemColors.GrayText
            };
            this.Controls.Add(lblDefaults);

            chkDeleteOriginal = new CheckBox
            {
                Text = "Delete the original after converting",
                Location = new Point(18, 265),
                AutoSize = true,
                Checked = false
            };
            this.Controls.Add(chkDeleteOriginal);

            btnOk = new Button { Text = "Apply", Location = new Point(230, 303), Size = new Size(80, 28), DialogResult = DialogResult.OK };
            btnOk.Click += BtnOk_Click;
            btnCancel = new Button { Text = "Cancel", Location = new Point(320, 303), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };

            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;

            UpdateHints();
        }

        private void Operation_CheckedChanged(object sender, EventArgs e)
        {
            UpdateHints();
        }

        private void UpdateHints()
        {
            if (radPanelToDomain.Checked)
            {
                lblWhatToSelect.Text = "Select the load panels to convert. Every selected panel is handled in one pass.";
                lblDefaults.Text = "New domains are created with placeholder attributes:\n" + domainDefaults +
                                   "\nAdjust them in AxisVM afterwards. When the panel is deleted, its area\nloads move onto the domain.";
                chkDeleteOriginal.Text = "Delete the load panel once its domain exists";
                chkDeleteOriginal.Enabled = true;
            }
            else if (radDomainToPanel.Checked)
            {
                lblWhatToSelect.Text = "Select the domains to convert. Every selected domain is handled in one pass.";
                lblDefaults.Text = "The new panel follows the domain's contour lines and stays when the domain\n" +
                                   "is deleted. Loads on a deleted domain are removed with it - the summary\n" +
                                   "says how many.";
                chkDeleteOriginal.Text = "Delete the domain once its load panel exists";
                chkDeleteOriginal.Enabled = true;
            }
            else if (radMerge.Checked)
            {
                lblWhatToSelect.Text = "Select the load panels to merge. Panels that lie in one plane and share edge\nlines become one panel; separate groups are merged separately.";
                lblDefaults.Text = "The merged panel keeps the loaded outline edges and the loads, which must be\n" +
                                   "the same on every panel. Shared edges end up inside it and stop receiving\n" +
                                   "load. The cut lines themselves stay in the model.";
                chkDeleteOriginal.Text = "Delete the originals (always on when merging)";
                chkDeleteOriginal.Enabled = false;
            }
            else
            {
                lblWhatToSelect.Text = "Select the load panels AND the lines that cut them. Each cutting line must run\nbetween two points on the panel's contour.";
                lblDefaults.Text = "Each panel is replaced by the pieces the lines cut it into. The pieces inherit the\n" +
                                   "original's Auto flag, load-carrying edges and area loads; a cutting line that is a\n" +
                                   "beam or rib also carries load.";
                chkDeleteOriginal.Text = "Delete the original (always on when splitting)";
                chkDeleteOriginal.Enabled = false;
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (radPanelToDomain.Checked) Operation = LoadPanelOperation.PanelToDomain;
            else if (radDomainToPanel.Checked) Operation = LoadPanelOperation.DomainToPanel;
            else if (radMerge.Checked) Operation = LoadPanelOperation.MergePanels;
            else Operation = LoadPanelOperation.SplitPanelByLines;

            // Splitting and merging replace the panels by definition - there is nothing meaningful to keep.
            DeleteOriginal = Operation == LoadPanelOperation.SplitPanelByLines || Operation == LoadPanelOperation.MergePanels || chkDeleteOriginal.Checked;
        }
    }
}
