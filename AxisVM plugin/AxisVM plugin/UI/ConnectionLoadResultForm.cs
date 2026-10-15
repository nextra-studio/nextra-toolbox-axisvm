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
    /// </summary>
    public class ConnectionLoadResultForm : Form
    {
        private readonly ExtractionResult result;
        private readonly Action<IEnumerable<int>, IEnumerable<int>> selectInModel; // (nodes, lines)

        private TabControl tabs;

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
        {
            this.result = result;
            this.selectInModel = selectInModel;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Connection Loads - " + result.Axes + " axes";
            ClientSize = new Size(1150, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(700, 350);
            MinimizeBox = false;

            tabs = new TabControl { Location = new Point(8, 8), Size = new Size(1134, 500), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
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

            // Buttons along the bottom
            Button btnExportAll = MakeButton("Export all (CSV)", 8, 150);
            btnExportAll.Click += delegate { SaveCsv("ConnectionLoads_all.csv", AllHeader(), AllRows()); };
            Button btnExportCrit = MakeButton("Export critical (CSV)", 164, 150);
            btnExportCrit.Click += delegate { SaveCsv("ConnectionLoads_critical.csv", CriticalHeader(), CriticalRows(result.Critical)); };
            Button btnCopy = MakeButton("Copy tab (TSV)", 320, 130);
            btnCopy.Click += delegate { CopyActiveTab(); };
            Button btnSelect = MakeButton("Select in model", 456, 130);
            btnSelect.Click += delegate { SelectFromActiveTab(); };

            Label lblHint = new Label
            {
                Text = "Select in model: the highlighted grid rows, or every target node and line if no row is highlighted.",
                Location = new Point(594, 522),
                Size = new Size(460, 30),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // The window is modeless: a button's DialogResult only closes MODAL forms, so close explicitly
            Button btnClose = new Button { Text = "Close", Location = new Point(1062, 520), Size = new Size(80, 27), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnClose.Click += delegate { Close(); };

            // Esc closes too (handled here because AxisVM's message loop does not do dialog-key processing)
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            Controls.AddRange(new Control[] { tabs, btnExportAll, btnExportCrit, btnCopy, btnSelect, lblHint, btnClose });
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
