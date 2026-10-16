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
        private static string lastLinkPath = "";
        private static bool lastOnLink;

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

                using (RhinoImportForm form = new RhinoImportForm(lastPath, exportCatalog, findLinked, lastLinkPath, lastOnLink))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    lastOnLink = form.Action != RhinoImportForm.FormAction.Import;
                    if (lastOnLink) lastLinkPath = form.LinkPath;
                    else lastPath = form.JsonPath;

                    switch (form.Action)
                    {
                        case RhinoImportForm.FormAction.LoadFromRhino: return LoadFromRhino(app, model, form);
                        case RhinoImportForm.FormAction.SendToRhino: return SendToRhino(model, form.LinkPath);
                        default: return RunImport(app, model, form, form.JsonPath, form.ReplacePrevious, form.DryRun, null);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Rhino Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
        }

        // ==================================================================
        // Live link
        // ==================================================================

        /// <summary>
        /// "Load from Rhino": an update import of the link file's Rhino section, after which the axisvm
        /// section records that AxisVM is in line with that send (and what it built).
        /// </summary>
        private static int LoadFromRhino(IAxisVMApplication app, IAxisVMModel model, RhinoImportForm form)
        {
            string path = form.LinkPath;
            JsonValue root;
            try { root = LinkFile.Read(path); }
            catch (Exception ex)
            {
                MessageBox.Show("The link file could not be read:\r\n\r\n" + ex.Message, "Live link", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            LinkFile.Status status = LinkFile.ReadStatus(root);
            if (status.AxisVmPending &&
                MessageBox.Show(string.Format(
                    "AxisVM sent {0} change(s) at {1} that Rhino has not loaded yet.\r\n\r\n" +
                    "Loading Rhino's model now replaces them with Rhino's version - they are lost unless Rhino loads them first " +
                    "(AxLink > Load from AxisVM).\r\n\r\nLoad from Rhino anyway?", status.AxisVmChanges, LinkFile.Local(status.AxisVmSent)),
                    "Live link", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return 1;

            int rc = RunImport(app, model, form, path, true, false, LinkFile.RhinoKnownGuids(root));
            if (rc != 1) return rc;

            try { RhinoLinkSender.MarkLoaded(model, path, LinkFile.Get(root[LinkFile.RhinoSection], "sent")); }
            catch (Exception ex)
            {
                MessageBox.Show("The model was updated, but the link file could not be marked as loaded:\r\n\r\n" + ex.Message,
                    "Live link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 1;
        }

        private static int SendToRhino(IAxisVMModel model, string path)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    LinkFile.Status status = LinkFile.ReadStatus(LinkFile.Read(path));
                    if (status.RhinoNewer && status.AxisVmBasedOnRhino.Length > 0 &&
                        MessageBox.Show("Rhino has sent a newer model that this AxisVM has not loaded yet. Rhino will apply only what you " +
                                        "changed here on top of it.\r\n\r\nSend anyway?", "Live link",
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
                sb.AppendLine(string.Format("  new          {0}   (drawn in AxisVM - Rhino creates them)", r.Adopted));
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

                string headline = r.Changed + r.Deleted > 0
                    ? string.Format("{0} change(s) are ready for Rhino.", r.Changed + r.Deleted)
                    : "Nothing was changed in AxisVM since the last load from Rhino.";
                using (RhinoImportReportForm report = new RhinoImportReportForm("Live link", headline, sb.ToString()))
                    report.ShowDialog();
                return 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sending to Rhino failed:\r\n\r\n" + ex.Message, "Live link", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
        }

        // ==================================================================
        // Import
        // ==================================================================

        private static int RunImport(IAxisVMApplication app, IAxisVMModel model, RhinoImportForm form,
                                     string jsonPath, bool replacePrevious, bool dryRun, HashSet<string> rhinoKnownGuids)
        {
            List<string> notes = new List<string>();

            // ---- read ----------------------------------------------------
            RhinoModel rhino;
            try
            {
                string text = File.ReadAllText(jsonPath);
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
                DryRun = dryRun,
                AutoIntersect = form.AutoIntersect,
                ReplacePrevious = replacePrevious,
                RhinoKnownGuids = rhinoKnownGuids,
                SourcePath = jsonPath
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
                try { model.SaveUndo(opt.ReplacePrevious ? "Rhino update" : "Rhino import"); }
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

            sb.AppendLine("Read from the export");
            sb.AppendLine(string.Format("  points       {0}", rhino.Points.Count));
            sb.AppendLine(string.Format("  lines        {0}", rhino.Lines.Count));
            sb.AppendLine(string.Format("  surfaces     {0}", rhino.Domains.Count));
            sb.AppendLine();

            if (stats.Replaced != null)
            {
                sb.AppendLine(opt.DryRun ? "Would replace (from the earlier import of this file)" : "Replaced (from the earlier import of this file)");
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
                    ? "One Ctrl+Z in AxisVM undoes this whole update (the earlier import comes back)."
                    : "One Ctrl+Z in AxisVM undoes this whole import.");
            }

            using (RhinoImportReportForm report = new RhinoImportReportForm("Rhino Import", headline, sb.ToString()))
                report.ShowDialog();
        }
    }
}
