using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using AxisBridge.Library;

namespace AxisBridge.UI
{
    /// <summary>
    /// Picks materials or cross-sections from the AxisVM catalog that the AxisVM Rhino Import add-on exported. The tree on the
    /// left follows the AxisVM library (materials: code / type / kind, sections: shape / table); the list on
    /// the right shows the entries of the selected group, or everything matching the search box. Ticked
    /// entries are returned in catalog order; entries already in the document library are shown grey.
    /// </summary>
    public class CatalogPickerForm : Form
    {
        /// <summary>One catalog row: where it sits in the tree, what the list shows, and the catalog object.</summary>
        private class Entry
        {
            public string[] Path;
            public string[] Cells;
            public string Name;
            public string Search;
            public object Item;
            public bool InLibrary;
            public int Order;
        }

        private const int MaxListed = 5000;

        private readonly List<Entry> entries = new List<Entry>();
        private readonly HashSet<Entry> ticked = new HashSet<Entry>();
        private readonly TreeView tree;
        private readonly ListView list;
        private readonly TextBox search;
        private readonly Label status;
        private readonly Label count;
        private readonly Button ok;
        private bool filling;

        /// <summary>The ticked catalog objects (CatalogMaterial or CatalogSection), in catalog order.</summary>
        public List<object> Picked { get; private set; } = new List<object>();

        public static CatalogPickerForm ForMaterials(AxCatalog catalog, IEnumerable<string> libraryNames)
        {
            var f = new CatalogPickerForm("AxisVM material catalog", Subtitle(catalog, catalog.Materials.Count, "materials"),
                new[] { "Name", "Type", "Standard" }, new[] { 150, 130, 160 });
            var have = new HashSet<string>(libraryNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (CatalogMaterial m in catalog.Materials)
            {
                string[] path = m.Kind.Length > 0 ? new[] { m.Code, m.Type, m.Kind } : new[] { m.Code, m.Type };
                f.AddEntry(path, new[] { m.Name, m.Kind.Length > 0 ? m.Type + " / " + m.Kind : m.Type, m.Standard }, m.Name, m, have.Contains(m.Name));
            }
            f.Finish();
            return f;
        }

        public static CatalogPickerForm ForSections(AxCatalog catalog, IEnumerable<string> libraryNames)
        {
            var f = new CatalogPickerForm("AxisVM cross-section catalog", Subtitle(catalog, catalog.Sections.Count, "cross-sections"),
                new[] { "Name", "Table", "Region" }, new[] { 170, 300, 60 });
            var have = new HashSet<string>(libraryNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (CatalogSection s in catalog.Sections)
                f.AddEntry(new[] { s.Shape, s.Table }, new[] { s.Name.Trim(), s.Table, s.Region }, s.Name.Trim(), s, have.Contains(s.Name.Trim()));
            f.Finish();
            return f;
        }

        private static string Subtitle(AxCatalog c, int n, string what)
        {
            return string.Format("{0} {1} read from AxisVM on {2:yyyy-MM-dd HH:mm}   ·   {3}", n, what, c.Written, c.Folder);
        }

        private CatalogPickerForm(string title, string subtitle, string[] columns, int[] widths)
        {
            Theme.Dialog(this, title, new Size(900, 600));

            // ---- search bar ------------------------------------------------------
            var bar = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 8, 12, 4) };
            var lbl = new Label { Text = "Search", AutoSize = true, Location = new Point(12, 12) };
            search = new TextBox { Location = new Point(70, 8), Width = 300, Anchor = AnchorStyles.Left | AnchorStyles.Top };
            var tickAll = new Button { Text = "Tick all shown", AutoSize = true, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var clear = new Button { Text = "Clear ticks", AutoSize = true, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            status = new Label { AutoSize = true, Location = new Point(380, 12), ForeColor = Theme.HintColor, Font = Theme.Small };
            bar.Controls.AddRange(new Control[] { lbl, search, status, tickAll, clear });
            bar.Layout += (s, e) =>
            {
                clear.Location = new Point(bar.ClientSize.Width - bar.Padding.Right - clear.Width, 6);
                tickAll.Location = new Point(clear.Left - 6 - tickAll.Width, 6);
            };

            // ---- tree | list -----------------------------------------------------
            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, FullRowSelect = true, ShowLines = true };
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            for (int i = 0; i < columns.Length; i++) list.Columns.Add(columns[i], widths[i]);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 160 };
            // The distance only sticks once the container has its real width.
            Load += (s, e) => split.SplitterDistance = 300;
            split.Panel1.Padding = new Padding(12, 0, 0, 8);
            split.Panel2.Padding = new Padding(0, 0, 12, 8);
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(list);

            // ---- buttons ---------------------------------------------------------
            Button cancel;
            Panel buttons = Theme.Buttons(this, "Add to library", out ok, out cancel);
            ok.Width = 130;
            count = new Label { AutoSize = true, Location = new Point(14, 18), ForeColor = Theme.HintColor };
            buttons.Controls.Add(count);
            ok.Click += (s, e) => Picked = Collect();

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(Theme.Header(title, subtitle));
            Controls.Add(Theme.Separator());
            Controls.Add(buttons);

            tree.AfterSelect += (s, e) => { if (!filling) { search.Text = ""; ShowNode(e.Node); } };
            search.TextChanged += (s, e) => ShowSearch();
            list.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var en = (Entry)e.Item.Tag;
                if (en.InLibrary && e.Item.Checked) { e.Item.Checked = false; return; }
                if (e.Item.Checked) ticked.Add(en); else ticked.Remove(en);
                UpdateCount();
            };
            tickAll.Click += (s, e) =>
            {
                filling = true;
                foreach (ListViewItem it in list.Items)
                {
                    var en = (Entry)it.Tag;
                    if (en.InLibrary) continue;
                    it.Checked = true;
                    ticked.Add(en);
                }
                filling = false;
                UpdateCount();
            };
            clear.Click += (s, e) =>
            {
                ticked.Clear();
                filling = true;
                foreach (ListViewItem it in list.Items) it.Checked = false;
                filling = false;
                UpdateCount();
            };
            Shown += (s, e) => search.Focus();
        }

        private void AddEntry(string[] path, string[] cells, string name, object item, bool inLibrary)
        {
            entries.Add(new Entry
            {
                Path = path,
                Cells = cells,
                Name = name,
                Search = Compact(string.Join(" ", cells)),
                Item = item,
                InLibrary = inLibrary,
                Order = entries.Count
            });
        }

        /// <summary>Builds the group tree (each node remembers the entries below it) and selects the first group.</summary>
        private void Finish()
        {
            tree.BeginUpdate();
            var nodes = new Dictionary<string, TreeNode>();
            foreach (Entry en in entries)
            {
                TreeNodeCollection level = tree.Nodes;
                string key = "";
                foreach (string part in en.Path)
                {
                    key += "\u0001" + part;
                    TreeNode node;
                    if (!nodes.TryGetValue(key, out node))
                    {
                        node = new TreeNode(part.Length > 0 ? part : "(unnamed)") { Tag = new List<Entry>() };
                        level.Add(node);
                        nodes[key] = node;
                    }
                    ((List<Entry>)node.Tag).Add(en);
                    level = node.Nodes;
                }
            }
            foreach (TreeNode n in nodes.Values) n.Text += "  (" + ((List<Entry>)n.Tag).Count + ")";
            if (tree.Nodes.Count == 1) tree.Nodes[0].Expand();
            tree.EndUpdate();

            if (tree.Nodes.Count > 0)
            {
                TreeNode first = tree.Nodes[0];
                while (first.Nodes.Count > 0 && first.IsExpanded) first = first.Nodes[0];
                tree.SelectedNode = first;
                ShowNode(first);
            }
            UpdateCount();
        }

        private void ShowNode(TreeNode node)
        {
            if (node == null) return;
            var items = (List<Entry>)node.Tag;
            Fill(items, items.Count + " in " + node.Text.Split(new[] { "  (" }, StringSplitOptions.None)[0]);
        }

        private void ShowSearch()
        {
            string q = Compact(search.Text);
            if (q.Length == 0) { ShowNode(tree.SelectedNode); return; }

            var hits = new List<Entry>();
            foreach (Entry en in entries)
                if (en.Search.Contains(q)) hits.Add(en);
            Fill(hits, hits.Count + " match" + (hits.Count == 1 ? "" : "es"));
        }

        private void Fill(List<Entry> items, string caption)
        {
            filling = true;
            list.BeginUpdate();
            list.Items.Clear();
            int shown = Math.Min(items.Count, MaxListed);
            var rows = new ListViewItem[shown];
            for (int i = 0; i < shown; i++)
            {
                Entry en = items[i];
                var it = new ListViewItem(en.Cells) { Tag = en, Checked = ticked.Contains(en) };
                if (en.InLibrary)
                {
                    it.ForeColor = Theme.HintColor;
                    it.Text = en.Name + "   (in library)";
                }
                rows[i] = it;
            }
            list.Items.AddRange(rows);
            list.EndUpdate();
            filling = false;
            status.Text = caption + (items.Count > shown ? string.Format(" - first {0} shown, refine the search", shown) : "");
        }

        private void UpdateCount()
        {
            count.Text = ticked.Count == 0 ? "Tick the entries to add to the document library." : ticked.Count + " ticked";
            ok.Enabled = ticked.Count > 0;
        }

        private List<object> Collect()
        {
            var picked = new List<Entry>(ticked);
            picked.Sort((a, b) => a.Order.CompareTo(b.Order));
            var result = new List<object>();
            foreach (Entry en in picked) result.Add(en.Item);
            return result;
        }

        /// <summary>Search key: upper case without spaces, so "hea300" finds "HEA 300".</summary>
        private static string Compact(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        // ---- test hooks (offline preview / checks) ------------------------------

        public void TickForTest(params string[] names)
        {
            var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (Entry en in entries) if (!en.InLibrary && want.Contains(en.Name)) ticked.Add(en);
            if (tree.SelectedNode != null) ShowNode(tree.SelectedNode);
            UpdateCount();
        }

        public void SearchForTest(string text) { search.Text = text; }

        public List<object> CollectForTest() { return Collect(); }

        public int ListedForTest { get { return list.Items.Count; } }

        public string StatusForTest { get { return status.Text; } }

        public List<string> TreeForTest()
        {
            var lines = new List<string>();
            Action<TreeNodeCollection, string> walk = null;
            walk = (nodes, indent) => { foreach (TreeNode n in nodes) { lines.Add(indent + n.Text); walk(n.Nodes, indent + "  "); } };
            walk(tree.Nodes, "");
            return lines;
        }
    }
}
