using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// One file shared with Rhino - an AxExport file or an AxLink link file - and the two directions:
    /// "From Rhino" (build or update this model from it) and "To Rhino" (send what changed in AxisVM).
    ///
    /// The form peeks at the chosen file as soon as it is picked, so the unit and tolerance boxes arrive
    /// pre-filled with what Rhino recorded, and the element counts, the document library and the link
    /// status are visible before anything is written. Everything stays overridable.
    /// </summary>
    public class RhinoImportForm : Form
    {
        public enum FormAction { Load, SendToRhino }

        /// <summary>What the dialog was closed for (with DialogResult.OK).</summary>
        public FormAction Action { get; private set; }

        public string FilePath { get { return txtPath.Text.Trim(); } }
        public bool DryRun { get { return chkDryRun.Checked; } }

        /// <summary>Connect crossing lines / points on lines before the attributes are applied.</summary>
        public bool AutoIntersect { get { return chkAutoIntersect.Checked; } }
        /// <summary>Mesh the domains an update rebuilt again (off by default).</summary>
        public bool RemeshRebuilt { get { return chkRemesh.Checked && chkRemesh.Enabled; } }

        /// <summary>Update what an earlier load of this file built (instead of adding a second copy).</summary>
        public bool ReplacePrevious { get { return chkReplace.Checked && chkReplace.Enabled; } }

        /// <summary>Update only what changed in Rhino since the last load (LinkDiff); off = replace everything.</summary>
        public bool OnlyChanges { get { return ReplacePrevious && chkOnlyChanges.Checked; } }

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
        private Label lblFileInfo;
        private ComboBox cboLength;
        private ComboBox cboForce;
        private TextBox txtTolerance;
        private CheckBox chkDryRun;
        private CheckBox chkAutoIntersect;
        private CheckBox chkReplace;
        private CheckBox chkOnlyChanges;
        private CheckBox chkRemesh;
        private Label lblReplaceInfo;
        private Button btnLoad;
        /// <summary>What an earlier load of the picked file (or a Send to Rhino with it) left in the model.</summary>
        private LinkedItems earlierLoad;
        private Button btnSend;
        private Button btnCancel;
        private TabControl tabs;
        private TabPage pageFrom;
        private TabPage pageTo;

        /// <summary>Writes the catalog CSVs for Rhino and returns a one-line summary (set by RhinoImportLogic).</summary>
        private readonly Func<string> exportCatalog;
        private Label lblCatalogInfo;

        /// <summary>What an earlier load of a file left in the open model (set by RhinoImportLogic).</summary>
        private readonly Func<string, LinkedItems> findLinked;

        public RhinoImportForm(string initialPath) : this(initialPath, null, null, false) { }

        public RhinoImportForm(string initialPath, Func<string> exportCatalog, Func<string, LinkedItems> findLinked, bool showSend)
        {
            this.exportCatalog = exportCatalog;
            this.findLinked = findLinked;
            InitializeComponent();

            if (!string.IsNullOrEmpty(initialPath)) txtPath.Text = initialPath;
            PeekFile();
            if (showSend) tabs.SelectedTab = pageTo;
            // Not from tabs.SelectedTab: before the window exists it does not report the first page yet.
            ShowTab(!showSend);
        }

        private void InitializeComponent()
        {
            Theme.Initialize(this);
            Text = "Rhino Import";
            ClientSize = new Size(600, 640);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // ---- the file shared with Rhino -------------------------------
            Label lblPath = new Label
            {
                Text = "File shared with Rhino (AxExport .json or AxLink .axlink.json)",
                Location = new Point(16, 16),
                AutoSize = true,
                Font = new Font(this.Font, FontStyle.Bold)
            };
            txtPath = new TextBox { Location = new Point(16, 40), Size = new Size(465, 24) };
            txtPath.Leave += delegate { PeekFile(); };
            Button btnBrowse = new Button { Text = "Browse...", Location = new Point(489, 39), Size = new Size(95, 26) };
            btnBrowse.Click += Browse_Click;
            lblFileInfo = new Label { Location = new Point(16, 70), Size = new Size(568, 92), ForeColor = SystemColors.GrayText };

            // ---- From Rhino --------------------------------------------------
            pageFrom = new TabPage("From Rhino") { BackColor = Color.White };

            chkReplace = new CheckBox
            {
                Text = "Update what an earlier load of this file built (no second copy)",
                Location = new Point(12, 12),
                AutoSize = true,
                Enabled = false
            };
            chkReplace.CheckedChanged += delegate { UpdateUpdateOptions(); };
            chkOnlyChanges = new CheckBox
            {
                Text = "Only apply what changed in Rhino (keep everything else as it is in AxisVM)",
                Location = new Point(30, 36),
                AutoSize = true,
                Checked = true
            };
            chkOnlyChanges.CheckedChanged += delegate { UpdateUpdateOptions(); };
            chkRemesh = new CheckBox
            {
                Text = "Mesh the rebuilt domains again afterwards (all at once - slow on large models)",
                Location = new Point(30, 60),
                AutoSize = true
            };
            lblReplaceInfo = new Label { Location = new Point(30, 84), Size = new Size(530, 64), ForeColor = SystemColors.GrayText };

            GroupBox grpUnits = new GroupBox { Text = "Units of the Rhino attributes", Location = new Point(12, 152), Size = new Size(552, 116) };
            Label lblLength = new Label { Text = "Length:", Location = new Point(16, 27), AutoSize = true };
            cboLength = new ComboBox { Location = new Point(76, 23), Size = new Size(80, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cboLength.Items.AddRange(AxUnits.LengthUnitNames);
            cboLength.SelectedIndex = 0;
            Label lblForce = new Label { Text = "Loads:", Location = new Point(178, 27), AutoSize = true };
            cboForce = new ComboBox { Location = new Point(228, 23), Size = new Size(310, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cboForce.Items.AddRange(AxUnits.ForceUnitNames);
            cboForce.SelectedIndex = 0;
            Label lblUnitNote = new Label
            {
                Location = new Point(16, 54),
                Size = new Size(524, 56),
                ForeColor = SystemColors.GrayText,
                Text = "Length = the Rhino model unit (coordinates, thickness, eccentricity, load positions). " +
                       "Loads = the unit of load values and stiffnesses; the AxisBridge dialogs always write kN, kN/m, kN/m², kNm - " +
                       "choose 'N, mm' only for attributes typed by hand in N, N/mm, N/mm², Nmm."
            };
            grpUnits.Controls.AddRange(new Control[] { lblLength, cboLength, lblForce, cboForce, lblUnitNote });

            GroupBox grpGeom = new GroupBox { Text = "Geometry", Location = new Point(12, 276), Size = new Size(552, 104) };
            Label lblTol = new Label { Text = "Merge nodes closer than", Location = new Point(16, 27), AutoSize = true };
            txtTolerance = new TextBox { Location = new Point(175, 23), Size = new Size(65, 24), Text = "1.0" };
            Label lblTolUnit = new Label { Text = "mm", Location = new Point(245, 27), AutoSize = true };
            Label lblTolNote = new Label
            {
                Location = new Point(285, 22),
                Size = new Size(255, 36),
                ForeColor = SystemColors.GrayText,
                Text = "A beam end and a slab corner within this distance become one node."
            };
            chkAutoIntersect = new CheckBox
            {
                Text = "Connect crossing lines and points lying on lines",
                Location = new Point(16, 58),
                AutoSize = true,
                Checked = true
            };
            Label lblIntersectNote = new Label
            {
                Location = new Point(34, 80),
                Size = new Size(510, 20),
                ForeColor = SystemColors.GrayText,
                Text = "Each Rhino curve stays one member. Springs, links and arcs are never split."
            };
            grpGeom.Controls.AddRange(new Control[] { lblTol, txtTolerance, lblTolUnit, lblTolNote, chkAutoIntersect, lblIntersectNote });

            chkDryRun = new CheckBox
            {
                Text = "Dry run (check the attributes and report only - change nothing)",
                Location = new Point(14, 390),
                AutoSize = true
            };
            pageFrom.Controls.AddRange(new Control[] { chkReplace, chkOnlyChanges, chkRemesh, lblReplaceInfo, grpUnits, grpGeom, chkDryRun });

            // ---- To Rhino ----------------------------------------------------
            pageTo = new TabPage("To Rhino") { BackColor = Color.White };
            Label lblTo = new Label
            {
                Location = new Point(12, 12),
                Size = new Size(540, 120),
                ForeColor = SystemColors.GrayText,
                Text = "Writes what you changed in AxisVM into the file (it becomes a link file): moved nodes, sections, materials, " +
                       "slab type and thickness, rigid diaphragms, supports, releases, springs, links, edge hinges, surface supports, " +
                       "loads of elements drawn in AxisVM, deleted elements, and new members, slabs and load panels - each with an id " +
                       "Rhino keeps, so loading Rhino's model afterwards leaves them alone. Then in Rhino: AxLink > Load from AxisVM."
            };
            btnSend = new Button { Text = "Send to Rhino", Location = new Point(408, 140), Size = new Size(144, 34), Font = new Font(this.Font, FontStyle.Bold) };
            btnSend.Click += delegate { CloseFor(FormAction.SendToRhino); };

            GroupBox grpCatalog = new GroupBox { Text = "Material and section catalog for Rhino", Location = new Point(12, 196), Size = new Size(552, 112) };
            Label lblCatalogNote = new Label
            {
                Location = new Point(16, 22),
                Size = new Size(370, 50),
                ForeColor = SystemColors.GrayText,
                Text = "Lists every Eurocode material and cross-section table of this AxisVM in Rhino " +
                       "(AxLibrary > From AxisVM catalog). Once per computer; the model is not changed."
            };
            Button btnCatalog = new Button { Text = "Export catalog for Rhino", Location = new Point(390, 24), Size = new Size(150, 30) };
            lblCatalogInfo = new Label { Location = new Point(16, 76), Size = new Size(524, 32), ForeColor = SystemColors.GrayText, Text = CatalogStatus() };
            btnCatalog.Enabled = exportCatalog != null;
            btnCatalog.Click += Catalog_Click;
            grpCatalog.Controls.AddRange(new Control[] { lblCatalogNote, btnCatalog, lblCatalogInfo });
            pageTo.Controls.AddRange(new Control[] { lblTo, btnSend, grpCatalog });

            tabs = new TabControl { Location = new Point(8, 168), Size = new Size(584, 444) };
            tabs.TabPages.Add(pageFrom);
            tabs.TabPages.Add(pageTo);
            tabs.SelectedIndexChanged += delegate { OnTabChanged(); };

            // ---- buttons -------------------------------------------------
            btnLoad = new Button { Text = "Load from Rhino", Location = new Point(352, 598), Size = new Size(130, 32), Font = new Font(this.Font, FontStyle.Bold) };
            btnLoad.Click += delegate { CloseFor(FormAction.Load); };
            btnCancel = new Button { Text = "Cancel", Location = new Point(494, 598), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { lblPath, txtPath, btnBrowse, lblFileInfo, tabs, btnLoad, btnCancel });
            // Theme.Apply moves only the accept and cancel buttons into the button bar - set both before it runs.
            AcceptButton = btnLoad;
            CancelButton = btnCancel;

            UpdateUpdateOptions();
            Theme.Apply(this, "Rhino import", "Build this model from Rhino, update it with only what changed, or send AxisVM's changes back.");
        }

        private void OnTabChanged()
        {
            ShowTab(tabs.SelectedTab != pageTo);
        }

        private void ShowTab(bool from)
        {
            btnLoad.Visible = from;
            AcceptButton = from ? btnLoad : btnSend;
        }

        private void CloseFor(FormAction action)
        {
            string path = FilePath;
            if (path.Length == 0)
            {
                MessageBox.Show(this, "Pick the file first: the one AxExport or AxLink writes in Rhino" +
                    (action == FormAction.SendToRhino ? ", or a new file name to start a link from AxisVM." : "."), "Rhino", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (action == FormAction.Load && !File.Exists(path))
            {
                MessageBox.Show(this, "This file does not exist yet. In Rhino run AxExport, or AxLink > Send to AxisVM, first.", "Rhino", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // The model already holds what this file built or sent to Rhino: a plain import would build a second copy on
            // top of it (never wanted, and AxisVM can crash on it). Offer the update instead of failing silently.
            if (action == FormAction.Load && !ReplacePrevious && !chkDryRun.Checked && earlierLoad != null && earlierLoad.Total > 0)
            {
                DialogResult r = MessageBox.Show(this,
                    "This model already holds the elements of '" + Path.GetFileName(path) + "' (" + earlierLoad + ") - loaded from it before, " +
                    "or sent to Rhino with it.\r\n\r\nLoading without 'Update what an earlier load built' would build a second copy on top of " +
                    "them, which AxisVM may not survive.\r\n\r\nYes = update them (only what changed in Rhino) - recommended.\r\nNo = cancel.",
                    "Rhino import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                chkReplace.Checked = true;
                chkOnlyChanges.Checked = true;
            }
            Action = action;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateUpdateOptions()
        {
            chkOnlyChanges.Enabled = ReplacePrevious;
            chkRemesh.Enabled = ReplacePrevious;
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
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                // A save dialog, so a new link file can be named here too (Send to Rhino creates it).
                dlg.Title = "Pick the file shared with Rhino";
                dlg.Filter = "AxisBridge files (*.json)|*.json|All files (*.*)|*.*";
                dlg.OverwritePrompt = false;
                try
                {
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                    if (FilePath.Length > 0) dlg.FileName = Path.GetFileName(FilePath);
                }
                catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                txtPath.Text = dlg.FileName;
                PeekFile();
            }
        }

        /// <summary>
        /// Reads the header, the element counts and the link status, to pre-fill the unit and tolerance boxes and show
        /// what is in the file. A file that will not parse is reported here rather than after the user has committed.
        /// </summary>
        private void PeekFile()
        {
            string path = FilePath;
            lblFileInfo.ForeColor = SystemColors.GrayText;
            if (path.Length == 0) { lblFileInfo.Text = "Pick the file AxExport or AxLink wrote in Rhino - or name a new one and Send to Rhino to start a link from AxisVM."; ShowEarlierLoad(); return; }
            if (!File.Exists(path)) { lblFileInfo.Text = "This file does not exist yet. 'Send to Rhino' creates it; Rhino's AxLink can then use the same file."; ShowEarlierLoad(); return; }

            try
            {
                JsonValue file = LinkFile.Read(path);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                bool attention = false;
                JsonValue root = null;
                try { root = LinkFile.RhinoPart(file); } catch (FormatException ex) { sb.AppendLine(ex.Message); }

                if (root != null)
                {
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

                    JsonValue lib = root["library"];
                    int warnings = root["warnings"] != null ? root["warnings"].Count : 0;
                    bool unitOk = AxUnits.IsSupportedRhinoUnit(unitSystem);
                    sb.AppendLine(string.Format("{0} points, {1} curves, {2} surfaces{3}{4}.  Rhino units: {5}.{6}",
                        root["points"] != null ? root["points"].Count : 0, root["lines"] != null ? root["lines"].Count : 0, root["domains"] != null ? root["domains"].Count : 0,
                        lib != null ? string.Format("; library: {0} load case(s), {1} section(s)", lib["loadCases"] != null ? lib["loadCases"].Count : 0, lib["sections"] != null ? lib["sections"].Count : 0) : "",
                        warnings > 0 ? string.Format("; {0} export warning(s)", warnings) : "",
                        string.IsNullOrEmpty(unitSystem) ? "not recorded" : unitSystem,
                        unitOk ? "" : "  Only millimetres and metres are supported - check Length below."));
                    if (!unitOk) attention = true;
                }

                if (LinkFile.IsLink(file))
                {
                    LinkFile.Status s = LinkFile.ReadStatus(file);
                    sb.AppendLine("Rhino sent " + (s.RhinoSent.Length > 0 ? LinkFile.Local(s.RhinoSent) : "nothing yet") +
                                  "; AxisVM sent " + (s.AxisVmSent.Length > 0 ? string.Format("{0} ({1} change(s))", LinkFile.Local(s.AxisVmSent), s.AxisVmChanges) : "nothing yet") +
                                  "; loaded here: " + (s.AxisVmBasedOnRhino.Length > 0 ? "Rhino's send of " + LinkFile.Local(s.AxisVmBasedOnRhino) : "not yet") + ".");
                    if (s.RhinoNewer) { sb.AppendLine("-> Rhino has sent a newer model: Load from Rhino."); attention = true; }
                    if (s.AxisVmPending) { sb.AppendLine("-> AxisVM changes wait in the file until Rhino loads them (AxLink > Load from AxisVM)."); attention = true; }
                }
                lblFileInfo.Text = sb.ToString().TrimEnd();
                if (attention) lblFileInfo.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                lblFileInfo.ForeColor = Color.Firebrick;
                lblFileInfo.Text = "This file could not be read: " + ex.Message;
            }
            ShowEarlierLoad();
        }

        /// <summary>
        /// Looks in the model for what an earlier load of the picked file built, and turns the update option on when
        /// there is something to update.
        /// </summary>
        private void ShowEarlierLoad()
        {
            string path = FilePath;
            LinkedItems found = null;
            if (findLinked != null && path.Length > 0)
            {
                try { found = findLinked(path); } catch { found = null; }
            }
            earlierLoad = found;

            if (found == null || found.Total == 0)
            {
                chkReplace.Enabled = false;
                chkReplace.Checked = false;
                lblReplaceInfo.Text = path.Length == 0 ? "" : "This model holds nothing from an earlier load of this file, so everything is built as new.";
            }
            else
            {
                chkReplace.Enabled = true;
                chkReplace.Checked = true;
                lblReplaceInfo.Text = string.Format(
                    "Found from '{0}': {1}. With 'only what changed', objects Rhino did not change stay exactly as they are here " +
                    "(edge hinges, supports, meshes and loads added in AxisVM included); changed ones are rebuilt with their neighbours. " +
                    "Without it, everything from the file is deleted and rebuilt.",
                    Path.GetFileName(path), found);
            }
            UpdateUpdateOptions();
        }
    }
}
