using System;
using System.Drawing;
using System.Windows.Forms;

namespace AxisVM_plugin.UI
{
    /// <summary>
    /// The run report.
    ///
    /// The other addons summarise into a MessageBox, which is right when there are five lines to say.
    /// An import can easily produce a hundred - every substituted material, every merged line, every
    /// mistyped attribute - and a MessageBox truncates and cannot be copied out. This is the same
    /// summary in a window the user can scroll, select and paste into an email.
    /// </summary>
    public class RhinoImportReportForm : Form
    {
        private TextBox txtReport;
        private Button btnCopy;
        private Button btnClose;

        public RhinoImportReportForm(string title, string headline, string body)
        {
            InitializeComponent(title, headline, body);
        }

        private void InitializeComponent(string title, string headline, string body)
        {
            Text = title;
            ClientSize = new Size(660, 460);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MinimumSize = new Size(480, 300);

            Label lblHeadline = new Label
            {
                Text = headline,
                Location = new Point(12, 12),
                Size = new Size(636, 40),
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            txtReport = new TextBox
            {
                Location = new Point(12, 58),
                Size = new Size(636, 350),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                Text = body,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            btnCopy = new Button
            {
                Text = "Copy",
                Location = new Point(478, 418),
                Size = new Size(80, 27),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(headline + Environment.NewLine + Environment.NewLine + body); }
                catch { }
            };

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(568, 418),
                Size = new Size(80, 27),
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            Controls.AddRange(new Control[] { lblHeadline, txtReport, btnCopy, btnClose });

            AcceptButton = btnClose;
            CancelButton = btnClose;
        }
    }
}
