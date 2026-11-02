using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Import settings for a Rhino AxisBridge export (tab "Import"), and the live link with Rhino's
    /// AxLink (tab "Live link": Load from Rhino / Send to Rhino on one shared link file).
    ///
    /// The form peeks at the chosen file as soon as it is picked, so the unit and tolerance boxes
    /// arrive pre-filled with what Rhino recorded and the element counts (and the document library)
    /// are visible before anything is written. Everything stays overridable.
    /// </summary>
    public class RhinoImportForm : Form
    {
        public enum FormAction { Import, LoadFromRhino, SendToRhino }

        /// <summary>What the dialog was closed for (with DialogResult.OK).</summary>
        public FormAction Action { get; private set; }

        /// <summary>The live link file on the "Live link" tab.</summary>
        public string LinkPath { get { return txtLinkPath.Text.Trim(); } }
        public string JsonPath { get { return txtPath.Text.Trim(); } }
        public bool DryRun { get { return chkDryRun.Checked; } }

        /// <summary>Connect crossing lines / points on lines before the attributes are applied.</summary>
        public bool AutoIntersect { get { return chkAutoIntersect.Checked; } }
        /// <summary>Live link: mesh the domains a load rebuilt again (off by default).</summary>
        public bool RemeshRebuilt { get { return chkRemesh != null && chkRemesh.Checked; } }

        /// <summary>Delete what an earlier import of this file created before building (update).</summary>
        public bool ReplacePrevious { get { return chkReplace.Checked && chkReplace.Enabled; } }

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
        private CheckBox chkReplace;
        private Label lblReplaceInfo;
        private Label lblFileInfo;
        private Button btnOk;
        private Button btnCancel;

        /// <summary>Writes the catalog CSVs for Rhino and returns a one-line summary (set by RhinoImportLogic).</summary>
        private readonly Func<string> exportCatalog;
        private Label lblCatalogInfo;

        /// <summary>What an earlier import of a file left in the open model (set by RhinoImportLogic).</summary>
        private readonly Func<string, LinkedItems> findLinked;

        private TabControl tabs;
        private TabPage pageImport;
        private TabPage pageLink;
        private TextBox txtLinkPath;
        private Label lblLinkStatus;
        private Button btnLinkLoad;
        private CheckBox chkRemesh;
        private Button btnLinkSend;

        public RhinoImportForm(string initialPath) : this(initialPath, null, null, null, false) { }

        public RhinoImportForm(string initialPath, Func<string> exportCatalog, Func<string, LinkedItems> findLinked,
                               string initialLinkPath, bool showLink)
        {
            this.exportCatalog = exportCatalog;
            this.findLinked = findLinked;
            InitializeComponent();

            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
            {
                txtPath.Text = initialPath;
                PeekFile();
            }

            txtLinkPath.Text = initialLinkPath ?? "";
            ShowLinkStatus();
            if (showLink) tabs.SelectedIndex = tabs.TabPages.IndexOf(pageLink);
            OnTabChanged();
        }

        private void InitializeComponent()
        {
            Theme.Initialize(this);
            Text = "Rhino Import";
            ClientSize = new Size(600, 490);
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
                Size = new Size(568, 60),
                ForeColor = SystemColors.GrayText,
                Text = "Pick the file AxExport wrote in Rhino."
            };

            // ---- update --------------------------------------------------
            chkReplace = new CheckBox
            {
                Text = "Update the earlier import of this file (replace it, do not add a second copy)",
                Location = new Point(16, 136),
                AutoSize = true,
                Enabled = false
            };
            lblReplaceInfo = new Label
            {
                Location = new Point(34, 158),
                Size = new Size(550, 48),
                ForeColor = SystemColors.GrayText,
                Text = ""
            };

            // ---- units ---------------------------------------------------
            GroupBox grpUnits = new GroupBox { Text = "Units of the Rhino attributes", Location = new Point(16, 212), Size = new Size(568, 142) };

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
                Size = new Size(540, 80),
                ForeColor = SystemColors.GrayText,
                Text = "Length = the Rhino model unit (coordinates, thickness, eccentricity, load positions).\r\n" +
                       "Loads = the unit of load values and stiffnesses. The AxisBridge dialogs always write kN, kN/m, kN/m², kNm - " +
                       "choose 'N, mm' only for attributes typed by hand in N, N/mm, N/mm², Nmm."
            };

            grpUnits.Controls.AddRange(new Control[] { lblLength, cboLength, lblForce, cboForce, lblUnitNote });

            // ---- geometry ------------------------------------------------
            GroupBox grpGeom = new GroupBox { Text = "Geometry", Location = new Point(16, 364), Size = new Size(568, 142) };

            Label lblTol = new Label { Text = "Merge nodes closer than", Location = new Point(16, 27), AutoSize = true };
            txtTolerance = new TextBox { Location = new Point(175, 23), Size = new Size(65, 24), Text = "1.0" };
            Label lblTolUnit = new Label { Text = "mm", Location = new Point(245, 27), AutoSize = true };
            Label lblTolNote = new Label
            {
                Location = new Point(285, 22),
                Size = new Size(270, 44),
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
                Size = new Size(520, 48),
                ForeColor = SystemColors.GrayText,
                Text = "Each Rhino curve stays one member (releases at its ends, loads along its length). " +
                       "Springs, links and arcs are never split."
            };

            grpGeom.Controls.AddRange(new Control[] { lblTol, txtTolerance, lblTolUnit, lblTolNote, chkAutoIntersect, lblIntersectNote });

            // ---- catalog for Rhino --------------------------------------
            GroupBox grpCatalog = new GroupBox { Text = "Material and section catalog for Rhino", Location = new Point(16, 516), Size = new Size(568, 112) };
            Label lblCatalogNote = new Label
            {
                Location = new Point(16, 22),
                Size = new Size(380, 50),
                ForeColor = SystemColors.GrayText,
                Text = "Lists every Eurocode material and cross-section table of this AxisVM in Rhino " +
                       "(AxLibrary > From AxisVM catalog). Once per computer; the model is not changed."
            };
            Button btnCatalog = new Button { Text = "Export catalog for Rhino", Location = new Point(404, 24), Size = new Size(150, 30) };
            lblCatalogInfo = new Label { Location = new Point(16, 76), Size = new Size(540, 32), ForeColor = SystemColors.GrayText, Text = CatalogStatus() };
            btnCatalog.Enabled = exportCatalog != null;
            btnCatalog.Click += Catalog_Click;
            grpCatalog.Controls.AddRange(new Control[] { lblCatalogNote, btnCatalog, lblCatalogInfo });

            // ---- buttons -------------------------------------------------
            chkDryRun = new CheckBox
            {
                Text = "Dry run (check the attributes and report only - change nothing)",
                Location = new Point(18, 640),
                AutoSize = true
            };

            btnOk = new Button { Text = "Import", Location = new Point(390, 398), Size = new Size(90, 32), Font = new Font(this.Font, FontStyle.Bold), DialogResult = DialogResult.OK };
            btnCancel = new Button { Text = "Cancel", Location = new Point(494, 398), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };

            pageImport = new TabPage("Import") { BackColor = Color.White };
            pageImport.Controls.AddRange(new Control[]
            {
                lblPath, txtPath, btnBrowse, lblFileInfo,
                chkReplace, lblReplaceInfo,
                grpUnits, grpGeom, grpCatalog,
                chkDryRun
            });

            pageLink = new TabPage("Live link") { BackColor = Color.White };
            BuildLinkPage(pageLink);

            tabs = new TabControl { Location = new Point(8, 8), Size = new Size(584, 700) };
            tabs.TabPages.Add(pageImport);
            tabs.TabPages.Add(pageLink);
            tabs.SelectedIndexChanged += delegate { OnTabChanged(); };

            Controls.AddRange(new Control[] { tabs, btnOk, btnCancel });

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            UpdateOkState();
            Theme.Apply(this, "Rhino import", "Import an AxisBridge export, or keep AxisVM and Rhino in step through a live link file.");
        }

        // ==================================================================
        // Live link tab
        // ==================================================================

        private void BuildLinkPage(TabPage page)
        {
            Label lblHead = new Label
            {
                Text = "Live link file (shared with Rhino - AxLink command)",
                Location = new Point(16, 16),
                AutoSize = true,
                Font = new Font(this.Font, FontStyle.Bold)
            };
            txtLinkPath = new TextBox { Location = new Point(16, 40), Size = new Size(445, 24) };
            txtLinkPath.Leave += delegate { ShowLinkStatus(); };
            Button btnLinkBrowse = new Button { Text = "Browse...", Location = new Point(469, 39), Size = new Size(95, 26) };
            btnLinkBrowse.Click += LinkBrowse_Click;

            lblLinkStatus = new Label { Location = new Point(16, 74), Size = new Size(548, 112), ForeColor = SystemColors.GrayText };

            GroupBox grpFrom = new GroupBox { Text = "From Rhino", Location = new Point(16, 196), Size = new Size(548, 162) };
            Label lblFrom = new Label
            {
                Location = new Point(16, 24),
                Size = new Size(360, 100),
                ForeColor = SystemColors.GrayText,
                Text = "Brings this model in line with what Rhino sent last: everything an earlier load created is " +
                       "replaced, so moved or changed objects arrive moved. Lines and elements you drew in AxisVM " +
                       "stay. Units and merge tolerance are read from the link file (Import tab)."
            };
            btnLinkLoad = new Button { Text = "Load from Rhino", Location = new Point(388, 26), Size = new Size(144, 34), Font = new Font(this.Font, FontStyle.Bold) };
            btnLinkLoad.Click += delegate { CloseFor(FormAction.LoadFromRhino); };
            chkRemesh = new CheckBox
            {
                Text = "Mesh the rebuilt domains again afterwards (all at once - slow on large models)",
                Location = new Point(16, 130),
                AutoSize = true,
                Checked = false
            };
            grpFrom.Controls.AddRange(new Control[] { lblFrom, btnLinkLoad, chkRemesh });

            GroupBox grpTo = new GroupBox { Text = "To Rhino", Location = new Point(16, 370), Size = new Size(548, 150) };
            Label lblTo = new Label
            {
                Location = new Point(16, 24),
                Size = new Size(360, 118),
                ForeColor = SystemColors.GrayText,
                Text = "Writes what you changed in AxisVM into the link file: moved nodes, sections, materials, " +
                       "slab type and thickness, deleted elements, and new beams, ribs, trusses and slabs. " +
                       "Then in Rhino: AxLink > Load from AxisVM. Supports, loads, releases and springs made " +
                       "in AxisVM are not sent - add those in Rhino."
            };
            btnLinkSend = new Button { Text = "Send to Rhino", Location = new Point(388, 26), Size = new Size(144, 34) };
            btnLinkSend.Click += delegate { CloseFor(FormAction.SendToRhino); };
            grpTo.Controls.AddRange(new Control[] { lblTo, btnLinkSend });

            page.Controls.AddRange(new Control[] { lblHead, txtLinkPath, btnLinkBrowse, lblLinkStatus, grpFrom, grpTo });
        }

        private void OnTabChanged()
        {
            bool import = tabs.SelectedIndex != tabs.TabPages.IndexOf(pageLink);
            btnOk.Visible = import;
            AcceptButton = import ? btnOk : null;
        }

        private void CloseFor(FormAction action)
        {
            string path = LinkPath;
            if (path.Length == 0)
            {
                MessageBox.Show(this, "Pick the link file first (the one AxLink in Rhino writes).", "Live link", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (action == FormAction.LoadFromRhino)
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "The link file does not exist yet. In Rhino run AxLink and press 'Send to AxisVM' first.", "Live link", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // Units and tolerance come from the link file, exactly as for an import.
                txtPath.Text = path;
                PeekFile();
            }
            Action = action;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void LinkBrowse_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Pick or create the live link file";
                dlg.Filter = "AxisBridge link (*.axlink.json)|*.axlink.json|JSON (*.json)|*.json|All files (*.*)|*.*";
                dlg.OverwritePrompt = false;
                try
                {
                    string dir = Path.GetDirectoryName(LinkPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                    if (LinkPath.Length > 0) dlg.FileName = Path.GetFileName(LinkPath);
                }
                catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                txtLinkPath.Text = dlg.FileName;
                ShowLinkStatus();
            }
        }

        /// <summary>Who sent what and when, and what is waiting on which side.</summary>
        private void ShowLinkStatus()
        {
            string path = LinkPath;
            lblLinkStatus.ForeColor = SystemColors.GrayText;
            if (path.Length == 0) { lblLinkStatus.Text = "Pick the link file AxLink writes in Rhino (or a new file name to start one from AxisVM)."; return; }
            if (!File.Exists(path)) { lblLinkStatus.Text = "This file does not exist yet. 'Send to Rhino' creates it; Rhino's AxLink can then use the same file."; return; }

            try
            {
                JsonValue root = LinkFile.Read(path);
                LinkFile.Status s = LinkFile.ReadStatus(root);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("Rhino sent:      " + (s.RhinoSent.Length > 0 ? LinkFile.Local(s.RhinoSent) : "nothing yet"));
                sb.AppendLine("AxisVM sent:     " + (s.AxisVmSent.Length > 0 ? string.Format("{0} ({1} change(s))", LinkFile.Local(s.AxisVmSent), s.AxisVmChanges) : "nothing yet"));
                sb.AppendLine("Loaded here:     " + (s.AxisVmBasedOnRhino.Length > 0 ? "Rhino's send of " + LinkFile.Local(s.AxisVmBasedOnRhino) : "not yet"));
                if (s.RhinoNewer) sb.AppendLine("-> Rhino has sent a newer model: Load from Rhino.");
                if (s.AxisVmPending) sb.AppendLine("-> AxisVM changes are waiting in the file until Rhino loads them (AxLink > Load from AxisVM).");
                lblLinkStatus.Text = sb.ToString();
                if (s.RhinoNewer || s.AxisVmPending) lblLinkStatus.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                lblLinkStatus.ForeColor = Color.Firebrick;
                lblLinkStatus.Text = "This file could not be read: " + ex.Message;
            }
        }

        /// <summary>Where the catalog is and when it was written, or that it has not been exported yet.</summary>
        private static string CatalogStatus()
        {
            string folder = CatalogExport.DefaultFolder();
            string file = Path.Combine(folder, CatalogExport.SectionsFile);
            return File.Exists(file)
                ? string.Format("Last exported {0:yyyy-MM-dd HH:mm} to {1}", File.GetLastWriteTime(file), folder)
                : "Not exported yet. Folder: " + folder;
        }

        private void Catalog_Click(object sender, EventArgs e)
        {
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                string summary = exportCatalog();
                lblCatalogInfo.Text = CatalogStatus();
                MessageBox.Show(this, summary + "\r\n\r\nIn Rhino: AxLibrary > Materials or Sections > 'From AxisVM catalog...'.",
                    "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The catalog could not be exported:\r\n\r\n" + ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = old; }
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
                ShowEarlierImport();
                UpdateOkState();
                return;
            }

            try
            {
                JsonValue root = LinkFile.RhinoPart(JsonValue.Parse(File.ReadAllText(path)));

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

            ShowEarlierImport();
            UpdateOkState();
        }

        /// <summary>
        /// Looks in the model for what an earlier import of the picked file created, and turns the update
        /// option on when there is something to update.
        /// </summary>
        private void ShowEarlierImport()
        {
            string path = JsonPath;
            LinkedItems found = null;
            if (findLinked != null && !string.IsNullOrEmpty(path))
            {
                try { found = findLinked(path); } catch { found = null; }
            }

            if (found == null || found.Total == 0)
            {
                chkReplace.Enabled = false;
                chkReplace.Checked = false;
                lblReplaceInfo.Text = string.IsNullOrEmpty(path)
                    ? ""
                    : "This model holds nothing from an earlier import of this file, so everything is imported as new.";
                return;
            }

            chkReplace.Enabled = true;
            chkReplace.Checked = true;
            lblReplaceInfo.Text = string.Format(
                "Found from '{0}': {1}. These are deleted and rebuilt from the file, so moved or changed objects arrive moved. " +
                "Loads or supports added to them in AxisVM go with them.",
                Path.GetFileName(path), found);
        }

        private void UpdateOkState()
        {
            if (btnOk == null) return;
            btnOk.Enabled = !string.IsNullOrEmpty(JsonPath);
        }
    }
}
