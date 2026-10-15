using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// Run summary for "N. One-Way Load Panel": a headline, then every note in a scrollable box
    /// (a MessageBox cuts long lists off and cannot be copied from).
    /// </summary>
    public class OneWayPanelReportForm : Form
    {
        public OneWayPanelReportForm(string headline, List<string> notes, bool allGood)
        {
            Text = "One-Way Load Panel";
            ClientSize = new Size(560, notes.Count > 0 ? 380 : 140);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            PictureBox icon = new PictureBox
            {
                Image = (allGood ? SystemIcons.Information : SystemIcons.Warning).ToBitmap(),
                Location = new Point(14, 14),
                Size = new Size(32, 32)
            };
            Label head = new Label
            {
                Text = headline,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                Location = new Point(56, 14),
                Size = new Size(490, 48)
            };
            Controls.Add(icon);
            Controls.Add(head);

            if (notes.Count > 0)
            {
                TextBox box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Location = new Point(14, 70),
                    Size = new Size(532, 254),
                    Text = string.Join("\r\n", notes),
                    BackColor = SystemColors.Window
                };
                Controls.Add(box);
            }

            Button ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Size = new Size(96, 30),
                Location = new Point(450, ClientSize.Height - 44)
            };
            Controls.Add(ok);
            ActiveControl = ok; // otherwise the notes box takes focus with all its text selected
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}
