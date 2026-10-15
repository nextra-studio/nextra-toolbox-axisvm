using System;
using System.Collections.Generic;
using System.Linq;
using AxisVM;

namespace AxisVM_plugin.Functions
{
    /// <summary>Outcome of one Load Panel Tools run. Notes explain everything that was skipped.</summary>
    public sealed class LoadPanelOpResult
    {
        public int Converted;
        public int PiecesCreated;
        public int Deleted;
        public int SurvivingPanels;
        public int SplittersUsed;
        public int LoadsCopied;

        /// <summary>Panel to domain: new domain indices. Domain to panel and split: new panel UIDs.</summary>
        public readonly List<int> Created = new List<int>();
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// The three load panel operations, free of any UI so they can be driven from the add-on and from the
    /// live checks alike:
    ///
    ///   1. Load Panel -> Domain     each panel's contour becomes a domain
    ///   2. Domain -> Load Panel     each domain gets a load panel (IAxisVMLoadPanels.AddFromDomain)
    ///   3. Split Load Panel         lines running edge to edge cut panels into pieces
    ///
    /// Everything that has to survive a mutation is tracked by UID rather than by index. Deleting a panel
    /// renumbers the panel collection and IAxisVMLine.SplitByNode renumbers the line collection (both halves
    /// get new UIDs, the original UID disappears), so an index read before one of those is worthless after.
    /// </summary>
    public static class LoadPanelOps
    {
        /// <summary>Placeholder thickness for a domain created from a load panel [m]. A panel carries no thickness of its own.</summary>
        public const double DefaultThicknessM = 0.200;

        /// <summary>How far off a contour edge a node may sit and still count as lying on it [m].</summary>
        private const double OnEdgeToleranceM = 1e-3;

        /// <summary>GetLoad size of a uniform / linear panel load - exactly the RLoadPanelLinear layout.</summary>
        private const int PanelLinearLoadBytes = 120;

        // ==================================================================
        // Selection
        // ==================================================================

        /// <summary>Load panels have no GetSelectedItemIds, so the selection is read index by index.</summary>
        public static List<int> SelectedPanelUids(IAxisVMModel model)
        {
            List<int> uids = new List<int>();
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

        public static List<int> SelectedDomainIds(IAxisVMModel model)
        {
            Array selected = null;
            try { model.Domains.GetSelectedItemIds(out selected); } catch { }
            return ToIdList(selected);
        }

        public static List<int> SelectedLineUids(IAxisVMModel model)
        {
            Array selected = null;
            try { model.Lines.GetSelectedItemIds(out selected); } catch { }

            List<int> uids = new List<int>();
            foreach (int id in ToIdList(selected))
            {
                try { uids.Add(model.Lines.get_UID(id)); } catch { }
            }
            return uids;
        }

        /// <summary>
        /// A load panel carries no material, so a domain made from one needs a placeholder. Concrete is
        /// preferred over whatever happens to be first, because a panel almost always stands in for a slab.
        /// Returns 0 when the model has no material at all.
        /// </summary>
        public static int FindDefaultMaterial(IAxisVMModel model, out string name)
        {
            name = null;
            int count = 0;
            try { count = model.Materials.Count; } catch { return 0; }

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    if (model.Materials.Item[i].MaterialType != EMaterialType.mtConcrete) continue;
                    name = model.Materials.Item[i].Name;
                    return i;
                }
                catch { }
            }

            if (count == 0) return 0;
            try { name = model.Materials.Item[1].Name; } catch { }
            return 1;
        }

        // ==================================================================
        // Operation 1 - load panels to domains
        // ==================================================================

        public static LoadPanelOpResult PanelsToDomains(IAxisVMModel model, IList<int> panelUids, int materialId, bool deleteOriginal)
        {
            LoadPanelOpResult r = new LoadPanelOpResult();
            if (panelUids.Count == 0) { r.Notes.Add("No load panels are selected."); return r; }
            if (materialId <= 0) { r.Notes.Add("The model has no material defined, so no domain could be created. Add a material first."); return r; }

            List<int> converted = new List<int>();
            Dictionary<int, List<RLoadPanelLinear>> loadsByDomain = new Dictionary<int, List<RLoadPanelLinear>>();

            // Creating domains renumbers neither panels nor domains, so indices hold for the whole loop.
            foreach (int uid in panelUids)
            {
                int panelId = PanelIndex(model, uid);
                if (panelId <= 0) continue;

                List<int> contour = ContourLineIds(model, panelId);
                if (contour.Count < 3)
                {
                    // lpctUserDefined panels are free polygons with no lines behind them.
                    r.Notes.Add(string.Format("Panel {0}: its contour is not built from lines, so it cannot become a domain.", panelId));
                    continue;
                }

                RSurfaceAttr attr = new RSurfaceAttr();
                attr.Thickness = DefaultThicknessM;
                attr.SurfaceType = ESurfaceType.stShell;
                attr.MaterialId = materialId;

                int domainId;
                try
                {
                    Array ids = ToComArray(contour);
                    domainId = model.Domains.Add(ids, ref attr);
                }
                catch (Exception ex) { r.Notes.Add(string.Format("Panel {0}: AxisVM rejected the domain ({1}).", panelId, ex.Message)); continue; }

                if (domainId <= 0)
                {
                    r.Notes.Add(string.Format("Panel {0}: AxisVM rejected the domain (code {1}). Its contour may not be planar, or a domain already covers it.", panelId, domainId));
                    continue;
                }

                r.Converted++;
                r.Created.Add(domainId);
                converted.Add(uid);

                if (deleteOriginal)
                {
                    int otherLoads;
                    loadsByDomain[domainId] = ReadPanelLinearLoads(model, panelId, out otherLoads);
                    if (otherLoads > 0)
                        r.Notes.Add(string.Format("Panel {0}: {1} load(s) of a kind other than uniform/linear area load were removed with it - re-apply them on domain {2}.", panelId, otherLoads, domainId));
                }
            }

            if (!deleteOriginal || converted.Count == 0) return r;

            r.Deleted = DeletePanels(model, converted);

            // The panel's area loads went with it; put them on the domain so the model stays loaded.
            // Deleting panels does not renumber domains.
            foreach (KeyValuePair<int, List<RLoadPanelLinear>> entry in loadsByDomain)
            {
                foreach (RLoadPanelLinear load in entry.Value)
                {
                    RLoadDomainLinear d = new RLoadDomainLinear();
                    d.LoadCaseId = load.LoadCaseId;
                    d.DomainId = entry.Key;
                    d.DistributionType = load.DistributionType;
                    d.LoadDistributionType = load.LoadDistributionType;
                    d.Component = load.Component;
                    d.P1 = load.P1; d.P2 = load.P2; d.P3 = load.P3;
                    d.x1 = load.x1; d.x2 = load.x2; d.x3 = load.x3;
                    d.y1 = load.y1; d.y2 = load.y2; d.y3 = load.y3;
                    d.z1 = load.z1; d.z2 = load.z2; d.z3 = load.z3;
                    d.WindowLoad = ELongBoolean.lbFalse;

                    int rc = 0;
                    try { rc = model.Loads.AddDomainLinear(ref d); } catch { }
                    if (rc > 0) r.LoadsCopied++;
                    else r.Notes.Add(string.Format("Domain {0}: a panel load (case {1}) could not be moved onto it (code {2}).", entry.Key, load.LoadCaseId, rc));
                }
            }
            return r;
        }

        // ==================================================================
        // Operation 2 - domains to load panels
        // ==================================================================

        public static LoadPanelOpResult DomainsToPanels(IAxisVMModel model, IList<int> domainIds, bool deleteOriginal)
        {
            LoadPanelOpResult r = new LoadPanelOpResult();
            if (domainIds.Count == 0) { r.Notes.Add("No domains are selected."); return r; }

            // AddFromDomain hands back the existing panel when one already covers the domain, so a returned
            // UID that was already there means nothing new was made.
            HashSet<int> existing = AllPanelUids(model);
            List<int> convertedDomains = new List<int>();

            // Adding load panels does not renumber domains, so the indices stay valid throughout.
            foreach (int domainId in domainIds)
            {
                int panelId;
                try { panelId = model.LoadPanels.AddFromDomain(domainId); }
                catch (Exception ex) { r.Notes.Add(string.Format("Domain {0}: AxisVM rejected the load panel ({1}).", domainId, ex.Message)); continue; }

                if (panelId <= 0)
                {
                    r.Notes.Add(string.Format("Domain {0}: AxisVM rejected the load panel (code {1}).", domainId, panelId));
                    continue;
                }

                int uid;
                try { uid = model.LoadPanels.get_UID(panelId); } catch { continue; }

                if (!existing.Add(uid))
                {
                    r.Notes.Add(string.Format("Domain {0}: load panel {1} already covers it, so nothing new was created and the domain was kept.", domainId, panelId));
                    continue;
                }

                r.Converted++;
                r.Created.Add(uid);
                convertedDomains.Add(domainId);
            }

            if (deleteOriginal && convertedDomains.Count > 0)
            {
                int otherLoads = CountDomainLoads(model, convertedDomains);
                if (otherLoads > 0)
                    r.Notes.Add(string.Format("{0} load(s) on the deleted domains were removed with them - re-apply them on the new load panels.", otherLoads));

                // Deleting renumbers the domains above the one removed, so go from the top down.
                List<int> order = new List<int>(convertedDomains);
                order.Sort();
                order.Reverse();
                foreach (int domainId in order)
                {
                    try { if (model.Domains.Delete(domainId) > 0) r.Deleted++; }
                    catch (Exception ex) { r.Notes.Add(string.Format("Domain {0}: could not be deleted ({1}).", domainId, ex.Message)); }
                }
            }

            foreach (int uid in r.Created)
            {
                if (PanelIndex(model, uid) > 0) r.SurvivingPanels++;
            }
            if (r.SurvivingPanels < r.Created.Count)
                r.Notes.Add(string.Format("{0} of the {1} new panels disappeared together with their domain.", r.Created.Count - r.SurvivingPanels, r.Created.Count));

            return r;
        }

        // ==================================================================
        // Operation 3 - split panels by lines
        // ==================================================================

        public static LoadPanelOpResult SplitPanels(IAxisVMModel model, IList<int> panelUids, IList<int> lineUids)
        {
            LoadPanelOpResult r = new LoadPanelOpResult();
            if (panelUids.Count == 0) { r.Notes.Add("No load panels are selected."); return r; }
            if (lineUids.Count == 0) { r.Notes.Add("No lines are selected. Select the panels and the lines that cut them."); return r; }

            HashSet<int> used = new HashSet<int>();
            foreach (int panelUid in panelUids) SplitOnePanel(model, panelUid, lineUids, used, r);

            // A selected line that cut nothing while others did is easy to miss in the model, so name the count.
            // Contour edges are left out: a box selection around a panel picks those up as a matter of course.
            if (r.SplittersUsed > 0)
            {
                HashSet<int> edges = new HashSet<int>();
                foreach (int uid in panelUids.Concat(r.Created))
                {
                    int idx = PanelIndex(model, uid);
                    if (idx <= 0) continue;
                    foreach (int lineId in ContourLineIds(model, idx))
                    {
                        try { edges.Add(model.Lines.get_UID(lineId)); } catch { }
                    }
                }

                int unused = lineUids.Count(uid => !used.Contains(uid) && !edges.Contains(uid) && LineIndex(model, uid) > 0);
                if (unused > 0)
                    r.Notes.Add(string.Format("{0} selected line(s) did not cut any panel: an end is not on a contour, or the line crosses another cut.", unused));
            }
            return r;
        }

        /// <summary>
        /// Cuts one panel with every candidate line that applies. Each successful cut replaces a panel with
        /// two pieces, and the remaining lines are tried against those pieces, so a panel crossed by two
        /// lines ends up as three or four pieces.
        /// </summary>
        private static void SplitOnePanel(IAxisVMModel model, int panelUid, IList<int> candidateLineUids, HashSet<int> used, LoadPanelOpResult r)
        {
            int startIndex = PanelIndex(model, panelUid);
            if (startIndex <= 0) { r.Notes.Add("A selected panel no longer exists."); return; }

            ELongBoolean autoFlag = ELongBoolean.lbFalse;
            try { autoFlag = model.LoadPanels.Item[startIndex].Auto; } catch { }

            PieceSettings settings = new PieceSettings
            {
                Auto = autoFlag,
                LoadedUids = ReadLoadedEdgeUids(model, startIndex),
                Ancestor = new Dictionary<int, int>()
            };

            // Deleting the panel deletes its loads, so read them now and put them on the final pieces.
            int otherLoads;
            List<RLoadPanelLinear> loads = ReadPanelLinearLoads(model, startIndex, out otherLoads);

            List<int> remaining = new List<int>(candidateLineUids);
            Queue<int> pending = new Queue<int>();
            pending.Enqueue(panelUid);
            List<int> finalPieces = new List<int>();
            string firstReason = null;
            int cuts = 0;

            while (pending.Count > 0)
            {
                int currentUid = pending.Dequeue();
                int usedSplitter = 0, pieceA = 0, pieceB = 0;

                foreach (int splitterUid in remaining)
                {
                    string reason;
                    if (TrySplitPanel(model, currentUid, splitterUid, settings, out pieceA, out pieceB, out reason))
                    {
                        usedSplitter = splitterUid;
                        break;
                    }
                    if (firstReason == null) firstReason = reason;
                }

                if (usedSplitter == 0)
                {
                    finalPieces.Add(currentUid); // nothing cuts this piece any further
                    continue;
                }

                remaining.Remove(usedSplitter);
                used.Add(usedSplitter);
                cuts++;

                int idx = PanelIndex(model, currentUid);
                if (idx > 0) { try { model.LoadPanels.Delete(idx); } catch { } }

                pending.Enqueue(pieceA);
                pending.Enqueue(pieceB);
            }

            if (cuts == 0)
            {
                // One reason per panel - every line is retried against every piece and would repeat itself.
                r.Notes.Add(string.Format("Panel {0} was not cut: {1}", startIndex,
                    firstReason ?? "no selected line runs between two points on its contour."));
                return;
            }

            r.Converted++;
            r.SplittersUsed += cuts;
            r.PiecesCreated += finalPieces.Count;
            r.Created.AddRange(finalPieces);

            foreach (int pieceUid in finalPieces)
            {
                int pieceIndex = PanelIndex(model, pieceUid);
                if (pieceIndex <= 0) continue;

                foreach (RLoadPanelLinear load in loads)
                {
                    RLoadPanelLinear copy = load;
                    copy.LoadPanelId = pieceIndex;
                    int rc = 0;
                    try { rc = model.Loads.AddLoadPanelLinear(ref copy); } catch { }
                    if (rc > 0) r.LoadsCopied++;
                    else r.Notes.Add(string.Format("Panel {0}: a load (case {1}) could not be copied onto piece {2} (code {3}).", startIndex, load.LoadCaseId, pieceIndex, rc));
                }
            }

            if (otherLoads > 0)
                r.Notes.Add(string.Format("Panel {0}: {1} load(s) of a kind other than uniform/linear area load were removed with it - re-apply them on the pieces.", startIndex, otherLoads));
        }

        /// <summary>What every piece of one original panel inherits from it.</summary>
        private sealed class PieceSettings
        {
            public ELongBoolean Auto;
            public HashSet<int> LoadedUids;

            /// <summary>
            /// Split contour edge UID -> the original edge's UID. Both halves of a split loaded edge must still
            /// count as "the loaded edge" when the pieces' load-carrying lines are worked out.
            /// </summary>
            public Dictionary<int, int> Ancestor;
        }

        /// <summary>A closed contour: Lines[i] runs from Nodes[i] to Nodes[i+1], the last edge closes onto Nodes[0].</summary>
        private sealed class Ring
        {
            public List<int> Lines = new List<int>();
            public List<int> Nodes = new List<int>();
            public List<RNode> Points = new List<RNode>();
        }

        /// <summary>Where a cutting line's end sits on the ring: on a corner, or part-way along an edge.</summary>
        private struct RingSpot
        {
            public int Corner; // ring node index, or -1
            public int Edge;   // ring edge index, or -1
        }

        /// <summary>
        /// Attempts one cut and returns the UIDs of the two new panels. Everything is validated before the
        /// model is touched, so a line that does not cut the panel leaves no split edge behind. The caller
        /// deletes the old panel - doing it here would invalidate the index it is still holding.
        /// </summary>
        private static bool TrySplitPanel(IAxisVMModel model, int panelUid, int splitterUid, PieceSettings settings, out int newUidA, out int newUidB, out string reason)
        {
            newUidA = newUidB = 0;
            reason = null;

            int panelId = PanelIndex(model, panelUid);
            int splitterId = LineIndex(model, splitterUid);
            if (panelId <= 0 || splitterId <= 0) return false;

            Ring ring;
            if (!BuildRing(model, panelId, out ring)) { reason = "its contour could not be walked as a single closed loop."; return false; }
            if (ring.Lines.Contains(splitterId)) return false; // the line IS a contour edge

            int nodeA, nodeB;
            try
            {
                IAxisVMLine splitter = model.Lines.Item[splitterId];
                nodeA = splitter.StartNode;
                nodeB = splitter.EndNode;
            }
            catch { return false; }

            RNode pa, pb;
            if (!TryGetNode(model, nodeA, out pa) || !TryGetNode(model, nodeB, out pb)) return false;

            RingSpot spotA, spotB;
            if (!Locate(ring, nodeA, pa, out spotA) || !Locate(ring, nodeB, pb, out spotB))
            {
                reason = "a cutting line's end does not lie on the panel contour.";
                return false;
            }

            if (RunsAlongContour(spotA, spotB, ring.Nodes.Count))
            {
                reason = "a cutting line runs along the contour, so it does not cut the panel in two.";
                return false;
            }

            RNode mid = new RNode { x = (pa.x + pb.x) / 2, y = (pa.y + pb.y) / 2, z = (pa.z + pb.z) / 2 };
            if (!InsidePolygon(ring.Points, mid))
            {
                reason = "a cutting line runs outside the panel.";
                return false;
            }

            // Turn ends that sit part-way along an edge into real corners.
            if (spotA.Edge >= 0 && !SplitEdge(model, ring.Lines[spotA.Edge], nodeA, settings.Ancestor)) { reason = "AxisVM could not split a contour edge."; return false; }
            if (spotB.Edge >= 0)
            {
                // The first split renumbered the lines, so find B's edge again by geometry.
                if (spotA.Edge >= 0)
                {
                    if (!BuildRing(model, PanelIndex(model, panelUid), out ring) || !Locate(ring, nodeB, pb, out spotB) || spotB.Edge < 0)
                    { reason = "the contour changed unexpectedly while it was being cut."; return false; }
                }
                if (!SplitEdge(model, ring.Lines[spotB.Edge], nodeB, settings.Ancestor)) { reason = "AxisVM could not split a contour edge."; return false; }
            }

            if (spotA.Edge >= 0 || spotB.Edge >= 0)
            {
                if (!BuildRing(model, PanelIndex(model, panelUid), out ring)) { reason = "the contour changed unexpectedly while it was being cut."; return false; }
                splitterId = LineIndex(model, splitterUid); // splitting renumbered the lines
                if (splitterId <= 0) return false;
            }

            int ia = ring.Nodes.IndexOf(nodeA);
            int ib = ring.Nodes.IndexOf(nodeB);
            if (ia < 0 || ib < 0) { reason = "the contour changed unexpectedly while it was being cut."; return false; }

            int n = ring.Nodes.Count;
            List<int> loop1 = EdgesBetween(ring.Lines, ia, ib, n);
            List<int> loop2 = EdgesBetween(ring.Lines, ib, ia, n);
            loop1.Add(splitterId);
            loop2.Add(splitterId);

            int panelA = CreatePiece(model, loop1, splitterId, settings);
            if (panelA <= 0) { reason = "AxisVM rejected one of the two pieces."; return false; }

            int panelB = CreatePiece(model, loop2, splitterId, settings);
            if (panelB <= 0)
            {
                try { model.LoadPanels.Delete(panelA); } catch { } // do not leave half a split behind
                reason = "AxisVM rejected one of the two pieces.";
                return false;
            }

            newUidA = model.LoadPanels.get_UID(panelA);
            newUidB = model.LoadPanels.get_UID(panelB);
            return true;
        }

        /// <summary>
        /// True when the two ends share a contour edge (or are the same corner): such a line lies along the
        /// boundary and encloses no area on one side.
        /// </summary>
        private static bool RunsAlongContour(RingSpot a, RingSpot b, int n)
        {
            if (a.Corner >= 0 && b.Corner >= 0)
                return a.Corner == b.Corner || (a.Corner + 1) % n == b.Corner || (b.Corner + 1) % n == a.Corner;
            if (a.Edge >= 0 && b.Edge >= 0) return a.Edge == b.Edge;

            RingSpot corner = a.Corner >= 0 ? a : b;
            RingSpot edge = a.Corner >= 0 ? b : a;
            return corner.Corner == edge.Edge || corner.Corner == (edge.Edge + 1) % n;
        }

        /// <summary>Contour edges walked forward from corner <paramref name="from"/> up to corner <paramref name="to"/>.</summary>
        private static List<int> EdgesBetween(List<int> ringLines, int from, int to, int n)
        {
            List<int> result = new List<int>();
            for (int i = from; i != to; i = (i + 1) % n) result.Add(ringLines[i]);
            return result;
        }

        /// <summary>Walks the panel's contour lines into an ordered ring by chaining shared end nodes.</summary>
        private static bool BuildRing(IAxisVMModel model, int panelId, out Ring ring)
        {
            ring = new Ring();
            if (panelId <= 0) return false;

            List<int> ids = ContourLineIds(model, panelId);
            if (ids.Count < 3) return false;

            Dictionary<int, int> startOf = new Dictionary<int, int>();
            Dictionary<int, int> endOf = new Dictionary<int, int>();
            foreach (int id in ids)
            {
                try
                {
                    IAxisVMLine line = model.Lines.Item[id];
                    startOf[id] = line.StartNode;
                    endOf[id] = line.EndNode;
                }
                catch { return false; }
            }

            HashSet<int> unused = new HashSet<int>(ids);
            int first = ids[0];
            unused.Remove(first);
            ring.Lines.Add(first);
            ring.Nodes.Add(startOf[first]);
            int current = endOf[first];

            while (unused.Count > 0)
            {
                int next = 0, other = 0;
                foreach (int id in ids)
                {
                    if (!unused.Contains(id)) continue;
                    if (startOf[id] == current) { next = id; other = endOf[id]; break; }
                    if (endOf[id] == current) { next = id; other = startOf[id]; break; }
                }
                if (next == 0) return false; // not a single closed loop

                unused.Remove(next);
                ring.Lines.Add(next);
                ring.Nodes.Add(current);
                current = other;
            }

            if (current != ring.Nodes[0]) return false;

            foreach (int nodeId in ring.Nodes)
            {
                RNode p;
                if (!TryGetNode(model, nodeId, out p)) return false;
                ring.Points.Add(p);
            }
            return true;
        }

        private static bool Locate(Ring ring, int nodeId, RNode p, out RingSpot spot)
        {
            spot = new RingSpot { Corner = ring.Nodes.IndexOf(nodeId), Edge = -1 };
            if (spot.Corner >= 0) return true;

            int n = ring.Points.Count;
            for (int k = 0; k < n; k++)
            {
                if (!StrictlyOnSegment(ring.Points[k], ring.Points[(k + 1) % n], p)) continue;
                spot.Edge = k;
                return true;
            }
            return false;
        }

        /// <summary>Splits a contour edge at a node and records both halves as descendants of the original edge.</summary>
        private static bool SplitEdge(IAxisVMModel model, int edgeId, int nodeId, Dictionary<int, int> ancestor)
        {
            int rootUid;
            try { rootUid = model.Lines.get_UID(edgeId); } catch { return false; }
            int known;
            if (ancestor.TryGetValue(rootUid, out known)) rootUid = known;

            int part1, part2;
            try { model.Lines.Item[edgeId].SplitByNode(nodeId, out part1, out part2); }
            catch { return false; }
            if (part1 <= 0 || part2 <= 0) return false;

            // SplitByNode returns the halves' indices after renumbering.
            try
            {
                ancestor[model.Lines.get_UID(part1)] = rootUid;
                ancestor[model.Lines.get_UID(part2)] = rootUid;
            }
            catch { return false; }
            return true;
        }

        /// <summary>Creates one piece and carries the original's load distribution settings across.</summary>
        private static int CreatePiece(IAxisVMModel model, List<int> loopLineIds, int splitterId, PieceSettings settings)
        {
            Array ids = ToComArray(loopLineIds);
            int panelId;
            try { panelId = model.LoadPanels.AddFromLineIDs(ref ids); }
            catch { return 0; }
            if (panelId <= 0) return 0;

            try
            {
                IAxisVMLoadPanel panel = model.LoadPanels.Item[panelId];
                panel.Auto = settings.Auto;
                if (settings.Auto == ELongBoolean.lbTrue) return panelId; // AxisVM works the edges out itself

                // Load-carrying edges: whichever of this piece's edges carried load on the original, plus the
                // cutting line when it is a real member (a beam or rib splitting a panel picks up load).
                // Only SetLines drives AxisVM's distribution - the per-edge SelectedEdge flags do not.
                List<int> loaded = new List<int>();
                foreach (int lineId in loopLineIds)
                {
                    if (lineId == splitterId)
                    {
                        if (IsBeamOrRib(model, lineId)) loaded.Add(lineId);
                        continue;
                    }

                    int uid;
                    try { uid = model.Lines.get_UID(lineId); } catch { continue; }
                    int root;
                    if (settings.Ancestor.TryGetValue(uid, out root)) uid = root;
                    if (settings.LoadedUids.Contains(uid)) loaded.Add(lineId);
                }

                if (loaded.Count > 0)
                {
                    Array loadedArr = ToComArray(loaded);
                    panel.SetLines(ref loadedArr);
                }
            }
            catch { }

            return panelId;
        }

        private static HashSet<int> ReadLoadedEdgeUids(IAxisVMModel model, int panelId)
        {
            HashSet<int> result = new HashSet<int>();
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
        // Loads
        // ==================================================================

        /// <summary>
        /// Uniform / linear area loads sitting on a panel, as records ready for AddLoadPanelLinear. There is
        /// no typed getter for them; GetLoad returns the record bytes, and a 120-byte record whose case and
        /// panel fields match the load's own is decoded as RLoadPanelLinear. Anything else on the panel is
        /// only counted, so the caller can say it was not carried over.
        /// </summary>
        private static List<RLoadPanelLinear> ReadPanelLinearLoads(IAxisVMModel model, int panelId, out int otherLoads)
        {
            List<RLoadPanelLinear> result = new List<RLoadPanelLinear>();
            otherLoads = 0;

            int count = 0;
            try { count = model.Loads.Count; } catch { return result; }

            for (int i = 1; i <= count; i++)
            {
                if (!LoadSitsOnPanel(model, i, panelId)) continue;

                byte[] bytes = LoadBytes(model, i);
                int caseId = 0;
                try { caseId = model.Loads.get_LoadCaseId(i); } catch { }

                if (bytes != null && bytes.Length == PanelLinearLoadBytes &&
                    BitConverter.ToInt32(bytes, 0) == caseId && BitConverter.ToInt32(bytes, 4) == panelId)
                {
                    RLoadPanelLinear d = new RLoadPanelLinear();
                    d.LoadCaseId = caseId;
                    d.LoadPanelId = panelId;
                    d.DistributionType = (EDistributionType)BitConverter.ToInt32(bytes, 8);
                    d.LoadDistributionType = (ELoadDistributionType)BitConverter.ToInt32(bytes, 12);
                    d.Component = (EAxis)BitConverter.ToInt32(bytes, 16);
                    // 4 bytes of padding align the doubles on 8.
                    d.P1 = BitConverter.ToDouble(bytes, 24);
                    d.P2 = BitConverter.ToDouble(bytes, 32);
                    d.P3 = BitConverter.ToDouble(bytes, 40);
                    d.x1 = BitConverter.ToDouble(bytes, 48);
                    d.x2 = BitConverter.ToDouble(bytes, 56);
                    d.x3 = BitConverter.ToDouble(bytes, 64);
                    d.y1 = BitConverter.ToDouble(bytes, 72);
                    d.y2 = BitConverter.ToDouble(bytes, 80);
                    d.y3 = BitConverter.ToDouble(bytes, 88);
                    d.z1 = BitConverter.ToDouble(bytes, 96);
                    d.z2 = BitConverter.ToDouble(bytes, 104);
                    d.z3 = BitConverter.ToDouble(bytes, 112);
                    result.Add(d);
                }
                else
                {
                    otherLoads++;
                }
            }
            return result;
        }

        /// <summary>
        /// Panel loads report no ELoadType of their own (-1) and carry the panel index as ElementId. Domain,
        /// beam and nodal loads all have a real type, so the pair identifies a panel load.
        /// </summary>
        private static bool LoadSitsOnPanel(IAxisVMModel model, int loadIndex, int panelId)
        {
            try
            {
                return (int)model.Loads.get_LoadType(loadIndex) == -1 && model.Loads.get_ElementId(loadIndex) == panelId;
            }
            catch { return false; }
        }

        /// <summary>Loads whose element is one of the given domains (any domain load type).</summary>
        private static int CountDomainLoads(IAxisVMModel model, List<int> domainIds)
        {
            int count = 0, total = 0;
            try { total = model.Loads.Count; } catch { return 0; }

            HashSet<int> domains = new HashSet<int>(domainIds);
            for (int i = 1; i <= total; i++)
            {
                try
                {
                    string type = model.Loads.get_LoadType(i).ToString();
                    if (type.StartsWith("ltDomain") && domains.Contains(model.Loads.get_ElementId(i))) count++;
                }
                catch { }
            }
            return count;
        }

        private static byte[] LoadBytes(IAxisVMModel model, int loadIndex)
        {
            Array data = null;
            try { model.Loads.GetLoad(loadIndex, out data); } catch { return null; }
            if (data == null) return null;

            byte[] bytes = new byte[data.Length];
            int lower = data.GetLowerBound(0);
            for (int b = 0; b < bytes.Length; b++) bytes[b] = Convert.ToByte(data.GetValue(lower + b));
            return bytes;
        }

        // ==================================================================
        // Shared helpers
        // ==================================================================

        private static int PanelIndex(IAxisVMModel model, int uid)
        {
            try { return model.LoadPanels.get_IndexOfUID(uid); } catch { return 0; }
        }

        private static int LineIndex(IAxisVMModel model, int uid)
        {
            try { return model.Lines.get_IndexOfUID(uid); } catch { return 0; }
        }

        private static HashSet<int> AllPanelUids(IAxisVMModel model)
        {
            HashSet<int> uids = new HashSet<int>();
            int count = 0;
            try { count = model.LoadPanels.Count; } catch { }
            for (int i = 1; i <= count; i++)
            {
                try { uids.Add(model.LoadPanels.get_UID(i)); } catch { }
            }
            return uids;
        }

        /// <summary>The panel's contour lines, or an empty list for a user-defined polygon (no lines behind it).</summary>
        private static List<int> ContourLineIds(IAxisVMModel model, int panelId)
        {
            Array arr = null;
            try
            {
                if (model.LoadPanels.Item[panelId].GetContourLineIDs(ref arr) <= 0) return new List<int>();
            }
            catch { return new List<int>(); }
            return ToIdList(arr);
        }

        /// <summary>Marks the given panels and removes them with one DeleteSelected call.</summary>
        private static int DeletePanels(IAxisVMModel model, List<int> panelUids)
        {
            try { model.LoadPanels.SelectAll(ELongBoolean.lbFalse); } catch { }

            int marked = 0;
            foreach (int uid in panelUids)
            {
                int idx = PanelIndex(model, uid);
                if (idx <= 0) continue;
                try { model.LoadPanels.set_Selected(idx, ELongBoolean.lbTrue); marked++; } catch { }
            }

            if (marked == 0) return 0;
            try { model.LoadPanels.DeleteSelected(); } catch { return 0; }

            int gone = 0;
            foreach (int uid in panelUids)
            {
                if (PanelIndex(model, uid) <= 0) gone++;
            }
            return gone;
        }

        private static bool TryGetNode(IAxisVMModel model, int nodeId, out RNode p)
        {
            p = new RNode();
            try { return model.Nodes.GetNode(nodeId, ref p) > 0; } catch { return false; }
        }

        /// <summary>True when <paramref name="p"/> lies on segment a-b, strictly between its ends.</summary>
        private static bool StrictlyOnSegment(RNode a, RNode b, RNode p)
        {
            double abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
            double len2 = abx * abx + aby * aby + abz * abz;
            if (len2 < 1e-12) return false;

            double t = ((p.x - a.x) * abx + (p.y - a.y) * aby + (p.z - a.z) * abz) / len2;
            double len = Math.Sqrt(len2);
            if (t * len <= OnEdgeToleranceM || (1.0 - t) * len <= OnEdgeToleranceM) return false;

            double cx = a.x + abx * t - p.x, cy = a.y + aby * t - p.y, cz = a.z + abz * t - p.z;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz) <= OnEdgeToleranceM;
        }

        /// <summary>
        /// Point-in-polygon test in the contour's own plane: the polygon is projected by dropping the axis its
        /// normal points along most. Guards concave panels, where a chord between two corners can run outside.
        /// </summary>
        private static bool InsidePolygon(List<RNode> poly, RNode p)
        {
            double nx = 0, ny = 0, nz = 0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                RNode a = poly[i], b = poly[(i + 1) % n];
                nx += (a.y - b.y) * (a.z + b.z);
                ny += (a.z - b.z) * (a.x + b.x);
                nz += (a.x - b.x) * (a.y + b.y);
            }

            double ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
            Func<RNode, double> u, v;
            if (az >= ax && az >= ay) { u = q => q.x; v = q => q.y; }
            else if (ay >= ax) { u = q => q.x; v = q => q.z; }
            else { u = q => q.y; v = q => q.z; }

            bool inside = false;
            double pu = u(p), pv = v(p);
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double ui = u(poly[i]), vi = v(poly[i]), uj = u(poly[j]), vj = v(poly[j]);
                if ((vi > pv) != (vj > pv) && pu < (uj - ui) * (pv - vi) / (vj - vi) + ui) inside = !inside;
            }
            return inside;
        }

        private static List<int> ToIdList(Array arr)
        {
            List<int> ids = new List<int>();
            if (arr == null) return ids;

            for (int i = arr.GetLowerBound(0); i <= arr.GetUpperBound(0); i++)
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
    }
}
