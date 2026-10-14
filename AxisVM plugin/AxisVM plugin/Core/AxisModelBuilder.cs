using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What the import dialog collected.</summary>
    public class ImportOptions
    {
        public AxUnits Units = new AxUnits();

        /// <summary>Two vertices closer than this become one AxisVM node [m].</summary>
        public double MergeToleranceM = 0.001;

        /// <summary>Parse and report only - nothing is written to the model.</summary>
        public bool DryRun;

        /// <summary>
        /// Before any attribute is applied, run AxisVM's line intersection (CrossLines) so lines that
        /// cross get a shared node, and split lines at every imported node lying on them (a support
        /// point or a beam end touching another beam). Each Rhino curve then becomes the chain of
        /// segments it was split into, and keeps behaving as ONE member: releases only at its two far
        /// ends, loads positioned along its whole length.
        /// </summary>
        public bool AutoIntersect = true;

        /// <summary>Where the JSON came from, used to place the id-map sidecar if one is needed.</summary>
        public string SourcePath = "";
    }

    /// <summary>Counters for the run summary.</summary>
    public class ImportStats
    {
        public int NodesCreated;
        public int NodesShared;
        public int LinesCreated;
        public int LinesMerged;
        public int LinesSplit;
        public int DomainsCreated;
        public int HolesCreated;
        public int PanelsCreated;
        public int SupportsCreated;
        public int LineSupportsCreated;
        public int SpringsCreated;
        public int LinksCreated;
        public int RigidBodiesCreated;
        public int HingesCreated;
        public int LoadsCreated;
        public int PartsCreated;
        public int AttributeErrors;
        public int MembersAssembled;
    }

    /// <summary>
    /// Rebuilds an exported Rhino document as an AxisVM model.
    ///
    /// The order matters and is the whole design:
    ///   1. geometry      - every coordinate becomes a node (merged within the tolerance) and every
    ///                      curve / surface edge a line; nothing has a type yet;
    ///   2. intersection  - CrossLines + splitting at nodes, which RE-INDEXES the whole model, so it
    ///                      runs before anything stores an id;
    ///   3. resolution    - each Rhino curve and surface edge is matched to the live segments it now
    ///                      consists of (its "chain");
    ///   4. attributes    - types, profiles, releases, domains, panels, supports, springs, links,
    ///                      hinges, loads and parts are all applied to the final ids.
    /// Running the intersection last (as the earlier version did) left parts, id stamps and releases
    /// pointing at stale line/node numbers.
    /// </summary>
    public partial class AxisModelBuilder
    {
        private readonly IAxisVMApplication app;
        private readonly IAxisVMModel model;
        private readonly RhinoModel rhino;
        private readonly ImportOptions opt;
        private readonly List<string> notes;
        private readonly CatalogResolver catalog;
        private readonly SpringParamResolver springs;
        private readonly ImportStats stats = new ImportStats();

        /// <summary>Nodes created/reused by this import, by position (metres).</summary>
        private readonly NodeIndex nodes;
        private readonly List<double[]> importedNodePositions = new List<double[]>();

        /// <summary>Pre-intersection lines by node pair (straight) or by node pair + arc mid point.</summary>
        private readonly Dictionary<string, BuiltLine> builtByKey = new Dictionary<string, BuiltLine>(StringComparer.Ordinal);

        private readonly List<LineJob> lineJobs = new List<LineJob>();
        private readonly List<SurfaceJob> surfaceJobs = new List<SurfaceJob>();

        /// <summary>Live line id -> the Rhino curve that decided its element type.</summary>
        private readonly Dictionary<int, LineJob> segmentOwner = new Dictionary<int, LineJob>();

        /// <summary>Live line id -> the element type actually applied (for picking the right load call).</summary>
        private readonly Dictionary<int, LineRole> segmentRole = new Dictionary<int, LineRole>();

        public ImportStats Stats { get { return stats; } }

        public AxisModelBuilder(IAxisVMApplication app, IAxisVMModel model, RhinoModel rhino, ImportOptions opt, List<string> notes)
        {
            this.app = app;
            this.model = model;
            this.rhino = rhino;
            this.opt = opt;
            this.notes = notes;
            this.catalog = new CatalogResolver(model, notes, rhino.Library);
            this.springs = new SpringParamResolver(model, notes);
            this.nodes = new NodeIndex(opt.MergeToleranceM);
        }

        // ==================================================================
        // Roles
        // ==================================================================

        public enum LineRole { None, Beam, Rib, Truss, Spring, RigidLink, NodeLink, EdgeHinge, Simple, Unknown }
        public enum SurfaceRole { Shell, Plate, Membrane, LoadPanel, OneWayLoadPanel, Unknown }

        /// <summary>Same spellings as the Rhino side (AxSchema.NormalizeLineType).</summary>
        public static LineRole ParseLineRole(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return LineRole.None;
            string k = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            switch (k)
            {
                case "beam": return LineRole.Beam;
                case "rib": return LineRole.Rib;
                case "truss": return LineRole.Truss;
                case "spring": return LineRole.Spring;
                case "rigidlink": case "rigid": case "rigidbody": return LineRole.RigidLink;
                case "nodelink": case "nodenodelink": case "nnlink": case "link": return LineRole.NodeLink;
                case "edgehinge": case "hinge": return LineRole.EdgeHinge;
                case "simple": case "simpleline": case "line": return LineRole.Simple;
                default: return LineRole.Unknown;
            }
        }

        public static SurfaceRole ParseSurfaceRole(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return SurfaceRole.Shell;
            string k = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            switch (k)
            {
                case "shell": return SurfaceRole.Shell;
                case "plate": return SurfaceRole.Plate;
                case "membrane": return SurfaceRole.Membrane;
                case "loadpanel": case "panel": case "twowayloadpanel": case "loadpaneltwoway": return SurfaceRole.LoadPanel;
                case "onewayloadpanel": case "onewaypanel": case "loadpaneloneway": case "oneway": return SurfaceRole.OneWayLoadPanel;
                default: return SurfaceRole.Unknown;
            }
        }

        private static bool IsFrame(LineRole r) { return r == LineRole.Beam || r == LineRole.Rib || r == LineRole.Truss; }
        private static bool IsConnector(LineRole r) { return r == LineRole.Spring || r == LineRole.RigidLink || r == LineRole.NodeLink; }
        private static bool IsPanel(SurfaceRole r) { return r == SurfaceRole.LoadPanel || r == SurfaceRole.OneWayLoadPanel; }

        // ==================================================================
        // Jobs
        // ==================================================================

        /// <summary>One AxisVM line as created in the geometry pass (before intersection).</summary>
        private class BuiltLine
        {
            public int Id;
            public int NodeA;
            public int NodeB;
            public bool Arc;
            public bool UsedByCrossable;
            public bool UsedByConnector;
            public int RhinoUses;
        }

        /// <summary>A segment of a chain, with its direction relative to the Rhino curve.</summary>
        private class SegRef
        {
            public int Id;
            /// <summary>The AxisVM line runs against the Rhino curve direction (its start node is at the far side).</summary>
            public bool Reversed;
            public double Length;
            /// <summary>Which straight piece of a polyline this segment belongs to (members are assembled per piece).</summary>
            public int Piece;
        }

        private class Chain
        {
            public readonly List<SegRef> Segs = new List<SegRef>();
            public double Total;
            public bool IsEmpty { get { return Segs.Count == 0; } }

            public void Add(SegRef s) { Segs.Add(s); Total += s.Length; }

            public List<int> Ids()
            {
                List<int> l = new List<int>();
                foreach (SegRef s in Segs) l.Add(s.Id);
                return l;
            }
        }

        private class LineJob
        {
            public RhinoLine Src;
            public LineRole Role;
            public string Context;
            public List<double[]> Path = new List<double[]>();   // vertices in metres
            public double[] ArcMid;                               // metres, arcs only
            public Chain Chain = new Chain();
            public bool Crossable { get { return !IsConnector(Role) && ArcMid == null; } }
        }

        private class SurfaceJob
        {
            public RhinoDomain Src;
            public SurfaceRole Role;
            public string Context;
            public List<double[]> Outer = new List<double[]>();
            public List<List<double[]>> Holes = new List<List<double[]>>();
            public Chain OuterChain = new Chain();
            public List<Chain> HoleChains = new List<Chain>();
            public int DomainId;
            public List<int> PanelIds = new List<int>();
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        public void Run()
        {
            ValidateAttributeNames();
            PrepareJobs();

            if (!opt.DryRun) catalog.CreateLibraryLoadCases();

            BuildGeometry();

            if (opt.AutoIntersect && !opt.DryRun) IntersectAndResolve();

            ApplyMemberTypes();
            BuildSurfaces();
            ApplySupports();
            BuildConnectors();
            BuildEdgeHinges();
            BuildLoads();
            BuildParts();
            StampRhinoIds();
        }

        /// <summary>
        /// Flags Ax_* keys this version does not know, before anything is written. A typo such as
        /// "Ax_Materal" would otherwise be silently ignored and the beam would quietly get the
        /// fallback material - the single most confusing way for this import to go wrong.
        /// </summary>
        private void ValidateAttributeNames()
        {
            List<RhinoEntity> all = new List<RhinoEntity>();
            foreach (RhinoPoint p in rhino.Points) all.Add(p);
            foreach (RhinoLine l in rhino.Lines) all.Add(l);
            foreach (RhinoDomain d in rhino.Domains) all.Add(d);

            HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RhinoEntity e in all)
            {
                foreach (string key in e.Attrs.Keys)
                {
                    if (!key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (AxKeys.IsKnown(key)) continue;
                    if (!reported.Add(key)) continue;

                    notes.Add(string.Format("Attribute '{0}' is not one this addon knows and was ignored. Check it for a typo.", key));
                    stats.AttributeErrors++;
                }
            }
        }

        /// <summary>Reads each Rhino curve and surface once: its role and its vertices in metres.</summary>
        private void PrepareJobs()
        {
            foreach (RhinoLine l in rhino.Lines)
            {
                LineJob job = new LineJob { Src = l, Context = l.Label("line"), Role = ParseLineRole(l.Attr(AxKeys.Type)) };
                if (job.Role == LineRole.Unknown)
                {
                    notes.Add(string.Format("{0}: Ax_Type '{1}' is not a known line type - it is imported as a plain line.", job.Context, l.Attr(AxKeys.Type)));
                    stats.AttributeErrors++;
                    job.Role = LineRole.None;
                }

                foreach (RhinoXyz v in l.Vertices) job.Path.Add(M(v));
                if (l.IsArc) job.ArcMid = M(l.ArcMid);

                if (IsConnector(job.Role) && job.Path.Count > 2)
                {
                    notes.Add(string.Format("{0}: a {1} must be a single straight line between two points; only its two end points were used.", job.Context, job.Role));
                    job.Path = new List<double[]> { job.Path[0], job.Path[job.Path.Count - 1] };
                }
                lineJobs.Add(job);
            }

            foreach (RhinoDomain d in rhino.Domains)
            {
                SurfaceJob job = new SurfaceJob { Src = d, Context = d.Label("surface") };
                job.Role = ParseSurfaceRole(d.Attr(AxKeys.FeType));
                if (job.Role == SurfaceRole.Unknown)
                {
                    notes.Add(string.Format("{0}: Ax_FEType '{1}' is not known - a Shell domain is created instead.", job.Context, d.Attr(AxKeys.FeType)));
                    stats.AttributeErrors++;
                    job.Role = SurfaceRole.Shell;
                }
                foreach (RhinoXyz v in d.Contour) job.Outer.Add(M(v));
                foreach (List<RhinoXyz> hole in d.Holes)
                {
                    List<double[]> h = new List<double[]>();
                    foreach (RhinoXyz v in hole) h.Add(M(v));
                    job.Holes.Add(h);
                }
                surfaceJobs.Add(job);
            }
        }

        private double[] M(RhinoXyz p)
        {
            return new[] { opt.Units.Length(p.X), opt.Units.Length(p.Y), opt.Units.Length(p.Z) };
        }

        // ==================================================================
        // 1 - geometry
        // ==================================================================

        private void BuildGeometry()
        {
            foreach (RhinoPoint p in rhino.Points) NodeFor(M(p.Xyz));

            // Curves first, so a slab edge drawn exactly on top of a beam reuses the beam's line.
            foreach (LineJob job in lineJobs)
            {
                if (job.ArcMid != null)
                {
                    int a = NodeFor(job.Path[0]);
                    int b = NodeFor(job.Path[job.Path.Count - 1]);
                    if (a == 0 || b == 0 || a == b) { SkipShort(job); continue; }
                    BuiltLine bl = ArcLineFor(a, b, job.Path[0], job.ArcMid, job.Path[job.Path.Count - 1], job.Context);
                    if (bl == null) continue;
                    bl.UsedByCrossable = false;
                    bl.UsedByConnector = true;   // never handed to CrossLines (see IntersectAndResolve)
                    bl.RhinoUses++;
                    job.Chain.Add(new SegRef { Id = bl.Id, Reversed = bl.NodeA != a, Length = ArcLength(job.Path[0], job.ArcMid, job.Path[job.Path.Count - 1]) });
                    continue;
                }

                for (int i = 0; i + 1 < job.Path.Count; i++)
                {
                    int a = NodeFor(job.Path[i]);
                    int b = NodeFor(job.Path[i + 1]);
                    if (a == 0 || b == 0 || a == b) continue;

                    BuiltLine bl = LineFor(a, b);
                    if (bl == null) continue;
                    if (job.Crossable) bl.UsedByCrossable = true; else bl.UsedByConnector = true;
                    bl.RhinoUses++;
                    job.Chain.Add(new SegRef { Id = bl.Id, Reversed = bl.NodeA != a, Length = Dist(job.Path[i], job.Path[i + 1]), Piece = i });
                }

                if (job.Chain.IsEmpty) SkipShort(job);
            }

            foreach (SurfaceJob s in surfaceJobs)
            {
                s.OuterChain = LoopFor(s.Outer);
                foreach (List<double[]> h in s.Holes) s.HoleChains.Add(LoopFor(h));
            }

            foreach (BuiltLine bl in builtByKey.Values)
                if (bl.RhinoUses > 1) stats.LinesMerged += bl.RhinoUses - 1;
            if (stats.LinesMerged > 0)
                notes.Add(string.Format("{0} Rhino curve(s) lay exactly on another curve and share its AxisVM line (the first typed curve decides the element type).", stats.LinesMerged));
        }

        private void SkipShort(LineJob job)
        {
            notes.Add(string.Format("{0}: it is shorter than the merge tolerance (both ends on one node). Skipped.", job.Context));
        }

        private Chain LoopFor(List<double[]> loop)
        {
            Chain c = new Chain();
            for (int i = 0; i < loop.Count; i++)
            {
                double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                int a = NodeFor(p);
                int b = NodeFor(q);
                if (a == 0 || b == 0 || a == b) continue;
                BuiltLine bl = LineFor(a, b);
                if (bl == null) continue;
                bl.UsedByCrossable = true;
                c.Add(new SegRef { Id = bl.Id, Reversed = bl.NodeA != a, Length = Dist(p, q), Piece = i });
            }
            return c;
        }

        /// <summary>
        /// The single door through which every coordinate becomes a node: looks for a node within the
        /// merge tolerance and only adds one when there is none.
        /// </summary>
        private int NodeFor(double[] p)
        {
            int existing;
            if (nodes.TryFind(p[0], p[1], p[2], out existing))
            {
                stats.NodesShared++;
                return existing;
            }

            int id;
            if (!opt.DryRun)
            {
                try { id = model.Nodes.Add(p[0], p[1], p[2]); }
                catch (Exception ex)
                {
                    notes.Add(string.Format("A node at {0} could not be created ({1}).", Fmt(p), ex.Message));
                    return 0;
                }
                if (id <= 0)
                {
                    notes.Add(string.Format("AxisVM rejected a node at {0} (code {1}).", Fmt(p), id));
                    return 0;
                }
            }
            else
            {
                // On a dry run the ids are synthetic, so the counts and the merge logic still work.
                id = -(stats.NodesCreated + 1);
            }

            nodes.Add(p[0], p[1], p[2], id);
            importedNodePositions.Add(p);
            stats.NodesCreated++;
            return id;
        }

        /// <summary>Finds or creates the single straight AxisVM line joining two nodes.</summary>
        private BuiltLine LineFor(int nodeA, int nodeB)
        {
            string key = PairKey(nodeA, nodeB);
            BuiltLine existing;
            if (builtByKey.TryGetValue(key, out existing)) return existing;

            int id;
            if (!opt.DryRun)
            {
                RLineGeomData geom = new RLineGeomData();
                try { id = model.Lines.Add(nodeA, nodeB, ELineGeomType.lgtStraightLine, ref geom); }
                catch (Exception ex)
                {
                    notes.Add(string.Format("A line between nodes {0} and {1} could not be created ({2}).", nodeA, nodeB, ex.Message));
                    return null;
                }
                if (id <= 0)
                {
                    notes.Add(string.Format("AxisVM rejected a line between nodes {0} and {1} (code {2}).", nodeA, nodeB, id));
                    return null;
                }
            }
            else id = -(stats.LinesCreated + 1);

            BuiltLine bl = new BuiltLine { Id = id, NodeA = nodeA, NodeB = nodeB };
            builtByKey[key] = bl;
            stats.LinesCreated++;
            return bl;
        }

        /// <summary>
        /// Creates a true circular arc through start, mid and end. AxisVM describes an arc by centre,
        /// plane normal and signed sweep angle; the sign convention is verified against AxisVM's own
        /// midpoint afterwards and flipped if the arc came out on the wrong side.
        /// </summary>
        private BuiltLine ArcLineFor(int nodeA, int nodeB, double[] s, double[] m, double[] e, string context)
        {
            string key = "arc|" + PairKey(nodeA, nodeB) + "|" + Fmt(m);
            BuiltLine existing;
            if (builtByKey.TryGetValue(key, out existing)) return existing;

            double[] center, normal;
            double sweep;
            if (!ArcData(s, m, e, out center, out normal, out sweep))
            {
                notes.Add(context + ": the arc is degenerate (its three points are collinear); a straight line was used.");
                return LineFor(nodeA, nodeB);
            }

            int id;
            if (!opt.DryRun)
            {
                RLineGeomData geom = ArcGeom(center, normal, sweep);
                try { id = model.Lines.Add(nodeA, nodeB, ELineGeomType.lgtCircleArc, ref geom); }
                catch (Exception ex) { id = 0; notes.Add(context + ": the arc could not be created (" + ex.Message + "); a straight line was used."); }
                if (id <= 0) return LineFor(nodeA, nodeB);

                if (!ArcMatches(id, m))
                {
                    bool fixedIt = false;
                    double[][] normals = { normal, Neg(normal) };
                    foreach (double[] n in normals)
                    {
                        foreach (double a in new[] { sweep, -sweep })
                        {
                            try
                            {
                                RLineGeomData g = ArcGeom(center, n, a);
                                model.Lines.Item[id].SetGeomData(ref g);
                            }
                            catch { continue; }
                            if (ArcMatches(id, m)) { fixedIt = true; break; }
                        }
                        if (fixedIt) break;
                    }
                    if (!fixedIt) notes.Add(context + ": AxisVM's arc does not pass through the Rhino arc's mid point - please check its geometry.");
                }
            }
            else id = -(stats.LinesCreated + 1);

            BuiltLine bl = new BuiltLine { Id = id, NodeA = nodeA, NodeB = nodeB, Arc = true };
            builtByKey[key] = bl;
            stats.LinesCreated++;
            return bl;
        }

        private bool ArcMatches(int lineId, double[] mid)
        {
            try
            {
                RPoint3d p = new RPoint3d();
                model.Lines.Item[lineId].GetMidPoint(ref p);
                return Dist(new[] { p.x, p.y, p.z }, mid) <= Math.Max(opt.MergeToleranceM * 10, 1e-3);
            }
            catch { return true; }   // cannot verify - trust the computed data
        }

        private static RLineGeomData ArcGeom(double[] c, double[] n, double sweep)
        {
            RLineGeomData g = new RLineGeomData();
            RCircleArcGeomData a = new RCircleArcGeomData();
            a.Center = new RPoint3d { x = c[0], y = c[1], z = c[2] };
            a.NormalVector = new RPoint3d { x = n[0], y = n[1], z = n[2] };
            a.Alfa = sweep;
            g.CircleArc = a;
            return g;
        }

        // ==================================================================
        // 2 + 3 - intersection and chain resolution
        // ==================================================================

        /// <summary>A line as it exists in the model after intersection.</summary>
        private class LiveSeg
        {
            public int Id;
            public int NodeA;
            public int NodeB;
            public double[] A;
            public double[] B;
        }

        private void IntersectAndResolve()
        {
            double tol = Math.Max(opt.MergeToleranceM, 1e-6);

            // Springs, links and arcs are never intersected: a rigid link that happens to cross a beam
            // in space must stay one element joining its own two end nodes.
            List<int> crossIds = new List<int>();
            bool anyProtected = false;
            foreach (BuiltLine bl in builtByKey.Values)
            {
                if (bl.Id <= 0) continue;
                if (bl.UsedByCrossable && !bl.UsedByConnector) crossIds.Add(bl.Id);
                else anyProtected = true;
            }

            int linesBefore = SafeLineCount();
            if (crossIds.Count > 1)
            {
                try { model.Lines.CrossLines(Ids(crossIds.ToArray())); }
                catch (Exception ex) { notes.Add("Auto-intersection: CrossLines failed (" + ex.Message + "); lines were left as drawn."); }
            }

            SplitAtImportedNodes(tol);

            int linesAfter = SafeLineCount();
            if (linesAfter > linesBefore)
            {
                stats.LinesSplit = linesAfter - linesBefore;
                stats.LinesCreated += linesAfter - linesBefore;
            }
            if (anyProtected)
                notes.Add("Auto-intersection: springs, links and arcs were kept out of the intersection (they keep joining exactly their own end points).");

            ResolveChains(tol);
        }

        /// <summary>
        /// Splits straight lines at every imported node lying inside them - a support point in the
        /// middle of a beam, a beam end landing on a slab edge, a link end on a girder. Without this
        /// such a node sits on the line but is not connected to it.
        /// </summary>
        private void SplitAtImportedNodes(double tol)
        {
            for (int guard = 0; guard < 5000; guard++)
            {
                LiveModel live = ReadLive(tol);
                HashSet<int> protectedIds = ProtectedLines(live);

                bool split = false;
                foreach (double[] p in importedNodePositions)
                {
                    int nodeId;
                    if (!live.Index.TryFind(p[0], p[1], p[2], out nodeId)) continue;

                    foreach (LiveSeg s in live.Segs)
                    {
                        if (protectedIds.Contains(s.Id) || s.NodeA == nodeId || s.NodeB == nodeId) continue;
                        if (!Interior(s.A, s.B, p, tol)) continue;

                        try
                        {
                            int l1, l2;
                            model.Lines.Item[s.Id].SplitByNode(nodeId, out l1, out l2);
                            split = true;
                        }
                        catch { }
                        break;
                    }
                    if (split) break;   // indices changed - re-read the model
                }
                if (!split) return;
            }
        }

        /// <summary>Live lines that belong to springs, links or arcs (matched by their end nodes).</summary>
        private HashSet<int> ProtectedLines(LiveModel live)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (LineJob job in lineJobs)
            {
                if (job.Crossable || job.Path.Count < 2) continue;
                int a, b;
                if (!live.Index.TryFind(job.Path[0][0], job.Path[0][1], job.Path[0][2], out a)) continue;
                double[] e = job.Path[job.Path.Count - 1];
                if (!live.Index.TryFind(e[0], e[1], e[2], out b)) continue;
                List<LiveSeg> hits;
                if (live.ByPair.TryGetValue(PairKey(a, b), out hits))
                    foreach (LiveSeg s in hits) ids.Add(s.Id);
            }
            return ids;
        }

        private class LiveModel
        {
            public NodeIndex Index;
            public readonly List<LiveSeg> Segs = new List<LiveSeg>();
            public readonly Dictionary<string, List<LiveSeg>> ByPair = new Dictionary<string, List<LiveSeg>>(StringComparer.Ordinal);
        }

        private LiveModel ReadLive(double tol)
        {
            LiveModel live = new LiveModel { Index = new NodeIndex(tol) };
            Dictionary<int, double[]> xyz = new Dictionary<int, double[]>();

            int nodeCount = SafeNodeCount();
            for (int i = 1; i <= nodeCount; i++)
            {
                double[] c = NodeXyz(i);
                if (c == null) continue;
                xyz[i] = c;
                live.Index.Add(c[0], c[1], c[2], i);
            }

            int lineCount = SafeLineCount();
            for (int i = 1; i <= lineCount; i++)
            {
                try
                {
                    IAxisVMLine ln = model.Lines.Item[i];
                    int na = ln.StartNode, nb = ln.EndNode;
                    double[] a, b;
                    if (!xyz.TryGetValue(na, out a) || !xyz.TryGetValue(nb, out b)) continue;
                    LiveSeg s = new LiveSeg { Id = i, NodeA = na, NodeB = nb, A = a, B = b };
                    live.Segs.Add(s);

                    string key = PairKey(na, nb);
                    List<LiveSeg> list;
                    if (!live.ByPair.TryGetValue(key, out list)) { list = new List<LiveSeg>(); live.ByPair[key] = list; }
                    list.Add(s);
                }
                catch { }
            }
            return live;
        }

        /// <summary>Re-identifies every curve and surface edge as the chain of live segments it became.</summary>
        private void ResolveChains(double tol)
        {
            LiveModel live = ReadLive(tol);
            liveNodes = live.Index;   // node ids changed - every later lookup uses the live index
            HashSet<int> protectedIds = ProtectedLines(live);

            List<LiveSeg> crossable = new List<LiveSeg>();
            foreach (LiveSeg s in live.Segs) if (!protectedIds.Contains(s.Id)) crossable.Add(s);

            foreach (LineJob job in lineJobs)
            {
                if (job.Chain.IsEmpty) continue;   // skipped in the geometry pass

                Chain c = new Chain();
                if (!job.Crossable)
                {
                    SegRef r = DirectSeg(live, job.Path[0], job.Path[job.Path.Count - 1], tol);
                    if (r != null) { r.Length = job.Chain.Total; c.Add(r); }
                }
                else
                {
                    for (int i = 0; i + 1 < job.Path.Count; i++)
                    {
                        if (Dist(job.Path[i], job.Path[i + 1]) <= tol) continue;
                        List<SegRef> piece = ChainAlong(job.Path[i], job.Path[i + 1], crossable, tol, i);
                        if (piece.Count == 0) { c = new Chain(); break; }
                        foreach (SegRef r in piece) c.Add(r);
                    }
                }

                if (c.IsEmpty)
                    notes.Add(string.Format("{0}: its segments could not be found after auto-intersection; its attributes were not applied.", job.Context));
                job.Chain = c;
            }

            foreach (SurfaceJob s in surfaceJobs)
            {
                s.OuterChain = LoopAlong(s.Outer, crossable, tol);
                List<Chain> holes = new List<Chain>();
                foreach (List<double[]> h in s.Holes) holes.Add(LoopAlong(h, crossable, tol));
                s.HoleChains = holes;
            }
        }

        private SegRef DirectSeg(LiveModel live, double[] p0, double[] p1, double tol)
        {
            int a, b;
            if (!live.Index.TryFind(p0[0], p0[1], p0[2], out a) || !live.Index.TryFind(p1[0], p1[1], p1[2], out b)) return null;
            List<LiveSeg> hits;
            if (!live.ByPair.TryGetValue(PairKey(a, b), out hits) || hits.Count == 0) return null;
            return new SegRef { Id = hits[0].Id, Reversed = hits[0].NodeA != a, Length = Dist(p0, p1) };
        }

        private Chain LoopAlong(List<double[]> loop, List<LiveSeg> segs, double tol)
        {
            Chain c = new Chain();
            for (int i = 0; i < loop.Count; i++)
            {
                double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                if (Dist(p, q) <= tol) continue;
                List<SegRef> piece = ChainAlong(p, q, segs, tol, i);
                if (piece.Count == 0) return new Chain();
                foreach (SegRef r in piece) c.Add(r);
            }
            return c;
        }

        /// <summary>
        /// The live straight segments that together run from p0 to p1, ordered from p0, or an empty
        /// list when the run cannot be matched without a gap.
        /// </summary>
        private static List<SegRef> ChainAlong(double[] p0, double[] p1, List<LiveSeg> segs, double tol, int piece)
        {
            List<SegRef> result = new List<SegRef>();

            double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
            double len = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (len <= tol) return result;
            ux /= len; uy /= len; uz /= len;

            double slack = tol * 4.0;
            List<KeyValuePair<double, LiveSeg>> hits = new List<KeyValuePair<double, LiveSeg>>();
            Dictionary<int, double[]> spans = new Dictionary<int, double[]>();

            foreach (LiveSeg s in segs)
            {
                double ta, tb;
                if (!OnLine(p0, ux, uy, uz, s.A, tol, out ta)) continue;
                if (!OnLine(p0, ux, uy, uz, s.B, tol, out tb)) continue;

                double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
                if (hi - lo <= tol) continue;
                if (lo < -slack || hi > len + slack) continue;

                hits.Add(new KeyValuePair<double, LiveSeg>(lo, s));
                spans[s.Id] = new[] { ta, tb };
            }

            if (hits.Count == 0) return result;
            hits.Sort((x, y) => x.Key.CompareTo(y.Key));

            double cursor = 0.0;
            foreach (var h in hits)
            {
                double[] t = spans[h.Value.Id];
                double lo = Math.Min(t[0], t[1]), hi = Math.Max(t[0], t[1]);
                if (lo > cursor + slack) return new List<SegRef>();   // gap
                if (hi <= cursor + slack) continue;                    // duplicate / overlapping segment
                result.Add(new SegRef { Id = h.Value.Id, Reversed = t[0] > t[1], Length = hi - lo, Piece = piece });
                cursor = hi;
            }

            if (cursor < len - slack) return new List<SegRef>();
            return result;
        }

        private static bool OnLine(double[] p0, double ux, double uy, double uz, double[] q, double tol, out double t)
        {
            double dx = q[0] - p0[0], dy = q[1] - p0[1], dz = q[2] - p0[2];
            t = dx * ux + dy * uy + dz * uz;
            double px = dx - t * ux, py = dy - t * uy, pz = dz - t * uz;
            return Math.Sqrt(px * px + py * py + pz * pz) <= Math.Max(tol, 1e-6) * 2.0;
        }

        /// <summary>True when p lies on segment a-b, strictly between its ends.</summary>
        private static bool Interior(double[] a, double[] b, double[] p, double tol)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double len = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (len <= 2 * tol) return false;
            ux /= len; uy /= len; uz /= len;
            double t;
            if (!OnLine(a, ux, uy, uz, p, tol, out t)) return false;
            return t > tol && t < len - tol;
        }
    }
}
