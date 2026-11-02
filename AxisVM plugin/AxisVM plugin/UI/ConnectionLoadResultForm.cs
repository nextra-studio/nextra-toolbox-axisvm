using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Result window of the Connection Load Extractor (modeless):
    ///  - "Summary": Max+ / Min- over all reported ends for each of the six components (12 rows);
    ///  - one tab per component (Fx .. Mz or N .. Mz): Max+ / Min- per member end, plus the "All ends" rows;
    ///  - "All": every member end x combination;
    ///  - "Checks": the self-checks.
    /// Every critical row carries the full concurrent vector of its combination. CSV / clipboard export and a
    /// button that selects the corresponding nodes and lines in AxisVM for a visual check.
    /// The panel on the right switches the coordinate system / custom angles: every table is recomputed from the
    /// loads already read (no new AxisVM call), and the axis triad on the model follows when it is shown.
    /// </summary>
    public class ConnectionLoadResultForm : Form
    {
        private readonly ExtractionResult result;
        private readonly Action<IEnumerable<int>, IEnumerable<int>> selectInModel; // (nodes, lines)
        private readonly Func<LoadSystem, CustomAxes, string> showAxes;            // null = no model to draw on
        private readonly Action hideAxes;
        private bool axesShown;

        private TabControl tabs;
        private ComboBox cmbAxes;
        private AxisRotationEditor editor;
        private Button btnShowAxes;
        private readonly Timer recomputeTimer = new Timer { Interval = 300 };

        /// <summary>What a tab shows: its grid (null for Checks) and the text it copies.</summary>
        private class TabContent
        {
            public DataGridView Grid;
            public Func<string> CopyText;
        }
        private readonly Dictionary<TabPage, TabContent> content = new Dictionary<TabPage, TabContent>();

        // Highlight colours: governing over all ends, and rows that are critical for their own end
        private static readonly Color OverallColour = Color.FromArgb(255, 230, 160);
        private static readonly Color CriticalColour = Color.FromArgb(255, 248, 210);

        // Column layout of the critical tables (Summary, component tabs, critical export)
        private const int ColValue = 8;        // the critical value
        private const int ColFirstComp = 9;    // first of the six concurrent components

        public ConnectionLoadResultForm(ExtractionResult result, Action<IEnumerable<int>, IEnumerable<int>> selectInModel)
            : this(result, selectInModel, null, null, false) { }

        public ConnectionLoadResultForm(ExtractionResult result, Action<IEnumerable<int>, IEnumerable<int>> selectInModel,
            Func<LoadSystem, CustomAxes, string> showAxes, Action hideAxes, bool axesShown)
        {
            this.result = result;
            this.selectInModel = selectInModel;
            this.showAxes = showAxes;
            this.hideAxes = hideAxes;
            InitializeComponent();

            // The dialog had the triad on: redraw it for the final choice once the window is up
            if (axesShown && showAxes != null) Shown += delegate { ToggleAxes(); };
            FormClosed += delegate
            {
                recomputeTimer.Stop();
                if (this.axesShown) hideAxes();
            };
        }

        private void InitializeComponent()
        {
            Theme.Initialize(this);
            Text = "Connection Loads - " + result.AxesText;
            ClientSize = new Size(1250, 600);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 520);
            MinimizeBox = false;

            // Tables on the left, the axes panel on the right; one container so the themed layout fills it
            Panel main = new Panel { Location = new Point(8, 8), Size = new Size(1234, 500) };
            tabs = new TabControl { Dock = DockStyle.Fill };
            Panel side = new Panel { Dock = DockStyle.Right, Width = 344, Padding = new Padding(12, 4, 4, 4) };
            Label lblAxes = new Label { Text = "Coordinate system (tables update straight away):", Location = new Point(12, 4), AutoSize = true };
            cmbAxes = new ComboBox { Location = new Point(12, 26), Size = new Size(324, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAxes.Items.AddRange(new object[] { "Local (N, Vy, Vz, Tx, My, Mz)", "Global (Fx, Fy, Fz, Mx, My, Mz)", "Custom: rotated global (Fx', Fy', Fz', ...)" });
            cmbAxes.SelectedIndex = result.Axes == LoadSystem.Local ? 0 : result.Axes == LoadSystem.Custom ? 2 : 1;
            editor = new AxisRotationEditor(324, 300) { Location = new Point(12, 60), Value = result.Custom };
            editor.SetSystem(result.Axes, FirstLocalFrame());
            side.Controls.AddRange(new Control[] { lblAxes, cmbAxes, editor });
            main.Controls.Add(tabs);
            main.Controls.Add(side);
            tabs.BringToFront(); // the Fill control docks last, after the side panel took its width

            cmbAxes.SelectedIndexChanged += delegate { AxesChanged(); };
            editor.ValueChanged += delegate { AxesChanged(); };
            recomputeTimer.Tick += delegate { recomputeTimer.Stop(); ApplyAxes(); };

            BuildTabs();

            // Buttons along the bottom
            Button btnExportAll = MakeButton("Export all (CSV)", 8, 150);
            btnExportAll.Click += delegate { FlushAxes(); SaveCsv("ConnectionLoads_all.csv", WithAxes(AllHeader()), WithAxes(AllRows())); };
            Button btnExportCrit = MakeButton("Export critical (CSV)", 164, 150);
            btnExportCrit.Click += delegate { FlushAxes(); SaveCsv("ConnectionLoads_critical.csv", WithAxes(CriticalHeader()), WithAxes(CriticalRows(result.Critical))); };
            Button btnCopy = MakeButton("Copy tab (TSV)", 320, 130);
            btnCopy.Click += delegate { FlushAxes(); CopyActiveTab(); };
            Button btnSelect = MakeButton("Select in model", 456, 130);
            btnSelect.Click += delegate { SelectFromActiveTab(); };
            btnShowAxes = MakeButton("Show axes in model", 592, 150);
            btnShowAxes.Enabled = showAxes != null;
            btnShowAxes.Click += delegate { ToggleAxes(); };

            Label lblHint = new Label
            {
                Text = "Select in model: the highlighted grid rows, or every target node and line if no row is highlighted.",
                Location = new Point(750, 522),
                Size = new Size(400, 30),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // The window is modeless: a button's DialogResult only closes MODAL forms, so close explicitly
            Button btnClose = new Button { Text = "Close", Location = new Point(1162, 520), Size = new Size(80, 27), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnClose.Click += delegate { Close(); };

            // Esc closes too (handled here because AxisVM's message loop does not do dialog-key processing)
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            Controls.AddRange(new Control[] { main, btnExportAll, btnExportCrit, btnCopy, btnSelect, btnShowAxes, lblHint, btnClose });
            Theme.ApplyResults(this, "Connection loads", "Summary, six critical components and all results. Switch or rotate the axes on the right; export CSV or select rows in the model.");
        }

        /// <summary>(Re)fills every tab for the current axes, keeping the tab the user is on.</summary>
        private void BuildTabs()
        {
            int selected = tabs.SelectedIndex;
            tabs.SuspendLayout();
            foreach (TabPage p in tabs.TabPages.Cast<TabPage>().ToList()) { tabs.TabPages.Remove(p); p.Dispose(); }
            content.Clear();
            string[] names = ConnectionLoadMath.Names(result.Axes);

            // Summary first: Max+ / Min- of every component over all ends - the answer the user came for
            List<CriticalRow> overall = result.Critical.Where(c => c.End == null).OrderBy(c => c.Component).ThenBy(c => c.Kind).ToList();
            AddCriticalTab("Summary", overall);

            // One tab per component: per member end, then the governing "All ends" pair
            for (int c = 0; c < 6; c++)
            {
                int comp = c; // captured by the lambda below
                List<CriticalRow> rows = result.Critical.Where(x => x.Component == comp).ToList();
                AddCriticalTab(names[c], rows);
            }

            // Every member end x combination
            DataGridView gridAll = MakeGrid();
            Fill(gridAll, AllHeader(), AllRows(), 5, 10);
            StyleAll(gridAll);
            AddTab("All (" + result.All.Count + " rows)", gridAll,
                () => ConnectionLoadMath.ToDelimited(AllHeader(), AllRows(), '\t', false));

            // Checks as plain text so they can be read and copied in full
            int warnings = result.Checks.Count(c => c.StartsWith("⚠"));
            TextBox txtChecks = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = true,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = string.Join(Environment.NewLine + Environment.NewLine, result.Checks)
            };
            TabPage pChecks = new TabPage(warnings > 0 ? "Checks (" + warnings + " ⚠)" : "Checks ✔");
            pChecks.Controls.Add(txtChecks);
            tabs.TabPages.Add(pChecks);
            content[pChecks] = new TabContent { Grid = null, CopyText = () => string.Join(Environment.NewLine, result.Checks) };

            if (selected >= 0 && selected < tabs.TabCount) tabs.SelectedIndex = selected;
            tabs.ResumeLayout();
        }

        // ------------------------------------------------------------------
        // Axes: switch / rotate, and the triad on the model
        // ------------------------------------------------------------------

        private LoadSystem SelectedSystem
        {
            get { return cmbAxes.SelectedIndex == 0 ? LoadSystem.Local : cmbAxes.SelectedIndex == 2 ? LoadSystem.Custom : LoadSystem.Global; }
        }

        /// <summary>The preview follows at once; the tables (and the model triad) after a short pause, so typing stays smooth.</summary>
        private void AxesChanged()
        {
            editor.SetSystem(SelectedSystem, FirstLocalFrame());
            recomputeTimer.Stop();
            recomputeTimer.Start();
        }

        /// <summary>Applies a pending axes change now, so an export or copy never mixes two systems.</summary>
        private void FlushAxes()
        {
            if (!recomputeTimer.Enabled) return;
            recomputeTimer.Stop();
            ApplyAxes();
        }

        private void ApplyAxes()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                ConnectionLoadExtractorFunction.Recompute(result, SelectedSystem, editor.Value);
                Text = "Connection Loads - " + result.AxesText;
                BuildTabs();
                Theme.Restyle(tabs);
            }
            finally { Cursor = Cursors.Default; }
            if (axesShown) DrawAxes();
        }

        private void ToggleAxes()
        {
            if (axesShown)
            {
                hideAxes();
                axesShown = false;
                btnShowAxes.Text = "Show axes in model";
                return;
            }
            axesShown = true;
            btnShowAxes.Text = "Hide axes in model";
            DrawAxes();
        }

        private void DrawAxes()
        {
            string error = showAxes(result.Axes, result.Custom);
            if (error != null) MessageBox.Show(error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private LocalFrame FirstLocalFrame() { return result.Ends.Count > 0 ? result.Ends[0].Frame : null; }

        /// <summary>Exports name the axes in a last column, so a saved file can never be misread.</summary>
        private List<string> WithAxes(List<string> header) { return header.Concat(new[] { "Axes" }).ToList(); }

        private IEnumerable<IList<string>> WithAxes(IEnumerable<IList<string>> rows)
        {
            // Materialised now: the save dialog that follows lets the axes change before the rows would be read
            string axes = result.AxesText;
            return rows.Select(r => (IList<string>)r.Concat(new[] { axes }).ToList()).ToList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) recomputeTimer.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Adds a tab with a critical table for the given rows.</summary>
        private void AddCriticalTab(string title, List<CriticalRow> rows)
        {
            DataGridView grid = MakeGrid();
            Fill(grid, CriticalHeader(), CriticalRows(rows), ColValue, ColFirstComp + 5);
            StyleCritical(grid, rows);
            AddTab(title, grid, () => ConnectionLoadMath.ToDelimited(CriticalHeader(), CriticalRows(rows), '\t', false));
        }

        private void AddTab(string title, DataGridView grid, Func<string> copyText)
        {
            TabPage page = new TabPage(title);
            page.Controls.Add(grid);
            tabs.TabPages.Add(page);
            content[page] = new TabContent { Grid = grid, CopyText = copyText };
        }

        // ------------------------------------------------------------------
        // Table content (shared by the grids and the exports)
        // ------------------------------------------------------------------

        /// <summary>Column titles of the six components with units, e.g. "Fy [kN]".</summary>
        private string[] ComponentHeaders()
        {
            string[] names = ConnectionLoadMath.Names(result.Axes);
            return Enumerable.Range(0, 6).Select(i => names[i] + " [" + ConnectionLoadMath.Unit(i) + "]").ToArray();
        }

        private List<string> AllHeader()
        {
            List<string> h = new List<string> { "Node", "Line", "Member", "End", "Combination" };
            h.AddRange(ComponentHeaders());
            return h;
        }

        private IEnumerable<IList<string>> AllRows()
        {
            foreach (EndLoadRow r in result.All)
            {
                List<string> row = new List<string> { r.End.NodeId.ToString(), r.End.LineId.ToString(), r.End.MemberId.ToString(), r.End.EndLabel, r.ComboName };
                row.AddRange(r.In(result.Axes).Select(ConnectionLoadMath.Num));
                yield return row;
            }
        }

        /// <summary>Header of every critical table; the column indices match ColValue / ColFirstComp.</summary>
        private List<string> CriticalHeader()
        {
            List<string> h = new List<string> { "Component", "Criterion", "Scope", "Node", "Line", "Member", "End", "Combination", "Value [kN / kNm]" };
            h.AddRange(ComponentHeaders());
            h.Add("Note");
            return h;
        }

        private IEnumerable<IList<string>> CriticalRows(IEnumerable<CriticalRow> rows)
        {
            string[] names = ConnectionLoadMath.Names(result.Axes);
            foreach (CriticalRow cr in rows)
            {
                EndLoadRow r = cr.Row;
                double[] v = r.In(result.Axes);
                List<string> row = new List<string>
                {
                    names[cr.Component],
                    cr.Kind == CriticalKind.MaxPositive ? "Max +" : "Min −",
                    cr.End == null ? "All ends" : "Member end",
                    r.End.NodeId.ToString(), r.End.LineId.ToString(), r.End.MemberId.ToString(), r.End.EndLabel, r.ComboName,
                    ConnectionLoadMath.Num(v[cr.Component])
                };
                row.AddRange(v.Select(ConnectionLoadMath.Num)); // the full concurrent vector
                row.Add(cr.SignMatches ? "" : cr.Kind == CriticalKind.MaxPositive ? "no positive value" : "no negative value");
                yield return row;
            }
        }

        // ------------------------------------------------------------------
        // Grids
        // ------------------------------------------------------------------

        private static DataGridView MakeGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
            };
        }

        /// <summary>Puts a header and string rows into a grid; columns firstNum..lastNum are right-aligned numbers.</summary>
        private static void Fill(DataGridView grid, IList<string> header, IEnumerable<IList<string>> rows, int firstNum, int lastNum)
        {
            for (int i = 0; i < header.Count; i++)
            {
                // Not sortable: cells are text, so a sort would order "10" before "9"
                DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn { HeaderText = header[i], SortMode = DataGridViewColumnSortMode.NotSortable };
                if (i >= firstNum && i <= lastNum) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                grid.Columns.Add(col);
            }
            foreach (IList<string> r in rows) grid.Rows.Add(r.Cast<object>().ToArray());
            grid.ClearSelection();
        }

        /// <summary>
        /// "All ends" rows bold and coloured, rows without the expected sign grey, the value column bold, and in
        /// each row the cell of the governing component bold so it stands out in the concurrent vector.
        /// </summary>
        private static void StyleCritical(DataGridView grid, List<CriticalRow> rows)
        {
            Font bold = new Font(grid.Font, FontStyle.Bold);
            for (int i = 0; i < rows.Count; i++)
            {
                DataGridViewRow row = grid.Rows[i];
                row.Tag = rows[i].Row; // used by "Select in model"
                if (rows[i].End == null) row.DefaultCellStyle.BackColor = OverallColour;
                if (!rows[i].SignMatches) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                row.Cells[ColFirstComp + rows[i].Component].Style.Font = bold;
            }
            grid.Columns[ColValue].DefaultCellStyle.Font = bold;
        }

        /// <summary>Rows that are critical (any component) for their own end get a light highlight, overall ones orange.</summary>
        private void StyleAll(DataGridView grid)
        {
            HashSet<EndLoadRow> critical = new HashSet<EndLoadRow>(result.Critical.Where(c => c.End != null && c.SignMatches).Select(c => c.Row));
            HashSet<EndLoadRow> overall = new HashSet<EndLoadRow>(result.Critical.Where(c => c.End == null && c.SignMatches).Select(c => c.Row));
            for (int i = 0; i < result.All.Count; i++)
            {
                DataGridViewRow row = grid.Rows[i];
                row.Tag = result.All[i];
                if (overall.Contains(result.All[i])) row.DefaultCellStyle.BackColor = OverallColour;
                else if (critical.Contains(result.All[i])) row.DefaultCellStyle.BackColor = CriticalColour;
            }
        }

        private Button MakeButton(string text, int x, int width)
        {
            return new Button { Text = text, Location = new Point(x, 520), Size = new Size(width, 27), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        /// <summary>Writes a CSV the user picks a location for (';' separated, '.' decimals, "sep=;" first line).</summary>
        private void SaveCsv(string defaultName, IList<string> header, IEnumerable<IList<string>> rows)
        {
            using (SaveFileDialog dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = defaultName, OverwritePrompt = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // UTF-8 with BOM so Excel shows "−" and other non-ASCII names correctly
                    File.WriteAllText(dlg.FileName, ConnectionLoadMath.ToDelimited(header, rows, ';', true), new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not write the file: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>Copies the visible tab as tab-separated text (pastes straight into Excel).</summary>
        private void CopyActiveTab()
        {
            TabContent tc;
            if (tabs.SelectedTab == null || !content.TryGetValue(tabs.SelectedTab, out tc)) return;
            try { Clipboard.SetText(tc.CopyText()); } catch { }
        }

        /// <summary>
        /// Selects in AxisVM the node + line of the highlighted grid rows; with nothing highlighted, every
        /// target node and every line that has a reported end.
        /// </summary>
        private void SelectFromActiveTab()
        {
            TabContent tc;
            DataGridView grid = tabs.SelectedTab != null && content.TryGetValue(tabs.SelectedTab, out tc) ? tc.Grid : null;
            List<EndLoadRow> picked = grid == null ? new List<EndLoadRow>()
                : grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as EndLoadRow).Where(r => r != null).ToList();

            IEnumerable<int> nodes, lines;
            if (picked.Count > 0)
            {
                nodes = picked.Select(r => r.End.NodeId);
                lines = picked.Select(r => r.End.LineId);
            }
            else
            {
                nodes = result.TargetNodeIds;
                lines = result.All.Select(r => r.End.LineId);
            }
            selectInModel(nodes.ToList(), lines.ToList());
        }
    }
}
