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
    /// Drives the Rhino dialog: "Load from Rhino" (build or update this model from an AxExport or AxLink file) and
    /// "Send to Rhino" (write what changed in AxisVM into the link file), and reports what happened.
    ///
    /// A load is preceded by one SaveUndo, the reason one Ctrl+Z backs the whole load out. That matters more here than
    /// anywhere else in this repo: a load touches every collection in the model at once, and a half-finished one is not
    /// something a user can unpick by hand. The builder holds BeginUpdate / EndUpdate itself (see AxisModelBuilder.Run).
    /// </summary>
    public static class RhinoImportLogic
    {
        /// <summary>Remembered between runs so loading again after a Rhino edit is two clicks.</summary>
        private static string lastPath = "";
        private static bool lastSend;

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

                Func<string> exportCatalog = () =>
                {
                    CatalogExport.Result r = CatalogExport.Export(app, CatalogExport.DefaultFolder());
                    return string.Format("{0} materials and {1} cross-sections in {2} tables written to\r\n{3}", r.Materials, r.Sections, r.Tables, r.Folder);
                };
                Func<string, LinkedItems> findLinked = path => AxisModelBuilder.FindLinked(model, path);

                using (RhinoImportForm form = new RhinoImportForm(lastPath, exportCatalog, findLinked, lastSend))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;
                    lastPath = form.FilePath;
                    lastSend = form.Action == RhinoImportForm.FormAction.SendToRhino;
                    return lastSend ? SendToRhino(model, form.FilePath) : Load(app, model, form);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
        }

        // ==================================================================
        // From Rhino
        // ==================================================================

        private static int Load(IAxisVMApplication app, IAxisVMModel model, RhinoImportForm form)
        {
            string path = form.FilePath;
            JsonValue root;
            try { root = LinkFile.Read(path); }
            catch (Exception ex)
            {
                MessageBox.Show("The file could not be read:\r\n\r\n" + ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            LinkFile.Status status = LinkFile.ReadStatus(root);
            if (form.ReplacePrevious && !form.DryRun && status.AxisVmPending &&
                MessageBox.Show(string.Format(
                    "AxisVM sent {0} change(s) at {1} that Rhino has not loaded yet.\r\n\r\n" +
                    (form.OnlyChanges
                        ? "Elements Rhino did not change keep their AxisVM version; elements Rhino changed too are rebuilt from Rhino's version. "
                        : "Loading replaces them with Rhino's version. ") +
                    "To keep every AxisVM change, let Rhino load them first (AxLink > Load from AxisVM).\r\n\r\nLoad from Rhino anyway?",
                    status.AxisVmChanges, LinkFile.Local(status.AxisVmSent)),
                    "Rhino Import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return 1;

            ImportOptions opt = new ImportOptions
            {
                Units = form.Units,
                MergeToleranceM = form.ToleranceM,
                DryRun = form.DryRun,
                AutoIntersect = form.AutoIntersect,
                RemeshRebuilt = form.RemeshRebuilt
            };
            List<string> notes = new List<string>();
            LinkLoadResult result;
            try
            {
                if (!opt.DryRun)
                {
                    try { model.SaveUndo(form.ReplacePrevious ? "Rhino update" : "Rhino import"); }
                    catch { notes.Add("An undo step could not be recorded before the load."); }
                }
                try { result = LinkLoader.Load(app, model, path, opt, form.ReplacePrevious, form.OnlyChanges, notes); }
                finally
                {
                    try { model.Refresh(); } catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Loading from Rhino failed:\r\n\r\n" + ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            ShowReport(result, notes);
            return 1;
        }

        // ==================================================================
        // To Rhino
        // ==================================================================

        private static int SendToRhino(IAxisVMModel model, string path)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    LinkFile.Status status = LinkFile.ReadStatus(LinkFile.Read(path));
                    if (status.RhinoNewer && status.AxisVmBasedOnRhino.Length > 0 &&
                        MessageBox.Show("Rhino has sent a newer model that this AxisVM has not loaded yet. Rhino will apply only what you " +
                                        "changed here on top of it.\r\n\r\nSend anyway?", "Rhino Import",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return 1;
                }

                SendResult r = RhinoLinkSender.Send(model, path);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Link file");
                sb.AppendLine("  " + path);
                sb.AppendLine();
                sb.AppendLine("Sent to Rhino");
                sb.AppendLine(string.Format("  changed      {0}", r.Changed));
                sb.AppendLine(string.Format("  unchanged    {0}", r.Unchanged));
                sb.AppendLine(string.Format("  new          {0}   (drawn in AxisVM - Rhino creates them, with ids both sides keep)", r.Adopted));
                sb.AppendLine(string.Format("  deleted      {0}", r.Deleted));
                sb.AppendLine();
                sb.AppendLine("Library (always sent in full; Rhino adds what it lacks)");
                sb.AppendLine(string.Format("  {0} material(s), {1} section(s), {2} load case(s), {3} combination(s)   ({4} not in Rhino yet)",
                    r.Library[0], r.Library[1], r.Library[2], r.Library[3], r.LibraryNew));
                sb.AppendLine();
                sb.AppendLine("Notes");
                foreach (string n in r.Notes) sb.AppendLine("  - " + n);
                sb.AppendLine();
                sb.AppendLine("In Rhino: AxLink > Load from AxisVM.");

                string headline = r.Changed + r.Deleted + r.Adopted > 0
                    ? string.Format("{0} change(s) are ready for Rhino.", r.Changed + r.Deleted + r.Adopted)
                    : "Nothing was changed in AxisVM since the last load from Rhino.";
                using (RhinoImportReportForm report = new RhinoImportReportForm("Send to Rhino", headline, sb.ToString()))
                    report.ShowDialog();
                return 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sending to Rhino failed:\r\n\r\n" + ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
        }

        // ==================================================================
        // Reporting
        // ==================================================================

        private static void ShowReport(LinkLoadResult result, List<string> notes)
        {
            RhinoModel rhino = result.Rhino;
            ImportOptions opt = result.Options;
            ImportStats stats = result.Stats;
            LinkDiffPlan plan = result.Plan;
            bool partial = plan != null && !plan.Everything;

            string headline = opt.DryRun
                ? "Dry run - nothing was written to the model."
                : partial ? (plan.Build.Count + plan.Deleted == 0 ? "Nothing changed in Rhino - the model was left as it is." : "Updated what changed in Rhino.")
                : stats.Replaced != null ? "Update finished." : "Import finished.";

            if (stats.AttributeErrors > 0)
                headline += string.Format("  {0} attribute problem(s) were found - see below.", stats.AttributeErrors);

            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Source");
            sb.AppendLine("  file         " + opt.SourcePath);
            if (!string.IsNullOrEmpty(rhino.SourceFile)) sb.AppendLine("  rhino model  " + rhino.SourceFile);
            sb.AppendLine(string.Format("  units        {0} lengths, {1} loads  ->  AxisVM m, kN",
                opt.Units.LengthName, opt.Units.ForceName));
            if (rhino.Library.LoadCases.Count + rhino.Library.Sections.Count + rhino.Library.Materials.Count > 0)
                sb.AppendLine(string.Format("  library      {0} load case(s), {1} section(s), {2} material(s)",
                    rhino.Library.LoadCases.Count, rhino.Library.Sections.Count, rhino.Library.Materials.Count));
            sb.AppendLine(string.Format("  merge tol.   {0:0.###} mm", opt.MergeToleranceM * 1000.0));
            sb.AppendLine();

            sb.AppendLine("Read from the file");
            sb.AppendLine(string.Format("  points       {0}", rhino.Points.Count));
            sb.AppendLine(string.Format("  lines        {0}", rhino.Lines.Count));
            sb.AppendLine(string.Format("  surfaces     {0}", rhino.Domains.Count));
            sb.AppendLine();

            if (plan != null)
            {
                sb.AppendLine("Only what changed");
                sb.AppendLine("  " + plan.Summary());
                if (partial && plan.Trace.Length > 0) sb.AppendLine("  rebuilt set: " + plan.Trace);
                sb.AppendLine();
            }

            if (stats.Replaced != null)
            {
                sb.AppendLine(opt.DryRun ? "Would replace (from the earlier load of this file)" : "Replaced (from the earlier load of this file)");
                sb.AppendLine("  " + stats.Replaced);
                sb.AppendLine();
            }

            sb.AppendLine(opt.DryRun ? "Would create" : "Created");
            sb.AppendLine(string.Format("  nodes        {0}{1}", stats.NodesCreated,
                stats.NodesShared > 0 ? string.Format("   ({0} coincident vertices reused an existing node)", stats.NodesShared) : ""));
            sb.AppendLine(string.Format("  lines        {0}{1}{2}", stats.LinesCreated,
                stats.LinesMerged > 0 ? string.Format("   ({0} Rhino curve(s) shared an existing line)", stats.LinesMerged) : "",
                stats.LinesSplit > 0 ? string.Format("   ({0} extra from intersections)", stats.LinesSplit) : ""));
            sb.AppendLine(string.Format("  domains      {0}{1}", stats.DomainsCreated,
                stats.HolesCreated > 0 ? string.Format("   ({0} opening(s))", stats.HolesCreated) : ""));
            sb.AppendLine(string.Format("  load panels  {0}", stats.PanelsCreated));
            sb.AppendLine(string.Format("  supports     {0} nodal, {1} line", stats.SupportsCreated, stats.LineSupportsCreated));
            sb.AppendLine(string.Format("  springs      {0}", stats.SpringsCreated));
            sb.AppendLine(string.Format("  links        {0} node-node, {1} rigid bodies", stats.LinksCreated, stats.RigidBodiesCreated));
            sb.AppendLine(string.Format("  edge hinges  {0}", stats.HingesCreated));
            if (stats.DiaphragmsCreated > 0) sb.AppendLine(string.Format("  diaphragms   {0}", stats.DiaphragmsCreated));
            sb.AppendLine(string.Format("  loads        {0}", stats.LoadsCreated));
            sb.AppendLine(string.Format("  parts        {0}", stats.PartsCreated));
            if (stats.MembersAssembled > 0)
                sb.AppendLine(string.Format("  members      {0} assembled from split segments", stats.MembersAssembled));

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
                sb.AppendLine(stats.Replaced != null
                    ? "One Ctrl+Z in AxisVM undoes this whole update."
                    : "One Ctrl+Z in AxisVM undoes this whole import.");
            }

            using (RhinoImportReportForm report = new RhinoImportReportForm("Rhino Import", headline, sb.ToString()))
                report.ShowDialog();
        }
    }
}
