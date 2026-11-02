using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What the link remembers from the last load, and the settings of this one (LinkDiff input).</summary>
    public class LinkDiffInput
    {
        public string Key = "";
        /// <summary>Signature of every object Rhino holds now (LinkSignature.All).</summary>
        public Dictionary<string, string> Signatures;
        /// <summary>Signatures recorded at the last load / send ("sigs" of the axisvm section).</summary>
        public Dictionary<string, string> Previous;
        public string Settings = "", PreviousSettings = "";
        public HashSet<string> RhinoKnows;
        /// <summary>Outline state -> guid of load panels "Send to Rhino" adopted ("panelIds").</summary>
        public Dictionary<string, string> AdoptedPanels;
        /// <summary>Load panel UID -> the Rhino surface it was built from ("panelUids").</summary>
        public Dictionary<int, string> PanelGuids;
        /// <summary>What the last load managed to build ("built" + the panels); null when unknown.</summary>
        public HashSet<string> PreviousBuilt;
        public AxUnits Units = new AxUnits();
        public double ToleranceM = 0.001;
    }

    /// <summary>
    /// What "only changes" will rebuild. <see cref="Everything"/> means the link has no record to compare with (or the
    /// units changed), and the update replaces everything as before.
    /// </summary>
    public class LinkDiffPlan
    {
        public bool Everything;
        public string Reason = "";

        /// <summary>Rhino objects to (re)build.</summary>
        public readonly HashSet<string> Build = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Unchanged, Changed, Added, Deleted, Neighbours;
        /// <summary>Unchanged objects AxisVM could not build last time either (a rejected domain...) - not tried again.</summary>
        public int Unbuildable;

        // What to delete before the build (UIDs; part ids).
        internal readonly List<int> LineUids = new List<int>(), DomainUids = new List<int>(), PanelUids = new List<int>(), NodeUids = new List<int>();
        internal readonly List<int> PartIds = new List<int>();
        /// <summary>Kept nodes of rebuilt points that carry Rhino's support: the support is rebuilt, so the old one goes.</summary>
        internal readonly List<int> SupportNodeUids = new List<int>();
        /// <summary>
        /// Lines of rebuilt curves whose supports / hinges were Rhino's (the last load built them from Ax_Support or an
        /// EdgeHinge curve): never carried over - the rebuild makes them as Rhino says now, or not at all.
        /// </summary>
        internal readonly HashSet<int> RhinoSupportLineUids = new HashSet<int>();
        /// <summary>Line UID -> domain guids of the rebuilt Rhino edge hinge curves on it (null = every domain along it).</summary>
        internal readonly Dictionary<int, HashSet<string>> RhinoHingeLines = new Dictionary<int, HashSet<string>>();
        /// <summary>
        /// Kept member lines a rebuilt edge hinge / plain line runs along: the line stays (the member is kept), only the
        /// edge connections and line supports on it go before the rebuild makes Rhino's again.
        /// </summary>
        internal readonly HashSet<int> CleanLineUids = new HashSet<int>();
        /// <summary>Rebuilt objects Rhino did not change (pulled in as neighbours): a domain among them that AxisVM refuses to
        /// rebuild from Rhino's outline is made again from its previous outline.</summary>
        public readonly HashSet<string> PulledIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Deleted lines that kept non-member curves (edge hinges, plain lines) ran along: their ids go onto the rebuilt pieces.</summary>
        internal readonly List<KeyValuePair<double[][], string>> KeptOwners = new List<KeyValuePair<double[][], string>>();

        /// <summary>
        /// Diaphragm name -> the kept objects in it. A rebuilt member joins the diaphragm they are in: AxisVM cannot add
        /// lines to a diaphragm, so it is made again over all of them (BuildDiaphragms) - the kept members are not rebuilt.
        /// </summary>
        public readonly Dictionary<string, List<string>> KeptDiaphragms = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Load panels that stay, UID -> Rhino surface (written back to the link file).</summary>
        public readonly Dictionary<int, string> KeptPanels = new Dictionary<int, string>();
        public readonly List<string> Notes = new List<string>();

        /// <summary>How the set grew, stage by stage ("changed 1 > shared 1 > touching 9 > groups 9 > edges 12") - for the report.</summary>
        public string Trace = "";

        public LinkedItems Counts()
        {
            return new LinkedItems { Nodes = NodeUids.Count, Lines = LineUids.Count, Domains = DomainUids.Count, LoadPanels = PanelUids.Count, Parts = PartIds.Count };
        }

        /// <summary>The Rhino model reduced to the objects to build (library and settings unchanged).</summary>
        public RhinoModel Subset(RhinoModel full)
        {
            if (Everything) return full;
            RhinoModel m = new RhinoModel { Schema = full.Schema, SourceFile = full.SourceFile, UnitSystem = full.UnitSystem, Tolerance = full.Tolerance, Library = full.Library };
            m.ExportWarnings.AddRange(full.ExportWarnings);
            foreach (RhinoPoint p in full.Points) if (Build.Contains(p.Guid)) m.Points.Add(p);
            foreach (RhinoLine l in full.Lines) if (Build.Contains(l.Guid)) m.Lines.Add(l);
            foreach (RhinoDomain d in full.Domains) if (Build.Contains(d.Guid)) m.Domains.Add(d);
            return m;
        }

        public string Summary()
        {
            if (Everything) return "everything rebuilt (" + Reason + ")";
            return string.Format("{0} unchanged, {1} changed, {2} new, {3} deleted in Rhino, {4} neighbour(s) rebuilt with them{5}", Unchanged, Changed, Added, Deleted, Neighbours,
                Unbuildable > 0 ? string.Format(", {0} that AxisVM refused last time not tried again", Unbuildable) : "");
        }
    }

    /// <summary>
    /// "Only changes": a load from Rhino that touches only what changed. Every AxisVM entity knows the Rhino object it
    /// stands for (RhinoGuid), and the link file remembers each object's signature from the last load or send
    /// (LinkSignature). A Rhino object whose signature is the same and whose AxisVM entities still exist is left alone -
    /// with everything AxisVM added to it (edge hinges, supports, meshes, loads drawn in AxisVM).
    ///
    /// Rebuilt are: the changed, new and deleted objects; curves sharing an AxisVM line with one of those; every object
    /// whose geometry touches their old or new geometry (one ring - a kept line is never split or left with a node on it
    /// that it is not connected to); and whole groups when one member is rebuilt (a diaphragm name, a part, a load panel
    /// with its target curves, an edge hinge with its domains), because those are made in one call over all members.
    /// A domain or load panel that loses an edge line is rebuilt too, as the surface only: its other edge lines stay and
    /// are reused. Before deleting, the AxisVM-only edge hinges and global nodal supports on what goes are recorded and
    /// put back afterwards where the same geometry is rebuilt (<see cref="CarryOver"/>).
    /// </summary>
    public partial class AxisModelBuilder
    {
        // ==================================================================
        // The model as the link sees it
        // ==================================================================

        private class LiveLink
        {
            public double[][] Xyz = new double[0][];
            public int[] NodeUid = new int[0];
            public int[][] Ends = new int[0][];
            public int[] LineUid = new int[0];
            public bool[] LineOurs = new bool[0];
            public List<string>[] LineOwners = new List<string>[0];
            /// <summary>Line -> its structural member (0 = none); member -> its lines.</summary>
            public int[] LineMember = new int[0];
            public readonly Dictionary<int, List<int>> MemberLines = new Dictionary<int, List<int>>();
            /// <summary>Line -> the guids of the domains / load panels it is an edge of ("" for one not from the link).</summary>
            public List<string>[] LineSurfaces = new List<string>[0];
            public int[] DomainUid = new int[0];
            public int[] PanelUid = new int[0];
            public readonly Dictionary<string, List<int>> LinesOf = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> DomainOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> PanelOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> NodeOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, List<int>> SurfaceEdges = new Dictionary<int, List<int>>();   // domain index -> edges
            public readonly Dictionary<int, List<int>> PanelEdges = new Dictionary<int, List<int>>();     // panel index -> edges

            public bool Exists(string g) { return LinesOf.ContainsKey(g) || DomainOf.ContainsKey(g) || PanelOf.ContainsKey(g) || NodeOf.ContainsKey(g); }

            public IEnumerable<string> Guids()
            {
                foreach (string g in LinesOf.Keys) yield return g;
                foreach (string g in DomainOf.Keys) yield return g;
                foreach (string g in PanelOf.Keys) yield return g;
                foreach (string g in NodeOf.Keys) yield return g;
            }
        }

        /// <summary>Imported from this link, or adopted into it and Rhino now holds what it became.</summary>
        private static bool OursFor(AxisVMAttachments a, int i, string key, HashSet<string> rhinoKnows, out List<string> guids)
        {
            guids = LinkState.Guids(a, i);
            if (string.Equals(LinkState.Read(a, LinkState.Link, i), key, StringComparison.Ordinal)) return true;
            List<string> owners;
            if (!string.Equals(LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, i), out owners), key, StringComparison.Ordinal)) return false;
            foreach (string g in owners) if (rhinoKnows != null && rhinoKnows.Contains(g)) return true;
            return false;
        }

        private static LiveLink ReadLink(IAxisVMModel model, LinkDiffInput inp)
        {
            LiveLink live = new LiveLink();
            int nn = TryCount(() => model.Nodes.Count);
            live.NodeUid = NodeUids(model);
            live.Xyz = new double[nn][];
            try
            {
                Array coords;
                if (nn > 0 && model.Nodes.BulkGetCoord(Range(nn), out coords) > 0 && coords != null)
                {
                    int lo = coords.GetLowerBound(0);
                    for (int i = 0; i < nn && i < coords.Length; i++) { RPoint3d p = (RPoint3d)coords.GetValue(lo + i); live.Xyz[i] = new[] { p.x, p.y, p.z }; }
                }
            }
            catch { }
            for (int i = 0; i < nn; i++) if (live.Xyz[i] == null) live.Xyz[i] = LinkState.NodeXyz(model, i + 1) ?? new double[3];

            AxisVMAttachments na = TryGet(() => model.Nodes.Attachments);
            for (int i = 1; i <= nn && na != null; i++)
            {
                List<string> g;
                if (!OursFor(na, i, inp.Key, inp.RhinoKnows, out g)) continue;
                foreach (string x in g) live.NodeOf[x] = i;
            }

            int nl = TryCount(() => model.Lines.Count);
            live.Ends = LineEnds(model);
            live.LineUid = LineUids(model);
            live.LineOurs = new bool[nl];
            live.LineOwners = new List<string>[nl];
            live.LineSurfaces = new List<string>[nl];
            AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
            for (int i = 1; i <= nl; i++)
            {
                live.LineSurfaces[i - 1] = new List<string>();
                List<string> g = null;
                bool ours = la != null && OursFor(la, i, inp.Key, inp.RhinoKnows, out g);
                if (!ours) { live.LineOwners[i - 1] = new List<string>(); continue; }
                // A line-to-line link's link lines ("guid#link") go with their slave curve.
                g = g.ConvertAll(x => { int h = x.IndexOf('#'); return h > 0 ? x.Substring(0, h) : x; });
                live.LineOurs[i - 1] = true;
                live.LineOwners[i - 1] = g;
                foreach (string x in g)
                {
                    List<int> l;
                    if (!live.LinesOf.TryGetValue(x, out l)) { l = new List<int>(); live.LinesOf[x] = l; }
                    l.Add(i);
                }
            }

            live.LineMember = new int[nl];
            try
            {
                Array mids;
                if (nl > 0 && model.Lines.BulkGetMemberIds(Range(nl), out mids) > 0 && mids != null)
                {
                    List<int> m = LinkState.IntList(mids);
                    for (int i = 0; i < nl && i < m.Count; i++)
                    {
                        live.LineMember[i] = m[i];
                        if (m[i] <= 0) continue;
                        List<int> l;
                        if (!live.MemberLines.TryGetValue(m[i], out l)) { l = new List<int>(); live.MemberLines[m[i]] = l; }
                        l.Add(i + 1);
                    }
                }
            }
            catch { }

            int nd = TryCount(() => model.Domains.Count);
            live.DomainUid = DomainUids(model);
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            for (int i = 1; i <= nd; i++)
            {
                List<string> g = new List<string>();
                bool ours = da != null && OursFor(da, i, inp.Key, inp.RhinoKnows, out g);
                string guid = ours && g.Count > 0 ? g[0] : "";
                if (guid.Length > 0) live.DomainOf[guid] = i;
                List<int> edges = LinkState.ContourLines(model, i);
                foreach (List<int> h in LinkState.HoleLines(model, i)) edges.AddRange(h);
                live.SurfaceEdges[i] = edges;
                foreach (int l in edges) if (l >= 1 && l <= nl) live.LineSurfaces[l - 1].Add(guid);
            }

            int np = TryCount(() => model.LoadPanels.Count);
            live.PanelUid = new int[np];
            LinkState.BeginCache(model);
            try
            {
                for (int i = 1; i <= np; i++)
                {
                    try { live.PanelUid[i - 1] = model.LoadPanels.get_UID(i); } catch { }
                    List<int> edges = new List<int>();
                    try
                    {
                        Array ids = null;
                        model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                        edges = LinkState.IntList(ids);
                    }
                    catch { }
                    live.PanelEdges[i] = edges;

                    string guid;
                    if (inp.PanelGuids == null || !inp.PanelGuids.TryGetValue(live.PanelUid[i - 1], out guid)) guid = null;
                    if (guid == null && inp.AdoptedPanels != null && inp.AdoptedPanels.Count > 0)
                    {
                        List<double[]> outline = edges.Count == 0 ? LinkState.PanelPolygon(model, i) : null;
                        string state = edges.Count > 0 ? LinkState.SegmentsState(model, edges) : outline == null ? null : LinkState.PolygonState(outline);
                        string g;
                        if (state != null && inp.AdoptedPanels.TryGetValue(state, out g) && inp.RhinoKnows != null && inp.RhinoKnows.Contains(g)) guid = g;
                    }
                    if (guid != null && guid.Length > 0) live.PanelOf[guid] = i;
                    foreach (int l in edges) if (l >= 1 && l <= nl) live.LineSurfaces[l - 1].Add(guid ?? "");
                }
            }
            finally { LinkState.EndCache(); }
            return live;
        }

        // ==================================================================
        // Planning
        // ==================================================================

        /// <summary>Decides what "only changes" rebuilds and deletes. Reads the model; changes nothing.</summary>
        public static LinkDiffPlan PlanDiff(IAxisVMModel model, RhinoModel rhino, LinkDiffInput inp)
        {
            LinkDiffPlan plan = new LinkDiffPlan();
            if (inp.Previous == null || inp.Previous.Count == 0)
            {
                plan.Everything = true;
                plan.Reason = "the link file holds no record of an earlier load to compare with";
                return plan;
            }
            if (!string.IsNullOrEmpty(inp.PreviousSettings) && inp.PreviousSettings != inp.Settings)
            {
                plan.Everything = true;
                plan.Reason = "the units or the merge tolerance differ from the last load";
                return plan;
            }

            LiveLink live = ReadLink(model, inp);
            double tol = Math.Max(inp.ToleranceM, 1e-6);

            // ---- Rhino objects, their geometry (metres) and group memberships ----------------------------------
            var entity = new Dictionary<string, RhinoEntity>(StringComparer.OrdinalIgnoreCase);
            var geom = new Dictionary<string, List<List<double[]>>>(StringComparer.OrdinalIgnoreCase);
            Func<RhinoXyz, double[]> m = p => new[] { inp.Units.Length(p.X), inp.Units.Length(p.Y), inp.Units.Length(p.Z) };
            foreach (RhinoPoint p in rhino.Points) { entity[p.Guid] = p; geom[p.Guid] = new List<List<double[]>> { new List<double[]> { m(p.Xyz) } }; }
            foreach (RhinoLine l in rhino.Lines)
            {
                entity[l.Guid] = l;
                List<double[]> path = new List<double[]>();
                foreach (RhinoXyz v in l.Vertices) path.Add(m(v));
                if (l.IsArc && path.Count == 2) path.Insert(1, m(l.ArcMid));
                geom[l.Guid] = new List<List<double[]>> { path };
            }
            foreach (RhinoDomain d in rhino.Domains)
            {
                entity[d.Guid] = d;
                var loops = new List<List<double[]>>();
                foreach (List<RhinoXyz> loop in Enumerable(d.Contour, d.Holes))
                {
                    List<double[]> pl = new List<double[]>();
                    foreach (RhinoXyz v in loop) pl.Add(m(v));
                    if (pl.Count > 2) pl.Add(pl[0]);
                    loops.Add(pl);
                }
                geom[d.Guid] = loops;
            }

            // ---- 1. changed, new, deleted -----------------------------------------------------------------------
            HashSet<string> dirty = plan.Build;
            foreach (string g in entity.Keys)
            {
                string now, before;
                inp.Signatures.TryGetValue(g, out now);
                inp.Previous.TryGetValue(g, out before);
                if (!live.Exists(g))
                {
                    // Not built last time either, and the same now: AxisVM would refuse it again (see the earlier report).
                    if (inp.PreviousBuilt != null && !inp.PreviousBuilt.Contains(g) && now != null && now == before) { plan.Unbuildable++; continue; }
                    dirty.Add(g);
                    plan.Added++;
                }
                else if (now == null || before == null || now != before) { dirty.Add(g); plan.Changed++; }
                else plan.Unchanged++;
            }
            HashSet<string> gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string g in live.Guids()) if (!entity.ContainsKey(g) && gone.Add(g)) plan.Deleted++;
            // Every identified panel stays known to the link file, also when nothing is rebuilt (removed below for those that go).
            foreach (KeyValuePair<string, int> kv in live.PanelOf) plan.KeptPanels[live.PanelUid[kv.Value - 1]] = kv.Key;
            if (dirty.Count == 0 && gone.Count == 0) return plan;   // nothing to do

            // Old geometry of what changed or went: as last built (signature) and as AxisVM holds it now.
            var oldGeom = new List<List<double[]>>();
            Action<string> addOld = g =>
            {
                string before;
                if (inp.Previous.TryGetValue(g, out before)) oldGeom.AddRange(LinkSignature.Geometry(before));
                List<int> lines;
                if (live.LinesOf.TryGetValue(g, out lines))
                    foreach (int l in lines) { int[] e = live.Ends[l - 1]; if (e != null) oldGeom.Add(new List<double[]> { NodeAt(live, e[0]), NodeAt(live, e[1]) }); }
                int idx;
                if (live.DomainOf.TryGetValue(g, out idx)) foreach (int l in live.SurfaceEdges[idx]) AddLineGeom(live, l, oldGeom);
                if (live.PanelOf.TryGetValue(g, out idx)) foreach (int l in live.PanelEdges[idx]) AddLineGeom(live, l, oldGeom);
                if (live.NodeOf.TryGetValue(g, out idx)) oldGeom.Add(new List<double[]> { NodeAt(live, idx) });
            };

            Dictionary<string, List<string>> groups = Groups(entity, inp.Previous);
            // Members (beam, rib, truss, spring, link) hold their content on the line itself; an edge hinge or a plain
            // (support) line along one does not - sharing a line couples members only.
            Func<string, bool> isMember = g =>
            {
                RhinoEntity e;
                string type = null, before;
                if (entity.TryGetValue(g, out e)) { if (!(e is RhinoLine)) return false; type = e.Attr(AxKeys.Type); }
                else if (inp.Previous.TryGetValue(g, out before)) SignatureAttrs(before).TryGetValue("ax_type", out type);
                switch (ParseLineRole(type))
                {
                    case LineRole.Beam: case LineRole.Rib: case LineRole.Truss: case LineRole.Spring: case LineRole.RigidLink: case LineRole.NodeLink: return true;
                    default: return false;
                }
            };
            HashSet<string> all = new HashSet<string>(dirty, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(gone);
            plan.Trace = "changed " + all.Count;
            Close(all, live, groups, isMember);
            plan.Trace += " > shared lines + groups " + all.Count;

            // ---- 2. one ring of geometric neighbours -------------------------------------------------------------
            // Only around what moved, appeared or went: an object whose geometry is the same (a new section, a load) is
            // rebuilt onto exactly the nodes it had, so nothing kept gets split or left unconnected by it.
            var dirtyGeom = new List<List<double[]>>();
            foreach (string g in all)
            {
                string sigNow, sigBefore;
                inp.Signatures.TryGetValue(g, out sigNow);
                inp.Previous.TryGetValue(g, out sigBefore);
                if (sigNow != null && sigBefore != null && live.Exists(g) && GeometryPart(sigNow) == GeometryPart(sigBefore)) continue;
                addOld(g);
                List<List<double[]>> now;
                if (geom.TryGetValue(g, out now)) dirtyGeom.AddRange(now);
            }
            dirtyGeom.AddRange(oldGeom);

            var kept = new List<string>();
            foreach (string g in entity.Keys) if (!all.Contains(g) && live.Exists(g)) kept.Add(g);
            HashSet<string> ring = Touching(kept, geom, entity, dirtyGeom, tol);
            all.UnionWith(ring);
            plan.Trace += " > touching " + all.Count + Kinds(ring, entity);
            Close(all, live, groups, isMember);
            plan.Trace += " > shared lines + groups " + all.Count;

            // ---- 3. what to delete (until the set stops growing) ---------------------------------------------------
            HashSet<int> delLines = new HashSet<int>();
            int lost = 0;
            while (true)
            {
                delLines.Clear();
                plan.CleanLineUids.Clear();
                lost = 0;
                for (int l = 1; l <= live.LineOurs.Length; l++)
                {
                    if (!live.LineOurs[l - 1]) continue;
                    List<string> owners = live.LineOwners[l - 1], surfaces = live.LineSurfaces[l - 1];
                    List<string> members = owners.FindAll(o => isMember(o)), others = owners.FindAll(o => !isMember(o));
                    bool del;
                    if (members.Count > 0) del = members.Exists(all.Contains);
                    else if (others.Count > 0) del = others.TrueForAll(all.Contains);
                    else del = surfaces.Count > 0 && surfaces.TrueForAll(s => s.Length > 0 && all.Contains(s));
                    if (del) delLines.Add(l);
                    else if (others.Exists(all.Contains)) plan.CleanLineUids.Add(live.LineUid[l - 1]);
                }
                int size = all.Count;

                // A domain or load panel that loses an edge is rebuilt (as the surface only - its other edges stay).
                foreach (int l in delLines)
                    foreach (string s in live.LineSurfaces[l - 1])
                    {
                        if (s.Length == 0) lost++;
                        else all.Add(s);
                    }

                // Nodes the deletion leaves unused: a kept point there must be rebuilt with them.
                HashSet<int> usedAfter = new HashSet<int>();
                for (int l = 1; l <= live.Ends.Length; l++)
                    if (!delLines.Contains(l) && live.Ends[l - 1] != null) foreach (int n in live.Ends[l - 1]) usedAfter.Add(n);
                HashSet<int> freed = new HashSet<int>();
                foreach (int l in delLines) if (live.Ends[l - 1] != null) foreach (int n in live.Ends[l - 1]) if (!usedAfter.Contains(n)) freed.Add(n);
                foreach (KeyValuePair<string, int> kv in live.NodeOf) if (freed.Contains(kv.Value) && entity.ContainsKey(kv.Key)) all.Add(kv.Key);

                Close(all, live, groups, isMember);
                if (all.Count == size) break;
            }
            plan.Trace += " > surfaces losing an edge, freed points " + all.Count + Kinds(all, entity);
            if (lost > 0)
                plan.Notes.Add(string.Format("{0} edge(s) of domains or load panels drawn in AxisVM run along rebuilt Rhino curves; those surfaces lose that edge - check them.", lost));

            foreach (int l in delLines)
            {
                plan.LineUids.Add(live.LineUid[l - 1]);
                // Kept non-member curves along it: their id goes onto whatever is rebuilt along the same segment.
                int[] e = live.Ends[l - 1];
                if (e == null) continue;
                foreach (string o in live.LineOwners[l - 1])
                    if (!all.Contains(o)) plan.KeptOwners.Add(new KeyValuePair<double[][], string>(new[] { NodeAt(live, e[0]), NodeAt(live, e[1]) }, o));
            }
            foreach (string g in all)
            {
                string before;
                List<int> lines;
                if (!inp.Previous.TryGetValue(g, out before) || !live.LinesOf.TryGetValue(g, out lines)) continue;
                Dictionary<string, string> was = SignatureAttrs(before);
                string type;
                was.TryGetValue("ax_type", out type);
                string t = (type ?? "").Replace(" ", "").ToLowerInvariant();
                if (was.ContainsKey("ax_support"))
                    foreach (int l in lines) plan.RhinoSupportLineUids.Add(live.LineUid[l - 1]);
                if (t == "edgehinge" || t == "hinge")
                {
                    // Only the hinges onto its own domains are Rhino's: a line between two domains also carries the other's.
                    string named;
                    was.TryGetValue("ax_hingedomains", out named);
                    HashSet<string> domains = null;
                    foreach (string n in AttributeParser.SplitList(named))
                    {
                        if (domains == null) domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        domains.Add(n);
                        foreach (RhinoEntity e in entity.Values)
                        {
                            RhinoDomain d = e as RhinoDomain;
                            if (d != null && string.Equals(d.BaseGuid, n, StringComparison.OrdinalIgnoreCase)) domains.Add(d.Guid);
                        }
                    }
                    foreach (int l in lines)
                    {
                        int uid = live.LineUid[l - 1];
                        HashSet<string> had;
                        if (!plan.RhinoHingeLines.TryGetValue(uid, out had))
                            plan.RhinoHingeLines[uid] = domains == null ? null : new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
                        else if (had == null || domains == null) plan.RhinoHingeLines[uid] = null;   // null = every domain
                        else had.UnionWith(domains);
                    }
                }
            }
            foreach (string g in all)
            {
                int idx;
                if (live.DomainOf.TryGetValue(g, out idx)) plan.DomainUids.Add(live.DomainUid[idx - 1]);
                if (live.PanelOf.TryGetValue(g, out idx)) plan.PanelUids.Add(live.PanelUid[idx - 1]);
                if (live.NodeOf.TryGetValue(g, out idx))
                {
                    plan.NodeUids.Add(live.NodeUid[idx - 1]);
                    RhinoEntity e;
                    string before;
                    bool hadSupport = inp.Previous.TryGetValue(g, out before) && before.IndexOf("\nax_support=", StringComparison.Ordinal) >= 0;
                    if (hadSupport || (entity.TryGetValue(g, out e) && e.Attr(AxKeys.Support) != null)) plan.SupportNodeUids.Add(live.NodeUid[idx - 1]);
                }
            }
            foreach (KeyValuePair<string, int> kv in live.PanelOf) if (all.Contains(kv.Key)) plan.KeptPanels.Remove(live.PanelUid[kv.Value - 1]);

            // Parts are rebuilt per name (the group rule put every member in the build).
            HashSet<string> partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> kv in groups)
                if (kv.Key.StartsWith("part:", StringComparison.Ordinal) && kv.Value.Exists(all.Contains)) partNames.Add(kv.Key.Substring(5));
            if (partNames.Count > 0)
            {
                try
                {
                    AxisVMAttachments pa = model.CustomParts.Attachments;
                    IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                    for (int i = 1; i <= root.PartCount; i++)
                    {
                        int id = root.get_PartId(i);
                        if (!string.Equals(LinkState.Read(pa, LinkState.Link, id), inp.Key, StringComparison.Ordinal)) continue;
                        if (partNames.Contains(model.CustomParts.get_Name(id) ?? "")) plan.PartIds.Add(id);
                    }
                }
                catch { }
            }

            foreach (RhinoEntity e in entity.Values)
            {
                string dia = e.Attr(AxKeys.Diaphragm);
                if (dia == null || all.Contains(e.Guid)) continue;
                List<string> l;
                if (!plan.KeptDiaphragms.TryGetValue(dia, out l)) { l = new List<string>(); plan.KeptDiaphragms[dia] = l; }
                l.Add(e.Guid);
            }

            foreach (string g in all) if (!dirty.Contains(g) && !gone.Contains(g)) plan.PulledIn.Add(g);
            plan.Neighbours = Math.Max(0, all.Count - gone.Count - dirty.Count);
            foreach (string g in all) if (entity.ContainsKey(g)) plan.Build.Add(g);
            return plan;
        }

        /// <summary>" (3 curves, 2 surfaces, 1 point)".</summary>
        private static string Kinds(IEnumerable<string> guids, Dictionary<string, RhinoEntity> entity)
        {
            int c = 0, s = 0, p = 0;
            foreach (string g in guids)
            {
                RhinoEntity e;
                if (!entity.TryGetValue(g, out e)) continue;
                if (e is RhinoLine) c++; else if (e is RhinoDomain) s++; else p++;
            }
            return string.Format(" ({0} curves, {1} surfaces, {2} points)", c, s, p);
        }

        private static IEnumerable<List<RhinoXyz>> Enumerable(List<RhinoXyz> outer, List<List<RhinoXyz>> holes)
        {
            yield return outer;
            foreach (List<RhinoXyz> h in holes) yield return h;
        }

        private static double[] NodeAt(LiveLink live, int node)
        {
            return node >= 1 && node <= live.Xyz.Length ? live.Xyz[node - 1] : new double[3];
        }

        private static void AddLineGeom(LiveLink live, int line, List<List<double[]>> into)
        {
            if (line < 1 || line > live.Ends.Length || live.Ends[line - 1] == null) return;
            into.Add(new List<double[]> { NodeAt(live, live.Ends[line - 1][0]), NodeAt(live, live.Ends[line - 1][1]) });
        }

        /// <summary>
        /// Groups whose members are built in one call: "part:name", "panel:guid" (a load panel and its Ax_LoadTargets),
        /// "hinge:guid" (an edge hinge curve and its Ax_HingeDomains). The attributes of the last build count too, so
        /// leaving a group also rebuilds it. Rigid diaphragms are no group: a rebuilt member joins the kept ones in place
        /// (see LinkDiffPlan.KeptDiaphragms).
        /// </summary>
        private static Dictionary<string, List<string>> Groups(Dictionary<string, RhinoEntity> entity, Dictionary<string, string> previous)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> add = (group, guid) =>
            {
                List<string> l;
                if (!groups.TryGetValue(group, out l)) { l = new List<string>(); groups[group] = l; }
                if (!l.Contains(guid)) l.Add(guid);
            };
            var baseGuids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (RhinoEntity e in entity.Values)
            {
                RhinoDomain d = e as RhinoDomain;
                if (d == null) continue;
                List<string> l;
                if (!baseGuids.TryGetValue(d.BaseGuid, out l)) { l = new List<string>(); baseGuids[d.BaseGuid] = l; }
                l.Add(d.Guid);
            }

            Action<string, Func<string, string>> collect = (guid, attr) =>
            {
                string master = attr(AxKeys.LineLink);
                if (!string.IsNullOrEmpty(master)) { add("llink:" + guid, guid); add("llink:" + guid, master.Trim()); }
                string parts = attr(AxKeys.Parts);
                if (!string.IsNullOrEmpty(parts)) foreach (string p in AttributeParser.SplitList(parts)) add("part:" + p, guid);
                string targets = attr(AxKeys.LoadTargets);
                if (!string.IsNullOrEmpty(targets))
                {
                    add("panel:" + guid, guid);
                    foreach (string t in AttributeParser.SplitList(targets)) add("panel:" + guid, t);
                }
                string hinges = attr(AxKeys.HingeDomains);
                if (!string.IsNullOrEmpty(hinges))
                {
                    add("hinge:" + guid, guid);
                    foreach (string t in AttributeParser.SplitList(hinges))
                    {
                        List<string> faces;
                        if (baseGuids.TryGetValue(t, out faces)) foreach (string f in faces) add("hinge:" + guid, f);
                        else add("hinge:" + guid, t);
                    }
                }
            };

            foreach (RhinoEntity e in entity.Values) collect(e.Guid, e.Attr);
            if (previous != null)
                foreach (KeyValuePair<string, string> kv in previous)
                {
                    Dictionary<string, string> attrs = SignatureAttrs(kv.Value);
                    collect(kv.Key, k => { string v; return attrs.TryGetValue(k, out v) ? v : null; });
                }
            return groups;
        }

        /// <summary>The geometry lines of a signature (everything before the first attribute).</summary>
        private static string GeometryPart(string sig)
        {
            string[] lines = sig.Split('\n');
            int n = 2;
            while (n < lines.Length && lines[n].StartsWith("H ", StringComparison.Ordinal)) n++;
            return string.Join("\n", lines, 0, Math.Min(n, lines.Length));
        }

        /// <summary>The attributes recorded in a signature (keys lower-case, see LinkSignature).</summary>
        private static Dictionary<string, string> SignatureAttrs(string sig)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sig)) return map;
            string[] lines = sig.Split('\n');
            for (int i = 2; i < lines.Length; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq > 0 && !lines[i].StartsWith("H ", StringComparison.Ordinal)) map[lines[i].Substring(0, eq)] = lines[i].Substring(eq + 1);
            }
            return map;
        }

        /// <summary>
        /// Closes the set: curves sharing an AxisVM line with a curve in it join (transitively - a curve's lines are
        /// deleted, so whatever else they carry must be rebuilt too), and so do curves whose lines are in the same AxisVM
        /// member (lines of several curves assembled into one member in AxisVM: deleting one curve's lines breaks the
        /// member and its loads), and whole groups with a member in it.
        /// </summary>
        private static void Close(HashSet<string> set, LiveLink live, Dictionary<string, List<string>> groups, Func<string, bool> isMember)
        {
            bool grew = true;
            while (grew)
            {
                grew = false;
                Queue<string> q = new Queue<string>(set);
                while (q.Count > 0)
                {
                    string g = q.Dequeue();
                    List<int> lines;
                    if (!isMember(g) || !live.LinesOf.TryGetValue(g, out lines)) continue;
                    foreach (int l in lines)
                    {
                        List<int> together = null;
                        int m = l <= live.LineMember.Length ? live.LineMember[l - 1] : 0;
                        if (m > 0) live.MemberLines.TryGetValue(m, out together);
                        foreach (int x in together ?? new List<int> { l })
                            foreach (string h in live.LineOwners[x - 1])
                                if (isMember(h) && set.Add(h)) { q.Enqueue(h); grew = true; }
                    }
                }
                foreach (List<string> members in groups.Values)
                {
                    if (!members.Exists(set.Contains)) continue;
                    foreach (string g in members) if (set.Add(g)) grew = true;
                }
            }
        }

        // ==================================================================
        // Geometry: what touches what
        // ==================================================================

        /// <summary>
        /// The kept objects whose geometry comes within the tolerance of any dirty polyline (shared node, point on a
        /// segment, crossing), or - for surfaces - that have a dirty vertex inside them.
        /// </summary>
        private static HashSet<string> Touching(List<string> kept, Dictionary<string, List<List<double[]>>> geom, Dictionary<string, RhinoEntity> entity,
                                                List<List<double[]>> dirty, double tol)
        {
            const double cell = 2.0;
            double reach = 2 * tol;
            var grid = new Dictionary<long, List<int>>();
            var segs = new List<double[][]>();
            var owner = new List<string>();
            foreach (string g in kept)
            {
                List<List<double[]>> polys;
                if (!geom.TryGetValue(g, out polys)) continue;
                foreach (List<double[]> pl in polys)
                {
                    if (pl.Count == 1) AddSeg(grid, segs, owner, pl[0], pl[0], g, cell, reach);
                    for (int i = 0; i + 1 < pl.Count; i++) AddSeg(grid, segs, owner, pl[i], pl[i + 1], g, cell, reach);
                }
            }

            HashSet<string> hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirtyPoints = new List<double[]>();
            foreach (List<double[]> pl in dirty)
            {
                for (int i = 0; i < pl.Count; i++)
                {
                    double[] a = pl[i], b = i + 1 < pl.Count ? pl[i + 1] : pl[i];
                    dirtyPoints.Add(a);
                    if (i + 1 < pl.Count) dirtyPoints.Add(new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 });
                    if (i + 1 >= pl.Count && pl.Count > 1) break;
                    foreach (int s in Cells(grid, a, b, cell, reach))
                    {
                        if (hits.Contains(owner[s])) continue;
                        if (SegSegDistance(a, b, segs[s][0], segs[s][1]) <= reach) hits.Add(owner[s]);
                    }
                }
            }

            // A dirty vertex inside a kept surface (a rib end landing in a slab) - the surface's mesh and edges change.
            foreach (string g in kept)
            {
                RhinoDomain d = entity[g] as RhinoDomain;
                if (d == null || hits.Contains(g)) continue;
                List<double[]> loop = geom[g][0];
                foreach (double[] p in dirtyPoints)
                    if (InsidePolygon(loop, p, tol)) { hits.Add(g); break; }
            }
            return hits;
        }

        private static void AddSeg(Dictionary<long, List<int>> grid, List<double[][]> segs, List<string> owner, double[] a, double[] b, string g, double cell, double reach)
        {
            int id = segs.Count;
            segs.Add(new[] { a, b });
            owner.Add(g);
            ForCells(a, b, cell, reach, k =>
            {
                List<int> l;
                if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
                l.Add(id);
            });
        }

        private static IEnumerable<int> Cells(Dictionary<long, List<int>> grid, double[] a, double[] b, double cell, double reach)
        {
            HashSet<int> seen = new HashSet<int>();
            List<int> result = new List<int>();
            ForCells(a, b, cell, reach, k =>
            {
                List<int> l;
                if (grid.TryGetValue(k, out l)) foreach (int s in l) if (seen.Add(s)) result.Add(s);
            });
            return result;
        }

        /// <summary>Every grid cell the segment's box (grown by <paramref name="reach"/>) overlaps.</summary>
        private static void ForCells(double[] a, double[] b, double cell, double reach, Action<long> visit)
        {
            long x0 = (long)Math.Floor((Math.Min(a[0], b[0]) - reach) / cell), x1 = (long)Math.Floor((Math.Max(a[0], b[0]) + reach) / cell);
            long y0 = (long)Math.Floor((Math.Min(a[1], b[1]) - reach) / cell), y1 = (long)Math.Floor((Math.Max(a[1], b[1]) + reach) / cell);
            long z0 = (long)Math.Floor((Math.Min(a[2], b[2]) - reach) / cell), z1 = (long)Math.Floor((Math.Max(a[2], b[2]) + reach) / cell);
            for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                    for (long z = z0; z <= z1; z++)
                        visit(((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF));
        }

        /// <summary>Shortest distance between segments p1-q1 and p2-q2 (either may be a point).</summary>
        internal static double SegSegDistance(double[] p1, double[] q1, double[] p2, double[] q2)
        {
            double[] d1 = Sub(q1, p1), d2 = Sub(q2, p2), r = Sub(p1, p2);
            double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
            double s, t;
            const double eps = 1e-14;
            if (a <= eps && e <= eps) return Math.Sqrt(Dot(r, r));
            if (a <= eps) { s = 0; t = Clamp01(f / e); }
            else
            {
                double c = Dot(d1, r);
                if (e <= eps) { t = 0; s = Clamp01(-c / a); }
                else
                {
                    double b = Dot(d1, d2), denom = a * e - b * b;
                    s = denom > eps ? Clamp01((b * f - c * e) / denom) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Clamp01(-c / a); }
                    else if (t > 1) { t = 1; s = Clamp01((b - c) / a); }
                }
            }
            double[] c1 = { p1[0] + d1[0] * s, p1[1] + d1[1] * s, p1[2] + d1[2] * s };
            double[] c2 = { p2[0] + d2[0] * t, p2[1] + d2[1] * t, p2[2] + d2[2] * t };
            return Dist(c1, c2);
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        /// <summary>True when p lies in the plane of the closed loop (within the tolerance) and inside it.</summary>
        internal static bool InsidePolygon(List<double[]> loop, double[] p, double tol)
        {
            if (loop.Count < 4) return false;
            // Newell normal
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i + 1 < loop.Count; i++)
            {
                double[] a = loop[i], b = loop[i + 1];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) return false;
            nx /= len; ny /= len; nz /= len;
            double dist = (p[0] - loop[0][0]) * nx + (p[1] - loop[0][1]) * ny + (p[2] - loop[0][2]) * nz;
            if (Math.Abs(dist) > 2 * tol) return false;

            // Project onto the plane's dominant axes and count crossings.
            int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            int u = drop == 0 ? 1 : 0, v = drop == 2 ? 1 : 2;
            bool inside = false;
            for (int i = 0, j = loop.Count - 2; i + 1 < loop.Count; j = i++)
            {
                double[] a = loop[i], b = loop[j];
                if ((a[v] > p[v]) != (b[v] > p[v]) && p[u] < (b[u] - a[u]) * (p[v] - a[v]) / (b[v] - a[v]) + a[u]) inside = !inside;
            }
            return inside;
        }

        // ==================================================================
        // Carrying AxisVM-only data across a rebuild
        // ==================================================================

        private class CarriedHinge
        {
            public double[] A, B;
            public string DomainGuid;
            public RSpringParamIndexes Springs;
        }

        private class CarriedSupport
        {
            public double[] At;
            public RNodalSupportSpringParams Springs;
        }

        /// <summary>A line support / elastic foundation by its line's end points (and, for an edge support, its domain).</summary>
        private class CarriedLineSupport
        {
            public double[] A, B;
            public string DomainGuid;
            public bool Foundation;
            public RStiffnesses Stiff;
            public RNonLinearity NonLin;
            public RResistances Resist;
            public RStiffnessesXYZ StiffXyz;
            public RNonLinearityXYZ NonLinXyz;
            public RResistancesXYZ ResistXyz;
        }

        /// <summary>The live straight lines lying on segment a-b: the same line, or the pieces it was split into by the rebuild.</summary>
        private static List<int> PiecesOn(LiveModel live, double[] a, double[] b, double tol)
        {
            List<int> r = new List<int>();
            double len = Dist(a, b);
            if (len <= tol) return r;
            double ux = (b[0] - a[0]) / len, uy = (b[1] - a[1]) / len, uz = (b[2] - a[2]) / len;
            foreach (LiveSeg s in live.Segs)
            {
                if (s.IsArc) continue;
                double ta, tb;
                if (!OnLine(a, ux, uy, uz, s.A, tol, out ta) || !OnLine(a, ux, uy, uz, s.B, tol, out tb)) continue;
                double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
                if (hi - lo <= tol || lo < -4 * tol || hi > len + 4 * tol) continue;
                r.Add(s.Id);
            }
            return r;
        }

        private readonly List<CarriedLineSupport> carriedLineSupports = new List<CarriedLineSupport>();

        /// <summary>A pulled-in domain as it was: its outline loops (segments, outer first) and its attributes.</summary>
        private class OldDomain
        {
            public List<List<double[][]>> Loops = new List<List<double[][]>>();
            public RSurfaceAttr Attr;
        }

        private readonly Dictionary<string, OldDomain> oldDomains = new Dictionary<string, OldDomain>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records the outline of the pulled-in domains about to be deleted (see <see cref="TryOldOutline"/>).</summary>
        private void RememberOldDomains(HashSet<int> domainUids)
        {
            if (opt.Diff == null || opt.Diff.PulledIn.Count == 0) return;
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            int[] uid = DomainUids();
            for (int i = 1; i <= uid.Length; i++)
            {
                if (!domainUids.Contains(uid[i - 1])) continue;
                List<string> g = LinkState.Guids(da, i);
                if (g.Count == 0 || !opt.Diff.PulledIn.Contains(g[0])) continue;
                OldDomain od = new OldDomain();
                if (!LinkState.TrySurfaceAttr(model, i, out od.Attr)) continue;
                var loops = new List<List<int>> { LinkState.ContourLines(model, i) };
                loops.AddRange(LinkState.HoleLines(model, i));
                foreach (List<int> loop in loops)
                {
                    var segs = new List<double[][]>();
                    foreach (int l in loop)
                    {
                        int[] e = LinkState.LineNodes(model, l);
                        double[] a = e == null ? null : NodeXyz(e[0]), b = e == null ? null : NodeXyz(e[1]);
                        if (a != null && b != null) segs.Add(new[] { a, b });
                    }
                    od.Loops.Add(segs);
                }
                oldDomains[g[0]] = od;
            }
        }

        /// <summary>
        /// A domain Rhino did not change, pulled into the rebuild as a neighbour, that AxisVM refused to make from Rhino's
        /// outline: made again from the lines along its previous outline (which AxisVM had accepted).
        /// </summary>
        private bool TryOldOutline(SurfaceJob s, out int domainId)
        {
            domainId = 0;
            OldDomain od;
            if (!Partial) return false;
            if (!oldDomains.TryGetValue(s.Src.Guid, out od) || od.Loops.Count == 0)
            {
                notes.Add(s.Context + ": no previous outline was recorded (" + (opt.Diff.PulledIn.Contains(s.Src.Guid) ? "pulled in" : "changed in Rhino") + ").");
                return false;
            }
            double tol = Math.Max(opt.MergeToleranceM, 1e-6);
            LiveModel live = ReadLive(tol);
            List<List<int>> loops = new List<List<int>>();
            List<LiveSeg> straight = live.Segs.FindAll(x => !x.IsArc);
            foreach (List<double[][]> segs in od.Loops)
            {
                // End to end along each old edge piece: old mesh pieces and new longer lines may both lie there now.
                List<int> lines = new List<int>();
                int gaps = 0;
                foreach (double[][] seg in segs)
                {
                    List<SegRef> chain = Along(seg[0], seg[1], straight, tol, 0);
                    if (chain.Count == 0) gaps++;
                    foreach (SegRef r in chain) if (!lines.Contains(r.Id)) lines.Add(r.Id);
                }
                List<int> ordered = OrderLoop(live, lines);
                if (ordered == null)
                {
                    // Which way it is not a loop (for the report), then let AxisVM try the lines as they are.
                    var degree = new Dictionary<int, int>();
                    foreach (LiveSeg x in straight)
                        if (lines.Contains(x.Id)) foreach (int n in new[] { x.NodeA, x.NodeB }) { int d; degree.TryGetValue(n, out d); degree[n] = d + 1; }
                    int ends = 0, branches = 0;
                    foreach (int d in degree.Values) { if (d == 1) ends++; else if (d > 2) branches++; }
                    notes.Add(string.Format("{0}: its previous outline ({1} segment(s), {2} with no line now) is {3} line(s) with {4} open end(s) and {5} branch node(s).",
                        s.Context, segs.Count, gaps, lines.Count, ends, branches));
                    ordered = lines;
                }
                loops.Add(ordered);
            }
            try
            {
                RSurfaceAttr attr = od.Attr;
                domainId = model.Domains.Add(Ids(loops[0].ToArray()), ref attr);
                if (domainId <= 0) { notes.Add(string.Format("{0}: AxisVM refused its previous outline too (code {1}, {2} line(s)).", s.Context, domainId, loops[0].Count)); return false; }
                for (int h = 1; h < loops.Count; h++) { try { model.Domains.Item[domainId].AddHole(Ids(loops[h].ToArray())); } catch { } }
                notes.Add(s.Context + ": AxisVM refused it from Rhino's outline, so it was made again from its previous outline.");
                return true;
            }
            catch { domainId = 0; return false; }
        }

        /// <summary>The lines of a closed loop in walking order, or null when they are no single loop.</summary>
        private static List<int> OrderLoop(LiveModel live, List<int> lines)
        {
            var byId = new Dictionary<int, LiveSeg>();
            foreach (LiveSeg s in live.Segs) if (lines.Contains(s.Id)) byId[s.Id] = s;
            if (byId.Count != lines.Count || lines.Count < 3) return null;
            var at = new Dictionary<int, List<int>>();
            foreach (LiveSeg s in byId.Values)
                foreach (int n in new[] { s.NodeA, s.NodeB }) { List<int> l; if (!at.TryGetValue(n, out l)) { l = new List<int>(); at[n] = l; } l.Add(s.Id); }
            foreach (List<int> l in at.Values) if (l.Count != 2) return null;
            List<int> order = new List<int> { lines[0] };
            int node = byId[lines[0]].NodeB;
            while (order.Count < lines.Count)
            {
                int next = at[node][0] == order[order.Count - 1] ? at[node][1] : at[node][0];
                if (order.Contains(next)) return null;
                order.Add(next);
                LiveSeg s = byId[next];
                node = s.NodeA == node ? s.NodeB : s.NodeA;
            }
            return order;
        }
        private readonly List<CarriedHinge> carriedHinges = new List<CarriedHinge>();
        private readonly List<CarriedSupport> carriedSupports = new List<CarriedSupport>();
        private int notCarried;

        /// <summary>
        /// Before the planned delete: the edge hinges on lines or domains about to go, and the global nodal supports on
        /// nodes about to go - things made in AxisVM that Rhino does not know. Rhino's own (rebuilt anyway) are skipped
        /// later by not adding where one exists.
        /// </summary>
        private void RememberCarried(HashSet<int> lineUids, HashSet<int> domainUids, HashSet<int> nodeUids, HashSet<int> rhinoLines,
            Dictionary<int, HashSet<string>> rhinoHinges, HashSet<int> rhinoNodes)
        {
            int[] lineUid = LineUids(), nodeUid = NodeUids(), domainUid = DomainUids();
            int[][] ends = LineEnds(model);
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);

            int nh = TryCount(() => model.EdgeConnections.Count);
            for (int i = 1; i <= nh; i++)
            {
                try
                {
                    REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172();
                    if (model.EdgeConnections.GetRec_V172(i, ref rec) <= 0) continue;
                    bool lineGoes = rec.LineId >= 1 && rec.LineId <= lineUid.Length && lineUids.Contains(lineUid[rec.LineId - 1]);
                    bool domainGoes = rec.DomainId >= 1 && rec.DomainId <= domainUid.Length && domainUids.Contains(domainUid[rec.DomainId - 1]);
                    if (!lineGoes && !domainGoes) continue;
                    List<string> g = LinkState.Guids(da, rec.DomainId);
                    HashSet<string> rhinos;
                    if (rec.LineId >= 1 && rec.LineId <= lineUid.Length && rhinoHinges.TryGetValue(lineUid[rec.LineId - 1], out rhinos)
                        && (rhinos == null || g.Exists(rhinos.Contains))) continue;   // Rhino's: rebuilt
                    int[] e = rec.LineId >= 1 && rec.LineId <= ends.Length ? ends[rec.LineId - 1] : null;
                    double[] a = e == null ? null : NodeXyz(e[0]), b = e == null ? null : NodeXyz(e[1]);
                    if (g.Count == 0 || a == null || b == null) { notCarried++; continue; }
                    carriedHinges.Add(new CarriedHinge { A = a, B = b, DomainGuid = g[0], Springs = rec.SpringParamIndexes });
                }
                catch { }
            }

            // Line supports: an edge support goes with its domain or its line, a foundation with its line.
            int nls = TryCount(() => model.LineSupports.Count);
            for (int i = 1; i <= nls; i++)
            {
                try
                {
                    IAxisVMLineSupport s = model.LineSupports.Item[i];
                    int line = model.LineSupports.get_LineId(i);
                    ELineSupportType t = s.SupportType;
                    bool foundation = t == ELineSupportType.lstBeamElasticFoundation || t == ELineSupportType.lstRibElasticFoundation;
                    int dom = foundation ? 0 : s.DomainId1;
                    bool lineGoes = line >= 1 && line <= lineUid.Length && lineUids.Contains(lineUid[line - 1]);
                    bool domainGoes = dom >= 1 && dom <= domainUid.Length && domainUids.Contains(domainUid[dom - 1]);
                    if (!lineGoes && !domainGoes) continue;
                    if (line >= 1 && line <= lineUid.Length && rhinoLines.Contains(lineUid[line - 1])) continue;   // Rhino's: rebuilt
                    if (!foundation && t != ELineSupportType.lstEdgeGlobal) { notCarried++; continue; }
                    int[] e = line >= 1 && line <= ends.Length ? ends[line - 1] : null;
                    double[] a = e == null ? null : NodeXyz(e[0]), b = e == null ? null : NodeXyz(e[1]);
                    List<string> g = foundation ? new List<string>() : LinkState.Guids(da, dom);
                    if (a == null || b == null || (!foundation && g.Count == 0)) { notCarried++; continue; }
                    CarriedLineSupport c = new CarriedLineSupport { A = a, B = b, Foundation = foundation, DomainGuid = foundation ? null : g[0] };
                    if (foundation)
                    {
                        RStiffnessesXYZ k = new RStiffnessesXYZ(); RNonLinearityXYZ nl = new RNonLinearityXYZ(); RResistancesXYZ rs = new RResistancesXYZ();
                        s.GetStiffnessesXYZ(ref k); s.GetNonLinearityXYZ(ref nl); s.GetResistancesXYZ(ref rs);
                        c.StiffXyz = k; c.NonLinXyz = nl; c.ResistXyz = rs;
                    }
                    else
                    {
                        RStiffnesses k = new RStiffnesses(); RNonLinearity nl = new RNonLinearity(); RResistances rs = new RResistances();
                        s.GetStiffnesses(ref k); s.GetNonLinearity(ref nl); s.GetResistances(ref rs);
                        c.Stiff = k; c.NonLin = nl; c.Resist = rs;
                    }
                    carriedLineSupports.Add(c);
                }
                catch { }
            }

            // Nodes that will be unused once the lines go (RemoveFound deletes those).
            HashSet<int> used = new HashSet<int>();
            for (int l = 0; l < ends.Length; l++)
                if (ends[l] != null && !lineUids.Contains(lineUid[l])) foreach (int n in ends[l]) used.Add(n);
            HashSet<int> going = new HashSet<int>();
            for (int l = 0; l < ends.Length; l++)
                if (ends[l] != null && lineUids.Contains(lineUid[l])) foreach (int n in ends[l]) if (!used.Contains(n)) going.Add(n);
            for (int n = 1; n <= nodeUid.Length; n++) if (nodeUids.Contains(nodeUid[n - 1]) && !used.Contains(n)) going.Add(n);

            int ns = TryCount(() => model.NodalSupports.Count);
            for (int i = 1; i <= ns; i++)
            {
                try
                {
                    IAxisVMNodalSupport s = model.NodalSupports.Item[i];
                    if (!going.Contains(s.NodeId)) continue;
                    if (s.NodeId >= 1 && s.NodeId <= nodeUid.Length && rhinoNodes.Contains(nodeUid[s.NodeId - 1])) continue;   // Rhino's: rebuilt
                    double[] at = NodeXyz(s.NodeId);
                    if (s.SupportType != ENodalSupportType.nstNodalGlobal || at == null) { notCarried++; continue; }
                    carriedSupports.Add(new CarriedSupport { At = at, Springs = s.SpringParams });
                }
                catch { }
            }
        }

        /// <summary>After the build: puts the remembered hinges and supports back where the same geometry now is.</summary>
        private void CarryOver()
        {
            if (carriedHinges.Count == 0 && carriedSupports.Count == 0 && carriedLineSupports.Count == 0 && notCarried == 0 && opt.Diff.KeptOwners.Count == 0) return;
            double tol = Math.Max(opt.MergeToleranceM, 1e-6);
            LiveModel live = ReadLive(tol);

            // Kept edge hinge / plain line curves along rebuilt lines: their id goes onto the new pieces.
            AxisVMAttachments lineAtt = TryGet(() => model.Lines.Attachments);
            foreach (KeyValuePair<double[][], string> kv in opt.Diff.KeptOwners)
                foreach (int line in PiecesOn(live, kv.Key[0], kv.Key[1], tol))
                {
                    List<string> owners = LinkState.Guids(lineAtt, line);
                    if (owners.Contains(kv.Value)) continue;
                    owners.Add(kv.Value);
                    LinkState.Write(lineAtt, LinkState.Guid, line, string.Join(";", owners));
                }

            Dictionary<string, int> domainOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            int nd = TryCount(() => model.Domains.Count);
            for (int i = 1; i <= nd; i++) foreach (string g in LinkState.Guids(da, i)) domainOf[g] = i;

            HashSet<string> hingeAt = new HashSet<string>(StringComparer.Ordinal);
            int nh = TryCount(() => model.EdgeConnections.Count);
            for (int i = 1; i <= nh; i++)
            {
                try
                {
                    REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172();
                    if (model.EdgeConnections.GetRec_V172(i, ref rec) > 0) hingeAt.Add(rec.LineId + "|" + rec.DomainId);
                }
                catch { }
            }

            int hinges = 0, supports = 0, missed = notCarried;
            // Each on every piece of its old line (a rebuild may have split it at a new node).
            foreach (CarriedHinge h in carriedHinges)
            {
                int domain;
                List<int> pieces = PiecesOn(live, h.A, h.B, tol);
                if (!domainOf.TryGetValue(h.DomainGuid, out domain) || pieces.Count == 0) { missed++; continue; }
                foreach (int line in pieces)
                {
                    if (hingeAt.Contains(line + "|" + domain)) continue;
                    try
                    {
                        REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172 { LineId = line, DomainId = domain, SpringParamIndexes = h.Springs };
                        if (model.EdgeConnections.Add_V172(ref rec) > 0) { hinges++; hingeAt.Add(line + "|" + domain); } else missed++;
                    }
                    catch { missed++; }
                }
            }

            // Line supports: one per line at most (a rebuilt curve's own support came first).
            HashSet<int> lineSupported = new HashSet<int>();
            int nls = TryCount(() => model.LineSupports.Count);
            for (int i = 1; i <= nls; i++) { try { lineSupported.Add(model.LineSupports.get_LineId(i)); } catch { } }
            int lineSupports = 0;
            foreach (CarriedLineSupport c in carriedLineSupports)
            {
                int domain = 0;
                List<int> pieces = PiecesOn(live, c.A, c.B, tol);
                if (pieces.Count == 0 || (!c.Foundation && !domainOf.TryGetValue(c.DomainGuid, out domain))) { missed++; continue; }
                foreach (int line in pieces)
                {
                    if (lineSupported.Contains(line)) continue;
                    try
                    {
                        int id;
                        if (c.Foundation)
                        {
                            RStiffnessesXYZ k = c.StiffXyz;
                            RNonLinearityXYZ nl = c.NonLinXyz;
                            RResistancesXYZ rs = c.ResistXyz;
                            RLineAttr la;
                            bool rib = LinkState.TryLineAttr(model, line, out la) && la.LineType == ELineType.ltRib;
                            id = rib ? model.LineSupports.AddRibElasticFoundation(line, ref k, ref nl, ref rs) : model.LineSupports.AddBeamElasticFoundation(line, ref k, ref nl, ref rs);
                        }
                        else
                        {
                            RStiffnesses k = c.Stiff;
                            RNonLinearity nl = c.NonLin;
                            RResistances rs = c.Resist;
                            id = model.LineSupports.AddEdgeGlobal(ref k, ref nl, ref rs, line, 0, 0, domain, 0);
                        }
                        if (id > 0) { lineSupports++; lineSupported.Add(line); } else missed++;
                    }
                    catch { missed++; }
                }
            }

            HashSet<int> supported = new HashSet<int>();
            int ns = TryCount(() => model.NodalSupports.Count);
            for (int i = 1; i <= ns; i++) { try { supported.Add(model.NodalSupports.Item[i].NodeId); } catch { } }
            foreach (CarriedSupport s in carriedSupports)
            {
                int n;
                if (!live.Index.TryFind(s.At[0], s.At[1], s.At[2], out n)) { missed++; continue; }
                if (supported.Contains(n)) continue;
                try
                {
                    RNodalSupportSpringParams p = s.Springs;
                    if (model.NodalSupports.AddNodalGlobal_V153(ref p, n) > 0) { supports++; supported.Add(n); } else missed++;
                }
                catch { missed++; }
            }

            if (hinges + supports + lineSupports > 0)
                notes.Add(string.Format("Update: {0} edge hinge(s), {1} line support(s) and {2} nodal support(s) on rebuilt elements were put back as they were.", hinges, lineSupports, supports));
            if (missed > 0)
                notes.Add(string.Format("Update: {0} edge hinge(s) or support(s) made in AxisVM sat on rebuilt elements whose geometry changed (or of a kind not carried over) and are gone - check them.", missed));
        }

        /// <summary>
        /// After "only changes": entities "Send to Rhino" adopted that Rhino now holds and that were kept become ordinary
        /// linked entities (RhinoLink mark, no RhinoAdopted), like those a load built.
        /// </summary>
        private void AdoptKept()
        {
            string key = LinkKey(opt.SourcePath);
            HashSet<string> knows = opt.RhinoKnownGuids;
            if (knows == null || key.Length == 0) return;
            Action<AxisVMAttachments, int> one = (a, n) =>
            {
                for (int i = 1; i <= n; i++)
                {
                    List<string> owners;
                    if (!string.Equals(LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, i), out owners), key, StringComparison.Ordinal)) continue;
                    if (!owners.Exists(knows.Contains)) continue;
                    LinkState.Write(a, LinkState.Link, i, key);
                    try { a.Delete(LinkState.Adopted, i); } catch { }
                }
            };
            try { one(model.Lines.Attachments, model.Lines.Count); } catch { }
            try { one(model.Domains.Attachments, model.Domains.Count); } catch { }
            try { one(model.Nodes.Attachments, model.Nodes.Count); } catch { }
        }
    }
}
