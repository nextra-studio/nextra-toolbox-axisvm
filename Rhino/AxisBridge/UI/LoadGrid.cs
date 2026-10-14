using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Loads;

namespace AxisBridge.UI
{
    /// <summary>
    /// Editable table of loads of one kind. Each row is one load: pick its load case from the
    /// dropdown, fill in the numbers, and add as many rows as needed - several load cases on the same
    /// panel are simply several rows. Rows are written back as Ax_DistLoad, Ax_DistLoad.1, ...
    /// </summary>
    public class LoadGrid : UserControl
    {
        public readonly LoadKind Kind;
        public readonly string FamilyKey;

        /// <summary>True once the user changed anything; only then are the loads rewritten.</summary>
        public bool Touched { get; private set; }

        /// <summary>The selection had different loads on different objects.</summary>
        public bool StartedVaries { get; private set; }

        /// <summary>Raised when the user asks to pick a surface point load location in the viewport.</summary>
        public event Action<LoadGrid, int> PickRequested;

        /// <summary>Raised when the user clicks "Load cases..." to edit the library.</summary>
        public event Action<LoadGrid> ManageCasesRequested;

        private readonly DataGridView grid;
        private readonly Label banner;
        private readonly List<LoadRow> rows = new List<LoadRow>();
        private readonly List<ColumnSpec> specs = new List<ColumnSpec>();
        private readonly DataGridViewComboBoxColumn caseColumn;
        private List<string> cases = new List<string>();
        private bool loading;

        private const string ColCase = "case", ColSys = "sys", ColBy = "by", ColPick = "pick";

        /// <summary>One numeric column: header, getter, setter.</summary>
        private class ColumnSpec
        {
            public string Name;
            public string Header;
            public Func<LoadRow, double> Get;
            public Action<LoadRow, double> Set;
            public bool BlankWhenNull;   // q2 columns: blank = uniform
            public Func<LoadRow, bool> IsNull;
        }

        public LoadGrid(LoadKind kind, string familyKey, string title, string help)
        {
            Kind = kind;
            FamilyKey = familyKey;
            Dock = DockStyle.Fill;
            Padding = new Padding(0, 0, 0, 6);

            // ---- toolbar ----------------------------------------------------
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
            var lbl = new Label { Text = title, Font = Theme.Bold, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, 6, 14, 0) };
            var add = SmallButton("+ Add");
            var dup = SmallButton("Duplicate");
            var del = SmallButton("Remove");
            var lc = SmallButton("Load cases...");
            bar.Controls.AddRange(new Control[] { lbl, add, dup, del, lc });

            banner = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 34,
                BackColor = Theme.WarnBack,
                Padding = new Padding(6, 3, 6, 3),
                Font = Theme.Small,
                Visible = false,
                Text = "The selected objects carry DIFFERENT loads of this kind. They are kept as they are unless you add or edit rows here - " +
                       "then these rows REPLACE this kind of load on every selected object."
            };

            var helpLabel = Theme.Hint(help);
            helpLabel.Dock = DockStyle.Bottom;
            helpLabel.MaximumSize = new Size(0, 0);

            // ---- grid -------------------------------------------------------
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersWidth = 24,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.Font = Theme.Body;

            caseColumn = new DataGridViewComboBoxColumn
            {
                Name = ColCase,
                HeaderText = "Load case",
                FlatStyle = FlatStyle.Flat,
                DisplayStyleForCurrentCellOnly = false,
                FillWeight = 150,
                MinimumWidth = 90
            };
            grid.Columns.Add(caseColumn);

            BuildColumns();

            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                // Commit dropdown picks immediately so the value is not lost on Apply.
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValidating += Grid_CellValidating;
            grid.CellContentClick += Grid_CellContentClick;
            grid.DataError += (s, e) => { e.ThrowException = false; };

            add.Click += (s, e) => AddRow(null);
            dup.Click += (s, e) => { int i = CurrentIndex(); if (i >= 0) AddRow(rows[i].Clone()); };
            del.Click += (s, e) => RemoveCurrent();
            lc.Click += (s, e) => ManageCasesRequested?.Invoke(this);

            Controls.Add(grid);
            Controls.Add(helpLabel);
            Controls.Add(banner);
            Controls.Add(bar);
        }

        private static Button SmallButton(string text)
        {
            return new Button { Text = text, AutoSize = true, Height = 26, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System };
        }

        // ==================================================================
        // Columns per kind
        // ==================================================================

        private void BuildColumns()
        {
            switch (Kind)
            {
                case LoadKind.Nodal:
                    ForceColumns(6);
                    break;

                case LoadKind.MemberPoint:
                    SystemColumn("Global", "Local");
                    ByColumn();
                    Num("pos", "Position", r => r.Pos, (r, v) => r.Pos = v);
                    ForceColumns(6);
                    break;

                case LoadKind.MemberDist:
                    SystemColumn("Global", "Local");
                    ByColumn();
                    Num("from", "From", r => r.From, (r, v) => r.From = v);
                    Num("to", "To", r => r.To, (r, v) => r.To = v);
                    Num("qx1", "qx start\n[kN/m]", r => r.V[0], (r, v) => r.V[0] = v);
                    Num("qy1", "qy start\n[kN/m]", r => r.V[1], (r, v) => r.V[1] = v);
                    Num("qz1", "qz start\n[kN/m]", r => r.V[2], (r, v) => r.V[2] = v);
                    NumEnd("qx2", "qx end\n(blank=uniform)", 0);
                    NumEnd("qy2", "qy end", 1);
                    NumEnd("qz2", "qz end", 2);
                    break;

                case LoadKind.SurfaceArea:
                    SystemColumn("Global", "Local", "Projected");
                    Num("qx", "qx [kN/m²]", r => r.V[0], (r, v) => r.V[0] = v);
                    Num("qy", "qy [kN/m²]", r => r.V[1], (r, v) => r.V[1] = v);
                    Num("qz", "qz [kN/m²]", r => r.V[2], (r, v) => r.V[2] = v);
                    break;

                case LoadKind.SurfacePoint:
                    SystemColumn("Global", "Local");
                    Num("x", "X", r => r.X, (r, v) => r.X = v);
                    Num("y", "Y", r => r.Y, (r, v) => r.Y = v);
                    Num("z", "Z", r => r.Z, (r, v) => r.Z = v);
                    grid.Columns.Add(new DataGridViewButtonColumn
                    {
                        Name = ColPick,
                        HeaderText = "",
                        Text = "Pick",
                        UseColumnTextForButtonValue = true,
                        FillWeight = 45,
                        MinimumWidth = 44
                    });
                    ForceColumns(3);
                    break;
            }
        }

        private void ForceColumns(int n)
        {
            string[] names = { "Fx [kN]", "Fy [kN]", "Fz [kN]", "Mx [kNm]", "My [kNm]", "Mz [kNm]" };
            for (int i = 0; i < n; i++)
            {
                int k = i;
                Num("f" + i, names[i], r => r.V[k], (r, v) => r.V[k] = v);
            }
        }

        private void SystemColumn(params string[] options)
        {
            var c = new DataGridViewComboBoxColumn { Name = ColSys, HeaderText = "System", FlatStyle = FlatStyle.Flat, FillWeight = 80, MinimumWidth = 70 };
            c.Items.AddRange(options);
            grid.Columns.Add(c);
        }

        private void ByColumn()
        {
            var c = new DataGridViewComboBoxColumn { Name = ColBy, HeaderText = "Position as", FlatStyle = FlatStyle.Flat, FillWeight = 80, MinimumWidth = 80 };
            c.Items.AddRange("Ratio 0..1", "Length");
            grid.Columns.Add(c);
        }

        private void Num(string name, string header, Func<LoadRow, double> get, Action<LoadRow, double> set)
        {
            specs.Add(new ColumnSpec { Name = name, Header = header, Get = get, Set = set });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = 60,
                MinimumWidth = 48,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
        }

        private void NumEnd(string name, string header, int idx)
        {
            specs.Add(new ColumnSpec
            {
                Name = name,
                Header = header,
                Get = r => r.V2 == null ? 0 : r.V2[idx],
                Set = (r, v) => { if (r.V2 == null) r.V2 = (double[])r.V.Clone(); r.V2[idx] = v; },
                BlankWhenNull = true,
                IsNull = r => r.V2 == null
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = 60,
                MinimumWidth = 48,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, BackColor = Color.FromArgb(250, 250, 252) }
            });
        }

        // ==================================================================
        // Data in / out
        // ==================================================================

        /// <summary>Fills the grid from the selection's load strings.</summary>
        public void LoadRows(FieldState state, List<string> values, IEnumerable<string> caseNames)
        {
            loading = true;
            try
            {
                StartedVaries = state.Kind == FieldStateKind.Varies;
                banner.Visible = StartedVaries;
                Touched = false;

                rows.Clear();
                if (!StartedVaries)
                {
                    foreach (string v in values)
                    {
                        LoadRow r;
                        if (LoadRow.TryParse(Kind, v, out r)) rows.Add(r);
                    }
                }
                SetCases(caseNames);
                Rebuild();
            }
            finally { loading = false; }
        }

        /// <summary>Updates the load case dropdown (library + any case already used by a row).</summary>
        public void SetCases(IEnumerable<string> caseNames)
        {
            var set = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in caseNames) if (!string.IsNullOrWhiteSpace(c) && seen.Add(c.Trim())) set.Add(c.Trim());
            foreach (LoadRow r in rows) if (!string.IsNullOrWhiteSpace(r.Case) && seen.Add(r.Case)) set.Add(r.Case);
            if (set.Count == 0) set.Add("Dead");
            cases = set;

            grid.EndEdit();
            caseColumn.Items.Clear();
            caseColumn.Items.AddRange(cases.ToArray());
            grid.Refresh();
        }

        /// <summary>The load strings to write, one per row.</summary>
        public List<string> Values()
        {
            grid.EndEdit();
            var list = new List<string>();
            foreach (LoadRow r in rows) list.Add(r.Format(Kind));
            return list;
        }

        /// <summary>Load case names used by the rows (so the command can register them in the library).</summary>
        public IEnumerable<string> UsedCases()
        {
            foreach (LoadRow r in rows) yield return r.Case;
        }

        /// <summary>Adds this grid's change (if any) to the edit.</summary>
        public void Collect(AttrEdit edit)
        {
            if (!Touched) return;
            edit.Families[FamilyKey] = Values();
        }

        public void SetPickedPoint(int rowIndex, double x, double y, double z)
        {
            if (rowIndex < 0 || rowIndex >= rows.Count) return;
            rows[rowIndex].X = x;
            rows[rowIndex].Y = y;
            rows[rowIndex].Z = z;
            Touched = true;
            banner.Visible = false;
            Rebuild();
        }

        private void Rebuild()
        {
            bool was = loading;
            loading = true;
            try
            {
                grid.Rows.Clear();
                foreach (LoadRow r in rows)
                {
                    int i = grid.Rows.Add();
                    FillRow(grid.Rows[i], r);
                }
            }
            finally { loading = was; }
        }

        private void FillRow(DataGridViewRow gr, LoadRow r)
        {
            gr.Cells[ColCase].Value = r.Case;
            if (grid.Columns.Contains(ColSys)) gr.Cells[ColSys].Value = r.System;
            if (grid.Columns.Contains(ColBy)) gr.Cells[ColBy].Value = r.ByLength ? "Length" : "Ratio 0..1";
            foreach (ColumnSpec c in specs)
            {
                bool blank = c.BlankWhenNull && c.IsNull(r);
                gr.Cells[c.Name].Value = blank ? "" : LoadRow.N(c.Get(r));
            }
        }

        private void AddRow(LoadRow template)
        {
            grid.EndEdit();
            LoadRow r = template ?? DefaultRow();
            if (template != null)
            {
                // Duplicating is mostly "same load, next load case" - move to the next case if there is one.
                int ci = cases.FindIndex(c => string.Equals(c, r.Case, StringComparison.OrdinalIgnoreCase));
                if (ci >= 0 && ci + 1 < cases.Count) r.Case = cases[ci + 1];
            }
            rows.Add(r);
            MarkTouched();
            Rebuild();
            if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[ColCase];
        }

        private LoadRow DefaultRow()
        {
            var r = new LoadRow { Case = cases.Count > 0 ? cases[0] : "Dead" };
            switch (Kind)
            {
                case LoadKind.Nodal:
                case LoadKind.MemberPoint:
                case LoadKind.SurfacePoint: r.V[2] = -10; break;
                case LoadKind.MemberDist: r.V[2] = -5; break;
                case LoadKind.SurfaceArea: r.V[2] = -2.5; break;
            }
            return r;
        }

        private void RemoveCurrent()
        {
            int i = CurrentIndex();
            if (i < 0) return;
            grid.EndEdit();
            rows.RemoveAt(i);
            MarkTouched();
            Rebuild();
        }

        private int CurrentIndex()
        {
            return grid.CurrentCell == null ? (rows.Count > 0 ? rows.Count - 1 : -1) : grid.CurrentCell.RowIndex;
        }

        private void MarkTouched()
        {
            Touched = true;
            banner.Visible = false;
        }

        // ==================================================================
        // Grid events
        // ==================================================================

        private void Grid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            string name = grid.Columns[e.ColumnIndex].Name;
            ColumnSpec spec = specs.Find(c => c.Name == name);
            if (spec == null) return;

            string text = (e.FormattedValue as string ?? "").Trim();
            if (text.Length == 0) return;   // blank -> 0 (or "uniform" for q2)
            double d;
            if (!TryNum(text, out d))
            {
                grid.Rows[e.RowIndex].ErrorText = "'" + text + "' is not a number";
                e.Cancel = true;
            }
            else grid.Rows[e.RowIndex].ErrorText = "";
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            LoadRow r = rows[e.RowIndex];
            string name = grid.Columns[e.ColumnIndex].Name;
            object value = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            string text = value == null ? "" : value.ToString().Trim();

            if (name == ColCase) r.Case = text;
            else if (name == ColSys) r.System = text;
            else if (name == ColBy)
            {
                bool byLen = text == "Length";
                if (byLen != r.ByLength)
                {
                    r.ByLength = byLen;
                    // Switching back to ratios: snap to a sensible default range.
                    if (!byLen) { r.Pos = 0.5; r.From = 0; r.To = 1; }
                    BeginInvoke((Action)Rebuild);
                }
            }
            else
            {
                ColumnSpec spec = specs.Find(c => c.Name == name);
                if (spec == null) return;
                if (text.Length == 0 && spec.BlankWhenNull)
                {
                    // Clearing all three q-end cells makes the load uniform again.
                    if (r.V2 != null && AllEndCellsBlank(e.RowIndex)) r.V2 = null;
                }
                else
                {
                    double d;
                    spec.Set(r, TryNum(text, out d) ? d : 0.0);
                }
            }
            MarkTouched();
        }

        private bool AllEndCellsBlank(int rowIndex)
        {
            foreach (ColumnSpec c in specs)
            {
                if (!c.BlankWhenNull) continue;
                object v = grid.Rows[rowIndex].Cells[c.Name].Value;
                if (v != null && v.ToString().Trim().Length > 0) return false;
            }
            return true;
        }

        private void Grid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != ColPick) return;
            grid.EndEdit();
            PickRequested?.Invoke(this, e.RowIndex);
        }

        /// <summary>Accepts "2.5" and "2,5" (a comma would otherwise break the attribute format).</summary>
        private static bool TryNum(string text, out double d)
        {
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
    }
}
