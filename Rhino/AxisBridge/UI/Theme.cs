using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AxisBridge.UI
{
    /// <summary>Shared look of the AxisBridge dialogs: fonts, colours and small layout helpers.</summary>
    public static class Theme
    {
        public const string VariesItem = "‹varies - keep each object's value›";
        public const string VariesCue = "‹varies› blank = keep each object's value,  - = clear on all";

        public static readonly Font Body = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Bold = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Title = new Font("Segoe UI Semibold", 11F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Small = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);

        public static readonly Color HintColor = Color.FromArgb(105, 105, 105);
        public static readonly Color HeaderBack = Color.FromArgb(241, 244, 248);
        public static readonly Color Accent = Color.FromArgb(0, 102, 180);
        public static readonly Color Orange = Color.FromArgb(230, 126, 34);
        public static readonly Color Background = Color.FromArgb(246, 248, 251);
        public static readonly Color WarnBack = Color.FromArgb(255, 246, 214);

        /// <summary>Applies the standard dialog settings.</summary>
        public static void Dialog(Form f, string title, Size size)
        {
            f.Text = title;
            f.Font = Body;
            f.BackColor = Background;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.Sizable;
            f.MinimizeBox = false;
            f.MaximizeBox = true;
            f.ShowInTaskbar = false;
            f.ClientSize = size;
            f.Shown += (s, e) => StyleAll(f);
            f.MinimumSize = new Size(Math.Min(size.Width, 640), Math.Min(size.Height, 420));
        }

        /// <summary>Grey, wrapping help text.</summary>
        public static Label Hint(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = HintColor,
                Font = Small,
                Margin = new Padding(3, 0, 3, 6)
            };
        }

        /// <summary>The blue gradient header strip at the top of a dialog (white text, thin orange accent line).</summary>
        public static Panel Header(string title, string subtitle)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Accent, Padding = new Padding(14, 8, 14, 6) };
            var t = new Label { Text = title, Font = new Font("Segoe UI Semibold", 12F, FontStyle.Regular, GraphicsUnit.Point), AutoSize = true, Location = new Point(14, 7), ForeColor = Color.White, BackColor = Color.Transparent };
            var s = new Label { Text = subtitle, Font = Small, AutoSize = true, Location = new Point(15, 34), ForeColor = Color.FromArgb(220, 235, 248), BackColor = Color.Transparent };
            p.Paint += (o, e) =>
            {
                using (var b = new LinearGradientBrush(p.ClientRectangle, Color.FromArgb(13, 121, 199), Color.FromArgb(0, 86, 156), LinearGradientMode.Vertical))
                    e.Graphics.FillRectangle(b, p.ClientRectangle);
                using (var line = new SolidBrush(Orange))
                    e.Graphics.FillRectangle(line, 0, p.Height - 3, p.Width, 3);
            };
            p.Controls.Add(t);
            p.Controls.Add(s);
            return p;
        }

        /// <summary>Flat button: filled blue when primary, white with a thin border otherwise.</summary>
        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.BackColor = primary ? Accent : Color.White;
            b.ForeColor = primary ? Color.White : Color.FromArgb(36, 53, 72);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(0, 86, 156) : Color.FromArgb(190, 200, 212);
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(18, 126, 204) : Color.FromArgb(237, 243, 249);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0, 70, 130) : Color.FromArgb(220, 232, 244);
        }

        /// <summary>Applies the flat button and grid look to every control that has not been styled yet.</summary>
        public static void StyleAll(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var b = c as Button;
                if (b != null && b.FlatStyle != FlatStyle.Flat) StyleButton(b, false);
                var g = c as DataGridView;
                if (g != null) StyleGrid(g);
                StyleAll(c);
            }
        }

        /// <summary>Blue column headers, light selection, white rows.</summary>
        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Color.White;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Accent;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Accent;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 234, 250);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 42, 65);
            g.GridColor = Color.FromArgb(222, 228, 236);
        }

        /// <summary>Bottom bar with Apply / Cancel on the right and a hint on the left.</summary>
        public static Panel Buttons(Form f, string okText, out Button ok, out Button cancel, string leftHint = null)
        {
            var p = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 10, 12, 10), BackColor = Color.White };
            var okB = new Button { Text = okText, Width = 110, Height = 30, Font = Bold, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var cancelB = new Button { Text = "Cancel", Width = 100, Height = 30, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            StyleButton(okB, true);
            StyleButton(cancelB, false);
            ok = okB;
            cancel = cancelB;
            p.Controls.Add(okB);
            p.Controls.Add(cancelB);
            p.Layout += (s, e) =>
            {
                cancelB.Location = new Point(p.ClientSize.Width - p.Padding.Right - cancelB.Width, p.Padding.Top);
                okB.Location = new Point(cancelB.Left - 8 - okB.Width, p.Padding.Top);
            };
            if (!string.IsNullOrEmpty(leftHint))
            {
                var h = Hint(leftHint);
                h.Location = new Point(12, 16);
                h.MaximumSize = new Size(420, 0);
                p.Controls.Add(h);
            }
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return p;
        }

        /// <summary>Thin separator strip above the button bar.</summary>
        public static Control Separator()
        {
            return new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(218, 222, 228) };
        }
    }

    /// <summary>
    /// A scrollable two-column "label | editor" page. Rows remember their controls so a whole row
    /// can be shown or hidden when the element type changes.
    /// </summary>
    public class FieldPage : Panel
    {
        private readonly TableLayoutPanel table;
        private int row;

        public FieldPage()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(10, 10, 16, 10);

            table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(table);
        }

        /// <summary>A bold section title spanning the page.</summary>
        public Row Section(string title)
        {
            var l = new Label { Text = title, Font = Theme.Bold, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, row == 0 ? 0 : 12, 0, 4) };
            table.Controls.Add(l, 0, row);
            table.SetColumnSpan(l, 3);
            row++;
            return new Row(l);
        }

        /// <summary>Label + editor (+ optional button) + optional hint underneath.</summary>
        public Row Add(string label, Control editor, string hint = null, Control button = null)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 3, 3) };
            editor.Margin = new Padding(3, 3, 3, hint == null ? 6 : 1);
            if (editor.Dock == DockStyle.None) editor.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            table.Controls.Add(lbl, 0, row);
            table.Controls.Add(editor, 1, row);
            if (button != null)
            {
                button.Margin = new Padding(3, 2, 0, 2);
                table.Controls.Add(button, 2, row);
            }
            row++;

            var r = new Row(lbl, editor, button);
            if (hint != null)
            {
                Label h = Theme.Hint(hint);
                table.Controls.Add(h, 1, row);
                table.SetColumnSpan(h, 2);
                r.Add(h);
                row++;
            }
            return r;
        }

        /// <summary>A full-width control (e.g. a note or a grid).</summary>
        public Row AddWide(Control c)
        {
            table.Controls.Add(c, 0, row);
            table.SetColumnSpan(c, 3);
            row++;
            return new Row(c);
        }

        /// <summary>The controls of one visual row, shown and hidden together.</summary>
        public class Row
        {
            private readonly List<Control> controls = new List<Control>();
            public Row(params Control[] cs) { foreach (Control c in cs) if (c != null) controls.Add(c); }
            public void Add(Control c) { controls.Add(c); }
            public bool Visible
            {
                set { foreach (Control c in controls) c.Visible = value; }
            }
        }
    }
}
