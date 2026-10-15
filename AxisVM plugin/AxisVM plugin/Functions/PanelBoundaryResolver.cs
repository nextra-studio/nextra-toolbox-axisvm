using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Turns an existing load panel's outline into boundary lines for <see cref="OneWayPanelBuilder"/>,
    /// using the members that are already in the model.
    ///
    /// Each outline edge is covered by the existing lines lying on it, beams / ribs / trusses first, so
    /// the one-way panel sends its load to real members. This matters because a panel drawn as a
    /// polygon has no contour lines: drawing new lines along its outline put a plain line on top of
    /// every edge beam, the panel then sent its load to those plain lines, and the load never reached
    /// the structure. A plain line is now only created to bridge a stretch that no line covers.
    /// </summary>
    internal static class PanelBoundaryResolver
    {
        private const double Tol = 1e-3; // [m] point-on-edge tolerance

        // Rank of a line as a load receiver: members first, then domain edges, then plain lines.
        private const int RankMember = 2, RankEdge = 1, RankPlain = 0;

        internal sealed class Result
        {
            public List<ResolvedLine> Segments = new List<ResolvedLine>();
            public List<int> CreatedLineUids = new List<int>(); // plain lines added where no line covered the outline
            public int ReplacedLines;  // contour lines skipped in favour of a coincident member
            public int PlainLines;     // boundary lines without a beam / rib / truss element
        }

        /// <summary>Removes the lines <see cref="TryResolve"/> created, when the panel is not converted after all.</summary>
        internal static void DeleteCreatedLines(IAxisVMModel model, Result result)
        {
            foreach (int uid in result.CreatedLineUids)
            {
                int id = model.Lines.get_IndexOfUID(uid);
                if (id > 0) model.Lines.Delete(id);
            }
            result.CreatedLineUids.Clear();
        }

        private struct P3 { public double X, Y, Z; }

        private sealed class LineInfo
        {
            public int Id, N1, N2, Rank;
            public P3 A, B;
        }

        private sealed class Candidate
        {
            public LineInfo Line;
            public double Ta, Tb;      // positions along the edge, Ta < Tb
            public int NodeA, NodeB;   // the line's nodes at Ta and Tb
        }

        internal static bool TryResolve(IAxisVMModel model, int panelIdx, out Result result, out string err)
        {
            result = new Result();
            List<int> contourIds;
            List<P3[]> outline = ReadOutline(model, panelIdx, out contourIds, out err);
            if (outline == null) return false;

            Dictionary<int, P3> nodeCache = new Dictionary<int, P3>();
            List<LineInfo> lines = IndexLines(model, nodeCache);

            HashSet<int> used = new HashSet<int>();
            foreach (P3[] edge in outline)
            {
                List<int> ids;
                if (!CoverEdge(model, lines, nodeCache, edge[0], edge[1], result, out ids, out err))
                {
                    DeleteCreatedLines(model, result);
                    return false;
                }
                foreach (int id in ids)
                {
                    if (used.Add(id)) result.Segments.Add(OneWayPanelBuilder.ResolveLine(model, id));
                }
            }

            foreach (int id in contourIds) if (!used.Contains(id)) result.ReplacedLines++;

            if (result.Segments.Count < 3)
            {
                DeleteCreatedLines(model, result);
                err = "the outline resolved to fewer than three lines";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the panel outline as straight edges: from its contour lines when it is associated
        /// with lines, otherwise from its contour polygon (a panel drawn as a free polygon).
        /// </summary>
        private static List<P3[]> ReadOutline(IAxisVMModel model, int panelIdx, out List<int> contourIds, out string err)
        {
            err = null;
            contourIds = new List<int>();
            List<P3[]> edges = new List<P3[]>();
            IAxisVMLoadPanel lp = model.LoadPanels.Item[panelIdx];

            Array arr = null;
            int count = 0;
            try { count = lp.GetContourLineIDs(ref arr); } catch { count = 0; }

            if (count > 0 && arr != null)
            {
                foreach (object o in arr)
                {
                    int id = Convert.ToInt32(o);
                    if (id <= 0) continue;
                    IAxisVMLine line = model.Lines.Item[id];
                    if (line.GeomType != ELineGeomType.lgtStraightLine) { err = "the outline has a curved line"; return null; }
                    contourIds.Add(id);
                    edges.Add(new P3[] { NodePoint(model, line.StartNode), NodePoint(model, line.EndNode) });
                }
                if (edges.Count >= 3) return edges;
                edges.Clear();
                contourIds.Clear();
            }

            AxisVMLines3d poly = null;
            int n;
            try { n = lp.GetContourPolygon(out poly); }
            catch (Exception ex) { err = "its outline could not be read (" + ex.Message + ")"; return null; }
            if (poly == null || n < 3) { err = "its outline could not be read"; return null; }

            for (int i = 1; i <= poly.Count; i++)
            {
                RLine3d seg = new RLine3d();
                poly.GetItem(i, ref seg);
                if (seg.LineType != ELine3dType.ltStraightLine3d) { err = "the outline has a curved edge"; return null; }
                edges.Add(new P3[] { ToP3(seg.P1), ToP3(seg.P2) });
            }
            return edges;
        }

        /// <summary>Every straight line that could receive load, with its end points.</summary>
        private static List<LineInfo> IndexLines(IAxisVMModel model, Dictionary<int, P3> nodeCache)
        {
            List<LineInfo> lines = new List<LineInfo>();
            int count = model.Lines.Count;
            for (int i = 1; i <= count; i++)
            {
                IAxisVMLine l = model.Lines.Item[i];
                if (l.GeomType != ELineGeomType.lgtStraightLine) continue;
                int rank = RankOf(l.LineType);
                if (rank < 0) continue;
                lines.Add(MakeInfo(model, nodeCache, i, l.StartNode, l.EndNode, rank));
            }
            return lines;
        }

        private static int RankOf(ELineType type)
        {
            switch (type)
            {
                case ELineType.ltBeam:
                case ELineType.ltRib:
                case ELineType.ltTruss:
                    return RankMember;
                case ELineType.ltEdge:
                    return RankEdge;
                case ELineType.ltSimpleLine:
                    return RankPlain;
                default:
                    return -1; // holes, springs, gaps, links: never a load receiver
            }
        }

        /// <summary>
        /// Lines covering the edge A-B, walked from A to B. At each point the best-ranked line that
        /// starts there is taken; a stretch with no line is bridged by a new plain line.
        /// </summary>
        private static bool CoverEdge(IAxisVMModel model, List<LineInfo> lines, Dictionary<int, P3> nodeCache,
            P3 a, P3 b, Result result, out List<int> ids, out string err)
        {
            ids = new List<int>();
            err = null;

            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < Tol) return true;
            double ux = dx / length, uy = dy / length, uz = dz / length;

            List<Candidate> cands = new List<Candidate>();
            foreach (LineInfo li in lines)
            {
                double t1, t2;
                if (!OnEdge(li.A, a, ux, uy, uz, length, out t1) || !OnEdge(li.B, a, ux, uy, uz, length, out t2)) continue;
                if (Math.Abs(t2 - t1) < Tol) continue;
                cands.Add(t1 < t2
                    ? new Candidate { Line = li, Ta = t1, Tb = t2, NodeA = li.N1, NodeB = li.N2 }
                    : new Candidate { Line = li, Ta = t2, Tb = t1, NodeA = li.N2, NodeB = li.N1 });
            }

            double cur = 0.0;
            int curNode = 0;
            while (cur < length - Tol)
            {
                Candidate best = null;
                foreach (Candidate c in cands)
                {
                    if (Math.Abs(c.Ta - cur) > Tol || c.Tb <= cur + Tol) continue;
                    if (best == null || Better(c, best, curNode)) best = c;
                }

                if (best != null)
                {
                    ids.Add(best.Line.Id);
                    if (best.Line.Rank != RankMember) result.PlainLines++;
                    cur = best.Tb;
                    curNode = best.NodeB;
                    continue;
                }

                // Gap: bridge to the next line along the edge, or to the edge end.
                double next = length;
                int nextNode = 0;
                foreach (Candidate c in cands)
                {
                    if (c.Ta > cur + Tol && c.Ta < next) { next = c.Ta; nextNode = c.NodeA; }
                }

                int startNode = curNode > 0 ? curNode : NodeAt(model, a, ux, uy, uz, cur);
                int endNode = nextNode > 0 ? nextNode : NodeAt(model, a, ux, uy, uz, next);
                if (startNode <= 0 || endNode <= 0 || startNode == endNode) { err = "a node on the outline could not be created"; return false; }

                int lineId = OneWayPanelBuilder.AddStraightLine(model, startNode, endNode);
                if (lineId <= 0) { err = "a missing outline line could not be created"; return false; }

                LineInfo created = MakeInfo(model, nodeCache, lineId, startNode, endNode, RankPlain);
                lines.Add(created);
                ids.Add(lineId);
                result.CreatedLineUids.Add(model.Lines.get_UID(lineId));
                result.PlainLines++;
                cur = next;
                curNode = endNode;
            }
            return true;
        }

        private static bool Better(Candidate c, Candidate best, int curNode)
        {
            if (c.Line.Rank != best.Line.Rank) return c.Line.Rank > best.Line.Rank;
            bool cJoins = c.NodeA == curNode, bJoins = best.NodeA == curNode;
            if (cJoins != bJoins) return cJoins;
            return c.Tb > best.Tb;
        }

        /// <summary>True when <paramref name="p"/> lies on the edge; <paramref name="t"/> is its distance from A.</summary>
        private static bool OnEdge(P3 p, P3 a, double ux, double uy, double uz, double length, out double t)
        {
            double vx = p.X - a.X, vy = p.Y - a.Y, vz = p.Z - a.Z;
            t = vx * ux + vy * uy + vz * uz;
            if (t < -Tol || t > length + Tol) return false;
            double px = vx - t * ux, py = vy - t * uy, pz = vz - t * uz;
            if (px * px + py * py + pz * pz > Tol * Tol) return false;
            t = Math.Max(0.0, Math.Min(length, t));
            return true;
        }

        /// <summary>The node at a position along the edge. IAxisVMNodes.Add returns an existing node there.</summary>
        private static int NodeAt(IAxisVMModel model, P3 a, double ux, double uy, double uz, double t)
        {
            return model.Nodes.Add(a.X + ux * t, a.Y + uy * t, a.Z + uz * t);
        }

        private static LineInfo MakeInfo(IAxisVMModel model, Dictionary<int, P3> nodeCache, int id, int n1, int n2, int rank)
        {
            return new LineInfo { Id = id, N1 = n1, N2 = n2, Rank = rank, A = CachedNode(model, nodeCache, n1), B = CachedNode(model, nodeCache, n2) };
        }

        private static P3 CachedNode(IAxisVMModel model, Dictionary<int, P3> cache, int nodeId)
        {
            P3 p;
            if (!cache.TryGetValue(nodeId, out p))
            {
                p = NodePoint(model, nodeId);
                cache[nodeId] = p;
            }
            return p;
        }

        private static P3 NodePoint(IAxisVMModel model, int nodeId)
        {
            RNode n = EdgeChainBuilder.GetNode(model, nodeId);
            return new P3 { X = n.x, Y = n.y, Z = n.z };
        }

        private static P3 ToP3(RPoint3d p)
        {
            return new P3 { X = p.x, Y = p.y, Z = p.z };
        }
    }
}
