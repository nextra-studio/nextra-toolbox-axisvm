using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Drives one Rhino import: collect settings, read the export, build the model, report what happened.
    ///
    /// The whole build runs inside a single BeginUpdate / EndUpdate pair preceded by SaveUndo, which is
    /// the pattern the other addons use and the reason one Ctrl+Z backs the entire import out. That
    /// matters more here than anywhere else in this repo: an import touches every collection in the
    /// model at once, and a half-finished one is not something a user can unpick by hand.
    /// </summary>
    public static class RhinoImportLogic
    {
        /// <summary>Remembered between runs so re-importing after a Rhino edit is two clicks.</summary>
        private static string lastPath = "";

        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                IAxisVMModel model = app.Models.Item[1];
                if (model == null)
                {
                    MessageBox.Show("No AxisVM model is open.", "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return -1;
                }

                using (RhinoImportForm form = new RhinoImportForm(lastPath))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    lastPath = form.JsonPath;

                    if (form.UpdateExisting)
                    {
                        MessageBox.Show(
                            "Update-by-GUID is not implemented yet.\r\n\r\n" +
                            "This import still records every Rhino id on the AxisVM entities it creates, so a " +
                            "later version can match them up without you having to re-export. For now, choose " +
                            "'Create new'.",
                            "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 1;
                    }

                    return RunImport(app, model, form);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
        }

        private static int RunImport(IAxisVMApplication app, IAxisVMModel model, RhinoImportForm form)
        {
            List<string> notes = new List<string>();

            // ---- read ----------------------------------------------------
            RhinoModel rhino;
            try
            {
                string text = File.ReadAllText(form.JsonPath);
                rhino = RhinoModel.FromJson(JsonValue.Parse(text), notes);
            }
            catch (Exception ex)
            {
                MessageBox.Show("The export could not be read:\r\n\r\n" + ex.Message,
                    "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            foreach (string w in rhino.ExportWarnings)
                notes.Add("Rhino export warning: " + w);

            ImportOptions opt = new ImportOptions
            {
                Units = form.Units,
                MergeToleranceM = form.ToleranceM,
                DryRun = form.DryRun,
                AutoIntersect = form.AutoIntersect,
                UpdateExisting = false,
                SourcePath = form.JsonPath
            };

            AxisModelBuilder builder = new AxisModelBuilder(app, model, rhino, opt, notes);

            // ---- build ---------------------------------------------------
            if (opt.DryRun)
            {
                // Nothing is written, so there is nothing to wrap or undo.
                builder.Run();
            }
            else
            {
                try { model.SaveUndo("Rhino import"); }
                catch { notes.Add("An undo step could not be recorded before the import."); }

                model.BeginUpdate();
                try
                {
                    builder.Run();
                }
                finally
                {
                    model.EndUpdate();
                    try { model.Refresh(); } catch { }
                }
            }

            ShowReport(rhino, opt, builder.Stats, notes);
            return 1;
        }

        // ==================================================================
        // Reporting
        // ==================================================================

        private static void ShowReport(RhinoModel rhino, ImportOptions opt, ImportStats stats, List<string> notes)
        {
            string headline = opt.DryRun
                ? "Dry run - nothing was written to the model."
                : "Import finished.";

            if (stats.AttributeErrors > 0)
                headline += string.Format("  {0} attribute problem(s) were found - see below.", stats.AttributeErrors);

            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Source");
            sb.AppendLine("  file         " + opt.SourcePath);
            if (!string.IsNullOrEmpty(rhino.SourceFile)) sb.AppendLine("  rhino model  " + rhino.SourceFile);
            sb.AppendLine(string.Format("  units        {0} lengths, {1} forces  ->  m, N",
                opt.Units.LengthName, opt.Units.ForceName));
            sb.AppendLine(string.Format("  merge tol.   {0:0.###} mm", opt.MergeToleranceM * 1000.0));
            sb.AppendLine();

            sb.AppendLine("Read from the export");
            sb.AppendLine(string.Format("  points       {0}", rhino.Points.Count));
            sb.AppendLine(string.Format("  lines        {0}", rhino.Lines.Count));
            sb.AppendLine(string.Format("  surfaces     {0}", rhino.Domains.Count));
            sb.AppendLine();

            sb.AppendLine(opt.DryRun ? "Would create" : "Created");
            sb.AppendLine(string.Format("  nodes        {0}{1}", stats.NodesCreated,
                stats.NodesShared > 0 ? string.Format("   ({0} coincident vertices reused an existing node)", stats.NodesShared) : ""));
            sb.AppendLine(string.Format("  lines        {0}{1}", stats.LinesCreated,
                stats.LinesMerged > 0 ? string.Format("   ({0} Rhino line(s) merged onto a line already there)", stats.LinesMerged) : ""));
            sb.AppendLine(string.Format("  domains      {0}", stats.DomainsCreated));
            sb.AppendLine(string.Format("  load panels  {0}", stats.PanelsCreated));
            sb.AppendLine(string.Format("  supports     {0}", stats.SupportsCreated));
            sb.AppendLine(string.Format("  links        {0}", stats.LinksCreated));
            sb.AppendLine(string.Format("  edge hinges  {0}", stats.HingesCreated));
            sb.AppendLine(string.Format("  loads        {0}", stats.LoadsCreated));
            sb.AppendLine(string.Format("  parts        {0}", stats.PartsCreated));
            if (opt.AutoIntersect && !opt.DryRun)
                sb.AppendLine(string.Format("  members      {0} reassembled after auto-intersection", stats.MembersAssembled));

            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format("Notes ({0})", notes.Count));
                foreach (string note in notes) sb.AppendLine("  - " + note);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Nothing needed a substitution or a warning.");
            }

            if (!opt.DryRun)
            {
                sb.AppendLine();
                sb.AppendLine("One Ctrl+Z in AxisVM undoes this whole import.");
            }

            using (RhinoImportReportForm report = new RhinoImportReportForm("Rhino Import", headline, sb.ToString()))
                report.ShowDialog();
        }
    }
}
