using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AxisVM;
using AxisVM_plugin.Functions;

/// <summary>
/// Live checks for Functions/LoadPanelOps.cs against a running AxisVM. Each case builds its own geometry far
/// from the model (x >= 100 m), runs the operation inside BeginUpdate / EndUpdate exactly as the add-on does,
/// and checks the result. Everything the run created is deleted again at the end.
/// </summary>
internal static class LoadPanelToolsLiveChecks
{
    private static int failures;
    private static IAxisVMModel m;
    private static double x0 = 100;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
        if (!condition) failures++;
    }

    private static int Main()
    {
        IAxisVMApplication app = FindAxisVM();
        if (app == null) { Console.WriteLine("FAIL  no running AxisVM found in the Running Object Table"); return 1; }
        m = app.Models.Item[1];
        if (m.FileName.IndexOf("LoadPanelTest", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.WriteLine("FAIL  refusing to run: the open model is not a LoadPanelTest*.axs scratch file (" + m.FileName + ")");
            return 1;
        }

        int panels0 = m.LoadPanels.Count, domains0 = m.Domains.Count, lines0 = m.Lines.Count, nodes0 = m.Nodes.Count, loads0 = m.Loads.Count;
        Console.WriteLine(string.Format("model before: panels {0}, domains {1}, lines {2}, nodes {3}, loads {4}", panels0, domains0, lines0, nodes0, loads0));

        try
        {
            SplitOneLine();
            SplitTwoLines();
            SplitDiagonal();
            SplitRejectsLineOffContour();
            SplitRejectsLineAlongEdge();
            SplitRejectsChordOutsideConcavePanel();
            PanelToDomainKeep();
            PanelToDomainDelete();
            DomainToPanel();
            DomainToPanelDelete();

            SplitKeepsLinearLoad();
            SplitByBeamCarriesLoad();
            SplitAutoPanel();
            SplitCornerToEdge();
            SplitAtExistingCorners();
            SplitWallPanel();
            SplitTShape();
            SplitCrossingLinesReportsUnused();
            SplitSeveralPanelsAtOnce();
            UserDefinedPanelIsSkipped();
            SeveralPanelsToDomains();
            SeveralDomainsToPanelsDelete();

            MergeRoundTrip();
            MergeThreePieces();
            MergeTwoGroupsAtOnce();
            MergeLShape();
            MergeRefusesNonAdjacent();
            MergeRefusesDifferentLoads();
            MergeRefusesNonCoplanar();
            MergeRefusesMixedAuto();
            MergeReportsLoadedInteriorBeam();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  exception: " + ex);
            failures++;
        }
        finally
        {
            Cleanup(panels0, domains0, lines0, nodes0);
        }

        Check(m.LoadPanels.Count == panels0 && m.Domains.Count == domains0 && m.Lines.Count == lines0 && m.Nodes.Count == nodes0 && m.Loads.Count == loads0,
            string.Format("cleanup restored the model (panels {0}, domains {1}, lines {2}, nodes {3}, loads {4})",
                m.LoadPanels.Count, m.Domains.Count, m.Lines.Count, m.Nodes.Count, m.Loads.Count));

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Split
    // ------------------------------------------------------------------

    private static void SplitOneLine()
    {
        Rect r = NewRect(6, 4);
        int a = Node(3, 0), b = Node(3, 4);          // free nodes sitting part-way along both long edges
        int cut = Line(a, b);
        int loadsBefore = m.Loads.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.Converted == 1 && res.PiecesCreated == 2 && res.SplittersUsed == 1, "split by one line: 2 pieces");
        Check(m.LoadPanels.get_IndexOfUID(r.PanelUid) <= 0, "split by one line: original panel removed");
        foreach (int uid in res.Created) CheckPiece(uid, 2, "split by one line");
        Check(m.Loads.Count == loadsBefore + 1, "split by one line: panel load copied to both pieces (net +1)");
    }

    private static void SplitTwoLines()
    {
        Rect r = NewRect(6, 4);
        int c1 = Line(Node(2, 0), Node(2, 4));
        int c2 = Line(Node(4, 0), Node(4, 4));

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(c1), Uid(c2) }));

        Check(res.PiecesCreated == 3 && res.SplittersUsed == 2, "split by two lines: 3 pieces");
        foreach (int uid in res.Created) CheckPiece(uid, 2, "split by two lines");
        Check(res.LoadsCopied == 3, "split by two lines: load on all 3 pieces");
    }

    private static void SplitDiagonal()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(r.Nodes[0], r.Nodes[2]);      // corner to corner

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.PiecesCreated == 2, "diagonal split: 2 triangles");
        foreach (int uid in res.Created)
        {
            int idx = m.LoadPanels.get_IndexOfUID(uid);
            Check(ContourIds(idx).Count == 3, "diagonal split: piece is a triangle");
            Check(LoadedIds(idx).Count == 1 && LoadedIds(idx).All(IsLongEdge), "diagonal split: one long edge loaded per triangle");
        }
    }

    private static void SplitRejectsLineOffContour()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(Node(3, 0), Node(3, 6));      // second end is beyond the panel
        int lines = m.Lines.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.Converted == 0 && m.LoadPanels.get_IndexOfUID(r.PanelUid) > 0, "line off the contour: panel kept");
        Check(m.Lines.Count == lines, "line off the contour: no contour edge split behind");
        Check(res.Notes.Count == 1 && res.Notes[0].Contains("does not lie on the panel contour"), "line off the contour: reason reported");
    }

    private static void SplitRejectsLineAlongEdge()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(r.Nodes[0], Node(3, 0));      // corner to a point on the same edge
        int lines = m.Lines.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.Converted == 0 && m.Lines.Count == lines, "line along an edge: rejected, nothing split");
    }

    private static void SplitRejectsChordOutsideConcavePanel()
    {
        // L shape: (0,0) (6,0) (6,2) (2,2) (2,4) (0,4). The chord (6,2)-(2,4) runs outside.
        NextSlot();
        int[] n = { Node(0, 0), Node(6, 0), Node(6, 2), Node(2, 2), Node(2, 4), Node(0, 4) };
        List<int> edges = new List<int>();
        for (int i = 0; i < n.Length; i++) edges.Add(Line(n[i], n[(i + 1) % n.Length]));
        int panelUid = Panel(edges, new int[0]);
        int cut = Line(n[2], n[4]);

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { panelUid }, new[] { Uid(cut) }));

        Check(res.Converted == 0 && res.Notes.Count == 1 && res.Notes[0].Contains("outside"), "concave panel: chord outside rejected");
    }

    /// <summary>A piece must have its own load and exactly the long-edge halves as its loaded lines.</summary>
    private static void CheckPiece(int uid, int expectedLoaded, string label)
    {
        int idx = m.LoadPanels.get_IndexOfUID(uid);
        Check(idx > 0, label + ": piece exists");
        if (idx <= 0) return;

        List<int> loaded = LoadedIds(idx);
        Check(loaded.Count == expectedLoaded && loaded.All(IsLongEdge),
            label + ": piece " + idx + " loads its long-edge halves only (got " + string.Join(",", loaded) + ")");
        Check(m.LoadPanels.Item[idx].Auto == ELongBoolean.lbFalse, label + ": Auto flag inherited");

        int loads = 0;
        for (int i = 1; i <= m.Loads.Count; i++)
        {
            if ((int)m.Loads.get_LoadType(i) == -1 && m.Loads.get_ElementId(i) == idx) loads++;
        }
        Check(loads == 1, label + ": piece " + idx + " carries the copied load");
        double[] v = PanelLoadValues(idx);
        Check(v != null && v.All(x => Math.Abs(x + 5) < 1e-9), label + ": piece " + idx + " load is -5 kN/m2 (got " + (v == null ? "none" : string.Join(",", v)) + ")");
    }

    // ------------------------------------------------------------------
    // Panel <-> domain
    // ------------------------------------------------------------------

    private static void PanelToDomainKeep()
    {
        Rect r = NewRect(6, 4);
        string name;
        int mat = LoadPanelOps.FindDefaultMaterial(m, out name);

        LoadPanelOpResult res = Run(() => LoadPanelOps.PanelsToDomains(m, new[] { r.PanelUid }, mat, false));

        Check(res.Converted == 1 && res.Created.Count == 1 && res.Created[0] > 0, "panel -> domain: domain created");
        Check(m.LoadPanels.get_IndexOfUID(r.PanelUid) > 0, "panel -> domain (keep): panel kept");
        Check(Math.Abs(m.Domains.Item[res.Created[0]].Area - 24) < 1e-6, "panel -> domain: domain has the panel's area");
    }

    private static void PanelToDomainDelete()
    {
        Rect r = NewRect(6, 4);
        string name;
        int mat = LoadPanelOps.FindDefaultMaterial(m, out name);

        LoadPanelOpResult res = Run(() => LoadPanelOps.PanelsToDomains(m, new[] { r.PanelUid }, mat, true));

        Check(res.Converted == 1 && res.Deleted == 1 && m.LoadPanels.get_IndexOfUID(r.PanelUid) <= 0, "panel -> domain (delete): panel removed");
        Check(res.LoadsCopied == 1, "panel -> domain (delete): panel load moved onto the domain");

        int onDomain = 0;
        for (int i = 1; i <= m.Loads.Count; i++)
        {
            if (m.Loads.get_LoadType(i).ToString().StartsWith("ltDomain") && m.Loads.get_ElementId(i) == res.Created[0]) onDomain++;
        }
        Check(onDomain == 1, "panel -> domain (delete): domain carries one load");
    }

    private static void DomainToPanel()
    {
        int domain = NewDomain(6, 4);

        LoadPanelOpResult res = Run(() => LoadPanelOps.DomainsToPanels(m, new[] { domain }, false));
        Check(res.Converted == 1 && res.Created.Count == 1 && m.LoadPanels.get_IndexOfUID(res.Created[0]) > 0, "domain -> panel: panel created");
        Check(m.Domains.Count >= domain, "domain -> panel (keep): domain kept");

        LoadPanelOpResult again = Run(() => LoadPanelOps.DomainsToPanels(m, new[] { domain }, true));
        Check(again.Converted == 0 && again.Deleted == 0 && again.Notes.Count == 1 && again.Notes[0].Contains("already covers"),
            "domain -> panel twice: existing panel recognised, domain kept");
    }

    private static void DomainToPanelDelete()
    {
        int domain = NewDomain(6, 4);
        int domains = m.Domains.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.DomainsToPanels(m, new[] { domain }, true));

        Check(res.Converted == 1 && res.Deleted == 1 && m.Domains.Count == domains - 1, "domain -> panel (delete): domain removed");
        Check(res.SurvivingPanels == 1 && m.LoadPanels.get_IndexOfUID(res.Created[0]) > 0, "domain -> panel (delete): panel survives");
    }

    // ------------------------------------------------------------------
    // More cases
    // ------------------------------------------------------------------

    private static void SplitByBeamCarriesLoad()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(Node(3, 0), Node(3, 4));
        RPoint3d ecc = new RPoint3d();
        Check(m.Lines.Item[cut].DefineAsBeam(1, 1, 1, ref ecc, ref ecc) > 0, "beam cutter: line defined as beam");
        int cutUid = Uid(cut);

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { cutUid }));

        Check(res.PiecesCreated == 2, "beam cutter: 2 pieces");
        foreach (int uid in res.Created)
        {
            List<int> loaded = LoadedIds(m.LoadPanels.get_IndexOfUID(uid));
            Check(loaded.Count == 3 && loaded.Contains(m.Lines.get_IndexOfUID(cutUid)),
                "beam cutter: piece loads both long-edge halves and the beam (got " + string.Join(",", loaded) + ")");
        }
    }

    private static void SplitAutoPanel()
    {
        Rect r = NewRect(6, 4);
        m.LoadPanels.Item[m.LoadPanels.get_IndexOfUID(r.PanelUid)].Auto = ELongBoolean.lbTrue;
        int cut = Line(Node(3, 0), Node(3, 4));

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.PiecesCreated == 2 && res.Created.All(u => m.LoadPanels.Item[m.LoadPanels.get_IndexOfUID(u)].Auto == ELongBoolean.lbTrue),
            "auto panel: 2 pieces, both Auto");
        Check(res.LoadsCopied == 2, "auto panel: load on both pieces");
    }

    private static void SplitCornerToEdge()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(r.Nodes[0], Node(3, 4));      // corner (0,0) to the middle of the far edge

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        List<int> sizes = res.Created.Select(u => ContourIds(m.LoadPanels.get_IndexOfUID(u)).Count).OrderBy(c => c).ToList();
        Check(res.PiecesCreated == 2 && sizes.SequenceEqual(new[] { 3, 4 }), "corner to edge: a triangle and a quadrilateral");
        Check(res.Created.Sum(u => LoadedIds(m.LoadPanels.get_IndexOfUID(u)).Count) == 3, "corner to edge: 3 loaded long-edge parts in total");
    }

    private static void SplitAtExistingCorners()
    {
        // The long edges are already split at x = 3 (as AxisVM does when a line is drawn snapped to an edge).
        NextSlot();
        int[] n = { Node(0, 0), Node(3, 0), Node(6, 0), Node(6, 4), Node(3, 4), Node(0, 4) };
        List<int> e = new List<int>();
        for (int i = 0; i < 6; i++) e.Add(Line(n[i], n[(i + 1) % 6]));
        int panelUid = Panel(e, new[] { e[0], e[1], e[3], e[4] });
        int cut = Line(n[1], n[4]);
        int lines = m.Lines.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { panelUid }, new[] { Uid(cut) }));

        Check(res.PiecesCreated == 2 && m.Lines.Count == lines, "existing corners: 2 pieces, no line split needed");
        foreach (int uid in res.Created)
        {
            List<int> loaded = LoadedIds(m.LoadPanels.get_IndexOfUID(uid));
            Check(loaded.Count == 2 && loaded.All(IsLongEdge), "existing corners: piece loads its two long-edge parts");
        }
    }

    private static void SplitWallPanel()
    {
        // Vertical panel in the XZ plane, loaded on its bottom edge.
        NextSlot();
        int[] n = { Node3(0, 0, 0), Node3(6, 0, 0), Node3(6, 0, 4), Node3(0, 0, 4) };
        int[] e = { Line(n[0], n[1]), Line(n[1], n[2]), Line(n[2], n[3]), Line(n[3], n[0]) };
        int panelUid = Panel(e, new[] { e[0] });
        int cut = Line(Node3(3, 0, 0), Node3(3, 0, 4));

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { panelUid }, new[] { Uid(cut) }));

        Check(res.PiecesCreated == 2, "wall panel: 2 pieces");
        foreach (int uid in res.Created)
        {
            List<int> loaded = LoadedIds(m.LoadPanels.get_IndexOfUID(uid));
            Check(loaded.Count == 1 && IsAtZ(loaded[0], 0), "wall panel: piece loads its half of the bottom edge");
        }
    }

    private static void SplitTShape()
    {
        // A cut made of two collinear lines (as AxisVM leaves it when a T is drawn: the first line is split
        // where the second one meets it) is not one edge-to-edge line. It is refused and the panel is kept.
        Rect r = NewRect(6, 4);
        int mid = Node(2, 2);
        int c1 = Line(Node(2, 0), Node(2, 4));
        int c2 = Line(mid, Node(6, 2));
        int p1, p2;
        m.Lines.Item[c1].SplitByNode(mid, out p1, out p2);

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(p1), Uid(p2), Uid(c2) }));

        Check(res.Converted == 0 && m.LoadPanels.get_IndexOfUID(r.PanelUid) > 0 && res.Notes.Count == 1 && res.Notes[0].Contains("does not lie on the panel contour"),
            "T cut from two collinear lines: refused with a reason, panel kept");
    }

    private static void SplitCrossingLinesReportsUnused()
    {
        Rect r = NewRect(6, 4);
        int d1 = Line(r.Nodes[0], r.Nodes[2]);
        int d2 = Line(r.Nodes[1], r.Nodes[3]);

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(d1), Uid(d2) }));

        Check(res.PiecesCreated == 2 && res.SplittersUsed == 1, "crossing lines: first cut made, second skipped");
        Check(res.Notes.Any(s => s.StartsWith("1 selected line(s) did not cut")), "crossing lines: skipped line reported");
    }

    private static void SplitSeveralPanelsAtOnce()
    {
        Rect a = NewRect(6, 4);
        int ca = Line(Node(3, 0), Node(3, 4));
        Rect b = NewRect(6, 4);
        int cb = Line(Node(2, 0), Node(2, 4));
        List<int> edgeUids = new List<int> { Uid(ca), Uid(cb) };
        // A box selection also picks up the panels' own edges; those must not count as unused lines.
        foreach (int id in ContourIds(m.LoadPanels.get_IndexOfUID(a.PanelUid))) edgeUids.Add(Uid(id));

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { a.PanelUid, b.PanelUid }, edgeUids));

        Check(res.Converted == 2 && res.PiecesCreated == 4 && res.LoadsCopied == 4, "two panels at once: 4 pieces, each with its load");
        Check(res.Notes.Count == 0, "two panels at once: selected contour edges are not reported as unused");
    }

    private static void UserDefinedPanelIsSkipped()
    {
        int uid = FirstUserDefinedPanelUid();
        Check(uid > 0, "user-defined polygon panel is present in the scratch model");
        if (uid <= 0) return;

        string name;
        int mat = LoadPanelOps.FindDefaultMaterial(m, out name);
        LoadPanelOpResult toDomain = Run(() => LoadPanelOps.PanelsToDomains(m, new[] { uid }, mat, true));
        Check(toDomain.Converted == 0 && toDomain.Notes.Count == 1 && m.LoadPanels.get_IndexOfUID(uid) > 0,
            "user-defined panel -> domain: skipped with a reason, panel kept");

        int cut = Line(Node(-200, 0), Node(-199, 0));
        LoadPanelOpResult split = Run(() => LoadPanelOps.SplitPanels(m, new[] { uid }, new[] { Uid(cut) }));
        Check(split.Converted == 0 && split.Notes.Count == 1 && m.LoadPanels.get_IndexOfUID(uid) > 0,
            "user-defined panel split: skipped with a reason, panel kept");
    }

    private static void SeveralPanelsToDomains()
    {
        Rect a = NewRect(6, 4), b = NewRect(3, 2);
        string name;
        int mat = LoadPanelOps.FindDefaultMaterial(m, out name);
        int domains = m.Domains.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.PanelsToDomains(m, new[] { a.PanelUid, b.PanelUid }, mat, true));

        Check(res.Converted == 2 && res.Deleted == 2 && res.LoadsCopied == 2 && m.Domains.Count == domains + 2, "two panels -> domains: both converted, loads moved");
        List<double> areas = res.Created.Select(d => m.Domains.Item[d].Area).OrderBy(x => x).ToList();
        Check(Math.Abs(areas[0] - 6) < 1e-6 && Math.Abs(areas[1] - 24) < 1e-6, "two panels -> domains: areas 6 and 24 m2");
    }

    private static void SeveralDomainsToPanelsDelete()
    {
        int d1 = NewDomain(6, 4), d2 = NewDomain(3, 2), d3 = NewDomain(5, 5);
        int domains = m.Domains.Count;

        // Converting d1 and d3 only: deleting d1 first would renumber d3, so the top-down order matters.
        LoadPanelOpResult res = Run(() => LoadPanelOps.DomainsToPanels(m, new[] { d1, d3 }, true));

        Check(res.Converted == 2 && res.Deleted == 2 && m.Domains.Count == domains - 2, "two domains -> panels (delete): both domains removed");
        Check(res.SurvivingPanels == 2, "two domains -> panels (delete): both panels survive");
        Check(d2 > 0 && Math.Abs(m.Domains.Item[m.Domains.Count].Area - 6) < 1e-6, "two domains -> panels (delete): the unselected 3x2 domain is untouched");
    }

    // ------------------------------------------------------------------
    // Merge
    // ------------------------------------------------------------------

    /// <summary>Splits a fresh 6 x 4 panel by vertical lines at the given x and returns the pieces' UIDs.</summary>
    private static List<int> SplitRect(params double[] xs)
    {
        Rect r = NewRect(6, 4);
        List<int> cuts = xs.Select(x => Uid(Line(Node(x, 0), Node(x, 4)))).ToList();
        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, cuts));
        if (res.PiecesCreated != xs.Length + 1) throw new InvalidOperationException("setup split failed");
        return res.Created;
    }

    /// <summary>
    /// Values of the first panel load on a panel, read independently of LoadPanelOps from the GetLoad bytes:
    /// a constant load stores its value in the X/Y/Z slot of its component, a linear one stores P1..P3.
    /// </summary>
    private static double[] PanelLoadValues(int panelIndex)
    {
        for (int i = 1; i <= m.Loads.Count; i++)
        {
            if ((int)m.Loads.get_LoadType(i) != -1 || m.Loads.get_ElementId(i) != panelIndex) continue;
            Array data;
            m.Loads.GetLoad(i, out data);
            byte[] b = data.Cast<object>().Select(Convert.ToByte).ToArray();
            int dist = BitConverter.ToInt32(b, 12), comp = BitConverter.ToInt32(b, 16);
            if (dist == 0) { double p = BitConverter.ToDouble(b, 24 + 8 * comp); return new[] { p, p, p }; }
            return new[] { BitConverter.ToDouble(b, 24), BitConverter.ToDouble(b, 32), BitConverter.ToDouble(b, 40) };
        }
        return null;
    }

    private static void SplitKeepsLinearLoad()
    {
        Rect r = NewRect(6, 4);
        int idx = m.LoadPanels.get_IndexOfUID(r.PanelUid);
        // Replace the constant load by a linear one: -2 at (0,0), -4 at (6,0), -6 at (0,4).
        for (int i = m.Loads.Count; i >= 1; i--)
        {
            if ((int)m.Loads.get_LoadType(i) == -1 && m.Loads.get_ElementId(i) == idx) m.Loads.Delete(i);
        }
        RLoadPanelLinear lin = new RLoadPanelLinear();
        lin.LoadCaseId = 1; lin.LoadPanelId = idx; lin.Component = EAxis.aZ;
        lin.LoadDistributionType = ELoadDistributionType.ldtLinear;
        lin.P1 = -2; lin.P2 = -4; lin.P3 = -6;
        lin.x1 = x0; lin.x2 = x0 + 6; lin.x3 = x0; lin.y3 = 4;
        if (m.Loads.AddLoadPanelLinear(ref lin) <= 0) throw new InvalidOperationException("linear test load rejected");
        int cut = Line(Node(3, 0), Node(3, 4));

        LoadPanelOpResult res = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        Check(res.LoadsCopied == 2, "linear load: copied to both pieces");
        foreach (int uid in res.Created) CheckLinearField(m.LoadPanels.get_IndexOfUID(uid), "linear load: piece");

        // ...and back: the two pieces carry the same field, so they merge into one panel that still has it.
        LoadPanelOpResult merged = Run(() => LoadPanelOps.MergePanels(m, res.Created));
        Check(merged.Created.Count == 1 && merged.LoadsCopied == 1, "linear load: pieces merge back with one load");
        if (merged.Created.Count == 1) CheckLinearField(m.LoadPanels.get_IndexOfUID(merged.Created[0]), "linear load: merged panel");
    }

    /// <summary>The panel's load must be the field -2 - (x - x0)/3 - y: -2 at (0,0), -4 at (6,0), -6 at (0,4).</summary>
    private static void CheckLinearField(int panelIndex, string label)
    {
        for (int i = 1; i <= m.Loads.Count; i++)
        {
            if ((int)m.Loads.get_LoadType(i) != -1 || m.Loads.get_ElementId(i) != panelIndex) continue;
            Array data;
            m.Loads.GetLoad(i, out data);
            byte[] b = data.Cast<object>().Select(Convert.ToByte).ToArray();
            bool ok = BitConverter.ToInt32(b, 12) == 1;
            string got = "";
            for (int k = 0; k < 3; k++)
            {
                double p = BitConverter.ToDouble(b, 24 + 8 * k), x = BitConverter.ToDouble(b, 48 + 8 * k), y = BitConverter.ToDouble(b, 72 + 8 * k);
                double expected = -2 - (x - x0) / 3 - y;
                ok &= Math.Abs(p - expected) < 1e-9;
                got += string.Format(" ({0:0.###},{1:0.###})={2:0.###}", x - x0, y, p);
            }
            Check(ok, label + " " + panelIndex + " carries the same linear field:" + got);
            return;
        }
        Check(false, label + " " + panelIndex + " has no load");
    }

    private static int PanelLoadCount(int panelIndex)
    {
        int n = 0;
        for (int i = 1; i <= m.Loads.Count; i++)
        {
            if ((int)m.Loads.get_LoadType(i) == -1 && m.Loads.get_ElementId(i) == panelIndex) n++;
        }
        return n;
    }

    private static void MergeRoundTrip()
    {
        List<int> pieces = SplitRect(3);
        int loadsBefore = m.Loads.Count;

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, pieces));

        Check(res.Converted == 2 && res.Created.Count == 1 && res.Deleted == 2 && res.Notes.Count == 0, "merge after split: 2 pieces become 1 panel");
        int idx = m.LoadPanels.get_IndexOfUID(res.Created[0]);
        Check(ContourIds(idx).Count == 6, "merge after split: outline is the 6 original edge parts (cut line inside)");
        List<int> loaded = LoadedIds(idx);
        Check(loaded.Count == 4 && loaded.All(IsLongEdge), "merge after split: all 4 long-edge parts loaded (got " + string.Join(",", loaded) + ")");
        Check(PanelLoadCount(idx) == 1 && m.Loads.Count == loadsBefore - 1, "merge after split: exactly one load on the merged panel");
        double[] v = PanelLoadValues(idx);
        Check(v != null && v.All(x => Math.Abs(x + 5) < 1e-9), "merge after split: merged load is -5 kN/m2 (got " + (v == null ? "none" : string.Join(",", v)) + ")");
        Check(m.LoadPanels.Item[idx].Auto == ELongBoolean.lbFalse, "merge after split: Auto flag kept");
    }

    private static void MergeThreePieces()
    {
        List<int> pieces = SplitRect(2, 4);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, pieces));

        Check(res.Converted == 3 && res.Created.Count == 1 && res.LoadsCopied == 1, "merge three pieces: one panel with one load");
        Check(ContourIds(m.LoadPanels.get_IndexOfUID(res.Created[0])).Count == 8, "merge three pieces: outline has 8 edge parts");
    }

    private static void MergeTwoGroupsAtOnce()
    {
        List<int> a = SplitRect(3), b = SplitRect(3);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, a.Concat(b).ToList()));

        Check(res.Converted == 4 && res.Created.Count == 2 && res.LoadsCopied == 2 && res.Notes.Count == 0, "two separate groups: merged into 2 panels, one load each");
    }

    private static void MergeLShape()
    {
        // A: 6 x 4 with its top edge already split at x = 2. B: 2 x 2 sitting on the left part of that edge.
        NextSlot();
        int a0 = Node(0, 0), a1 = Node(6, 0), a2 = Node(6, 4), a3 = Node(2, 4), a4 = Node(0, 4), b1 = Node(2, 6), b2 = Node(0, 6);
        int shared = Line(a4, a3);
        int pa = Panel(new[] { Line(a0, a1), Line(a1, a2), Line(a2, a3), shared, Line(a4, a0) }, new int[0]);
        int pb = Panel(new[] { shared, Line(a3, b1), Line(b1, b2), Line(b2, a4) }, new int[0]);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, new[] { pa, pb }));

        Check(res.Created.Count == 1 && ContourIds(m.LoadPanels.get_IndexOfUID(res.Created[0])).Count == 7, "L shape: merged into one 7-edge panel");
    }

    private static void MergeRefusesNonAdjacent()
    {
        Rect a = NewRect(6, 4), b = NewRect(6, 4);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, new[] { a.PanelUid, b.PanelUid }));

        Check(res.Created.Count == 0 && res.Notes.Count == 2 && res.Notes.All(s => s.Contains("shares no edge")), "not adjacent: nothing merged, both reported");
        Check(m.LoadPanels.get_IndexOfUID(a.PanelUid) > 0 && m.LoadPanels.get_IndexOfUID(b.PanelUid) > 0, "not adjacent: both panels kept");
    }

    private static void MergeRefusesDifferentLoads()
    {
        List<int> pieces = SplitRect(3);
        // Make one piece's load different: add a second load to it.
        int idx = m.LoadPanels.get_IndexOfUID(pieces[0]);
        RLoadPanelLinear extra = new RLoadPanelLinear();
        extra.LoadCaseId = 1; extra.LoadPanelId = idx; extra.Component = EAxis.aZ;
        extra.P1 = extra.P2 = extra.P3 = -2;
        m.Loads.AddLoadPanelLinear(ref extra);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, pieces));

        Check(res.Created.Count == 0 && res.Notes.Count == 1 && res.Notes[0].Contains("different loads"), "different loads: refused with a reason");
        Check(pieces.All(u => m.LoadPanels.get_IndexOfUID(u) > 0), "different loads: panels kept");
    }

    private static void MergeRefusesNonCoplanar()
    {
        // A floor panel and a wall panel sharing the floor's back edge.
        NextSlot();
        int f0 = Node3(0, 0, 0), f1 = Node3(6, 0, 0), f2 = Node3(6, 4, 0), f3 = Node3(0, 4, 0), w2 = Node3(6, 4, 3), w3 = Node3(0, 4, 3);
        int shared = Line(f2, f3);
        int floor = Panel(new[] { Line(f0, f1), Line(f1, f2), shared, Line(f3, f0) }, new int[0]);
        int wall = Panel(new[] { shared, Line(f2, w2), Line(w2, w3), Line(w3, f3) }, new int[0]);

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, new[] { floor, wall }));

        Check(res.Created.Count == 0 && res.Notes.Count == 1 && res.Notes[0].Contains("one plane"), "floor + wall: refused, not coplanar");
    }

    private static void MergeRefusesMixedAuto()
    {
        List<int> pieces = SplitRect(3);
        m.LoadPanels.Item[m.LoadPanels.get_IndexOfUID(pieces[0])].Auto = ELongBoolean.lbTrue;

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, pieces));

        Check(res.Created.Count == 0 && res.Notes.Count == 1 && res.Notes[0].Contains("Auto"), "mixed Auto: refused with a reason");
    }

    private static void MergeReportsLoadedInteriorBeam()
    {
        Rect r = NewRect(6, 4);
        int cut = Line(Node(3, 0), Node(3, 4));
        RPoint3d ecc = new RPoint3d();
        m.Lines.Item[cut].DefineAsBeam(1, 1, 1, ref ecc, ref ecc);
        LoadPanelOpResult split = Run(() => LoadPanelOps.SplitPanels(m, new[] { r.PanelUid }, new[] { Uid(cut) }));

        LoadPanelOpResult res = Run(() => LoadPanelOps.MergePanels(m, split.Created));

        Check(res.Created.Count == 1 && res.Notes.Count == 1 && res.Notes[0].Contains("1 shared edge(s) carried load"), "merge over a loaded beam: merged, lost beam load reported");
    }

    private static int FirstUserDefinedPanelUid()
    {
        for (int i = 1; i <= m.LoadPanels.Count; i++)
        {
            if ((int)m.LoadPanels.Item[i].ContourType == 0) return m.LoadPanels.get_UID(i);
        }
        return 0;
    }

    private static bool IsAtZ(int lineId, double z)
    {
        IAxisVMLine line = m.Lines.Item[lineId];
        RNode a = new RNode(), b = new RNode();
        m.Nodes.GetNode(line.StartNode, ref a);
        m.Nodes.GetNode(line.EndNode, ref b);
        return Math.Abs(a.z - z) < 1e-6 && Math.Abs(b.z - z) < 1e-6;
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------

    private sealed class Rect
    {
        public int[] Nodes;
        public int PanelUid;
    }

    private static void NextSlot() { x0 += 20; }

    /// <summary>A w x h rectangle at a fresh offset, with a load panel loaded on its two long edges and a -5 kN/m2 pz load.</summary>
    private static Rect NewRect(double w, double h)
    {
        NextSlot();
        int[] n = { Node(0, 0), Node(w, 0), Node(w, h), Node(0, h) };
        int[] e = { Line(n[0], n[1]), Line(n[1], n[2]), Line(n[2], n[3]), Line(n[3], n[0]) };
        int uid = Panel(e, new[] { e[0], e[2] });

        RLoadPanelLinear load = new RLoadPanelLinear();
        load.LoadCaseId = 1;
        load.LoadPanelId = m.LoadPanels.get_IndexOfUID(uid);
        load.Component = EAxis.aZ;
        load.P1 = load.P2 = load.P3 = -5;
        load.x1 = x0; load.y1 = 0; load.z1 = 0;
        load.x2 = x0 + w; load.y2 = 0; load.z2 = 0;
        load.x3 = x0; load.y3 = h; load.z3 = 0;
        if (m.Loads.AddLoadPanelLinear(ref load) <= 0) throw new InvalidOperationException("test load rejected");

        return new Rect { Nodes = n, PanelUid = uid };
    }

    private static int NewDomain(double w, double h)
    {
        NextSlot();
        int[] n = { Node(0, 0), Node(w, 0), Node(w, h), Node(0, h) };
        int[] e = { Line(n[0], n[1]), Line(n[1], n[2]), Line(n[2], n[3]), Line(n[3], n[0]) };

        string name;
        RSurfaceAttr attr = new RSurfaceAttr();
        attr.Thickness = 0.2;
        attr.SurfaceType = ESurfaceType.stShell;
        attr.MaterialId = LoadPanelOps.FindDefaultMaterial(m, out name);
        int id = m.Domains.Add(Ids(e), ref attr);
        if (id <= 0) throw new InvalidOperationException("test domain rejected: " + id);
        return id;
    }

    private static int Panel(IList<int> edges, IList<int> loaded)
    {
        Array ids = Ids(edges);
        int idx = m.LoadPanels.AddFromLineIDs(ref ids);
        if (idx <= 0) throw new InvalidOperationException("test panel rejected: " + idx);

        IAxisVMLoadPanel p = m.LoadPanels.Item[idx];
        p.Auto = ELongBoolean.lbFalse;
        if (loaded.Count > 0)
        {
            Array l = Ids(loaded);
            p.SetLines(ref l);
        }
        return m.LoadPanels.get_UID(idx);
    }

    private static int Node(double x, double y)
    {
        int id = m.Nodes.Add(x0 + x, y, 0);
        if (id <= 0) throw new InvalidOperationException("node rejected: " + id);
        return id;
    }

    private static int Node3(double x, double y, double z)
    {
        int id = m.Nodes.Add(x0 + x, y, z);
        if (id <= 0) throw new InvalidOperationException("node rejected: " + id);
        return id;
    }

    private static int Line(int a, int b)
    {
        RLineGeomData g = new RLineGeomData();
        int id = m.Lines.Add(a, b, ELineGeomType.lgtStraightLine, ref g);
        if (id <= 0) throw new InvalidOperationException("line rejected: " + id);
        return id;
    }

    private static int Uid(int lineId) { return m.Lines.get_UID(lineId); }

    /// <summary>A long edge of the test rectangles runs along x, at y = 0 or y = h.</summary>
    private static bool IsLongEdge(int lineId)
    {
        IAxisVMLine line = m.Lines.Item[lineId];
        RNode a = new RNode(), b = new RNode();
        m.Nodes.GetNode(line.StartNode, ref a);
        m.Nodes.GetNode(line.EndNode, ref b);
        return Math.Abs(a.y - b.y) < 1e-6 && Math.Abs(a.x - b.x) > 1e-6;
    }

    private static List<int> LoadedIds(int panelIndex)
    {
        Array arr = null;
        try { m.LoadPanels.Item[panelIndex].GetLines(out arr); } catch { }
        return ToList(arr);
    }

    private static List<int> ContourIds(int panelIndex)
    {
        Array arr = null;
        m.LoadPanels.Item[panelIndex].GetContourLineIDs(ref arr);
        return ToList(arr);
    }

    private static List<int> ToList(Array arr)
    {
        List<int> ids = new List<int>();
        if (arr == null) return ids;
        foreach (object o in arr) { int id = Convert.ToInt32(o); if (id > 0) ids.Add(id); }
        return ids;
    }

    private static Array Ids(IList<int> values)
    {
        Array a = Array.CreateInstance(typeof(int), new[] { values.Count }, new[] { 1 });
        for (int i = 0; i < values.Count; i++) a.SetValue(values[i], i + 1);
        return a;
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ctx);

    /// <summary>
    /// AxisVM is not always registered under its ProgID, so walk the Running Object Table and take the first
    /// entry that answers as an AxisVM application, directly or through the NextraAxisVM host.
    /// </summary>
    private static IAxisVMApplication FindAxisVM()
    {
        // LP_PID pins the run to one AxisVM, so a model someone is working in is never touched.
        string pid = Environment.GetEnvironmentVariable("LP_PID");
        if (pid == null) { Console.WriteLine("set LP_PID to the process id of a scratch AxisVM"); return null; }

        System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot;
        System.Runtime.InteropServices.ComTypes.IEnumMoniker monikers;
        System.Runtime.InteropServices.ComTypes.IBindCtx ctx;
        if (GetRunningObjectTable(0, out rot) != 0 || CreateBindCtx(0, out ctx) != 0) return null;
        rot.EnumRunning(out monikers);

        System.Runtime.InteropServices.ComTypes.IMoniker[] one = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            string name;
            try { one[0].GetDisplayName(ctx, null, out name); } catch { continue; }
            if (!name.EndsWith("NextraAxisVM.Host." + pid, StringComparison.OrdinalIgnoreCase)) continue;

            object o;
            try { rot.GetObject(one[0], out o); } catch { continue; }

            // The NextraAxisVM add-on publishes a host object whose GetApplication() hands out the application.
            if (name.IndexOf("NextraAxisVM.Host", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { o = o.GetType().InvokeMember("GetApplication", System.Reflection.BindingFlags.InvokeMethod, null, o, null); }
                catch { continue; }
            }

            IAxisVMApplication app = o as IAxisVMApplication;
            if (app != null) { Console.WriteLine("attached to " + name); return app; }
        }
        return null;
    }

    private static LoadPanelOpResult Run(Func<LoadPanelOpResult> op)
    {
        LoadPanelOpResult r;
        m.BeginUpdate();
        try { r = op(); }
        finally { m.EndUpdate(); }

        Console.WriteLine(string.Format("      converted {0}, pieces {1}, cuts {2}, deleted {3}, loads copied {4}{5}",
            r.Converted, r.PiecesCreated, r.SplittersUsed, r.Deleted, r.LoadsCopied,
            r.Notes.Count == 0 ? "" : " | " + string.Join(" | ", r.Notes)));
        foreach (int uid in r.Created)
        {
            int idx = m.LoadPanels.get_IndexOfUID(uid);
            if (idx > 0) Console.WriteLine(string.Format("      panel {0}: contour [{1}] loaded [{2}]", idx, string.Join(",", ContourIds(idx)), string.Join(",", LoadedIds(idx))));
        }
        return r;
    }

    /// <summary>Everything the checks made sits above the counts taken at the start; remove it top-down.</summary>
    private static void Cleanup(int panels0, int domains0, int lines0, int nodes0)
    {
        m.BeginUpdate();
        try
        {
            for (int i = m.LoadPanels.Count; i > panels0; i--) m.LoadPanels.Delete(i);
            for (int i = m.Domains.Count; i > domains0; i--) m.Domains.Delete(i);
            for (int i = m.Lines.Count; i > lines0; i--) m.Lines.Delete(i);
            for (int i = m.Nodes.Count; i > nodes0; i--) m.Nodes.Delete(i);
        }
        finally { m.EndUpdate(); }
        try { m.Refresh(); } catch { }
    }
}
