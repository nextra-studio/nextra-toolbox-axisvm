using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AxisBridge.Export;
using AxisBridge.Link;
using Rhino;

namespace AxisBridge.UI
{
    /// <summary>
    /// AxLink window: the live link file shared with the AxisVM "Rhino Import" add-on, its status, and the two
    /// actions. It stays open between actions so a round trip is Send / (AxisVM) / Load without reopening.
    /// </summary>
    public class LinkForm : Form
    {
        private readonly RhinoDoc doc;
        private readonly TextBox path;
        private readonly Label status;

        public LinkForm(RhinoDoc doc)
        {
            this.doc = doc;
            Theme.Dialog(this, "AxLink", new Size(600, 440));
            MinimumSize = new Size(520, 440);

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(14, 12, 14, 8),
                BackColor = Theme.Background
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            body.Controls.Add(new Label { Text = "Live link file (shared with AxisVM: Rhino Import add-on > Live link)", Font = Theme.Bold, AutoSize = true, Margin = new Padding(3, 0, 3, 4) }, 0, 0);
            body.SetColumnSpan(body.GetControlFromPosition(0, 0), 2);

            path = new TextBox { Dock = DockStyle.Fill, Text = AxLinkSync.LinkPath(doc) };
            path.Leave += (s, e) => ShowStatus();
            var browse = new Button { Text = "Browse...", Width = 95, Height = 26 };
            browse.Click += Browse;
            body.Controls.Add(path, 0, 1);
            body.Controls.Add(browse, 1, 1);

            status = new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.HintColor, Margin = new Padding(3, 8, 3, 10) };
            body.Controls.Add(status, 0, 2);
            body.SetColumnSpan(status, 2);

            var send = new Button { Text = "Send to AxisVM", Width = 160, Height = 32, Font = Theme.Bold };
            Theme.StyleButton(send, true);
            send.Click += (s, e) => Run(SendNow);
            body.Controls.Add(Theme.Hint("Writes this model (every object with Ax_* attributes) into the link file. Then in AxisVM: Rhino Import > Live link > Load from Rhino."), 0, 3);
            body.Controls.Add(send, 1, 3);

            var load = new Button { Text = "Load from AxisVM", Width = 160, Height = 32 };
            load.Click += (s, e) => Run(LoadNow);
            body.Controls.Add(Theme.Hint("Applies what was changed in AxisVM and sent with 'Send to Rhino': moved nodes, sections, materials, slab type and thickness, new members and slabs, deletions. One Undo reverts it."), 0, 4);
            body.Controls.Add(load, 1, 4);

            var close = new Button { Text = "Close", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 10, 12, 10), BackColor = Color.White };
            bar.Controls.Add(close);
            bar.Layout += (s, e) => close.Location = new Point(bar.ClientSize.Width - bar.Padding.Right - close.Width, bar.Padding.Top);
            CancelButton = close;

            Controls.Add(body);
            Controls.Add(Theme.Separator());
            Controls.Add(bar);
            Controls.Add(Theme.Header("AxLink", "Live link with AxisVM: one shared file, Send and Load on both sides"));

            ShowStatus();
        }

        private string LinkPath { get { return path.Text.Trim(); } }

        private void Browse(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Pick or create the live link file";
                dlg.Filter = "AxisBridge link (*.axlink.json)|*.axlink.json|JSON (*.json)|*.json|All files (*.*)|*.*";
                dlg.OverwritePrompt = false;
                string start = LinkPath.Length > 0 ? LinkPath : (!string.IsNullOrEmpty(doc.Path) ? Path.Combine(Path.GetDirectoryName(doc.Path), Path.GetFileNameWithoutExtension(doc.Path) + ".axlink.json") : "");
                try
                {
                    if (start.Length > 0)
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(start);
                        dlg.FileName = Path.GetFileName(start);
                    }
                }
                catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path.Text = dlg.FileName;
                AxLinkSync.SetLinkPath(doc, dlg.FileName);
                ShowStatus();
            }
        }

        private void ShowStatus()
        {
            status.ForeColor = Theme.HintColor;
            if (LinkPath.Length == 0) { status.Text = "Pick a link file, or a new file name to start one with 'Send to AxisVM'."; return; }
            if (!File.Exists(LinkPath)) { status.Text = "This file does not exist yet. 'Send to AxisVM' creates it."; return; }
            try
            {
                AxLinkSync.LinkStatus s = AxLinkSync.Status(doc, LinkPath);
                var sb = new StringBuilder();
                sb.AppendLine("Rhino sent:     " + (s.RhinoSent.Length > 0 ? AxLinkSync.Local(s.RhinoSent) : "nothing yet"));
                sb.AppendLine("AxisVM loaded:  " + (s.AxisVmBasedOnRhino.Length > 0 ? "Rhino's send of " + AxLinkSync.Local(s.AxisVmBasedOnRhino) : "not yet"));
                sb.AppendLine("AxisVM sent:    " + (s.AxisVmSent.Length > 0 ? string.Format("{0} ({1} change(s))", AxLinkSync.Local(s.AxisVmSent), s.AxisVmChanges) : "nothing yet"));
                if (s.AxisVmNew) sb.AppendLine("-> AxisVM has sent changes: Load from AxisVM.");
                if (s.RhinoPending) sb.AppendLine("-> AxisVM has not loaded Rhino's last send yet.");
                status.Text = sb.ToString();
                if (s.AxisVmNew) status.ForeColor = Theme.Accent;
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = "This file could not be read: " + ex.Message;
            }
        }

        private void Run(Action action)
        {
            if (LinkPath.Length == 0) { Browse(this, EventArgs.Empty); if (LinkPath.Length == 0) return; }
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            try { action(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "AxLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = old;
                ShowStatus();
            }
        }

        private void SendNow()
        {
            AxLinkSync.LinkStatus s = AxLinkSync.Status(doc, LinkPath);
            if (s.AxisVmNew)
            {
                DialogResult r = MessageBox.Show(this,
                    "AxisVM sent " + s.AxisVmChanges + " change(s) that this model has not loaded.\r\n\r\n" +
                    "Yes = load them first, then send (recommended).\r\nNo = send without them: AxisVM's next 'Load from Rhino' discards them.",
                    "AxLink", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) { LoadNow(); return; }   // a load re-sends the merged model
            }

            ExportResult res = AxLinkSync.Send(doc, LinkPath);
            string text = string.Format("Sent to the link file: {0} point(s), {1} curve(s), {2} surface face(s).{3}\r\n\r\nIn AxisVM: Rhino Import > Live link > Load from Rhino.",
                res.PointCount, res.LineCount, res.DomainCount, res.Warnings.Count > 0 ? "\r\n\r\nWarnings:\r\n  - " + string.Join("\r\n  - ", res.Warnings) : "");
            RhinoApp.WriteLine("AxLink: " + text.Replace("\r\n", " "));
            MessageBox.Show(this, text, "AxLink", MessageBoxButtons.OK, res.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void LoadNow()
        {
            AxLinkSync.LoadResult r = AxLinkSync.Load(doc, LinkPath, n => MessageBox.Show(this,
                n + " object(s) were deleted in AxisVM. Delete them here too?\r\n\r\n(No keeps and selects them; AxisVM gets them back with its next 'Load from Rhino'.)",
                "AxLink", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

            var sb = new StringBuilder();
            if (r.NothingNew) sb.AppendLine("Nothing to load.");
            else
            {
                sb.AppendLine(string.Format("Moved / reshaped:  {0}", r.Moved));
                sb.AppendLine(string.Format("Properties set:    {0}", r.Updated));
                sb.AppendLine(string.Format("Created:           {0}   (drawn in AxisVM, layers AxisVM::*)", r.Created));
                sb.AppendLine(string.Format("Deleted:           {0}", r.Deleted));
                sb.AppendLine(string.Format("Unchanged:         {0}", r.Unchanged));
                if (r.Library.Total > 0)
                    sb.AppendLine(string.Format("Library added:     {0} material(s), {1} section(s), {2} load case(s), {3} combination(s)",
                        r.Library.Materials, r.Library.Sections, r.Library.LoadCases, r.Library.Combinations));
                if (r.Resent != null) sb.AppendLine("\r\nThe merged model was sent back to the link file, so AxisVM keeps these changes on its next 'Load from Rhino'.");
            }
            foreach (string n in r.Notes) sb.AppendLine("\r\n- " + n);
            RhinoApp.WriteLine("AxLink load: " + sb.ToString().Replace("\r\n", " "));
            MessageBox.Show(this, sb.ToString(), "AxLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
