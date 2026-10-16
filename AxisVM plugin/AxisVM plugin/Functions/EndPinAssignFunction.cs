using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

/*
 * =====================================================================================
 * END PIN ASSIGN ADDON - LOGIC DOCUMENTATION
 * =====================================================================================
 *
 * This addon automatically assigns structural pins (hinges or semi-rigid connections)
 * to the true "free ends" of a continuous chain of selected lines.
 *
 * CORE LOGIC WORKFLOW:
 * 1. SELECTION & DATA GATHERING:
 *    - The script identifies all user-selected lines.
 *    - For each line it reads IAxisVMLine.StartNode / EndNode. Those two properties are defined as
 *      "the node at the start / end of the local x axis", so they - unlike BulkGetLineData's
 *      NodeId1/NodeId2 - are guaranteed to reflect the line's actual local direction, which is
 *      exactly what SetStartReleases / SetEndReleases act on.
 *    - It builds a map of which nodes belong to which lines.
 *
 * 2. CONTINUITY CHECK & CHAIN BUILDING (IsContinuousStrict):
 *    - To determine if two lines meeting at a node should be considered "continuous" (acting as a single beam),
 *      the script calculates the 3D vectors of the two lines pointing outwards from the shared node.
 *    - It calculates the interior angle between these two lines (0 to 180 degrees).
 *    - SMALLER ANGLE COMPARISON: The user defines an "Angle Tolerance" (e.g., 90 degrees).
 *      The script calculates the "smaller angle" of the bend: Math.Min(angle, 180 - angle).
 *      If this smaller angle is LESS THAN OR EQUAL to the tolerance, the lines are considered continuous.
 *      (e.g., if Tolerance = 90, then straight lines (0 deg bend), right angles (90 deg bend), and sharp
 *      acute corners like 60 deg (smaller angle = 60 deg) are ALL considered continuous).
 *    - Lines that are continuous are grouped into a "Chain" using a breadth-first search (Queue).
 *
 * 3. IDENTIFYING FREE ENDS (Within the Selection):
 *    - Once the chains are built purely from the selected lines, the script counts how many times
 *      each node appears within that specific chain.
 *    - If a node appears exactly ONCE in the chain, it means it is a "free end" of that selected chain.
 *
 * 4. DIRECTION NORMALISATION (NormaliseChainDirections):
 *    - The chain is traced end-to-end from one free end to the other. With "Auto-orient chain" ticked
 *      (the default) every line is then re-oriented so that its local x axis points the same way along
 *      that trace. Unticking it keeps the trace - and therefore the chain's Start / End - but leaves the
 *      lines' local axes as drawn.
 *    - The trace direction is chosen by majority vote: whichever of the two possible directions already
 *      matches most of the lines wins, so the smallest possible number of lines is touched.
 *    - Re-orientation is done through IAxisVMLines.BulkGetAttr_V161 / BulkSetAttr_V161 by writing
 *      RLineAttr_V161.LocalXOrientation (mlxo_ij / mlxo_ji). That flips only the local x axis; it does
 *      not renumber nodes, split the line, or move it, and the releases stay attached to the same
 *      physical node (in that record they are stored against the lower/higher node index, not local x).
 *    - THIS IS WHY IT MATTERS: without it, a chain whose lines were drawn in mixed directions had no
 *      well-defined "Start" and "End", so "Chain: Start Node Only" landed on an arbitrary end and the
 *      per-line start/end release assignment could not be reasoned about. After normalisation, the
 *      chain's Start is the free end the trace begins at and the End is where it finishes.
 *
 * 5. MODEL CONTINUITY CHECK (Unselected Lines Filter):
 *    - If the user checked "Check continuity against unselected lines":
 *      The script iterates through the entire structural model to see if any of these "free ends"
 *      connect to an UNSELECTED line in the model.
 *    - If a free end connects to an unselected line AND they are continuous (based on the same angle logic),
 *      then this node is NOT truly a free end! It is just a segment boundary in a larger continuous beam.
 *      The script removes this node from the list of valid free ends.
 *    - The filtered list only decides WHICH ends get a pin; the Start/End ordering still comes from the
 *      normalised trace in step 4.
 *
 * 6. APPLYING PINS:
 *    - Based on the user's UI choice (Both Ends, Start Node Only, End Node Only), it identifies
 *      which of the free ends should receive the pin.
 *    - It applies a complete hinge (yy, zz, and optionally xx torsion) or a semi-rigid spring
 *      to those specific ends, without overwriting existing translational releases.
 *    - "Modify intermediate releases" (on by default) first resets both ends of every selected
 *      beam/rib to fully fixed, then pins only the requested chain ends. Off preserves every
 *      existing release and only adds/changes the requested chain ends.
 * =====================================================================================
 */

namespace AxisVM_plugin.Functions
{
    internal struct Coord : IEquatable<Coord>
    {
        public int NodeId;
        public double X, Y, Z;
        // Connectivity follows the model's node identity, even when separate nodes coincide.
        // Keep full precision for the continuity angle and short members.
        public Coord(int nodeId, double x, double y, double z) { NodeId = nodeId; X = x; Y = y; Z = z; }
        public bool Equals(Coord other) { return NodeId == other.NodeId; }
        public override bool Equals(object obj) { return obj is Coord && Equals((Coord)obj); }
        public override int GetHashCode() { return NodeId.GetHashCode(); }
    }

    public class EndPinAssignFunction
    {
        /// <summary>Everything known about one selected line, kept in local-x order.</summary>
        private class LineInfo
        {
            public int LineId;
            public int StartNodeId;   // node at the start of the local x axis
            public int EndNodeId;     // node at the end of the local x axis
            public Coord StartCoord;
            public Coord EndCoord;
            public bool CanTakeReleases; // only beams and ribs have end releases
        }

        /// <summary>A chain walked end-to-end, with the local direction each line currently has.</summary>
        private class ChainTrace
        {
            public List<int> LineIds = new List<int>();
            public List<bool> Forward = new List<bool>(); // true when the line's local x already points along the walk
            public Coord Start;
            public Coord End;
        }

        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                Array selectedLines;
                if (model.Lines.GetSelectedItemIds(out selectedLines) < 0)
                    throw new InvalidOperationException("Could not read the selected lines.");

                if (selectedLines == null || selectedLines.Length == 0)
                {
                    MessageBox.Show("No lines selected.", "End Pin Assign", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                using (EndPinAssignForm form = new EndPinAssignForm())
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    List<string> warnings = new List<string>();
                    int chainCount = 0;
                    int reorientedLines = 0;
                    int pinnedEnds = 0;
                    int fixedIntermediateEnds = 0;

                    List<int> linesToProcessForSync = new List<int>();
                    model.BeginUpdate();
                    try
                    {
                        // ------------------------------------------------------------------
                        // 1. Read the selection in local-x order
                        // ------------------------------------------------------------------
                        List<int> linesToProcess = new List<int>();
                        Dictionary<int, LineInfo> lines = new Dictionary<int, LineInfo>();

                        foreach (object selectedId in selectedLines)
                        {
                            int lineId = Convert.ToInt32(selectedId);
                            if (lines.ContainsKey(lineId)) continue;
                            LineInfo info = ReadLine(model, lineId);
                            if (info == null)
                                throw new InvalidOperationException("Could not read selected line " + lineId + ". No releases were assigned.");
                            if (!info.CanTakeReleases)
                            {
                                warnings.Add("Line " + lineId + " is not a beam or rib and was excluded from the chains.");
                                continue;
                            }
                            lines[lineId] = info;
                            linesToProcess.Add(lineId);
                        }

                        // Stable ordering makes majority-vote ties independent of selection order.
                        linesToProcess.Sort();
                        linesToProcessForSync = linesToProcess;

                        if (lines.Count == 0)
                        {
                            MessageBox.Show("None of the selected lines could be read.", "End Pin Assign", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return 0;
                        }

                        // model node identity -> the selected beams/ribs touching it
                        Dictionary<Coord, List<int>> linesAtNode = new Dictionary<Coord, List<int>>();
                        foreach (int lineId in linesToProcess)
                        {
                            LineInfo info = lines[lineId];
                            AddAt(linesAtNode, info.StartCoord, lineId);
                            AddAt(linesAtNode, info.EndCoord, lineId);
                        }

                        // ------------------------------------------------------------------
                        // 2. Build strictly continuous chains based on the angle tolerance
                        // ------------------------------------------------------------------
                        Dictionary<int, int> lineToChainMap = new Dictionary<int, int>();
                        chainCount = 0;

                        foreach (int lId in linesToProcess)
                        {
                            if (lineToChainMap.ContainsKey(lId)) continue;

                            Queue<int> q = new Queue<int>();
                            q.Enqueue(lId);
                            lineToChainMap[lId] = chainCount;

                            while (q.Count > 0)
                            {
                                int curr = q.Dequeue();
                                foreach (Coord shared in new Coord[] { lines[curr].StartCoord, lines[curr].EndCoord })
                                {
                                    foreach (int neighbor in linesAtNode[shared])
                                    {
                                        if (neighbor == curr || lineToChainMap.ContainsKey(neighbor)) continue;
                                        if (!IsContinuousStrict(lines[curr], lines[neighbor], shared, form.AngleTolerance)) continue;

                                        lineToChainMap[neighbor] = chainCount;
                                        q.Enqueue(neighbor);
                                    }
                                }
                            }
                            chainCount++;
                        }

                        // chainId -> its lines
                        Dictionary<int, List<int>> chainLines = new Dictionary<int, List<int>>();
                        foreach (int lineId in linesToProcess)
                        {
                            int chainId = lineToChainMap[lineId];
                            if (!chainLines.ContainsKey(chainId)) chainLines[chainId] = new List<int>();
                            chainLines[chainId].Add(lineId);
                        }

                        // ------------------------------------------------------------------
                        // 3. Free ends: a node used exactly once inside its own chain
                        // ------------------------------------------------------------------
                        Dictionary<int, Dictionary<Coord, int>> chainNodeCounts = new Dictionary<int, Dictionary<Coord, int>>();
                        Dictionary<int, List<Coord>> chainFreeEnds = new Dictionary<int, List<Coord>>();

                        foreach (var kvp in chainLines)
                        {
                            Dictionary<Coord, int> counts = new Dictionary<Coord, int>();
                            foreach (int lineId in kvp.Value)
                            {
                                Bump(counts, lines[lineId].StartCoord);
                                Bump(counts, lines[lineId].EndCoord);
                            }
                            chainNodeCounts[kvp.Key] = counts;

                            List<Coord> ends = new List<Coord>();
                            foreach (int lineId in kvp.Value)
                            {
                                Coord c1 = lines[lineId].StartCoord;
                                Coord c2 = lines[lineId].EndCoord;
                                if (counts[c1] == 1 && !ends.Contains(c1)) ends.Add(c1);
                                if (counts[c2] == 1 && !ends.Contains(c2)) ends.Add(c2);
                            }
                            chainFreeEnds[kvp.Key] = ends;
                        }

                        // ------------------------------------------------------------------
                        // 4. Re-orient every chain so its lines all run the same way
                        // ------------------------------------------------------------------
                        HashSet<int> unorderedChains = new HashSet<int>();
                        reorientedLines = NormaliseChainDirections(model, chainLines, chainFreeEnds, lines, warnings, form.AutoOrient, unorderedChains);

                        // ------------------------------------------------------------------
                        // 5. Drop free ends that continue into an unselected line
                        // ------------------------------------------------------------------
                        Dictionary<int, List<Coord>> chainValidEnds = new Dictionary<int, List<Coord>>();
                        foreach (var kvp in chainFreeEnds)
                        {
                            chainValidEnds[kvp.Key] = form.CheckModelContinuity
                                ? FilterAgainstUnselectedLines(model, kvp.Value, chainLines[kvp.Key], lines, linesToProcess, form.AngleTolerance, warnings)
                                : new List<Coord>(kvp.Value);
                        }

                        // ------------------------------------------------------------------
                        // 6. Apply the releases
                        // ------------------------------------------------------------------
                        // Modify intermediate: first make BOTH ends of EVERY selected beam/rib fully
                        // fixed, then pin only the requested chain ends below. Any old pin elsewhere in
                        // the selection (intermediate joints, untargeted or filtered-out ends, chains
                        // without a Start/End) is therefore removed. Done before the torsion check so a
                        // removed torsion hinge does not block a requested terminal torsion release.
                        if (form.ModifyIntermediateReleases)
                        {
                            foreach (int lineId in linesToProcess)
                            {
                                var item = model.Lines.Item[lineId];
                                RReleases start = new RReleases(), end = new RReleases();
                                if (item.GetStartReleases(ref start) <= 0 || item.GetEndReleases(ref end) <= 0)
                                {
                                    warnings.Add("Could not read releases on line " + lineId + "; they were not reset to fixed.");
                                    continue;
                                }
                                if (HasAnyRelease(start))
                                {
                                    start = FullyFixed();
                                    if (item.SetStartReleases(ref start) > 0) fixedIntermediateEnds++;
                                    else warnings.Add("Could not reset start releases on line " + lineId + " to fixed.");
                                }
                                if (HasAnyRelease(end))
                                {
                                    end = FullyFixed();
                                    if (item.SetEndReleases(ref end) > 0) fixedIntermediateEnds++;
                                    else warnings.Add("Could not reset end releases on line " + lineId + " to fixed.");
                                }
                            }
                        }

                        HashSet<int> chainTorsionReleased = new HashSet<int>();
                        // Respect torsion releases already present anywhere in the chain. Repeated runs
                        // must not add a second torsion hinge simply because the target end changed.
                        if (form.ReleaseTorsion)
                        {
                            foreach (int id in linesToProcess)
                            {
                                RReleases start = new RReleases(), end = new RReleases();
                                var item = model.Lines.Item[id];
                                if (item.GetStartReleases(ref start) <= 0 || item.GetEndReleases(ref end) <= 0)
                                    throw new InvalidOperationException("Could not read existing releases on line " + id + ".");
                                if (start.xx.ReleaseType != EReleaseType.rtRigid || end.xx.ReleaseType != EReleaseType.rtRigid)
                                    chainTorsionReleased.Add(lineToChainMap[id]);
                            }
                        }

                        foreach (int lineId in linesToProcess)
                        {
                            LineInfo info = lines[lineId];
                            int chainId = lineToChainMap[lineId];

                            // A branch has no unique chain Start/End. Both Ends can still pin its leaves.
                            if (form.Target != PinTarget.ChainBothEnds && unorderedChains.Contains(chainId)) continue;
                            List<Coord> ordered = chainFreeEnds[chainId];
                            List<Coord> valid = chainValidEnds[chainId];

                            Coord? chainStart = ordered.Count > 0 ? ordered[0] : (Coord?)null;
                            Coord? chainEnd = ordered.Count > 1 ? ordered[ordered.Count - 1] : (Coord?)null;

                            // A node earns a pin only if it is a free end of its chain AND survived the
                            // continuity filter.
                            bool c1Free = chainNodeCounts[chainId][info.StartCoord] == 1 && valid.Contains(info.StartCoord);
                            bool c2Free = chainNodeCounts[chainId][info.EndCoord] == 1 && valid.Contains(info.EndCoord);

                            bool isStartFree = false;
                            bool isEndFree = false;

                            if (form.Target == PinTarget.ChainBothEnds)
                            {
                                isStartFree = c1Free;
                                isEndFree = c2Free;
                            }
                            else if (form.Target == PinTarget.ChainStartNode)
                            {
                                isStartFree = c1Free && chainStart.HasValue && info.StartCoord.Equals(chainStart.Value);
                                isEndFree = c2Free && chainStart.HasValue && info.EndCoord.Equals(chainStart.Value);
                            }
                            else if (form.Target == PinTarget.ChainEndNode)
                            {
                                isStartFree = c1Free && chainEnd.HasValue && info.StartCoord.Equals(chainEnd.Value);
                                isEndFree = c2Free && chainEnd.HasValue && info.EndCoord.Equals(chainEnd.Value);
                            }

                            if (!isStartFree && !isEndFree) continue;

                            if (!info.CanTakeReleases)
                            {
                                warnings.Add(string.Format("Line {0} is not a beam or rib, so it cannot take end releases.", lineId));
                                continue;
                            }

                            var lineItem = model.Lines.Item[lineId];

                            // Read before modifying, and count only successful COM writes. A failed
                            // write does not consume the chain's one new torsion release.
                            RReleases relStart = new RReleases();
                            RReleases relEnd = new RReleases();
                            if (lineItem.GetStartReleases(ref relStart) <= 0 || lineItem.GetEndReleases(ref relEnd) <= 0)
                            {
                                warnings.Add("Could not read releases on line " + lineId + "; it was skipped.");
                                continue;
                            }

                            if (isStartFree)
                            {
                                bool torsion = form.ReleaseTorsion && !chainTorsionReleased.Contains(chainId);
                                relStart = ModifyRelease(relStart, form, torsion);
                                if (lineItem.SetStartReleases(ref relStart) > 0)
                                {
                                    pinnedEnds++;
                                    if (torsion) chainTorsionReleased.Add(chainId);
                                }
                                else warnings.Add("Could not assign start releases on line " + lineId + ".");
                            }
                            if (isEndFree)
                            {
                                bool torsion = form.ReleaseTorsion && !chainTorsionReleased.Contains(chainId);
                                relEnd = ModifyRelease(relEnd, form, torsion);
                                if (lineItem.SetEndReleases(ref relEnd) > 0)
                                {
                                    pinnedEnds++;
                                    if (torsion) chainTorsionReleased.Add(chainId);
                                }
                                else warnings.Add("Could not assign end releases on line " + lineId + ".");
                            }
                        }
                    }
                    finally
                    {
                        model.EndUpdate();
                    }
                    // AxisVM draws end-release symbols from the structural member, and a line-level
                    // write made from inside AxisVM does not update the member's copy, so the old
                    // pins stay on screen. Writing the same releases through the member does.
                    SyncMemberReleases(model, linesToProcessForSync, warnings);

                    ShowSummary(chainCount, reorientedLines, pinnedEnds, fixedIntermediateEnds, warnings);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }


        /// <summary>
        /// Copies each touched line's final release at its member's start/end node onto that member.
        /// The values are unchanged; the write only makes AxisVM refresh the symbols it draws.
        /// </summary>
        private static void SyncMemberReleases(IAxisVMModel model, List<int> lineIds, List<string> warnings)
        {
            if (lineIds.Count == 0) return;
            HashSet<int> touched = new HashSet<int>(lineIds);
            int failed = 0;
            for (int m = 1; m <= model.Members.Count; m++)
            {
                try
                {
                    var member = model.Members.Item[m];
                    Array memberLines;
                    if (member.GetLines(out memberLines) <= 0 || memberLines == null) continue;
                    bool hit = false;
                    foreach (object o in memberLines) if (touched.Contains(Convert.ToInt32(o))) { hit = true; break; }
                    if (!hit) continue;

                    RReleases rel;
                    if (ReleaseAtNode(model, memberLines, member.StartNode, out rel) && member.SetStartReleases(ref rel) <= 0) failed++;
                    if (ReleaseAtNode(model, memberLines, member.EndNode, out rel) && member.SetEndReleases(ref rel) <= 0) failed++;
                }
                catch { failed++; }
            }
            if (failed > 0)
                warnings.Add("The display of " + failed + " member end(s) could not be refreshed; the releases themselves are assigned. Reopen the model to see them.");
        }

        /// <summary>The release of whichever line of the member ends at <paramref name="nodeId"/>.</summary>
        private static bool ReleaseAtNode(IAxisVMModel model, Array memberLines, int nodeId, out RReleases rel)
        {
            rel = new RReleases();
            foreach (object o in memberLines)
            {
                var line = model.Lines.Item[Convert.ToInt32(o)];
                if (line.StartNode == nodeId) return line.GetStartReleases(ref rel) > 0;
                if (line.EndNode == nodeId) return line.GetEndReleases(ref rel) > 0;
            }
            return false;
        }

        private static void ShowSummary(int chainCount, int reorientedLines, int pinnedEnds, int fixedIntermediateEnds, List<string> warnings)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("Assigned {0} end release(s) across {1} chain(s).", pinnedEnds, chainCount));
            sb.AppendLine(string.Format("Lines re-oriented along their chain: {0}", reorientedLines));
            sb.AppendLine(string.Format("Line ends reset to fixed before pinning: {0}", fixedIntermediateEnds));

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                int shown = Math.Min(warnings.Count, 10);
                for (int i = 0; i < shown; i++) sb.AppendLine("  " + warnings[i]);
                if (warnings.Count > shown) sb.AppendLine("  ... and " + (warnings.Count - shown) + " more");
            }

            MessageBox.Show(sb.ToString(), "End Pin Assign", MessageBoxButtons.OK,
                pinnedEnds > 0 || fixedIntermediateEnds > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------
        // Reading the model
        // ------------------------------------------------------------------

        private static LineInfo ReadLine(IAxisVMModel model, int lineId)
        {
            try
            {
                var line = model.Lines.Item[lineId];
                int startNode = line.StartNode;
                int endNode = line.EndNode;

                AxisVM.RNode n1 = new AxisVM.RNode();
                AxisVM.RNode n2 = new AxisVM.RNode();
                if (startNode <= 0 || endNode <= 0 || startNode == endNode) return null;
                if (model.Nodes.GetNode(startNode, ref n1) <= 0 || model.Nodes.GetNode(endNode, ref n2) <= 0) return null;

                ELineType lineType = line.LineType;

                return new LineInfo
                {
                    LineId = lineId,
                    StartNodeId = startNode,
                    EndNodeId = endNode,
                    StartCoord = new Coord(startNode, n1.x, n1.y, n1.z),
                    EndCoord = new Coord(endNode, n2.x, n2.y, n2.z),
                    CanTakeReleases = (lineType == ELineType.ltBeam || lineType == ELineType.ltRib)
                };
            }
            catch
            {
                return null;
            }
        }

        private static void AddAt(Dictionary<Coord, List<int>> map, Coord key, int lineId)
        {
            List<int> bucket;
            if (!map.TryGetValue(key, out bucket)) { bucket = new List<int>(); map[key] = bucket; }
            bucket.Add(lineId);
        }

        private static void Bump(Dictionary<Coord, int> counts, Coord key)
        {
            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;
        }

        // ------------------------------------------------------------------
        // Direction normalisation
        // ------------------------------------------------------------------

        /// <summary>
        /// Walks each chain from one free end to the other and rewrites chainFreeEnds so that entry 0 is
        /// the node the walk starts at (the chain's "Start") and the last entry is where it ends.
        /// When <paramref name="applyFlip"/> is set (the "Auto-orient chain" option), the chain's lines are
        /// also re-oriented in the model so every local x axis points the same way along that walk.
        /// The walk itself is read-only, so the Start / End of a chain is well defined either way - only
        /// the model edit is optional.
        /// Chains that are not simple open runs - loops, branches, or anything the walk cannot cover
        /// completely - are left untouched: there is no single direction to normalise them to.
        /// Returns the number of lines whose orientation was changed.
        /// </summary>
        private static int NormaliseChainDirections(
            IAxisVMModel model,
            Dictionary<int, List<int>> chainLines,
            Dictionary<int, List<Coord>> chainFreeEnds,
            Dictionary<int, LineInfo> lines,
            List<string> warnings,
            bool applyFlip,
            HashSet<int> unorderedChains)
        {
            List<int> toFlip = new List<int>();

            foreach (var kvp in chainLines)
            {
                int chainId = kvp.Key;
                List<int> ids = kvp.Value;
                List<Coord> ends = chainFreeEnds[chainId];

                if (ends.Count != 2)
                {
                    unorderedChains.Add(chainId);
                    if (ids.Count > 1)
                    {
                        warnings.Add(string.Format("Chain {0} ({1} lines) has {2} free end(s) - it is a loop or branches, so its direction was left as-is and Start/End-only targets are skipped.",
                            chainId + 1, ids.Count, ends.Count));
                    }
                    continue;
                }

                ChainTrace trace = TraceChain(ids, ends[0], lines);
                if (trace == null)
                {
                    unorderedChains.Add(chainId);
                    warnings.Add(string.Format("Chain {0} could not be walked end-to-end; Start/End-only targets are skipped.", chainId + 1));
                    continue;
                }

                // Majority vote on the existing orientations, so we flip as few lines as possible.
                int forward = 0;
                foreach (bool f in trace.Forward) if (f) forward++;

                if (forward * 2 < trace.Forward.Count)
                {
                    ChainTrace reverse = TraceChain(ids, ends[1], lines);
                    if (reverse != null) trace = reverse;
                }

                ends[0] = trace.Start;
                ends[1] = trace.End;

                for (int i = 0; i < trace.LineIds.Count; i++)
                {
                    if (trace.Forward[i]) continue;

                    int lineId = trace.LineIds[i];
                    if (!lines[lineId].CanTakeReleases) continue; // BulkSetAttr rejects edges, links, ...
                    toFlip.Add(lineId);
                }
            }

            if (toFlip.Count == 0) return 0;

            if (!applyFlip)
            {
                // The ends were still ordered by the walk above, so Start / End stay meaningful; only the
                // model edit is skipped.
                warnings.Add("Auto-orient is off: " + toFlip.Count + " line(s) still run against their chain direction in the model.");
                return 0;
            }

            string error;
            int flippedCount;
            if (FlipLocalX(model, toFlip, lines, out error, out flippedCount)) return flippedCount;

            warnings.Add("Could not re-orient " + toFlip.Count + " line(s): " + error +
                         " The current local axes were re-read before assigning releases.");
            return flippedCount;
        }

        /// <summary>
        /// Walks the chain from <paramref name="from"/>, taking whichever unvisited line touches the
        /// current node. Returns null when the walk does not cover every line of the chain, which means
        /// the chain is not a simple open run.
        /// </summary>
        private static ChainTrace TraceChain(List<int> chainLineIds, Coord from, Dictionary<int, LineInfo> lines)
        {
            // A full walk can also be an Euler trail through a loop with branches.
            // Require a simple open path explicitly, rather than just visiting every line.
            Dictionary<Coord, int> degrees = new Dictionary<Coord, int>();
            foreach (int id in chainLineIds)
            {
                Bump(degrees, lines[id].StartCoord);
                Bump(degrees, lines[id].EndCoord);
            }
            int startDegree;
            if (!degrees.TryGetValue(from, out startDegree) || startDegree != 1) return null;
            foreach (int degree in degrees.Values) if (degree > 2) return null;

            ChainTrace trace = new ChainTrace();
            trace.Start = from;

            Coord current = from;
            HashSet<int> visited = new HashSet<int>();

            while (visited.Count < chainLineIds.Count)
            {
                int next = -1;
                bool forward = false;

                foreach (int lineId in chainLineIds)
                {
                    if (visited.Contains(lineId)) continue;
                    if (lines[lineId].StartCoord.Equals(current)) { next = lineId; forward = true; break; }
                    if (lines[lineId].EndCoord.Equals(current)) { next = lineId; forward = false; break; }
                }

                if (next < 0) break;

                visited.Add(next);
                trace.LineIds.Add(next);
                trace.Forward.Add(forward);
                current = forward ? lines[next].EndCoord : lines[next].StartCoord;
            }

            trace.End = current;
            return trace.LineIds.Count == chainLineIds.Count ? trace : null;
        }

        /// <summary>
        /// Reverses the local x axis of the given lines through RLineAttr_V161.LocalXOrientation, which is
        /// expressed relative to the node with the LOWER index: mlxo_ij means local x runs from the lower
        /// index towards the higher one. Nothing else about the line is changed - the attribute records are
        /// read and written back untouched apart from that single field.
        /// On success the cached LineInfo entries are refreshed from the model so the caller's Start/End
        /// coordinates match the new orientation.
        /// </summary>
        private static bool FlipLocalX(IAxisVMModel model, List<int> lineIds, Dictionary<int, LineInfo> lines, out string error, out int flippedCount)
        {
            error = null;
            flippedCount = 0;

            Array ids = Array.CreateInstance(typeof(int), new int[] { lineIds.Count }, new int[] { 1 });
            for (int i = 0; i < lineIds.Count; i++) ids.SetValue(lineIds[i], i + 1);

            Array attrs = null;
            try
            {
                if (model.Lines.BulkGetAttr_V161(ids, out attrs) != lineIds.Count) attrs = null;
            }
            catch (Exception ex)
            {
                error = "BulkGetAttr_V161 failed (" + ex.Message + ").";
                return false;
            }

            if (attrs == null)
            {
                error = "BulkGetAttr_V161 returned no line attributes.";
                return false;
            }

            // The records are copied into a FRESH 1-based array of our own RLineAttr_V161 instead of
            // being written back into the array BulkGetAttr_V161 handed us. The interop types are
            // embedded (EmbedInteropTypes), so the element type of the returned array is only
            // *equivalent* to the RLineAttr_V161 compiled into this assembly, not identical to it.
            // A cast honours that equivalence, but Array.SetValue does an exact element-type check on
            // value-type arrays and throws "Object cannot be stored in an array of this type."
            // Building our own array side-steps it and matches how AxisVMHelper drives BulkSetAttr.
            Array updated = Array.CreateInstance(typeof(RLineAttr_V161), new int[] { lineIds.Count }, new int[] { 1 });

            int lower = attrs.GetLowerBound(0);
            for (int i = 0; i < lineIds.Count; i++)
            {
                object raw;
                try { raw = attrs.GetValue(lower + i); }
                catch { error = "The line attribute array was shorter than the request."; return false; }

                if (!(raw is RLineAttr_V161))
                {
                    error = "Unexpected line attribute record type.";
                    return false;
                }

                LineInfo info = lines[lineIds[i]];

                // After the flip local x must start at the node it currently ends at.
                int fromNode = info.EndNodeId;
                int toNode = info.StartNodeId;

                RLineAttr_V161 attr = (RLineAttr_V161)raw;
                attr.LocalXOrientation = (fromNode < toNode)
                    ? EMemberLocXOrientation.mlxo_ij
                    : EMemberLocXOrientation.mlxo_ji;

                try { updated.SetValue(attr, i + 1); }
                catch (Exception ex)
                {
                    error = "The line attribute record could not be written back (" + ex.Message + ").";
                    return false;
                }
            }

            bool success = false;
            try
            {
                if (model.Lines.BulkSetAttr_V161(ids, updated) != lineIds.Count)
                {
                    error = "BulkSetAttr_V161 rejected the new orientation.";
                }
                else success = true;
            }
            catch (Exception ex)
            {
                error = "BulkSetAttr_V161 failed (" + ex.Message + ").";
            }

            // Re-read rather than assume: the model is the authority on which node local x now starts at.
            foreach (int lineId in lineIds)
            {
                LineInfo refreshed = ReadLine(model, lineId);
                // A failed bulk write may still have changed some records. Never assign
                // releases using stale local start/end data, even after an API error.
                if (refreshed == null)
                    throw new InvalidOperationException("Could not refresh line " + lineId + " after orientation. Release assignment stopped.");
                if (refreshed.StartNodeId != lines[lineId].EndNodeId || refreshed.EndNodeId != lines[lineId].StartNodeId)
                {
                    success = false;
                    error = "The requested local x reversal was not confirmed for line " + lineId + ".";
                }
                else flippedCount++;
                lines[lineId] = refreshed;
            }

            return success;
        }

        // ------------------------------------------------------------------
        // Continuity against the rest of the model
        // ------------------------------------------------------------------

        /// <summary>
        /// Drops the free ends that are really just a segment boundary inside a longer beam: the node
        /// carries an unselected line that continues in the same direction within the angle tolerance.
        /// </summary>
        private static List<Coord> FilterAgainstUnselectedLines(
            IAxisVMModel model,
            List<Coord> freeEnds,
            List<int> chainLineIds,
            Dictionary<int, LineInfo> lines,
            List<int> selectedLineIds,
            double angleTolerance,
            List<string> warnings)
        {
            List<Coord> valid = new List<Coord>();
            HashSet<int> selected = new HashSet<int>(selectedLineIds);

            foreach (Coord c in freeEnds)
            {
                // The chain line that owns this end - the one whose direction we compare against.
                LineInfo owner = null;
                foreach (int lineId in chainLineIds)
                {
                    LineInfo info = lines[lineId];
                    if (info.StartCoord.Equals(c) || info.EndCoord.Equals(c)) { owner = info; break; }
                }

                if (owner == null) { valid.Add(c); continue; }

                // Only lines actually meeting at this node can continue it. Resolving the node index from
                // the owner line avoids scanning the whole model, which is what the previous
                // Members-based scan did (and it missed lines that belong to no structural member).
                int nodeId = owner.StartCoord.Equals(c) ? owner.StartNodeId : owner.EndNodeId;

                Array connected = null;
                try
                {
                    if (model.Nodes.GetConnectedLines(nodeId, out connected) < 0 || connected == null)
                        throw new InvalidOperationException("No connectivity data returned.");
                }
                catch (Exception ex)
                {
                    // With continuity checking requested, an unreadable node must not earn a pin.
                    warnings.Add("Continuity could not be checked at node " + nodeId + "; no pin assigned there. " + ex.Message);
                    continue;
                }

                bool isTrulyFree = true;
                if (connected != null)
                {
                    foreach (object obj in connected)
                    {
                        int otherId = Convert.ToInt32(obj);
                        if (otherId <= 0 || selected.Contains(otherId)) continue;

                        LineInfo other = ReadLine(model, otherId);
                        if (other == null)
                        {
                            warnings.Add("Connected line " + otherId + " could not be read; no pin assigned at node " + nodeId + ".");
                            isTrulyFree = false;
                            break;
                        }
                        if (!other.CanTakeReleases) continue; // geometric edges/links are not beam continuations
                        if (!other.StartCoord.Equals(c) && !other.EndCoord.Equals(c)) continue;

                        if (IsContinuousStrict(owner, other, c, angleTolerance)) { isTrulyFree = false; break; }
                    }
                }

                if (isTrulyFree) valid.Add(c);
            }

            return valid;
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        private static bool HasAnyRelease(RReleases rel)
        {
            return rel.x.ReleaseType != EReleaseType.rtRigid || rel.y.ReleaseType != EReleaseType.rtRigid ||
                rel.z.ReleaseType != EReleaseType.rtRigid || rel.xx.ReleaseType != EReleaseType.rtRigid ||
                rel.yy.ReleaseType != EReleaseType.rtRigid || rel.zz.ReleaseType != EReleaseType.rtRigid;
        }

        /// <summary>All six components rigid - a fully fixed line end.</summary>
        private static RReleases FullyFixed()
        {
            RRelease rigid = new RRelease { ReleaseType = EReleaseType.rtRigid };
            return new RReleases { x = rigid, y = rigid, z = rigid, xx = rigid, yy = rigid, zz = rigid };
        }

        private static RReleases ModifyRelease(RReleases rel, EndPinAssignForm form, bool applyTorsion)
        {
            if (form.IsSemiRigid)
            {
                rel.yy = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };
                rel.zz = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };

                if (applyTorsion)
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };

            }
            else
            {
                rel.yy = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                rel.zz = new RRelease() { ReleaseType = EReleaseType.rtHinged };

                if (applyTorsion)
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtHinged };

            }

            // Note: We intentionally DO NOT modify x, y, and z translational releases.
            // Existing torsion is preserved unless this end explicitly receives a new torsion release.

            return rel;
        }

        /// <summary>
        /// True when the two lines meeting at <paramref name="sharedCoord"/> bend by no more than the
        /// tolerance. The bend is measured as the smaller of (angle, 180 - angle), so a line's own
        /// direction of definition does not affect the result.
        /// </summary>
        private static bool IsContinuousStrict(LineInfo current, LineInfo other, Coord sharedCoord, double angleTolerance)
        {
            double cx, cy, cz;
            if (!Direction(current, sharedCoord, out cx, out cy, out cz)) return false;

            double ox, oy, oz;
            if (!Direction(other, sharedCoord, out ox, out oy, out oz)) return false;

            double dot = cx * ox + cy * oy + cz * oz;
            if (dot < -1) dot = -1;
            if (dot > 1) dot = 1;

            double angleDeg = Math.Acos(dot) * 180.0 / Math.PI;
            double smallerAngle = Math.Min(angleDeg, 180.0 - angleDeg);

            return smallerAngle <= angleTolerance + 1e-5;
        }

        /// <summary>Unit vector of the line pointing away from the shared node.</summary>
        private static bool Direction(LineInfo line, Coord sharedCoord, out double x, out double y, out double z)
        {
            Coord a = line.StartCoord;
            Coord b = line.EndCoord;

            if (a.Equals(sharedCoord)) { x = b.X - a.X; y = b.Y - a.Y; z = b.Z - a.Z; }
            else if (b.Equals(sharedCoord)) { x = a.X - b.X; y = a.Y - b.Y; z = a.Z - b.Z; }
            else { x = y = z = 0; return false; }

            double len = Math.Sqrt(x * x + y * y + z * z);
            if (double.IsNaN(len) || double.IsInfinity(len) || len < 1e-12) { x = y = z = 0; return false; }

            x /= len; y /= len; z /= len;
            return true;
        }
    }
}
