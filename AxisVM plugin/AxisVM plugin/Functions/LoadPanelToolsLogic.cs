using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Three multi-target load panel operations, all driven off the current selection and all run inside a
    /// single BeginUpdate / EndUpdate pair:
    ///
    ///   1. Load Panel -> Domain     every selected panel's contour becomes a domain
    ///   2. Domain -> Load Panel     every selected domain gets a load panel (IAxisVMLoadPanels.AddFromDomain)
    ///   3. Split Load Panel         selected lines cut the selected panels into pieces
    ///
    /// Everything that has to survive a mutation is tracked by UID rather than by index. Deleting a panel
    /// renumbers the panel collection and splitting a line renumbers the line collection, so an index read
    /// before one of those is worthless afterwards.
    /// </summary>
    public static class LoadPanelToolsLogic
    {
        /// <summary>Placeholder thickness for a domain created from a load panel [m]. A panel carries no thickness of its own.</summary>
        private const double DefaultThicknessM = 0.200;

        /// <summary>How far off a contour edge a node may sit and still count as lying on it [m].</summary>
        private const double OnEdgeToleranceM = 1e-4;

        private class Counters
        {
            public int Converted;
            public int PiecesCreated;
            public int Deleted;
            public int SurvivingPanels;
            public int SplittersUsed;
        }

        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                int defaultMaterialId;
                string defaultsText = DescribeDomainDefaults(model, out defaultMaterialId);

                using (LoadPanelToolsForm form = new LoadPanelToolsForm(defaultsText))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    Counters counters = new Counters();
                    List<string> notes = new List<string>();

                    model.BeginUpdate();
                    try
                    {
                        switch (form.Operation)
                        {
                            case LoadPanelOperation.PanelToDomain:
                                PanelsToDomains(model, form.DeleteOriginal, defaultMaterialId, counters, notes);
                                break;

                            case LoadPanelOperation.DomainToPanel:
                                DomainsToPanels(model, form.DeleteOriginal, counters, notes);
                                break;

                            case LoadPanelOperation.SplitPanelByLines:
                                SplitPanels(model, counters, notes);
                                break;
                        }
                    }
                    finally
                    {
                        model.EndUpdate();
                        try { model.Refresh(); } catch { }
                    }

                    ShowSummary(form.Operation, counters, notes);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        // ==================================================================
        // Operation 1 - load panels to domains
        // ==================================================================

        private static void PanelsToDomains(IAxisVMModel model, bool deleteOriginal, int materialId, Counters counters, List<string> notes)
        {
            List<int> panelUids = SelectedPanelUids(model);
            if (panelUids.Count == 0)
            {
                notes.Add("No load panels are selected.");
                return;
            }

            if (materialId <= 0)
            {
                notes.Add("The model has no material defined, so no domain could be created. Add a material first.");
                return;
            }

            RSurfaceAttr attr = new RSurfaceAttr();
            attr.Thickness = DefaultThicknessM;
            attr.SurfaceType = ESurfaceType.stShell;
            attr.MaterialId = materialId;

            List<int> converted = new List<int>();

            // Creating domains does not renumber load panels, so every panel can be resolved up front and
            // the whole loop runs against stable indices.
            for (int i = 0; i < panelUids.Count; i++)
            {
                int panelId = model.LoadPanels.get_IndexOfUID(panelUids[i]);
                if (panelId <= 0) continue;

                Array contour = null;
                int edges = 0;
                try { edges = model.LoadPanels.Item[panelId].GetContourLineIDs(ref contour); }
                catch { edges = 0; }

                if (edges <= 0 || contour == null)
                {
                    // GetContourLineIDs fails with lopeInvalidContourType for lpctUserDefined panels -
                    // those are free polygons with no lines behind them, so there is nothing to hand to
                    // Domains.Add.
                    notes.Add(string.Format("Panel {0}: its contour is not built from lines (user-defined polygon), so it cannot become a domain.", panelId));
                    continue;
                }

                int domainId = 0;
                try { domainId = model.Domains.Add(contour, ref attr); }
                catch (Exception ex) { notes.Add(string.Format("Panel {0}: AxisVM rejected the domain ({1}).", panelId, ex.Message)); continue; }

                if (domainId <= 0)
                {
                    notes.Add(string.Format("Panel {0}: AxisVM rejected the domain (code {1}). Its contour may not be planar or closed.", panelId, domainId));
                    continue;
                }

                counters.Converted++;
                converted.Add(panelUids[i]);
            }

            if (deleteOriginal && converted.Count > 0)
            {
                counters.Deleted = DeletePanels(model, converted);
            }
        }

        // ==================================================================
        // Operation 2 - domains to load panels
        // ==================================================================

        private static void DomainsToPanels(IAxisVMModel model, bool deleteOriginal, Counters counters, List<string> notes)
        {
            Array selected = null;
            try { model.Domains.GetSelectedItemIds(out selected); }
            catch { }

            List<int> domainIds = ToIdList(selected);
            if (domainIds.Count == 0)
            {
                notes.Add("No domains are selected.");
                return;
            }

            List<int> createdPanelUids = new List<int>();

            // Adding load panels does not renumber domains, so the domain indices stay valid throughout.
            foreach (int domainId in domainIds)
            {
                int panelId = 0;
                try { panelId = model.LoadPanels.AddFromDomain(domainId); }
                catch (Exception ex) { notes.Add(string.Format("Domain {0}: AxisVM rejected the load panel ({1}).", domainId, ex.Message)); continue; }

                if (panelId <= 0)
                {
                    notes.Add(string.Format("Domain {0}: AxisVM rejected the load panel (code {1}).", domainId, panelId));
                    continue;
                }

                counters.Converted++;
                try { createdPanelUids.Add(model.LoadPanels.get_UID(panelId)); } catch { }
            }

            if (!deleteOriginal || createdPanelUids.Count == 0)
            {
                counters.SurvivingPanels = createdPanelUids.Count;
                return;
            }

            int removed = 0;
            try
            {
                foreach (int domainId in domainIds)
                {
                    try { model.Domains.set_Selected(domainId, ELongBoolean.lbTrue); } catch { }
                }
                removed = model.Domains.DeleteSelected();
            }
            catch (Exception ex) { notes.Add("Deleting the source domains failed: " + ex.Message); }

            counters.Deleted = Math.Max(removed, 0);

            // A panel made by AddFromDomain has an associated contour (lpctAssociated), so removing the
            // domain can remove the panel with it. Rather than guess, count what is actually still there.
            foreach (int uid in createdPanelUids)
            {
                int idx = 0;
                try { idx = model.LoadPanels.get_IndexOfUID(uid); } catch { }
                if (idx > 0) counters.SurvivingPanels++;
            }

            if (counters.SurvivingPanels < createdPanelUids.Count)
            {
                notes.Add(string.Format(
                    "{0} of the {1} new panels were removed together with their domain - a panel created from a domain is associated with it. Re-run without \"delete the original\" to keep them.",
                    createdPanelUids.Count - counters.SurvivingPanels, createdPanelUids.Count));
            }
        }

        // ==================================================================
        // Operation 3 - split panels by lines
        // ==================================================================

        private static void SplitPanels(IAxisVMModel model, Counters counters, List<string> notes)
        {
            List<int> panelUids = SelectedPanelUids(model);
            if (panelUids.Count == 0)
            {
                notes.Add("No load panels are selected.");
                return;
            }

            Array selectedLines = null;
            try { model.Lines.GetSelectedItemIds(out selectedLines); }
            catch { }

            List<int> lineIds = ToIdList(selectedLines);
            if (lineIds.Count == 0)
            {
                notes.Add("No lines are selected. Select the panels and the lines that cut them.");
                return;
            }

            // Splitting a contour edge renumbers lines, so the cutting lines are held as UIDs too.
            List<int> lineUids = new List<int>();
            foreach (int id in lineIds)
            {
                try { lineUids.Add(model.Lines.get_UID(id)); } catch { }
            }

            foreach (int panelUid in panelUids)
            {
                SplitOnePanel(model, panelUid, lineUids, counters, notes);
            }
        }

        /// <summary>
        /// Cuts one panel with as many of the candidate lines as actually apply. Each successful cut
        /// replaces a panel with two pieces, and the remaining lines are then tried against those pieces,
        /// so a panel crossed by two lines ends up as three or four pieces.
        /// </summary>
        private static void SplitOnePanel(IAxisVMModel model, int panelUid, List<int> candidateLineUids, Counters counters, List<string> notes)
        {
            // Ancestry of split contour edges: when a contour line is cut in two, both halves must still
            // count as "the original loaded edge" when the new panels' load-carrying lines are worked out.
            Dictionary<int, int> uidAncestor = new Dictionary<int, int>();

            HashSet<int> loadedUids = ReadLoadedEdgeUids(model, panelUid);
            ELongBoolean autoFlag = ELongBoolean.lbFalse;
            int startIndex = model.LoadPanels.get_IndexOfUID(panelUid);
            if (startIndex <= 0)
            {
                notes.Add("A selected panel no longer exists.");
                return;
            }
            try { autoFlag = model.LoadPanels.Item[startIndex].Auto; } catch { }

            List<int> remaining = new List<int>(candidateLineUids);
            List<int> pending = new List<int> { panelUid };
            List<string> localNotes = new List<string>();
            int piecesFromThisPanel = 0;
            bool anySplit = false;

            while (pending.Count > 0)
            {
                int currentUid = pending[0];
                pending.RemoveAt(0);

                int usedSplitter = 0;
                int pieceA = 0, pieceB = 0;

                foreach (int splitterUid in new List<int>(remaining))
                {
                    string reason;
                    if (TrySplitPanel(model, currentUid, splitterUid, loadedUids, uidAncestor, autoFlag, out pieceA, out pieceB, out reason))
                    {
                        usedSplitter = splitterUid;
                        break;
                    }

                    if (!string.IsNullOrEmpty(reason)) localNotes.Add(reason);
                }

                if (usedSplitter == 0)
                {
                    // Nothing cuts this piece any further - it is a final result.
                    piecesFromThisPanel++;
                    continue;
                }

                remaining.Remove(usedSplitter);
                counters.SplittersUsed++;
                anySplit = true;

                int idx = model.LoadPanels.get_IndexOfUID(currentUid);
                if (idx > 0) { try { model.LoadPanels.Delete(idx); } catch { } }

                pending.Add(pieceA);
                pending.Add(pieceB);
            }

            if (!anySplit)
            {
                // Report why, but only once per panel - the loop retries every line against every piece and
                // would otherwise emit the same complaint many times over.
                notes.Add(string.Format("Panel UID {0} was not cut. {1}", panelUid, FirstDistinct(localNotes)));
                return;
            }

            counters.PiecesCreated += piecesFromThisPanel;
            counters.Converted++;
        }

        /// <summary>
        /// Attempts one cut. Returns the UIDs of the two new panels. The caller deletes the old one -
        /// doing it here would invalidate the index it is still holding.
        /// </summary>
        private static bool TrySplitPanel(IAxisVMModel model, int panelUid, int splitterUid, HashSet<int> loadedUids, Dictionary<int, int> uidAncestor, ELongBoolean autoFlag, out int newUidA, out int newUidB, out string reason)
        {
            newUidA = 0;
            newUidB = 0;
            reason = null;

            int panelId = model.LoadPanels.get_IndexOfUID(panelUid);
            int splitterId = model.Lines.get_IndexOfUID(splitterUid);
            if (panelId <= 0 || splitterId <= 0) { reason = null; return false; }

            List<int> ringLines, ringNodes;
            if (!BuildContourRing(model, panelId, out ringLines, out ringNodes))
            {
                reason = "Its contour could not be walked as a closed loop.";
                return false;
            }

            if (ringLines.Contains(splitterId)) { reason = null; return false; } // the line IS an edge

            int nodeA, nodeB;
            try
            {
                var splitter = model.Lines.Item[splitterId];
                nodeA = splitter.StartNode;
                nodeB = splitter.EndNode;
            }
            catch { reason = null; return false; }

            // Both ends have to be corners of the contour. If an end sits part-way along an edge, that
            // edge is cut there first so the end becomes a real corner.
            if (!EnsureContourCorner(model, panelUid, nodeA, uidAncestor, ref ringLines, ref ringNodes)) { reason = "A cutting line's end does not lie on the panel contour."; return false; }
            if (!EnsureContourCorner(model, panelUid, nodeB, uidAncestor, ref ringLines, ref ringNodes)) { reason = "A cutting line's end does not lie on the panel contour."; return false; }

            // EnsureContourCorner may have split a contour edge, and splitting renumbers the line
            // collection - so the cutting line's index from before that is no longer trustworthy.
            // Node indices are safe: splitting only adds nodes, it never removes them.
            splitterId = model.Lines.get_IndexOfUID(splitterUid);
            if (splitterId <= 0) { reason = null; return false; }

            int ia = ringNodes.IndexOf(nodeA);
            int ib = ringNodes.IndexOf(nodeB);
            if (ia < 0 || ib < 0) { reason = "A cutting line's end does not lie on the panel contour."; return false; }

            int n = ringNodes.Count;
            if (ia == ib) { reason = "A cutting line starts and ends at the same contour corner."; return false; }
            if ((ia + 1) % n == ib || (ib + 1) % n == ia) { reason = "A cutting line runs between two neighbouring corners, so it does not cut the panel in two."; return false; }

            List<int> loop1 = EdgesBetween(ringLines, ia, ib, n);
            List<int> loop2 = EdgesBetween(ringLines, ib, ia, n);
            loop1.Add(splitterId);
            loop2.Add(splitterId);

            bool splitterCarriesLoad = IsBeamOrRib(model, splitterId);

            int panelA = CreatePanel(model, loop1, loadedUids, uidAncestor, autoFlag, splitterId, splitterCarriesLoad);
            if (panelA <= 0) { reason = "AxisVM rejected one of the two pieces."; return false; }

            int panelB = CreatePanel(model, loop2, loadedUids, uidAncestor, autoFlag, splitterId, splitterCarriesLoad);
            if (panelB <= 0)
            {
                // Do not leave half a split behind.
                try { model.LoadPanels.Delete(panelA); } catch { }
                reason = "AxisVM rejected one of the two pieces.";
                return false;
            }

            newUidA = model.LoadPanels.get_UID(panelA);
            newUidB = model.LoadPanels.get_UID(panelB);
            return true;
        }

        /// <summary>Contour edges walked forward from corner <paramref name="from"/> up to corner <paramref name="to"/>.</summary>
        private static List<int> EdgesBetween(List<int> ringLines, int from, int to, int n)
        {
            List<int> result = new List<int>();
            int i = from;
            while (i != to)
            {
                result.Add(ringLines[i]);
                i = (i + 1) % n;
            }
            return result;
        }

        /// <summary>
        /// Walks the panel's contour into an ordered ring: ringLines[i] runs from ringNodes[i] to
        /// ringNodes[i+1], and the last edge closes back onto ringNodes[0].
        /// </summary>
        private static bool BuildContourRing(IAxisVMModel model, int panelId, out List<int> ringLines, out List<int> ringNodes)
        {
            ringLines = new List<int>();
            ringNodes = new List<int>();

            Array arr = null;
            int edges = 0;
            try { edges = model.LoadPanels.Item[panelId].GetContourLineIDs(ref arr); }
            catch { return false; }

            List<int> ids = ToIdList(arr);
            if (edges <= 0 || ids.Count < 3) return false;

            Dictionary<int, int> startOf = new Dictionary<int, int>();
            Dictionary<int, int> endOf = new Dictionary<int, int>();
            foreach (int id in ids)
            {
                try
                {
                    var line = model.Lines.Item[id];
                    startOf[id] = line.StartNode;
                    endOf[id] = line.EndNode;
                }
                catch { return false; }
            }

            HashSet<int> unused = new HashSet<int>(ids);
            int first = ids[0];
            unused.Remove(first);

            ringLines.Add(first);
            ringNodes.Add(startOf[first]);
            int current = endOf[first];

            while (unused.Count > 0)
            {
                int next = 0;
                int other = 0;
                foreach (int id in ids)
                {
                    if (!unused.Contains(id)) continue;
                    if (startOf[id] == current) { next = id; other = endOf[id]; break; }
                    if (endOf[id] == current) { next = id; other = startOf[id]; break; }
                }

                if (next == 0) return false; // not a single closed loop

                unused.Remove(next);
                ringLines.Add(next);
                ringNodes.Add(current);
                current = other;
            }

            // The walk must come back to where it started.
            return current == ringNodes[0] && ringNodes.Count == ringLines.Count;
        }

        /// <summary>
        /// Makes sure <paramref name="nodeId"/> is a corner of the contour. If it sits part-way along a
        /// contour edge, that edge is split there (IAxisVMLine.SplitByNode) and the ring is rebuilt.
        /// Returns false when the node is not on the contour at all.
        /// </summary>
        private static bool EnsureContourCorner(IAxisVMModel model, int panelUid, int nodeId, Dictionary<int, int> uidAncestor, ref List<int> ringLines, ref List<int> ringNodes)
        {
            if (ringNodes.Contains(nodeId)) return true;

            RNode target = new RNode();
            try { model.Nodes.GetNode(nodeId, ref target); } catch { return false; }

            foreach (int edgeId in new List<int>(ringLines))
            {
                if (!NodeLiesOnLine(model, edgeId, target)) continue;

                int ancestorUid;
                try { ancestorUid = model.Lines.get_UID(edgeId); } catch { return false; }
                if (uidAncestor.ContainsKey(ancestorUid)) ancestorUid = uidAncestor[ancestorUid];

                int part1, part2;
                try
                {
                    model.Lines.Item[edgeId].SplitByNode(nodeId, out part1, out part2);
                }
                catch { return false; }

                if (part1 <= 0 || part2 <= 0) return false;

                // Both halves inherit the original edge's identity for load-carrying purposes.
                try
                {
                    uidAncestor[model.Lines.get_UID(part1)] = ancestorUid;
                    uidAncestor[model.Lines.get_UID(part2)] = ancestorUid;
                }
                catch { }

                // The panel's contour follows its lines, so re-read it rather than patching the ring by
                // hand - and re-resolve the panel first, since the split may have renumbered things.
                int panelId = model.LoadPanels.get_IndexOfUID(panelUid);
                if (panelId <= 0) return false;

                List<int> newLines, newNodes;
                if (!BuildContourRing(model, panelId, out newLines, out newNodes)) return false;

                ringLines = newLines;
                ringNodes = newNodes;
                return ringNodes.Contains(nodeId);
            }

            return false;
        }

        private static bool NodeLiesOnLine(IAxisVMModel model, int lineId, RNode target)
        {
            RNode a = new RNode();
            RNode b = new RNode();
            try
            {
                var line = model.Lines.Item[lineId];
                model.Nodes.GetNode(line.StartNode, ref a);
                model.Nodes.GetNode(line.EndNode, ref b);
            }
            catch { return false; }

            double abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
            double apx = target.x - a.x, apy = target.y - a.y, apz = target.z - a.z;

            double abLen2 = abx * abx + aby * aby + abz * abz;
            if (abLen2 < 1e-12) return false;

            // Projection parameter along the edge; strictly between the ends, otherwise it is an existing corner.
            double t = (apx * abx + apy * aby + apz * abz) / abLen2;
            if (t <= 1e-6 || t >= 1.0 - 1e-6) return false;

            double cx = a.x + abx * t - target.x;
            double cy = a.y + aby * t - target.y;
            double cz = a.z + abz * t - target.z;

            return Math.Sqrt(cx * cx + cy * cy + cz * cz) <= OnEdgeToleranceM;
        }

        /// <summary>Creates one piece and carries the original's load distribution settings across.</summary>
        private static int CreatePanel(IAxisVMModel model, List<int> loopLineIds, HashSet<int> loadedUids, Dictionary<int, int> uidAncestor, ELongBoolean autoFlag, int splitterId, bool splitterCarriesLoad)
        {
            Array ids = ToComArray(loopLineIds);

            int panelId;
            try { panelId = model.LoadPanels.AddFromLineIDs(ref ids); }
            catch { return 0; }

            if (panelId <= 0) return 0;

            try
            {
                var panel = model.LoadPanels.Item[panelId];
                panel.Auto = autoFlag;

                if (autoFlag == ELongBoolean.lbTrue) return panelId; // AxisVM works the edges out itself

                // Load-carrying edges: whichever of this piece's edges carried load on the original panel,
                // plus the cutting line itself when it is a real member.
                List<int> loaded = new List<int>();
                foreach (int lineId in loopLineIds)
                {
                    if (lineId == splitterId)
                    {
                        if (splitterCarriesLoad) loaded.Add(lineId);
                        continue;
                    }

                    int uid;
                    try { uid = model.Lines.get_UID(lineId); } catch { continue; }

                    int ancestor;
                    if (uidAncestor.TryGetValue(uid, out ancestor)) uid = ancestor;

                    if (loadedUids.Contains(uid)) loaded.Add(lineId);
                }

                if (loaded.Count > 0)
                {
                    Array loadedArr = ToComArray(loaded);
                    panel.SetLines(ref loadedArr);

                    // AxisVM's own distribution also reads per-edge flags, indexed in contour order.
                    HashSet<int> loadedSet = new HashSet<int>(loaded);
                    Array contour = null;
                    panel.GetContourLineIDs(ref contour);
                    if (contour != null)
                    {
                        int lower = contour.GetLowerBound(0);
                        int upper = contour.GetUpperBound(0);
                        for (int i = lower; i <= upper; i++)
                        {
                            int contourLineId = Convert.ToInt32(contour.GetValue(i));
                            panel.set_SelectedEdge(i - lower + 1, loadedSet.Contains(contourLineId) ? ELongBoolean.lbTrue : ELongBoolean.lbFalse);
                        }
                    }
                }
            }
            catch { }

            return panelId;
        }

        private static HashSet<int> ReadLoadedEdgeUids(IAxisVMModel model, int panelUid)
        {
            HashSet<int> result = new HashSet<int>();

            int panelId = model.LoadPanels.get_IndexOfUID(panelUid);
            if (panelId <= 0) return result;

            Array arr = null;
            try { model.LoadPanels.Item[panelId].GetLines(out arr); }
            catch { return result; }

            foreach (int lineId in ToIdList(arr))
            {
                try { result.Add(model.Lines.get_UID(lineId)); } catch { }
            }
            return result;
        }

        private static bool IsBeamOrRib(IAxisVMModel model, int lineId)
        {
            try
            {
                ELineType t = model.Lines.Item[lineId].LineType;
                return t == ELineType.ltBeam || t == ELineType.ltRib;
            }
            catch { return false; }
        }

        // ==================================================================
        // Shared helpers
        // ==================================================================

        /// <summary>Load panels have no GetSelectedItemIds, so the selection is read index by index.</summary>
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
                    if (model.LoadPanels.get_Selected(i) != ELongBoolean.lbTrue) continue;
                    uids.Add(model.LoadPanels.get_UID(i));
                }
                catch { }
            }
            return uids;
        }

        /// <summary>Marks the given panels and removes them with one DeleteSelected call.</summary>
        private static int DeletePanels(IAxisVMModel model, List<int> panelUids)
        {
            try { model.LoadPanels.SelectAll(ELongBoolean.lbFalse); } catch { }

            int marked = 0;
            foreach (int uid in panelUids)
            {
                int idx = 0;
                try { idx = model.LoadPanels.get_IndexOfUID(uid); } catch { }
                if (idx <= 0) continue;

                try { model.LoadPanels.set_Selected(idx, ELongBoolean.lbTrue); marked++; } catch { }
            }

            if (marked == 0) return 0;

            try { model.LoadPanels.DeleteSelected(); } catch { return 0; }
            return marked;
        }

        private static List<int> ToIdList(Array arr)
        {
            List<int> ids = new List<int>();
            if (arr == null) return ids;

            int lower = arr.GetLowerBound(0);
            int upper = arr.GetUpperBound(0);
            for (int i = lower; i <= upper; i++)
            {
                try
                {
                    int id = Convert.ToInt32(arr.GetValue(i));
                    if (id > 0) ids.Add(id);
                }
                catch { }
            }
            return ids;
        }

        /// <summary>AxisVM expects 1-based SAFEARRAYs of long for every id list.</summary>
        private static Array ToComArray(IList<int> ids)
        {
            Array arr = Array.CreateInstance(typeof(int), new int[] { ids.Count }, new int[] { 1 });
            for (int i = 0; i < ids.Count; i++) arr.SetValue(ids[i], i + 1);
            return arr;
        }

        /// <summary>
        /// A load panel carries no thickness or material, so a domain made from one needs placeholders.
        /// Concrete is preferred over whatever happens to be first, because a panel almost always stands
        /// in for a slab.
        /// </summary>
        private static string DescribeDomainDefaults(IAxisVMModel model, out int materialId)
        {
            materialId = 0;
            string materialName = "(none - add a material first)";

            try
            {
                int count = model.Materials.Count;
                for (int i = 1; i <= count; i++)
                {
                    if (model.Materials.Item[i].MaterialType != EMaterialType.mtConcrete) continue;
                    materialId = i;
                    materialName = model.Materials.Item[i].Name;
                    break;
                }

                if (materialId <= 0 && count > 0)
                {
                    materialId = 1;
                    materialName = model.Materials.Item[1].Name;
                }
            }
            catch { }

            return string.Format("  thickness {0:F0} mm, type Shell, material '{1}'", DefaultThicknessM * 1000.0, materialName);
        }

        private static string FirstDistinct(List<string> notes)
        {
            foreach (string note in notes)
            {
                if (!string.IsNullOrEmpty(note)) return note;
            }
            return "No selected line runs between two points on its contour.";
        }

        private static void ShowSummary(LoadPanelOperation operation, Counters counters, List<string> notes)
        {
            StringBuilder sb = new StringBuilder();

            if (operation == LoadPanelOperation.PanelToDomain)
            {
                sb.AppendLine(string.Format("Created {0} domain(s) from the selected load panels.", counters.Converted));
                if (counters.Deleted > 0) sb.AppendLine(string.Format("Deleted {0} source load panel(s).", counters.Deleted));
            }
            else if (operation == LoadPanelOperation.DomainToPanel)
            {
                sb.AppendLine(string.Format("Created {0} load panel(s) from the selected domains.", counters.Converted));
                if (counters.Deleted > 0)
                {
                    sb.AppendLine(string.Format("Deleted {0} source domain(s).", counters.Deleted));
                    sb.AppendLine(string.Format("Load panels still present afterwards: {0}.", counters.SurvivingPanels));
                }
            }
            else
            {
                sb.AppendLine(string.Format("Split {0} load panel(s) into {1} piece(s) using {2} cutting line(s).",
                    counters.Converted, counters.PiecesCreated, counters.SplittersUsed));
            }

            if (notes.Count > 0)
            {
                sb.AppendLine();
                int shown = Math.Min(notes.Count, 12);
                for (int i = 0; i < shown; i++) sb.AppendLine(notes[i]);
                if (notes.Count > shown) sb.AppendLine("... and " + (notes.Count - shown) + " more.");
            }

            MessageBox.Show(sb.ToString(), "Load Panel Tools", MessageBoxButtons.OK,
                notes.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
