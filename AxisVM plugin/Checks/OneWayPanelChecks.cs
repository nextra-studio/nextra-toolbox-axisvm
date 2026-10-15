using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AxisVM;
using AxisVM_plugin.Functions;

// Tests production geometry against a small in-memory API double. No AxisVM model is opened.
// The double deliberately deletes split UIDs and renumbers indices, as documented by AxisVM.
internal static class OneWayPanelChecks
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        checks++;
    }

    private static IAxisVMModel Rectangle(bool subdivided)
    {
        var m = new IAxisVMModel();
        int a = m.Nodes.Add(0, 0, 0), b = m.Nodes.Add(4, 0, 0);
        int c = m.Nodes.Add(4, 8, 0), d = m.Nodes.Add(0, 8, 0);
        m.Lines.AddLine(a, b);
        if (subdivided)
        {
            int e = m.Nodes.Add(4, 0.3, 0), f = m.Nodes.Add(4, 4, 0);
            m.Lines.AddLine(e, b); // reversed segment, inset exactly on an existing node
            m.Lines.AddLine(e, f);
            m.Lines.AddLine(c, f);
        }
        else m.Lines.AddLine(b, c);
        m.Lines.AddLine(c, d);
        m.Lines.AddLine(a, d);
        return m;
    }

    private static List<ResolvedLine> Boundary(IAxisVMModel m)
    {
        return Enumerable.Range(1, m.Lines.Count).Select(i => OneWayPanelBuilder.ResolveLine(m, i)).ToList();
    }

    private static List<int> Generate(IAxisVMModel m, bool single, List<string> notes)
    {
        return OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions
        { NoSpacing = single, EdgeSpacingMm = 300, AngleToleranceDeg = 15 }, 1, notes);
    }

    private static int Main()
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            double number;
            Check(OneWayPanelOptions.TryParseNumber("15.0", out number) && number == 15, "Dutch culture: decimal point stays 15");
            Check(OneWayPanelOptions.TryParseNumber("15,5", out number) && number == 15.5, "Dutch decimal comma accepted");
            Check(!OneWayPanelOptions.TryParseNumber("NaN", out number), "NaN rejected");
            Check(!OneWayPanelOptions.TryParseNumber("Infinity", out number), "infinity rejected");
            Check(new OneWayPanelOptions { NoSpacing = true, AngleToleranceDeg = 45 }.Validate() != null, "ambiguous tolerance rejected");
            Check(new OneWayPanelOptions { NoSpacing = true, AngleToleranceDeg = 15, UseSpanAngle = true, SpanAngleDeg = double.PositiveInfinity }.Validate() != null,
                "infinite explicit span rejected");

            var m = Rectangle(false);
            Check(Generate(m, true, new List<string>()).Count == 1, "single panel generated");
            Check(m.LoadPanels.Item[1].Receiving.Count == 2 && m.LoadPanels.Item[1].Selected.Values.Count(v => v) == 2,
                "single panel selects only the two supports");
            Check(m.Lines.Count == 4, "single panel does not split beams");
            m = Rectangle(false);
            m.LoadPanels.ArrayLowerBound = 0;
            Check(Generate(m, false, new List<string>()).Count == 3, "three spaced panels with zero-based contour array");
            Check(m.LoadPanels.Item[1].Receiving.Count == 1 && m.LoadPanels.Item[2].Receiving.Count == 1 && m.LoadPanels.Item[3].Receiving.Count == 2,
                "edge strips feed edge beams; centre feeds supports");
            Check(m.Lines.Count == 10, "four support splits and two inset lines");
            m = Rectangle(true);
            Check(Generate(m, false, new List<string>()).Count == 3, "subdivided reversed segments and existing inset node supported");

            for (int failedSplit = 1; failedSplit <= 4; failedSplit++)
            {
                m = Rectangle(false);
                m.Lines.FailSplitAt = failedSplit;
                var notes = new List<string>();
                Check(Generate(m, false, notes).Count == 1, "split failure " + failedSplit + " falls back to closed full boundary");
                Check(m.Lines.Count == 4 + failedSplit - 1 && m.Nodes.Count == 4 + failedSplit - 1,
                    "split failure " + failedSplit + " retains valid splits and removes unused node");
            }
            for (int rejectedPanel = 1; rejectedPanel <= 3; rejectedPanel++)
            {
                m = Rectangle(false);
                m.LoadPanels.RejectAddAt = rejectedPanel;
                var notes = new List<string>();
                Check(Generate(m, false, notes).Count == 1 && m.LoadPanels.Count == 1,
                    "panel rejection " + rejectedPanel + " cleans partial panels and restores full boundary");
                Check(m.Lines.Count == 8 && m.LoadPanels.Item[1].Contour.Length == 8,
                    "panel rejection " + rejectedPanel + " removes insets and retains all split boundary pieces");
            }

            m = Rectangle(false);
            m.LoadPanels.FailSetLinesAt = 2;
            bool threw = false;
            var survivingBoundaryUids = Enumerable.Range(1, m.Lines.Count).Select(m.Lines.get_UID).ToList();
            try
            {
                OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions
                { EdgeSpacingMm = 300, AngleToleranceDeg = 15 }, 1, new List<string>(), survivingBoundaryUids);
            }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && m.LoadPanels.Count == 0 && m.Lines.Count == 8, "configuration error is surfaced and partial panels/insets cleaned");
            Check(survivingBoundaryUids.Count == 8 && survivingBoundaryUids.All(uid => m.Lines.get_IndexOfUID(uid) > 0),
                "conversion restoration receives surviving child UIDs even when configuration throws");
            Array restore = survivingBoundaryUids.Select(uid => m.Lines.get_IndexOfUID(uid)).ToArray();
            Check(m.LoadPanels.AddFromLineIDs(ref restore) > 0, "conversion can restore a closed plain panel after configuration failure");

            m = Rectangle(false);
            var noSupports = OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions
            { NoSpacing = true, AngleToleranceDeg = 15, UseSpanAngle = true, SpanAngleDeg = 45 }, 1, new List<string>());
            Check(noSupports.Count == 0 && m.LoadPanels.Count == 0, "unsupported explicit angle cannot produce a panel with no receiving lines");
            m = Rectangle(false);
            Check(OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions
            { NoSpacing = true, SpanningDirection = 2, AngleToleranceDeg = 15 }, 1, new List<string>()).Count == 1 &&
                m.LoadPanels.Item[1].Receiving.SetEquals(new int[] { 1, 3 }), "Dir 2 overrides short-span support selection");

            m = Rectangle(false);
            m.Lines.AddLine(1, 3);
            Check(Generate(m, false, new List<string>()).Count == 0 && m.Lines.SplitCalls == 0, "branched/shared-bay selection rejected before splitting");
            m = Rectangle(false);
            m.Lines.Delete(4);
            Check(Generate(m, false, new List<string>()).Count == 0 && m.Lines.SplitCalls == 0, "open boundary rejected before splitting");
            m = Rectangle(false);
            m.Nodes.Data[2] = new RNode { x = 4, y = 0, z = 1 };
            Check(Generate(m, false, new List<string>()).Count == 0, "sloped boundary rejected instead of assigning XY load path");
            // Auto follows the panel's own edges: shorter span vs. the same turned 90 deg.
            m = Rectangle(false); // 4 wide (X) x 8 long (Y): lines 1,3 along X, 2,4 along Y
            OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions { NoSpacing = true, SpanningDirection = 0, AngleToleranceDeg = 15 }, 1, new List<string>());
            Check(m.LoadPanels.Item[1].Receiving.SetEquals(new int[] { 2, 4 }), "Auto shorter: spans the 4 m way onto the long sides");
            m = Rectangle(false);
            OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions { NoSpacing = true, SpanningDirection = 3, AngleToleranceDeg = 15 }, 1, new List<string>());
            Check(m.LoadPanels.Item[1].Receiving.SetEquals(new int[] { 1, 3 }), "Auto longer: spans the 8 m way onto the short sides");
            m = new IAxisVMModel(); // the same bay rotated 30 deg: Auto must still pick the bay's short span
            double c30 = Math.Cos(Math.PI / 6), s30 = Math.Sin(Math.PI / 6);
            Func<double, double, int> rn = (u, v) => m.Nodes.Add(u * c30 - v * s30, u * s30 + v * c30, 0);
            int r1 = rn(0, 0), r2 = rn(4, 0), r3 = rn(4, 8), r4 = rn(0, 8);
            m.Lines.AddLine(r1, r2); m.Lines.AddLine(r2, r3); m.Lines.AddLine(r3, r4); m.Lines.AddLine(r4, r1);
            OneWayPanelBuilder.GeneratePanels(m, Boundary(m), new OneWayPanelOptions { NoSpacing = true, SpanningDirection = 0, AngleToleranceDeg = 15 }, 1, new List<string>());
            Check(m.LoadPanels.Item[1].Receiving.SetEquals(new int[] { 2, 4 }), "Auto shorter on a rotated bay uses the bay's own edges");
            Check(new OneWayPanelOptions { NoSpacing = true, SpanningDirection = 4, AngleToleranceDeg = 15 }.Validate() != null, "unknown span option rejected");

            // Intermediate members inside the bay (4 wide in X, 8 long in Y; short span = X).
            m = Rectangle(false);
            int boundaryCount = m.Lines.Count;
            int ia = m.Nodes.Add(1, 0, 0), ib = m.Nodes.Add(1, 8, 0);   // along Y = across the X span
            int across = m.Lines.AddLine(ia, ib);
            int pa = m.Nodes.Add(0, 4, 0), pb = m.Nodes.Add(4, 4, 0);   // along X = parallel to the span
            int along = m.Lines.AddLine(pa, pb);
            int ta = m.Nodes.Add(3, 0, 0), tb = m.Nodes.Add(3.5, 8, 0); // ~3.6 deg off "across"
            int tilted = m.Lines.AddLine(ta, tb);
            int plainA = m.Nodes.Add(2, 0, 0), plainB = m.Nodes.Add(2, 8, 0);
            int plain = m.Lines.AddLine(plainA, plainB, ELineType.ltSimpleLine);
            var boundaryOnly = Enumerable.Range(1, boundaryCount).Select(i => OneWayPanelBuilder.ResolveLine(m, i)).ToList();
            var interiorNotes = new List<string>();
            Check(OneWayPanelBuilder.GeneratePanels(m, boundaryOnly, new OneWayPanelOptions
            { NoSpacing = true, AngleToleranceDeg = 5, UseSpanAngle = true, SpanAngleDeg = 0 }, 1, interiorNotes).Count == 1,
                "single panel with interior members generated");
            var recv = m.LoadPanels.Item[1].Receiving;
            Check(recv.Contains(across) && recv.Contains(tilted), "interior members across the span (within tolerance) receive load");
            Check(!recv.Contains(along) && !recv.Contains(plain), "members along the span and plain lines receive nothing");
            Check(m.LoadPanels.Item[1].Selected.Values.Count(v => v) == 2, "edge flags still mark only the two boundary supports");
            Check(interiorNotes.Any(n => n.Contains("2 intermediate member")), "interior receivers are reported");

            m = Rectangle(false);
            boundaryCount = m.Lines.Count;
            ia = m.Nodes.Add(1, 0, 0); ib = m.Nodes.Add(1, 8, 0);
            across = m.Lines.AddLine(ia, ib);
            ta = m.Nodes.Add(3, 0, 0); tb = m.Nodes.Add(3.5, 8, 0);
            tilted = m.Lines.AddLine(ta, tb);
            boundaryOnly = Enumerable.Range(1, boundaryCount).Select(i => OneWayPanelBuilder.ResolveLine(m, i)).ToList();
            OneWayPanelBuilder.GeneratePanels(m, boundaryOnly, new OneWayPanelOptions
            { NoSpacing = true, AngleToleranceDeg = 2, UseSpanAngle = true, SpanAngleDeg = 0 }, 1, new List<string>());
            recv = m.LoadPanels.Item[1].Receiving;
            Check(recv.Contains(across) && !recv.Contains(tilted), "a member outside the tolerance receives nothing");

            m = Rectangle(false);
            boundaryCount = m.Lines.Count;
            pa = m.Nodes.Add(0, 4, 0); pb = m.Nodes.Add(4, 4, 0);       // across the Y span, through the centre and both strips
            int mid = m.Lines.AddLine(pa, pb);
            boundaryOnly = Enumerable.Range(1, boundaryCount).Select(i => OneWayPanelBuilder.ResolveLine(m, i)).ToList();
            Check(OneWayPanelBuilder.GeneratePanels(m, boundaryOnly, new OneWayPanelOptions
            { EdgeSpacingMm = 300, AngleToleranceDeg = 10, UseSpanAngle = true, SpanAngleDeg = 90 }, 1, new List<string>()).Count == 3,
                "edge strips generated with an intermediate beam");
            int midNow = m.Lines.get_IndexOfUID(m.Lines.get_UID(mid));
            Check(m.LoadPanels.Item[3].Receiving.Contains(midNow) &&
                  !m.LoadPanels.Item[1].Receiving.Contains(midNow) && !m.LoadPanels.Item[2].Receiving.Contains(midNow),
                "intermediate beam loads from the centre panel only, not from the edge strips");

            Console.WriteLine(checks + " checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

namespace AxisVM
{
    public enum ELongBoolean { False = 0, True = 1 }
    public enum ELineGeomType { lgtStraightLine }
    public enum ELineType { ltTruss = 0, ltBeam = 1, ltRib = 2, ltSimpleLine = 7 }
    public struct RLineGeomData { }
    public struct RNode { public double x, y, z; }
    public sealed class IAxisVMModel
    {
        public readonly Nodes Nodes = new Nodes();
        public readonly Lines Lines;
        public readonly Panels LoadPanels;
        public IAxisVMModel() { Lines = new Lines(); LoadPanels = new Panels(this); }
    }
    public sealed class Nodes
    {
        internal readonly Dictionary<int, RNode> Data = new Dictionary<int, RNode>();
        private int next;
        public int Count { get { return Data.Count; } }
        public int Add(double x, double y, double z) { Data[++next] = new RNode { x = x, y = y, z = z }; return next; }
        public int GetNode(int id, ref RNode node) { node = Data[id]; return id; }
        public int Delete(int id) { return Data.Remove(id) ? 1 : -1; }
    }
    public sealed class Line
    {
        internal Lines Owner;
        internal int Uid;
        public int StartNode, EndNode;
        public ELineType LineType = ELineType.ltBeam;
        public ELineGeomType GeomType { get { return ELineGeomType.lgtStraightLine; } }
        public int SplitByNode(int node, out int first, out int second)
        {
            first = second = 0;
            if (++Owner.SplitCalls == Owner.FailSplitAt) return -1;
            Owner.Delete(Owner.get_IndexOfUID(Uid));
            first = Owner.AddLine(StartNode, node, LineType);
            second = Owner.AddLine(node, EndNode, LineType);
            return 1;
        }
    }
    public sealed class Lines
    {
        private readonly List<Line> data = new List<Line>();
        private int nextUid;
        public int SplitCalls, FailSplitAt;
        public int Count { get { return data.Count; } }
        public Lines Item { get { return this; } }
        [System.Runtime.CompilerServices.IndexerName("Entry")]
        public Line this[int id] { get { return data[id - 1]; } }
        public int AddLine(int a, int b, ELineType type = ELineType.ltBeam) { data.Add(new Line { Owner = this, Uid = ++nextUid, StartNode = a, EndNode = b, LineType = type }); return Count; }
        // IAxisVMLines.Add makes a plain line without an element, as in AxisVM.
        public int Add(int a, int b, ELineGeomType type, ref RLineGeomData geom) { return AddLine(a, b, ELineType.ltSimpleLine); }
        public int get_UID(int id) { return this[id].Uid; }
        public int get_IndexOfUID(int uid) { return data.FindIndex(l => l.Uid == uid) + 1; }
        public int Delete(int id) { data.RemoveAt(id - 1); return 1; }
    }
    public sealed class Panel
    {
        internal int Uid;
        internal Panels Owner;
        public int[] Contour;
        public ELongBoolean Auto;
        public readonly HashSet<int> Receiving = new HashSet<int>();
        public readonly Dictionary<int, bool> Selected = new Dictionary<int, bool>();
        public int SetLines(ref Array ids)
        {
            if (++Owner.SetCalls == Owner.FailSetLinesAt) return -1;
            foreach (int id in ids) Receiving.Add(id);
            return ids.Length;
        }
        public int GetContourLineIDs(ref Array ids)
        {
            ids = Array.CreateInstance(typeof(int), new int[] { Contour.Length }, new int[] { Owner.ArrayLowerBound });
            for (int i = 0; i < Contour.Length; i++) ids.SetValue(Contour[i], i + Owner.ArrayLowerBound);
            return Contour.Length;
        }
        public void set_SelectedEdge(int id, ELongBoolean selected)
        {
            if (id < 1 || id > Contour.Length) throw new Exception("Invalid edge index");
            Selected[id] = selected == ELongBoolean.True;
        }
    }
    public sealed class Panels
    {
        private readonly IAxisVMModel model;
        private readonly List<Panel> data = new List<Panel>();
        private int nextUid, addCalls;
        public int RejectAddAt, FailSetLinesAt, SetCalls, ArrayLowerBound = 1;
        public Panels(IAxisVMModel model) { this.model = model; }
        public int Count { get { return data.Count; } }
        public Panels Item { get { return this; } }
        [System.Runtime.CompilerServices.IndexerName("Entry")]
        public Panel this[int id] { get { return data[id - 1]; } }
        public int get_UID(int id) { return this[id].Uid; }
        public int get_IndexOfUID(int uid) { return data.FindIndex(p => p.Uid == uid) + 1; }
        public int Delete(int id) { data.RemoveAt(id - 1); return 1; }
        public int AddFromLineIDs(ref Array ids)
        {
            if (++addCalls == RejectAddAt) return -1;
            var boundary = new List<ResolvedLine>();
            foreach (int id in ids) boundary.Add(OneWayPanelBuilder.ResolveLine(model, id));
            if (OneWayPanelBuilder.ValidateBoundary(model, boundary) != null) return -2;
            data.Add(new Panel { Uid = ++nextUid, Owner = this, Contour = ids.Cast<int>().ToArray() });
            return Count;
        }
    }
}
