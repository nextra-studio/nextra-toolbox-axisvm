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
    /// Result window of the Connection Load Extractor: every member end x combination ("All"), the governing
    /// combinations ("Critical") and the self-checks ("Checks"), with CSV / clipboard export and a button that
    /// selects the corresponding nodes and lines in AxisVM for a visual check.
    /// </summary>
    public class ConnectionLoadResultForm : Form
    {
        private readonly ExtractionResult result;
        private readonly Action<IEnumerable<int>, IEnumerable<int>> selectInModel; // (nodes, lines)

        private TabControl tabs;
        private DataGridView gridAll;
        private DataGridView gridCritical;

        // Highlight colours: governing over all ends, and rows that are critical for their own end
        private static readonly Color OverallColour = Color.FromArgb(255, 230, 160);
        private static readonly Color CriticalColour = Color.FromArgb(255, 248, 210);

        public ConnectionLoadResultForm(ExtractionResult result, Action<IEnumerable<int>, IEnumerable<int>> selectInModel)
        {
            this.result = result;
            this.selectInModel = selectInModel;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Connection Loads - critical " + result.CriticalLabel + ", output " + result.OutputSystem;
            ClientSize = new Size(1100, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(700, 350);
            MinimizeBox = false;

            tabs = new TabControl { Location = new Point(8, 8), Size = new Size(1084, 500), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };

            // Critical first: that is the answer the user came for
            gridCritical = MakeGrid();
            Fill(gridCritical, CriticalHeader(), CriticalRows());
            StyleCritical();
            TabPage pCrit = new TabPage("Critical (" + result.CriticalLabel + ")");
            pCrit.Controls.Add(gridCritical);

            gridAll = MakeGrid();
            Fill(gridAll, AllHeader(), AllRows());
            StyleAll();
            TabPage pAll = new TabPage("All (" + result.All.Count + " rows)");
            pAll.Controls.Add(gridAll);

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

            tabs.TabPages.AddRange(new[] { pCrit, pAll, pChecks });

            // Buttons along the bottom
            Button btnExportAll = MakeButton("Export all (CSV)", 8, 150);
            btnExportAll.Click += delegate { SaveCsv("ConnectionLoads_all.csv", AllHeader(), AllRows()); };
            Button btnExportCrit = MakeButton("Export critical (CSV)", 164, 150);
            btnExportCrit.Click += delegate { SaveCsv("ConnectionLoads_critical.csv", CriticalHeader(), CriticalRows()); };
            Button btnCopy = MakeButton("Copy tab (TSV)", 320, 130);
            btnCopy.Click += delegate { CopyActiveTab(); };
            Button btnSelect = MakeButton("Select in model", 456, 130);
            btnSelect.Click += delegate { SelectFromActiveTab(); };

            Label lblHint = new Label
            {
                Text = "Select in model: the highlighted grid rows, or every target node and line if no row is highlighted.",
                Location = new Point(594, 522),
                Size = new Size(410, 30),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Button btnClose = new Button { Text = "Close", Location = new Point(1012, 520), Size = new Size(80, 27), DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            Controls.AddRange(new Control[] { tabs, btnExportAll, btnExportCrit, btnCopy, btnSelect, lblHint, btnClose });
            CancelButton = btnClose;
        }

        // ------------------------------------------------------------------
        // Table content (shared by the grids and the exports)
        // ------------------------------------------------------------------

        /// <summary>Column titles of the six output components with units, e.g. "Fy [kN]".</summary>
        private string[] OutputComponentHeaders()
        {
            string[] names = ConnectionLoadMath.Names(result.OutputSystem);
            return Enumerable.Range(0, 6).Select(i => names[i] + " [" + ConnectionLoadMath.Unit(i) + "]").ToArray();
        }

        private List<string> AllHeader()
        {
            List<string> h = new List<string> { "Node", "Line", "Member", "End", "Combination" };
            h.AddRange(OutputComponentHeaders());
            return h;
        }

        private IEnumerable<IList<string>> AllRows()
        {
            foreach (EndLoadRow r in result.All)
            {
                List<string> row = new List<string> { r.End.NodeId.ToString(), r.End.LineId.ToString(), r.End.MemberId.ToString(), r.End.EndLabel, r.ComboName };
                row.AddRange(r.In(result.OutputSystem).Select(ConnectionLoadMath.Num));
                yield return row;
            }
        }

        private List<string> CriticalHeader()
        {
            int c = result.CriticalComponent;
            List<string> h = new List<string> { "Criterion", "Scope", "Node", "Line", "Member", "End", "Combination",
                result.CriticalLabel + " [" + ConnectionLoadMath.Unit(c) + "]" };
            h.AddRange(OutputComponentHeaders());
            h.Add("Note");
            return h;
        }

        private IEnumerable<IList<string>> CriticalRows()
        {
            foreach (CriticalRow cr in result.Critical)
            {
                EndLoadRow r = cr.Row;
                List<string> row = new List<string>
                {
                    cr.Kind == CriticalKind.MaxPositive ? "Max +" : "Min −",
                    cr.End == null ? "All ends" : "Member end",
                    r.End.NodeId.ToString(), r.End.LineId.ToString(), r.End.MemberId.ToString(), r.End.EndLabel, r.ComboName,
                    ConnectionLoadMath.Num(r.In(result.CriticalSystem)[result.CriticalComponent])
                };
                row.AddRange(r.In(result.OutputSystem).Select(ConnectionLoadMath.Num));
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

        /// <summary>Puts a header and string rows into a grid; numeric columns are right-aligned.</summary>
        private static void Fill(DataGridView grid, IList<string> header, IEnumerable<IList<string>> rows)
        {
            foreach (string h in header)
            {
                // Not sortable: cells are text, so a sort would order "10" before "9"
                DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn { HeaderText = h, SortMode = DataGridViewColumnSortMode.NotSortable };
                if (h.Contains("[")) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; // has a unit -> number
                grid.Columns.Add(col);
            }
            foreach (IList<string> r in rows) grid.Rows.Add(r.Cast<object>().ToArray());
            grid.ClearSelection();
        }

        /// <summary>Governing-over-all rows bold and coloured; the critical value column bold.</summary>
        private void StyleCritical()
        {
            for (int i = 0; i < result.Critical.Count; i++)
            {
                DataGridViewRow row = gridCritical.Rows[i];
                row.Tag = result.Critical[i].Row; // used by "Select in model"
                if (result.Critical[i].End == null)
                {
                    row.DefaultCellStyle.BackColor = OverallColour;
                    row.DefaultCellStyle.Font = new Font(gridCritical.Font, FontStyle.Bold);
                }
                if (!result.Critical[i].SignMatches) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            gridCritical.Columns[7].DefaultCellStyle.Font = new Font(gridCritical.Font, FontStyle.Bold);
        }

        /// <summary>Rows that are critical for their own end get a light highlight, so they stand out in context.</summary>
        private void StyleAll()
        {
            HashSet<EndLoadRow> critical = new HashSet<EndLoadRow>(result.Critical.Where(c => c.End != null && c.SignMatches).Select(c => c.Row));
            HashSet<EndLoadRow> overall = new HashSet<EndLoadRow>(result.Critical.Where(c => c.End == null && c.SignMatches).Select(c => c.Row));
            for (int i = 0; i < result.All.Count; i++)
            {
                DataGridViewRow row = gridAll.Rows[i];
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
            string text;
            if (tabs.SelectedIndex == 0) text = ConnectionLoadMath.ToDelimited(CriticalHeader(), CriticalRows(), '\t', false);
            else if (tabs.SelectedIndex == 1) text = ConnectionLoadMath.ToDelimited(AllHeader(), AllRows(), '\t', false);
            else text = string.Join(Environment.NewLine, result.Checks);
            try { Clipboard.SetText(text); } catch { }
        }

        /// <summary>
        /// Selects in AxisVM the node + line of the highlighted grid rows; with nothing highlighted, every
        /// target node and every line that has a reported end.
        /// </summary>
        private void SelectFromActiveTab()
        {
            DataGridView grid = tabs.SelectedIndex == 0 ? gridCritical : tabs.SelectedIndex == 1 ? gridAll : null;
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
