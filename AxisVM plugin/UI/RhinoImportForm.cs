using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Import settings for a Rhino AxisBridge export.
    ///
    /// The form peeks at the chosen file as soon as it is picked, so the unit and tolerance boxes
    /// arrive pre-filled with what Rhino recorded and the element counts (and the document library)
    /// are visible before anything is written. Everything stays overridable.
    /// </summary>
    public class RhinoImportForm : Form
    {
        public string JsonPath { get { return txtPath.Text.Trim(); } }
        public bool DryRun { get { return chkDryRun.Checked; } }

        /// <summary>Connect crossing lines / points on lines before the attributes are applied.</summary>
        public bool AutoIntersect { get { return chkAutoIntersect.Checked; } }

        /// <summary>Node merge tolerance in metres, parsed from the box (which is in millimetres).</summary>
        public double ToleranceM
        {
            get
            {
                double mm;
                if (!double.TryParse(txtTolerance.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out mm))
                    return 0.001;
                return Math.Max(mm, 0.0001) / 1000.0;
            }
        }

        /// <summary>The unit choice, ready to hand to the builder.</summary>
        public AxUnits Units
        {
            get
            {
                int li = Math.Max(0, cboLength.SelectedIndex);
                int fi = Math.Max(0, cboForce.SelectedIndex);
                return new AxUnits
                {
                    LengthToM = AxUnits.LengthUnitFactors[li],
                    LengthName = AxUnits.LengthUnitNames[li],
                    ForceSystem = fi,
                    ForceName = fi == 1 ? "N, mm" : "kN, m"
                };
            }
        }

        private TextBox txtPath;
        private Button btnBrowse;
        private ComboBox cboLength;
        private ComboBox cboForce;
        private TextBox txtTolerance;
        private CheckBox chkDryRun;
        private CheckBox chkAutoIntersect;
        private Label lblFileInfo;
        private Button btnOk;
        private Button btnCancel;

        public RhinoImportForm(string initialPath)
        {
            InitializeComponent();

            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
            {
                txtPath.Text = initialPath;
                PeekFile();
            }
        }

        private void InitializeComponent()
        {
            Text = "Rhino Import";
            ClientSize = new Size(600, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // ---- file ----------------------------------------------------
            Label lblPath = new Label
            {
                Text = "AxisBridge export (.json written by AxExport in Rhino)",
                Location = new Point(16, 16),
                AutoSize = true,
                Font = new Font(this.Font, FontStyle.Bold)
            };

            txtPath = new TextBox { Location = new Point(16, 40), Size = new Size(465, 24) };
            txtPath.TextChanged += delegate { UpdateOkState(); };
            txtPath.Leave += delegate { PeekFile(); };

            btnBrowse = new Button { Text = "Browse...", Location = new Point(489, 39), Size = new Size(95, 26) };
            btnBrowse.Click += Browse_Click;

            lblFileInfo = new Label
            {
                Location = new Point(16, 70),
                Size = new Size(568, 42),
                ForeColor = SystemColors.GrayText,
                Text = "Pick the file AxExport wrote in Rhino."
            };

            // ---- units ---------------------------------------------------
            GroupBox grpUnits = new GroupBox { Text = "Units of the Rhino attributes", Location = new Point(16, 116), Size = new Size(568, 122) };

            Label lblLength = new Label { Text = "Length:", Location = new Point(16, 27), AutoSize = true };
            cboLength = new ComboBox { Location = new Point(76, 23), Size = new Size(80, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cboLength.Items.AddRange(AxUnits.LengthUnitNames);
            cboLength.SelectedIndex = 0;

            Label lblForce = new Label { Text = "Loads:", Location = new Point(178, 27), AutoSize = true };
            cboForce = new ComboBox { Location = new Point(228, 23), Size = new Size(320, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cboForce.Items.AddRange(AxUnits.ForceUnitNames);
            cboForce.SelectedIndex = 0;

            Label lblUnitNote = new Label
            {
                Location = new Point(16, 56),
                Size = new Size(540, 58),
                ForeColor = SystemColors.GrayText,
                Text = "Length = the Rhino model unit (coordinates, thickness, eccentricity, load positions).\r\n" +
                       "Loads = the unit of load values and stiffnesses. The AxisBridge dialogs always write kN, kN/m, kN/m², kNm - " +
                       "choose 'N, mm' only for attributes typed by hand in N, N/mm, N/mm², Nmm."
            };

            grpUnits.Controls.AddRange(new Control[] { lblLength, cboLength, lblForce, cboForce, lblUnitNote });

            // ---- geometry ------------------------------------------------
            GroupBox grpGeom = new GroupBox { Text = "Geometry", Location = new Point(16, 246), Size = new Size(568, 120) };

            Label lblTol = new Label { Text = "Merge nodes closer than", Location = new Point(16, 27), AutoSize = true };
            txtTolerance = new TextBox { Location = new Point(175, 23), Size = new Size(65, 24), Text = "1.0" };
            Label lblTolUnit = new Label { Text = "mm", Location = new Point(245, 27), AutoSize = true };
            Label lblTolNote = new Label
            {
                Location = new Point(285, 22),
                Size = new Size(270, 34),
                ForeColor = SystemColors.GrayText,
                Text = "A beam end and a slab corner within this distance become one node."
            };

            chkAutoIntersect = new CheckBox
            {
                Text = "Connect crossing lines and points lying on lines",
                Location = new Point(16, 62),
                AutoSize = true,
                Checked = true
            };
            Label lblIntersectNote = new Label
            {
                Location = new Point(34, 84),
                Size = new Size(520, 32),
                ForeColor = SystemColors.GrayText,
                Text = "Each Rhino curve stays one member (releases at its ends, loads along its length). " +
                       "Springs, links and arcs are never split."
            };

            grpGeom.Controls.AddRange(new Control[] { lblTol, txtTolerance, lblTolUnit, lblTolNote, chkAutoIntersect, lblIntersectNote });

            // ---- buttons -------------------------------------------------
            chkDryRun = new CheckBox
            {
                Text = "Dry run (check the attributes and report only - change nothing)",
                Location = new Point(18, 380),
                AutoSize = true
            };

            btnOk = new Button { Text = "Import", Location = new Point(390, 398), Size = new Size(90, 32), Font = new Font(this.Font, FontStyle.Bold), DialogResult = DialogResult.OK };
            btnCancel = new Button { Text = "Cancel", Location = new Point(494, 398), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[]
            {
                lblPath, txtPath, btnBrowse, lblFileInfo,
                grpUnits, grpGeom,
                chkDryRun, btnOk, btnCancel
            });

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            UpdateOkState();
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Open an AxisBridge export";
                dlg.Filter = "AxisBridge export (*.json)|*.json|All files (*.*)|*.*";

                if (!string.IsNullOrEmpty(txtPath.Text))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(txtPath.Text);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                    }
                    catch { }
                }

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                txtPath.Text = dlg.FileName;
                PeekFile();
            }
        }

        /// <summary>
        /// Reads the header and the element counts, to pre-fill the unit and tolerance boxes and show
        /// what is in the file. A file that will not parse is reported here rather than after the
        /// user has committed to an import.
        /// </summary>
        private void PeekFile()
        {
            string path = JsonPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                lblFileInfo.Text = "Pick the file AxExport wrote in Rhino.";
                lblFileInfo.ForeColor = SystemColors.GrayText;
                UpdateOkState();
                return;
            }

            try
            {
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));

                JsonValue source = root["source"];
                string unitSystem = source != null && source["unitSystem"] != null ? source["unitSystem"].AsString("") : "";

                int lengthIndex = AxUnits.LengthIndexFromRhinoName(unitSystem);
                cboLength.SelectedIndex = lengthIndex;

                if (source != null && source["tolerance"] != null)
                {
                    double tolMm = source["tolerance"].AsDouble(0.001) * AxUnits.LengthUnitFactors[lengthIndex] * 1000.0;
                    // Never merge tighter than 0.1 mm or looser than 10 mm automatically.
                    tolMm = Math.Max(0.1, Math.Min(10.0, tolMm));
                    txtTolerance.Text = tolMm.ToString("0.###", CultureInfo.InvariantCulture);
                }

                int points = root["points"] != null ? root["points"].Count : 0;
                int lines = root["lines"] != null ? root["lines"].Count : 0;
                int domains = root["domains"] != null ? root["domains"].Count : 0;
                int warnings = root["warnings"] != null ? root["warnings"].Count : 0;
                JsonValue lib = root["library"];
                int cases = lib != null && lib["loadCases"] != null ? lib["loadCases"].Count : 0;
                int secs = lib != null && lib["sections"] != null ? lib["sections"].Count : 0;

                bool unitOk = AxUnits.IsSupportedRhinoUnit(unitSystem);
                lblFileInfo.ForeColor = unitOk ? SystemColors.GrayText : Color.Firebrick;
                lblFileInfo.Text = string.Format(
                    "{0} points, {1} curves, {2} surfaces{3}{4}.  Rhino units: {5}.{6}",
                    points, lines, domains,
                    lib != null ? string.Format("; library: {0} load case(s), {1} section(s)", cases, secs) : "",
                    warnings > 0 ? string.Format("; {0} export warning(s)", warnings) : "",
                    string.IsNullOrEmpty(unitSystem) ? "not recorded" : unitSystem,
                    unitOk ? "" : "  Only millimetres and metres are supported - check Length below.");
            }
            catch (Exception ex)
            {
                lblFileInfo.ForeColor = Color.Firebrick;
                lblFileInfo.Text = "This file could not be read: " + ex.Message;
            }

            UpdateOkState();
        }

        private void UpdateOkState()
        {
            if (btnOk == null) return;
            btnOk.Enabled = !string.IsNullOrEmpty(JsonPath);
        }
    }
}
