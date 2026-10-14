using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Input dialog of the Connection Load Extractor: which load combinations, which analysis, the output
    /// coordinate system and the direction the critical combination is searched in. Members and nodes come
    /// from the AxisVM selection and are only summarised here.
    /// </summary>
    public class ConnectionLoadExtractorForm : Form
    {
        /// <summary>1-based ids of the ticked load combinations.</summary>
        public List<int> SelectedComboIds { get; private set; }
        /// <summary>True = non-linear static results, false = linear static.</summary>
        public bool NonLinear { get; private set; }
        /// <summary>Axes the exported loads are written in.</summary>
        public LoadSystem OutputSystem { get; private set; }
        /// <summary>Axes the critical direction is defined in.</summary>
        public LoadSystem CriticalSystem { get; private set; }
        /// <summary>Critical component index 0..5 (forces 0..2, moments 3..5).</summary>
        public int CriticalComponent { get; private set; }

        private CheckedListBox lstCombos;
        private ComboBox cmbAnalysis;
        private ComboBox cmbOutput;
        private ComboBox cmbCritSystem;
        private ComboBox cmbCritComponent;

        public ConnectionLoadExtractorForm(string selectionSummary, IList<string> comboNames)
        {
            SelectedComboIds = new List<int>();
            InitializeComponent(selectionSummary, comboNames);
        }

        private void InitializeComponent(string selectionSummary, IList<string> comboNames)
        {
            Text = "Connection Load Extractor";
            ClientSize = new Size(420, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            // What the AxisVM selection gave us
            Label lblSel = new Label
            {
                Text = "Selection: " + selectionSummary,
                Location = new Point(12, 12),
                Size = new Size(396, 32)
            };

            // Load combinations - all ticked by default
            Label lblCombos = new Label { Text = "Load combinations:", Location = new Point(12, 52), AutoSize = true };
            lstCombos = new CheckedListBox
            {
                Location = new Point(12, 72),
                Size = new Size(396, 220),
                CheckOnClick = true,
                IntegralHeight = false
            };
            for (int i = 0; i < comboNames.Count; i++) lstCombos.Items.Add(string.Format("{0}: {1}", i + 1, comboNames[i]), true);

            Button btnAll = new Button { Text = "All", Location = new Point(12, 298), Size = new Size(70, 25) };
            btnAll.Click += delegate { SetAllCombos(true); };
            Button btnNone = new Button { Text = "None", Location = new Point(88, 298), Size = new Size(70, 25) };
            btnNone.Click += delegate { SetAllCombos(false); };

            // Analysis type
            Label lblAnalysis = new Label { Text = "Results:", Location = new Point(12, 341), AutoSize = true };
            cmbAnalysis = new ComboBox { Location = new Point(170, 338), Size = new Size(238, 21), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAnalysis.Items.AddRange(new object[] { "Linear static", "Non-linear static" });
            cmbAnalysis.SelectedIndex = 0;

            // Output coordinate system
            Label lblOutput = new Label { Text = "Output loads in:", Location = new Point(12, 373), AutoSize = true };
            cmbOutput = new ComboBox { Location = new Point(170, 370), Size = new Size(238, 21), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbOutput.Items.AddRange(new object[] { "Local (N, Vy, Vz, Tx, My, Mz)", "Global (Fx, Fy, Fz, Mx, My, Mz)" });
            cmbOutput.SelectedIndex = 1;

            // Critical direction = system + component
            Label lblCrit = new Label { Text = "Critical direction:", Location = new Point(12, 405), AutoSize = true };
            cmbCritSystem = new ComboBox { Location = new Point(170, 402), Size = new Size(100, 21), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbCritSystem.Items.AddRange(new object[] { "Local", "Global" });
            cmbCritComponent = new ComboBox { Location = new Point(278, 402), Size = new Size(130, 21), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbCritSystem.SelectedIndexChanged += delegate { FillComponents(); };
            cmbCritSystem.SelectedIndex = 1;  // fills the component list
            cmbCritComponent.SelectedIndex = 1; // Global Fy - the example from the brief

            Label lblNote = new Label
            {
                Text = "Loads are the force each member exerts on the node. Critical = the combination with\n" +
                       "the largest (Max+) and the smallest (Min−) value, per member end and overall.",
                Location = new Point(12, 436),
                Size = new Size(396, 34),
                ForeColor = SystemColors.GrayText
            };

            Button btnOk = new Button { Text = "Extract", Location = new Point(242, 482), Size = new Size(80, 27) };
            btnOk.Click += BtnOk_Click;
            Button btnCancel = new Button { Text = "Cancel", Location = new Point(328, 482), Size = new Size(80, 27), DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { lblSel, lblCombos, lstCombos, btnAll, btnNone, lblAnalysis, cmbAnalysis,
                lblOutput, cmbOutput, lblCrit, cmbCritSystem, cmbCritComponent, lblNote, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        /// <summary>Ticks or clears every combination.</summary>
        private void SetAllCombos(bool value)
        {
            for (int i = 0; i < lstCombos.Items.Count; i++) lstCombos.SetItemChecked(i, value);
        }

        /// <summary>Refills the component list with the names of the chosen system, keeping the index.</summary>
        private void FillComponents()
        {
            int keep = Math.Max(0, cmbCritComponent.SelectedIndex);
            LoadSystem s = cmbCritSystem.SelectedIndex == 0 ? LoadSystem.Local : LoadSystem.Global;
            string[] names = ConnectionLoadMath.Names(s);
            cmbCritComponent.Items.Clear();
            for (int i = 0; i < 6; i++) cmbCritComponent.Items.Add(names[i] + " [" + ConnectionLoadMath.Unit(i) + "]");
            cmbCritComponent.SelectedIndex = keep;
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
            OutputSystem = cmbOutput.SelectedIndex == 0 ? LoadSystem.Local : LoadSystem.Global;
            CriticalSystem = cmbCritSystem.SelectedIndex == 0 ? LoadSystem.Local : LoadSystem.Global;
            CriticalComponent = cmbCritComponent.SelectedIndex;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
