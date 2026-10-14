using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AxisBridge.Attributes;

namespace AxisBridge.UI
{
    /// <summary>
    /// One editable attribute in a dialog.
    ///
    /// The important part is <see cref="Touched"/>: a field only writes its value when the user
    /// changed it (or when no selected object had the key, so the shown default must be written).
    /// A field whose objects disagree starts as "varies" and leaves every object's own value alone
    /// unless the user types/picks something.
    /// </summary>
    public abstract class Field
    {
        public string Key;
        public bool Touched;
        public bool StartedVaries;

        /// <summary>Raised whenever the user changes the value (used to refresh dependent UI).</summary>
        public event EventHandler Changed;

        public abstract Control Control { get; }

        /// <summary>The attribute value to write; "" deletes the key.</summary>
        public abstract string Value { get; }

        /// <summary>True while the field still shows "varies" (nothing chosen).</summary>
        public abstract bool ShowsVaries { get; }

        protected bool loading;

        protected void RaiseChanged()
        {
            if (loading) return;
            Touched = !ShowsVaries;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Fills the field from the selection. Absent keys show the default and count as touched.</summary>
        public void Load(FieldState state, string defaultValue)
        {
            loading = true;
            try
            {
                StartedVaries = state.Kind == FieldStateKind.Varies;
                switch (state.Kind)
                {
                    case FieldStateKind.Same: SetValue(state.Value); Touched = false; break;
                    case FieldStateKind.Varies: SetVaries(); Touched = false; break;
                    default: SetValue(defaultValue ?? ""); Touched = !string.IsNullOrEmpty(defaultValue); break;
                }
            }
            finally { loading = false; }
        }

        /// <summary>Programmatic change (e.g. type defaults) that should be written like a user edit.</summary>
        public void Assign(string value)
        {
            loading = true;
            try { SetValue(value); }
            finally { loading = false; }
            Touched = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public abstract void SetValue(string value);
        protected abstract void SetVaries();

        /// <summary>Adds this field's change (if any) to the edit.</summary>
        public void Collect(AttrEdit edit)
        {
            if (!Touched || ShowsVaries) return;
            edit.Scalars[Key] = Value;
        }
    }

    /// <summary>TextBox with a grey placeholder (Windows cue banner).</summary>
    public class CueTextBox : TextBox
    {
        private string cue = "";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public string Cue
        {
            get { return cue; }
            set { cue = value ?? ""; if (IsHandleCreated) Apply(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Apply();
        }

        private void Apply()
        {
            const int EM_SETCUEBANNER = 0x1501;
            SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, cue);
        }
    }

    /// <summary>Free text field (thickness, eccentricity, stiffness...).</summary>
    public class TextField : Field
    {
        public readonly CueTextBox Box;
        private readonly string normalCue;

        public TextField(string key, string cue = "")
        {
            Key = key;
            normalCue = cue ?? "";
            Box = new CueTextBox { Dock = DockStyle.Fill, Cue = normalCue };
            Box.TextChanged += (s, e) => RaiseChanged();
        }

        public override Control Control { get { return Box; } }
        public override string Value { get { return Box.Text.Trim(); } }
        public override bool ShowsVaries { get { return StartedVaries && Box.Text.Trim().Length == 0; } }

        public override void SetValue(string value) { Box.Cue = normalCue; Box.Text = value ?? ""; }
        protected override void SetVaries() { Box.Text = ""; Box.Cue = Theme.VariesCue; }
    }

    /// <summary>
    /// A dropdown. Items are (label, value) pairs; when <paramref name="editable"/> the user may also
    /// type a value that is not in the list (material / section names).
    /// </summary>
    public class ComboField : Field
    {
        public readonly ComboBox Combo;
        private readonly bool editable;
        private readonly List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
        private bool variesItemPresent;

        public ComboField(string key, bool editable)
        {
            Key = key;
            this.editable = editable;
            Combo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                AutoCompleteMode = editable ? AutoCompleteMode.SuggestAppend : AutoCompleteMode.None,
                AutoCompleteSource = editable ? AutoCompleteSource.ListItems : AutoCompleteSource.None,
                MaxDropDownItems = 20
            };
            Combo.SelectedIndexChanged += (s, e) => RaiseChanged();
            if (editable) Combo.TextChanged += (s, e) => RaiseChanged();
        }

        public override Control Control { get { return Combo; } }

        /// <summary>Replaces the list items, keeping the current value.</summary>
        public void SetItems(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            string current = ShowsVaries ? null : Value;
            bool wasLoading = loading;
            loading = true;
            try
            {
                items.Clear();
                items.AddRange(pairs);
                Combo.BeginUpdate();
                Combo.Items.Clear();
                if (variesItemPresent) Combo.Items.Add(Theme.VariesItem);
                foreach (var p in items) Combo.Items.Add(p.Key);
                Combo.EndUpdate();
                if (current != null) SetValue(current);
                else if (variesItemPresent) Combo.SelectedIndex = 0;
            }
            finally { loading = wasLoading; }
        }

        public void SetItems(IEnumerable<string> values)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (string v in values) pairs.Add(new KeyValuePair<string, string>(v, v));
            SetItems(pairs);
        }

        public override bool ShowsVaries
        {
            get
            {
                if (!StartedVaries) return false;
                if (editable) return Combo.Text.Trim().Length == 0 || Combo.Text == Theme.VariesItem;
                return Combo.SelectedItem as string == Theme.VariesItem;
            }
        }

        public override string Value
        {
            get
            {
                string text = Combo.Text.Trim();
                foreach (var p in items)
                    if (string.Equals(p.Key, text, StringComparison.Ordinal)) return p.Value;
                return editable ? text : "";
            }
        }

        public override void SetValue(string value)
        {
            value = value ?? "";
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    Combo.SelectedIndex = i + (variesItemPresent ? 1 : 0);
                    return;
                }
            }
            if (editable) { Combo.SelectedIndex = -1; Combo.Text = value; }
            else if (Combo.Items.Count > 0) Combo.SelectedIndex = variesItemPresent ? 1 : 0;
        }

        protected override void SetVaries()
        {
            if (!variesItemPresent)
            {
                variesItemPresent = true;
                Combo.Items.Insert(0, Theme.VariesItem);
            }
            Combo.SelectedIndex = 0;
            if (editable) Combo.Text = "";
        }
    }

    /// <summary>
    /// End releases: a preset dropdown plus, for "Custom", one 6-value box per end.
    /// Writes "fix" / "pin" / "semi-pin" / "start=...; end=...".
    /// </summary>
    public class ReleaseField : Field
    {
        private readonly ComboBox preset;
        private readonly CueTextBox start;
        private readonly CueTextBox end;
        private readonly TableLayoutPanel panel;
        private readonly TableLayoutPanel custom;
        private bool variesItem;

        private static readonly string[] Labels =
        {
            "Fixed - rigid at both ends",
            "Pinned - free bending (yy, zz) at both ends",
            "Semi-pinned - 1 kNm/rad bending at both ends",
            "Custom - per end and per DOF"
        };
        private const string RigidSix = "1e10,1e10,1e10,1e10,1e10,1e10";
        private const string PinSix = "1e10,1e10,1e10,1e10,0,0";

        public ReleaseField()
        {
            Key = AxKeys.Release;

            preset = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            preset.Items.AddRange(Labels);
            preset.SelectedIndex = 0;

            start = new CueTextBox { Dock = DockStyle.Fill, Cue = "x, y, z, xx, yy, zz", Text = PinSix };
            end = new CueTextBox { Dock = DockStyle.Fill, Cue = "x, y, z, xx, yy, zz", Text = PinSix };

            custom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            custom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            custom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            custom.Controls.Add(new Label { Text = "Start", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            custom.Controls.Add(start, 1, 0);
            custom.Controls.Add(new Label { Text = "End", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            custom.Controls.Add(end, 1, 1);
            custom.Controls.Add(Theme.Hint("0 = released, 1e10 = rigid, anything between = semi-rigid [kN/m, kNm/rad]. Start/end follow the Rhino curve direction."), 1, 2);

            panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            panel.Controls.Add(preset, 0, 0);
            panel.Controls.Add(custom, 0, 1);

            preset.SelectedIndexChanged += (s, e) => { UpdateCustom(); RaiseChanged(); };
            start.TextChanged += (s, e) => RaiseChanged();
            end.TextChanged += (s, e) => RaiseChanged();
            UpdateCustom();
        }

        public override Control Control { get { return panel; } }

        private int PresetIndex { get { return preset.SelectedIndex - (variesItem ? 1 : 0); } }

        public override bool ShowsVaries { get { return StartedVaries && PresetIndex < 0; } }

        private void UpdateCustom()
        {
            custom.Visible = PresetIndex == 3;
        }

        public override string Value
        {
            get
            {
                switch (PresetIndex)
                {
                    case 0: return "fix";
                    case 1: return "pin";
                    case 2: return "semi-pin";
                    case 3: return "start=" + start.Text.Trim() + "; end=" + end.Text.Trim();
                    default: return "";
                }
            }
        }

        public override void SetValue(string value)
        {
            string v = (value ?? "").Trim();
            string k = v.ToLowerInvariant();
            int off = variesItem ? 1 : 0;

            if (k == "" || k == "fix" || k == "fixed") preset.SelectedIndex = off + 0;
            else if (k == "pin" || k == "pinned") preset.SelectedIndex = off + 1;
            else if (k == "semi-pin" || k == "semipin") preset.SelectedIndex = off + 2;
            else
            {
                Dictionary<string, string> f = AttrReader.ParseFields(v);
                string s, e;
                bool hasS = f.TryGetValue("start", out s) || f.TryGetValue("s", out s);
                bool hasE = f.TryGetValue("end", out e) || f.TryGetValue("e", out e);
                if (!hasS && !hasE) { s = v; e = v; }
                start.Text = hasS || !hasE ? s : RigidSix;
                end.Text = hasE || !hasS ? e : RigidSix;
                preset.SelectedIndex = off + 3;
            }
            UpdateCustom();
        }

        protected override void SetVaries()
        {
            if (!variesItem) { variesItem = true; preset.Items.Insert(0, Theme.VariesItem); }
            preset.SelectedIndex = 0;
            UpdateCustom();
        }
    }

    /// <summary>
    /// Nodal or line support: a preset dropdown plus a 6-value box for "Custom".
    /// Writes "" (no support), "pinned", "fixed", "roller-z" or six stiffnesses.
    /// </summary>
    public class SupportField : Field
    {
        private readonly ComboBox preset;
        private readonly CueTextBox customBox;
        private readonly TableLayoutPanel panel;
        private readonly Control hint;
        private bool variesItem;

        private static readonly string[] Labels =
        {
            "None",
            "Pinned - translations fixed, rotations free",
            "Fixed - all 6 DOFs fixed",
            "Roller - vertical (Z) fixed only",
            "Custom - 6 stiffnesses"
        };
        private static readonly string[] Values = { "", "pinned", "fixed", "roller-z", null };

        public SupportField(bool lineSupport)
        {
            Key = AxKeys.Support;
            preset = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            preset.Items.AddRange(Labels);
            preset.SelectedIndex = 0;

            customBox = new CueTextBox { Dock = DockStyle.Fill, Cue = "kx, ky, kz, kxx, kyy, kzz", Text = "1e10,1e10,1e10,0,0,0" };
            hint = Theme.Hint(lineSupport
                ? "Global directions. Per metre of line: kN/m/m (translations), kNm/rad/m (rotations). 1e10 = rigid, 0 = free."
                : "Global directions. kN/m (translations), kNm/rad (rotations). 1e10 = rigid, 0 = free.");

            panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            panel.Controls.Add(preset, 0, 0);
            panel.Controls.Add(customBox, 0, 1);
            panel.Controls.Add(hint, 0, 2);

            preset.SelectedIndexChanged += (s, e) => { UpdateCustom(); RaiseChanged(); };
            customBox.TextChanged += (s, e) => RaiseChanged();
            UpdateCustom();
        }

        public override Control Control { get { return panel; } }
        private int PresetIndex { get { return preset.SelectedIndex - (variesItem ? 1 : 0); } }
        public override bool ShowsVaries { get { return StartedVaries && PresetIndex < 0; } }

        private void UpdateCustom()
        {
            bool c = PresetIndex == 4;
            customBox.Visible = c;
            hint.Visible = c;
        }

        public override string Value
        {
            get
            {
                int i = PresetIndex;
                if (i < 0) return "";
                return Values[i] ?? customBox.Text.Trim();
            }
        }

        public override void SetValue(string value)
        {
            string k = (value ?? "").Trim().ToLowerInvariant();
            int off = variesItem ? 1 : 0;
            if (k == "" || k == "-") preset.SelectedIndex = off;
            else if (k == "pinned" || k == "pin") preset.SelectedIndex = off + 1;
            else if (k == "fixed" || k == "fix") preset.SelectedIndex = off + 2;
            else if (k == "roller-z" || k == "rollerz" || k == "roller") preset.SelectedIndex = off + 3;
            else { customBox.Text = value.Trim(); preset.SelectedIndex = off + 4; }
            UpdateCustom();
        }

        protected override void SetVaries()
        {
            if (!variesItem) { variesItem = true; preset.Items.Insert(0, Theme.VariesItem); }
            preset.SelectedIndex = 0;
            UpdateCustom();
        }
    }
}
