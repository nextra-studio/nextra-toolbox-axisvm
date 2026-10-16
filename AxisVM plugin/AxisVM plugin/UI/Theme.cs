using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    /// <summary>Presentation only: original controls, values, events and dialog results are preserved.</summary>
    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(0, 102, 180);
        public static readonly Color Orange = Color.FromArgb(230, 126, 34);
        private static readonly Color Background = Color.FromArgb(246, 248, 251);
        private static readonly HashSet<Button> PaintedButtons = new HashSet<Button>();

        /// <summary>Set before creating controls, so AutoSize measures the final font.</summary>
        public static void Initialize(Form form)
        {
            form.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.BackColor = Background;
        }

        /// <summary>
        /// Rehouses a coordinate-based dialog without changing its input/output contract. Buttons stay
        /// outside the scrolling area. The optional old header belongs to the reference one-way dialog.
        /// </summary>
        public static void Apply(Form form, string title, string subtitle, bool replaceHeader = false)
        {
            int width = form.ClientSize.Width;
            // Removing controls can clear Form.AcceptButton/CancelButton; preserve these contracts.
            IButtonControl accept = form.AcceptButton;
            IButtonControl cancel = form.CancelButton;
            Control[] original = new Control[form.Controls.Count];
            form.Controls.CopyTo(original, 0);
            form.Controls.Clear();
            Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background };
            Panel canvas = new Panel { Location = Point.Empty, Width = width, BackColor = Background };
            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
                Padding = new Padding(12, 10, 12, 10), BackColor = Color.White
            };
            int bottom = 0;
            foreach (Control control in original)
            {
                if (replaceHeader && control is Panel && control.Top == 0)
                {
                    control.Dispose();
                    continue;
                }
                Button button = control as Button;
                if (button != null && (button == accept || button == cancel))
                {
                    button.Anchor = AnchorStyles.None;
                    button.AutoSize = true;
                    button.MinimumSize = new Size(92, 32);
                    button.Margin = new Padding(0, 0, 8, 0);
                    actions.Controls.Add(button);
                    StyleButton(button, button == accept);
                }
                else
                {
                    if (replaceHeader) control.Top -= 64;
                    canvas.Controls.Add(control);
                    bottom = Math.Max(bottom, control.Bottom);
                }
            }
            form.AcceptButton = accept;
            form.CancelButton = cancel;
            canvas.Height = bottom + 16;
            body.Controls.Add(canvas);
            StyleControls(canvas);
            Panel header = CreateHeader(title, subtitle);
            form.Controls.Add(body);
            form.Controls.Add(actions);
            form.Controls.Add(header);
            // Fit the monitor; the content scrolls when Windows scaling leaves less vertical space.
            int desiredHeight = canvas.Height + header.Height + actions.GetPreferredSize(new Size(width, 0)).Height;
            Rectangle work = Screen.FromControl(form).WorkingArea;
            form.ClientSize = new Size(width, Math.Min(desiredHeight, work.Height - 90));
            body.Resize += delegate { canvas.Width = Math.Max(width, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth); };
            // Windows can apply the host's DPI scale after construction, so fit once more on display.
            form.Shown += delegate
            {
                Rectangle available = Screen.FromControl(form).WorkingArea;
                form.ClientSize = new Size(Math.Min(form.ClientSize.Width, available.Width - 40),
                    Math.Min(form.ClientSize.Height, available.Height - 90));
            };
        }

        /// <summary>Results retain their data and handlers; a wrapping footer also fits narrow windows.</summary>
        public static void ApplyResults(Form form, string title, string subtitle)
        {
            Size originalSize = form.ClientSize;
            IButtonControl accept = form.AcceptButton;
            IButtonControl cancel = form.CancelButton;
            Control[] original = new Control[form.Controls.Count];
            form.Controls.CopyTo(original, 0);
            form.Controls.Clear();
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
                Padding = new Padding(12, 10, 12, 10), BackColor = Color.White
            };
            Label headline = null;
            foreach (Control control in original)
            {
                Button button = control as Button;
                Label label = control as Label;
                if (button != null)
                {
                    button.Anchor = AnchorStyles.None;
                    button.AutoSize = true;
                    button.MinimumSize = new Size(92, 32);
                    button.Margin = new Padding(0, 0, 8, 0);
                    actions.Controls.Add(button);
                    StyleButton(button, button == accept || button.Text == "Export critical (CSV)");
                }
                else if (label != null && (label.Anchor & AnchorStyles.Bottom) != 0)
                {
                    label.AutoSize = true;
                    label.MaximumSize = new Size(360, 0);
                    label.Anchor = AnchorStyles.None;
                    label.Margin = new Padding(4, 5, 0, 0);
                    actions.Controls.Add(label);
                }
                else if (label != null) headline = label;
                else
                {
                    control.Dock = DockStyle.Fill;
                    body.Controls.Add(control);
                }
            }
            if (headline != null)
            {
                headline.Dock = DockStyle.Top;
                headline.Height = 64;
                body.Controls.Add(headline);
            }
            form.Controls.Add(body);
            form.Controls.Add(actions);
            form.Controls.Add(CreateHeader(title, subtitle));
            form.AcceptButton = accept;
            form.CancelButton = cancel;
            form.ClientSize = new Size(originalSize.Width, originalSize.Height + 88);
            form.MinimumSize = new Size(form.MinimumSize.Width, form.MinimumSize.Height + 88);
            StyleControls(body);
        }

        private static Panel CreateHeader(string title, string subtitle)
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Accent, Padding = new Padding(16, 10, 16, 10) };
            Label heading = new Label
            {
                Text = title, Dock = DockStyle.Top, Height = 25,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
            };
            Label description = new Label
            {
                Text = subtitle, Dock = DockStyle.Fill, ForeColor = Color.White, BackColor = Color.Transparent,
                Padding = new Padding(0, 5, 0, 0), UseMnemonic = false
            };
            header.Controls.Add(description);
            header.Controls.Add(heading);
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Brush gradient = new LinearGradientBrush(header.ClientRectangle,
                    Color.FromArgb(13, 121, 199), Color.FromArgb(0, 86, 156), LinearGradientMode.Vertical))
                    e.Graphics.FillRectangle(gradient, header.ClientRectangle);
                // Low-opacity highlight gives a little depth without reducing text contrast.
                using (Pen highlight = new Pen(Color.FromArgb(45, Color.White)))
                    e.Graphics.DrawLine(highlight, 0, 0, header.Width, 0);
                using (Brush brush = new SolidBrush(Orange)) e.Graphics.FillRectangle(brush, 0, header.Height - 3, header.Width, 3);
            };
            return header;
        }

        /// <summary>Paint the existing Button: Click, DialogResult, keyboard and accessibility stay native.</summary>
        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(36, 53, 72);
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            if (!PaintedButtons.Add(button)) return;
            bool hover = false, pressed = false;
            button.MouseEnter += delegate { hover = true; button.Invalidate(); };
            button.MouseLeave += delegate { hover = false; pressed = false; button.Invalidate(); };
            button.MouseDown += delegate { pressed = true; button.Invalidate(); };
            button.MouseUp += delegate { pressed = false; button.Invalidate(); };
            button.GotFocus += delegate { button.Invalidate(); };
            button.LostFocus += delegate { button.Invalidate(); };
            button.Disposed += delegate { PaintedButtons.Remove(button); };
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                bool blue = button.BackColor == Accent;
                Color top = blue ? Color.FromArgb(18, 126, 204) : Color.White;
                Color bottom = blue ? Color.FromArgb(0, 96, 170) : Color.FromArgb(237, 243, 249);
                if (hover) { top = blue ? Color.FromArgb(30, 142, 218) : Color.FromArgb(244, 250, 255); }
                if (pressed) { top = bottom; }
                if (!button.Enabled) { top = Color.FromArgb(240, 242, 245); bottom = top; }
                e.Graphics.Clear(button.Parent == null ? Background : button.Parent.BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(1, 1, button.Width - 3, button.Height - 3);
                using (GraphicsPath path = Rounded(bounds, 6))
                using (Brush fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical))
                using (Pen border = new Pen(blue && button.Enabled ? Color.FromArgb(0, 85, 153) : Color.FromArgb(185, 201, 217)))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(border, path);
                }
                TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle,
                    button.Enabled ? button.ForeColor : Color.FromArgb(137, 148, 160),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (button.Focused)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), button.ForeColor, bottom);
            };
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>A rounded border around the same native TextBox; parsing, text and events stay intact.</summary>
        private static void FrameInput(TextBox input)
        {
            if (input.Multiline || input.Dock != DockStyle.None) return;
            Control parent = input.Parent;
            int order = parent.Controls.GetChildIndex(input);
            Panel frame = new Panel
            {
                Location = new Point(input.Left - 1, input.Top - 4),
                Size = new Size(input.Width + 2, Math.Max(29, input.Height + 8)),
                TabIndex = input.TabIndex, BackColor = parent.BackColor
            };
            input.BorderStyle = BorderStyle.None;
            input.Location = new Point(8, (frame.Height - input.PreferredHeight) / 2);
            input.Width = Math.Max(10, frame.Width - 16);
            input.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            input.BackColor = input.ReadOnly ? Color.FromArgb(242, 246, 250) : Color.White;
            parent.Controls.Add(frame);
            parent.Controls.SetChildIndex(frame, order);
            frame.Controls.Add(input);
            frame.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (!input.Visible) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Rounded(new Rectangle(1, 1, frame.Width - 3, frame.Height - 3), 6))
                using (Brush fill = new SolidBrush(input.Enabled ? input.BackColor : Color.FromArgb(240, 243, 247)))
                using (Pen border = new Pen(input.Focused ? Accent : Color.FromArgb(185, 201, 217)))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(border, path);
                }
            };
            input.VisibleChanged += delegate { frame.Invalidate(); };
            input.GotFocus += delegate { frame.Invalidate(); };
            input.LostFocus += delegate { frame.Invalidate(); };
            input.EnabledChanged += delegate
            {
                input.BackColor = input.Enabled && !input.ReadOnly ? Color.White : Color.FromArgb(240, 243, 247);
                frame.Invalidate();
            };
            frame.Click += delegate { input.Focus(); };
        }

        /// <summary>Colours and grid readability; selection, sorting and values stay with each form.</summary>
        /// <summary>Styles controls created after Apply / ApplyResults (e.g. grids rebuilt at run time).</summary>
        public static void Restyle(Control parent) { StyleControls(parent); }

        private static void StyleControls(Control parent)
        {
            Control[] children = new Control[parent.Controls.Count];
            parent.Controls.CopyTo(children, 0);
            foreach (Control control in children)
            {
                TextBox input = control as TextBox;
                if (input != null) FrameInput(input);
                if (control is GroupBox) control.ForeColor = Color.FromArgb(36, 53, 72);
                Button button = control as Button;
                if (button != null) StyleButton(button, false);
                ComboBox choices = control as ComboBox;
                if (choices != null)
                {
                    int dropdownWidth = choices.Width;
                    foreach (object item in choices.Items)
                        dropdownWidth = Math.Max(dropdownWidth, TextRenderer.MeasureText(item.ToString(), choices.Font).Width + 28);
                    choices.DropDownWidth = Math.Min(dropdownWidth, Screen.FromControl(choices).WorkingArea.Width - 40);
                }
                DataGridView grid = control as DataGridView;
                if (grid != null)
                {
                    grid.BackgroundColor = Color.White;
                    grid.BorderStyle = BorderStyle.None;
                    grid.EnableHeadersVisualStyles = false;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = Accent;
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                    grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4);
                    grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                    grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 234, 250);
                    grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 42, 65);
                    grid.RowTemplate.Height = 26;
                }
                StyleControls(control);
            }
        }
    }
}
