using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AxisBridge.Library;

namespace AxisBridge.UI
{
    /// <summary>
    /// Edits the document library: materials, cross-sections and load cases. Whatever is defined here
    /// shows up in the AxLine / AxDomain / AxPoint dropdowns and is exported to AxisVM.
    /// Works on a copy; the caller receives <see cref="Result"/> only when the user presses OK.
    /// </summary>
    public class LibraryForm : Form
    {
        public const int TabMaterials = 0;
        public const int TabSections = 1;
        public const int TabLoadCases = 2;

        public AxLibrary Result { get; private set; }

        private readonly DataGridView gridMat;
        private readonly DataGridView gridSec;
        private readonly DataGridView gridLc;

        public LibraryForm(AxLibrary source, int initialTab)
        {
            Theme.Dialog(this, "AxisBridge library", new Size(760, 520));

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };

            // ---- Materials ------------------------------------------------------
            gridMat = MakeGrid();
            gridMat.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name (AxisVM catalog grade)", FillWeight = 200 });
            var fam = new DataGridViewComboBoxColumn { Name = "family", HeaderText = "Family", FlatStyle = FlatStyle.Flat, FillWeight = 100 };
            fam.Items.AddRange("Steel", "Concrete", "Timber", "Other");
            gridMat.Columns.Add(fam);
            foreach (LibMaterial m in source.Materials) gridMat.Rows.Add(m.Name, Family(m.Family));

            var matPresets = new ContextMenuStrip();
            foreach (string[] p in AxLibrary.MaterialPresets)
            {
                string[] preset = p;
                matPresets.Items.Add(preset[0] + "  " + preset[1], null, (s, e) => AddUnique(gridMat, preset[1], preset[0]));
            }
            tabs.TabPages.Add(Page("Materials", gridMat,
                "Names are looked up in the AxisVM material catalog (S355 and 'S 355' both work). Elements reference the name. " +
                "'From AxisVM catalog' lists every Eurocode grade exactly as AxisVM spells it (exported once from AxisVM > Rhino Import).",
                () => gridMat.Rows.Add("", "Steel"), matPresets, PickMaterials));

            // ---- Sections -------------------------------------------------------
            gridSec = MakeGrid();
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", FillWeight = 140 });
            var shape = new DataGridViewComboBoxColumn { Name = "shape", HeaderText = "Shape", FlatStyle = FlatStyle.Flat, FillWeight = 110 };
            shape.Items.AddRange(LibSection.Shapes);
            gridSec.Columns.Add(shape);
            gridSec.Columns.Add(NumCol("b", "b [mm]"));
            gridSec.Columns.Add(NumCol("h", "h or d [mm]"));
            gridSec.Columns.Add(NumCol("t", "t [mm]"));
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Definition", ReadOnly = true, FillWeight = 170, DefaultCellStyle = { ForeColor = Theme.HintColor } });
            // Where a catalog profile came from (catalog picker) - travels with the row, not shown.
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "cshape", Visible = false });
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "ctable", Visible = false });
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "cfile", Visible = false });
            gridSec.Columns.Add(new DataGridViewTextBoxColumn { Name = "cname", Visible = false });
            foreach (LibSection s in source.Sections) AddSectionRow(s);
            gridSec.CellEndEdit += (s, e) => RefreshSectionRow(e.RowIndex);
            gridSec.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (gridSec.IsCurrentCellDirty && gridSec.CurrentCell is DataGridViewComboBoxCell)
                    gridSec.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            gridSec.CellValueChanged += (s, e) => { if (e.RowIndex >= 0 && gridSec.Columns[e.ColumnIndex].Name == "shape") RefreshSectionRow(e.RowIndex); };

            var secPresets = new ContextMenuStrip();
            foreach (string p in AxLibrary.SectionPresets)
            {
                string preset = p;
                secPresets.Items.Add(preset, null, (s, e) => { if (!HasName(gridSec, preset)) AddSectionRow(new LibSection { Name = preset, Shape = LibSection.Catalog }); });
            }
            tabs.TabPages.Add(Page("Sections", gridSec,
                "Catalog: looked up in the AxisVM profile catalog (IPE300, HEA200...); picked 'From AxisVM catalog' = taken from that exact table. " +
                "Other shapes are built from the dimensions (mm). Blank name = auto-name.",
                () => AddSectionRow(new LibSection { Shape = LibSection.Rectangle, B = 300, H = 500 }), secPresets, PickSections));

            // ---- Load cases -----------------------------------------------------
            gridLc = MakeGrid();
            gridLc.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Load case", FillWeight = 120 });
            gridLc.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Description (optional)", FillWeight = 220 });
            foreach (LibLoadCase c in source.LoadCases) gridLc.Rows.Add(c.Name, c.Description);
            var lcPresets = new ContextMenuStrip();
            foreach (string p in new[] { "Dead", "Finishes", "Live", "Snow", "Wind X", "Wind Y", "Installation" })
            {
                string preset = p;
                lcPresets.Items.Add(preset, null, (s, e) => AddUnique(gridLc, preset, ""));
            }
            tabs.TabPages.Add(Page("Load cases", gridLc,
                "Created in AxisVM in this order on import. Every load grid offers these names.",
                () => gridLc.Rows.Add("", ""), lcPresets, null));

            tabs.SelectedIndex = Math.Max(0, Math.Min(2, initialTab));

            Button ok, cancel;
            Panel buttons = Theme.Buttons(this, "OK", out ok, out cancel);
            ok.Click += (s, e) => Result = Collect();

            Controls.Add(tabs);
            Controls.Add(Theme.Header("Document library", "Materials, cross-sections and load cases stored in this .3dm"));
            Controls.Add(Theme.Separator());
            Controls.Add(buttons);
        }

        // ==================================================================
        // Layout helpers
        // ==================================================================

        private static DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                RowHeadersWidth = 24,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
                SelectionMode = DataGridViewSelectionMode.CellSelect
            };
            g.DataError += (s, e) => { e.ThrowException = false; };
            return g;
        }

        private static DataGridViewTextBoxColumn NumCol(string name, string header)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = 60,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            };
        }

        private static TabPage Page(string title, DataGridView grid, string help, Action add, ContextMenuStrip presets, Action fromCatalog)
        {
            var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
            var bAdd = new Button { Text = "+ Add", AutoSize = true, Height = 26 };
            var bPreset = new Button { Text = "Add preset ▾", AutoSize = true, Height = 26 };
            var bDel = new Button { Text = "Remove", AutoSize = true, Height = 26 };
            bAdd.Click += (s, e) => add();
            bPreset.Click += (s, e) => presets.Show(bPreset, new Point(0, bPreset.Height));
            bDel.Click += (s, e) =>
            {
                grid.EndEdit();
                var rows = new HashSet<int>();
                foreach (DataGridViewCell c in grid.SelectedCells) rows.Add(c.RowIndex);
                var sorted = new List<int>(rows);
                sorted.Sort();
                for (int i = sorted.Count - 1; i >= 0; i--) grid.Rows.RemoveAt(sorted[i]);
            };
            bar.Controls.AddRange(new Control[] { bAdd, bPreset, bDel });
            if (fromCatalog != null)
            {
                var bCat = new Button { Text = "From AxisVM catalog...", AutoSize = true, Height = 26 };
                bCat.Click += (s, e) => fromCatalog();
                bar.Controls.Add(bCat);
            }

            var hint = Theme.Hint(help);
            hint.Dock = DockStyle.Bottom;
            hint.AutoSize = false;   // fixed height so the text wraps to the dialog width instead of being cut off
            hint.Height = 40;
            hint.Padding = new Padding(0, 4, 0, 0);

            page.Controls.Add(grid);
            page.Controls.Add(hint);
            page.Controls.Add(bar);
            return page;
        }

        private static string Family(string f)
        {
            switch ((f ?? "").Trim().ToLowerInvariant())
            {
                case "steel": return "Steel";
                case "concrete": return "Concrete";
                case "timber": return "Timber";
                default: return "Other";
            }
        }

        private static bool HasName(DataGridView g, string name)
        {
            foreach (DataGridViewRow r in g.Rows)
                if (string.Equals(Convert.ToString(r.Cells["name"].Value)?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void AddUnique(DataGridView g, string name, string second)
        {
            if (HasName(g, name)) return;
            g.Rows.Add(name, second);
        }

        private void AddSectionRow(LibSection s)
        {
            int i = gridSec.Rows.Add(s.Name, s.Shape, Num(s.B), Num(s.H), Num(s.T), s.Describe(),
                s.CatalogShape > 0 ? s.CatalogShape.ToString(CultureInfo.InvariantCulture) : "", s.CatalogTable ?? "", s.CatalogFile ?? "", s.CatalogName ?? "");
            RefreshSectionRow(i);
        }

        // ==================================================================
        // AxisVM catalog (exported by the AxisVM Rhino Import add-on)
        // ==================================================================

        /// <summary>The catalog the AxisVM add-on exported; explains how to export it when there is none yet.</summary>
        private AxCatalog Catalog()
        {
            AxCatalog c = AxCatalog.FindDefault();
            if (c != null) return c;

            MessageBox.Show(this,
                "The AxisVM catalog has not been exported on this computer yet.\r\n\r\n" +
                "In AxisVM open the Rhino Import tool and click 'Export catalog for Rhino' (once; again after an AxisVM update). " +
                "It writes every Eurocode material and cross-section table to\r\n\r\n" + AxCatalog.DefaultFolder() +
                "\r\n\r\nand they are listed here from then on.",
                "AxisBridge library", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        private void PickMaterials()
        {
            AxCatalog c = Catalog();
            if (c == null) return;
            gridMat.EndEdit();
            using (var f = CatalogPickerForm.ForMaterials(c, Names(gridMat)))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (object o in f.Picked)
                {
                    LibMaterial m = AxCatalog.ToLibrary((CatalogMaterial)o);
                    AddUnique(gridMat, m.Name, m.Family);
                }
            }
        }

        private void PickSections()
        {
            AxCatalog c = Catalog();
            if (c == null) return;
            gridSec.EndEdit();
            using (var f = CatalogPickerForm.ForSections(c, Names(gridSec)))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (object o in f.Picked)
                {
                    LibSection s = AxCatalog.ToLibrary((CatalogSection)o);
                    if (!HasName(gridSec, s.Name)) AddSectionRow(s);
                }
            }
        }

        private static List<string> Names(DataGridView g)
        {
            var names = new List<string>();
            foreach (DataGridViewRow r in g.Rows) names.Add((Convert.ToString(r.Cells["name"].Value) ?? "").Trim());
            return names;
        }

        /// <summary>Adds catalog picks the way the picker does, without its dialog (offline preview and checks).</summary>
        public void AddFromCatalogForTest(IEnumerable<CatalogMaterial> materials, IEnumerable<CatalogSection> sections)
        {
            foreach (CatalogMaterial m in materials) { LibMaterial l = AxCatalog.ToLibrary(m); AddUnique(gridMat, l.Name, l.Family); }
            foreach (CatalogSection s in sections) { LibSection l = AxCatalog.ToLibrary(s); if (!HasName(gridSec, l.Name)) AddSectionRow(l); }
        }

        public AxLibrary CollectForTest() { return Collect(); }

        private static string Num(double v) { return v > 0 ? v.ToString("0.###", CultureInfo.InvariantCulture) : ""; }

        private static double Parse(object o)
        {
            double d;
            return double.TryParse(Convert.ToString(o)?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private LibSection ReadSection(DataGridViewRow r)
        {
            var s = new LibSection
            {
                Name = (Convert.ToString(r.Cells["name"].Value) ?? "").Trim(),
                Shape = Convert.ToString(r.Cells["shape"].Value) ?? LibSection.Catalog,
                B = Parse(r.Cells["b"].Value),
                H = Parse(r.Cells["h"].Value),
                T = Parse(r.Cells["t"].Value)
            };
            if (s.Shape == LibSection.Catalog)
            {
                int shape;
                int.TryParse(Convert.ToString(r.Cells["cshape"].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out shape);
                string table = Convert.ToString(r.Cells["ctable"].Value) ?? "", file = Convert.ToString(r.Cells["cfile"].Value) ?? "";
                if (shape > 0) s.CatalogShape = shape;
                if (table.Length > 0) s.CatalogTable = table;
                if (file.Length > 0) s.CatalogFile = file;
                string exact = Convert.ToString(r.Cells["cname"].Value) ?? "";
                if (exact.Length > 0 && exact != s.Name) s.CatalogName = exact;
            }
            return s;
        }

        /// <summary>Greys out dimension cells the shape does not use and refreshes the definition text.</summary>
        private void RefreshSectionRow(int index)
        {
            if (index < 0 || index >= gridSec.Rows.Count) return;
            DataGridViewRow r = gridSec.Rows[index];
            LibSection s = ReadSection(r);
            bool b = s.Shape == LibSection.Rectangle || s.Shape == LibSection.RectHollow;
            bool h = s.Shape != LibSection.Catalog;
            bool t = s.Shape == LibSection.Pipe || s.Shape == LibSection.RectHollow;
            SetEnabled(r.Cells["b"], b);
            SetEnabled(r.Cells["h"], h);
            SetEnabled(r.Cells["t"], t);
            r.Cells["desc"].Value = s.Describe();
        }

        private static void SetEnabled(DataGridViewCell c, bool on)
        {
            c.ReadOnly = !on;
            c.Style.BackColor = on ? SystemColors.Window : Color.FromArgb(236, 236, 236);
        }

        // ==================================================================
        // Result
        // ==================================================================

        private AxLibrary Collect()
        {
            gridMat.EndEdit(); gridSec.EndEdit(); gridLc.EndEdit();
            var lib = new AxLibrary();

            foreach (DataGridViewRow r in gridMat.Rows)
            {
                string n = (Convert.ToString(r.Cells["name"].Value) ?? "").Trim();
                if (n.Length > 0) lib.Materials.Add(new LibMaterial { Name = n, Family = Convert.ToString(r.Cells["family"].Value) ?? "" });
            }

            foreach (DataGridViewRow r in gridSec.Rows)
            {
                LibSection s = ReadSection(r);
                if (s.Name.Length == 0) s.Name = s.AutoName();
                if (!string.IsNullOrWhiteSpace(s.Name)) lib.Sections.Add(s);
            }

            foreach (DataGridViewRow r in gridLc.Rows)
            {
                string n = (Convert.ToString(r.Cells["name"].Value) ?? "").Trim().Replace(";", " ").Replace("=", " ");
                if (n.Length > 0) lib.LoadCases.Add(new LibLoadCase { Name = n, Description = Convert.ToString(r.Cells["desc"].Value) ?? "" });
            }

            lib.Normalize();
            return lib;
        }
    }
}
