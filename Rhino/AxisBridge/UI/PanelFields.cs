using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Loads;

namespace AxisBridge.UI
{
    /// <summary>
    /// One-way load panel span direction: Auto (shorter span) / Auto (longer span) / Global X /
    /// Global Y / Custom vector, with a "Pick in viewport" button. Writes "" (auto), "autolong",
    /// "x", "y" or "dx,dy,dz". Both Auto options follow each panel's own outline.
    /// </summary>
    public class SpanField : Field
    {
        private readonly RadioButton rAuto, rAutoLong, rX, rY, rCustom;
        private readonly CueTextBox vector;
        private readonly Button pick;
        private readonly TableLayoutPanel panel;
        private bool none;   // no radio checked = "varies"

        /// <summary>Raised when the user clicks "Pick in viewport".</summary>
        public event Action PickRequested;

        public SpanField()
        {
            Key = AxKeys.SpanDirection;

            rAuto = Radio("Auto - shorter span", true);
            rAutoLong = Radio("Auto - longer span", false);
            rX = Radio("Global X", false);
            rY = Radio("Global Y", false);
            rCustom = Radio("Custom", false);

            var radios = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            radios.Controls.AddRange(new Control[] { rAuto, rAutoLong, rX, rY, rCustom });

            vector = new CueTextBox { Width = 160, Cue = "dx, dy, dz  e.g. 1,1,0" };
            pick = new Button { Text = "Pick in viewport...", AutoSize = true, Height = 25 };
            pick.Click += (s, e) => PickRequested?.Invoke();

            var custom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            custom.Controls.Add(vector);
            custom.Controls.Add(pick);

            panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            panel.Controls.Add(radios, 0, 0);
            panel.Controls.Add(custom, 0, 1);

            foreach (RadioButton r in new[] { rAuto, rAutoLong, rX, rY, rCustom })
                r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) { none = false; UpdateState(); RaiseChanged(); } };
            vector.TextChanged += (s, e) => { if (rCustom.Checked) RaiseChanged(); };
            UpdateState();
        }

        private static RadioButton Radio(string text, bool on)
        {
            return new RadioButton { Text = text, AutoSize = true, Checked = on, Margin = new Padding(0, 4, 14, 2) };
        }

        private void UpdateState()
        {
            vector.Enabled = rCustom.Checked;
        }

        public override Control Control { get { return panel; } }
        public override bool ShowsVaries { get { return StartedVaries && none; } }

        public override string Value
        {
            get
            {
                if (none) return "";
                if (rAutoLong.Checked) return SpanTools.AutoLong;
                if (rX.Checked) return "x";
                if (rY.Checked) return "y";
                if (rCustom.Checked) return vector.Text.Trim();
                return "";
            }
        }

        /// <summary>The value to preview (null while it still shows "varies").</summary>
        public string PreviewValue { get { return ShowsVaries ? null : Value; } }

        public override void SetValue(string value)
        {
            string k = (value ?? "").Trim().ToLowerInvariant();
            none = false;
            if (k == "" || k == "auto" || k == "0" || k == "-") rAuto.Checked = true;
            else if (SpanTools.IsAutoLong(k)) rAutoLong.Checked = true;
            else if (k == "x") rX.Checked = true;
            else if (k == "y") rY.Checked = true;
            else { vector.Text = value.Trim(); rCustom.Checked = true; }
            UpdateState();
        }

        protected override void SetVaries()
        {
            none = true;
            foreach (RadioButton r in new[] { rAuto, rAutoLong, rX, rY, rCustom }) r.Checked = false;
            UpdateState();
        }

        /// <summary>Sets a picked direction as Custom.</summary>
        public void SetPicked(string vectorText)
        {
            vector.Text = vectorText;
            Assign(vectorText);
        }
    }

    /// <summary>
    /// Where a two-way load panel sends its load: automatically (all members and nodes in / on the
    /// panel) or only to picked members / nodes. Writes "" or the picked Rhino ids.
    /// </summary>
    public class TargetsField : Field
    {
        private readonly RadioButton rAuto, rOnly;
        private readonly Label count;
        private readonly Button pick, clear;
        private readonly TableLayoutPanel panel;
        private readonly List<Guid> ids = new List<Guid>();
        private bool none;

        public event Action PickRequested;

        public TargetsField()
        {
            Key = AxKeys.LoadTargets;

            rAuto = new RadioButton { Text = "Automatic - every member and node in or on the panel", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 2) };
            rOnly = new RadioButton { Text = "Only the members / nodes I pick", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

            count = new Label { AutoSize = true, ForeColor = Theme.HintColor, Margin = new Padding(18, 8, 10, 0) };
            pick = new Button { Text = "Pick members...", AutoSize = true, Height = 25 };
            clear = new Button { Text = "Clear", AutoSize = true, Height = 25 };
            pick.Click += (s, e) => PickRequested?.Invoke();
            clear.Click += (s, e) => { ids.Clear(); Changed2(); };

            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.AddRange(new Control[] { count, pick, clear });

            panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            panel.Controls.Add(rAuto, 0, 0);
            panel.Controls.Add(rOnly, 0, 1);
            panel.Controls.Add(row, 0, 2);

            rAuto.CheckedChanged += (s, e) => { if (rAuto.Checked) { none = false; Changed2(); } };
            rOnly.CheckedChanged += (s, e) => { if (rOnly.Checked) { none = false; Changed2(); } };
            UpdateState();
        }

        private void Changed2()
        {
            UpdateState();
            RaiseChanged();
        }

        private void UpdateState()
        {
            pick.Enabled = clear.Enabled = rOnly.Checked;
            count.Text = rOnly.Checked
                ? (ids.Count == 0 ? "nothing picked yet" : ids.Count + " picked (shown orange)")
                : "";
        }

        public override Control Control { get { return panel; } }
        public override bool ShowsVaries { get { return StartedVaries && none; } }

        /// <summary>True when "only picked" is chosen but nothing was picked.</summary>
        public bool IsIncomplete { get { return rOnly.Checked && ids.Count == 0; } }

        /// <summary>The targets currently shown (for the viewport highlight).</summary>
        public IList<Guid> Ids { get { return rOnly.Checked ? ids : new List<Guid>(); } }

        public override string Value
        {
            get
            {
                if (none || !rOnly.Checked || ids.Count == 0) return "";
                var s = new List<string>();
                foreach (Guid g in ids) s.Add(g.ToString());
                return string.Join("; ", s.ToArray());
            }
        }

        public override void SetValue(string value)
        {
            ids.Clear();
            none = false;
            foreach (string part in (value ?? "").Split(';'))
            {
                Guid g;
                if (Guid.TryParse(part.Trim(), out g) && !ids.Contains(g)) ids.Add(g);
            }
            if (ids.Count > 0) rOnly.Checked = true; else rAuto.Checked = true;
            UpdateState();
        }

        protected override void SetVaries()
        {
            none = true;
            rAuto.Checked = false;
            rOnly.Checked = false;
            UpdateState();
        }

        /// <summary>Adds viewport-picked ids.</summary>
        public void AddPicked(IEnumerable<Guid> picked)
        {
            foreach (Guid g in picked) if (!ids.Contains(g)) ids.Add(g);
            rOnly.Checked = true;
            UpdateState();
            Assign(Value);
        }
    }
}
