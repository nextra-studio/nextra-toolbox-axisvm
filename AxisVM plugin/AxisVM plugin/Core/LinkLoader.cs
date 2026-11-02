using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What one "Load from Rhino" did.</summary>
    public class LinkLoadResult
    {
        public RhinoModel Rhino;
        public ImportOptions Options;
        public ImportStats Stats;
        /// <summary>What "only changes" decided (null when it was off).</summary>
        public LinkDiffPlan Plan;
    }

    /// <summary>
    /// "Load from Rhino" from a live link file: read Rhino's section, decide what to rebuild (all of it, or with "only
    /// changes" what LinkDiff finds), build it, and record in the axisvm section what AxisVM is now in line with -
    /// including every object's signature for the next "only changes" load. The add-on and the live checks both run this.
    /// The caller records the undo step; the builder holds BeginUpdate itself.
    /// </summary>
    public static class LinkLoader
    {
        /// <param name="replace">Update what an earlier load of this file built (false: build a second copy).</param>
        /// <param name="onlyChanges">With <paramref name="replace"/>: rebuild only what changed (link files only - a plain
        /// AxExport file is Rhino's own export and gets no record written into it, so it is always replaced whole).</param>
        public static LinkLoadResult Load(IAxisVMApplication app, IAxisVMModel model, string linkPath, ImportOptions opt, bool replace, bool onlyChanges,
                                          List<string> notes, Action<string> log = null, string stopAfter = null)
        {
            JsonValue root = LinkFile.Read(linkPath);
            RhinoModel rhino = RhinoModel.FromJson(root, notes);
            foreach (string w in rhino.ExportWarnings) notes.Add("Rhino export warning: " + w);
            bool isLink = LinkFile.IsLink(root);

            opt.SourcePath = linkPath;
            opt.ReplacePrevious = replace;
            opt.RhinoKnownGuids = LinkFile.RhinoKnownGuids(root);
            opt.AdoptedPanels = RhinoLinkSender.AdoptedPanels(root);

            Dictionary<string, string> sigs = LinkSignature.All(LinkFile.RhinoPart(root), opt.Units.LengthToM);
            string settings = LinkSignature.Settings(opt.Units, opt.MergeToleranceM);

            Dictionary<string, string> previous;
            string previousSettings;
            Dictionary<int, string> panelGuids;
            HashSet<string> built;
            RhinoLinkSender.ReadRecord(root, out previous, out previousSettings, out panelGuids, out built);

            LinkDiffPlan plan = null;
            RhinoModel build = rhino;
            if (replace && onlyChanges && !isLink)
                plan = new LinkDiffPlan { Everything = true, Reason = "a plain AxExport file keeps no record of the last load - use AxLink for updates of only what changed" };
            else if (replace && onlyChanges)
            {
                plan = AxisModelBuilder.PlanDiff(model, rhino, new LinkDiffInput
                {
                    Key = AxisModelBuilder.LinkKey(linkPath), Signatures = sigs, Previous = previous, Settings = settings,
                    PreviousSettings = previousSettings, RhinoKnows = opt.RhinoKnownGuids, AdoptedPanels = opt.AdoptedPanels,
                    PanelGuids = panelGuids, PreviousBuilt = built, Units = opt.Units, ToleranceM = opt.MergeToleranceM
                });
                notes.AddRange(plan.Notes);
                if (!plan.Everything)
                {
                    opt.Diff = plan;
                    build = plan.Subset(rhino);
                }
            }

            // Run even when nothing changed: the library (load cases, combinations) is synchronised on every load.
            AxisModelBuilder builder = new AxisModelBuilder(app, model, build, opt, notes) { Log = log, StopAfter = stopAfter };
            builder.Run();

            if (!opt.DryRun && isLink)
            {
                Dictionary<int, string> panels = new Dictionary<int, string>(builder.Stats.PanelGuids);
                if (plan != null && !plan.Everything) foreach (KeyValuePair<int, string> kv in plan.KeptPanels) panels[kv.Key] = kv.Value;
                RhinoLinkSender.MarkLoaded(model, linkPath, LinkFile.Get(root[LinkFile.RhinoSection], "sent"), sigs, settings, panels,
                    plan != null && !plan.Everything ? plan.Build : null);
            }
            return new LinkLoadResult { Rhino = rhino, Options = opt, Stats = builder.Stats, Plan = plan };
        }
    }
}
