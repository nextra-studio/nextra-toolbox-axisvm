using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Plain, UI-free copy of the settings the one-way generator needs. Each addon fills this in
    /// from its own dialog, so <see cref="OneWayPanelBuilder"/> never has to reference a Form type.
    /// </summary>
    internal struct OneWayPanelOptions
    {
        public int SpanningDirection;    // 0 = Auto (short), 1 = Dir 1, 2 = Dir 2
        public bool NoSpacing;           // true = a single panel over the whole boundary
        public double EdgeSpacingMm;     // edge-strip tributary width [mm] (ignored when NoSpacing)
        public double AngleToleranceDeg; // chaining / classification tolerance [deg]

        // Optional explicit span direction (used by the Rhino import, where "edge 1" means nothing
        // to the user). When UseSpanAngle is true, SpanningDirection is ignored and the slab spans
        // along SpanAngleDeg, measured in the global XY plane from +X towards +Y.
        public bool UseSpanAngle;
        public double SpanAngleDeg;

        internal static bool TryParseNumber(string text, out double value)
        {
            // Do not accept thousands separators: under nl-NL, "15.0" otherwise becomes 150.
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                   IsFinite(value);
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal string Validate()
        {
            if (!IsFinite(AngleToleranceDeg) || AngleToleranceDeg < 0 || AngleToleranceDeg >= 45)
                return "Angle tolerance must be between 0 (inclusive) and 45 (exclusive) degrees.";
            if (!NoSpacing && (!IsFinite(EdgeSpacingMm) || EdgeSpacingMm <= 0.001))
                return "Edge spacing must be finite and greater than 0.001 mm.";
            if (UseSpanAngle && !IsFinite(SpanAngleDeg)) return "Span angle must be finite.";
            if (!UseSpanAngle && (SpanningDirection < 0 || SpanningDirection > 2))
                return "Unknown spanning direction.";
            return null;
        }
    }

    /// <summary>
    /// The shared geometry engine for the Lines -> One-Way Panel addon, the Convert Load Panel
    /// -> One-Way addon, and Rhino import.
    ///
    /// It takes a set of connected boundary segments, reconstructs the physically-straight sides
    /// with <see cref="EdgeChainBuilder"/> (so a side split into several collinear beams is handled),
    /// classifies them into support edges / edge beams, and creates either a single one-way load
    /// panel or the two-edge-strip + centre-panel set.
    ///
    /// Nothing here shows UI or reads a selection - the addons do that and call
    /// <see cref="GeneratePanels"/>.
    /// </summary>
    internal static class OneWayPanelBuilder
    {
        // ==================================================================
        // Model reads
        // ==================================================================

        // A resolved line: its two endpoint nodes, fetched directly via IAxisVMLine.StartNode/EndNode
        // rather than via BulkGetLineData's RLineData array. The bulk path was silently handing back
        // zeroed-out node ids for every line in testing (every line's angle collapsed to 0 deg,
        // which is why edge spacing kept classifying 0 lines as support edges) - fetching per line
        // through the plain, well-documented Item[]/StartNode/EndNode properties avoids that.
        // (ResolvedLine itself lives in EdgeChainBuilder.cs.)
        internal static ResolvedLine ResolveLine(IAxisVMModel model, int lineId)
        {
            var line = model.Lines.Item[lineId];
            return new ResolvedLine { LineId = lineId, NodeId1 = line.StartNode, NodeId2 = line.EndNode };
        }

        private static AxisVM.RNode GetNode(IAxisVMModel model, int nodeId)
        {
            AxisVM.RNode n = new AxisVM.RNode();
            model.Nodes.GetNode(nodeId, ref n);
            return n;
        }

        // ==================================================================
        // Angle helpers
        // ==================================================================

        /// <summary>
        /// True when a side whose chord direction is <paramref name="edgeAngleDeg"/> runs
        /// perpendicular (within tolerance) to the span direction <paramref name="spanAngleDeg"/>.
        /// Both angles are folded into [0,180) first, so the sense of each direction is irrelevant.
        /// </summary>
        private static bool IsAnglePerpendicular(double edgeAngleDeg, double spanAngleDeg, double toleranceDeg)
        {
            double lineAngle = (edgeAngleDeg % 180.0 + 180.0) % 180.0;
            double s = (spanAngleDeg % 180.0 + 180.0) % 180.0;

            double targetAngle = (s + 90.0) % 180.0;

            double diff = Math.Abs(lineAngle - targetAngle);
            if (diff > 90.0) diff = 180.0 - diff;

            return diff <= toleranceDeg;
        }

        // Determines the spanning direction angle (deg) from the first logical edge and the
        // user's Auto/Dir1/Dir2 choice.
        private static double ComputeSpanAngle(List<LogicalEdge> edges, OneWayPanelOptions opt)
        {
            LogicalEdge first = edges[0];
            double dir1Angle = first.AngleDeg;
            double dir1Length = first.Length;

            if (opt.UseSpanAngle) // explicit global direction
            {
                return opt.SpanAngleDeg;
            }

            if (opt.SpanningDirection == 1) // Dir 1
            {
                return dir1Angle;
            }
            else if (opt.SpanningDirection == 2) // Dir 2
            {
                return dir1Angle + 90.0;
            }
            else // Auto (Short)
            {
                double dir2Length = dir1Length;
                for (int i = 1; i < edges.Count; i++)
                {
                    if (IsAnglePerpendicular(edges[i].AngleDeg, dir1Angle, 45.0))
                    {
                        dir2Length = edges[i].Length;
                        break;
                    }
                }
                return (dir1Length <= dir2Length) ? dir1Angle : dir1Angle + 90.0;
            }
        }

        /// <summary>
        /// Splits the reconstructed sides into support edges (perpendicular to the span - the slab
        /// distributes its load onto these) and edge beams (parallel to the span - they get no load
        /// from a one-way panel).
        /// </summary>
        private static void ClassifyEdges(List<LogicalEdge> edges, double spanAngle, double angleTolerance,
                                          out List<LogicalEdge> supportEdges, out List<LogicalEdge> beamEdges)
        {
            supportEdges = new List<LogicalEdge>();
            beamEdges = new List<LogicalEdge>();
            foreach (LogicalEdge e in edges)
            {
                if (IsAnglePerpendicular(e.AngleDeg, spanAngle, angleTolerance)) supportEdges.Add(e);
                else beamEdges.Add(e);
            }
        }

        // ==================================================================
        // Panel creation
        // ==================================================================

        private static int CreatePanelFromLines(IAxisVMModel model, List<int> lineIds, HashSet<int> supportLineIds)
        {
            if (lineIds.Count < 3 || supportLineIds.Count == 0) return 0;
            Array idsArray = Array.CreateInstance(typeof(int), new int[] { lineIds.Count }, new int[] { 1 });
            for (int i = 0; i < lineIds.Count; i++) idsArray.SetValue(lineIds[i], i + 1);

            int panelId = model.LoadPanels.AddFromLineIDs(ref idsArray);
            if (panelId <= 0) return panelId;
            try
            {
                var lp = model.LoadPanels.Item[panelId];
                if (lp != null)
                {
                    lp.Auto = (ELongBoolean)0;

                    // SetLines takes a plain array of line IDs (same 1-based convention as
                    // AddFromLineIDs, no count prefix) and tells the panel which of its boundary
                    // lines actually receive the distributed load - this is the one-way behavior:
                    // only the support edges get load, the edge beams get none.
                    if (supportLineIds.Count > 0)
                    {
                        Array supportArray = Array.CreateInstance(typeof(int), new int[] { supportLineIds.Count }, new int[] { 1 });
                        int idx = 1;
                        foreach (int id in supportLineIds) supportArray.SetValue(id, idx++);
                        int result = lp.SetLines(ref supportArray);
                        if (result != supportLineIds.Count)
                            throw new InvalidOperationException("AxisVM rejected the receiving lines (code " + result + ").");

                        // AxisVM's own one-way distribution algorithm reads per-edge "SelectedEdge"
                        // flags (indexed by the panel's contour edge order, per GetContourLineIDs),
                        // separately from SetLines. Set both.
                        Array contourIds = null;
                        lp.GetContourLineIDs(ref contourIds);
                        if (contourIds != null)
                        {
                            int lower = contourIds.GetLowerBound(0);
                            for (int i = 0; i < contourIds.Length; i++)
                            {
                                int contourLineId = (int)contourIds.GetValue(lower + i);
                                lp.set_SelectedEdge(i + 1, supportLineIds.Contains(contourLineId) ? (ELongBoolean)1 : (ELongBoolean)0);
                            }
                        }
                        else throw new InvalidOperationException("AxisVM returned no contour lines for the new panel.");
                    }
                }
                else throw new InvalidOperationException("AxisVM returned no object for the new panel.");
            }
            catch
            {
                // A created but unconfigured panel must never be reported as a one-way success.
                if (model.LoadPanels.Delete(panelId) <= 0)
                    throw new InvalidOperationException("Could not remove a panel after failed one-way configuration.");
                throw;
            }
            return panelId;
        }

        // ==================================================================
        // The generator
        // ==================================================================

        /// <summary>Reject ambiguous graphs and geometry unsupported by the XY span calculation.</summary>
        internal static string ValidateBoundary(IAxisVMModel model, List<ResolvedLine> segments)
        {
            if (segments.Count < 3) return "at least three boundary segments are required";
            HashSet<int> lineIds = new HashSet<int>();
            Dictionary<int, int> degree = new Dictionary<int, int>();
            double elevation = GetNode(model, segments[0].NodeId1).z;
            foreach (ResolvedLine segment in segments)
            {
                if (!lineIds.Add(segment.LineId)) return "a boundary line was supplied more than once";
                if (segment.NodeId1 == segment.NodeId2) return "a boundary segment has identical endpoints";
                if (model.Lines.Item[segment.LineId].GeomType != ELineGeomType.lgtStraightLine)
                    return "only straight boundary lines are supported";
                RNode a = GetNode(model, segment.NodeId1), b = GetNode(model, segment.NodeId2);
                if (EdgeChainBuilder.Distance(a, b) <= 1e-9) return "a boundary segment has zero length";
                if (Math.Abs(a.z - elevation) > 1e-6 || Math.Abs(b.z - elevation) > 1e-6)
                    return "only horizontal boundaries are supported by the XY spanning-direction calculation";
                foreach (int node in new int[] { segment.NodeId1, segment.NodeId2 })
                {
                    int count;
                    degree.TryGetValue(node, out count);
                    degree[node] = count + 1;
                }
            }
            foreach (int count in degree.Values)
                if (count != 2) return "lines must form one closed loop without branches; select adjacent bays separately";
            if (GroupByConnectivity(segments).Count != 1) return "boundary contains disconnected loops";
            return null;
        }

        /// <summary>
        /// Builds the load panel(s) for one boundary (a set of connected segments). Returns the UID
        /// of every load panel created - one for the no-spacing case, three for the edge-strip case,
        /// or none if AxisVM rejected the contour. Human-readable notes are appended to
        /// <paramref name="notes"/> whenever something was downgraded or failed.
        /// </summary>
        internal static List<int> GeneratePanels(IAxisVMModel model, List<ResolvedLine> segments, OneWayPanelOptions opt,
            int label, List<string> notes, List<int> currentBoundaryUids = null)
        {
            List<int> created = new List<int>();
            if (segments == null || segments.Count == 0)
            {
                notes.Add(string.Format("Boundary {0}: no lines.", label));
                return created;
            }

            string invalid = opt.Validate() ?? ValidateBoundary(model, segments);
            if (invalid != null)
            {
                notes.Add(string.Format("Boundary {0}: skipped ({1}).", label, invalid));
                return created;
            }

            List<LogicalEdge> edges = EdgeChainBuilder.Build(model, segments, opt.AngleToleranceDeg, notes);
            if (edges.Count == 0)
            {
                notes.Add(string.Format("Boundary {0}: could not reconstruct any edges.", label));
                return created;
            }

            double spanAngle = ComputeSpanAngle(edges, opt);

            // ---- With-spacing path: two edge strips + one centre panel -------------------------
            if (!opt.NoSpacing)
            {
                double spacingM = opt.EdgeSpacingMm / 1000.0;
                string failureReason;
                List<int> spacedUids;
                bool spaced;
                try
                {
                    spaced = TryCreateSpacedPanels(model, edges, spanAngle, opt.AngleToleranceDeg, spacingM, out spacedUids, out failureReason);
                }
                finally
                {
                    // A converter may need to restore its original panel even when a COM call throws.
                    if (currentBoundaryUids != null)
                    {
                        currentBoundaryUids.Clear();
                        foreach (LogicalEdge edge in edges) currentBoundaryUids.AddRange(edge.SegmentUids);
                    }
                }
                if (spaced)
                {
                    return spacedUids; // two edge strips + one centre panel
                }

                notes.Add(string.Format("Boundary {0}: edge spacing not applied ({1}) - fell back to a single panel.",
                    label, Summarise(failureReason)));

                // Splitting DELETES the original UID. The logical edges now contain both child
                // UIDs, updated after each successful cut, so the fallback retains the full contour.
            }

            // ---- No-spacing path: one panel over the whole boundary --------------------------
            List<int> allIds = FlattenSegmentIds(model, edges);
            List<LogicalEdge> supportEdges, beamEdges;
            ClassifyEdges(edges, spanAngle, opt.AngleToleranceDeg, out supportEdges, out beamEdges);
            HashSet<int> supportIds = new HashSet<int>(FlattenSegmentIds(model, supportEdges));
            if (supportIds.Count == 0)
            {
                notes.Add(string.Format("Boundary {0}: no receiving edges perpendicular to the span; no panel created.", label));
                return created;
            }

            int panelId = CreatePanelFromLines(model, allIds, supportIds);
            if (panelId > 0)
            {
                created.Add(model.LoadPanels.get_UID(panelId));
                return created;
            }

            notes.Add(string.Format("Boundary {0}: AxisVM rejected the contour ({1} segment(s)). Check that the lines form a closed loop.",
                label, segments.Count));
            return created;
        }

        /// <summary>Resolves every segment UID of the given edges to a current line index.</summary>
        private static List<int> FlattenSegmentIds(IAxisVMModel model, IEnumerable<LogicalEdge> edges)
        {
            List<int> ids = new List<int>();
            foreach (LogicalEdge e in edges)
            {
                foreach (int uid in e.SegmentUids)
                {
                    int id = model.Lines.get_IndexOfUID(uid);
                    if (id <= 0) throw new InvalidOperationException("A boundary segment no longer exists; cannot create an incomplete contour.");
                    ids.Add(id);
                }
            }
            return ids;
        }

        // ==================================================================
        // With-spacing generation on polyline sides
        // ==================================================================

        /// <summary>
        /// Insets each support edge by <paramref name="spacingM"/> from both of its corners, cuts two
        /// edge strips (feeding their tributary load straight to the parallel edge beam) and one
        /// centre strip that still spans between the two support edges. Every support edge may be a
        /// polyline of several real beam segments; only the segment that straddles an inset point is
        /// split.
        ///
        /// Returns false (and, on a failure after work has started, cleans up any panels it made)
        /// when the boundary is not a clean 2-support + 2-beam quad or the spacing does not fit - the
        /// caller then falls back to a single panel.
        /// </summary>
        private static bool TryCreateSpacedPanels(IAxisVMModel model, List<LogicalEdge> edges, double spanAngle,
                                                  double angleTolerance, double spacingM,
                                                  out List<int> createdPanelUids, out string failureReason)
        {
            createdPanelUids = new List<int>();
            List<int> insetLineUids = new List<int>();
            bool success = false;
            try
            {
                success = TryCreateSpacedPanelsCore(model, edges, spanAngle, angleTolerance, spacingM,
                    createdPanelUids, insetLineUids, out failureReason);
                return success;
            }
            finally
            {
                if (!success)
                {
                    // Keep successful beam splits (their attributes belong to AxisVM), but remove
                    // temporary panels and inset lines before attempting the single-panel fallback.
                    CleanupPanels(model, createdPanelUids);
                    foreach (int uid in insetLineUids)
                    {
                        int id = model.Lines.get_IndexOfUID(uid);
                        if (id > 0 && model.Lines.Delete(id) <= 0)
                            throw new InvalidOperationException("Could not remove an inset line after failed edge spacing.");
                    }
                }
            }
        }

        private static bool TryCreateSpacedPanelsCore(IAxisVMModel model, List<LogicalEdge> edges, double spanAngle,
            double angleTolerance, double spacingM, List<int> createdPanelUids, List<int> insetLineUids,
            out string failureReason)
        {
            failureReason = null;

            // ---- 1. classify -------------------------------------------------------------------
            List<LogicalEdge> supportEdges, beamEdges;
            ClassifyEdges(edges, spanAngle, angleTolerance, out supportEdges, out beamEdges);

            if (supportEdges.Count != 2 || beamEdges.Count != 2)
            {
                System.Text.StringBuilder detail = new System.Text.StringBuilder();
                detail.AppendFormat(
                    "Reconstructed {0} side(s): {1} classified as support edges (perpendicular to the span) and {2} as edge beams. Edge spacing needs exactly 2 of each.\n\nspanAngle = {3:F2} deg\n",
                    edges.Count, supportEdges.Count, beamEdges.Count, spanAngle);
                foreach (LogicalEdge e in edges)
                {
                    detail.AppendFormat("  side: nodes {0}->{1}, {2} segment(s), angle {3:F2} deg, length {4:F3} m\n",
                        e.NodeA, e.NodeB, e.SegmentUids.Count, e.AngleDeg, e.Length);
                }
                failureReason = detail.ToString();
                return false;
            }

            // ---- 2. length guard --------------------------------------------------------------
            foreach (LogicalEdge s in supportEdges)
            {
                if (s.Length <= 2.0 * spacingM + 1e-6)
                {
                    failureReason = "A support edge is not long enough to fit two edge strips of that spacing.";
                    return false;
                }
            }

            // ---- 3. corner topology ---------------------------------------------------------
            Dictionary<int, LogicalEdge> supportByCorner = new Dictionary<int, LogicalEdge>();
            foreach (LogicalEdge s in supportEdges)
            {
                supportByCorner[s.NodeA] = s;
                supportByCorner[s.NodeB] = s;
            }
            foreach (LogicalEdge b in beamEdges)
            {
                if (!supportByCorner.ContainsKey(b.NodeA) || !supportByCorner.ContainsKey(b.NodeB))
                {
                    failureReason = "The four sides do not meet at shared corners (open contour).";
                    return false;
                }
            }

            // ---- 4. inset each support edge from both ends ---------------------------------
            Dictionary<LogicalEdge, List<int>> middleRun = new Dictionary<LogicalEdge, List<int>>();
            Dictionary<int, List<int>> endRunByCorner = new Dictionary<int, List<int>>();
            Dictionary<int, int> insetNodeByCorner = new Dictionary<int, int>();

            foreach (LogicalEdge s in supportEdges)
            {
                // Walk from NodeA, split at spacingM -> [endRun near A] | [rest towards B].
                List<int> nearA, rest;
                int insetA;
                string err;
                if (!SplitPolylineAt(model, s.SegmentUids, s.NodeA, spacingM, out nearA, out rest, out insetA, out err))
                {
                    failureReason = "Could not inset a support edge near a corner (" + err + ").";
                    return false;
                }
                s.SegmentUids = JoinRuns(nearA, rest);

                // Walk the rest from NodeB, split at spacingM -> [endRun near B] | [middle run].
                List<int> nearB, middle;
                int insetB;
                if (!SplitPolylineAt(model, rest, s.NodeB, spacingM, out nearB, out middle, out insetB, out err))
                {
                    failureReason = "Could not inset a support edge near the opposite corner (" + err + ").";
                    return false;
                }
                // The second walk runs backwards: reverse its UID runs to preserve NodeA -> NodeB.
                middle.Reverse();
                nearB.Reverse();
                s.SegmentUids = JoinRuns(nearA, JoinRuns(middle, nearB));

                middleRun[s] = middle;
                endRunByCorner[s.NodeA] = nearA;
                endRunByCorner[s.NodeB] = nearB;
                insetNodeByCorner[s.NodeA] = insetA;
                insetNodeByCorner[s.NodeB] = insetB;
            }

            // ---- 5. one inset line per edge beam ------------------------------------------
            Dictionary<LogicalEdge, int> insetLineUidByBeam = new Dictionary<LogicalEdge, int>();
            foreach (LogicalEdge b in beamEdges)
            {
                int i1 = insetNodeByCorner[b.NodeA];
                int i2 = insetNodeByCorner[b.NodeB];
                int lid = AddStraightLine(model, i1, i2);
                if (lid <= 0)
                {
                    failureReason = "Could not create an inset line parallel to an edge beam.";
                    return false;
                }
                insetLineUidByBeam[b] = model.Lines.get_UID(lid);
                insetLineUids.Add(insetLineUidByBeam[b]);
            }

            // ---- 6. edge strips ----------------------------------------------------------
            foreach (LogicalEdge b in beamEdges)
            {
                List<int> contour = new List<int>();
                List<int> beamSegs;
                if (!TryResolveUids(model, b.SegmentUids, out beamSegs) ||
                    !AppendResolved(model, endRunByCorner[b.NodeB], contour) ||
                    !AppendResolved(model, b.SegmentUids, contour))
                {
                    failureReason = "An edge strip contour segment went missing.";
                    return false;
                }
                int insetIdx = model.Lines.get_IndexOfUID(insetLineUidByBeam[b]);
                if (insetIdx <= 0 || !AppendResolved(model, endRunByCorner[b.NodeA], contour))
                {
                    failureReason = "An edge strip contour segment went missing.";
                    return false;
                }
                contour.Add(insetIdx);

                HashSet<int> support = new HashSet<int>(beamSegs);
                int stripId = CreatePanelFromLines(model, contour, support);
                if (stripId <= 0)
                {
                    failureReason = "AxisVM rejected an edge strip contour.";
                    return false;
                }
                createdPanelUids.Add(model.LoadPanels.get_UID(stripId));
            }

            // ---- 7. centre panel -------------------------------------------------------
            List<int> centre = new List<int>();
            int centreInset0 = model.Lines.get_IndexOfUID(insetLineUidByBeam[beamEdges[0]]);
            int centreInset1 = model.Lines.get_IndexOfUID(insetLineUidByBeam[beamEdges[1]]);
            if (centreInset0 <= 0 || centreInset1 <= 0)
            {
                failureReason = "A centre panel inset line went missing.";

                return false;
            }
            centre.Add(centreInset0);
            if (!AppendResolved(model, middleRun[supportEdges[0]], centre)) { failureReason = "A centre panel segment went missing."; return false; }
            centre.Add(centreInset1);
            if (!AppendResolved(model, middleRun[supportEdges[1]], centre)) { failureReason = "A centre panel segment went missing."; return false; }

            HashSet<int> centreSupport = new HashSet<int>();
            foreach (LogicalEdge s in supportEdges)
            {
                List<int> mids;
                if (!TryResolveUids(model, middleRun[s], out mids)) { failureReason = "A centre panel support segment went missing."; return false; }
                foreach (int id in mids) centreSupport.Add(id);
            }

            int centreId = CreatePanelFromLines(model, centre, centreSupport);
            if (centreId <= 0)
            {
                failureReason = "AxisVM rejected the centre panel contour.";

                return false;
            }
            createdPanelUids.Add(model.LoadPanels.get_UID(centreId));

            return true;
        }

        /// <summary>Deletes every panel whose UID is listed - used to undo a half-finished spaced split.</summary>
        private static void CleanupPanels(IAxisVMModel model, List<int> panelUids)
        {
            foreach (int uid in panelUids)
            {
                int idx = model.LoadPanels.get_IndexOfUID(uid);
                if (idx > 0 && model.LoadPanels.Delete(idx) <= 0)
                    throw new InvalidOperationException("Could not remove a partial load panel after failed edge spacing.");
            }
            panelUids.Clear();
        }

        private static List<int> JoinRuns(List<int> first, List<int> second)
        {
            List<int> joined = new List<int>(first.Count + second.Count);
            joined.AddRange(first);
            joined.AddRange(second);
            return joined;
        }

        // ------------------------------------------------------------------
        // Polyline walking / splitting
        // ------------------------------------------------------------------

        /// <summary>One segment of a support edge, oriented along the current walk.</summary>
        private struct OrientedSeg
        {
            public int Uid;
            public int StartNode;
            public int EndNode;
            public double Length;
        }

        /// <summary>
        /// Orders <paramref name="segUids"/> into a single chain that starts at
        /// <paramref name="startNode"/>. Returns false if they do not form one simple chain from
        /// that node (a missing segment, a branch, ...).
        /// </summary>
        private static bool OrientChain(IAxisVMModel model, List<int> segUids, int startNode, out List<OrientedSeg> chain, out string err)
        {
            chain = new List<OrientedSeg>();
            err = null;

            HashSet<int> remaining = new HashSet<int>(segUids);
            int current = startNode;

            while (remaining.Count > 0)
            {
                int pickUid = 0;
                ResolvedLine picked = default(ResolvedLine);
                bool found = false;

                foreach (int uid in remaining)
                {
                    int idx = model.Lines.get_IndexOfUID(uid);
                    if (idx <= 0) { err = "a support segment no longer exists"; return false; }
                    ResolvedLine r = ResolveLine(model, idx);
                    if (r.NodeId1 == current || r.NodeId2 == current)
                    {
                        pickUid = uid;
                        picked = r;
                        found = true;
                        break;
                    }
                }

                if (!found) { err = "support segments do not form a single chain"; return false; }

                int endNode = (picked.NodeId1 == current) ? picked.NodeId2 : picked.NodeId1;
                double len = EdgeChainBuilder.Distance(GetNode(model, current), GetNode(model, endNode));
                chain.Add(new OrientedSeg { Uid = pickUid, StartNode = current, EndNode = endNode, Length = len });

                remaining.Remove(pickUid);
                current = endNode;
            }

            return true;
        }

        /// <summary>
        /// Walks the polyline made of <paramref name="segUids"/> starting at
        /// <paramref name="startNode"/> and splits it <paramref name="dist"/> metres along.
        /// <paramref name="nearUids"/> is the run between the start and the split point,
        /// <paramref name="farUids"/> the run beyond it, and <paramref name="splitNode"/> the node at
        /// the cut (an existing interior node when the distance lands on one, otherwise a new node).
        /// Only the single segment that straddles the distance is ever cut.
        /// </summary>
        private static bool SplitPolylineAt(IAxisVMModel model, List<int> segUids, int startNode, double dist,
                                            out List<int> nearUids, out List<int> farUids, out int splitNode, out string err)
        {
            nearUids = new List<int>();
            farUids = new List<int>();
            splitNode = startNode;
            err = null;

            List<OrientedSeg> chain;
            if (!OrientChain(model, segUids, startNode, out chain, out err)) return false;

            double total = 0.0;
            foreach (OrientedSeg os in chain) total += os.Length;

            if (dist >= total - 1e-6) { err = "spacing does not fit on this side"; return false; }
            if (dist <= 1e-6)
            {
                farUids.AddRange(segUids);
                return true;
            }

            double cum = 0.0;
            for (int i = 0; i < chain.Count; i++)
            {
                OrientedSeg os = chain[i];

                if (cum + os.Length >= dist - 1e-9)
                {
                    double local = dist - cum; // distance into this segment from its start node

                    // Inset lands on this segment's start node - no cut, just partition.
                    if (local <= 1e-6)
                    {
                        splitNode = os.StartNode;
                        for (int k = 0; k < i; k++) nearUids.Add(chain[k].Uid);
                        for (int k = i; k < chain.Count; k++) farUids.Add(chain[k].Uid);
                        return true;
                    }

                    // Inset lands on this segment's end node - no cut, just partition.
                    if (local >= os.Length - 1e-6)
                    {
                        splitNode = os.EndNode;
                        for (int k = 0; k <= i; k++) nearUids.Add(chain[k].Uid);
                        for (int k = i + 1; k < chain.Count; k++) farUids.Add(chain[k].Uid);
                        return true;
                    }

                    // Interpolate the cut point and split this one segment there.
                    AxisVM.RNode p0 = GetNode(model, os.StartNode);
                    AxisVM.RNode p1 = GetNode(model, os.EndNode);
                    double t = local / os.Length;
                    int newNode = model.Nodes.Add(
                        p0.x + (p1.x - p0.x) * t,
                        p0.y + (p1.y - p0.y) * t,
                        p0.z + (p1.z - p0.z) * t);
                    if (newNode <= 0) { err = "could not create an inset node"; return false; }

                    int lineIdx = model.Lines.get_IndexOfUID(os.Uid);
                    int part1, part2;
                    if (lineIdx <= 0 || !TrySplitLine(model, lineIdx, newNode, out part1, out part2))
                    {
                        model.Nodes.Delete(newNode);
                        err = "SplitByNode failed on a support segment";
                        return false;
                    }

                    int nearPiece = LineTouchesNode(model, part1, os.StartNode) ? part1 : part2;
                    int farPiece = (nearPiece == part1) ? part2 : part1;

                    splitNode = newNode;
                    for (int k = 0; k < i; k++) nearUids.Add(chain[k].Uid);
                    nearUids.Add(model.Lines.get_UID(nearPiece));
                    farUids.Add(model.Lines.get_UID(farPiece));
                    for (int k = i + 1; k < chain.Count; k++) farUids.Add(chain[k].Uid);
                    return true;
                }

                cum += os.Length;
            }

            err = "internal: inset distance not located on the polyline";
            return false;
        }

        private static bool TryResolveUids(IAxisVMModel model, List<int> uids, out List<int> ids)
        {
            ids = new List<int>(uids.Count);
            foreach (int uid in uids)
            {
                int id = model.Lines.get_IndexOfUID(uid);
                if (id <= 0) return false;
                ids.Add(id);
            }
            return true;
        }

        private static bool AppendResolved(IAxisVMModel model, List<int> uids, List<int> target)
        {
            List<int> ids;
            if (!TryResolveUids(model, uids, out ids)) return false;
            target.AddRange(ids);
            return true;
        }

        // Splits lineId at nodeId, returning the two resulting line ids.
        private static bool TrySplitLine(IAxisVMModel model, int lineId, int nodeId, out int part1, out int part2)
        {
            part1 = 0;
            part2 = 0;
            try
            {
                if (model.Lines.Item[lineId].SplitByNode(nodeId, out part1, out part2) != 1) return false;
            }
            catch
            {
                return false;
            }
            return part1 > 0 && part2 > 0;
        }

        private static bool LineTouchesNode(IAxisVMModel model, int lineId, int nodeId)
        {
            ResolvedLine l = ResolveLine(model, lineId);
            return l.NodeId1 == nodeId || l.NodeId2 == nodeId;
        }

        /// <summary>Adds a plain straight line between two existing nodes and returns its index.</summary>
        internal static int AddStraightLine(IAxisVMModel model, int nodeId1, int nodeId2)
        {
            RLineGeomData geomData = new RLineGeomData();
            return model.Lines.Add(nodeId1, nodeId2, ELineGeomType.lgtStraightLine, ref geomData);
        }

        // ==================================================================
        // Boundary grouping
        // ==================================================================

        /// <summary>
        /// Splits a selection into independent boundaries: two lines belong to the same boundary when
        /// they share an end node, transitively. Selecting five bays at once therefore yields five
        /// groups (and five panel sets) rather than one nonsense contour spanning all of them.
        /// </summary>
        internal static List<List<ResolvedLine>> GroupByConnectivity(List<ResolvedLine> lines)
        {
            Dictionary<int, List<int>> byNode = new Dictionary<int, List<int>>();
            for (int i = 0; i < lines.Count; i++)
            {
                foreach (int nodeId in new int[] { lines[i].NodeId1, lines[i].NodeId2 })
                {
                    List<int> bucket;
                    if (!byNode.TryGetValue(nodeId, out bucket)) { bucket = new List<int>(); byNode[nodeId] = bucket; }
                    bucket.Add(i);
                }
            }

            List<List<ResolvedLine>> groups = new List<List<ResolvedLine>>();
            bool[] seen = new bool[lines.Count];

            for (int i = 0; i < lines.Count; i++)
            {
                if (seen[i]) continue;

                List<ResolvedLine> group = new List<ResolvedLine>();
                Queue<int> q = new Queue<int>();
                q.Enqueue(i);
                seen[i] = true;

                while (q.Count > 0)
                {
                    int idx = q.Dequeue();
                    group.Add(lines[idx]);

                    foreach (int nodeId in new int[] { lines[idx].NodeId1, lines[idx].NodeId2 })
                    {
                        foreach (int neighbour in byNode[nodeId])
                        {
                            if (seen[neighbour]) continue;
                            seen[neighbour] = true;
                            q.Enqueue(neighbour);
                        }
                    }
                }

                groups.Add(group);
            }

            return groups;
        }

        // TryCreateSpacedPanels' diagnostics are multi-line; with several boundaries in one run that
        // would bury the summary, so only the first line is kept.
        private static string Summarise(string failureReason)
        {
            if (string.IsNullOrEmpty(failureReason)) return "unknown reason";
            int nl = failureReason.IndexOf('\n');
            string head = (nl >= 0) ? failureReason.Substring(0, nl) : failureReason;
            return head.Trim();
        }
    }
}
