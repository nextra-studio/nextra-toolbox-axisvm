using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Addon "N. One-Way Load Panel": one command for both ways of making one-way load panels.
    ///
    ///   - load panels selected: each panel is rebuilt as one-way panel(s) on the members under its
    ///     outline, and the loads that were on it are carried over;
    ///   - only lines selected: every closed boundary in the selection gets new one-way panel(s),
    ///     optionally with an area load.
    ///
    /// The geometry (support / edge-beam classification, edge-strip split) is the shared
    /// <see cref="OneWayPanelBuilder"/>; outlines are resolved onto existing members by
    /// <see cref="PanelBoundaryResolver"/> and loads are carried by <see cref="PanelLoadCarrier"/>.
    /// </summary>
    public class OneWayLoadPanelLogic
    {
        private sealed class RunStats
        {
            public int Targets, Done, PanelsCreated, LoadsApplied, LoadsLost;
            public readonly List<string> Notes = new List<string>();
        }

        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                // A window selection picks a panel together with its edge lines, so selected panels win.
                List<int> panelUids = SelectedPanelUids(model);
                List<ResolvedLine> lines = panelUids.Count > 0 ? new List<ResolvedLine>() : SelectedLines(model);

                if (panelUids.Count == 0 && lines.Count == 0)
                {
                    MessageBox.Show("Select the load panels to convert to one-way,\nor the boundary lines (beams) of the new panels.",
                        "One-Way Load Panel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                List<List<ResolvedLine>> groups = lines.Count > 0
                    ? OneWayPanelBuilder.GroupByConnectivity(lines) : new List<List<ResolvedLine>>();

                bool convert = panelUids.Count > 0;
                string selection = convert
                    ? string.Format("{0} load panel(s) selected - they are rebuilt as one-way panels and keep their loads.", panelUids.Count)
                    : string.Format("{0} line(s) selected, forming {1} boundary(ies) - new one-way panels are created.", lines.Count, groups.Count);

                using (OneWayLoadPanelForm form = new OneWayLoadPanelForm(convert, selection, LoadCaseNames(model)))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    OneWayPanelOptions opt = form.Options;
                    RunStats stats = new RunStats();

                    model.BeginUpdate();
                    try
                    {
                        if (convert) ConvertPanels(model, panelUids, opt, stats);
                        else CreateFromLines(model, groups, opt, form.ApplyLoad, form.LoadCaseId, form.LoadValue, stats);
                    }
                    finally
                    {
                        model.EndUpdate();
                        try { model.Refresh(); } catch { }
                    }

                    using (OneWayPanelReportForm report = new OneWayPanelReportForm(Headline(convert, stats), stats.Notes,
                        stats.Done == stats.Targets && stats.LoadsLost == 0))
                    {
                        report.ShowDialog();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "One-Way Load Panel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        // ==================================================================
        // Lines -> new panels
        // ==================================================================

        private static void CreateFromLines(IAxisVMModel model, List<List<ResolvedLine>> groups, OneWayPanelOptions opt,
            bool applyLoad, int loadCaseId, double q, RunStats stats)
        {
            // Splitting for edge strips renumbers lines: remember each group by UID.
            List<List<int>> groupUids = new List<List<int>>();
            foreach (List<ResolvedLine> group in groups)
            {
                List<int> uids = new List<int>(group.Count);
                foreach (ResolvedLine line in group) uids.Add(model.Lines.get_UID(line.LineId));
                groupUids.Add(uids);
            }
            stats.Targets = groups.Count;

            for (int g = 0; g < groupUids.Count; g++)
            {
                string label = "Boundary " + (g + 1);
                List<ResolvedLine> current = new List<ResolvedLine>(groupUids[g].Count);
                foreach (int uid in groupUids[g])
                {
                    int lineId = model.Lines.get_IndexOfUID(uid);
                    if (lineId > 0) current.Add(OneWayPanelBuilder.ResolveLine(model, lineId));
                }
                if (current.Count != groupUids[g].Count)
                {
                    stats.Notes.Add(label + ": skipped, some of its lines no longer exist.");
                    continue;
                }

                List<int> made = OneWayPanelBuilder.GeneratePanels(model, current, opt, g + 1, stats.Notes);
                if (made.Count == 0) continue;

                stats.Done++;
                stats.PanelsCreated += made.Count;
                CheckReceivers(model, made, label, stats.Notes);

                if (applyLoad)
                {
                    PanelLoadCarrier.CarriedLoad load = new PanelLoadCarrier.CarriedLoad
                    {
                        Kind = PanelLoadCarrier.Kind.Area,
                        LoadCaseId = loadCaseId,
                        DistributionType = (int)EDistributionType.dtGlobal,
                        LoadDistributionType = (int)ELoadDistributionType.ldtConst,
                        Component = (int)EAxis.aZ
                    };
                    load.P[2] = -q; // q is entered as a downward load
                    int applied = PanelLoadCarrier.Reapply(model, new List<PanelLoadCarrier.CarriedLoad> { load }, made, label, stats.Notes);
                    stats.LoadsApplied += applied;
                    if (applied == 0) stats.LoadsLost++;
                }
            }
        }

        // ==================================================================
        // Load panels -> one-way panels
        // ==================================================================

        private static void ConvertPanels(IAxisVMModel model, List<int> panelUids, OneWayPanelOptions opt, RunStats stats)
        {
            stats.Targets = panelUids.Count;
            for (int p = 0; p < panelUids.Count; p++)
            {
                string label = "Panel " + (p + 1);
                try { ConvertPanel(model, panelUids[p], p + 1, label, opt, stats); }
                catch (Exception ex) { stats.Notes.Add(string.Format("{0}: FAILED ({1}).", label, ex.Message)); }
            }
        }

        private static void ConvertPanel(IAxisVMModel model, int panelUid, int number, string label, OneWayPanelOptions opt, RunStats stats)
        {
            int idx = model.LoadPanels.get_IndexOfUID(panelUid);
            if (idx <= 0) { stats.Notes.Add(label + ": no longer exists."); return; }

            // 1. Outline -> boundary lines on the existing members.
            PanelBoundaryResolver.Result outline;
            string err;
            if (!PanelBoundaryResolver.TryResolve(model, idx, out outline, out err))
            {
                stats.Notes.Add(string.Format("{0}: skipped, {1}. The panel was left unchanged.", label, err));
                return;
            }

            string invalid = opt.Validate() ?? OneWayPanelBuilder.ValidateBoundary(model, outline.Segments);
            if (invalid != null)
            {
                PanelBoundaryResolver.DeleteCreatedLines(model, outline);
                stats.Notes.Add(string.Format("{0}: skipped, {1}. The panel was left unchanged.", label, invalid));
                return;
            }

            if (outline.ReplacedLines > 0)
                stats.Notes.Add(string.Format("{0}: {1} plain line(s) on its outline duplicate members - the members are used. " +
                    "Delete the duplicates if they are left over from an earlier run.", label, outline.ReplacedLines));
            if (outline.CreatedLineUids.Count > 0)
                stats.Notes.Add(string.Format("{0}: no line existed along part of its outline - {1} plain line(s) were added there.",
                    label, outline.CreatedLineUids.Count));

            // 2. Take the loads off the panel. They are deleted first, explicitly, so none are
            //    orphaned by the panel deletion and none are applied twice.
            PanelLoadCarrier.Captured captured = PanelLoadCarrier.Capture(model, idx);
            if (captured.NotCarried > 0)
                stats.Notes.Add(string.Format("{0}: {1} load(s) of another kind (line/polyline) are on it and are NOT carried over.",
                    label, captured.NotCarried));
            PanelLoadCarrier.DeleteCaptured(model, captured);

            idx = model.LoadPanels.get_IndexOfUID(panelUid);
            if (idx <= 0 || model.LoadPanels.Delete(idx) <= 0)
            {
                PanelBoundaryResolver.DeleteCreatedLines(model, outline);
                int back = PanelLoadCarrier.Reapply(model, captured.Loads, new List<int> { panelUid }, label, stats.Notes);
                stats.LoadsLost += captured.Loads.Count - back;
                stats.Notes.Add(label + ": AxisVM refused to delete the original panel - it was left as it was.");
                return;
            }

            // 3. Rebuild as one-way.
            List<int> boundaryUids = new List<int>(outline.Segments.Count);
            foreach (ResolvedLine s in outline.Segments) boundaryUids.Add(model.Lines.get_UID(s.LineId));

            List<int> made;
            try { made = OneWayPanelBuilder.GeneratePanels(model, outline.Segments, opt, number, stats.Notes, boundaryUids); }
            catch (Exception ex)
            {
                stats.Notes.Add(string.Format("{0}: one-way generation failed ({1}).", label, ex.Message));
                made = new List<int>();
            }

            if (made.Count > 0)
            {
                stats.Done++;
                stats.PanelsCreated += made.Count;
                CheckReceivers(model, made, label, stats.Notes);
            }
            else
            {
                // Never leave a hole: put a plain (two-way) panel back and keep its loads.
                int restored = RestorePlainPanel(model, boundaryUids);
                if (restored <= 0)
                {
                    stats.Notes.Add(string.Format("{0}: FAILED and the original panel could not be restored - {1} load(s) were lost.",
                        label, captured.Loads.Count));
                    stats.LoadsLost += captured.Loads.Count;
                    return;
                }
                made.Add(model.LoadPanels.get_UID(restored));
                stats.Notes.Add(label + ": could not be made one-way - a plain load panel was put back with its loads.");
            }

            // 4. Loads back on.
            int applied = PanelLoadCarrier.Reapply(model, captured.Loads, made, label, stats.Notes);
            stats.LoadsApplied += applied;
            stats.LoadsLost += captured.Loads.Count - applied;
        }

        private static int RestorePlainPanel(IAxisVMModel model, List<int> boundaryUids)
        {
            List<int> ids = new List<int>();
            foreach (int uid in boundaryUids)
            {
                int id = model.Lines.get_IndexOfUID(uid);
                if (id > 0) ids.Add(id);
            }
            if (ids.Count < 3) return 0;

            Array arr = Array.CreateInstance(typeof(int), new int[] { ids.Count }, new int[] { 1 });
            for (int i = 0; i < ids.Count; i++) arr.SetValue(ids[i], i + 1);
            int pid;
            try { pid = model.LoadPanels.AddFromLineIDs(ref arr); } catch { return 0; }
            if (pid > 0) { try { model.LoadPanels.Item[pid].Auto = ELongBoolean.lbTrue; } catch { } }
            return pid;
        }

        // ==================================================================
        // Checks and selection
        // ==================================================================

        /// <summary>
        /// Warns when a new panel sends load to a line without a beam / rib / truss: AxisVM drops
        /// that load at analysis, so it would silently vanish from the results.
        /// </summary>
        private static void CheckReceivers(IAxisVMModel model, List<int> panelUids, string label, List<string> notes)
        {
            HashSet<int> plain = new HashSet<int>();
            foreach (int uid in panelUids)
            {
                int pidx = model.LoadPanels.get_IndexOfUID(uid);
                if (pidx <= 0) continue;
                Array arr = null;
                try { model.LoadPanels.Item[pidx].GetLines(out arr); } catch { continue; }
                if (arr == null) continue;
                foreach (object o in arr)
                {
                    int id = Convert.ToInt32(o);
                    if (id <= 0) continue;
                    ELineType type;
                    try { type = model.Lines.Item[id].LineType; } catch { continue; }
                    if (type == ELineType.ltSimpleLine) plain.Add(id);
                }
            }
            if (plain.Count > 0)
                notes.Add(string.Format("{0}: WARNING - {1} receiving line(s) have no beam/rib element (line {2}). " +
                    "Load sent to them does not reach the structure; define them as beams or ribs.",
                    label, plain.Count, string.Join(", ", plain)));
        }

        /// <summary>Load panels have no GetSelectedItemIds; the selection is read index by index.</summary>
        private static List<int> SelectedPanelUids(IAxisVMModel model)
        {
            List<int> uids = new List<int>();
            if (model.LoadPanels == null) return uids;

            int count = 0;
            try { count = model.LoadPanels.Count; } catch { return uids; }

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    if (model.LoadPanels.get_Selected(i) == ELongBoolean.lbTrue) uids.Add(model.LoadPanels.get_UID(i));
                }
                catch { }
            }
            return uids;
        }

        /// <summary>
        /// The selected lines. Where a plain line lies on the same two nodes as a member (a duplicate,
        /// often left by an earlier run), only the member is kept, otherwise the boundary would branch.
        /// </summary>
        private static List<ResolvedLine> SelectedLines(IAxisVMModel model)
        {
            Array selected = null;
            try { model.Lines.GetSelectedItemIds(out selected); } catch { }
            List<ResolvedLine> result = new List<ResolvedLine>();
            if (selected == null) return result;

            Dictionary<long, int> byNodes = new Dictionary<long, int>(); // node pair -> position in result
            foreach (object o in selected)
            {
                int id = Convert.ToInt32(o);
                if (id <= 0) continue;
                ResolvedLine line = OneWayPanelBuilder.ResolveLine(model, id);
                long key = (long)Math.Min(line.NodeId1, line.NodeId2) << 32 | (uint)Math.Max(line.NodeId1, line.NodeId2);

                int at;
                if (!byNodes.TryGetValue(key, out at))
                {
                    byNodes[key] = result.Count;
                    result.Add(line);
                }
                else if (IsPlain(model, result[at].LineId) && !IsPlain(model, id))
                {
                    result[at] = line;
                }
            }
            return result;
        }

        private static bool IsPlain(IAxisVMModel model, int lineId)
        {
            try { return model.Lines.Item[lineId].LineType == ELineType.ltSimpleLine; } catch { return false; }
        }

        private static List<string> LoadCaseNames(IAxisVMModel model)
        {
            List<string> names = new List<string>();
            int count = 0;
            try { count = model.LoadCases.Count; } catch { }
            for (int i = 1; i <= count; i++)
            {
                string name = null;
                try { name = model.LoadCases.get_Name(i); } catch { }
                names.Add(string.IsNullOrEmpty(name) ? "Load case " + i : name);
            }
            return names;
        }

        private static string Headline(bool convert, RunStats s)
        {
            string head = convert
                ? string.Format("Converted {0} of {1} load panel(s) into {2} one-way panel(s).", s.Done, s.Targets, s.PanelsCreated)
                : string.Format("Created {0} one-way panel(s) on {1} of {2} boundary(ies).", s.PanelsCreated, s.Done, s.Targets);
            if (s.LoadsApplied > 0 || s.LoadsLost > 0)
                head += string.Format("\nLoads applied: {0}.", s.LoadsApplied) + (s.LoadsLost > 0 ? string.Format("  NOT applied: {0}.", s.LoadsLost) : "");
            return head;
        }
    }
}
