using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// The Timber SLS Check window. Modeless: members are selected in AxisVM while it is open. Tabs: 1 Members,
    /// 2 Loads, 3 Deflection, 4 Vibration, 5 Results. Every input has a default, dropdowns offer presets and any
    /// value can be typed (custom).
    /// </summary>
    public class TimberSlsForm : Form
    {
        private readonly TimberSlsBackend backend;
        private readonly TimberSlsSettings st;
        private List<ModelLoadCase> modelCases = new List<ModelLoadCase>();
        private List<ModelCombination> modelCombos = new List<ModelCombination>();
        private SlsRun run;

        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        private readonly Label status = new Label { AutoSize = true, Margin = new Padding(12, 9, 0, 0), ForeColor = Color.FromArgb(70, 84, 99) };

        // Members
        private readonly DataGridView memberGrid = new DataGridView();
        private readonly Label memberCount = new Label { AutoSize = true, Margin = new Padding(12, 9, 0, 0), ForeColor = Color.FromArgb(70, 84, 99) };

        // Loads
        private readonly ComboBox sourceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        private readonly DataGridView caseGrid = new DataGridView();
        private readonly CheckedListBox comboList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly Label loadSummary = new Label { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

        // Deflection
        private readonly ComboBox groupBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly PropertyGrid groupGrid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
        private readonly ComboBox presetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        private readonly DataGridView rowGrid = new DataGridView();
        private string shownGroup = "Floor";
        private bool rowsShown;   // the row grid holds a group's rows (so it may be committed)

        // Vibration
        private readonly PropertyGrid vibGrid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
        private readonly TextBox vibHelp = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

        // Results
        private readonly DataGridView resultGrid = new DataGridView();
        private readonly ComboBox comboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        private readonly ComboBox colourBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly SlsLegendView legendStrip = new SlsLegendView { Dock = DockStyle.Fill };
        private readonly CheckBox autoOverlay = new CheckBox { Text = "Colour model after run", Checked = true, AutoSize = true, Margin = new Padding(10, 8, 0, 0) };
        private readonly SlsPlanView plan = new SlsPlanView { Dock = DockStyle.Fill };
        private readonly SlsDiagramView diagram = new SlsDiagramView { Dock = DockStyle.Fill };
        private readonly TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), BackColor = Color.White };
        private readonly TextBox notes = new TextBox { Dock = DockStyle.Bottom, Height = 64, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(255, 250, 235) };
        private bool filling;
        private bool overlayShown;
        private readonly SlsGuideView termsGuide = new SlsGuideView(SlsGuideView.GuideMode.Terms) { Dock = DockStyle.Fill };
        private readonly SlsGuideView typesGuide = new SlsGuideView(SlsGuideView.GuideMode.Types) { Dock = DockStyle.Fill };
        private readonly DataGridView vibMemberGrid = new DataGridView();

        public TimberSlsForm(TimberSlsBackend backend)
        {
            this.backend = backend;
            st = TimberSlsSettings.Load(backend.ModelPath);
            Theme.Initialize(this);
            Text = TimberSlsFunction.Title;
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(60, 60);
            ShowInTaskbar = false;

            BuildMembersTab();
            BuildLoadsTab();
            BuildDeflectionTab();
            BuildVibrationTab();
            BuildResultsTab();
            BuildGuideTab();

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 10, 12, 10), BackColor = Color.White };
            Button runButton = ActionButton("Run check", true, (s, e) => RunCheck());
            Button saveButton = ActionButton("Save settings", false, (s, e) => { Commit(); string err = st.Save(backend.ModelPath); SetStatus(err == null ? "Settings saved." : "Could not save: " + err); });
            Button closeButton = ActionButton("Close", false, (s, e) => Close());
            actions.Controls.AddRange(new Control[] { runButton, saveButton, closeButton, status });

            Controls.Add(tabs);
            Controls.Add(actions);
            Controls.Add(Theme.CreateHeader("Timber SLS check (EN 1995-1-1)",
                "Deflection (EC5 2.2.3 / NL NA, Harff) and floor vibration (EC5:2004 §7.3 or EN 1995-1-1:2025 §9.3) for Floor and Roof members. Select members in AxisVM while this window is open."));

            // Spreadsheet-style editing (several cells at once, right-click, Ctrl+V / Ctrl+D / Delete) in every input table
            foreach (DataGridView g in new[] { memberGrid, caseGrid, rowGrid, vibMemberGrid }) GridTools.Attach(g);
            ReloadLoads();
            FillMembers();
            ShowGroup("Floor");
            FormClosing += (s, e) => { Commit(); st.Save(backend.ModelPath); backend.HideOverlay(); };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Button ActionButton(string text, bool primary, EventHandler click)
        {
            Button b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(0, 0, 8, 0) };
            b.Click += click;
            Theme.StyleButton(b, primary);
            return b;
        }

        private static Button SmallButton(string text, EventHandler click)
        {
            Button b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 28), Margin = new Padding(0, 4, 6, 0) };
            b.Click += click;
            Theme.StyleButton(b, false);
            return b;
        }

        private static void StyleGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Accent;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 234, 250);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 42, 65);
            grid.RowTemplate.Height = 24;
            grid.RowHeadersVisible = false;
            grid.AllowUserToResizeRows = false;
        }

        private static TabPage Page(string text) { return new TabPage(text) { BackColor = Color.FromArgb(246, 248, 251), Padding = new Padding(10) }; }

        private void SetStatus(string text) { status.Text = text; }

        private static double ParseD(object v, double fallback)
        {
            double d;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        private static string F(double v, int dec) { return TimberSlsMath.Num(v, dec); }

        /// <summary>A light, opaque tint of a colour (grids do not draw transparent cell colours).</summary>
        private static Color Tint(Color c) { return Color.FromArgb(255 - (255 - c.R) * 35 / 100, 255 - (255 - c.G) * 35 / 100, 255 - (255 - c.B) * 35 / 100); }

        // ------------------------------------------------------------------
        // 1 Members
        // ------------------------------------------------------------------

        private void BuildMembersTab()
        {
            TabPage page = Page("1  Members");
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            top.Controls.Add(SmallButton("Add AxisVM selection as Floor", (s, e) => AddSelection(true)));
            top.Controls.Add(SmallButton("Add AxisVM selection as Roof", (s, e) => AddSelection(false)));
            top.Controls.Add(SmallButton("Remove selected", (s, e) =>
            {
                foreach (int id in SelectedMemberIds()) { st.FloorLines.Remove(id); st.RoofLines.Remove(id); st.LineTypes.Remove(id); }
                FillMembers();
            }));
            top.Controls.Add(SmallButton("Clear all", (s, e) => { st.FloorLines.Clear(); st.RoofLines.Clear(); FillMembers(); }));
            top.Controls.Add(SmallButton("Select in AxisVM", (s, e) =>
            {
                List<int> sel = SelectedMemberIds();
                backend.Select(sel.Count > 0 ? sel : st.FloorLines.Concat(st.RoofLines));
            }));
            top.Controls.Add(memberCount);

            StyleGrid(memberGrid);
            memberGrid.AllowUserToAddRows = false;
            memberGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            memberGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "Line", ReadOnly = true, FillWeight = 40 });
            DataGridViewComboBoxColumn group = new DataGridViewComboBoxColumn { Name = "Group", HeaderText = "Group", FillWeight = 60, FlatStyle = FlatStyle.Flat };
            group.Items.AddRange("Floor", "Roof");
            memberGrid.Columns.Add(group);
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "L", HeaderText = "L [m]", ReadOnly = true, FillWeight = 45 });
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Material", HeaderText = "Material", ReadOnly = true, FillWeight = 60 });
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Section", HeaderText = "Section", ReadOnly = true, FillWeight = 80 });
            DataGridViewComboBoxColumn type = new DataGridViewComboBoxColumn { Name = "Type", HeaderText = "Span type", FillWeight = 70, FlatStyle = FlatStyle.Flat };
            type.Items.AddRange("Auto", "Span", "Cantilever");
            memberGrid.Columns.Add(type);
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kdef", HeaderText = "kdef (AxisVM)", ReadOnly = true, FillWeight = 50 });
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SC", HeaderText = "Service class", ReadOnly = true, FillWeight = 45 });
            memberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Last", HeaderText = "Last check: checked as, UC", ReadOnly = true, FillWeight = 130 });
            memberGrid.CurrentCellDirtyStateChanged += (s, e) => { if (memberGrid.IsCurrentCellDirty) memberGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            memberGrid.CellValueChanged += (s, e) =>
            {
                if (filling || e.RowIndex < 0) return;
                DataGridViewRow r = memberGrid.Rows[e.RowIndex];
                int id = Convert.ToInt32(r.Cells["Line"].Value);
                string col = memberGrid.Columns[e.ColumnIndex].Name;
                if (col == "Group")
                {
                    bool floor = Convert.ToString(r.Cells["Group"].Value) == "Floor";
                    st.FloorLines.Remove(id); st.RoofLines.Remove(id);
                    (floor ? st.FloorLines : st.RoofLines).Add(id);
                }
                else if (col == "Type")
                {
                    SpanType t;
                    if (Enum.TryParse(Convert.ToString(r.Cells["Type"].Value), out t))
                    {
                        if (t == st.General.DefaultType) st.LineTypes.Remove(id); else st.LineTypes[id] = t;
                    }
                }
                BeginInvoke((Action)FillMembers);
            };
            memberGrid.DataError += (s, e) => { e.ThrowException = false; };
            GroupBox membersBox = new GroupBox
            {
                Text = "Members: Floor = floor limits + vibration, Roof = roof limits. Span type: grey = default, black = own. Select several rows and right-click to set them all at once (Guide tab: what the types mean).",
                Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            membersBox.Controls.Add(memberGrid);
            membersBox.Controls.Add(top);

            GroupBox general = new GroupBox { Text = "How members are measured", Dock = DockStyle.Fill, Padding = new Padding(8) };
            PropertyGrid pg = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, SelectedObject = st.General, HelpVisible = true };
            pg.PropertyValueChanged += (s, e) => FillMembers();
            general.Controls.Add(pg);

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(membersBox);
            split.Panel2.Controls.Add(general);
            page.Controls.Add(split);
            tabs.TabPages.Add(page);
            Shown += (s, e) => { try { split.SplitterDistance = (int)(split.Height * 0.6); } catch { } };
        }

        /// <summary>The Guide tab: what the span types mean, how spans are split, and the steps.</summary>
        private void BuildGuideTab()
        {
            TabPage page = Page("6  Guide");
            Label help = new Label
            {
                Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(10, 8, 10, 4), ForeColor = Color.FromArgb(36, 53, 72),
                Text = "How to use\r\n" +
                       "1. Select timber beams / ribs (lines or members) in AxisVM; keep this window open.\r\n" +
                       "2. Tab 1: press 'Add AxisVM selection as Floor' or '... as Roof'. Group and span type can be changed in the table.\r\n" +
                       "3. Check the load cases (tab 2), limits per group (tab 3, with a drawing of what each check measures) and vibration (tab 4).\r\n" +
                       "4. Run linear static analysis in AxisVM (and vibration, if f1 is taken from AxisVM), then press Run check. Tab 5 shows the results."
            };
            page.Controls.Add(typesGuide);
            page.Controls.Add(help);
            tabs.TabPages.Add(page);
        }

        private List<int> SelectedMemberIds()
        {
            return memberGrid.SelectedCells.Cast<DataGridViewCell>().Select(x => x.OwningRow).Distinct().Select(r => Convert.ToInt32(r.Cells["Line"].Value)).ToList();
        }

        private void AddSelection(bool floor)
        {
            int skipped;
            List<int> ids = backend.SelectedLines(out skipped);
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Select timber beams or ribs (lines or members) in AxisVM first." + (skipped > 0 ? "\n\n" + skipped + " selected line(s) are not beams or ribs." : ""),
                    TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<int> target = floor ? st.FloorLines : st.RoofLines, other = floor ? st.RoofLines : st.FloorLines;
            int moved = 0, added = 0;
            foreach (int id in ids)
            {
                if (other.Remove(id)) moved++;
                if (!target.Contains(id)) { target.Add(id); added++; }
            }
            FillMembers();
            SetStatus(added + " line(s) added to " + (floor ? "Floor" : "Roof") + (moved > 0 ? ", " + moved + " moved from the other group" : "") +
                (skipped > 0 ? "; " + skipped + " non-beam line(s) skipped" : "") + ".");
        }

        /// <summary>One row per member: group, geometry, section, span type (own or default) and the last result.</summary>
        private void FillMembers()
        {
            filling = true;
            int first = memberGrid.FirstDisplayedScrollingRowIndex;
            memberGrid.Rows.Clear();
            foreach (var g in new[] { Tuple.Create("Floor", st.FloorLines), Tuple.Create("Roof", st.RoofLines) })
                foreach (int id in g.Item2)
                {
                    string[] info = backend.LineCells(id);
                    bool own = st.LineTypes.ContainsKey(id);
                    SpanType t = own ? st.LineTypes[id] : st.General.DefaultType;
                    string last = "";
                    if (run != null)
                    {
                        SpanResult r = run.Spans.FirstOrDefault(x => x.Span.Lines.Contains(id));
                        if (r != null) last = r.Error ?? (r.Reference + ",  UC " + F(r.Uc, 2) + "  " + r.Status);
                    }
                    int i = memberGrid.Rows.Add(id, g.Item1, info[0], info[1], info[2], t.ToString(), info[3], info[4], last);
                    DataGridViewCell tc = memberGrid.Rows[i].Cells["Type"];
                    tc.Style.ForeColor = own ? Color.FromArgb(20, 42, 65) : Color.FromArgb(150, 160, 170);
                    if (run != null)
                    {
                        SpanResult r = run.Spans.FirstOrDefault(x => x.Span.Lines.Contains(id));
                        if (r != null && !double.IsNaN(r.Uc)) memberGrid.Rows[i].Cells["Last"].Style.BackColor = Tint(SlsPlanView.UcColor(r.Uc));
                    }
                }
            if (first >= 0 && first < memberGrid.Rows.Count) memberGrid.FirstDisplayedScrollingRowIndex = first;
            memberCount.Text = st.FloorLines.Count + " floor and " + st.RoofLines.Count + " roof line(s)";
            filling = false;
        }

        // ------------------------------------------------------------------
        // 2 Loads
        // ------------------------------------------------------------------

        private void BuildLoadsTab()
        {
            TabPage page = Page("2  Loads");
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            top.Controls.Add(new Label { Text = "Combinations:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
            foreach (LoadSource ls in Enum.GetValues(typeof(LoadSource))) sourceBox.Items.Add(DescriptionEnumConverter.Text(ls));
            sourceBox.SelectedIndex = (int)st.LoadSource;
            sourceBox.SelectedIndexChanged += (s, e) => { st.LoadSource = (LoadSource)sourceBox.SelectedIndex; comboList.Enabled = st.LoadSource == LoadSource.AxisVMCombinations; UpdateLoadSummary(); };
            top.Controls.Add(sourceBox);
            top.Controls.Add(SmallButton("Reload from model", (s, e) => ReloadLoads()));
            top.Controls.Add(SmallButton("Reset to load groups", (s, e) => { st.Cases.Clear(); FillCaseGrid(); UpdateLoadSummary(); }));
            top.Controls.Add(SmallButton("Preview combinations", (s, e) => PreviewCombinations()));
            top.Controls.Add(loadSummary);

            StyleGrid(caseGrid);
            caseGrid.AllowUserToAddRows = false;
            caseGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Case", HeaderText = "Load case", ReadOnly = true, FillWeight = 160 });
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", ReadOnly = true, FillWeight = 70 });
            DataGridViewComboBoxColumn role = new DataGridViewComboBoxColumn { Name = "Role", HeaderText = "Role", FillWeight = 85, FlatStyle = FlatStyle.Flat };
            role.Items.AddRange("Permanent", "Variable", "Ignore");
            caseGrid.Columns.Add(role);
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = "Group", FillWeight = 110 });
            caseGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Excl", HeaderText = "Exclusive", FillWeight = 55 });
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Psi0", HeaderText = "ψ0", FillWeight = 40 });
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Psi1", HeaderText = "ψ1", FillWeight = 40 });
            caseGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Psi2", HeaderText = "ψ2", FillWeight = 40 });
            caseGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Imposed", HeaderText = "Imposed (mass)", FillWeight = 60 });
            caseGrid.CellValueChanged += (s, e) => { if (!filling) { CommitCases(); UpdateLoadSummary(); } };
            caseGrid.CurrentCellDirtyStateChanged += (s, e) => { if (caseGrid.IsCurrentCellDirty) caseGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            caseGrid.DataError += (s, e) => { e.ThrowException = false; };

            GroupBox comboBoxGroup = new GroupBox { Text = "AxisVM combinations (source 'Existing AxisVM load combinations')", Dock = DockStyle.Fill, Padding = new Padding(8) };
            FlowLayoutPanel cb = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            cb.Controls.Add(SmallButton("Tick all SLS", (s, e) => { for (int i = 0; i < comboList.Items.Count; i++) comboList.SetItemChecked(i, ((ComboItem)comboList.Items[i]).C.IsSls); }));
            cb.Controls.Add(SmallButton("Untick all", (s, e) => { for (int i = 0; i < comboList.Items.Count; i++) comboList.SetItemChecked(i, false); }));
            comboList.ItemCheck += (s, e) => BeginInvoke((Action)(() => { CommitCombos(); UpdateLoadSummary(); }));
            comboBoxGroup.Controls.Add(comboList);
            comboBoxGroup.Controls.Add(cb);

            GroupBox caseBox = new GroupBox
            {
                Text = "Load cases: role, group and ψ from the AxisVM load groups (edit to override; saved per model)", Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            caseBox.Controls.Add(caseGrid);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 760 };
            split.Panel1.Controls.Add(caseBox);
            split.Panel2.Controls.Add(comboBoxGroup);
            Label help = new Label
            {
                Dock = DockStyle.Bottom, Height = 54, ForeColor = Color.FromArgb(70, 84, 99), Padding = new Padding(4, 6, 4, 0),
                Text = "The add-on never writes combinations to the model: it reads each load case and sums them itself, so creep is applied per case " +
                       "(G with kdef, Q with ψ2·kdef). Auto builds every SLS combination like AxisVM (each variable group leading in turn, the others with ψ0; " +
                       "exclusive groups one case at a time). With existing combinations, factors come from AxisVM and ψ2 from the case's group."
            };
            page.Controls.Add(split);
            page.Controls.Add(help);
            page.Controls.Add(top);
            tabs.TabPages.Add(page);
            Shown += (s, e) => { try { split.SplitterDistance = (int)(split.Width * 0.62); } catch { } };
        }

        private class ComboItem
        {
            public ModelCombination C;
            public override string ToString() { return C.Name + "   [" + C.TypeName + "]"; }
        }

        private void ReloadLoads()
        {
            try
            {
                modelCases = backend.ReadLoadCases();
                modelCombos = backend.ReadCombinations();
            }
            catch (Exception ex) { SetStatus("Could not read loads: " + ex.Message); }
            FillCaseGrid();
            filling = true;
            comboList.Items.Clear();
            foreach (ModelCombination c in modelCombos)
                comboList.Items.Add(new ComboItem { C = c }, st.SelectedCombinations.Contains(c.Name));
            comboList.Enabled = st.LoadSource == LoadSource.AxisVMCombinations;
            filling = false;
            UpdateLoadSummary();
        }

        private void FillCaseGrid()
        {
            filling = true;
            caseGrid.Rows.Clear();
            Dictionary<int, SlsLoadCase> eff = TimberSlsBackend.EffectiveCases(modelCases, st);
            foreach (ModelLoadCase mc in modelCases)
            {
                SlsLoadCase c = eff[mc.Id];
                caseGrid.Rows.Add(mc.Name, mc.Type, c.Role.ToString(), c.Group, c.Exclusive, F(c.Psi0, 2), F(c.Psi1, 2), F(c.Psi2, 2), c.Imposed);
            }
            filling = false;
        }

        /// <summary>The grid becomes the per-case overrides (only rows that differ from the model's load groups).</summary>
        private void CommitCases()
        {
            if (caseGrid.Rows.Count != modelCases.Count) return;
            List<CaseOverride> list = new List<CaseOverride>();
            for (int i = 0; i < modelCases.Count; i++)
            {
                DataGridViewRow r = caseGrid.Rows[i];
                LoadRole role;
                Enum.TryParse(Convert.ToString(r.Cells["Role"].Value), out role);
                CaseOverride o = new CaseOverride
                {
                    Name = modelCases[i].Name, Role = role, Group = Convert.ToString(r.Cells["Group"].Value) ?? "",
                    Exclusive = Convert.ToBoolean(r.Cells["Excl"].Value ?? false),
                    Psi0 = ParseD(r.Cells["Psi0"].Value, 0), Psi1 = ParseD(r.Cells["Psi1"].Value, 0), Psi2 = ParseD(r.Cells["Psi2"].Value, 0),
                    Imposed = Convert.ToBoolean(r.Cells["Imposed"].Value ?? false)
                };
                SlsLoadCase d = modelCases[i].Default;
                bool same = d.Role == o.Role && (d.Group ?? "") == o.Group && d.Exclusive == o.Exclusive && Math.Abs(d.Psi0 - o.Psi0) < 1e-9 &&
                            Math.Abs(d.Psi1 - o.Psi1) < 1e-9 && Math.Abs(d.Psi2 - o.Psi2) < 1e-9 && d.Imposed == o.Imposed;
                if (!same) list.Add(o);
            }
            st.Cases = list;
        }

        private void CommitCombos()
        {
            if (filling) return;
            st.SelectedCombinations = comboList.CheckedItems.Cast<ComboItem>().Select(c => c.C.Name).ToList();
        }

        private List<SlsCombination> CurrentCombinations(out List<string> msgs)
        {
            msgs = new List<string>();
            return backend.Combinations(st, TimberSlsBackend.EffectiveCases(modelCases, st), msgs);
        }

        private void UpdateLoadSummary()
        {
            if (filling) return;
            try
            {
                List<string> msgs;
                List<SlsCombination> c = CurrentCombinations(out msgs);
                loadSummary.Text = c.Count + " combination(s) will be checked" + (msgs.Count > 0 ? "  (" + msgs[0] + ")" : "");
            }
            catch (Exception ex) { loadSummary.Text = ex.Message; }
        }

        private void PreviewCombinations()
        {
            List<string> msgs;
            List<SlsCombination> combos = CurrentCombinations(out msgs);
            Dictionary<int, SlsLoadCase> cases = TimberSlsBackend.EffectiveCases(modelCases, st);
            StringBuilder sb = new StringBuilder();
            foreach (string m in msgs) sb.AppendLine("Note: " + m);
            sb.AppendLine(combos.Count + " combination(s). Per load case: inst = factor in w_inst, creep = share × kdef, w1 = share in w1 (G + ψ2Q).");
            sb.AppendLine();
            foreach (SlsCombination c in combos.Take(400))
            {
                sb.AppendLine(c.Name);
                foreach (SlsTerm t in c.Terms)
                    sb.AppendLine("    " + (cases.ContainsKey(t.CaseId) ? cases[t.CaseId].Name : "#" + t.CaseId).PadRight(28) +
                        " inst " + F(t.Inst, 2) + "   creep " + F(t.Creep, 2) + "   w1 " + F(t.W1, 2) + "   freq " + F(t.Freq, 2));
            }
            if (combos.Count > 400) sb.AppendLine("... " + (combos.Count - 400) + " more");
            ShowText("Combinations", sb.ToString());
        }

        private void ShowText(string title, string text)
        {
            Form f = new Form { Text = title, ClientSize = new Size(820, 560), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            Theme.Initialize(f);
            f.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), Text = text, BackColor = Color.White });
            f.Show(this);
        }

        // ------------------------------------------------------------------
        // 3 Deflection
        // ------------------------------------------------------------------

        private void BuildDeflectionTab()
        {
            TabPage page = Page("3  Deflection");
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            top.Controls.Add(new Label { Text = "Group:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
            groupBox.Items.AddRange(new object[] { "Floor", "Roof" });
            groupBox.SelectedIndex = 0;
            groupBox.SelectedIndexChanged += (s, e) => ShowGroup((string)groupBox.SelectedItem);
            top.Controls.Add(groupBox);
            top.Controls.Add(new Label { Text = "    Each group has its own creep model, kdef, precamber and limit rows.", AutoSize = true, Margin = new Padding(0, 8, 0, 0), ForeColor = Color.FromArgb(70, 84, 99) });

            GroupBox left = new GroupBox { Text = "Creep, kdef, cantilever, precamber", Dock = DockStyle.Fill, Padding = new Padding(8) };
            left.Controls.Add(groupGrid);

            GroupBox right = new GroupBox { Text = "Limits: every enabled row is checked; relative (l_rep / x) and absolute (mm), the smaller governs", Dock = DockStyle.Fill, Padding = new Padding(8) };
            FlowLayoutPanel presetBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            presetBar.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
            presetBox.Items.AddRange(TimberSlsPresets.Names);
            presetBox.SelectedIndex = 0;
            presetBar.Controls.Add(presetBox);
            presetBar.Controls.Add(SmallButton("Apply preset", (s, e) =>
            {
                st.Group(shownGroup).Rows = TimberSlsPresets.Rows((string)presetBox.SelectedItem);
                FillRows();
            }));

            StyleGrid(rowGrid);
            rowGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            rowGrid.AllowUserToAddRows = true;
            rowGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On", FillWeight = 30 });
            DataGridViewComboBoxColumn q = new DataGridViewComboBoxColumn { Name = "Check", HeaderText = "Check", FillWeight = 170, FlatStyle = FlatStyle.Flat };
            foreach (SlsQuantity v in Enum.GetValues(typeof(SlsQuantity))) q.Items.Add(TimberSlsMath.QuantityName(v));
            rowGrid.Columns.Add(q);
            DataGridViewComboBoxColumn ratio = new DataGridViewComboBoxColumn { Name = "Ratio", HeaderText = "Relative  l_rep / x", FillWeight = 90, FlatStyle = FlatStyle.Flat };
            ratio.Items.AddRange("-", "150", "200", "250", "300", "333.33", "350", "400", "500");
            rowGrid.Columns.Add(ratio);
            rowGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Abs", HeaderText = "Absolute [mm] (blank = none)", FillWeight = 90 });
            rowGrid.DataError += (s, e) => { e.ThrowException = false; };
            rowGrid.EditingControlShowing += (s, e) =>
            {
                // The ratio dropdown also takes typed values (custom)
                ComboBox cb = e.Control as ComboBox;
                if (cb != null && rowGrid.CurrentCell.ColumnIndex == 2) { cb.DropDownStyle = ComboBoxStyle.DropDown; }
            };
            rowGrid.CellValidating += (s, e) =>
            {
                if (e.ColumnIndex != 2) return;
                string text = Convert.ToString(e.FormattedValue);
                DataGridViewComboBoxColumn col = (DataGridViewComboBoxColumn)rowGrid.Columns[2];
                if (!string.IsNullOrWhiteSpace(text) && !col.Items.Contains(text)) col.Items.Add(text);
                rowGrid.Rows[e.RowIndex].Cells[2].Value = text;
            };

            TextBox help = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 92, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
                Font = new Font("Consolas", 8F),
                Text = "Quantities (doc ec5-sls-timber.md §4), per span, w_i = instantaneous relative deflection of load case i (E_mean):\r\n" +
                       "  w_inst            = Σ G + Q1 + Σ ψ0,i Qi                        (characteristic, 2.2.3(2))\r\n" +
                       "  w_creep           = kdef · (G + Σ ψ2 Q)                         (creep model EC5)\r\n" +
                       "  w_fin             = G(1+kdef) + Q1(1+ψ2,1 kdef) + Σ Qi(ψ0,i+ψ2,i kdef)   (2.2–2.5)\r\n" +
                       "  w_net,fin (w_eind) = w_fin − w_c                                  (7.2)\r\n" +
                       "  w_bij (Harff)     = G·kdef + Q1(1+ψ2 kdef−ψ2) + …  = w_fin − w1, w1 = G + ψ2Q   (NL NA, quasi-permanent reading)\r\n" +
                       "  w_bij (NA freq.)  = kdef(G + ψ2Q) + ψ1Q1 + ψ2Qi                  (NA frequent reading, w1 = G)\r\n" +
                       "  w_max (quasi-p.)  = (G + ψ2Q)(1+kdef) − w_c                       (appearance)\r\n" +
                       "l_rep = span, or cantilever factor × length. NL defaults: floor w_eind ≤ l/250 and w_bij ≤ 3/1000·l (l/333); brittle partitions l/500; roofs per preset."
            };
            // Limit rows on top, the explanation (drawing + table) below; the splitter can be dragged
            SplitContainer rowsAndGuide = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            rowsAndGuide.Panel1.Controls.Add(rowGrid);
            rowsAndGuide.Panel2.Controls.Add(termsGuide);
            right.Controls.Add(rowsAndGuide);
            right.Controls.Add(presetBar);
            Shown += (s, e) => { try { rowsAndGuide.SplitterDistance = 112; } catch { } };
            help.Dispose();
            rowGrid.CurrentCellChanged += (s, e) =>
            {
                if (rowGrid.CurrentRow == null || rowGrid.CurrentRow.IsNewRow) { termsGuide.Highlight = null; return; }
                string qn = Convert.ToString(rowGrid.CurrentRow.Cells["Check"].Value);
                termsGuide.Highlight = Enum.GetValues(typeof(SlsQuantity)).Cast<SlsQuantity>().Where(v => TimberSlsMath.QuantityName(v) == qn).Cast<SlsQuantity?>().FirstOrDefault();
            };

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            page.Controls.Add(split);
            page.Controls.Add(top);
            tabs.TabPages.Add(page);
            Shown += (s, e) => { try { split.SplitterDistance = (int)(split.Width * 0.42); } catch { } };
        }

        private void ShowGroup(string name)
        {
            if (rowsShown) CommitRows();
            rowsShown = true;
            shownGroup = name;
            groupGrid.SelectedObject = st.Group(name);
            FillRows();
        }

        private void FillRows()
        {
            rowGrid.Rows.Clear();
            DataGridViewComboBoxColumn ratioCol = (DataGridViewComboBoxColumn)rowGrid.Columns["Ratio"];
            foreach (LimitRow r in st.Group(shownGroup).Rows)
            {
                string ratio = r.Ratio > 0 ? r.Ratio.ToString("0.##", CultureInfo.InvariantCulture) : "-";
                if (!ratioCol.Items.Contains(ratio)) ratioCol.Items.Add(ratio);
                rowGrid.Rows.Add(r.Enabled, TimberSlsMath.QuantityName(r.Quantity), ratio, r.AbsMm > 0 ? r.AbsMm.ToString("0.##", CultureInfo.InvariantCulture) : "");
            }
        }

        private void CommitRows()
        {
            if (rowGrid.Columns.Count == 0 || !rowsShown) return;
            rowGrid.EndEdit();
            List<LimitRow> rows = new List<LimitRow>();
            foreach (DataGridViewRow r in rowGrid.Rows)
            {
                if (r.IsNewRow) continue;
                string qn = Convert.ToString(r.Cells["Check"].Value);
                SlsQuantity q = Enum.GetValues(typeof(SlsQuantity)).Cast<SlsQuantity>().FirstOrDefault(v => TimberSlsMath.QuantityName(v) == qn);
                if (string.IsNullOrEmpty(qn)) continue;
                rows.Add(new LimitRow(q, ParseD(r.Cells["Ratio"].Value, 0), ParseD(r.Cells["Abs"].Value, 0))
                {
                    Enabled = r.Cells["On"].Value == null || Convert.ToBoolean(r.Cells["On"].Value)
                });
            }
            st.Group(shownGroup).Rows = rows;
        }

        // ------------------------------------------------------------------
        // 4 Vibration
        // ------------------------------------------------------------------

        private static readonly string[] VibCols = { "b", "s", "eiT", "eiDeckL", "g", "q", "m" };

        private void BuildVibrationTab()
        {
            TabPage page = Page("4  Vibration (Floor)");
            vibGrid.SelectedObject = st.Vibration;
            vibGrid.PropertyValueChanged += (s, e) => { vibGrid.Refresh(); UpdateVibHelp(); FillVibMembers(); };
            GroupBox left = new GroupBox { Text = "Method, frequency, damping and the default floor field (for members without own values)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            left.Controls.Add(vibGrid);
            GroupBox right = new GroupBox { Text = "Method", Dock = DockStyle.Fill, Padding = new Padding(8) };
            right.Controls.Add(vibHelp);

            // Floor field per member: the main input, every member with its own values
            GroupBox members = new GroupBox
            {
                Text = "Floor field per member  (grey italic = default from below; type to give a member its own value; select several cells and type, right-click or Ctrl+V to set them all; Delete = back to default)",
                Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            StyleGrid(vibMemberGrid);
            vibMemberGrid.AllowUserToAddRows = false;
            vibMemberGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            vibMemberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "Line", ReadOnly = true, FillWeight = 40 });
            vibMemberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Member", HeaderText = "Member", ReadOnly = true, FillWeight = 150 });
            string[] heads = { "Floor width b [m]", "Joist spacing s [m]", "(EI)_T transverse [Nm²/m]", "Extra (EI)_L deck [Nm²/m]", "Permanent g [kN/m²]", "Imposed q [kN/m²]", "Mass m [kg/m²]" };
            for (int i = 0; i < VibCols.Length; i++)
                vibMemberGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = VibCols[i], HeaderText = heads[i], FillWeight = 62 });
            vibMemberGrid.CellValueChanged += (s, e) =>
            {
                if (filling || e.RowIndex < 0 || e.ColumnIndex < 2) return;
                DataGridViewCell cell = vibMemberGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                cell.Tag = string.IsNullOrWhiteSpace(Convert.ToString(cell.Value)) ? null : "own";
                CommitVibMembers();
                BeginInvoke((Action)FillVibMembers);   // refresh after the edit has finished
            };
            vibMemberGrid.DataError += (s, e) => { e.ThrowException = false; };
            FlowLayoutPanel vbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            vbar.Controls.Add(SmallButton("Copy the first selected row to the other selected rows", (s, e) => CopyVibRow()));
            vbar.Controls.Add(SmallButton("Reset selected rows to the defaults", (s, e) =>
            {
                foreach (DataGridViewRow r in vibMemberGrid.SelectedCells.Cast<DataGridViewCell>().Select(x => x.OwningRow).Distinct())
                    st.VibOverrides.Remove(Convert.ToInt32(r.Cells["Line"].Value));
                FillVibMembers();
            }));
            vbar.Controls.Add(new Label { Text = "Black = this member's own value. Mass: grey = from its g and q (2025: + 10 % of q).", AutoSize = true, Margin = new Padding(10, 9, 0, 0), ForeColor = Color.FromArgb(70, 84, 99) });
            members.Controls.Add(vibMemberGrid);
            members.Controls.Add(vbar);
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedIndex == 3) FillVibMembers(); };

            SplitContainer bottom = new SplitContainer { Dock = DockStyle.Fill };
            bottom.Panel1.Controls.Add(left);
            bottom.Panel2.Controls.Add(right);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(members);
            split.Panel2.Controls.Add(bottom);
            page.Controls.Add(split);
            tabs.TabPages.Add(page);
            Shown += (s, e) =>
            {
                try { split.SplitterDistance = (int)(split.Height * 0.46); bottom.SplitterDistance = (int)(bottom.Width * 0.55); } catch { }
            };
            UpdateVibHelp();
        }

        private void UpdateVibHelp()
        {
            VibrationSettings v = st.Vibration;
            string common = "\r\nFloor field: (EI)_L = E_mean·I_joist/s + deck; mass from the permanent load" +
                            (v.Method == VibrationMethod.En1995_2025 ? " + 10 % of the imposed load (9.3.1.2)" : " (unloaded floor, 7.3.3(3))") +
                            ", ·1000/9.81, unless a mass is typed.\r\nSpans: a two-span member is a double span floor; more spans are taken as one span on the largest span.";
            switch (v.Method)
            {
                case VibrationMethod.Ec5_2004:
                    vibHelp.Text = "EN 1995-1-1:2004 §7.3.3 (residential floors)\r\n\r\n" +
                        "f1 = λ²/(2π l²)·√((EI)_L/m)     (7.5, generalised; λ = π pinned)\r\n" +
                        "f1 ≤ 8 Hz → special investigation (7.3.3(1)); a and v are still shown.\r\n" +
                        "a = c·F·l³/EI_joist·k_dist·k_amp ≤ a_lim [mm/kN]     (7.3)\r\n" +
                        "k_dist = max{0.38 − 0.08·ln[14(EI)_b/s⁴]; 0.30}  ((EI)_b N·mm²/m, s mm)\r\n" +
                        "n40 = {[(40/f1)² − 1]·(b/l)⁴·(EI)_L/(EI)_b}^0.25     (7.7)\r\n" +
                        "v = 4(0.4 + 0.6 n40)/(m b l + 200) ≤ b^(f1ζ − 1)     (7.6), (7.4)\r\n" +
                        "Defaults: ζ = 0.01; a_lim and b per UK NA (VERIFY against the NA you use)." + common;
                    break;
                case VibrationMethod.En1995_2025:
                    vibHelp.Text = "EN 1995-1-1:2025 §9.3.2 (use categories A, B, C1, C3, D)\r\n\r\n" +
                        "f1 = k_e1·k_e2·π/(2l²)·√((EI)_L/m)  (9.13), or 18/√w_sys (9.15); flexible supports (9.16)\r\n" +
                        "f1,lim = 4·f_w (9.12), ≥ 8 Hz for multi-family / levels I-II. f1 < f1,lim → resonant\r\n" +
                        "Frequency: f1 ≥ 4.5 Hz (I-IV), f1 ≥ f1,lim (V-VIII)\r\n" +
                        "Stiffness: w_1kN = F l³/(48 (EI)_L b_ef) ≤ w_lim  (9.17); b_ef (9.18) / (9.19); (9.20)\r\n" +
                        "Velocity: I_mod = 42 f_w^1.43/f1^1.3; v1 = k_red·I_mod/(M*+70); k_imp; v_rms = v_tot·(0.65−0.01f1)(1.22−11ζ)η ≤ v_rms,lim\r\n" +
                        "Acceleration (resonant): a_rms = k_res μ_res F_dyn/(√2·2ζ M*) ≤ a_rms,lim\r\n" +
                        "Table 9.1 limits are editable (category 7). The result shows the best level achieved and the UC against the target level.\r\n" +
                        "Note: 9.3.2.2(6), acceleration and velocity apply to the floor of a room, not to single girders." + common;
                    break;
                default:
                    vibHelp.Text = "Vibration is off. Choose a method under '1 Method'.";
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 5 Results
        // ------------------------------------------------------------------

        private void BuildResultsTab()
        {
            TabPage page = Page("5  Results");
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            top.Controls.Add(new Label { Text = "Combination:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
            top.Controls.Add(comboBox);
            top.Controls.Add(new Label { Text = "Colour:", AutoSize = true, Margin = new Padding(10, 8, 4, 0) });
            colourBox.Items.AddRange(new object[] { "Utilisation (max)", "Deflection", "Vibration" });
            colourBox.SelectedIndex = 0;
            top.Controls.Add(colourBox);
            top.Controls.Add(SmallButton("Colour AxisVM model", (s, e) => ShowOverlay(true)));
            top.Controls.Add(SmallButton("Clear colours", (s, e) => { backend.HideOverlay(); overlayShown = false; SetStatus("Overlay removed."); }));
            top.Controls.Add(SmallButton("Select in AxisVM", (s, e) => SelectCurrent()));
            top.Controls.Add(SmallButton("Export report", (s, e) => ExportReport()));
            top.Controls.Add(SmallButton("Export CSV", (s, e) => ExportCsv()));
            top.Controls.Add(SmallButton("Copy table", (s, e) => CopyTable()));
            top.Controls.Add(autoOverlay);
            top.SetFlowBreak(autoOverlay, true);
            ComboBox scaleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            foreach (ColourScale cs in Enum.GetValues(typeof(ColourScale))) scaleBox.Items.Add(DescriptionEnumConverter.Text(cs));
            scaleBox.SelectedIndex = (int)st.Display.Scale;
            NumericUpDown lineBox = new NumericUpDown { Minimum = 1, Maximum = 20, DecimalPlaces = 0, Width = 52, Value = (decimal)Math.Max(1, Math.Min(20, st.Display.LineWidth)) };
            NumericUpDown textBox = new NumericUpDown { Minimum = 5, Maximum = 30, DecimalPlaces = 0, Width = 52, Value = (decimal)Math.Max(5, Math.Min(30, st.Display.LabelSize)) };
            NumericUpDown maxBox = new NumericUpDown { Minimum = 1.0M, Maximum = 5.0M, DecimalPlaces = 1, Increment = 0.1M, Width = 58, Value = (decimal)Math.Max(1, Math.Min(5, st.Display.LegendMax)) };
            CheckBox labelsBox = new CheckBox { Text = "UC labels", Checked = st.Display.Labels, AutoSize = true, Margin = new Padding(10, 8, 0, 0) };
            EventHandler apply = (s, e) =>
            {
                st.Display.Scale = (ColourScale)scaleBox.SelectedIndex;
                st.Display.LineWidth = (double)lineBox.Value;
                st.Display.LabelSize = (double)textBox.Value;
                st.Display.LegendMax = (double)maxBox.Value;
                st.Display.Labels = labelsBox.Checked;
                ApplyDisplay();
            };
            scaleBox.SelectedIndexChanged += apply; lineBox.ValueChanged += apply; textBox.ValueChanged += apply; maxBox.ValueChanged += apply;
            labelsBox.CheckedChanged += apply;
            Func<string, Label> lab = t => new Label { Text = t, AutoSize = true, Margin = new Padding(10, 8, 4, 0) };
            top.Controls.Add(lab("Display:"));
            top.Controls.Add(scaleBox);
            top.Controls.Add(lab("Line width"));
            top.Controls.Add(lineBox);
            top.Controls.Add(lab("Text size"));
            top.Controls.Add(textBox);
            top.Controls.Add(lab("Legend max UC"));
            top.Controls.Add(maxBox);
            top.Controls.Add(labelsBox);
            TimberSlsBackend.Scale = st.Display.Scale;
            plan.Display = st.Display;
            legendStrip.Display = st.Display;

            comboBox.SelectedIndexChanged += (s, e) => RefreshResults();
            colourBox.SelectedIndexChanged += (s, e) => RefreshResults();

            StyleGrid(resultGrid);
            resultGrid.AllowUserToAddRows = false;
            resultGrid.ReadOnly = true;
            resultGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            resultGrid.MultiSelect = false;
            resultGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            foreach (string c in new[] { "Group", "Span", "l [m]", "Checked as", "Section", "kdef", "Governing check", "Combination", "w [mm]", "Limit [mm]", "UC defl.", "f1 [Hz]", "UC vib.", "Vibration", "UC", "Status" })
                resultGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c, Name = c, SortMode = DataGridViewColumnSortMode.Automatic });
            resultGrid.SelectionChanged += (s, e) => ShowSelected();
            resultGrid.SortCompare += (s, e) =>
            {
                double a, b;
                if (double.TryParse(Convert.ToString(e.CellValue1), NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    double.TryParse(Convert.ToString(e.CellValue2), NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                { e.SortResult = a.CompareTo(b); e.Handled = true; }
            };
            plan.SpanClicked += (s, i) => SelectSpan(i);

            Panel left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(resultGrid);
            left.Controls.Add(notes);

            TabControl bottom = new TabControl { Dock = DockStyle.Fill };
            TabPage dPage = new TabPage("Deflection build-up") { BackColor = Color.White };
            dPage.Controls.Add(diagram);
            TabPage tPage = new TabPage("Details (all values)") { BackColor = Color.White };
            tPage.Controls.Add(details);
            bottom.TabPages.Add(dPage);
            bottom.TabPages.Add(tPage);

            // Only the colour scale here: the members themselves are coloured in AxisVM
            SplitContainer right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1 };
            right.Panel1.Controls.Add(legendStrip);
            right.Panel2.Controls.Add(bottom);
            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            page.Controls.Add(split);
            page.Controls.Add(top);
            tabs.TabPages.Add(page);
            Shown += (s, e) =>
            {
                try { split.SplitterDistance = (int)(split.Width * 0.5); right.SplitterDistance = 64; } catch { }
            };
        }

        /// <summary>Redraws the window (and the AxisVM colours, when shown) with the display settings.</summary>
        private void ApplyDisplay()
        {
            TimberSlsBackend.Scale = st.Display.Scale;
            plan.Display = st.Display;
            legendStrip.Display = st.Display;
            legendStrip.Invalidate();
            if (run == null) { plan.Invalidate(); return; }
            RefreshResults();
            if (overlayShown) ShowOverlay(false);
        }

        /// <summary>The default (Vibration tab) value of a member column; mass from that member's g and q.</summary>
        private double VibDefault(string col, double g, double q)
        {
            VibrationSettings v = st.Vibration;
            switch (col)
            {
                case "b": return v.Width;
                case "s": return v.Spacing;
                case "eiT": return v.EiTransverse;
                case "eiDeckL": return v.EiDeckLongitudinal;
                case "g": return v.PermanentLoad;
                case "q": return v.ImposedLoad;
                default:
                    return v.MassOverride > 0 ? v.MassOverride : (g + (v.Method == VibrationMethod.En1995_2025 ? 0.1 * q : 0)) * 1000.0 / 9.81;
            }
        }

        private static double OwnValue(VibOverride o, string col)
        {
            switch (col)
            {
                case "b": return o.Width;
                case "s": return o.Spacing;
                case "eiT": return o.EiT;
                case "eiDeckL": return o.EiDeckL;
                case "g": return o.PermanentLoad;
                case "q": return o.ImposedLoad;
                default: return o.Mass;
            }
        }

        /// <summary>
        /// One row per Floor line showing the values that member actually uses: its own values in black, the
        /// defaults in grey italic.
        /// </summary>
        private void FillVibMembers()
        {
            filling = true;
            int first = vibMemberGrid.FirstDisplayedScrollingRowIndex;
            vibMemberGrid.Rows.Clear();
            Func<double, string> fmt = x => double.IsNaN(x) ? "" : x.ToString("0.###", CultureInfo.InvariantCulture);
            using (Font italic = new Font(vibMemberGrid.Font, FontStyle.Italic))
            using (Font bold = new Font(vibMemberGrid.Font, FontStyle.Bold))
            {
                foreach (int id in st.FloorLines)
                {
                    VibOverride o;
                    st.VibOverrides.TryGetValue(id, out o);
                    if (o == null) o = new VibOverride { Line = id };
                    int i = vibMemberGrid.Rows.Add(id, backend.Describe(id).Replace("Line " + id + "   ", ""));
                    DataGridViewRow row = vibMemberGrid.Rows[i];
                    double g = VibOverride.Or(o.PermanentLoad, st.Vibration.PermanentLoad), q = VibOverride.Or(o.ImposedLoad, st.Vibration.ImposedLoad);
                    foreach (string col in VibCols)
                    {
                        double own = OwnValue(o, col);
                        DataGridViewCell cell = row.Cells[col];
                        if (!double.IsNaN(own))
                        {
                            cell.Value = fmt(own);
                            cell.Tag = "own";
                            cell.Style.ForeColor = Color.FromArgb(20, 42, 65);
                            cell.Style.Font = bold;
                        }
                        else
                        {
                            cell.Value = fmt(VibDefault(col, g, q));
                            cell.Tag = null;
                            cell.Style.ForeColor = Color.FromArgb(150, 160, 170);
                            cell.Style.Font = italic;
                        }
                    }
                }
                // DataGridView keeps references to the fonts it renders: give each cell its own copy
                foreach (DataGridViewRow r in vibMemberGrid.Rows)
                    foreach (string col in VibCols) r.Cells[col].Style.Font = new Font(r.Cells[col].Style.Font, r.Cells[col].Style.Font.Style);
            }
            if (first >= 0 && first < vibMemberGrid.Rows.Count) vibMemberGrid.FirstDisplayedScrollingRowIndex = first;
            filling = false;
        }

        /// <summary>The table becomes the per-member overrides: only cells marked as the member's own value.</summary>
        private void CommitVibMembers()
        {
            foreach (DataGridViewRow r in vibMemberGrid.Rows)
            {
                int id = Convert.ToInt32(r.Cells["Line"].Value);
                Func<string, double> d = col =>
                {
                    DataGridViewCell cell = r.Cells[col];
                    string s = Convert.ToString(cell.Value);
                    return (string)cell.Tag == "own" && !string.IsNullOrWhiteSpace(s) ? ParseD(s, double.NaN) : double.NaN;
                };
                VibOverride o = new VibOverride
                {
                    Line = id, Width = d("b"), Spacing = d("s"), EiT = d("eiT"), EiDeckL = d("eiDeckL"), PermanentLoad = d("g"), ImposedLoad = d("q"), Mass = d("m")
                };
                if (o.IsEmpty) st.VibOverrides.Remove(id); else st.VibOverrides[id] = o;
            }
        }

        private void CopyVibRow()
        {
            List<DataGridViewRow> rows = vibMemberGrid.SelectedCells.Cast<DataGridViewCell>().Select(x => x.OwningRow).Distinct().OrderBy(x => x.Index).ToList();
            if (rows.Count < 2) { SetStatus("Select two or more rows: the first one is copied to the others."); return; }
            foreach (DataGridViewRow r in rows.Skip(1))
                foreach (string col in VibCols) { r.Cells[col].Value = rows[0].Cells[col].Value; r.Cells[col].Tag = rows[0].Cells[col].Tag; }
            CommitVibMembers();
            FillVibMembers();
        }

        // ------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------

        private void Commit()
        {
            if (vibMemberGrid.Rows.Count > 0) CommitVibMembers();
            CommitRows();
            CommitCases();
            CommitCombos();
        }

        private void RunCheck()
        {
            Commit();
            st.Save(backend.ModelPath);
            if (st.FloorLines.Count + st.RoofLines.Count == 0)
            {
                MessageBox.Show(this, "Add members to Floor or Roof first (tab 1).", TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                tabs.SelectedIndex = 0;
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                run = backend.Run(st, modelCases);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The check failed: " + ex.Message, TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { Cursor = Cursors.Default; }

            filling = true;
            comboBox.Items.Clear();
            comboBox.Items.Add("Governing (all " + run.Combinations.Count + " combinations)");
            foreach (SlsCombination c in run.Combinations) comboBox.Items.Add(c.Name);
            comboBox.SelectedIndex = 0;
            filling = false;
            notes.Text = run.Notes.Count == 0 ? "No notes." : string.Join("\r\n", run.Notes);
            RefreshResults();
            tabs.SelectedIndex = 4;
            int fails = run.Spans.Count(r => r.Status != "OK");
            SetStatus(run.Spans.Count + " span(s) checked, " + fails + " not OK, max UC " +
                (run.Spans.Count == 0 ? "-" : F(run.Spans.Where(r => !double.IsNaN(r.Uc)).Select(r => r.Uc).DefaultIfEmpty(0).Max(), 2)) + ".");
            if (autoOverlay.Checked) ShowOverlay(false);
            FillMembers();
        }

        /// <summary>The selected combination after the group's creep model; null = governing over all.</summary>
        private SlsCombination ComboFor(SpanResult r, string name)
        {
            SlsCombination c = run.Combinations.FirstOrDefault(x => x.Name == name);
            if (c == null) return null;
            return TimberSlsMath.ApplyCreepModel(c, r.Settings.CreepModel, r.Settings.CreepShareG, r.Settings.CreepShareQ, run.Cases);
        }

        /// <summary>Rows and governing row of a span for the combination selector.</summary>
        private List<RowResult> RowsFor(SpanResult r, out RowResult gov)
        {
            gov = r.Governing;
            if (comboBox.SelectedIndex <= 0 || r.Error != null) return r.Rows;
            SlsCombination c = ComboFor(r, (string)comboBox.SelectedItem);
            List<RowResult> rows = new List<RowResult>();
            gov = null;
            if (c == null) return rows;
            foreach (LimitRow row in r.Settings.Rows.Where(x => x.Enabled))
            {
                RowResult rr = TimberSlsMath.CheckRow(r.Defl, new[] { c }, row, r.Kdef, r.Settings.PrecamberMm, r.LRep);
                rows.Add(rr);
                if (gov == null || rr.Uc > gov.Uc) gov = rr;
            }
            return rows;
        }

        private double ShownUc(SpanResult r, RowResult gov)
        {
            double d = gov == null ? 0 : gov.Uc, v = double.IsNaN(r.VibUc) ? 0 : r.VibUc;
            if (r.Error != null) return double.NaN;
            switch (colourBox.SelectedIndex)
            {
                case 1: return d;
                case 2: return double.IsNaN(r.VibUc) ? double.NaN : v;
                default: return Math.Max(d, v);
            }
        }

        /// <summary>Coloured pieces: the governing row's local utilisation along each span.</summary>
        private List<TimberSlsBackend.Piece> Pieces(out Dictionary<SpanInfo, double> spanUc)
        {
            List<TimberSlsBackend.Piece> pieces = new List<TimberSlsBackend.Piece>();
            spanUc = new Dictionary<SpanInfo, double>();
            foreach (SpanResult r in run.Spans)
            {
                RowResult gov;
                RowsFor(r, out gov);
                double shown = ShownUc(r, gov);
                spanUc[r.Span] = shown;
                SpanInfo s = r.Span;
                bool local = colourBox.SelectedIndex != 2 && gov != null && r.Error == null && !double.IsInfinity(gov.Limit) && gov.Limit > 0;
                if (!local)
                {
                    pieces.Add(new TimberSlsBackend.Piece { Span = s, X0 = 0, X1 = s.Length, Uc = shown });
                    continue;
                }
                SlsCombination c = ComboFor(r, gov.Combination);
                if (c == null) { pieces.Add(new TimberSlsBackend.Piece { Span = s, X0 = 0, X1 = s.Length, Uc = shown }); continue; }
                double[] w = TimberSlsMath.Evaluate(r.Defl, c, gov.Row.Quantity, r.Kdef, r.Settings.PrecamberMm);
                double vib = colourBox.SelectedIndex == 0 && !double.IsNaN(r.VibUc) ? r.VibUc : 0;
                double[] x = r.Defl.X;
                for (int i = 0; i + 1 < x.Length; i++)
                {
                    double uc = Math.Max((Math.Abs(w[i]) + Math.Abs(w[i + 1])) / 2 / gov.Limit, vib);
                    pieces.Add(new TimberSlsBackend.Piece { Span = s, X0 = s.GeoX(x[i]), X1 = s.GeoX(x[i + 1]), Uc = uc });
                }
            }
            return pieces;
        }

        private void RefreshResults()
        {
            if (filling || run == null) return;
            int keep = SelectedSpanIndex();
            resultGrid.Rows.Clear();
            foreach (SpanResult r in run.Spans)
            {
                RowResult gov;
                RowsFor(r, out gov);
                SpanInfo s = r.Span;
                double f1 = r.Vib04 != null ? r.Vib04.F1 : r.Vib25 != null ? r.Vib25.F1 : double.NaN;
                double uc = r.Error != null ? double.NaN : Math.Max(gov == null ? 0 : gov.Uc, double.IsNaN(r.VibUc) ? 0 : r.VibUc);
                string statusText = r.Error != null ? "Error" : uc > 1 ? "Fails" : r.Status == "f1 ≤ 8 Hz" ? "f1 ≤ 8 Hz" : "OK";
                string checkedAs = r.Error != null ? "" : (s.Cantilever ? (s.RootAtEnd ? "cantilever ←" : "cantilever →") : "span") + (r.Type == SpanType.Auto ? " (auto)" : "");
                int i = resultGrid.Rows.Add(s.Group, s.Label, F(s.Length, 3), checkedAs, s.Material + " " + s.Section,
                    F(r.Kdef, 2), gov == null ? (r.Error ?? "-") : gov.Row.Describe(), gov == null ? "" : gov.Combination,
                    gov == null ? "" : F(gov.W, 2), gov == null ? "" : F(gov.Limit, 2), gov == null ? "" : F(gov.Uc, 2),
                    double.IsNaN(f1) ? "" : F(f1, 2), double.IsNaN(r.VibUc) ? "" : F(r.VibUc, 2), r.VibNote, F(uc, 2), statusText);
                DataGridViewRow row = resultGrid.Rows[i];
                row.Tag = r;
                foreach (string col in new[] { "UC defl.", "UC vib.", "UC" })
                {
                    double v;
                    if (!double.TryParse(Convert.ToString(row.Cells[col].Value), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    row.Cells[col].Style.BackColor = Tint(SlsPlanView.UcColor(v));
                }
                row.Cells["Status"].Style.ForeColor = statusText == "OK" ? Color.FromArgb(20, 120, 50) : Color.FromArgb(190, 30, 30);
            }
            Dictionary<SpanInfo, double> spanUc;
            List<TimberSlsBackend.Piece> pieces = Pieces(out spanUc);
            plan.SetData(run.Spans.Select(r => r.Span).ToList(), pieces, spanUc,
                colourBox.SelectedIndex == 2 ? "Vibration utilisation" : colourBox.SelectedIndex == 1 ? "Deflection utilisation along the members" : "Utilisation along the members");
            SelectSpan(keep >= 0 ? keep : (run.Spans.Count > 0 ? run.Spans.OrderByDescending(r => double.IsNaN(r.Uc) ? -1 : r.Uc).First().Span.Index : -1));
        }

        private int SelectedSpanIndex()
        {
            if (resultGrid.SelectedRows.Count == 0) return -1;
            SpanResult r = resultGrid.SelectedRows[0].Tag as SpanResult;
            return r == null ? -1 : r.Span.Index;
        }

        private void SelectSpan(int index)
        {
            foreach (DataGridViewRow row in resultGrid.Rows)
            {
                SpanResult r = row.Tag as SpanResult;
                if (r != null && r.Span.Index == index)
                {
                    row.Selected = true;
                    if (row.Index >= 0 && !row.Displayed) resultGrid.FirstDisplayedScrollingRowIndex = row.Index;
                    break;
                }
            }
            ShowSelected();
        }

        private void ShowSelected()
        {
            if (run == null) return;
            SpanResult r = resultGrid.SelectedRows.Count > 0 ? resultGrid.SelectedRows[0].Tag as SpanResult : null;
            plan.SelectedIndex = r == null ? -1 : r.Span.Index;
            plan.Invalidate();
            if (r == null) { diagram.SetData(null, null, null, null); details.Text = ""; return; }
            RowResult gov;
            List<RowResult> rows = RowsFor(r, out gov);
            SlsCombination c = gov != null ? ComboFor(r, gov.Combination)
                : comboBox.SelectedIndex > 0 ? ComboFor(r, (string)comboBox.SelectedItem)
                : run.Combinations.Count > 0 ? ComboFor(r, run.Combinations[0].Name) : null;
            diagram.SetData(r, c, gov, run.Cases);
            details.Text = Details(r, rows);
            details.Select(0, 0);
        }

        private string Details(SpanResult r, List<RowResult> rows)
        {
            SpanInfo s = r.Span;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(s.Group + "  " + s.Label + "   lines " + string.Join(", ", s.Lines));
            sb.AppendLine("Checked as: " + r.Reference + "  (type " + r.Type + "),  l = " + F(s.Length, 3) + " m,  l_rep = " + F(r.LRep, 3) + " m");
            if (r.Candidates.Count > 1)
            {
                sb.AppendLine("References tried (critical one used):");
                foreach (string cand in r.Candidates) sb.AppendLine("  " + cand);
            }
            sb.AppendLine("Material " + s.Material + ",  section " + s.Section + ",  E = " + F(s.E / 1e6, 0) + " N/mm²,  Iy = " + TimberSlsMath.Sig(s.Iy * 1e12) + " mm⁴" +
                (s.IsTimber ? "" : "   (not timber)"));
            sb.AppendLine("kdef = " + F(r.Kdef, 2) + " (" + r.KdefNote + "),  creep model " + r.Settings.CreepModel + ",  precamber w_c = " + F(r.Settings.PrecamberMm, 1) + " mm");
            if (r.Error != null) { sb.AppendLine(); sb.AppendLine("ERROR: " + r.Error); return sb.ToString(); }
            sb.AppendLine();
            sb.AppendLine("Deflection checks (" + (comboBox.SelectedIndex <= 0 ? "governing combination per row" : "combination " + comboBox.SelectedItem) + "):");
            foreach (RowResult rr in rows)
                sb.AppendLine("  " + rr.Row.Describe().PadRight(42) + "  w = " + F(rr.W, 2).PadLeft(7) + " mm at x = " + F(rr.X, 2) + " m   limit " +
                    F(rr.Limit, 2).PadLeft(7) + " mm   UC " + F(rr.Uc, 2) + "   [" + rr.Combination + "]");
            sb.AppendLine();
            sb.AppendLine("Instantaneous deflection per load case (relative, max |w|):");
            foreach (var kv in r.Defl.W)
            {
                string name = run.Cases.ContainsKey(kv.Key) ? run.Cases[kv.Key].Name : "#" + kv.Key;
                int k = TimberSlsMath.ArgMaxAbs(kv.Value);
                sb.AppendLine("  " + name.PadRight(30) + F(kv.Value[k], 3).PadLeft(9) + " mm at x = " + F(r.Defl.X[k], 2) + " m");
            }
            if (r.Vib04 != null || r.Vib25 != null)
            {
                sb.AppendLine();
                sb.AppendLine("Vibration:  floor field " + r.VibField);
                foreach (string l in (r.Vib04 != null ? r.Vib04.Lines : r.Vib25.Lines)) sb.AppendLine("  " + l);
                if (r.Vib25 != null)
                {
                    sb.Append("  Levels:");
                    for (int k = 0; k < 8; k++) sb.Append("  " + TimberSlsMath.Levels[k] + (r.Vib25.Pass[k] ? " ✔" : " ✘"));
                    sb.AppendLine();
                }
            }
            else if (!string.IsNullOrEmpty(r.VibNote)) { sb.AppendLine(); sb.AppendLine("Vibration: " + r.VibNote); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Output
        // ------------------------------------------------------------------

        private void ShowOverlay(bool report)
        {
            if (run == null || run.Spans.Count == 0) { SetStatus("Run the check first."); return; }
            Dictionary<SpanInfo, double> spanUc;
            List<TimberSlsBackend.Piece> pieces = Pieces(out spanUc);
            List<KeyValuePair<SpanInfo, string>> labels = spanUc.Select(kv => new KeyValuePair<SpanInfo, string>(kv.Key, double.IsNaN(kv.Value) ? "-" : F(kv.Value, 2))).ToList();
            string err = backend.ShowOverlay(pieces, labels, st.Display);
            overlayShown = err == null;
            SetStatus(err ?? "AxisVM model coloured on layer '" + TimberSlsFunction.LayerName + "' (removed on close).");
            if (err != null && report) MessageBox.Show(this, err, TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void SelectCurrent()
        {
            if (run == null) return;
            List<int> ids = resultGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as SpanResult).Where(r => r != null).SelectMany(r => r.Span.Lines).ToList();
            if (ids.Count == 0) ids = run.Spans.Where(r => r.Uc > 1).SelectMany(r => r.Span.Lines).ToList();
            backend.Select(ids);
        }

        /// <summary>Vibration key values of a span for the table: method, f1, stiffness, response, level.</summary>
        private static string[] VibCells(SpanResult r)
        {
            if (r.Vib04 != null)
                return new[] { "EC5:2004", F(r.Vib04.F1, 3), "a = " + F(r.Vib04.A, 3) + " mm/kN (lim " + F(r.Vib04.ALim, 3) + ")",
                    "v = " + TimberSlsMath.Sig(r.Vib04.V) + " (lim " + TimberSlsMath.Sig(r.Vib04.VLim) + ")", r.Vib04.F1Above8 ? "f1 > 8 Hz" : "f1 ≤ 8 Hz", F(r.VibUc, 3) };
            if (r.Vib25 != null)
                return new[] { "EN 1995-1-1:2025", F(r.Vib25.F1, 3), "w_1kN = " + F(r.Vib25.W1kN, 3) + " mm",
                    "v_rms = " + TimberSlsMath.Sig(r.Vib25.VRms) + " m/s, a_rms = " + TimberSlsMath.Sig(r.Vib25.ARms) + " m/s²",
                    "achieved " + (r.Vib25.AchievedLevel < 0 ? "none" : TimberSlsMath.Levels[r.Vib25.AchievedLevel]) + ", governing " + r.Vib25.Governing, F(r.VibUc, 3) };
            return new[] { "", "", "", "", r.VibNote ?? "", "" };
        }

        private string StatusOf(SpanResult r, RowResult gov)
        {
            if (r.Error != null) return "Error";
            double uc = Math.Max(gov == null ? 0 : gov.Uc, double.IsNaN(r.VibUc) ? 0 : r.VibUc);
            if (uc > 1) return "Fails";
            return r.Status == "f1 ≤ 8 Hz" ? "f1 ≤ 8 Hz" : "OK";
        }

        /// <summary>One row per span and limit row (CSV / clipboard), with the governing values and the vibration summary.</summary>
        private string Table(char sep)
        {
            StringBuilder sb = new StringBuilder();
            Func<string, string> q = v => { v = v ?? ""; return sep == ',' && (v.Contains(",") || v.Contains("\"") || v.Contains("\n")) ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; };
            sb.AppendLine(string.Join(sep.ToString(), new[] { "Group", "Span", "Lines", "l [m]", "l_rep [m]", "Checked as", "Type", "Material", "Section", "kdef", "kdef source",
                "Check", "Combination", "w [mm]", "x [m]", "Limit [mm]", "UC check", "Governing row", "UC deflection",
                "Vibration method", "f1 [Hz]", "Stiffness", "Response", "Vibration result", "UC vibration", "UC", "Status" }.Select(q)));
            foreach (SpanResult r in run.Spans)
            {
                RowResult gov;
                List<RowResult> rows = RowsFor(r, out gov);
                string[] vib = VibCells(r);
                double uc = r.Error != null ? double.NaN : Math.Max(gov == null ? 0 : gov.Uc, double.IsNaN(r.VibUc) ? 0 : r.VibUc);
                if (rows.Count == 0) rows = new List<RowResult> { null };
                foreach (RowResult rr in rows)
                {
                    SpanInfo s = r.Span;
                    List<string> cells = new List<string>
                    {
                        s.Group, s.Label, string.Join(" ", s.Lines), F(s.Length, 3), F(r.LRep, 3), r.Reference, r.Type.ToString(), s.Material, s.Section, F(r.Kdef, 2), r.KdefNote,
                        rr == null ? (r.Error ?? "") : rr.Row.Describe(), rr == null ? "" : rr.Combination, rr == null ? "" : F(rr.W, 3), rr == null ? "" : F(rr.X, 3),
                        rr == null ? "" : F(rr.Limit, 3), rr == null ? "" : F(rr.Uc, 3), rr != null && rr == gov ? "yes" : "", gov == null ? "" : F(gov.Uc, 3)
                    };
                    cells.AddRange(vib);
                    cells.Add(F(uc, 3));
                    cells.Add(StatusOf(r, gov));
                    sb.AppendLine(string.Join(sep.ToString(), cells.Select(q)));
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Report (self-contained HTML, printable to PDF from the browser)
        // ------------------------------------------------------------------

        private static string H(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }

        private static string Png(Control c, int w, int h)
        {
            c.Size = new Size(w, h);
            using (Bitmap bmp = new Bitmap(w, h))
            using (MemoryStream ms = new MemoryStream())
            {
                c.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        private static string UcCell(double uc)
        {
            if (double.IsNaN(uc)) return "<td class=\"n\">-</td>";
            Color col = SlsPlanView.UcColor(uc);
            return "<td class=\"n\" style=\"background:rgba(" + col.R + "," + col.G + "," + col.B + ",0.28)\">" + F(uc, 2) + "</td>";
        }

        private string GroupBasis(GroupSettings g)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<tr><th>Creep model</th><td>" + H(DescriptionEnumConverter.Text(g.CreepModel)) + (g.CreepModel == CreepModel.Custom ? " (G share " + F(g.CreepShareG, 2) + ", Q share " + (g.CreepShareQ < 0 ? "ψ2" : F(g.CreepShareQ, 2)) + ")" : "") + "</td></tr>");
            sb.Append("<tr><th>kdef</th><td>" + H(DescriptionEnumConverter.Text(g.KdefSource)) + (g.KdefSource == KdefSource.Custom ? ": " + F(g.KdefCustom, 2) : ", service class " + g.ServiceClass + " when the line has none") + "</td></tr>");
            sb.Append("<tr><th>Cantilever l_rep</th><td>" + F(g.CantileverFactor, 2) + " × length</td></tr>");
            sb.Append("<tr><th>Precamber w_c</th><td>" + F(g.PrecamberMm, 1) + " mm</td></tr>");
            sb.Append("<tr><th>Limits</th><td>" + string.Join("<br>", g.Rows.Where(r => r.Enabled).Select(r => H(r.Describe()))) + "</td></tr>");
            return sb.ToString();
        }

        private string ReportHtml()
        {
            StringBuilder sb = new StringBuilder();
            string model = backend.ModelPath;
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Timber SLS check - " + H(Path.GetFileName(model)) + "</title><style>");
            sb.Append("body{font:13px/1.45 'Segoe UI',Arial,sans-serif;color:#18221f;margin:28px auto;max-width:1060px;padding:0 16px}");
            sb.Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:26px 0 8px;border-bottom:2px solid #0066b4;padding-bottom:3px}h3{font-size:14px;margin:18px 0 6px}");
            sb.Append("table{border-collapse:collapse;width:100%;margin:4px 0 10px}th,td{border:1px solid #cfd8dc;padding:4px 6px;text-align:left;vertical-align:top}");
            sb.Append("thead th{background:#0066b4;color:#fff}th{background:#eef3f7;font-weight:600}.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}");
            sb.Append(".ok{color:#1d7a45;font-weight:600}.bad{color:#b3261e;font-weight:600}.meta{color:#5a6964}pre{font:12px Consolas,monospace;background:#f6f8fa;border:1px solid #e1e6ea;padding:8px;white-space:pre-wrap}");
            sb.Append("img{max-width:100%;border:1px solid #cfd8dc}.span{page-break-inside:avoid}.basis th{width:190px}@media print{body{margin:0}h2{page-break-after:avoid}}</style></head><body>");

            sb.Append("<h1>Timber SLS check (EN 1995-1-1)</h1>");
            sb.Append("<div class=\"meta\">Model: " + H(string.IsNullOrEmpty(model) ? "(unsaved)" : model) + "<br>Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                      "<br>Combination shown: " + H(comboBox.SelectedIndex <= 0 ? "governing of all " + run.Combinations.Count + " combinations" : (string)comboBox.SelectedItem) + "</div>");

            // Summary
            int fails = 0;
            sb.Append("<h2>1 Summary</h2><table><thead><tr><th>Group</th><th>Span</th><th>l [m]</th><th>Checked as</th><th>Section</th><th>kdef</th><th>Governing check</th><th>w / limit [mm]</th><th>UC defl.</th><th>f1 [Hz]</th><th>UC vib.</th><th>UC</th><th>Status</th></tr></thead><tbody>");
            foreach (SpanResult r in run.Spans)
            {
                RowResult gov;
                RowsFor(r, out gov);
                double f1 = r.Vib04 != null ? r.Vib04.F1 : r.Vib25 != null ? r.Vib25.F1 : double.NaN;
                double uc = r.Error != null ? double.NaN : Math.Max(gov == null ? 0 : gov.Uc, double.IsNaN(r.VibUc) ? 0 : r.VibUc);
                string st2 = StatusOf(r, gov);
                if (st2 != "OK") fails++;
                sb.Append("<tr><td>" + H(r.Span.Group) + "</td><td>" + H(r.Span.Label) + "</td><td class=\"n\">" + F(r.Span.Length, 3) + "</td><td>" + H(r.Reference) + "</td><td>" +
                    H(r.Span.Material + " " + r.Span.Section) + "</td><td class=\"n\">" + F(r.Kdef, 2) + "</td><td>" + H(gov == null ? (r.Error ?? "-") : gov.Row.Describe()) + "</td><td class=\"n\">" +
                    (gov == null ? "" : F(gov.W, 2) + " / " + F(gov.Limit, 2)) + "</td>" + UcCell(gov == null ? double.NaN : gov.Uc) + "<td class=\"n\">" + (double.IsNaN(f1) ? "" : F(f1, 2)) + "</td>" +
                    UcCell(r.VibUc) + UcCell(uc) + "<td class=\"" + (st2 == "OK" ? "ok" : "bad") + "\">" + H(st2) + "</td></tr>");
            }
            sb.Append("</tbody></table><p>" + run.Spans.Count + " span(s) checked, " + fails + " not OK.</p>");

            Dictionary<SpanInfo, double> spanUc;
            List<TimberSlsBackend.Piece> pieces = Pieces(out spanUc);
            using (SlsPlanView pv = new SlsPlanView { View = plan.View, ShowHint = false, Display = st.Display })
            {
                pv.Size = new Size(1000, 460);
                pv.SetData(run.Spans.Select(r => r.Span).ToList(), pieces, spanUc, "Utilisation along the members");
                sb.Append("<img alt=\"Members coloured by utilisation\" src=\"" + Png(pv, 1000, 460) + "\">");
            }

            // Basis
            GeneralSettings gs = st.General;
            VibrationSettings v = st.Vibration;
            sb.Append("<h2>2 Basis of the check</h2><table class=\"basis\">");
            sb.Append("<tr><th>Standards</th><td>NEN-EN 1995-1-1+C1+A1:2011 (2.2.3, 2.3.2.2, Table 3.2, 7.2, 7.3), NEN-EN 1990/NB A1.4.3; Harff et al. (2018) for w_bij" +
                      (v.Method == VibrationMethod.En1995_2025 ? "; EN 1995-1-1:2025 §9.3 (vibration)" : "") + "</td></tr>");
            sb.Append("<tr><th>Instantaneous deflection</th><td>AxisVM linear static results per load case at E_mean" +
                      (gs.AxisStiffness == AxisStiffness.Auto ? " (AxisVM timber results divided by 1 + kdef of the line)" : "") + "</td></tr>");
            sb.Append("<tr><th>Direction</th><td>" + H(DescriptionEnumConverter.Text(gs.Direction)) + "</td></tr>");
            sb.Append("<tr><th>Spans</th><td>" + H(DescriptionEnumConverter.Text(gs.SpanMode)) + "; split at columns " + (gs.SplitAtColumns ? "yes" : "no") +
                      ", at non-parallel members " + (gs.SplitAtBeams ? "yes" : "no") + "; default type " + H(DescriptionEnumConverter.Text(gs.DefaultType)) +
                      "; cantilever reference " + H(DescriptionEnumConverter.Text(gs.CantileverRef)) + "</td></tr>");
            sb.Append("<tr><th>Combinations</th><td>" + H(DescriptionEnumConverter.Text(st.LoadSource)) + ", " + run.Combinations.Count + " combination(s)</td></tr>");
            sb.Append("</table><h3>Floor</h3><table class=\"basis\">" + GroupBasis(st.Floor) + "</table><h3>Roof</h3><table class=\"basis\">" + GroupBasis(st.Roof) + "</table>");
            if (v.Method != VibrationMethod.Off)
            {
                sb.Append("<h3>Floor vibration</h3><table class=\"basis\">");
                sb.Append("<tr><th>Method</th><td>" + H(DescriptionEnumConverter.Text(v.Method)) + "</td></tr>");
                sb.Append("<tr><th>Floor field</th><td>b = " + F(v.Width, 2) + " m, s = " + F(v.Spacing, 2) + " m, (EI)_T = " + TimberSlsMath.Sig(v.EiTransverse) + " Nm²/m, extra (EI)_L = " +
                          TimberSlsMath.Sig(v.EiDeckLongitudinal) + " Nm²/m, span: " + H(DescriptionEnumConverter.Text(v.SpanChoice)) + "</td></tr>");
                sb.Append("<tr><th>Mass</th><td>" + (v.MassOverride > 0 ? F(v.MassOverride, 1) + " kg/m² (given)" : "permanent " + F(v.PermanentLoad, 2) + " kN/m²" +
                          (v.Method == VibrationMethod.En1995_2025 ? " + 10 % of imposed " + F(v.ImposedLoad, 2) + " kN/m²" : "")) + "</td></tr>");
                sb.Append("<tr><th>Damping, f1</th><td>ζ = " + F(v.Damping, 3) + "; f1: " + H(DescriptionEnumConverter.Text(v.F1Source)) + "</td></tr>");
                if (v.Method == VibrationMethod.En1995_2025)
                    sb.Append("<tr><th>Performance level</th><td>target " + v.TargetLevel + ", f_w = " + F(v.WalkingFrequency, 2) + " Hz, multi-family " + (v.MultiFamily ? "yes" : "no") +
                              ", floor type " + H(DescriptionEnumConverter.Text(v.FloorType)) + "</td></tr>");
                else
                    sb.Append("<tr><th>Limits</th><td>a_lim " + H(DescriptionEnumConverter.Text(v.ALimMode)) + ", b " + H(DescriptionEnumConverter.Text(v.BMode)) + ", k_amp " + F(v.KAmp, 2) + "</td></tr>");
                sb.Append("</table>");
            }

            // Load cases
            sb.Append("<h2>3 Load cases</h2><table><thead><tr><th>Load case</th><th>Role</th><th>Group</th><th>Exclusive</th><th>ψ0</th><th>ψ1</th><th>ψ2</th></tr></thead><tbody>");
            foreach (SlsLoadCase lc in run.Cases.Values.OrderBy(x => x.Id))
                sb.Append("<tr><td>" + H(lc.Name) + "</td><td>" + lc.Role + "</td><td>" + H(lc.Group) + "</td><td>" + (lc.Exclusive ? "yes" : "") + "</td><td class=\"n\">" +
                          F(lc.Psi0, 2) + "</td><td class=\"n\">" + F(lc.Psi1, 2) + "</td><td class=\"n\">" + F(lc.Psi2, 2) + "</td></tr>");
            sb.Append("</tbody></table>");

            // Per span
            sb.Append("<h2>4 Spans</h2>");
            using (SlsDiagramView dv = new SlsDiagramView())
            {
                foreach (SpanResult r in run.Spans)
                {
                    RowResult gov;
                    List<RowResult> rows = RowsFor(r, out gov);
                    SlsCombination cmb = gov != null ? ComboFor(r, gov.Combination) : run.Combinations.Count > 0 ? ComboFor(r, run.Combinations[0].Name) : null;
                    sb.Append("<div class=\"span\"><h3>" + H(r.Span.Group + "  " + r.Span.Label) + "</h3>");
                    if (r.Error == null && cmb != null)
                    {
                        dv.SetData(r, cmb, gov, run.Cases);
                        sb.Append("<img alt=\"Deflection build-up\" src=\"" + Png(dv, 1000, 340) + "\">");
                    }
                    sb.Append("<pre>" + H(Details(r, rows)) + "</pre></div>");
                }
            }

            if (run.Notes.Count > 0) sb.Append("<h2>5 Notes</h2><ul>" + string.Join("", run.Notes.Select(n => "<li>" + H(n) + "</li>")) + "</ul>");
            sb.Append("<h2>Appendix: combinations</h2><ol>" + string.Join("", run.Combinations.Take(500).Select(cc => "<li>" + H(cc.Name) + "</li>")) + "</ol>");
            if (run.Combinations.Count > 500) sb.Append("<p>... " + (run.Combinations.Count - 500) + " more</p>");
            sb.Append("<p class=\"meta\">Generated by the Nextra Timber SLS Check add-on. Formulas: docs/reference/ec5-sls-timber.md. Limits apply by agreement with the client.</p></body></html>");
            return sb.ToString();
        }

        private void ExportReport()
        {
            if (run == null) { SetStatus("Run the check first."); return; }
            string name = string.IsNullOrEmpty(backend.ModelPath) ? "timber-sls-report" : Path.GetFileNameWithoutExtension(backend.ModelPath) + "-timber-sls";
            using (SaveFileDialog d = new SaveFileDialog { Filter = "HTML report (*.html)|*.html", FileName = name + ".html" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    File.WriteAllText(d.FileName, ReportHtml(), new UTF8Encoding(false));
                    SetStatus("Report saved: " + d.FileName + " (open it in a browser; print to PDF from there).");
                    try { System.Diagnostics.Process.Start(d.FileName); } catch { }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        private void ExportCsv()
        {
            if (run == null) return;
            using (SaveFileDialog d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = (string.IsNullOrEmpty(backend.ModelPath) ? "timber-sls" : Path.GetFileNameWithoutExtension(backend.ModelPath) + "-timber-sls") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, Table(','), new UTF8Encoding(true));
                    SetStatus("Exported " + d.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void CopyTable()
        {
            if (run == null) return;
            try { Clipboard.SetText(Table('\t')); SetStatus("Table copied (paste into Excel)."); }
            catch (Exception ex) { SetStatus("Copy failed: " + ex.Message); }
        }
    }
}
