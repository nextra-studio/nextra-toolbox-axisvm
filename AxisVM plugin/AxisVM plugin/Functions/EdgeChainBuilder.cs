using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// A resolved line segment: its model index plus its two endpoint node indexes.
    /// The node indexes are read straight from IAxisVMLine.StartNode / EndNode (see
    /// OneWayLoadPanelLogic for why the bulk path is avoided). Shared by
    /// the one-way panel generator and Rhino import.
    /// </summary>
    internal struct ResolvedLine
    {
        public int LineId;   // 1-based line index in model.Lines
        public int NodeId1;  // start node
        public int NodeId2;  // end node
    }

    /// <summary>
    /// One physically-straight side of a load-panel boundary, reconstructed from one
    /// or more collinear model segments. A bay side that a user has split into three
    /// beams (columns dropped, secondary beams framing in, meshing, ...) is a single
    /// LogicalEdge with three entries in <see cref="SegmentUids"/>.
    ///
    /// Everything is stored by line UID rather than index because the with-spacing
    /// generation splits lines, and splitting renumbers every line index after the
    /// split point. UIDs survive that; node indexes survive it too (splitting only
    /// adds nodes).
    /// </summary>
    internal sealed class LogicalEdge
    {
        /// <summary>Segment line UIDs in walk order from <see cref="NodeA"/> to <see cref="NodeB"/>.</summary>
        public List<int> SegmentUids = new List<int>();

        /// <summary>Node index at the start of the walk (one physical corner of the bay).</summary>
        public int NodeA;

        /// <summary>Node index at the end of the walk (the opposite corner of this side).</summary>
        public int NodeB;

        /// <summary>Chord direction NodeA -&gt; NodeB, in degrees (atan2, not normalised).</summary>
        public double AngleDeg;

        /// <summary>Summed length of every segment [m].</summary>
        public double Length;

        /// <summary>
        /// True when the walk covered every segment of the chain as one open run
        /// (exactly two free ends). Loops / branches / broken chains are flagged
        /// false; the caller then usually falls back to a single panel.
        /// </summary>
        public bool IsSimpleRun;
    }

    /// <summary>
    /// Groups a flat list of boundary segments into <see cref="LogicalEdge"/>s by
    /// chaining collinear neighbours, using the same "smaller bend angle vs tolerance"
    /// test as EndPinAssignFunction.IsContinuousStrict.
    ///
    /// The chaining tolerance is meant to be small (the One-Way form defaults to
    /// 15 deg): two segments meeting at a node are chained only when the bend there
    /// is nearly straight, so real 90 deg corners of the bay stay as edge boundaries
    /// and a rectangular bay comes back as exactly four LogicalEdges.
    /// </summary>
    internal static class EdgeChainBuilder
    {
        /// <param name="model">the AxisVM model (used only to read node coordinates).</param>
        /// <param name="segments">every boundary segment of one bay / one panel contour.</param>
        /// <param name="toleranceDeg">max bend angle for two segments to count as one straight side.</param>
        /// <param name="notes">human-readable diagnostics are appended here.</param>
        public static List<LogicalEdge> Build(IAxisVMModel model, List<ResolvedLine> segments, double toleranceDeg, List<string> notes)
        {
            List<LogicalEdge> edges = new List<LogicalEdge>();
            if (segments == null || segments.Count == 0) return edges;

            // node index -> positions in "segments" that touch it
            Dictionary<int, List<int>> segsAtNode = new Dictionary<int, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                AddAt(segsAtNode, segments[i].NodeId1, i);
                AddAt(segsAtNode, segments[i].NodeId2, i);
            }

            // ---------------------------------------------------------------
            // 1. Chain segments into groups: neighbours joined only when the
            //    bend between them is within tolerance (i.e. nearly straight).
            // ---------------------------------------------------------------
            int[] chainOf = new int[segments.Count];
            for (int i = 0; i < chainOf.Length; i++) chainOf[i] = -1;
            int chainCount = 0;

            for (int i = 0; i < segments.Count; i++)
            {
                if (chainOf[i] >= 0) continue;

                Queue<int> queue = new Queue<int>();
                queue.Enqueue(i);
                chainOf[i] = chainCount;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    foreach (int sharedNode in new int[] { segments[cur].NodeId1, segments[cur].NodeId2 })
                    {
                        foreach (int neighbour in segsAtNode[sharedNode])
                        {
                            if (neighbour == cur || chainOf[neighbour] >= 0) continue;
                            if (!IsCollinearAtNode(model, segments[cur], segments[neighbour], sharedNode, toleranceDeg)) continue;

                            chainOf[neighbour] = chainCount;
                            queue.Enqueue(neighbour);
                        }
                    }
                }
                chainCount++;
            }

            // chainId -> the segment positions in it
            Dictionary<int, List<int>> chainMembers = new Dictionary<int, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                List<int> bucket;
                if (!chainMembers.TryGetValue(chainOf[i], out bucket)) { bucket = new List<int>(); chainMembers[chainOf[i]] = bucket; }
                bucket.Add(i);
            }

            // ---------------------------------------------------------------
            // 2. Walk each chain end-to-end into an ordered LogicalEdge.
            // ---------------------------------------------------------------
            foreach (var kvp in chainMembers)
            {
                List<int> members = kvp.Value;

                // Free ends of the chain: nodes that only one of its segments touches.
                Dictionary<int, int> nodeUse = new Dictionary<int, int>();
                foreach (int m in members)
                {
                    Bump(nodeUse, segments[m].NodeId1);
                    Bump(nodeUse, segments[m].NodeId2);
                }
                List<int> freeEnds = new List<int>();
                foreach (var nu in nodeUse) if (nu.Value == 1) freeEnds.Add(nu.Key);

                if (members.Count >= 2 && freeEnds.Count != 2)
                {
                    notes.Add(string.Format(
                        "Edge chain of {0} segment(s) is a loop or branches ({1} free end(s)); its segments were kept separate.",
                        members.Count, freeEnds.Count));
                    foreach (int m in members) edges.Add(SingleSegmentEdge(model, segments[m]));
                    continue;
                }

                if (members.Count == 1)
                {
                    edges.Add(SingleSegmentEdge(model, segments[members[0]]));
                    continue;
                }

                // Ordered walk from one free end to the other.
                List<ResolvedLine> ordered;
                int endNode;
                if (!WalkChain(segments, members, freeEnds[0], out ordered, out endNode))
                {
                    notes.Add(string.Format("Edge chain of {0} segment(s) could not be walked end-to-end; its segments were kept separate.", members.Count));
                    foreach (int m in members) edges.Add(SingleSegmentEdge(model, segments[m]));
                    continue;
                }

                LogicalEdge edge = new LogicalEdge { NodeA = freeEnds[0], NodeB = endNode, IsSimpleRun = true };
                double length = 0.0;
                foreach (ResolvedLine seg in ordered)
                {
                    edge.SegmentUids.Add(model.Lines.get_UID(seg.LineId));
                    length += Distance(GetNode(model, seg.NodeId1), GetNode(model, seg.NodeId2));
                }
                edge.Length = length;
                edge.AngleDeg = ChordAngleDeg(GetNode(model, edge.NodeA), GetNode(model, edge.NodeB));
                edges.Add(edge);
            }

            return edges;
        }

        // ------------------------------------------------------------------
        // Chain walking
        // ------------------------------------------------------------------

        /// <summary>
        /// Orders <paramref name="members"/> into a single run starting at
        /// <paramref name="startNode"/>. Returns false if the run does not cover
        /// every member (chain is not a simple path).
        /// </summary>
        private static bool WalkChain(List<ResolvedLine> segments, List<int> members, int startNode, out List<ResolvedLine> ordered, out int endNode)
        {
            ordered = new List<ResolvedLine>();
            endNode = startNode;

            HashSet<int> used = new HashSet<int>();
            int current = startNode;

            while (used.Count < members.Count)
            {
                int pick = -1;
                foreach (int m in members)
                {
                    if (used.Contains(m)) continue;
                    if (segments[m].NodeId1 == current || segments[m].NodeId2 == current) { pick = m; break; }
                }
                if (pick < 0) break;

                used.Add(pick);
                ResolvedLine seg = segments[pick];
                // Orient the stored copy so NodeId1 == the node we arrived on.
                if (seg.NodeId1 != current)
                {
                    int t = seg.NodeId1; seg.NodeId1 = seg.NodeId2; seg.NodeId2 = t;
                }
                ordered.Add(seg);
                current = seg.NodeId2;
            }

            endNode = current;
            return used.Count == members.Count;
        }

        private static LogicalEdge SingleSegmentEdge(IAxisVMModel model, ResolvedLine seg)
        {
            AxisVM.RNode a = GetNode(model, seg.NodeId1);
            AxisVM.RNode b = GetNode(model, seg.NodeId2);
            LogicalEdge edge = new LogicalEdge
            {
                NodeA = seg.NodeId1,
                NodeB = seg.NodeId2,
                AngleDeg = ChordAngleDeg(a, b),
                Length = Distance(a, b),
                IsSimpleRun = true
            };
            edge.SegmentUids.Add(model.Lines.get_UID(seg.LineId));
            return edge;
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// True when the two segments meeting at <paramref name="sharedNode"/> bend
        /// by no more than <paramref name="toleranceDeg"/>. The bend is the smaller
        /// of (angle, 180 - angle) between the two directions pointing away from the
        /// shared node, so a segment's direction of definition does not matter. This
        /// is the exact test used by EndPinAssignFunction.IsContinuousStrict.
        /// </summary>
        private static bool IsCollinearAtNode(IAxisVMModel model, ResolvedLine a, ResolvedLine b, int sharedNode, double toleranceDeg)
        {
            double ax, ay, az;
            double bx, by, bz;
            if (!DirectionAway(model, a, sharedNode, out ax, out ay, out az)) return false;
            if (!DirectionAway(model, b, sharedNode, out bx, out by, out bz)) return false;

            double dot = ax * bx + ay * by + az * bz;
            if (dot < -1.0) dot = -1.0;
            if (dot > 1.0) dot = 1.0;

            double angleDeg = Math.Acos(dot) * 180.0 / Math.PI;
            double smaller = Math.Min(angleDeg, 180.0 - angleDeg);
            return smaller <= toleranceDeg + 1e-5;
        }

        /// <summary>Unit vector of a segment pointing away from the shared node.</summary>
        private static bool DirectionAway(IAxisVMModel model, ResolvedLine seg, int sharedNode, out double x, out double y, out double z)
        {
            int otherNode = (seg.NodeId1 == sharedNode) ? seg.NodeId2 : seg.NodeId1;
            AxisVM.RNode p0 = GetNode(model, sharedNode);
            AxisVM.RNode p1 = GetNode(model, otherNode);

            x = p1.x - p0.x;
            y = p1.y - p0.y;
            z = p1.z - p0.z;

            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-9) { x = y = z = 0; return false; }

            x /= len; y /= len; z /= len;
            return true;
        }

        internal static double ChordAngleDeg(AxisVM.RNode a, AxisVM.RNode b)
        {
            return Math.Atan2(b.y - a.y, b.x - a.x) * 180.0 / Math.PI;
        }

        internal static double Distance(AxisVM.RNode a, AxisVM.RNode b)
        {
            double dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static AxisVM.RNode GetNode(IAxisVMModel model, int nodeId)
        {
            AxisVM.RNode n = new AxisVM.RNode();
            model.Nodes.GetNode(nodeId, ref n);
            return n;
        }

        // ------------------------------------------------------------------
        // Small dictionary helpers
        // ------------------------------------------------------------------

        private static void AddAt(Dictionary<int, List<int>> map, int key, int value)
        {
            List<int> bucket;
            if (!map.TryGetValue(key, out bucket)) { bucket = new List<int>(); map[key] = bucket; }
            bucket.Add(value);
        }

        private static void Bump(Dictionary<int, int> counts, int key)
        {
            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;
        }
    }
}
