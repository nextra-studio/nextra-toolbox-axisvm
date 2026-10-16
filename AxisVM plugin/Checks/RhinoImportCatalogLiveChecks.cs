// AxisVM side of the AxLoadLib checks. Run through scripts/Check-AxLoadLib.ps1, after the Rhino checks
// wrote AxLoadLib-export.json (one beam per picked catalog section, one per catalog material).
//
// Starts its own hidden AxisVM through COM with a new, empty model (never the user's window), imports the
// JSON with the real AxisModelBuilder, and checks every beam:
//   - its cross-section has exactly the catalog name and came from the table that was picked (aX, Iy, Iz
//     and Iyz equal to the profile added straight from that table file - the Iyz=0 angle tables share
//     names with the ordinary ones, so Iyz tells them apart);
//   - its material has exactly the catalog name and type;
//   - nothing fell back to a default, and no section was built from its dimensions.
// AxisVM is closed again at the end.
//
// Args: <export json> <AxisVM folder>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AxisVM;
using AxisVM_plugin.Core;

internal static class RhinoImportCatalogLiveChecks
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0] == "export") return ExportCheck(args[1]);
        if (args[0] == "full") return FullCheck(args[1], args.Length > 2 ? args[2] : "");

        string jsonPath = args[0];
        string axisDir = args.Length > 1 ? args[1] : @"C:\AXISVM X8";

        var notes = new List<string>();
        RhinoModel rhino = RhinoModel.FromJson(JsonValue.Parse(File.ReadAllText(jsonPath)), notes);

        // Optional third arg narrows the run: "sec", "mat", or "sec:FROM:TO" (beam index range, for bisecting).
        if (args.Length > 2)
        {
            string[] f = args[2].Split(':');
            int from = f.Length > 1 ? int.Parse(f[1]) : 0, to = f.Length > 2 ? int.Parse(f[2]) : int.MaxValue;
            rhino.Lines.RemoveAll(l =>
            {
                if (!l.Guid.StartsWith(f[0] + "-")) return true;
                int k = int.Parse(l.Guid.Substring(f[0].Length + 1));
                return k < from || k >= to;
            });
            Console.WriteLine("      subset " + args[2] + ": " + rhino.Lines.Count + " beams");
        }
        Check(rhino.Lines.Count > 0 && rhino.Library.Sections.Count > 0, "json: export read (" + rhino.Lines.Count + " beams, " + rhino.Library.Sections.Count + " library sections)");
        Check(rhino.Library.Sections.Count(s => s.CatalogFile.Length > 0 && s.CatalogShape > 0) >= rhino.Lines.Count(l => l.Guid.StartsWith("sec-")),
            "json: catalog table, file and shape reach the AxisVM reader");
        Check(CatalogResolver.CatalogFilePath("HEA_PL") != null && CatalogResolver.CatalogFilePath("HEA_PL").EndsWith("HEA_PL.SEC", StringComparison.OrdinalIgnoreCase)
              && CatalogResolver.CatalogFilePath("NO_SUCH_TABLE") == null, "resolver: catalog file found from the registered AxisVM (" + CatalogResolver.CatalogFilePath("HEA_PL") + ")");

        IAxisVMApplication app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            IAxisVMModel model = AxisVmDialogGuard.FreshModel(app);
            Check(model.Lines.Count == 0 && model.CrossSections.Count == 0, "axisvm: fresh empty model in a separate AxisVM");

            var opt = new ImportOptions { Units = new AxUnits { LengthToM = 1.0, LengthName = "m" }, MergeToleranceM = 0.001, AutoIntersect = false, SourcePath = jsonPath };
            var builder = new AxisModelBuilder(app, model, rhino, opt, notes);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            model.BeginUpdate();
            try { builder.Run(); }
            finally { model.EndUpdate(); }
            Console.WriteLine("      imported in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s, " + model.Lines.Count + " lines");

            Verify(model, rhino, notes, axisDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  exception: " + ex);
            failures++;
        }
        finally
        {
            try { app.AskCloseOnLastReleased = ELongBoolean.lbFalse; app.Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
        }

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ==================================================================
    // Whole model exported by the real Rhino (every element type) into a fresh AxisVM
    // ==================================================================

    private static int FullCheck(string jsonPath, string savePath)
    {
        var notes = new List<string>();
        RhinoModel rhino = RhinoModel.FromJson(JsonValue.Parse(File.ReadAllText(jsonPath)), notes);
        Console.WriteLine("      read " + rhino.Points.Count + " points, " + rhino.Lines.Count + " lines, " + rhino.Domains.Count + " domains from " + jsonPath);

        IAxisVMApplication app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            IAxisVMModel model = AxisVmDialogGuard.FreshModel(app);

            var opt = new ImportOptions { Units = new AxUnits { LengthToM = 0.001, LengthName = "mm" }, MergeToleranceM = 0.001, AutoIntersect = true, SourcePath = jsonPath };
            var builder = new AxisModelBuilder(app, model, rhino, opt, notes);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            model.BeginUpdate();
            try { builder.Run(); } finally { model.EndUpdate(); }
            Console.WriteLine("      imported in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s");

            ImportStats st = builder.Stats;
            Console.WriteLine(string.Format("      stats: nodes {0} (shared {1}), lines {2} (split {3}, merged {4}), domains {5}, holes {6}, panels {7}, supports {8}, line supports {9}, springs {10}, links {11}, rigid bodies {12}, loads {13}, attribute errors {14}",
                st.NodesCreated, st.NodesShared, st.LinesCreated, st.LinesSplit, st.LinesMerged, st.DomainsCreated, st.HolesCreated, st.PanelsCreated,
                st.SupportsCreated, st.LineSupportsCreated, st.SpringsCreated, st.LinksCreated, st.RigidBodiesCreated, st.LoadsCreated, st.AttributeErrors));
            foreach (string n in notes) Console.WriteLine("      note: " + n);

            Check(st.AttributeErrors == 0, "import: no attribute errors");
            Check(st.DomainsCreated == 4 && model.Domains.Count == 4, "domains: slab, wall and the two plates beside the edge hinge created (" + model.Domains.Count + ")");
            Check(st.HingesCreated >= 1, "edge hinge created along the edge shared by the two plates (" + st.HingesCreated + ")");
            Check(st.HolesCreated == 1, "domains: the wall opening became a hole (" + st.HolesCreated + ")");
            Check(model.LoadPanels.Count == 2 && st.PanelsCreated == 2, "load panels: two-way and one-way created (" + model.LoadPanels.Count + ")");
            Check(model.NodalSupports.Count >= 4, "supports: four nodal supports (" + model.NodalSupports.Count + ")");
            Check(st.SpringsCreated == 1, "spring element created (" + st.SpringsCreated + ")");
            Check(st.LinksCreated >= 1, "node-node link created (" + st.LinksCreated + ")");
            Check(st.RigidBodiesCreated >= 1, "rigid link created as a rigid body (" + st.RigidBodiesCreated + ")");
            var lcNames = Enumerable.Range(1, model.LoadCases.Count).Select(i => model.LoadCases.get_Name(i)).ToList();
            Console.WriteLine("      load cases in the model: " + string.Join(" | ", lcNames.ToArray()));
            int firstLib = lcNames.IndexOf("Dead");
            Check(firstLib >= 0 && lcNames.Skip(firstLib).Take(3).SequenceEqual(new[] { "Dead", "Live", "Wind X" }),
                "load cases: Dead, Live, Wind X created in library order");
            // 1 nodal + beam line + beam point + slab area + slab point + 2 panel area loads
            Check(st.LoadsCreated == 7 && model.Loads.Count >= 7, "loads: nodal, member point/line, surface point/area and both panel loads created (stats " + st.LoadsCreated + ", model " + model.Loads.Count + ")");

            // ---- members: type, material and section of every straight Rhino member ----
            IAxisVMLines lines = model.Lines;
            IAxisVMNodes nodes = model.Nodes;
            int n2 = lines.Count;
            var segs = new List<Seg>();
            for (int i = 1; i <= n2; i++)
            {
                IAxisVMLine l = lines.Item[i];
                RPoint3d p = new RPoint3d(), q = new RPoint3d();
                nodes.GetNodeCoord(Math.Abs(l.StartNode), ref p);
                nodes.GetNodeCoord(Math.Abs(l.EndNode), ref q);
                Array got; lines.BulkGetAttr(new[] { i }, out got);
                segs.Add(new Seg { Id = i, A = new[] { p.x, p.y, p.z }, B = new[] { q.x, q.y, q.z }, Attr = (RLineAttr)got.GetValue(got.GetLowerBound(0)) });
            }

            int checkedMembers = 0, bad = 0;
            foreach (RhinoLine rl in rhino.Lines)
            {
                string type = rl.Attrs.ContainsKey("Ax_Type") ? rl.Attrs["Ax_Type"] : "";
                if (type != "Beam" && type != "Truss") continue;
                if (rl.Geom != "line") continue;
                if (rl.Attrs["Ax_Section"] == "XYZ999") continue;   // checked below: must fall back, with a note
                double[] a = { rl.Start.X / 1000, rl.Start.Y / 1000, rl.Start.Z / 1000 }, b = { rl.End.X / 1000, rl.End.Y / 1000, rl.End.Z / 1000 };
                List<Seg> mine = segs.Where(s => OnSegment(s.A, a, b) && OnSegment(s.B, a, b)).ToList();
                string wantMat = rl.Attrs["Ax_Material"], wantSec = rl.Attrs["Ax_Section"];
                RhinoLibSection lib = rhino.Library.FindSection(wantSec);
                string exactSec = lib != null && lib.CatalogName.Length > 0 ? lib.CatalogName : (lib != null ? lib.Name : wantSec);
                bool picked = lib != null && lib.CatalogFile.Length > 0;
                checkedMembers++;
                if (mine.Count == 0) { bad++; Console.WriteLine("      no model line for " + type + " " + wantSec + " " + Fmt3(a) + "->" + Fmt3(b)); continue; }
                foreach (Seg s in mine)
                {
                    string mat = model.Materials.Item[s.Attr.MaterialIndex].Name;
                    IAxisVMCrossSection cs = model.CrossSections.Item[s.Attr.StartCrossSectionIndex];
                    bool typeOk = type == "Truss" ? s.Attr.LineType == ELineType.ltTruss : (s.Attr.LineType == ELineType.ltBeam || s.Attr.LineType == ELineType.ltRib);
                    bool matOk = CatalogResolver.Compact(mat) == CatalogResolver.Compact(wantMat);
                    bool secOk = picked ? cs.Name == exactSec : CatalogResolver.Compact(cs.Name) == CatalogResolver.Compact(wantSec);
                    if (!(typeOk && matOk && secOk))
                    {
                        bad++;
                        Console.WriteLine(string.Format("      {0} {1}: type {2}, material '{3}', section '{4}' - wanted {5} '{6}' '{7}'", s.Id, Fmt3(s.A), s.Attr.LineType, mat, cs.Name, type, wantMat, exactSec));
                    }
                }
            }
            Check(checkedMembers == 24 && bad == 0, "members: all " + checkedMembers + " straight Beam/Truss members have the right type, material and exact section (" + bad + " wrong)");
            // sloppy names: resolved to AxisVM's own spelling (compared by exact name here)
            var wantExact = new Dictionary<string, string[]>
            {
                { "ipe300", new[] { "S 355", "IPE 300" } }, { "HEA300", new[] { "S 355", "HEA 300" } }, { "400x600", new[] { "C30/37", "400x600" } },
                { "HEA 300|GL24h", new[] { "GL 24h", "HEA 300" } }, { "IPE-300", new[] { "C30/37", "IPE 300" } }, { "  IPE 300  ", new[] { "S 355", "IPE 300" } }
            };
            foreach (RhinoLine rl in rhino.Lines.Where(x => x.Start.Y == -3000 && x.Attrs.ContainsKey("Ax_Section")))
            {
                string sec0 = rl.Attrs["Ax_Section"], mat0 = rl.Attrs["Ax_Material"];
                if (sec0 == "XYZ999") continue;
                string key = sec0 == "HEA 300" && mat0 == "GL24h" ? "HEA 300|GL24h" : sec0;
                double[] a = { rl.Start.X / 1000, rl.Start.Y / 1000, rl.Start.Z / 1000 }, b = { rl.End.X / 1000, rl.End.Y / 1000, rl.End.Z / 1000 };
                Seg s0 = segs.First(s => OnSegment(s.A, a, b) && OnSegment(s.B, a, b));
                string gotMat = model.Materials.Item[s0.Attr.MaterialIndex].Name, gotSec = model.CrossSections.Item[s0.Attr.StartCrossSectionIndex].Name;
                Check(gotMat == wantExact[key][0] && gotSec == wantExact[key][1],
                    string.Format("names: material '{0}' + section '{1}' resolve to AxisVM's '{2}' + '{3}' (got '{4}' + '{5}')", mat0, sec0, wantExact[key][0], wantExact[key][1], gotMat, gotSec));
            }
            RhinoLine junk = rhino.Lines.First(x => x.Attrs.ContainsKey("Ax_Section") && x.Attrs["Ax_Section"] == "XYZ999");
            {
                double[] a = { junk.Start.X / 1000, junk.Start.Y / 1000, junk.Start.Z / 1000 }, b = { junk.End.X / 1000, junk.End.Y / 1000, junk.End.Z / 1000 };
                Seg sj = segs.First(s => OnSegment(s.A, a, b) && OnSegment(s.B, a, b));
                Check(sj.Attr.MaterialIndex > 0 && sj.Attr.StartCrossSectionIndex > 0 && notes.Any(x => x.Contains("XYZ999")) && notes.Any(x => x.Contains("NOPE-MAT")),
                    "names: an unknown material and section fall back to a default and the report says which ('" + model.CrossSections.Item[sj.Attr.StartCrossSectionIndex].Name + "')");
            }
            // ---- arcs: where is the middle of the arc member in the model? ----
            foreach (Seg sg in segs.Where(x => lines.Item[x.Id].GeomType == ELineGeomType.lgtCircleArc))
            {
                RLineGeomData g = new RLineGeomData(); lines.Item[sg.Id].GetGeomData(ref g);
                // the arc's own middle: start turned by half the sweep about the normal (2-D here, normal +-z)
                double nz = g.CircleArc.NormalVector.z >= 0 ? 1 : -1, half = g.CircleArc.Alfa / 2 * nz;
                double vx = sg.A[0] - g.CircleArc.Center.x, vy = sg.A[1] - g.CircleArc.Center.y;
                RPoint3d mid = new RPoint3d { x = g.CircleArc.Center.x + vx * Math.Cos(half) - vy * Math.Sin(half), y = g.CircleArc.Center.y + vx * Math.Sin(half) + vy * Math.Cos(half), z = sg.A[2] };
                Console.WriteLine(string.Format("      arc line {0}: {1}->{2}, midpoint ({3:0.###},{4:0.###},{5:0.###}) m, centre ({6:0.###},{7:0.###},{8:0.###}), normal ({9:0.##},{10:0.##},{11:0.##}), sweep {12:0.####}",
                    sg.Id, Fmt3(sg.A), Fmt3(sg.B), mid.x, mid.y, mid.z, g.CircleArc.Center.x, g.CircleArc.Center.y, g.CircleArc.Center.z, g.CircleArc.NormalVector.x, g.CircleArc.NormalVector.y, g.CircleArc.NormalVector.z, g.CircleArc.Alfa));
                bool onRhinoArc = Math.Abs(mid.x - 10.5) < 1e-3 && Math.Abs(mid.y - 1.0) < 1e-3 && Math.Abs(mid.z - 3.5) < 1e-3;
                Check(onRhinoArc, "arc: the AxisVM arc passes through the Rhino arc's mid point (10.5, 1, 3.5)");
            }
            Check(!notes.Any(x => x.Contains("does not pass through")), "arc: no false 'does not pass through' warning");
            Check(model.LineSupports.Count >= 1 && st.LineSupportsCreated >= 1, "supports: the Simple line on the wall base now carries a line support (" + model.LineSupports.Count + ")");

            var roles = segs.GroupBy(s => s.Attr.LineType).Select(g => g.Key + " " + g.Count()).ToArray();
            Console.WriteLine("      line roles: " + string.Join(", ", roles));

            // picked-section proof: the Iyz=0 angle really is the Iyz=0 table's profile
            Seg truss = segs.First(s => s.Attr.LineType == ELineType.ltTruss);
            IAxisVMCrossSection ts = model.CrossSections.Item[truss.Attr.StartCrossSectionIndex];
            Check(ts.Name == "L 250x28" && Math.Abs(ts.Iyz) < 1e-12, "sections: truss uses 'L 250x28' from the Iyz=0 table (Iyz " + ts.Iyz + ")");
            var names = Enumerable.Range(1, model.CrossSections.Count).Select(i => model.CrossSections.Item[i].Name).ToList();
            Check(names.Contains(" 50X30X  4"), "sections: the padded catalog name ' 50X30X  4' exists exactly as AxisVM spells it");
            Check(names.Contains("HEA 300") && names.Contains("IPE 300") && names.Contains("CHS 168,3x6,3") && names.Contains("P219x10") && names.Contains("D400") && names.Contains("RH200x100x8") && names.Contains("300x500"),
                "sections: picked, typed-catalog and parametric profiles all present (" + string.Join(" | ", names.ToArray()) + ")");
            Check(names.Count(x => CatalogResolver.Compact(x) == "IPE300") == 1, "sections: typed 'IPE300' and picked 'IPE 300' share one AxisVM section");
            var mnames = Enumerable.Range(1, model.Materials.Count).Select(i => model.Materials.Item[i].Name).ToList();
            Check(mnames.Contains("S 355") && mnames.Contains("S 235") && mnames.Contains("C30/37"), "materials: S 355, S 235, C30/37 present (" + string.Join(" | ", mnames.ToArray()) + ")");

            if (savePath.Length > 0)
            {
                try
                {
                    bool ok = model.SaveToFile(savePath, ELongBoolean.lbTrue) == ELongBoolean.lbTrue;
                    Console.WriteLine("      model " + (ok ? "saved: " : "NOT saved: ") + savePath);
                }
                catch (Exception e) { Console.WriteLine("      (save failed: " + e.Message + ")"); }
            }
        }
        catch (Exception ex) { Console.WriteLine("FAIL  exception: " + ex); failures++; }
        finally
        {
            try { app.AskCloseOnLastReleased = ELongBoolean.lbFalse; app.Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private class Seg { public int Id; public double[] A, B; public RLineAttr Attr; }

    private static string Fmt3(double[] p) { return "(" + p[0].ToString("0.###") + "," + p[1].ToString("0.###") + "," + p[2].ToString("0.###") + ")"; }

    /// <summary>Point p lies on the segment a-b (within 1 mm).</summary>
    private static bool OnSegment(double[] p, double[] a, double[] b)
    {
        double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, ap = { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
        double len2 = ab[0] * ab[0] + ab[1] * ab[1] + ab[2] * ab[2];
        double t = (ap[0] * ab[0] + ap[1] * ab[1] + ap[2] * ab[2]) / len2;
        if (t < -1e-6 || t > 1 + 1e-6) return false;
        double dx = ap[0] - t * ab[0], dy = ap[1] - t * ab[1], dz = ap[2] - t * ab[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) < 0.001;
    }

    // ==================================================================
    // "Export catalog for Rhino" (CatalogExport) against a fresh AxisVM
    // ==================================================================

    private static int ExportCheck(string folder)
    {
        IAxisVMApplication app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            IAxisVMModel model = AxisVmDialogGuard.FreshModel(app);
            int mats0 = model.Materials.Count, secs0 = model.CrossSections.Count;

            if (Directory.Exists(folder)) foreach (string f in Directory.GetFiles(folder, "*.csv")) File.Delete(f);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            CatalogExport.Result r = CatalogExport.Export(app, folder);
            Console.WriteLine(string.Format("      exported in {0:0.0} s (out of process): {1} materials, {2} sections, {3} tables -> {4}",
                clock.Elapsed.TotalSeconds, r.Materials, r.Sections, r.Tables, r.Folder));

            Check(model.Materials.Count == mats0 && model.CrossSections.Count == secs0, "export: the model is not changed (materials and sections untouched)");
            Check(File.Exists(Path.Combine(folder, CatalogExport.MaterialsFile)) && File.Exists(Path.Combine(folder, CatalogExport.SectionsFile))
                  && Directory.GetFiles(folder, "*.tmp").Length == 0, "export: both CSV files written, no temp file left");
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Check(CatalogExport.DefaultFolder() == Path.Combine(appData, "AxisBridge"), "export: default folder is the user's %APPDATA%\\AxisBridge (" + CatalogExport.DefaultFolder() + ")");

            // Reference: the catalog read straight from AxisVM, name by name.
            IAxisVMCatalog cat = app.Catalog;
            Array all;
            int matCount = cat.GetMaterialNames(ENationalDesignCode.ndcEuroCode, out all);
            var tables = new Dictionary<string, List<string>>();
            AxisVMCrossSectionTables t;
            cat.GetAllTables(out t);
            for (int i = 1; i <= t.Count; i++)
            {
                RCrossSectionTable rec = t.Item[i];
                if (rec.Id <= 0) continue;
                AxisVMCrossSectionTable tab;
                cat.GetTableCrossSections(rec.Id, out tab);
                var names = new List<string>();
                for (int k = 1; k <= tab.Count; k++) names.Add(tab.CrossSectionName[k]);
                tables[t.FileName[i]] = names;
            }

            List<string[]> matRows = ReadCsv(Path.Combine(folder, CatalogExport.MaterialsFile));
            List<string[]> secRows = ReadCsv(Path.Combine(folder, CatalogExport.SectionsFile));
            Check(matRows.Count == matCount && r.Materials == matCount, "export: every Eurocode material written (" + matRows.Count + " = " + matCount + " from GetMaterialNames)");
            Check(matRows.Select(x => x[3]).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(all.Cast<string>().OrderBy(x => x, StringComparer.Ordinal)),
                "export: material names identical to AxisVM's");
            Check(r.Tables == tables.Count, "export: every cross-section table written (" + r.Tables + " = " + tables.Count + ")");
            int wrong = 0;
            foreach (var kv in tables)
            {
                List<string> got = secRows.Where(x => x[3] == kv.Key).Select(x => x[5]).ToList();
                if (!got.SequenceEqual(kv.Value)) { wrong++; if (wrong <= 5) Console.WriteLine("      table " + kv.Key + ": " + got.Count + " vs " + kv.Value.Count); }
            }
            Check(wrong == 0, "export: every table has all its profiles in catalog order, names identical to CrossSectionName[1..Count] (padding kept)");
            Check(secRows.Count == tables.Values.Sum(v => v.Count), "export: profile total " + secRows.Count);
            int[] ids = secRows.Select(x => int.Parse(x[1])).ToArray();
            bool sorted = true;
            for (int i = 1; i < ids.Length; i++) if (ids[i] < ids[i - 1]) sorted = false;
            Check(sorted, "export: sections grouped by shape in AxisVM's order");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  exception: " + ex);
            return 1;
        }
        finally
        {
            try { app.AskCloseOnLastReleased = ELongBoolean.lbFalse; app.Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        }
    }

    /// <summary>Minimal RFC 4180 reader (header row dropped) - independent of the Rhino-side reader.</summary>
    private static List<string[]> ReadCsv(string path)
    {
        string text = File.ReadAllText(path, System.Text.Encoding.UTF8).TrimStart('﻿');
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (q) { if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else if (ch != '\r') cell.Append(ch);
        }
        rows.RemoveAt(0);
        return rows;
    }

    private static void Verify(IAxisVMModel model, RhinoModel rhino, List<string> notes, string axisDir)
    {
        // Model line -> exported beam, by its bottom node (every beam stands at its own x, y).
        var byXY = new Dictionary<string, RhinoLine>();
        foreach (RhinoLine l in rhino.Lines) byXY[Key(l.Start.X, l.Start.Y)] = l;

        IAxisVMLines lines = model.Lines;
        IAxisVMCrossSections cs = model.CrossSections;
        IAxisVMMaterials mats = model.Materials;
        int n = lines.Count;
        Check(n == rhino.Lines.Count, "axisvm: one line per exported beam (" + n + " / " + rhino.Lines.Count + ")");

        var sectionOf = new Dictionary<string, int>();
        var materialOf = new Dictionary<string, int>();
        for (int i = 1; i <= n; i++)
        {
            IAxisVMLine line = lines.Item[i];
            RPoint3d p = new RPoint3d();
            model.Nodes.GetNodeCoord(Math.Abs(line.StartNode), ref p);
            RPoint3d q = new RPoint3d();
            model.Nodes.GetNodeCoord(Math.Abs(line.EndNode), ref q);
            RhinoLine src;
            if (!byXY.TryGetValue(Key(Math.Min(p.z, q.z) == p.z ? p.x : q.x, Math.Min(p.z, q.z) == p.z ? p.y : q.y), out src)) continue;

            Array got;
            lines.BulkGetAttr(new[] { i }, out got);
            RLineAttr a = (RLineAttr)got.GetValue(got.GetLowerBound(0));
            sectionOf[src.Guid] = a.StartCrossSectionIndex;
            materialOf[src.Guid] = a.MaterialIndex;
            if (a.LineType != ELineType.ltBeam) Console.WriteLine("      " + src.Guid + " is " + a.LineType);
        }
        Check(sectionOf.Count == rhino.Lines.Count, "axisvm: every exported beam found in the model");

        // ---- cross-sections -----------------------------------------------------------------------
        var reference = new ReferenceSections(model, axisDir);
        int secBeams = 0, secName = 0, secTable = 0, iyzCases = 0;
        var bad = new List<string>();
        foreach (RhinoLine l in rhino.Lines.Where(x => x.Guid.StartsWith("sec-")))
        {
            secBeams++;
            string want = l.Attrs["Ax_Section"];
            RhinoLibSection lib = rhino.Library.FindSection(want);
            int id;
            if (!sectionOf.TryGetValue(l.Guid, out id) || id <= 0) { bad.Add(want + ": no section"); continue; }
            IAxisVMCrossSection s = cs.Item[id];
            string exact = lib.CatalogName.Length > 0 ? lib.CatalogName : want;
            if (s.Name == exact || s.Name == want) secName++; else bad.Add(want + ": named '" + s.Name + "'");

            double[] got = Props(s), exp = reference.Props(lib.CatalogFile, exact);
            if (exp != null && Same(got, exp)) secTable++;
            else bad.Add(string.Format("{0} [{1}]: aX/Iy/Iz/Iyz {2} vs table {3}", want, lib.CatalogFile, Fmt(got), exp == null ? "?" : Fmt(exp)));
            if (lib.CatalogFile.EndsWith("_IYZ0")) iyzCases++;
        }
        foreach (string b in bad.Take(15)) Console.WriteLine("      " + b);
        Console.WriteLine("      spacing-only twins in the sample: " + string.Join(", ", rhino.Lines.Where(x => x.Guid.StartsWith("sec-")).Select(x => x.Attrs["Ax_Section"])
            .GroupBy(Compact).Where(g => g.Count() > 1).Select(g => string.Join(" / ", g.Select(v => "'" + v + "'").ToArray())).Take(6).ToArray()));
        Check(secName == secBeams, "sections: all " + secBeams + " beams carry the exact catalog name (" + secName + " ok)");
        Check(secTable == secBeams, "sections: all " + secBeams + " come from the table that was picked - aX, Iy, Iz, Iyz match that table (" + secTable + " ok)");
        if (secBeams > 0) Check(iyzCases >= 3,"sections: includes " + iyzCases + " profiles from the Iyz=0 tables whose names also exist in the ordinary tables");

        RhinoLine lIyz = rhino.Lines.FirstOrDefault(x => x.Attrs["Ax_Section"] == "L 250x28");
        if (lIyz != null)
        {
            double[] got = Props(cs.Item[sectionOf[lIyz.Guid]]);
            double[] plain = reference.Props("L_EN", "L 250x28"), iyz0 = reference.Props("L_EN_IYZ0", "L 250x28");
            Check(plain != null && iyz0 != null && !Same(plain, iyz0) && Same(got, iyz0),
                "sections: 'L 250x28' picked from LE Iyz=0 (EN 10056-1) is that one, not the same-named EN 10056-1 angle (Iyz " + got[3] + " vs " + plain[3] + ")");
        }

        RhinoLine sq = rhino.Lines.FirstOrDefault(x => x.Attrs["Ax_Section"] == "78x80");
        if (sq != null)
            Check(cs.Item[sectionOf[sq.Guid]].Name == "78x80" && notes.Any(x => x.Contains("'78x80' was added from the AxisVM catalog table")),
                "sections: catalog '78x80' (concrete table) comes from its table, not parsed as a 78 x 80 mm rectangle");

        Check(!notes.Any(x => x.Contains("used instead")), "sections/materials: nothing fell back to a default" + FirstNote(notes, "used instead"));
        Check(!notes.Any(x => x.Contains("built from its dimensions") || x.Contains("Rectangular cross-section")), "sections: none rebuilt parametrically" + FirstNote(notes, "dimensions"));
        Check(!notes.Any(x => x.Contains("not found, matched by shape")), "sections: every table file was found in " + axisDir + FirstNote(notes, "not found"));

        // ---- materials ------------------------------------------------------------------------------
        int matBeams = 0, matOk = 0;
        var badM = new List<string>();
        foreach (RhinoLine l in rhino.Lines.Where(x => x.Guid.StartsWith("mat-")))
        {
            matBeams++;
            string want = l.Attrs["Ax_Material"];
            int id;
            if (!materialOf.TryGetValue(l.Guid, out id) || id <= 0) { badM.Add(want + ": none"); continue; }
            IAxisVMMaterial m = mats.Item[id];
            if (m.Name == want && m.NationalDesignCode == ENationalDesignCode.ndcEuroCode) matOk++;
            else badM.Add(want + ": '" + m.Name + "' " + m.NationalDesignCode);
        }
        foreach (string b in badM.Take(15)) Console.WriteLine("      " + b);
        if (matBeams > 0) Check(matOk == matBeams,"materials: all " + matBeams + " catalog materials assigned with the exact name and Eurocode (" + matOk + " ok)");
        var used = rhino.Lines.Select(x => x.Attrs["Ax_Material"]).Distinct().ToList();
        Check(mats.Count == used.Count,
            "materials: no duplicates or stray materials in the model (" + mats.Count + ")");
    }

    private static string FirstNote(List<string> notes, string text)
    {
        string n = notes.FirstOrDefault(x => x.Contains(text));
        return n == null ? "" : "  [" + n + "]";
    }

    private static string Compact(string s) { return CatalogResolver.Compact(s); }

    private static string Key(double x, double y) { return Math.Round(x, 3) + "|" + Math.Round(y, 3); }

    private static double[] Props(IAxisVMCrossSection s) { return new[] { s.aX, s.Iy, s.Iz, s.Iyz }; }

    private static bool Same(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9 + 1e-6 * Math.Max(Math.Abs(a[i]), Math.Abs(b[i]))) return false;
        return true;
    }

    private static string Fmt(double[] v) { return string.Join("/", v.Select(x => x.ToString("G4")).ToArray()); }

    /// <summary>Section properties straight from a table file, added to a scratch second model and cached.</summary>
    private class ReferenceSections
    {
        private readonly IAxisVMCrossSections cs;
        private readonly string dir;
        private readonly Dictionary<string, double[]> cache = new Dictionary<string, double[]>();

        public ReferenceSections(IAxisVMModel model, string axisDir)
        {
            // Reference profiles go into the same model after the import has been checked for names; they are
            // looked up only by the id AddFromCatalogFile returns, so they cannot be confused with imported ones.
            cs = model.CrossSections;
            dir = axisDir;
        }

        public double[] Props(string file, string name)
        {
            string key = file + "|" + name;
            double[] p;
            if (cache.TryGetValue(key, out p)) return p;
            int id;
            try { id = cs.AddFromCatalogFile(Path.Combine(dir, file + ".SEC"), name); } catch { id = 0; }
            p = id > 0 ? new[] { cs.Item[id].aX, cs.Item[id].Iy, cs.Item[id].Iz, cs.Item[id].Iyz } : null;
            cache[key] = p;
            return p;
        }
    }
}
