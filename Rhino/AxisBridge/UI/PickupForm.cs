using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Attributes;

namespace AxisBridge.UI
{
    /// <summary>
    /// AxPickup: which properties of the picked object to copy. One check box per group with the source's value next to it;
    /// groups the source has are ticked by default, empty ones are offered unticked (ticking one clears it on the targets).
    /// </summary>
    public class PickupForm : Form
    {
        private readonly List<KeyValuePair<CheckBox, AxPickup.Group>> boxes = new List<KeyValuePair<CheckBox, AxPickup.Group>>();

        /// <summary>The groups the user ticked (after OK).</summary>
        public List<AxPickup.Group> Chosen
        {
            get
            {
                var l = new List<AxPickup.Group>();
                foreach (var b in boxes) if (b.Key.Checked) l.Add(b.Value);
                return l;
            }
        }

        public PickupForm(string sourceLabel, AxGeomKind kind, Dictionary<string, string> attrs)
        {
            Theme.Dialog(this, "AxPickup", new Size(600, 560));
            MinimumSize = new Size(520, 420);

            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoScroll = true,
                Padding = new Padding(14, 12, 14, 8),
                BackColor = Theme.Background
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var head = new Label { Text = "Copy from " + sourceLabel + " - tick what to copy:", Font = Theme.Bold, AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
            page.Controls.Add(head, 0, 0);
            page.SetColumnSpan(head, 2);

            int row = 1;
            foreach (AxPickup.Group g in AxPickup.GroupsFor(kind))
            {
                Dictionary<string, string> values = AxPickup.ValuesOf(attrs, g);
                var box = new CheckBox { Text = g.Name, AutoSize = true, Checked = values.Count > 0, Margin = new Padding(3, 4, 12, 4) };
                var value = new Label { Text = AxPickup.Describe(values, g), AutoSize = true, ForeColor = values.Count > 0 ? Color.FromArgb(36, 53, 72) : Theme.HintColor, Margin = new Padding(3, 6, 3, 4) };
                page.Controls.Add(box, 0, row);
                page.Controls.Add(value, 1, row);
                boxes.Add(new KeyValuePair<CheckBox, AxPickup.Group>(box, g));
                row++;
            }

            var all = new LinkLabel { Text = "All", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            all.LinkClicked += (s, e) => { foreach (var b in boxes) b.Key.Checked = true; };
            var none = new LinkLabel { Text = "None", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            none.LinkClicked += (s, e) => { foreach (var b in boxes) b.Key.Checked = false; };
            var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            links.Controls.Add(all);
            links.Controls.Add(none);
            page.Controls.Add(links, 0, row);
            page.SetColumnSpan(links, 2);

            Button ok, cancel;
            Panel bar = Theme.Buttons(this, "Pick targets", out ok, out cancel, "Then select the objects to copy onto.");
            Controls.Add(page);
            Controls.Add(Theme.Separator());
            Controls.Add(bar);
            Controls.Add(Theme.Header("AxPickup", "Copy structural properties from one object to others of the same kind"));
        }
    }
}
