using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AxisBridge.UI
{
    /// <summary>
    /// Stiffness choice for AxEdgeHinge. Values are per metre of edge, in the edge's local system
    /// (x along the edge): kN/m/m for translations, kNm/rad/m for rotations.
    /// </summary>
    public class EdgeHingeForm : Form
    {
        public string Stiffness { get; private set; }

        private readonly RadioButton rAllRot, rAboutEdge, rSemi, rCustom;
        private readonly CueTextBox semi, custom;

        private const string Rigid = "1e10";

        public EdgeHingeForm(int edgeCount, string domainSummary)
        {
            Theme.Dialog(this, "AxEdgeHinge - edge hinge", new Size(640, 470));
            MinimumSize = new Size(560, 430);

            var page = new FieldPage();
            page.Section("What is released along the edge");

            rAboutEdge = Radio("Line hinge - free rotation about the edge only (typical slab-on-wall hinge)", true);
            rAllRot = Radio("All rotations free, translations fixed", false);
            rSemi = Radio("Semi-rigid rotation about the edge:", false);
            rCustom = Radio("Custom - 6 stiffnesses", false);

            semi = new CueTextBox { Width = 140, Cue = "kNm/rad/m e.g. 5000", Enabled = false };
            custom = new CueTextBox { Width = 320, Cue = "kx, ky, kz, kxx, kyy, kzz", Text = "1e10,1e10,1e10,0,1e10,1e10", Enabled = false };

            // All four radio buttons share ONE parent, otherwise WinForms does not make them exclusive.
            var group = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.Controls.Add(rAboutEdge, 0, 0);
            group.SetColumnSpan(rAboutEdge, 2);
            group.Controls.Add(rAllRot, 0, 1);
            group.SetColumnSpan(rAllRot, 2);
            group.Controls.Add(rSemi, 0, 2);
            group.Controls.Add(semi, 1, 2);
            group.Controls.Add(rCustom, 0, 3);
            group.Controls.Add(custom, 1, 3);
            page.AddWide(group);
            page.AddWide(Theme.Hint("Edge local system: x along the edge. 1e10 = rigid, 0 = free. Translations kN/m/m, rotations kNm/rad/m."));

            page.Section("Summary");
            page.AddWide(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Text = string.Format("{0} hinge line(s) will be created on layer AxLayer::AxLine::EdgeHinge.\r\nReleased domains: {1}.", edgeCount, domainSummary)
            });

            foreach (RadioButton r in new[] { rAboutEdge, rAllRot, rSemi, rCustom })
                r.CheckedChanged += (s, e) => { semi.Enabled = rSemi.Checked; custom.Enabled = rCustom.Checked; };

            Button ok, cancel;
            Panel buttons = Theme.Buttons(this, "Create", out ok, out cancel);
            ok.Click += Ok_Click;

            Controls.Add(page);
            Controls.Add(Theme.Header("Edge hinge", "Release domains along the selected edges"));
            Controls.Add(Theme.Separator());
            Controls.Add(buttons);
        }

        private static RadioButton Radio(string text, bool on)
        {
            return new RadioButton { Text = text, AutoSize = true, Checked = on, Margin = new Padding(0, 4, 8, 4) };
        }

        private void Ok_Click(object sender, EventArgs e)
        {
            if (rAboutEdge.Checked) Stiffness = Rigid + "," + Rigid + "," + Rigid + ",0," + Rigid + "," + Rigid;
            else if (rAllRot.Checked) Stiffness = Rigid + "," + Rigid + "," + Rigid + ",0,0,0";
            else if (rSemi.Checked)
            {
                double k;
                if (!double.TryParse(semi.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out k) || k < 0)
                {
                    MessageBox.Show(this, "Enter the rotational stiffness as a number (kNm/rad per metre).", Text);
                    DialogResult = DialogResult.None;
                    return;
                }
                Stiffness = Rigid + "," + Rigid + "," + Rigid + "," + k.ToString("R", CultureInfo.InvariantCulture) + "," + Rigid + "," + Rigid;
            }
            else
            {
                string t = custom.Text.Trim();
                if (t.Split(',').Length != 6)
                {
                    MessageBox.Show(this, "Custom stiffness needs 6 comma-separated numbers.", Text);
                    DialogResult = DialogResult.None;
                    return;
                }
                Stiffness = t;
            }
        }
    }
}
