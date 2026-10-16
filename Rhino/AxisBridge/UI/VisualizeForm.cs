using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Commands;
using AxisBridge.Library;
using AxisBridge.Visualize;
using Rhino;

namespace AxisBridge.UI
{
    /// <summary>
    /// Floating AxVisualize window: pick what the overlay draws and which load case. The overlay lives
    /// as long as the window; closing it removes everything from the viewports.
    /// </summary>
    public class VisualizeForm : Form
    {
        private const string NoCase = "(none)";

        /// <summary>The open window, or null.</summary>
        public static VisualizeForm Current { get; private set; }

        /// <summary>Settings survive closing the window so reopening shows the same view.</summary>
        private static readonly VisualSettings Last = new VisualSettings();

        /// <summary>True once the user picked a case (including "(none)"), so it is never auto-picked again.</summary>
        private static bool caseChosen;

        private readonly AxVisualConduit conduit;
        private readonly ComboBox caseBox;
        private bool updatingCases;

        public static void Toggle()
        {
            if (Current != null && !Current.IsDisposed)
            {
                Current.Close();
                return;
            }
            Current = new VisualizeForm();
            Current.Show(AttrCommandRunner.RhinoOwner());
        }

        private VisualizeForm()
        {
            Theme.Dialog(this, "AxVisualize", new Size(300, 560));
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(280, 480);
            MaximizeBox = false;

            conduit = new AxVisualConduit(Last);
            conduit.Rebuilt += (s, e) =>
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)FillCases);
            };

            var page = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12, 10, 12, 10)
            };

            page.Controls.Add(SectionLabel("Show"));
            page.Controls.Add(Check("Element info (type, section, material)", Last.Info, v => Last.Info = v));
            page.Controls.Add(Check("Supports", Last.Supports, v => Last.Supports = v));
            page.Controls.Add(Check("Releases at member ends", Last.Releases, v => Last.Releases = v));
            page.Controls.Add(Check("Span direction (one-way panels)", Last.Span, v => Last.Span = v));
            page.Controls.Add(Check("Local axes (members)", Last.LocalAxes, v => Last.LocalAxes = v));
            page.Controls.Add(Check("Other attributes", Last.Other, v => Last.Other = v));

            page.Controls.Add(SectionLabel("Loads"));
            page.Controls.Add(Check("Load arrows", Last.Loads, v => Last.Loads = v));
            page.Controls.Add(Check("Load values", Last.LoadValues, v => Last.LoadValues = v));
            page.Controls.Add(new Label { Text = "Load case", AutoSize = true, Margin = new Padding(3, 6, 3, 2) });
            caseBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
            caseBox.SelectedIndexChanged += (s, e) =>
            {
                if (updatingCases) return;
                caseChosen = true;
                string c = caseBox.SelectedItem as string;
                Last.LoadCase = c == null || c == NoCase ? null : c;
                AxVisualConduit.Redraw();
            };
            page.Controls.Add(caseBox);

            page.Controls.Add(SectionLabel("Size"));
            page.Controls.Add(Slider("Symbols", 25, 300, Last.SymbolScale, v => Last.SymbolScale = v));
            page.Controls.Add(Slider("Arrow length", 25, 400, Last.ArrowScale, v => Last.ArrowScale = v));
            var labelSize = new NumericUpDown { Minimum = 8, Maximum = 24, Value = Math.Max(8, Math.Min(24, Last.LabelSize)), Width = 60 };
            labelSize.ValueChanged += (s, e) => { Last.LabelSize = (int)labelSize.Value; AxVisualConduit.Redraw(); };
            page.Controls.Add(Row("Text size", labelSize));

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 9, 12, 9), BackColor = Color.White };
            var refresh = new Button { Text = "Refresh", Width = 90, Height = 30, Location = new Point(12, 9) };
            refresh.Click += (s, e) => conduit.Invalidate();
            var close = new Button { Text = "Close", Width = 90, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            close.Click += (s, e) => Close();
            Theme.StyleButton(close, true);
            bar.Controls.Add(refresh);
            bar.Controls.Add(close);
            bar.Layout += (s, e) => close.Location = new Point(bar.ClientSize.Width - bar.Padding.Right - close.Width, bar.Padding.Top);
            CancelButton = close;

            Controls.Add(page);
            Controls.Add(Theme.Separator());
            Controls.Add(bar);
            Controls.Add(Theme.Header("AxVisualize", "Display only - nothing is added to the model"));

            Load += (s, e) => { FillCases(); conduit.Start(); };
            FormClosed += (s, e) =>
            {
                conduit.Stop();
                if (Current == this) Current = null;
            };
        }

        /// <summary>Library cases first, then any other case found on objects. Keeps the current pick.</summary>
        private void FillCases()
        {
            var names = new List<string>();
            RhinoDoc doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                foreach (string n in AxLibrary.Load(doc).LoadCaseNames()) AddName(names, n);
            foreach (string n in conduit.CaseNames) AddName(names, n);

            // First open: start on the first case that actually carries loads.
            if (!caseChosen && Last.LoadCase == null && conduit.CaseNames.Count > 0) Last.LoadCase = conduit.CaseNames[0];

            updatingCases = true;
            try
            {
                caseBox.BeginUpdate();
                caseBox.Items.Clear();
                caseBox.Items.Add(NoCase);
                foreach (string n in names) caseBox.Items.Add(n);
                int pick = Last.LoadCase == null ? 0 : names.FindIndex(n => string.Equals(n, Last.LoadCase, StringComparison.OrdinalIgnoreCase)) + 1;
                caseBox.SelectedIndex = Math.Max(0, pick);
                if (pick <= 0) Last.LoadCase = null;
                caseBox.EndUpdate();
            }
            finally { updatingCases = false; }
            AxVisualConduit.Redraw();
        }

        private static void AddName(List<string> names, string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            if (!names.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) names.Add(n.Trim());
        }

        private static Label SectionLabel(string text)
        {
            return new Label { Text = text, Font = Theme.Bold, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        }

        private static CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            c.CheckedChanged += (s, e) => { set(c.Checked); AxVisualConduit.Redraw(); };
            return c;
        }

        /// <summary>Percentage slider (25 % .. max %) bound to a scale factor.</summary>
        private static Control Slider(string text, int min, int max, double value, Action<double> set)
        {
            var t = new TrackBar
            {
                Minimum = min,
                Maximum = max,
                TickFrequency = 25,
                SmallChange = 5,
                LargeChange = 25,
                Value = Math.Max(min, Math.Min(max, (int)Math.Round(value * 100))),
                Width = 150,
                AutoSize = false,
                Height = 30
            };
            var pct = new Label { AutoSize = true, Margin = new Padding(3, 7, 3, 3), Text = t.Value + " %" };
            t.ValueChanged += (s, e) => { pct.Text = t.Value + " %"; set(t.Value / 100.0); AxVisualConduit.Redraw(); };
            Control r = Row(text, t);
            r.Controls.Add(pct);
            return r;
        }

        private static FlowLayoutPanel Row(string text, Control editor)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            p.Controls.Add(new Label { Text = text, AutoSize = false, Width = 80, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 3) });
            p.Controls.Add(editor);
            return p;
        }
    }
}
