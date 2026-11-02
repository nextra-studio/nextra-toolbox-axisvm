using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Input dialog of the Connection Load Extractor: which load combinations, which analysis and the
    /// coordinate system (local, global or custom = global turned about Z and then about x'). The critical search
    /// covers all six components. Members and nodes come from the AxisVM selection and are only summarised here.
    /// A 3D preview shows the chosen axes; "Show axes in model" draws them on the AxisVM model as well.
    /// </summary>
    public class ConnectionLoadExtractorForm : Form
    {
        /// <summary>1-based ids of the ticked load combinations.</summary>
        public List<int> SelectedComboIds { get; private set; }
        /// <summary>True = non-linear static results, false = linear static.</summary>
        public bool NonLinear { get; private set; }
        /// <summary>Axes the loads are reported in and the critical combinations are searched in.</summary>
        public LoadSystem Axes { get; private set; }
        /// <summary>The angles of the custom system (also kept when another system is chosen).</summary>
        public CustomAxes Custom { get { return editor.Value; } }
        /// <summary>True when the axis triad is on the model as the dialog closes.</summary>
        public bool AxesShown { get; private set; }

        private CheckedListBox lstCombos;
        private ComboBox cmbAnalysis;
        private ComboBox cmbAxes;
        private AxisRotationEditor editor;
        private Button btnShowAxes;
        private readonly LocalFrame localSample;                       // first member's local axes, for the preview
        private readonly Func<LoadSystem, CustomAxes, string> showAxes; // null = no model to draw on
        private readonly Action hideAxes;
        private readonly Timer redrawTimer = new Timer { Interval = 300 };

        public ConnectionLoadExtractorForm(string selectionSummary, IList<string> comboNames)
            : this(selectionSummary, comboNames, null, null, null) { }

        public ConnectionLoadExtractorForm(string selectionSummary, IList<string> comboNames, LocalFrame localSample,
            Func<LoadSystem, CustomAxes, string> showAxes, Action hideAxes)
        {
            SelectedComboIds = new List<int>();
            this.localSample = localSample;
            this.showAxes = showAxes;
            this.hideAxes = hideAxes;
            InitializeComponent(selectionSummary, comboNames);
        }

        private void InitializeComponent(string selectionSummary, IList<string> comboNames)
        {
            Theme.Initialize(this);
            Text = "Connection Load Extractor";
            ClientSize = new Size(940, 535);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            // What the AxisVM selection gave us
            Label lblSel = new Label
            {
                Text = "Selection: " + selectionSummary,
                Location = new Point(12, 12),
                Size = new Size(576, 36)
            };

            // Load combinations - all ticked by default
            Label lblCombos = new Label { Text = "Load combinations:", Location = new Point(12, 52), AutoSize = true };
            lstCombos = new CheckedListBox
            {
                Location = new Point(12, 72),
                Size = new Size(576, 220),
                CheckOnClick = true,
                HorizontalScrollbar = true,
                IntegralHeight = false
            };
            for (int i = 0; i < comboNames.Count; i++) lstCombos.Items.Add(string.Format("{0}: {1}", i + 1, comboNames[i]), true);

            Button btnAll = new Button { Text = "All", Location = new Point(12, 298), Size = new Size(70, 25) };
            btnAll.Click += delegate { SetAllCombos(true); };
            Button btnNone = new Button { Text = "None", Location = new Point(88, 298), Size = new Size(70, 25) };
            btnNone.Click += delegate { SetAllCombos(false); };

            // Analysis type
            Label lblAnalysis = new Label { Text = "Results:", Location = new Point(12, 341), AutoSize = true };
            cmbAnalysis = new ComboBox { Location = new Point(170, 338), Size = new Size(408, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAnalysis.Items.AddRange(new object[] { "Linear static", "Non-linear static" });
            cmbAnalysis.SelectedIndex = 0;

            // One coordinate system for both the output and the critical search
            Label lblAxes = new Label { Text = "Coordinate system:", Location = new Point(12, 373), AutoSize = true };
            cmbAxes = new ComboBox { Location = new Point(170, 370), Size = new Size(408, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAxes.Items.AddRange(new object[] { "Local (N, Vy, Vz, Tx, My, Mz)", "Global (Fx, Fy, Fz, Mx, My, Mz)", "Custom: rotated global (Fx', Fy', Fz', Mx', My', Mz')" });
            cmbAxes.SelectedIndex = 1;

            Label lblNote = new Label
            {
                Text = "Loads are the force each member exerts on the node. For each of the six components the\n" +
                       "critical combinations are found: the largest (Max+) and the smallest (Min−) value,\n" +
                       "per member end and over all ends.\n" +
                       "Custom: the global axes turned by αz about Z, then by αx about the new x'. Positive angles follow\n" +
                       "the right-hand rule (counter-clockwise seen from the axis tip). x' stays in the global XY plane.",
                Location = new Point(12, 404),
                Size = new Size(576, 92),
                ForeColor = SystemColors.GrayText
            };

            // Right column: the custom angles, the 3D preview and the in-model triad
            Label lblRot = new Label { Text = "Axes preview (drag to orbit)", Location = new Point(604, 12), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            editor = new AxisRotationEditor(324, 330) { Location = new Point(604, 36) };
            editor.ValueChanged += delegate { AxesChanged(); };
            cmbAxes.SelectedIndexChanged += delegate { AxesChanged(); };

            btnShowAxes = new Button { Text = "Show axes in model", Location = new Point(604, 470), Size = new Size(324, 28), Enabled = showAxes != null };
            btnShowAxes.Click += delegate { ToggleAxes(); };
            redrawTimer.Tick += delegate { redrawTimer.Stop(); if (AxesShown) DrawAxes(); };

            Button btnOk = new Button { Text = "Extract", Location = new Point(242, 482), Size = new Size(80, 27) };
            btnOk.Click += BtnOk_Click;
            Button btnCancel = new Button { Text = "Cancel", Location = new Point(328, 482), Size = new Size(80, 27), DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { lblSel, lblCombos, lstCombos, btnAll, btnNone, lblAnalysis, cmbAnalysis,
                lblAxes, cmbAxes, lblNote, lblRot, editor, btnShowAxes, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            AxesChanged();
            Theme.Apply(this, "Connection load extractor", "Choose combinations and axes (local, global or a custom rotation). Search all six force and moment components.");
        }

        private LoadSystem SelectedSystem
        {
            get { return cmbAxes.SelectedIndex == 0 ? LoadSystem.Local : cmbAxes.SelectedIndex == 2 ? LoadSystem.Custom : LoadSystem.Global; }
        }

        /// <summary>System or angle changed: update the preview now, the model triad shortly after (typing stays smooth).</summary>
        private void AxesChanged()
        {
            editor.SetSystem(SelectedSystem, localSample);
            if (AxesShown) { redrawTimer.Stop(); redrawTimer.Start(); }
        }

        private void ToggleAxes()
        {
            if (AxesShown)
            {
                hideAxes();
                AxesShown = false;
                btnShowAxes.Text = "Show axes in model";
                return;
            }
            AxesShown = true;
            btnShowAxes.Text = "Hide axes in model";
            DrawAxes();
        }

        private void DrawAxes()
        {
            string error = showAxes(SelectedSystem, editor.Value);
            if (error != null) MessageBox.Show(error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Ticks or clears every combination.</summary>
        private void SetAllCombos(bool value)
        {
            for (int i = 0; i < lstCombos.Items.Count; i++) lstCombos.SetItemChecked(i, value);
        }

        /// <summary>Validates and copies the choices into the public properties.</summary>
        private void BtnOk_Click(object sender, EventArgs e)
        {
            SelectedComboIds.Clear();
            foreach (int idx in lstCombos.CheckedIndices) SelectedComboIds.Add(idx + 1); // list index -> 1-based id
            if (SelectedComboIds.Count == 0)
            {
                MessageBox.Show("Tick at least one load combination.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            NonLinear = cmbAnalysis.SelectedIndex == 1;
            Axes = SelectedSystem;
            redrawTimer.Stop();
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) redrawTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
