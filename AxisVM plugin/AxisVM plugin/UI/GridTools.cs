using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Spreadsheet-style editing for a DataGridView: a value typed into one of several selected cells goes to all
    /// selected cells of that column; right-click sets the selected cells of a column to a choice or a value;
    /// Ctrl+V pastes one value into the selection (or a block from Excel), Ctrl+D fills down, Delete clears.
    /// Every change goes through cell.Value, so the grid's own CellValueChanged handling stays in charge.
    /// </summary>
    internal static class GridTools
    {
        public static void Attach(DataGridView grid)
        {
            grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            List<DataGridViewCell> selectionAtEdit = null;

            // Remember the selection when an edit starts: the edited value is copied to it afterwards
            grid.CellBeginEdit += (s, e) => { selectionAtEdit = grid.SelectedCells.Cast<DataGridViewCell>().ToList(); };
            grid.CellEndEdit += (s, e) =>
            {
                List<DataGridViewCell> sel = selectionAtEdit;
                selectionAtEdit = null;
                if (sel == null || sel.Count < 2) return;
                DataGridViewCell edited = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                object v = edited.Value;
                grid.BeginInvoke((Action)(() => SetCells(grid, Targets(grid, sel, e.ColumnIndex).Where(c => c != edited), v)));
            };

            grid.KeyDown += (s, e) =>
            {
                if (grid.IsCurrentCellInEditMode) return;
                if (e.Control && e.KeyCode == Keys.V) { Paste(grid); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.D) { FillDown(grid); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { Clear(grid); e.Handled = true; }
            };

            // Right-click: keep the selection (select the clicked cell only when it is not part of it)
            int menuColumn = -1;
            ContextMenuStrip menu = new ContextMenuStrip();
            grid.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                menuColumn = e.ColumnIndex;
                DataGridViewCell cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (!cell.Selected) { grid.ClearSelection(); cell.Selected = true; }
                grid.CurrentCell = cell;
            };
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                if (menuColumn < 0 || menuColumn >= grid.Columns.Count) { e.Cancel = true; return; }
                DataGridViewColumn col = grid.Columns[menuColumn];
                List<DataGridViewCell> targets = Targets(grid, grid.SelectedCells.Cast<DataGridViewCell>(), menuColumn).ToList();
                if (targets.Count == 0) { e.Cancel = true; return; }
                string name = col.HeaderText.Replace("\n", " ");
                ToolStripMenuItem set = new ToolStripMenuItem("Set " + targets.Count + " selected '" + name + "' cell(s) to");
                DataGridViewComboBoxColumn combo = col as DataGridViewComboBoxColumn;
                if (combo != null)
                    foreach (object item in combo.Items)
                    {
                        object v = item;
                        set.DropDownItems.Add(Convert.ToString(item), null, (o, a) => SetCells(grid, targets, v));
                    }
                else if (col is DataGridViewCheckBoxColumn)
                {
                    set.DropDownItems.Add("Yes (ticked)", null, (o, a) => SetCells(grid, targets, true));
                    set.DropDownItems.Add("No", null, (o, a) => SetCells(grid, targets, false));
                }
                else
                    set.DropDownItems.Add("Value...", null, (o, a) =>
                    {
                        string v = Ask(grid, name, Convert.ToString(grid.CurrentCell == null ? "" : grid.CurrentCell.Value));
                        if (v != null) SetCells(grid, targets, v);
                    });
                menu.Items.Add(set);
                menu.Items.Add("Fill down from the first selected   (Ctrl+D)", null, (o, a) => FillDown(grid));
                menu.Items.Add("Paste into the selection   (Ctrl+V)", null, (o, a) => Paste(grid));
                if (!(col is DataGridViewCheckBoxColumn) && combo == null)
                    menu.Items.Add("Clear   (Delete)", null, (o, a) => Clear(grid));
            };
            grid.ContextMenuStrip = menu;
        }

        /// <summary>Editable cells of the given column among the selection (whole rows count for row selection).</summary>
        private static IEnumerable<DataGridViewCell> Targets(DataGridView grid, IEnumerable<DataGridViewCell> sel, int column)
        {
            return sel.Select(c => c.OwningRow).Where(r => r != null && !r.IsNewRow).Distinct()
                      .Select(r => r.Cells[column]).Where(c => !c.ReadOnly && !c.OwningColumn.ReadOnly).OrderBy(c => c.RowIndex);
        }

        private static void SetCells(DataGridView grid, IEnumerable<DataGridViewCell> cells, object value)
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            foreach (DataGridViewCell c in cells.ToList())
            {
                DataGridViewComboBoxCell combo = c as DataGridViewComboBoxCell;
                object v = value;
                if (combo != null)
                {
                    DataGridViewComboBoxColumn col = (DataGridViewComboBoxColumn)c.OwningColumn;
                    string s = Convert.ToString(value);
                    object match = col.Items.Cast<object>().FirstOrDefault(i => string.Equals(Convert.ToString(i), s, StringComparison.OrdinalIgnoreCase));
                    if (match == null) { if (string.IsNullOrWhiteSpace(s)) continue; col.Items.Add(s); match = s; }
                    v = match;
                }
                else if (c is DataGridViewCheckBoxCell)
                {
                    bool b;
                    v = value is bool ? (bool)value : bool.TryParse(Convert.ToString(value), out b) ? b : Convert.ToString(value) == "1";
                }
                c.Value = v;
            }
        }

        private static void FillDown(DataGridView grid)
        {
            foreach (var group in grid.SelectedCells.Cast<DataGridViewCell>().GroupBy(c => c.ColumnIndex))
            {
                List<DataGridViewCell> cells = Targets(grid, group, group.Key).ToList();
                if (cells.Count < 2) continue;
                SetCells(grid, cells.Skip(1), cells[0].Value);
            }
        }

        private static void Clear(DataGridView grid)
        {
            foreach (var group in grid.SelectedCells.Cast<DataGridViewCell>().GroupBy(c => c.ColumnIndex))
            {
                if (grid.Columns[group.Key] is DataGridViewCheckBoxColumn || grid.Columns[group.Key] is DataGridViewComboBoxColumn) continue;
                SetCells(grid, Targets(grid, group, group.Key), "");
            }
        }

        /// <summary>One value: into every selected editable cell. A block (tabs / lines, e.g. from Excel): from the current cell on.</summary>
        private static void Paste(DataGridView grid)
        {
            string text;
            try { text = Clipboard.GetText(); } catch { return; }
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 1 && !lines[0].Contains('\t'))
            {
                foreach (var group in grid.SelectedCells.Cast<DataGridViewCell>().GroupBy(c => c.ColumnIndex))
                    SetCells(grid, Targets(grid, group, group.Key), lines[0]);
                return;
            }
            if (grid.CurrentCell == null) return;
            int r0 = grid.CurrentCell.RowIndex, c0 = grid.CurrentCell.ColumnIndex;
            for (int i = 0; i < lines.Length && r0 + i < grid.Rows.Count; i++)
            {
                string[] parts = lines[i].Split('\t');
                for (int j = 0; j < parts.Length; j++)
                {
                    int ci = c0 + j;
                    while (ci < grid.Columns.Count && !grid.Columns[ci].Visible) ci++;
                    if (ci >= grid.Columns.Count) break;
                    DataGridViewCell cell = grid.Rows[r0 + i].Cells[ci];
                    if (!cell.ReadOnly && !cell.OwningColumn.ReadOnly) SetCells(grid, new[] { cell }, parts[j]);
                }
            }
        }

        private static string Ask(IWin32Window owner, string what, string current)
        {
            using (Form f = new Form { Text = "Set value", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(340, 110), ShowInTaskbar = false })
            {
                Theme.Initialize(f);
                Label l = new Label { Text = what + ":", AutoSize = true, Location = new Point(12, 12) };
                TextBox t = new TextBox { Text = current, Location = new Point(12, 36), Width = 316 };
                Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(166, 70), Size = new Size(76, 28) };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(252, 70), Size = new Size(76, 28) };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }
    }
}
