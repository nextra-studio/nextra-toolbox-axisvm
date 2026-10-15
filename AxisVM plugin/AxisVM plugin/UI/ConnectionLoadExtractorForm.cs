using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Input dialog of the Connection Load Extractor: which load combinations, which analysis and the
    /// coordinate system (local or global). The critical search covers all six components. Members and nodes come
    /// from the AxisVM selection and are only summarised here.
    /// </summary>
    public class ConnectionLoadExtractorForm : Form
    {
        /// <summary>1-based ids of the ticked load combinations.</summary>
        public List<int> SelectedComboIds { get; private set; }
        /// <summary>True = non-linear static results, false = linear static.</summary>
        public bool NonLinear { get; private set; }
        /// <summary>Axes the loads are reported in and the critical combinations are searched in.</summary>
        public LoadSystem Axes { get; private set; }

        private CheckedListBox lstCombos;
        private ComboBox cmbAnalysis;
        private ComboBox cmbAxes;

        public ConnectionLoadExtractorForm(string selectionSummary, IList<string> comboNames)
        {
            SelectedComboIds = new List<int>();
            InitializeComponent(selectionSummary, comboNames);
        }

        private void InitializeComponent(string selectionSummary, IList<string> comboNames)
        {
            Theme.Initialize(this);
            Text = "Connection Load Extractor";
            ClientSize = new Size(600, 535);
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
            cmbAxes.Items.AddRange(new object[] { "Local (N, Vy, Vz, Tx, My, Mz)", "Global (Fx, Fy, Fz, Mx, My, Mz)" });
            cmbAxes.SelectedIndex = 1;

            Label lblNote = new Label
            {
                Text = "Loads are the force each member exerts on the node. For each of the six components the\n" +
                       "critical combinations are found: the largest (Max+) and the smallest (Min−) value,\n" +
                       "per member end and over all ends.",
                Location = new Point(12, 410),
                Size = new Size(576, 64),
                ForeColor = SystemColors.GrayText
            };

            Button btnOk = new Button { Text = "Extract", Location = new Point(242, 482), Size = new Size(80, 27) };
            btnOk.Click += BtnOk_Click;
            Button btnCancel = new Button { Text = "Cancel", Location = new Point(328, 482), Size = new Size(80, 27), DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { lblSel, lblCombos, lstCombos, btnAll, btnNone, lblAnalysis, cmbAnalysis,
                lblAxes, cmbAxes, lblNote, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Theme.Apply(this, "Connection load extractor", "Choose combinations and axes. Search all six force and moment components.");
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
            Axes = cmbAxes.SelectedIndex == 0 ? LoadSystem.Local : LoadSystem.Global;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
