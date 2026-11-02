using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.UI;

/*
 * =====================================================================================
 * TIMBER SLS CHECK - AxisVM side
 * =====================================================================================
 *
 * 1. MEMBERS   Floor and Roof hold AxisVM line ids (beams / ribs). Selected lines that meet end to end in a
 *              straight line form one chain; each chain is split into spans at
 *              nodal supports and, optionally, at columns or connecting beams (doc §5.1). A span end that is not
 *              supported and has no other line attached is free: that span is a cantilever.
 * 2. LOADS     Load cases and AxisVM load groups (type, ψ0/ψ1/ψ2, exclusive), overridable per case; or existing
 *              AxisVM combinations (factors per case). The add-on never writes combinations (doc §6).
 * 3. RESULTS   IAxisVMDisplacements.GetLineDisplacementsByLoadCaseId per line and load case, global system,
 *              projected on the checked direction, relative to the span chord (or the cantilever root), in mm.
 * 4. CHECK     TimberSlsMath per span: every limit row for every combination, then vibration for Floor spans.
 * 5. VISUAL    A temporary drawing layer colours every span segment by its local utilisation.
 * =====================================================================================
 */

namespace AxisVM_plugin.Functions
{
    /// <summary>One straight span of a chain of lines.</summary>
    public class SpanInfo
    {
        public int Index;
        public string Group;                   // "Floor" / "Roof"
        public string Label;
        public int ChainId;
        public int SpansInChain;
        public List<int> Lines = new List<int>();
        public double Length;                  // m
        public bool Cantilever;                // set by the check: the reference that governed
        public bool RootAtEnd;                 // cantilever with its root at End: result x runs from End
        public Vec Start, End;                 // geometric ends; x of geometry runs from Start
        public double E;                       // N/m²
        public double Iy, Iz;                  // m⁴
        public double KdefModel;               // 0 = not a timber line
        public int ServiceClass;               // 0 = unknown
        public string Material = "", Section = "";
        public ETimberType TimberType = ETimberType.ttOther;
        public bool IsTimber;

        public Vec PointAt(double x) { return Length > 0 ? Start + (x / Length) * (End - Start) : Start; }

        /// <summary>Geometric x (from Start) of a result position (from the root for a cantilever rooted at End).</summary>
        public double GeoX(double x) { return RootAtEnd ? Length - x : x; }
    }

    /// <summary>Small 3D vector for geometry.</summary>
    public struct Vec
    {
        public double X, Y, Z;
        public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec operator +(Vec a, Vec b) { return new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec operator -(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec operator *(double s, Vec a) { return new Vec(s * a.X, s * a.Y, s * a.Z); }
        public double Dot(Vec b) { return X * b.X + Y * b.Y + Z * b.Z; }
        public Vec Cross(Vec b) { return new Vec(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X); }
        public double Length { get { return Math.Sqrt(Dot(this)); } }
        public Vec Unit { get { double l = Length; return l > 0 ? (1 / l) * this : this; } }
    }

    /// <summary>Everything about one checked span.</summary>
    public class SpanResult
    {
        public SpanInfo Span;
        public SpanDeflections Defl;
        public GroupSettings Settings;
        public double Kdef;
        public string KdefNote;
        public double LRep;
        public List<RowResult> Rows = new List<RowResult>();
        public RowResult Governing;
        public double DeflUc;
        public TimberSlsMath.Ec5VibResult Vib04;
        public TimberSlsMath.En2025Result Vib25;
        public double VibUc = double.NaN;
        public string VibNote = "";
        public string Error;
        public string VibField = "";           // the floor field used for vibration
        public SpanType Type;
        public string Reference = "";          // which reference governed
        public List<string> Candidates = new List<string>();
        public double Uc { get { return Error != null ? double.NaN : Math.Max(DeflUc, double.IsNaN(VibUc) ? 0 : VibUc); } }

        public string Status
        {
            get
            {
                if (Error != null) return "Error";
                if (Vib04 != null && !Vib04.F1Above8 && DeflUc <= 1 && Vib04.Uc <= 1) return "f1 ≤ 8 Hz";
                return Uc <= 1.0 ? "OK" : "Fails";
            }
        }
    }

    public class SlsRun
    {
        public List<SpanResult> Spans = new List<SpanResult>();
        public List<SlsCombination> Combinations = new List<SlsCombination>();
        public Dictionary<int, SlsLoadCase> Cases = new Dictionary<int, SlsLoadCase>();
        public List<string> Notes = new List<string>();
        public TimberSlsSettings Settings;
    }

    /// <summary>A load case as read from AxisVM, before overrides.</summary>
    public class ModelLoadCase
    {
        public int Id;
        public string Name;
        public string Type;
        public SlsLoadCase Default;
    }

    public class ModelCombination
    {
        public int Id;
        public string Name;
        public ECombinationType Type;
        public string TypeName
        {
            get
            {
                switch (Type)
                {
                    case ECombinationType.ctSLSChar: return "SLS characteristic";
                    case ECombinationType.ctSLSFreq: return "SLS frequent";
                    case ECombinationType.ctSLSQuasi: return "SLS quasi-permanent";
                    case ECombinationType.ctOther: return "other";
                    default: return Type.ToString().Substring(2);
                }
            }
        }
        public bool IsSls { get { return TypeName.StartsWith("SLS"); } }
    }

    public static class TimberSlsFunction
    {
        public const string Title = "Timber SLS Check";
        public const string LayerName = "Timber SLS check (temporary)";
        private static TimberSlsForm openForm;

        private class HandleOwner : IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public HandleOwner(int handle) { Handle = new IntPtr(handle); }
        }

        /// <summary>Opens the window modeless, so members can be selected in AxisVM while it is open.</summary>
        public static int Execute(IAxisVMApplication app, int ownerHandle = 0)
        {
            try
            {
                IAxisVMModel model = app.Models.Item[1];
                if (model == null) return -1;
                if (openForm != null && !openForm.IsDisposed)
                {
                    openForm.Activate();
                    return 1;
                }
                TimberSlsBackend backend = new TimberSlsBackend(app, model);
                openForm = new TimberSlsForm(backend);
                if (ownerHandle != 0) openForm.Show(new HandleOwner(ownerHandle)); else openForm.Show();
                return 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }
        }
    }

    /// <summary>All COM access of the tool; the form only talks to this class.</summary>
    public class TimberSlsBackend
    {
        private readonly IAxisVMApplication app;
        private readonly IAxisVMModel model;
        private readonly Dictionary<int, Vec> nodes = new Dictionary<int, Vec>();
        private readonly Dictionary<int, int[]> lineNodes = new Dictionary<int, int[]>();

        public TimberSlsBackend(IAxisVMApplication app, IAxisVMModel model) { this.app = app; this.model = model; }

        public string ModelPath { get { try { return model.FileName; } catch { return ""; } } }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------

        /// <summary>Selected beam / rib lines, including every line of every selected member.</summary>
        public List<int> SelectedLines(out int skipped)
        {
            skipped = 0;
            List<int> ids = new List<int>();
            HashSet<int> seen = new HashSet<int>();
            try
            {
                Array arr;
                if (model.Lines.SelCount > 0 && model.Lines.GetSelectedItemIds(out arr) > 0 && arr != null)
                    foreach (object o in arr) { int id = Convert.ToInt32(o); if (seen.Add(id)) ids.Add(id); }
            }
            catch { }
            try
            {
                Array arr;
                if (model.Members.SelCount > 0 && model.Members.GetSelectedItemIds(out arr) > 0 && arr != null)
                    foreach (object o in arr)
                    {
                        Array lines;
                        model.Members.Item[Convert.ToInt32(o)].GetLines(out lines);
                        if (lines != null) foreach (object l in lines) { int id = Convert.ToInt32(l); if (seen.Add(id)) ids.Add(id); }
                    }
            }
            catch { }
            List<int> beams = new List<int>();
            foreach (int id in ids)
            {
                ELineType t;
                try { t = model.Lines.Item[id].LineType; } catch { skipped++; continue; }
                if (t == ELineType.ltBeam || t == ELineType.ltRib) beams.Add(id); else skipped++;
            }
            return beams;
        }

        /// <summary>A short description of a line for the member lists.</summary>
        public string Describe(int lineId)
        {
            try
            {
                IAxisVMLine l = model.Lines.Item[lineId];
                SpanInfo tmp = new SpanInfo();
                ReadSection(lineId, tmp);
                return "Line " + lineId + "   L = " + TimberSlsMath.Num(l.Length, 3) + " m   " + tmp.Material + "  " + tmp.Section;
            }
            catch { return "Line " + lineId + " (not found)"; }
        }

        /// <summary>Length, material, section, kdef and service class of a line for the member table.</summary>
        public string[] LineCells(int lineId)
        {
            try
            {
                IAxisVMLine l = model.Lines.Item[lineId];
                SpanInfo tmp = new SpanInfo();
                ReadSection(lineId, tmp);
                return new[] { TimberSlsMath.Num(l.Length, 3), tmp.Material, tmp.Section, tmp.KdefModel > 0 ? TimberSlsMath.Num(tmp.KdefModel, 2) : "-",
                    tmp.ServiceClass > 0 ? tmp.ServiceClass.ToString() : "-" };
            }
            catch { return new[] { "-", "(not found)", "", "", "" }; }
        }

        public void Select(IEnumerable<int> lineIds)
        {
            try
            {
                model.Nodes.SelectAll(ELongBoolean.lbFalse);
                model.Lines.SelectAll(ELongBoolean.lbFalse);
                model.Members.SelectAll(ELongBoolean.lbFalse);
                foreach (int l in lineIds.Distinct()) model.Lines.set_Selected(l, ELongBoolean.lbTrue);
                model.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the selection: " + ex.Message, TimberSlsFunction.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        // Loads
        // ------------------------------------------------------------------

        /// <summary>Load cases with defaults from their AxisVM load groups (doc §6). Vibration and special cases are left out.</summary>
        public List<ModelLoadCase> ReadLoadCases()
        {
            List<ModelLoadCase> list = new List<ModelLoadCase>();
            IAxisVMLoadCases cases = model.LoadCases;
            for (int i = 1; i <= cases.Count; i++)
            {
                ELoadCaseType type;
                string name;
                try { type = cases.get_LoadCaseType(i); name = cases.get_Name(i); } catch { continue; }
                if (type == ELoadCaseType.lctVibration || type == ELoadCaseType.lctInfluenceLine || type == ELoadCaseType.lctDynamic ||
                    type == ELoadCaseType.lctPushOver || type == ELoadCaseType.lctCFD) continue;

                SlsLoadCase d = new SlsLoadCase { Id = i, Name = name, Role = LoadRole.Variable, Group = name, Psi0 = 1, Psi1 = 1, Psi2 = 1 };
                int gid = 0;
                try { gid = cases.get_GroupID(i); } catch { }
                if (gid > 0)
                {
                    try
                    {
                        IAxisVMLoadGroup g = model.LoadGroups.Item[gid];
                        d.Group = g.Name;
                        d.Exclusive = g.CombinationType == EGroupCombinationType.gctExclusive;
                        d.Psi0 = g.Psi0; d.Psi1 = g.Psi1; d.Psi2 = g.Psi2;
                        switch (g.LoadGroupType)
                        {
                            case ELoadGroupType.lgtPermanent: d.Role = LoadRole.Permanent; d.Psi0 = d.Psi1 = d.Psi2 = 1; break;
                            case ELoadGroupType.lgtIncidental: d.Role = LoadRole.Variable; d.Imposed = true; break;
                            case ELoadGroupType.lgtSnow: case ELoadGroupType.lgtWind: d.Role = LoadRole.Variable; break;
                            default: d.Role = LoadRole.Ignore; break;
                        }
                    }
                    catch { }
                }
                else if (type == ELoadCaseType.lctSelfWeight)
                {
                    d.Role = LoadRole.Permanent; d.Group = "Self-weight";
                }
                else d.Role = LoadRole.Ignore;   // no group: the user decides
                if (type == ELoadCaseType.lctSeismic || type == ELoadCaseType.lctManualSeismic || type == ELoadCaseType.lctFire ||
                    type == ELoadCaseType.lctImperfection || type == ELoadCaseType.lctLocalImperfection) d.Role = LoadRole.Ignore;
                list.Add(new ModelLoadCase { Id = i, Name = name, Type = type.ToString().Substring(3), Default = d });
            }
            return list;
        }

        public List<ModelCombination> ReadCombinations()
        {
            List<ModelCombination> list = new List<ModelCombination>();
            try
            {
                IAxisVMLoadCombinations combos = model.LoadCombinations;
                for (int i = 1; i <= combos.Count; i++)
                {
                    ModelCombination c = new ModelCombination { Id = i };
                    try { c.Name = combos.get_Name(i); } catch { c.Name = "Combination " + i; }
                    try { c.Type = combos.get_CombinationType(i); } catch { c.Type = ECombinationType.ctOther; }
                    list.Add(c);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Factors of an AxisVM combination per load case.</summary>
        public bool ReadCombinationFactors(int id, out List<int> caseIds, out List<double> factors)
        {
            caseIds = new List<int>();
            factors = new List<double>();
            try
            {
                Array f, c;
                if (model.LoadCombinations.GetCombination(id, out f, out c) <= 0 || f == null || c == null) return false;
                int fo = f.GetLowerBound(0), co = c.GetLowerBound(0);
                for (int i = 0; i < Math.Min(f.Length, c.Length); i++)
                {
                    caseIds.Add(Convert.ToInt32(c.GetValue(co + i)));
                    factors.Add(Convert.ToDouble(f.GetValue(fo + i)));
                }
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Geometry: chains and spans (doc §5.1)
        // ------------------------------------------------------------------

        private Vec Node(int id)
        {
            Vec v;
            if (nodes.TryGetValue(id, out v)) return v;
            RPoint3d p = new RPoint3d();
            try { model.Nodes.GetNodeCoord(id, ref p); } catch { }
            v = new Vec(p.x, p.y, p.z);
            nodes[id] = v;
            return v;
        }

        private int[] Ends(int lineId)
        {
            int[] e;
            if (lineNodes.TryGetValue(lineId, out e)) return e;
            IAxisVMLine l = model.Lines.Item[lineId];
            e = new[] { l.StartNode, l.EndNode };
            lineNodes[lineId] = e;
            return e;
        }

        private List<int> NodeLines(int nodeId)
        {
            List<int> r = new List<int>();
            try
            {
                Array a;
                if (model.Nodes.GetNodeLines(nodeId, out a) > 0 && a != null) foreach (object o in a) r.Add(Convert.ToInt32(o));
            }
            catch { }
            return r;
        }

        private HashSet<int> SupportedNodes()
        {
            HashSet<int> s = new HashSet<int>();
            try
            {
                IAxisVMNodesSupports ns = model.NodesSupports;
                for (int i = 1; i <= ns.Count; i++) { try { s.Add(ns.get_NodeId(i)); } catch { } }
            }
            catch { }
            try
            {
                IAxisVMNodalSupports ns = model.NodalSupports;
                for (int i = 1; i <= ns.Count; i++) { try { s.Add(ns.Item[i].NodeId); } catch { } }
            }
            catch { }
            return s;
        }

        /// <summary>Material, section, E, I and timber data of a line.</summary>
        private void ReadSection(int lineId, SpanInfo s)
        {
            IAxisVMLine l = model.Lines.Item[lineId];
            int mat = 0, sec = 0, sec2 = 0, sc = 0;
            double kdef = 0;
            RPoint3d e1 = new RPoint3d(), e2 = new RPoint3d();
            bool ok = false;
            try { ok = l.GetTimberBeamData(out mat, out sc, out kdef, out sec, out sec2, ref e1, ref e2) > 0; } catch { }
            if (!ok) try { ok = l.GetTimberRibData(out mat, out sc, out kdef, out sec, out sec2, ref e1, ref e2) > 0; } catch { }
            if (ok) s.IsTimber = true;
            else
            {
                kdef = 0; sc = 0;
                try { ok = l.GetBeamData(out mat, out sec, out sec2, ref e1, ref e2) > 0; } catch { }
                if (!ok) try { ok = l.GetRibData(out mat, out sec, out sec2, ref e1, ref e2) > 0; } catch { }
            }
            if (kdef <= 0) try { kdef = l.Timber_kdef; } catch { }
            if (sc <= 0) try { sc = l.Timber_ServiceClass; } catch { }
            s.KdefModel = kdef;
            s.ServiceClass = sc;
            if (mat > 0)
            {
                try
                {
                    IAxisVMMaterial m = model.Materials.Item[mat];
                    s.Material = m.Name;
                    double ex = m.ex;
                    // AxisVM COM works in kN and m (E in kN/m²); a value that looks like N/mm² is converted too
                    s.E = ex > 1e5 ? ex * 1000.0 : ex * 1e6;
                    if (m.MaterialType == EMaterialType.mtTimber) { s.IsTimber = true; s.TimberType = m.TimberType; }
                }
                catch { }
            }
            if (sec > 0)
            {
                try
                {
                    IAxisVMCrossSection c = model.CrossSections.Item[sec];
                    s.Section = c.Name;
                    s.Iy = c.Iy;
                    s.Iz = c.Iz;
                }
                catch { }
            }
        }

        /// <summary>Chains of the given lines, split into spans (doc §5.1).</summary>
        public List<SpanInfo> BuildSpans(IList<int> lineIds, string group, GeneralSettings gs, List<string> notes)
        {
            List<SpanInfo> spans = new List<SpanInfo>();
            HashSet<int> set = new HashSet<int>(lineIds);
            HashSet<int> supported = SupportedNodes();
            Dictionary<int, List<int>> atNode = new Dictionary<int, List<int>>();   // node -> selected lines
            foreach (int l in set)
                foreach (int n in Ends(l))
                {
                    List<int> list;
                    if (!atNode.TryGetValue(n, out list)) atNode[n] = list = new List<int>();
                    list.Add(l);
                }

            // Two selected lines continue through a node when they are the only selected lines there and collinear
            // (AxisVM gives every line its own design member, so members do not decide this)
            Func<int, int, int, bool> continues = (a, b, node) =>
            {
                if (gs.SpanMode == SpanMode.EachLine) return false;
                if (atNode[node].Count != 2) return false;
                Vec da = (Node(Other(a, node)) - Node(node)).Unit, db = (Node(Other(b, node)) - Node(node)).Unit;
                return da.Dot(db) < -0.9995;
            };

            HashSet<int> used = new HashSet<int>();
            int chainId = 0;
            foreach (int start in lineIds)
            {
                if (used.Contains(start)) continue;
                // Walk back to one end of the chain
                int first = start, firstNode = Ends(start)[0];
                HashSet<int> guard = new HashSet<int> { start };
                while (true)
                {
                    int nxt = atNode[firstNode].FirstOrDefault(x => x != first);
                    if (nxt == 0 || guard.Contains(nxt) || !continues(first, nxt, firstNode)) break;
                    guard.Add(nxt);
                    firstNode = Other(nxt, firstNode);
                    first = nxt;
                }
                // Walk forward collecting lines and nodes
                List<int> chainLines = new List<int>();
                List<int> chainNodes = new List<int> { firstNode };
                int cur = first, node = firstNode;
                while (true)
                {
                    chainLines.Add(cur);
                    used.Add(cur);
                    node = Other(cur, node);
                    chainNodes.Add(node);
                    int nxt = atNode[node].FirstOrDefault(x => x != cur);
                    if (nxt == 0 || used.Contains(nxt) || !continues(cur, nxt, node)) break;
                    cur = nxt;
                }
                chainId++;

                // Split nodes
                Vec dir = (Node(chainNodes[chainNodes.Count - 1]) - Node(chainNodes[0])).Unit;
                bool chainVertical = Math.Abs(dir.Z) > 0.985;
                Func<int, bool> isSplit = n =>
                {
                    if (supported.Contains(n)) return true;
                    foreach (int other in NodeLines(n))
                    {
                        if (chainLines.Contains(other)) continue;
                        IAxisVMLine ol;
                        try { ol = model.Lines.Item[other]; } catch { continue; }
                        Vec od = (Node(Other(other, n)) - Node(n)).Unit;
                        bool column = false;
                        try { column = ol.IsColumn == ELongBoolean.lbTrue; } catch { }
                        if (!chainVertical && Math.Abs(od.Z) > 0.985) column = true;
                        ELineType t = ELineType.ltBeam;
                        try { t = ol.LineType; } catch { }
                        if (gs.SplitAtColumns && column && t != ELineType.ltSpring) return true;
                        if (gs.SplitAtBeams && !column && (t == ELineType.ltBeam || t == ELineType.ltRib)) return true;
                    }
                    return false;
                };
                Func<int, bool> isFree = n => !supported.Contains(n) && NodeLines(n).All(x => chainLines.Contains(x));

                List<int> cuts = new List<int> { 0 };
                for (int i = 1; i < chainNodes.Count - 1; i++) if (isSplit(chainNodes[i])) cuts.Add(i);
                cuts.Add(chainNodes.Count - 1);
                int nSpans = cuts.Count - 1;
                bool freeStart = isFree(chainNodes[0]), freeEnd = isFree(chainNodes[chainNodes.Count - 1]);
                if (nSpans == 1 && freeStart && freeEnd)
                {
                    notes.Add(group + ": line(s) " + string.Join(", ", chainLines) + " have no support or connection at either end and were skipped.");
                    continue;
                }

                for (int k = 0; k < nSpans; k++)
                {
                    SpanInfo s = new SpanInfo { Group = group, ChainId = chainId, SpansInChain = nSpans };
                    for (int i = cuts[k]; i < cuts[k + 1]; i++) s.Lines.Add(chainLines[i]);
                    Vec a = Node(chainNodes[cuts[k]]), b = Node(chainNodes[cuts[k + 1]]);
                    s.Start = a;
                    s.End = b;
                    s.Length = (b - a).Length;
                    ReadSection(s.Lines[0], s);
                    s.Label = (s.Lines.Count == 1 ? "L" + s.Lines[0] : "L" + s.Lines[0] + "–L" + s.Lines[s.Lines.Count - 1]) +
                              (nSpans > 1 ? " (span " + (k + 1) + "/" + nSpans + ")" : "");
                    spans.Add(s);
                }
            }
            return spans;
        }

        private int Other(int lineId, int node) { int[] e = Ends(lineId); return e[0] == node ? e[1] : e[0]; }

        // ------------------------------------------------------------------
        // Results (doc §5.1)
        // ------------------------------------------------------------------

        private readonly Dictionary<long, Tuple<double[], RDisplacementValues[]>> cache = new Dictionary<long, Tuple<double[], RDisplacementValues[]>>();

        /// <summary>Global displacement records of a line for one load case; null when there are none.</summary>
        private Tuple<double[], RDisplacementValues[]> LineDisp(int lineId, int caseId)
        {
            long key = ((long)lineId << 20) | (uint)caseId;
            Tuple<double[], RDisplacementValues[]> r;
            if (cache.TryGetValue(key, out r)) return r;
            r = null;
            try
            {
                IAxisVMDisplacements d = model.Results.Displacements;
                d.DisplacementSystem = EDisplacementSystem.dsGlobal;
                Array dArr, xArr;
                int rc = d.GetLineDisplacementsByLoadCaseId(lineId, caseId, 0, EAnalysisType.atLinearStatic, ELongBoolean.lbFalse, out dArr, out xArr);
                if (rc > 0 && dArr != null && xArr != null && dArr.Length > 0)
                {
                    int n = Math.Min(dArr.Length, xArr.Length);
                    double[] x = new double[n];
                    RDisplacementValues[] v = new RDisplacementValues[n];
                    int dl = dArr.GetLowerBound(0), xl = xArr.GetLowerBound(0);
                    for (int i = 0; i < n; i++) { x[i] = Convert.ToDouble(xArr.GetValue(xl + i)); v[i] = (RDisplacementValues)dArr.GetValue(dl + i); }
                    r = Tuple.Create(x, v);
                }
            }
            catch { r = null; }
            cache[key] = r;
            return r;
        }

        public void ClearCache() { cache.Clear(); nodes.Clear(); lineNodes.Clear(); }

        /// <summary>Unit vector of the checked component for a span (doc settings: Direction).</summary>
        private static Vec Direction(SpanInfo s, DeflectionDirection mode)
        {
            Vec t = (s.End - s.Start).Unit;
            Vec z = new Vec(0, 0, 1);
            if (mode == DeflectionDirection.GlobalZ) return z;
            Vec n = z - z.Dot(t) * t;                  // normal in the vertical plane
            if (n.Length < 1e-6) n = new Vec(1, 0, 0) - t.X * t;   // vertical member: any horizontal normal
            n = n.Unit;
            return mode == DeflectionDirection.Lateral ? t.Cross(n).Unit : n;
        }

        /// <summary>Absolute deflection lines of a span (x from Start), with the end slopes for the tangent reference.</summary>
        public class AbsoluteDeflections
        {
            public double[] X = new double[0];
            public Dictionary<int, double[]> W = new Dictionary<int, double[]>();
            public Dictionary<int, double> Slope0 = new Dictionary<int, double>(), SlopeL = new Dictionary<int, double>();

            public void Scale(double f)
            {
                foreach (int k in W.Keys.ToList()) W[k] = W[k].Select(v => v * f).ToArray();
                foreach (int k in Slope0.Keys.ToList()) Slope0[k] *= f;
                foreach (int k in SlopeL.Keys.ToList()) SlopeL[k] *= f;
            }
        }

        /// <summary>
        /// Deflection of a span per load case in mm, positive along −direction (downward for floors), absolute (not
        /// yet relative). Cases without results are listed in <paramref name="missing"/>.
        /// </summary>
        public AbsoluteDeflections ReadAbsolute(SpanInfo s, IEnumerable<int> caseIds, GeneralSettings gs, out List<int> missing)
        {
            missing = new List<int>();
            AbsoluteDeflections ad = new AbsoluteDeflections();
            Vec dir = Direction(s, gs.Direction);
            Vec t = (s.End - s.Start).Unit;
            List<double> xs = null;
            foreach (int c in caseIds)
            {
                List<KeyValuePair<double, RDisplacementValues>> pts = new List<KeyValuePair<double, RDisplacementValues>>();
                bool ok = true;
                foreach (int lineId in s.Lines)
                {
                    Tuple<double[], RDisplacementValues[]> d = LineDisp(lineId, c);
                    if (d == null) { ok = false; break; }
                    int[] e = Ends(lineId);
                    Vec p0 = Node(e[0]), p1 = Node(e[1]);
                    double len = (p1 - p0).Length;
                    for (int i = 0; i < d.Item1.Length; i++)
                    {
                        Vec p = len > 0 ? p0 + (d.Item1[i] / len) * (p1 - p0) : p0;
                        pts.Add(new KeyValuePair<double, RDisplacementValues>((p - s.Start).Dot(t), d.Item2[i]));
                    }
                }
                if (!ok || pts.Count < 2) { missing.Add(c); continue; }
                pts.Sort((a, b) => a.Key.CompareTo(b.Key));
                // Drop duplicates at shared nodes and points outside the span
                List<KeyValuePair<double, RDisplacementValues>> clean = new List<KeyValuePair<double, RDisplacementValues>>();
                foreach (var p in pts)
                {
                    if (p.Key < -1e-6 || p.Key > s.Length + 1e-6) continue;
                    if (clean.Count > 0 && Math.Abs(clean[clean.Count - 1].Key - p.Key) < 1e-6) continue;
                    clean.Add(p);
                }
                if (clean.Count < 2) { missing.Add(c); continue; }
                if (xs == null) xs = FineGrid(clean.Select(p => Math.Max(0, Math.Min(s.Length, p.Key))).ToList(), s.Length);
                ad.W[c] = Resample(clean, xs, dir, t);
                // dw/dx (mm/m) from the end rotations: u = phi x (x t), w = -u.dir
                RDisplacementValues r0 = clean[0].Value, rL = clean[clean.Count - 1].Value;
                ad.Slope0[c] = -new Vec(r0.Fx, r0.Fy, r0.Fz).Cross(t).Dot(dir) * 1000.0;
                ad.SlopeL[c] = -new Vec(rL.Fx, rL.Fy, rL.Fz).Cross(t).Dot(dir) * 1000.0;
            }
            ad.X = xs == null ? new double[0] : xs.ToArray();
            return ad;
        }

        public enum Reference { Span, RootStart, RootEnd }

        /// <summary>
        /// AxisVM analyses timber with E_mean/(1+kdef); its displacements times 1/(1+kdef of the line) are the
        /// instantaneous ones at E_mean (setting AxisStiffness).
        /// </summary>
        public static double AxisFactor(SpanInfo s, GeneralSettings gs)
        {
            if (gs.AxisStiffness == AxisStiffness.Off || !s.IsTimber || s.KdefModel <= 0) return 1.0;
            return 1.0 / (1.0 + s.KdefModel);
        }

        /// <summary>
        /// The relative deflection lines for one reference (doc §5.1): Span = minus the chord between both ends
        /// (middle relative); RootStart / RootEnd = cantilever relative to that end (x then runs from the root).
        /// </summary>
        public static SpanDeflections Relative(SpanInfo s, AbsoluteDeflections ad, Reference reference, CantileverReference cref)
        {
            SpanDeflections sd = new SpanDeflections { Length = s.Length, Cantilever = reference != Reference.Span };
            sd.X = reference == Reference.RootEnd ? ad.X.Select(x => s.Length - x).Reverse().ToArray() : (double[])ad.X.Clone();
            foreach (var kv in ad.W)
            {
                double[] w = kv.Value;
                if (reference == Reference.Span) { sd.W[kv.Key] = TimberSlsMath.RelativeToChord(ad.X, w); continue; }
                double[] wr = reference == Reference.RootEnd ? w.Reverse().ToArray() : w;
                double slope = 0;
                if (cref == CantileverReference.RootTangent)
                    slope = reference == Reference.RootEnd ? -ad.SlopeL[kv.Key] : ad.Slope0[kv.Key];
                sd.W[kv.Key] = TimberSlsMath.RelativeToRoot(sd.X, wr, slope);
            }
            return sd;
        }

        /// <summary>Root of a cantilever from the deflection: the end that moves least (sum over the load cases).</summary>
        public static Reference CantileverRoot(AbsoluteDeflections ad)
        {
            double a = 0, b = 0;
            foreach (double[] w in ad.W.Values) { if (w.Length == 0) continue; a += Math.Abs(w[0]); b += Math.Abs(w[w.Length - 1]); }
            return a <= b ? Reference.RootStart : Reference.RootEnd;
        }

        /// <summary>The result sections plus 40 equal divisions, so a peak between sections is not missed.</summary>
        private static List<double> FineGrid(List<double> sections, double length)
        {
            List<double> all = new List<double>(sections);
            for (int i = 0; i <= 40; i++) all.Add(length * i / 40.0);
            all.Sort();
            List<double> r = new List<double>();
            foreach (double x in all) if (r.Count == 0 || x - r[r.Count - 1] > 1e-6) r.Add(x);
            return r;
        }

        /// <summary>
        /// w = −(u·dir)·1000 at the target positions. Between result sections the deflection is interpolated with
        /// cubic Hermite polynomials from the displacement and the slope (from the section rotation, dw/dx =
        /// −(φ × t)·dir), which recovers the peak of a continuous span to well below 1 %.
        /// </summary>
        private static double[] Resample(List<KeyValuePair<double, RDisplacementValues>> pts, List<double> xs, Vec dir, Vec t)
        {
            double[] px = pts.Select(p => p.Key).ToArray();
            double[] pw = pts.Select(p => -(p.Value.ex * dir.X + p.Value.ey * dir.Y + p.Value.ez * dir.Z) * 1000.0).ToArray();
            double[] ps = pts.Select(p => -new Vec(p.Value.Fx, p.Value.Fy, p.Value.Fz).Cross(t).Dot(dir) * 1000.0).ToArray();
            double[] r = new double[xs.Count];
            for (int i = 0; i < xs.Count; i++)
            {
                double x = xs[i];
                int k = Array.BinarySearch(px, x);
                if (k >= 0) { r[i] = pw[k]; continue; }
                k = ~k;
                if (k <= 0) { r[i] = pw[0]; continue; }
                if (k >= px.Length) { r[i] = pw[px.Length - 1]; continue; }
                double h = px[k] - px[k - 1], u = (x - px[k - 1]) / h;
                double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
                r[i] = h00 * pw[k - 1] + h10 * h * ps[k - 1] + h01 * pw[k] + h11 * h * ps[k];
            }
            return r;
        }

        /// <summary>Largest absolute deflection along a span for one load case (w_sys of 9.15), mm.</summary>
        public double MaxAbsolute(SpanInfo s, int caseId, GeneralSettings gs)
        {
            Vec dir = Direction(s, gs.Direction);
            double best = double.NaN;
            foreach (int l in s.Lines)
            {
                Tuple<double[], RDisplacementValues[]> d = LineDisp(l, caseId);
                if (d == null) continue;
                foreach (RDisplacementValues v in d.Item2)
                {
                    double w = Math.Abs(v.ex * dir.X + v.ey * dir.Y + v.ez * dir.Z) * 1000.0;
                    if (double.IsNaN(best) || w > best) best = w;
                }
            }
            return best;
        }

        /// <summary>Frequency of mode 1 of an AxisVM vibration result case; NaN when missing.</summary>
        public double Frequency(int resultCase)
        {
            try
            {
                if (model.Results.get_ResultCaseCount(EAnalysisType.atLinearVibration) < resultCase) return double.NaN;
                double f = model.Results.get_Frequency(EAnalysisType.atLinearVibration, resultCase, 1);
                return f > 0 ? f : double.NaN;
            }
            catch { return double.NaN; }
        }

        // ------------------------------------------------------------------
        // The check (doc §4 - §8)
        // ------------------------------------------------------------------

        /// <summary>Builds the case list after the user's overrides.</summary>
        public static Dictionary<int, SlsLoadCase> EffectiveCases(List<ModelLoadCase> modelCases, TimberSlsSettings st)
        {
            Dictionary<int, SlsLoadCase> r = new Dictionary<int, SlsLoadCase>();
            foreach (ModelLoadCase mc in modelCases)
            {
                SlsLoadCase c = mc.Default;
                CaseOverride o = st.Cases.FirstOrDefault(x => x.Name == mc.Name);
                if (o != null)
                    c = new SlsLoadCase { Id = mc.Id, Name = mc.Name, Role = o.Role, Group = o.Group, Exclusive = o.Exclusive, Psi0 = o.Psi0, Psi1 = o.Psi1, Psi2 = o.Psi2, Imposed = o.Imposed };
                r[mc.Id] = c;
            }
            return r;
        }

        /// <summary>The combinations to check, by source (doc §6).</summary>
        public List<SlsCombination> Combinations(TimberSlsSettings st, Dictionary<int, SlsLoadCase> cases, List<string> notes)
        {
            List<SlsCombination> combos = new List<SlsCombination>();
            if (st.LoadSource == LoadSource.AxisVMCombinations)
            {
                foreach (ModelCombination mc in ReadCombinations())
                {
                    if (!st.SelectedCombinations.Contains(mc.Name)) continue;
                    List<int> ids; List<double> f;
                    if (!ReadCombinationFactors(mc.Id, out ids, out f)) { notes.Add("Could not read combination " + mc.Name + "."); continue; }
                    combos.Add(TimberSlsMath.FromFactors(mc.Name, ids, f, cases));
                }
                if (combos.Count == 0) notes.Add("No AxisVM combination ticked in the Loads tab.");
            }
            else
            {
                string note;
                combos = TimberSlsMath.Generate(cases.Values.Where(c => c.Role != LoadRole.Ignore).ToList(), st.General.CombinationCap, out note);
                if (note != null) notes.Add(note);
            }
            return combos;
        }

        /// <summary>kdef of a span by the group's source (doc §3).</summary>
        private static double Kdef(SpanInfo s, GroupSettings g, out string note)
        {
            int sc = s.ServiceClass > 0 ? s.ServiceClass : Math.Max(1, Math.Min(3, g.ServiceClass));
            if (g.KdefSource == KdefSource.Custom) { note = "custom"; return g.KdefCustom; }
            if (g.KdefSource == KdefSource.FromAxisVM && s.KdefModel > 0) { note = "AxisVM line"; return s.KdefModel; }
            note = "Table 3.2, SC" + sc + (s.TimberType == ETimberType.ttCLT ? " (CLT: solid timber row, check)" : "") +
                   (g.KdefSource == KdefSource.FromAxisVM ? " (no kdef on the line)" : "");
            return TimberSlsMath.KdefSolid(sc);
        }

        /// <summary>Reads results and checks every span of both groups.</summary>
        public SlsRun Run(TimberSlsSettings st, List<ModelLoadCase> modelCases)
        {
            ClearCache();
            SlsRun run = new SlsRun { Settings = st };
            run.Cases = EffectiveCases(modelCases, st);
            run.Combinations = Combinations(st, run.Cases, run.Notes);
            List<int> caseIds = run.Combinations.SelectMany(c => c.Terms.Select(t => t.CaseId)).Distinct().ToList();

            List<SpanInfo> spans = new List<SpanInfo>();
            spans.AddRange(BuildSpans(st.FloorLines, "Floor", st.General, run.Notes));
            spans.AddRange(BuildSpans(st.RoofLines, "Roof", st.General, run.Notes));
            for (int i = 0; i < spans.Count; i++) spans[i].Index = i;
            if (spans.Count == 0) { run.Notes.Add("No members in Floor or Roof."); return run; }
            if (run.Combinations.Count == 0 || caseIds.Count == 0) { run.Notes.Add("No load combination to check."); return run; }

            HashSet<int> missingAll = new HashSet<int>();
            foreach (SpanInfo s in spans)
            {
                GroupSettings g = st.Group(s.Group);
                SpanResult r = new SpanResult { Span = s, Settings = g, Type = st.TypeOf(s.Lines) };
                List<int> missing;
                AbsoluteDeflections ad = ReadAbsolute(s, caseIds, st.General, out missing);
                double axisFactor = AxisFactor(s, st.General);
                if (axisFactor != 1.0) ad.Scale(axisFactor);
                foreach (int m in missing) missingAll.Add(m);
                if (ad.X.Length < 2 || ad.W.Count == 0) r.Error = "No displacement results (run a linear static analysis).";
                if (!s.IsTimber) run.Notes.Add(s.Group + " " + s.Label + ": material " + s.Material + " is not timber; checked anyway.");
                r.Kdef = Kdef(s, g, out r.KdefNote);
                r.LRep = s.Length;
                if (r.Error == null)
                {
                    List<SlsCombination> combos = run.Combinations.Select(c =>
                        TimberSlsMath.ApplyCreepModel(c, g.CreepModel, g.CreepShareG, g.CreepShareQ, run.Cases)).ToList();
                    // References to try (doc §5.1): Auto = middle, left and right relative; the critical one governs
                    List<Reference> refs = new List<Reference>();
                    if (r.Type == SpanType.Span) refs.Add(Reference.Span);
                    else if (r.Type == SpanType.Cantilever) refs.Add(CantileverRoot(ad));
                    else if (st.General.CantileverRef == CantileverReference.RootTangent)
                        refs.AddRange(new[] { Reference.Span, CantileverRoot(ad) });   // the tip's tangent is no root
                    else
                    {
                        // Both directions; the root found from the deflection first, so a tie (same |w|) keeps the real root
                        Reference root = CantileverRoot(ad);
                        refs.AddRange(new[] { Reference.Span, root, root == Reference.RootStart ? Reference.RootEnd : Reference.RootStart });
                    }
                    double bestUc = -1;
                    foreach (Reference rf in refs)
                    {
                        SpanDeflections sd = Relative(s, ad, rf, st.General.CantileverRef);
                        double lRep = rf == Reference.Span ? s.Length : g.CantileverFactor * s.Length;
                        List<RowResult> rows = new List<RowResult>();
                        RowResult gov = null;
                        foreach (LimitRow row in g.Rows.Where(x => x.Enabled))
                        {
                            RowResult rr = TimberSlsMath.CheckRow(sd, combos, row, r.Kdef, g.PrecamberMm, lRep);
                            if (rr == null) continue;
                            rows.Add(rr);
                            if (gov == null || rr.Uc > gov.Uc) gov = rr;
                        }
                        double uc = gov == null ? 0 : gov.Uc;
                        string name = RefName(rf, s);
                        r.Candidates.Add(name + ": UC " + TimberSlsMath.Num(uc, 3) + (gov == null ? "" : " (" + TimberSlsMath.QuantityName(gov.Row.Quantity) + ")"));
                        if (uc > bestUc + 1e-12)
                        {
                            bestUc = uc;
                            r.Defl = sd; r.Rows = rows; r.Governing = gov; r.LRep = lRep; r.Reference = name;
                            s.Cantilever = rf != Reference.Span;
                            s.RootAtEnd = rf == Reference.RootEnd;
                        }
                    }
                    r.DeflUc = r.Governing == null ? 0 : r.Governing.Uc;
                }
                run.Spans.Add(r);
            }
            if (missingAll.Count > 0)
                run.Notes.Add("No results for load case(s): " + string.Join(", ", missingAll.Select(i => run.Cases.ContainsKey(i) ? run.Cases[i].Name : i.ToString())) +
                    ". Treated as zero where other cases had results; run the linear static analysis.");

            Vibration(run, st);
            return run;
        }

        private static string RefName(Reference rf, SpanInfo s)
        {
            switch (rf)
            {
                case Reference.RootStart: return "cantilever, root at the start of L" + s.Lines[0];
                case Reference.RootEnd: return "cantilever, root at the end of L" + s.Lines[s.Lines.Count - 1];
                default: return "span, relative to both ends";
            }
        }

        /// <summary>Vibration of the Floor spans (doc §7, §8).</summary>
        private void Vibration(SlsRun run, TimberSlsSettings st)
        {
            VibrationSettings v = st.Vibration;
            if (v.Method == VibrationMethod.Off) return;
            List<SpanResult> floor = run.Spans.Where(r => r.Span.Group == "Floor" && !r.Span.Cantilever && r.Error == null).ToList();
            if (floor.Count == 0) return;
            double largest = floor.Max(r => r.Span.Length);
            bool is2025 = v.Method == VibrationMethod.En1995_2025;
            double f1Axis = v.F1Source == F1Source.AxisVMVibration ? Frequency(v.VibrationCase) : double.NaN;
            if (v.F1Source == F1Source.AxisVMVibration && double.IsNaN(f1Axis))
                run.Notes.Add("No AxisVM vibration result case " + v.VibrationCase + ": f1 from the formula instead.");
            int wsysCase = 0;
            if (v.F1Source == F1Source.WsysLoadCase)
            {
                foreach (SlsLoadCase c in run.Cases.Values) if (string.Equals(c.Name, v.WsysLoadCase, StringComparison.OrdinalIgnoreCase)) wsysCase = c.Id;
                if (wsysCase == 0) run.Notes.Add("w_sys load case '" + v.WsysLoadCase + "' not found: f1 from the formula instead.");
            }

            foreach (SpanResult r in floor)
            {
                SpanInfo s = r.Span;
                double ei = s.E * s.Iy;   // Nm²
                if (ei <= 0) { r.VibNote = "No E·I for this section."; continue; }
                // Floor field: the member's own values where given (tab 4 table), else the defaults
                VibOverride ov = st.VibFor(s.Lines);
                double width = ov == null ? v.Width : VibOverride.Or(ov.Width, v.Width);
                double spacing = ov == null ? v.Spacing : VibOverride.Or(ov.Spacing, v.Spacing);
                double eiT = ov == null ? v.EiTransverse : VibOverride.Or(ov.EiT, v.EiTransverse);
                double eiDeck = ov == null ? v.EiDeckLongitudinal : VibOverride.Or(ov.EiDeckL, v.EiDeckLongitudinal);
                double gPerm = ov == null ? v.PermanentLoad : VibOverride.Or(ov.PermanentLoad, v.PermanentLoad);
                double qImp = ov == null ? v.ImposedLoad : VibOverride.Or(ov.ImposedLoad, v.ImposedLoad);
                double massGiven = ov != null && !double.IsNaN(ov.Mass) ? ov.Mass : v.MassOverride;
                double mass = massGiven > 0 ? massGiven : (gPerm + (is2025 ? 0.1 * qImp : 0)) * 1000.0 / 9.81;
                r.VibField = "b " + TimberSlsMath.Num(width, 2) + " m, s " + TimberSlsMath.Num(spacing, 2) + " m, (EI)_T " + TimberSlsMath.Sig(eiT) +
                             " Nm²/m, m " + TimberSlsMath.Num(mass, 1) + " kg/m²" + (ov == null ? " (defaults)" : " (member values)");
                // Span: own span; a two-span chain is a double span floor; more spans: largest span (9.3.2.1(3))
                List<SpanResult> chain = run.Spans.Where(x => x.Span.ChainId == s.ChainId && x.Span.Group == "Floor" && !x.Span.Cantilever).ToList();
                double l = s.Length, l2 = 0;
                if (v.SpanChoice == SpanChoice.LargestInGroup) l = largest;
                else if (chain.Count == 2) { l = chain.Max(x => x.Span.Length); l2 = chain.Min(x => x.Span.Length); }
                else if (chain.Count > 2) l = chain.Max(x => x.Span.Length);

                double f1Given = double.NaN;
                if (v.F1Source == F1Source.Custom && v.F1Custom > 0) f1Given = v.F1Custom;
                else if (!double.IsNaN(f1Axis)) f1Given = f1Axis;

                if (!is2025)
                {
                    TimberSlsMath.Ec5VibInput i = new TimberSlsMath.Ec5VibInput
                    {
                        Length = l, Width = width, Spacing = spacing, EiJoist = ei, EiDeckL = eiDeck, EiB = eiT,
                        Mass = mass, Zeta = v.Damping, Boundary = v.Boundary, KDistCustom = v.KDistCustom > 0 ? v.KDistCustom : double.NaN,
                        KAmp = v.KAmp, ALimCustom = v.ALimMode == ALimMode.Custom ? v.ALimCustom : double.NaN,
                        BMode = (int)v.BMode, BCustom = v.BCustom, F1Given = f1Given
                    };
                    r.Vib04 = TimberSlsMath.Ec5Vibration(i);
                    r.VibUc = r.Vib04.Uc;
                    r.VibNote = r.Vib04.F1Above8 ? "" : "f1 ≤ 8 Hz: special investigation (7.3.3(1))";
                }
                else
                {
                    TimberSlsMath.En2025Input i = new TimberSlsMath.En2025Input
                    {
                        Span = l, SecondSpan = l2, Width = width, EiL = ei / Math.Max(1e-6, spacing) + eiDeck, EiT = eiT,
                        Mass = mass, Zeta = v.Damping, Fw = v.WalkingFrequency, MultiFamily = v.MultiFamily,
                        Joisted = v.FloorType == FloorType.Joisted || v.FloorType == FloorType.JoistedFloating,
                        FourEdges = v.TwoWay, F1Given = f1Given,
                        WSysMm = wsysCase > 0 ? MaxAbsolute(s, wsysCase, st.General) * AxisFactor(s, st.General) : double.NaN,
                        F1Beam1 = v.F1Beam1, F1Beam2 = v.F1Beam2, EiStiffener = v.EiStiffener, WBeam1 = v.WBeam1, WBeam2 = v.WBeam2,
                        KRed = v.KRed, MuRes = v.MuRes, FDyn = v.FDyn, ModalMassDivisor = v.ModalMass == ModalMassEdges.Two ? 2 : 4,
                        TargetLevel = (int)v.TargetLevel, Table = v.BuildTable()
                    };
                    r.Vib25 = TimberSlsMath.En2025Vibration(i);
                    r.VibUc = r.Vib25.Uc;
                    r.VibNote = "level " + (r.Vib25.AchievedLevel < 0 ? "none" : TimberSlsMath.Levels[r.Vib25.AchievedLevel]);
                }
            }
        }

        // ------------------------------------------------------------------
        // Overlay on the model (doc §9)
        // ------------------------------------------------------------------

        /// <summary>Colour scale used by the window, the overlay and the report (set from the display settings).</summary>
        public static ColourScale Scale = ColourScale.Gradient;

        private static readonly double[] StopUc = { 0.0, 0.5, 0.8, 1.0, 1.25 };
        private static readonly int[] StopRgb = { 0x1E9E4A, 0x8CC63E, 0xF2C80F, 0xF07B16, 0xD7261E };

        /// <summary>Colour of a utilisation, RGB: a continuous gradient through green, yellow (0.8), orange (1.0) to red, or steps.</summary>
        public static int UcRgb(double uc)
        {
            if (double.IsNaN(uc)) return 0x8C96A0;
            if (Scale == ColourScale.Steps)
            {
                if (uc <= 0.5) return 0x1E9E4A;
                if (uc <= 0.8) return 0x8CC63E;
                if (uc <= 1.0) return 0xF2B705;
                if (uc <= 1.2) return 0xF07B16;
                return 0xD7261E;
            }
            if (uc <= 0) return StopRgb[0];
            for (int i = 1; i < StopUc.Length; i++)
            {
                if (uc > StopUc[i]) continue;
                double t = (uc - StopUc[i - 1]) / (StopUc[i] - StopUc[i - 1]);
                return Mix(StopRgb[i - 1], StopRgb[i], t);
            }
            return StopRgb[StopRgb.Length - 1];
        }

        private static int Mix(int a, int b, double t)
        {
            Func<int, int, int> ch = (x, s) => (x >> s) & 0xFF;
            int r = (int)Math.Round(ch(a, 16) + (ch(b, 16) - ch(a, 16)) * t), g = (int)Math.Round(ch(a, 8) + (ch(b, 8) - ch(a, 8)) * t),
                bl = (int)Math.Round(ch(a, 0) + (ch(b, 0) - ch(a, 0)) * t);
            return (r << 16) | (g << 8) | bl;
        }

        /// <summary>One coloured piece of a span: from x0 to x1 (m), with its utilisation.</summary>
        public class Piece { public SpanInfo Span; public double X0, X1, Uc; }

        /// <summary>Replaces the overlay layer. Returns null when it worked, otherwise a message.</summary>
        public string ShowOverlay(IList<Piece> pieces, IList<KeyValuePair<SpanInfo, string>> labels, DisplaySettings display)
        {
            Scale = display.Scale;
            try
            {
                HideOverlay(false);
                IAxisVMLayers layers = model.Layers;
                int layer = layers.AddLayer(TimberSlsFunction.LayerName, Colour(0x1E9E4A), ELayerPenStyle.lpsSolid, 1.5);
                if (layer <= 0) return "AxisVM refused to add the drawing layer (error " + layer + ").";
                int failed = 0;
                foreach (Piece p in pieces)
                {
                    RLayerShapeAttributes attr = new RLayerShapeAttributes { LayerIndex = layer, Colour = Colour(UcRgb(p.Uc)), PenStyle = ELayerPenStyle.lpsSolid, PenWidth = Math.Max(0.5, display.LineWidth / 2.0) };
                    AxisVMLines3d poly = NewLines();
                    RLine3d seg = new RLine3d { LineType = ELine3dType.ltStraightLine3d, P1 = P(p.Span.PointAt(p.X0)), P2 = P(p.Span.PointAt(p.X1)) };
                    poly.Add(ref seg);
                    if (layers.AddShape(ELayerShapeType.lst3DLine, poly, ref attr) <= 0) failed++;
                }
                foreach (var kv in display.Labels ? labels : new List<KeyValuePair<SpanInfo, string>>())
                {
                    SpanInfo s = kv.Key;
                    Vec t = (s.End - s.Start).Unit;
                    Vec up = Math.Abs(t.Z) > 0.9 ? new Vec(1, 0, 0) : new Vec(0, 0, 1);
                    Vec side = t.Cross(up).Unit;
                    double h = Math.Max(0.08, Math.Min(0.3, s.Length / 25)) * display.LabelSize / 9.0;
                    Vec at = s.PointAt(s.Length / 2) + (0.5 * h) * up;
                    RLayerTextParams tp = new RLayerTextParams
                    {
                        LayerIndex = layer, Colour = Colour(0x24354A), PenStyle = ELayerPenStyle.lpsSolid, PenWidth = 0.5,
                        FontSize = h, Angle = 0, AlignmentPoint1 = P(at), AlignmentPoint2 = P(at + h * t),
                        NormalVector = P(Math.Abs(t.Z) > 0.9 ? side : new Vec(0, 0, 1)),
                        HorizontalAlignment = ETextHorizontalAlignment.thaCenter, VerticalAlignment = ETextVerticalAlignment.tvaBottom
                    };
                    if (layers.AddText(kv.Value, "Arial", ref tp) <= 0) failed++;
                }
                // Keep the utilisation legend in the checker window, outside the model overlay.
                Redraw();
                return failed == 0 ? null : failed + " overlay part(s) could not be drawn.";
            }
            catch (Exception ex) { return "Could not draw the overlay: " + ex.Message; }
        }

        public void HideOverlay(bool redraw = true)
        {
            try
            {
                IAxisVMLayers layers = model.Layers;
                bool any = false;
                for (int i = layers.Count; i >= 1; i--)
                {
                    string name;
                    try { name = layers.get_Name(i); } catch { continue; }
                    if (name != TimberSlsFunction.LayerName) continue;
                    layers.DeleteLayer(i);
                    any = true;
                }
                if (any && redraw) Redraw();
            }
            catch { }
        }

        private AxisVMLines3d NewLines()
        {
            try { AxisVMLines3d l = app.ObjectCreator.NewLines3d(); if (l != null) return l; } catch { }
            return new AxisVMLines3d();
        }

        private void Redraw()
        {
            try { model.Windows.ReDraw(); } catch { }
            try { model.Refresh(); } catch { }
        }

        private static RPoint3d P(Vec v) { return new RPoint3d { x = v.X, y = v.Y, z = v.Z }; }
        private static int Colour(int rgb) { return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF); }
    }
}
