// Live checks for re-importing a Rhino export as an update (Core\AxisModelBuilder.Link.cs). Run through
// scripts/Check-RhinoUpdate.ps1 with any AxExport JSON that has a few beams.
//
// Starts its own hidden AxisVM through COM with a new, empty model (never the user's window), draws one line
// of its own, and then imports the export three times under one file name with the real AxisModelBuilder:
//   1. a plain import                         - everything it created is marked with RhinoLink;
//   2. the same export again as an update     - every count is back to exactly what step 1 left;
//   3. the export with one beam moved up 0.5 m, one curve deleted and a part on a beam, as an update -
//      the moved beam exists only at its new place, the deleted one is gone, the part is rebuilt;
// and finally once more without the update option, which must duplicate (so the check can tell the two apart).
// The line drawn before the imports must survive all of it. AxisVM is closed again at the end.
//
// Mode "link" (third argument) checks the live link round trip instead: load a link file, send with nothing
// changed, then re-section and move a beam, delete one and draw a new one in AxisVM and send (only the changes,
// in metres, the new beam adopted); send again; load the unchanged Rhino model (the adopted beam survives);
// load a Rhino model that holds the adopted beam (exactly one copy is left).
//
// Args: <export json> [mm|m] [link]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AxisVM;
using AxisVM_plugin.Core;

internal static class RhinoUpdateLiveChecks
{
    private static int failures;
    private static IAxisVMApplication app;
    private static IAxisVMModel model;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    /// <summary>
    /// Closes the AxisVM this check started. A hidden AxisVM X8 now and then never returns from Quit; the results
    /// are already printed by then, so after a minute the check exits with its own result instead of hanging.
    /// </summary>
    private static void Quit()
    {
        new System.Threading.Thread(() => { System.Threading.Thread.Sleep(60000); Console.WriteLine("(AxisVM did not close within a minute; it was left to exit on its own.)"); Environment.Exit(failures == 0 ? 0 : 1); }) { IsBackground = true }.Start();
        try { app.AskCloseOnLastReleased = ELongBoolean.lbFalse; app.Quit(); } catch { }
        AxisVmDialogGuard.WaitClosed(50);
    }

    private static int C(Func<int> f) { try { return f(); } catch { return -1; } }

    private static string Counts()
    {
        return string.Format("nodes {0}, lines {1}, domains {2}, load panels {3}, members {4}, nodal supports {5}, line supports {6}, links {7}, rigid bodies {8}, edge hinges {9}, parts {10}, loads {11}, diaphragm lines {12}",
            C(() => model.Nodes.Count), C(() => model.Lines.Count), C(() => model.Domains.Count), C(() => model.LoadPanels.Count),
            C(() => model.Members.Count), C(() => model.NodalSupports.Count), C(() => model.LineSupports.Count),
            C(() => model.LinkElements.Count), C(() => model.RigidBodies.Count), C(() => model.EdgeConnections.Count),
            C(() => model.CustomParts.RootFolder.PartCount), C(() => model.Loads.Count), C(DiaphragmLines));
    }

    private static Array IdArray(List<int> ids)
    {
        Array a = Array.CreateInstance(typeof(int), new[] { ids.Count }, new[] { 1 });
        for (int i = 0; i < ids.Count; i++) a.SetValue(ids[i], i + 1);
        return a;
    }

    private static List<int> LineIdsOf(string guid)
    {
        var ids = new List<int>();
        for (int i = 1; i <= model.Lines.Count; i++) if (LinkState.Guids(model.Lines.Attachments, i).Contains(guid)) ids.Add(i);
        return ids;
    }

    private static HashSet<int> MembersOf(string guid)
    {
        var m = new HashSet<int>();
        foreach (int l in LineIdsOf(guid)) { int x = model.Lines.Item[l].MemberID; if (x > 0) m.Add(x); }
        return m;
    }

    private static double[] P(JsonValue v) { return new[] { v.At(0).AsDouble(0), v.At(1).AsDouble(0), v.At(2).AsDouble(0) }; }

    /// <summary>Two straight curves of one type, outside any diaphragm, meeting end to end in one direction (metres).</summary>
    private static string[] CollinearPair(string linkPath)
    {
        var lines = LinkFile.Read(linkPath)[LinkFile.RhinoSection]["lines"].ArrayValue
            .Where(e => e["start"] != null && e["points"] == null && e["arcMid"] == null && LinkFile.Get(e["attrs"], "Ax_Diaphragm").Length == 0).ToList();
        Func<double[], double[], double[]> sub = (a, b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        Func<double[], double> len = a => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (LinkFile.Get(lines[i]["attrs"], "Ax_Type") != LinkFile.Get(lines[j]["attrs"], "Ax_Type")) continue;
                double[] a0 = P(lines[i]["start"]), a1 = P(lines[i]["end"]), b0 = P(lines[j]["start"]), b1 = P(lines[j]["end"]);
                if (len(sub(a1, b0)) > 1e-4) continue;
                double[] u = sub(a1, a0), v = sub(b1, b0);
                double lu = len(u), lv = len(v);
                double dot = (u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / (lu * lv);
                if (dot > 0.99999 && lu > 0.5 && lv > 0.5) return new[] { LinkFile.Get(lines[i], "guid"), LinkFile.Get(lines[j], "guid") };
            }
        return null;
    }

    /// <summary>A curve outside any diaphragm that AxisVM holds as one member of several lines.</summary>
    private static string MultiLineCurve(string linkPath)
    {
        var byGuid = new Dictionary<string, List<int>>();
        for (int i = 1; i <= model.Lines.Count; i++)
            foreach (string g in LinkState.Guids(model.Lines.Attachments, i))
            {
                List<int> l;
                if (!byGuid.TryGetValue(g, out l)) { l = new List<int>(); byGuid[g] = l; }
                l.Add(i);
            }
        foreach (JsonValue e in LinkFile.Read(linkPath)[LinkFile.RhinoSection]["lines"].ArrayValue)
        {
            if (e["start"] == null || e["points"] != null || LinkFile.Get(e["attrs"], "Ax_Diaphragm").Length > 0) continue;
            List<int> ids;
            if (!byGuid.TryGetValue(LinkFile.Get(e, "guid"), out ids) || ids.Count < 2) continue;
            if (ids.Select(l => model.Lines.Item[l].MemberID).Distinct().Count() == 1) return LinkFile.Get(e, "guid");
        }
        return null;
    }

    private static string originalLoads = "";

    private static string LoadTypes()
    {
        var t = new SortedDictionary<string, int>();
        for (int i = 1; i <= model.Loads.Count; i++) { string k = model.Loads.get_LoadType(i).ToString(); int c; t.TryGetValue(k, out c); t[k] = c + 1; }
        return string.Join(", ", t.Select(kv => kv.Key + " " + kv.Value));
    }

    private static int DiaphragmLines()
    {
        int n = 0;
        for (int d = 1; d <= model.Diaphragm.Count; d++) { Array a; model.Diaphragm.GetLines(d, out a); n += a == null ? 0 : a.Length; }
        return n;
    }

    private static string Attachment(AxisVMAttachments a, string name, int index)
    {
        try
        {
            Array d;
            if (a.GetData(name, index, out d) <= 0 || d == null) return null;
            return Encoding.UTF8.GetString(d.Cast<object>().Select(Convert.ToByte).ToArray());
        }
        catch { return null; }
    }

    /// <summary>End points [m] of every line stamped with this Rhino id.</summary>
    private static List<double[][]> LinesOf(string guid)
    {
        var hits = new List<double[][]>();
        for (int i = 1; i <= model.Lines.Count; i++)
        {
            if (!LinkState.SplitGuids(Attachment(model.Lines.Attachments, AxisModelBuilder.GuidAttachment, i)).Contains(guid)) continue;
            IAxisVMLine ln = model.Lines.Item[i];
            RPoint3d a = new RPoint3d(), b = new RPoint3d();
            model.Nodes.GetNodeCoord(ln.StartNode, ref a);
            model.Nodes.GetNodeCoord(ln.EndNode, ref b);
            hits.Add(new[] { new[] { a.x, a.y, a.z }, new[] { b.x, b.y, b.z } });
        }
        return hits;
    }

    /// <summary>Total length [m] of all edge hinges (counted per domain). Meshing splits hinges, this stays.</summary>
    private static double HingedLength()
    {
        double sum = 0;
        int n = C(() => model.EdgeConnections.Count);
        for (int i = 1; i <= n; i++)
        {
            try
            {
                REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172();
                if (model.EdgeConnections.GetRec_V172(i, ref rec) <= 0) continue;
                IAxisVMLine ln = model.Lines.Item[rec.LineId];
                RPoint3d a = new RPoint3d(), b = new RPoint3d();
                model.Nodes.GetNodeCoord(ln.StartNode, ref a);
                model.Nodes.GetNodeCoord(ln.EndNode, ref b);
                sum += Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
            }
            catch { }
        }
        return sum;
    }

    private static ImportStats Import(RhinoModel rhino, string path, double lengthToM, bool update, List<string> notes, HashSet<string> known = null, Dictionary<string, string> adoptedPanels = null)
    {
        var opt = new ImportOptions
        {
            Units = new AxUnits { LengthToM = lengthToM, LengthName = lengthToM < 1 ? "mm" : "m" },
            MergeToleranceM = 0.001, AutoIntersect = true, SourcePath = path, ReplacePrevious = update, RhinoKnownGuids = known, AdoptedPanels = adoptedPanels
        };
        var builder = new AxisModelBuilder(app, model, rhino, opt, notes);
        builder.Run();
        return builder.Stats;
    }

    private static RhinoModel Read(string json)
    {
        return RhinoModel.FromJson(JsonValue.Parse(File.ReadAllText(json)), new List<string>());
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string json = args[0];
        if (args.Length > 1 && args[1] == "roundtrip") return RoundTripMain(json);
        if (args.Length > 1 && args[1] == "rhino-echo") return RhinoEchoMain(json);
        if (args.Length > 1 && args[1] == "rhino-elements") return RhinoElementsMain(json);
        if (args.Length > 1 && args[1] == "plain") return PlainMain(json);
        if (args.Length > 1 && args[1] == "load") return LoadMain(json);
        if (args.Length > 1 && args[1] == "diff") return DiffMain(json);
        if (args.Length > 1 && args[1] == "probe-diaphragm") return ProbeDiaphragm(json);
        if (args.Length > 1 && args[1] == "probe-elements") return ProbeElements();
        if (args.Length > 1 && args[1] == "elements") return ElementsMain();
        double lengthToM = args.Length > 1 && args[1] == "m" ? 1.0 : 0.001;
        if (args.Length > 2 && args[2] == "link") return LinkMain(json, lengthToM);
        // The mark is the file name, so every step imports "under" the same one.
        string linkPath = Path.Combine(Path.GetTempPath(), "nextra-update-check.json");

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Check(model.Lines.Count == 0, "axisvm: fresh empty model in a separate AxisVM");

            int u1 = model.Nodes.Add(-50, 0, 0), u2 = model.Nodes.Add(-45, 0, 0);
            RLineGeomData g = new RLineGeomData();
            int userLine = model.Lines.Add(u1, u2, ELineGeomType.lgtStraightLine, ref g);
            int userUid = model.Lines.get_UID(userLine);

            // ---- 1. plain import ------------------------------------------------------------------
            var notes = new List<string>();
            RhinoModel rhino = Read(json);
            RhinoLine beam = rhino.Lines.First(l => AxisModelBuilder.ParseLineRole(l.Attr(AxKeys.Type)) == AxisModelBuilder.LineRole.Beam && l.Vertices.Count == 2);
            beam.Attrs[AxKeys.Parts] = "UpdateCheckPart";
            ImportStats s1 = Import(rhino, linkPath, lengthToM, false, notes);
            string after1 = Counts();
            Console.WriteLine("      after import: " + after1);
            Check(s1.Replaced == null, "import: a first import replaces nothing");
            Check(!notes.Any(n => n.Contains("could not be marked")), "import: every created entity was marked");

            LinkedItems found = AxisModelBuilder.FindLinked(model, linkPath);
            Console.WriteLine("      found as earlier import: " + found);
            Check(found.Lines == model.Lines.Count - 1 && found.Nodes == model.Nodes.Count - 2,
                "find: every imported line and node is found, the user's own are not");
            Check(found.Domains == model.Domains.Count && found.LoadPanels == model.LoadPanels.Count && found.Parts == 1,
                "find: every domain, load panel and the part are found");
            Check(AxisModelBuilder.FindLinked(model, Path.Combine(Path.GetTempPath(), "other.json")).Total == 0,
                "find: an import of another file is not this one");

            // ---- 2. same export as an update ------------------------------------------------------
            notes.Clear();
            ImportStats s2 = Import(Read(json).WithPart(beam.Guid, "UpdateCheckPart"), linkPath, lengthToM, true, notes);
            string after2 = Counts();
            Console.WriteLine("      after update: " + after2);
            Check(s2.Replaced != null && s2.Replaced.Total == found.Total, "update: replaced exactly what the import created");
            Check(after2 == after1, "update: unchanged export -> every count is the same as after the import");
            Check(!notes.Any(n => n.Contains("Part '")), "update: the part was rebuilt without a name clash");
            Check(model.Lines.get_IndexOfUID(userUid) > 0, "update: the line drawn in AxisVM is untouched");

            // ---- 3. one beam moved, one curve deleted ---------------------------------------------
            notes.Clear();
            RhinoModel changed = Read(json).WithPart(beam.Guid, "UpdateCheckPart");
            RhinoLine moved = changed.Lines.First(l => l.Guid == beam.Guid);
            double up = 0.5 / lengthToM;
            for (int i = 0; i < moved.Vertices.Count; i++)
                moved.Vertices[i] = new RhinoXyz(moved.Vertices[i].X, moved.Vertices[i].Y, moved.Vertices[i].Z + up);
            moved.Start = moved.Vertices[0];
            moved.End = moved.Vertices[moved.Vertices.Count - 1];
            RhinoLine gone = changed.Lines.First(l => l.Guid != beam.Guid && AxisModelBuilder.ParseLineRole(l.Attr(AxKeys.Type)) == AxisModelBuilder.LineRole.Beam);
            changed.Lines.Remove(gone);

            double oldZ = beam.Vertices[0].Z * lengthToM;
            Import(changed, linkPath, lengthToM, true, notes);
            Console.WriteLine("      after change: " + Counts());
            List<double[][]> movedLines = LinesOf(beam.Guid);
            Check(movedLines.Count > 0 && movedLines.All(l => Math.Abs(l[0][2] - (oldZ + 0.5)) < 1e-6 && Math.Abs(l[1][2] - (oldZ + 0.5)) < 1e-6),
                string.Format("change: the moved beam exists only at its new height ({0} line(s))", movedLines.Count));
            Check(LinesOf(gone.Guid).Count == 0, "change: the curve deleted in Rhino is gone from AxisVM");
            Check(C(() => model.CustomParts.RootFolder.PartCount) == 1, "change: still exactly one part");
            Check(model.Lines.get_IndexOfUID(userUid) > 0, "change: the line drawn in AxisVM is untouched");

            // ---- back to the original, then the duplicate control -----------------------------------
            Import(Read(json).WithPart(beam.Guid, "UpdateCheckPart"), linkPath, lengthToM, true, new List<string>());
            Check(Counts() == after1, "revert: updating back to the original export restores every count");

            Import(Read(json), linkPath, lengthToM, false, new List<string>());
            Check(C(() => model.Lines.Count) > C(() => model.Domains.Count) && Counts() != after1,
                "control: importing again WITHOUT update adds a second copy");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All update checks passed." : failures + " update check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    // ==================================================================================================
    // Live link round trip (mode "link")
    // ==================================================================================================

    private static string rhinoSent;

    /// <summary>"Load from Rhino" as the add-on runs it: update from the link file, then mark AxisVM as in line.</summary>
    private static void LoadFromRhino(string linkPath, double lengthToM, List<string> notes)
    {
        JsonValue root = LinkFile.Read(linkPath);
        RhinoModel rhino = RhinoModel.FromJson(root, notes);
        Import(rhino, linkPath, lengthToM, true, notes, LinkFile.RhinoKnownGuids(root), RhinoLinkSender.AdoptedPanels(root));
        rhinoSent = LinkFile.Get(root[LinkFile.RhinoSection], "sent");
        RhinoLinkSender.MarkLoaded(model, linkPath, rhinoSent);
    }

    private static JsonValue Section(string linkPath) { return LinkFile.Read(linkPath)[LinkFile.AxisVmSection]; }

    private static JsonValue Entry(JsonValue section, string list, string guid)
    {
        JsonValue arr = section[list];
        for (int i = 0; arr != null && i < arr.Count; i++)
            if (LinkFile.Get(arr.At(i), "guid") == guid) return arr.At(i);
        return null;
    }

    private static JsonValue LibEntry(JsonValue lib, string list, string name)
    {
        JsonValue arr = lib == null ? null : lib[list];
        for (int i = 0; arr != null && i < arr.Count; i++)
            if (string.Equals(LinkFile.Get(arr.At(i), "name"), name, StringComparison.OrdinalIgnoreCase)) return arr.At(i);
        return null;
    }

    private static bool Changed(JsonValue e) { return e != null && e["changed"] != null && e["changed"].BoolValue; }

    private static bool Deleted(JsonValue section, string guid)
    {
        JsonValue d = section["deleted"];
        for (int i = 0; d != null && i < d.Count; i++) if (d.At(i).AsString("") == guid) return true;
        return false;
    }

    private static void SetLineAttr(int lineId, Func<RLineAttr, RLineAttr> change)
    {
        RLineAttr a;
        LinkState.TryLineAttr(model, lineId, out a);
        Array ids = Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 1 });
        ids.SetValue(lineId, 1);
        Array attrs = Array.CreateInstance(typeof(RLineAttr), new[] { 1 }, new[] { 1 });
        attrs.SetValue(change(a), 1);
        model.Lines.BulkSetAttr(ids, attrs);
    }

    private static int LineIndexOf(string guid)
    {
        for (int i = 1; i <= model.Lines.Count; i++)
            if (LinkState.Guids(model.Lines.Attachments, i).Contains(guid)) return i;
        return 0;
    }

    private static int LinkMain(string json, double lengthToM)
    {
        string linkPath = Path.Combine(Path.GetTempPath(), "nextra-link-check.axlink.json");
        JsonValue exported = JsonValue.Parse(File.ReadAllText(json));
        JsonValue link = JsonValue.Obj().Set("link", JsonValue.Num(1))
            .Set(LinkFile.RhinoSection, exported.Set("sent", JsonValue.Str("2026-01-01T00:00:00.000Z")).Set("basedOnAxisvm", JsonValue.Str("")));
        File.WriteAllText(linkPath, link.ToJson());

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Check(model.Lines.Count == 0, "axisvm: fresh empty model in a separate AxisVM");

            // ---- 1. load, send at once: nothing changed --------------------------------------------
            var notes = new List<string>();
            LoadFromRhino(linkPath, lengthToM, notes);
            string loaded = Counts();
            Console.WriteLine("      after load: " + loaded);
            Check(LinkFile.Get(Section(linkPath), "basedOnRhino") == rhinoSent && Section(linkPath)["built"].Count > 0,
                "load: the axisvm section records the Rhino send it is based on and what was built");

            SendResult r = RhinoLinkSender.Send(model, linkPath);
            Console.WriteLine(string.Format("      send: {0} changed, {1} unchanged, {2} adopted, {3} deleted", r.Changed, r.Unchanged, r.Adopted, r.Deleted));
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0 && r.Unchanged > 0, "send: right after a load nothing is reported as changed");
            Check(LinkFile.Read(linkPath)[LinkFile.RhinoSection]["lines"].Count == exported["lines"].Count, "send: the rhino section is kept as it was");

            // ---- 2. edit in AxisVM ------------------------------------------------------------------
            RhinoModel rhino = RhinoModel.FromJson(exported, new List<string>());
            Func<RhinoLine, AxisModelBuilder.LineRole> role = l => AxisModelBuilder.ParseLineRole(l.Attr(AxKeys.Type));
            List<RhinoLine> singles = rhino.Lines.Where(l => l.Vertices.Count == 2 && LinesOf(l.Guid).Count == 1).ToList();
            RhinoLine sectionBeam = singles.First(l => role(l) == AxisModelBuilder.LineRole.Beam);
            RhinoLine gone = singles.First(l => l.Guid != sectionBeam.Guid && role(l) == AxisModelBuilder.LineRole.Beam);

            int sbLine = LineIndexOf(sectionBeam.Guid);
            RLineAttr before;
            LinkState.TryLineAttr(model, sbLine, out before);
            int otherSection = before.StartCrossSectionIndex == 1 ? 2 : 1;
            Check(model.CrossSections.Count >= 2, "edit: the model has a second cross-section to switch to");
            SetLineAttr(sbLine, a => { a.StartCrossSectionIndex = otherSection; a.EndCrossSectionIndex = otherSection; return a; });
            string newSection = LinkState.SectionName(model, otherSection);

            // Move the beam's end node 0.3 m in y (whatever else hangs on it moves too).
            int movedNode = model.Lines.Item[sbLine].EndNode;
            double[] p0 = LinkState.NodeXyz(model, movedNode);
            RPoint3d np = new RPoint3d { x = p0[0], y = p0[1] + 0.3, z = p0[2] };
            model.Nodes.SetNodeCoord(movedNode, ref np);

            model.Lines.Delete(LineIndexOf(gone.Guid));

            int n1 = model.Nodes.Add(200, 0, 0), n2 = model.Nodes.Add(205, 0, 0);
            RLineGeomData g = new RLineGeomData();
            int nativeLine = model.Lines.Add(n1, n2, ELineGeomType.lgtStraightLine, ref g);
            RPoint3d e1 = new RPoint3d(), e2 = new RPoint3d();
            model.Lines.Item[nativeLine].DefineAsBeam(before.MaterialIndex, otherSection, otherSection, ref e1, ref e2);

            r = RhinoLinkSender.Send(model, linkPath);
            JsonValue sec = Section(linkPath);
            Console.WriteLine(string.Format("      send after edits: {0} changed, {1} unchanged, {2} adopted, {3} deleted", r.Changed, r.Unchanged, r.Adopted, r.Deleted));

            JsonValue sb = Entry(sec, "lines", sectionBeam.Guid);
            Check(Changed(sb) && LinkFile.Get(sb["attrs"], "Ax_Section") == newSection && sb["attrs"].Count == 1,
                "send: the re-sectioned beam carries only its new Ax_Section (" + newSection + ")");
            double[] end = { sb["end"].At(0).AsDouble(0), sb["end"].At(1).AsDouble(0), sb["end"].At(2).AsDouble(0) };
            double[] start = { sb["start"].At(0).AsDouble(0), sb["start"].At(1).AsDouble(0), sb["start"].At(2).AsDouble(0) };
            double[] rs = { sectionBeam.Start.X * lengthToM, sectionBeam.Start.Y * lengthToM, sectionBeam.Start.Z * lengthToM };
            Check(Math.Abs(start[0] - rs[0]) + Math.Abs(start[1] - rs[1]) + Math.Abs(start[2] - rs[2]) < 1e-6
                  && Math.Abs(end[1] - (sectionBeam.End.Y * lengthToM + 0.3)) < 1e-6,
                "send: its moved end is sent, in metres, the same way round as the Rhino curve");
            Check(Deleted(sec, gone.Guid), "send: the beam deleted in AxisVM is listed as deleted");
            string adopted = LinkState.Guids(model.Lines.Attachments, nativeLine).FirstOrDefault();
            JsonValue ad = Entry(sec, "lines", adopted ?? "-");
            Check(r.Adopted == 1 && ad != null && Changed(ad) && LinkFile.Get(ad["attrs"], "Ax_Type") == "Beam" && LinkFile.Get(ad["attrs"], "Ax_Section") == newSection,
                "send: the beam drawn in AxisVM is adopted with a new id, its type and section");
            int unchangedBeams = rhino.Lines.Count(l => l.Guid != sectionBeam.Guid && Entry(sec, "lines", l.Guid) != null && !Changed(Entry(sec, "lines", l.Guid)));
            Check(unchangedBeams > 0, "send: untouched curves are listed as unchanged (" + unchangedBeams + ")");
            Check(LinkFile.ReadStatus(LinkFile.Read(linkPath)).AxisVmPending, "status: AxisVM changes are pending for Rhino");

            // ---- 3. send again before Rhino loaded: nothing is lost -----------------------------------
            r = RhinoLinkSender.Send(model, linkPath);
            sec = Section(linkPath);
            Check(r.Adopted == 0 && Changed(Entry(sec, "lines", sectionBeam.Guid)) && Changed(Entry(sec, "lines", adopted)) && Deleted(sec, gone.Guid),
                "send again: the earlier changes are still there, nothing adopted twice");

            // ---- 4. load the old Rhino model: the adopted beam must survive ---------------------------
            LoadFromRhino(linkPath, lengthToM, new List<string>());
            Check(LinesOf(adopted).Count == 1, "load (Rhino has not seen the adopted beam): it is kept, once");
            Check(LinesOf(gone.Guid).Count > 0 && Counts() != loaded, "load: Rhino's model is rebuilt (the deleted beam is back)");

            // ---- 5. Rhino loaded it and re-sent: the adopted beam is now Rhino's ----------------------
            LinkFile.Update(linkPath, root =>
            {
                JsonValue rh = root[LinkFile.RhinoSection];
                double k = 1.0 / lengthToM;
                rh["lines"].Add(JsonValue.Obj().Set("guid", JsonValue.Str(adopted)).Set("geom", JsonValue.Str("line"))
                    .Set("start", JsonValue.Xyz(200 * k, 0, 0)).Set("end", JsonValue.Xyz(205 * k, 0, 0))
                    .Set("attrs", JsonValue.Obj().Set("Ax_Type", JsonValue.Str("Beam")).Set("Ax_Section", JsonValue.Str(newSection)).Set("Ax_Guid", JsonValue.Str(adopted))));
                rh.Set("seenAxisvmGuids", JsonValue.Arr().Add(JsonValue.Str(adopted)));
                rh.Set("sent", JsonValue.Str("2026-01-02T00:00:00.000Z"));
            });
            LoadFromRhino(linkPath, lengthToM, new List<string>());
            int il = LineIndexOf(adopted);
            Check(LinesOf(adopted).Count == 1 && il > 0 && LinkState.Read(model.Lines.Attachments, LinkState.Link, il) != null
                  && LinkState.Read(model.Lines.Attachments, LinkState.Adopted, il) == null,
                "load (Rhino holds the adopted beam): exactly one copy, now an ordinary linked element");
            Check(C(() => model.Lines.Count) == LinesCountAfterLoad(loaded) + 1, "load: no leftovers - the model is Rhino's model plus that beam");

            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after that load: nothing changed");

            // ---- 6. library: materials, sections, load cases, combinations ---------------------------------
            model.CrossSections.AddRectangular("LibRect300x500", 0.3, 0.5, ECrossSectionProcess.cspRolled);
            model.CrossSections.AddPipe("LibPipe168x8", 0.1683, 0.008, ECrossSectionProcess.cspRolled);
            model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C25/30");
            int wind = model.LoadCases.Add("LibWind", ELoadCaseType.lctStandard);
            Array cf = Array.CreateInstance(typeof(double), new[] { 2 }, new[] { 1 }); cf.SetValue(1.35, 1); cf.SetValue(1.5, 2);
            Array ci = Array.CreateInstance(typeof(int), new[] { 2 }, new[] { 1 }); ci.SetValue(1, 1); ci.SetValue(wind, 2);
            int combo = model.LoadCombinations.Add("LibULS", ECombinationType.ctULS, cf, ci);
            Check(wind > 0 && combo > 0, "library: a load case and a combination were made in AxisVM (" + wind + ", " + combo + ")");

            r = RhinoLinkSender.Send(model, linkPath);
            JsonValue lib = Section(linkPath)["library"];
            Console.WriteLine(string.Format("      library sent: {0} materials, {1} sections, {2} load cases, {3} combinations; {4} new for Rhino", r.Library[0], r.Library[1], r.Library[2], r.Library[3], r.LibraryNew));
            JsonValue rect = LibEntry(lib, "sections", "LibRect300x500"), pipe = LibEntry(lib, "sections", "LibPipe168x8");
            Check(rect != null && LinkFile.Get(rect, "shape") == "Rectangle" && Math.Abs(rect["b"].AsDouble(0) - 300) < 0.01 && Math.Abs(rect["h"].AsDouble(0) - 500) < 0.01,
                "library: a rectangular section arrives as Rectangle 300 x 500 mm (" + (rect == null ? "missing" : rect.ToJson().Replace("\r\n", " ")) + ")");
            Check(pipe != null && LinkFile.Get(pipe, "shape") == "Pipe" && Math.Abs(pipe["h"].AsDouble(0) - 168.3) < 0.01 && Math.Abs(pipe["t"].AsDouble(0) - 8) < 0.01,
                "library: a pipe section arrives as Pipe d=168.3 t=8 mm (" + (pipe == null ? "missing" : pipe.ToJson().Replace("\r\n", " ")) + ")");
            JsonValue ipe = LibEntry(lib, "sections", "IPE 300");
            Check(ipe != null && LinkFile.Get(ipe, "shape") == "Catalog", "library: a catalog profile arrives by name (IPE 300)");
            Check(LibEntry(lib, "materials", "C25/30") != null && LinkFile.Get(LibEntry(lib, "materials", "C25/30"), "family") == "Concrete", "library: a material arrives with its family");
            Check(LibEntry(lib, "loadCases", "LibWind") != null, "library: the load case arrives");
            JsonValue lc = LibEntry(lib, "combinations", "LibULS");
            Check(lc != null && LinkFile.Get(lc, "type") == "ULS" && lc["terms"].Count == 2
                  && LinkFile.Get(lc["terms"].At(1), "case") == "LibWind" && Math.Abs(lc["terms"].At(1)["factor"].AsDouble(0) - 1.5) < 1e-9,
                "library: the combination arrives with its type and factor x case terms");
            Check(r.LibraryNew >= 5 && LinkFile.ReadStatus(LinkFile.Read(linkPath)).AxisVmPending, "library: the new entries count as changes waiting for Rhino (" + r.LibraryNew + ")");
            RhinoLinkSender.MarkLoaded(model, linkPath, rhinoSent);

            // Rhino library -> AxisVM: a combination defined in Rhino is created on load
            LinkFile.Update(linkPath, root =>
            {
                JsonValue rl = root[LinkFile.RhinoSection]["library"];
                if (rl == null) { rl = JsonValue.Obj(); root[LinkFile.RhinoSection].Set("library", rl); }
                if (rl["combinations"] == null) rl.Set("combinations", JsonValue.Arr());
                rl["combinations"].Add(JsonValue.Obj().Set("name", JsonValue.Str("RhinoSLS")).Set("type", JsonValue.Str("SLSChar"))
                    .Set("terms", JsonValue.Arr()
                        .Add(JsonValue.Obj().Set("case", JsonValue.Str("Dead")).Set("factor", JsonValue.Num(1.0)))
                        .Add(JsonValue.Obj().Set("case", JsonValue.Str("RhinoOnlyCase")).Set("factor", JsonValue.Num(0.7)))));
                root[LinkFile.RhinoSection].Set("sent", JsonValue.Str("2026-01-03T00:00:00.000Z"));
            });
            var comboNotes = new List<string>();
            LoadFromRhino(linkPath, lengthToM, comboNotes);
            foreach (string n in comboNotes) if (n.IndexOf("ombination", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("RhinoOnlyCase", StringComparison.Ordinal) >= 0) Console.WriteLine("      note: " + n);
            Console.WriteLine("      combinations in AxisVM: " + model.LoadCombinations.Count);
            JsonValue after = Section(linkPath);
            JsonValue made = LibEntry(Section(linkPath)["library"] ?? JsonValue.Obj(), "combinations", "RhinoSLS");
            RhinoLinkSender.Send(model, linkPath);
            JsonValue lib2 = Section(linkPath)["library"];
            JsonValue slsCombo = LibEntry(lib2, "combinations", "RhinoSLS");
            Check(slsCombo != null && LinkFile.Get(slsCombo, "type") == "SLSChar" && slsCombo["terms"].Count == 2 && LibEntry(lib2, "loadCases", "RhinoOnlyCase") != null,
                "library: a combination from Rhino's library is created in AxisVM, with the load case it names");
            Check(LibEntry(lib2, "sections", "LibRect300x500") != null && LibEntry(lib2, "combinations", "LibULS") != null,
                "library: nothing AxisVM-made is removed by loading Rhino's model");

            // ---- 7. load panels --------------------------------------------------------------------------
            int[] pn = new int[4]; int[] pl = new int[4];
            double[][] corner = { new[] { 300.0, 0, 0 }, new[] { 304.0, 0, 0 }, new[] { 304.0, 4, 0 }, new[] { 300.0, 4, 0 } };
            for (int k = 0; k < 4; k++) pn[k] = model.Nodes.Add(corner[k][0], corner[k][1], corner[k][2]);
            for (int k = 0; k < 4; k++) { RLineGeomData lg = new RLineGeomData(); pl[k] = model.Lines.Add(pn[k], pn[(k + 1) % 4], ELineGeomType.lgtStraightLine, ref lg); }
            Array plIds = Array.CreateInstance(typeof(int), new[] { 4 }, new[] { 1 });
            for (int k = 0; k < 4; k++) plIds.SetValue(pl[k], k + 1);
            int panelsBefore = model.LoadPanels.Count;
            int panel = model.LoadPanels.AddFromLineIDs(ref plIds);
            Check(panel > 0 && model.LoadPanels.Count == panelsBefore + 1, "panel: a load panel was drawn in AxisVM");

            r = RhinoLinkSender.Send(model, linkPath);
            sec = Section(linkPath);
            JsonValue pe = null;
            for (int i = 0; sec["domains"] != null && i < sec["domains"].Count; i++)
                if (LinkFile.Get(sec["domains"].At(i)["attrs"], "Ax_FEType") == "LoadPanel") pe = sec["domains"].At(i);
            Check(pe != null && pe["new"].BoolValue && Changed(pe) && pe["contour"].Count == 4,
                "panel: it is sent as a new LoadPanel surface with its 4-corner outline");
            string panelGuid = pe == null ? "" : LinkFile.Get(pe, "guid");

            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Adopted == 0, "panel: sending again does not adopt it a second time");

            // Rhino has loaded it: the file's rhino section now holds a LoadPanel domain with that id
            LinkFile.Update(linkPath, root =>
            {
                JsonValue rh = root[LinkFile.RhinoSection];
                JsonValue contour = JsonValue.Arr();
                foreach (double[] c in corner) contour.Add(JsonValue.Xyz(c[0] / lengthToM, c[1] / lengthToM, c[2] / lengthToM));
                rh["domains"].Add(JsonValue.Obj().Set("guid", JsonValue.Str(panelGuid)).Set("contour", contour)
                    .Set("attrs", JsonValue.Obj().Set("Ax_FEType", JsonValue.Str("LoadPanel")).Set("Ax_Guid", JsonValue.Str(panelGuid))));
                rh.Set("seenAxisvmGuids", JsonValue.Arr().Add(JsonValue.Str(panelGuid)));
                rh.Set("sent", JsonValue.Str("2026-01-04T00:00:00.000Z"));
            });
            LoadFromRhino(linkPath, lengthToM, new List<string>());
            int panelsAfter = model.LoadPanels.Count;
            Check(panelsAfter == panelsBefore + 1, "panel: Rhino holds it now - loading rebuilds exactly one panel, not two (" + panelsAfter + " vs " + (panelsBefore + 1) + ")");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Adopted == 0 && r.Deleted == 0, "panel: after that load it is an ordinary linked element - nothing to send");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All live link checks passed." : failures + " live link check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    // ==================================================================================================
    // AxisVM-first round trip on a real model (mode "roundtrip")
    // ==================================================================================================

    /// <summary>
    /// "Load from Rhino" exactly as the add-on runs it (undo step, one BeginUpdate, refresh, mark loaded), every builder
    /// step printed as it finishes - if AxisVM dies, the last printed step is where.
    /// </summary>
    private static LinkDiffPlan lastPlan;

    /// <summary>"Load from Rhino" as the add-on runs it (undo step, LinkLoader, refresh), steps printed as they finish.</summary>
    private static ImportStats AddonLoad(string linkPath, List<string> notes, bool onlyChanges = true)
    {
        // The unit as the dialog takes it: from the Rhino section's source (the simulated echo writes none = metres).
        string unit = LinkFile.Get(LinkFile.RhinoPart(LinkFile.Read(linkPath))["source"], "unitSystem");
        double toM = string.IsNullOrEmpty(unit) ? 1.0 : AxUnits.LengthUnitFactors[AxUnits.LengthIndexFromRhinoName(unit)];
        var opt = new ImportOptions { Units = new AxUnits { LengthToM = toM, LengthName = toM < 1 ? "mm" : "m" }, MergeToleranceM = 0.001, AutoIntersect = true };
        model.SaveUndo("Rhino update");
        LinkLoadResult r;
        // RT_REPLACE=0: the dialog's "Update what an earlier load built" unticked - a full new import on top.
        bool replace = Environment.GetEnvironmentVariable("RT_REPLACE") != "0";
        try { r = LinkLoader.Load(app, model, linkPath, opt, replace, onlyChanges, notes, s => Console.WriteLine("      step " + s), Environment.GetEnvironmentVariable("RT_STOPAFTER")); }
        finally { try { model.Refresh(); } catch { } }
        lastPlan = r.Plan;
        if (r.Plan != null) Console.WriteLine("      only changes: " + r.Plan.Summary() + "; deleting " + r.Plan.Counts() + (r.Plan.Trace.Length > 0 ? "\n        " + r.Plan.Trace : ""));
        return r.Stats;
    }

    /// <summary>Rhino moves one end of a curve (as Rhino's AxLink would send it): returns the curve's guid.</summary>
    private static string MoveCurveEnd(string linkPath, Func<JsonValue, bool> pick, double dy, string sent)
    {
        string guid = null;
        LinkFile.Update(linkPath, root =>
        {
            JsonValue lines = root[LinkFile.RhinoSection]["lines"];
            for (int i = 0; i < lines.Count && guid == null; i++)
            {
                JsonValue e = lines.At(i);
                if (e["end"] == null || e["points"] != null || e["arcMid"] != null || !pick(e)) continue;
                JsonValue end = e["end"];
                e.Set("end", JsonValue.Xyz(end.At(0).AsDouble(0), end.At(1).AsDouble(0) + dy, end.At(2).AsDouble(0)));
                guid = LinkFile.Get(e, "guid");
            }
            root[LinkFile.RhinoSection].Set("sent", JsonValue.Str(sent));
        });
        return guid;
    }

    private static string Without(string counts, params string[] names)
    {
        foreach (string n in names)
        {
            int i = counts.IndexOf(n + " ");
            if (i < 0) continue;
            int j = counts.IndexOf(',', i);
            counts = counts.Remove(i, (j < 0 ? counts.Length : j + 2) - i);
        }
        return counts;
    }

    /// <summary>What Rhino's AxLink sends back after loading an AxisVM send: the same entries as Rhino objects with those ids.</summary>
    private static void RhinoTakesAxisVmSend(string linkPath, string sent)
    {
        LinkFile.Update(linkPath, root =>
        {
            JsonValue ax = root[LinkFile.AxisVmSection];
            JsonValue rh = JsonValue.Obj().Set("schema", JsonValue.Num(2))
                .Set("source", JsonValue.Obj().Set("file", JsonValue.Str("roundtrip.3dm")).Set("unitSystem", JsonValue.Str("Meters")).Set("tolerance", JsonValue.Num(0.001)));
            JsonValue seen = JsonValue.Arr();
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                JsonValue arr = JsonValue.Arr();
                JsonValue src = ax[list];
                for (int i = 0; src != null && i < src.Count; i++)
                {
                    JsonValue e = src.At(i), copy = JsonValue.Obj();
                    string guid = LinkFile.Get(e, "guid");
                    foreach (string k in new[] { "guid", "geom", "xyz", "start", "end", "arcMid", "points", "contour", "holes" })
                        if (e[k] != null) copy.Set(k, e[k]);
                    JsonValue attrs = JsonValue.Obj().Set("Ax_Guid", JsonValue.Str(guid));
                    JsonValue ea = e["attrs"];
                    if (ea != null && ea.ObjectValue != null) foreach (string k in ea.ObjectValue.Keys) if (ea[k].AsString("").Length > 0) attrs.Set(k, ea[k]);
                    arr.Add(copy.Set("attrs", attrs));
                    seen.Add(JsonValue.Str(guid));
                }
                rh.Set(list, arr);
            }
            if (ax["library"] != null) rh.Set("library", ax["library"]);
            rh.Set("sent", JsonValue.Str(sent)).Set("basedOnAxisvm", JsonValue.Str(LinkFile.Get(ax, "sent"))).Set("seenAxisvmGuids", seen);
            root.Set(LinkFile.RhinoSection, rh);
        });
    }

    /// <summary>Lines, by Rhino id, as sorted end-point text - what a model "looks like" for comparing two builds.</summary>
    private static string Shape()
    {
        var all = new List<string>();
        AxisVMAttachments la = model.Lines.Attachments;
        LinkState.BeginCache(model);
        try
        {
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                List<string> g = LinkState.Guids(la, i);
                if (g.Count == 0) continue;
                g.Sort(StringComparer.Ordinal);
                all.Add(string.Join(";", g) + " " + LinkState.LineGeom(LinkState.Fields(LinkState.LineState(model, i), 5)[0]) + " " + LinkState.Fields(LinkState.LineState(model, i), 5)[1]);
            }
        }
        finally { LinkState.EndCache(); }
        all.Sort(StringComparer.Ordinal);
        return string.Join("\n", all);
    }

    /// <summary>
    /// "Only changes" on a Rhino-first link (mode "diff", any link file with a rhino section in metres): first load builds
    /// everything; loading again changes nothing; then a moved beam end, a new section and a deleted rib are each loaded
    /// with only changes - each must rebuild a few objects, and the result must equal a full rebuild of the same Rhino
    /// state (same counts, same lines per Rhino id).
    /// </summary>
    private static int DiffMain(string link)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        Directory.CreateDirectory(dir);
        string linkPath = Path.Combine(dir, "diff.axlink.json");
        File.Copy(link, linkPath, true);

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            var notes = new List<string>();
            AddonLoad(linkPath, notes);
            Check(lastPlan != null && lastPlan.Everything, "first load: no record yet - everything is built (" + (lastPlan == null ? "-" : lastPlan.Reason) + ")");
            string first = Counts();
            Console.WriteLine("      after first load: " + first);

            AddonLoad(linkPath, notes);
            Check(lastPlan != null && !lastPlan.Everything && lastPlan.Build.Count == 0 && lastPlan.Counts().Total == 0 && Counts() == first,
                "load again: nothing rebuilt, nothing deleted, every count the same");

            // Each state is compared with a full rebuild of the same Rhino model at the end, in a fresh AxisVM (a second
            // model in the same AxisVM answers some calls for the wrong model).
            var states = new List<string[]>();   // what, snapshot path, counts, shape
            Func<string, int> step = (what) =>
            {
                notes.Clear();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AddonLoad(linkPath, notes);
                int built = lastPlan == null ? -1 : lastPlan.Build.Count;
                foreach (string n in notes.Where(n => n.StartsWith("Update") || n.Contains("could not") || n.Contains("rejected")).Take(10)) Console.WriteLine("      note: " + n);
                string diffCounts = Counts();
                Console.WriteLine(string.Format("      {0} ({1:0.0} s): {2}", what, sw.Elapsed.TotalSeconds, diffCounts));
                SendResult sr = RhinoLinkSender.Send(model, linkPath);
                Check(sr.Changed == 0 && sr.Adopted == 0 && sr.Deleted == 0, what + ": a send right after reports nothing changed (" + sr.Changed + " / " + sr.Adopted + " / " + sr.Deleted + ")");
                JsonValue sec = Section(linkPath);
                foreach (string list in new[] { "points", "lines", "domains" })
                    foreach (JsonValue e in sec[list].ArrayValue)
                        if (Changed(e)) Console.WriteLine("        changed " + list + ": " + e.ToJson().Replace("\r\n", " ").Replace("  ", ""));
                foreach (string n in sr.Notes.Where(n => n.Contains("chain"))) Console.WriteLine("        send note: " + n);
                string snapshot = Path.Combine(dir, "diff-ref" + states.Count + ".axlink.json");
                File.Copy(linkPath, snapshot, true);
                LinkFile.Update(snapshot, root => root.Set(LinkFile.AxisVmSection, JsonValue.Obj()));
                states.Add(new[] { what, snapshot, diffCounts, Shape() });
                return built;
            };

            MoveCurveEnd(linkPath, e => LinkFile.Get(e["attrs"], "Ax_Type") == "Beam", 0.3, "2026-02-02T00:00:00.000Z");
            int n1 = step("moved beam end");
            Check(n1 > 0 && n1 < 60, "moved beam end: a few objects rebuilt (" + n1 + ")");

            string sectionGuid = null;
            LinkFile.Update(linkPath, root =>
            {
                foreach (JsonValue e in root[LinkFile.RhinoSection]["lines"].ArrayValue)
                {
                    // Outside any diaphragm: a diaphragm is rebuilt whole when one of its members is (AxisVM cannot add to one).
                    if (sectionGuid != null || LinkFile.Get(e["attrs"], "Ax_Type") != "Rib" || LinkFile.Get(e["attrs"], "Ax_Diaphragm").Length > 0) continue;
                    e["attrs"].Set("Ax_Section", JsonValue.Str(LinkFile.Get(e["attrs"], "Ax_Section") == "45x360" ? "45x220" : "45x360"));
                    sectionGuid = LinkFile.Get(e, "guid");
                }
                root[LinkFile.RhinoSection].Set("sent", JsonValue.Str("2026-02-03T00:00:00.000Z"));
            });
            int n2 = step("new section on a rib");
            Check(n2 > 0 && n2 < 40, "new section: a few objects rebuilt (" + n2 + ")");

            LinkFile.Update(linkPath, root =>
            {
                JsonValue lines = root[LinkFile.RhinoSection]["lines"];
                for (int i = lines.Count - 1; i >= 0; i--)
                    if (LinkFile.Get(lines.At(i)["attrs"], "Ax_Type") == "Rib" && LinkFile.Get(lines.At(i), "guid") != sectionGuid) { lines.ArrayValue.RemoveAt(i); break; }
                root[LinkFile.RhinoSection].Set("sent", JsonValue.Str("2026-02-04T00:00:00.000Z"));
            });
            int n3 = step("rib deleted in Rhino");
            Check(n3 >= 0 && n3 < 40 && lastPlan.Deleted == 1, "deleted rib: gone, a few neighbours rebuilt (" + n3 + ")");

            AddonLoad(linkPath, notes);
            Check(lastPlan != null && lastPlan.Build.Count == 0 && lastPlan.Counts().Total == 0, "load again: nothing to do");

            // ---- members assembled in AxisVM across two Rhino curves -------------------------------------------
            string[] pair = CollinearPair(linkPath);
            Check(pair != null, "assembled: found two collinear curves meeting end to end");
            if (pair != null)
            {
                List<int> joined = LineIdsOf(pair[0]).Concat(LineIdsOf(pair[1])).ToList();
                int membersBefore = model.Members.Count;
                int m = model.Members.Add(IdArray(joined));
                Check(m > 0 && MembersOf(pair[0]).SetEquals(MembersOf(pair[1])) && MembersOf(pair[0]).Count == 1,
                    "assembled: AxisVM joined the two curves' " + joined.Count + " lines into one member (" + m + ", members " + membersBefore + " -> " + model.Members.Count + ")");
                int loadsBefore = model.Loads.Count;
                LinkFile.Update(linkPath, root =>
                {
                    foreach (JsonValue e in root[LinkFile.RhinoSection]["lines"].ArrayValue)
                        if (LinkFile.Get(e, "guid") == pair[0]) e["attrs"].Set("Ax_Section", JsonValue.Str(LinkFile.Get(e["attrs"], "Ax_Section") == "45x360" ? "45x220" : "45x360"));
                    root[LinkFile.RhinoSection].Set("sent", JsonValue.Str("2026-02-05T00:00:00.000Z"));
                });
                step("assembled pair, one changed");
                Check(lastPlan != null && lastPlan.Build.Contains(pair[0]) && lastPlan.Build.Contains(pair[1]),
                    "assembled: the other curve of the AxisVM member is rebuilt with the changed one");
                Check(model.Loads.Count == loadsBefore, "assembled: no load lost or doubled (" + loadsBefore + " -> " + model.Loads.Count + ")");
            }

            // ---- a member disassembled in AxisVM ---------------------------------------------------------------
            string multi = MultiLineCurve(linkPath);
            Check(multi != null, "disassembled: found a curve that is one member of several lines");
            if (multi != null)
            {
                int member = MembersOf(multi).First(), lines = LineIdsOf(multi).Count;
                model.Members.Delete(member);
                Console.WriteLine("      disassembled member " + member + ": its " + lines + " lines are now in " + MembersOf(multi).Count + " member(s)");
                string before = Counts();
                AddonLoad(linkPath, notes);
                Check(lastPlan != null && lastPlan.Build.Count == 0 && Counts() == before, "disassembled: a load with no Rhino change keeps AxisVM's edit");
                MoveCurveEnd(linkPath, e => LinkFile.Get(e, "guid") == multi, 0.05, "2026-02-07T00:00:00.000Z");
                step("disassembled curve moved in Rhino");
                // Not necessarily one: the moved end can make it overlap another curve; the full-rebuild comparison below
                // checks it is assembled exactly as a fresh import would.
                Check(MembersOf(multi).Count < lines, "disassembled: rebuilt from Rhino it is assembled again (" + MembersOf(multi).Count + " member(s) for " + LineIdsOf(multi).Count + " lines)");
            }

            // ---- full rebuilds of the same Rhino states, in a fresh AxisVM -------------------------------------
            try { app.AskCloseOnLastReleased = ELongBoolean.lbFalse; app.Quit(); } catch { }
            AxisVmDialogGuard.WaitClosed(50);
            app = AxisVmDialogGuard.Launch();
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            foreach (string[] st in states)
            {
                model = AxisVmDialogGuard.FreshModel(app);
                AddonLoad(st[1], new List<string>(), false);
                string refCounts = Counts(), refShape = Shape();
                bool same = Without(st[2], "members") == Without(refCounts, "members") && st[3] == refShape;
                if (!same)
                {
                    Console.WriteLine("      " + st[0] + " - update:       " + st[2]);
                    Console.WriteLine("      " + st[0] + " - full rebuild: " + refCounts);
                    string[] a = st[3].Split('\n'), b = refShape.Split('\n');
                    foreach (string x in a.Except(b).Take(6)) Console.WriteLine("        only in update:  " + x);
                    foreach (string x in b.Except(a).Take(6)) Console.WriteLine("        only in rebuild: " + x);
                }
                Check(same, st[0] + ": the update equals a full rebuild of the same Rhino model");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All only-changes checks passed." : failures + " only-changes check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    /// <summary>
    /// What the element calls Phase 4 needs actually take: line-to-line links (RLLLinkElementRec's MasterStartLink /
    /// MasterEndLink), beam / rib elastic foundations and surface supports - and how each reads back.
    /// </summary>
    private static int ProbeElements()
    {
        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Func<double, double, double, int> node = (x, y, z) => model.Nodes.Add(x, y, z);
            Func<int, int, int> line = (a, b) => { RLineGeomData g = new RLineGeomData(); return model.Lines.Add(a, b, ELineGeomType.lgtStraightLine, ref g); };
            int mat = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "S 235");
            Console.WriteLine("      material " + mat);
            int sec = model.CrossSections.AddRectangular("P200", 0.2, 0.2, ECrossSectionProcess.cspRolled);
            Func<int, bool, int> beam = (l, asRib) => { RPoint3d e1 = new RPoint3d(), e2 = new RPoint3d(); return asRib ? model.Lines.Item[l].DefineAsRib(mat, sec, sec, ref e1, ref e2) : model.Lines.Item[l].DefineAsBeam(mat, sec, sec, ref e1, ref e2); };

            // Two parallel beams 1 m apart; a third pair running the other way.
            int m1 = line(node(0, 0, 0), node(5, 0, 0)), s1 = line(node(0, 1, 0), node(5, 1, 0));
            int m2 = line(node(0, 3, 0), node(5, 3, 0)), s2 = line(node(5, 4, 0), node(0, 4, 0));
            foreach (int l in new[] { m1, s1, m2, s2 }) beam(l, false);
            Console.WriteLine("      lines " + m1 + "," + s1 + "," + m2 + "," + s2 + "; slave nodes " + model.Lines.Item[s1].StartNode + "->" + model.Lines.Item[s1].EndNode);

            Func<int, int, int, int, string> ll = (master, slave, a, b) =>
            {
                RLLLinkElementRec r = new RLLLinkElementRec
                {
                    MasterLine = master, SlaveLine = slave, MasterStartLink = a, MasterEndLink = b,
                    PositionType = EBeamRibDistributionType.brdtLength, Position = 0,
                    Stiffnesses = new RStiffnesses { x = 1e10, y = 1e10, z = 1e10, xx = 1e10, yy = 1e10, zz = 1e10 },
                    Resistances = new RResistances(), NonLinearity = new RNonLinearity()
                };
                int rc;
                try { rc = model.LinkElements.AddLL(ref r); } catch (Exception ex) { return "exception " + ex.Message; }
                string back = "";
                if (rc > 0)
                {
                    RLinkElementRec got = new RLinkElementRec();
                    try
                    {
                        model.LinkElements.GetRec(rc, ref got);
                        back = string.Format(" -> type {0}, master {1} slave {2} startLink {3} endLink {4}", model.LinkElements.get_LinkElementType(rc),
                            got.LLLinkElementRec.MasterLine, got.LLLinkElementRec.SlaveLine, got.LLLinkElementRec.MasterStartLink, got.LLLinkElementRec.MasterEndLink);
                    }
                    catch (Exception ex) { back = " (GetRec: " + ex.Message + ")"; }
                }
                return "rc " + rc + back;
            };
            // The link lines: plain lines joining the master's ends to the slave's ends.
            Func<int, int, int[]> ends = (a, b) => new[] { model.Lines.Item[a].StartNode, model.Lines.Item[a].EndNode, model.Lines.Item[b].StartNode, model.Lines.Item[b].EndNode };
            int[] q1 = ends(m1, s1);
            int c1 = line(q1[0], q1[2]), c2 = line(q1[1], q1[3]);
            Console.WriteLine("      AddLL with link lines " + c1 + "," + c2 + ": " + ll(m1, s1, c1, c2) + "; lines now " + model.Lines.Count);
            int[] q2 = ends(m2, s2);   // s2 runs the other way: master start meets slave END
            int c3 = line(q2[0], q2[3]), c4 = line(q2[1], q2[2]);
            Console.WriteLine("      AddLL reversed slave:   " + ll(m2, s2, c3, c4));
            Console.WriteLine("      links now: " + model.LinkElements.Count + "; link line types " + LinkState.LineTypeName(model.Lines.Item[c1].LineType) + "/" + (int)model.Lines.Item[c1].LineType);

            // Elastic foundations on a beam and on a rib, and a surface support on a domain.
            RStiffnessesXYZ k = new RStiffnessesXYZ { x = 1000, y = 1000, z = 5000 };
            RNonLinearityXYZ nl = new RNonLinearityXYZ();
            RResistancesXYZ rs = new RResistancesXYZ();
            int fb = line(node(0, 6, 0), node(5, 6, 0)); beam(fb, false);
            Console.WriteLine("      beam foundation:  rc " + Try(() => model.LineSupports.AddBeamElasticFoundation(fb, ref k, ref nl, ref rs)) + ", line supports " + model.LineSupports.Count);

            int[] c = { node(0, 10, 0), node(5, 10, 0), node(5, 15, 0), node(0, 15, 0) };
            int[] dl = { line(c[0], c[1]), line(c[1], c[2]), line(c[2], c[3]), line(c[3], c[0]) };
            RSurfaceAttr sa = new RSurfaceAttr { SurfaceType = ESurfaceType.stShell, Thickness = 0.2, MaterialId = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C25/30") };
            int dom = model.Domains.Add(IdArray(dl.ToList()), ref sa);
            int rib = line(node(1, 12, 0), node(4, 12, 0)); beam(rib, true);
            Console.WriteLine("      domain " + dom + "; rib in it typed: " + LinkState.LineTypeName(model.Lines.Item[rib].LineType));
            Console.WriteLine("      rib foundation:   rc " + Try(() => model.LineSupports.AddRibElasticFoundation(rib, ref k, ref nl, ref rs)) + ", line supports " + model.LineSupports.Count);
            Console.WriteLine("      domain foundation: rc " + Try(() => model.Domains.Item[dom].SetElasticFoundation(ref k, ref nl, ref rs)) + ", exists " + model.Domains.Item[dom].ElasticFoundationExists);
            RStiffnessesXYZ kb = new RStiffnessesXYZ();
            model.Domains.Item[dom].GetElasticFoundation(ref kb, ref nl, ref rs);
            Console.WriteLine("        reads back " + kb.x + "," + kb.y + "," + kb.z);
            int ribEdge = dl[0]; beam(ribEdge, true);
            Console.WriteLine("      edge rib typed " + LinkState.LineTypeName(model.Lines.Item[ribEdge].LineType) + "; rib foundation on an edge rib: rc " + Try(() => model.LineSupports.AddRibElasticFoundation(ribEdge, ref k, ref nl, ref rs)));
            for (int i = 1; i <= model.LineSupports.Count; i++) Console.WriteLine("        line support " + i + " on line " + model.LineSupports.get_LineId(i));
            return 0;
        }
        finally { Quit(); }
    }

    private static JsonValue Curve(string guid, double[] a, double[] b, params string[] attrs)
    {
        JsonValue at = JsonValue.Obj().Set("Ax_Guid", JsonValue.Str(guid));
        for (int i = 0; i + 1 < attrs.Length; i += 2) at.Set(attrs[i], JsonValue.Str(attrs[i + 1]));
        return JsonValue.Obj().Set("guid", JsonValue.Str(guid)).Set("geom", JsonValue.Str("line"))
            .Set("start", JsonValue.Xyz(a[0], a[1], a[2])).Set("end", JsonValue.Xyz(b[0], b[1], b[2])).Set("attrs", at);
    }

    /// <summary>
    /// What Rhino's AxLink "Load from AxisVM" does to the rhino section (AxLinkSync.Load, in metres): changed entries
    /// update their object (attributes verbatim, "" removes a key, a load family is replaced whole), new ones are created
    /// with the same id, deleted ones go; then Rhino sends again.
    /// </summary>
    private static int RhinoLoadsAxisVm(string linkPath, string sent)
    {
        int created = 0;
        LinkFile.Update(linkPath, root =>
        {
            JsonValue ax = root[LinkFile.AxisVmSection], rh = root[LinkFile.RhinoSection];
            JsonValue seen = rh["seenAxisvmGuids"] ?? JsonValue.Arr();
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                if (rh[list] == null) rh.Set(list, JsonValue.Arr());
                foreach (JsonValue e in ax[list].ArrayValue)
                {
                    if (!Changed(e)) continue;
                    string guid = LinkFile.Get(e, "guid");
                    JsonValue target = rh[list].ArrayValue.FirstOrDefault(x => LinkFile.Get(x, "guid") == guid);
                    if (target == null)
                    {
                        if (e["new"] == null || !e["new"].BoolValue) continue;
                        target = JsonValue.Obj().Set("guid", JsonValue.Str(guid)).Set("attrs", JsonValue.Obj().Set("Ax_Guid", JsonValue.Str(guid)));
                        rh[list].Add(target);
                        seen.Add(JsonValue.Str(guid));
                        created++;
                    }
                    if (e["geomChanged"] == null || e["geomChanged"].BoolValue)
                        foreach (string k in new[] { "geom", "xyz", "start", "end", "arcMid", "points", "contour", "holes" })
                            if (e[k] != null) target.Set(k, e[k]); else if (k == "points" || k == "arcMid" || k == "holes") target.ObjectValue.Remove(k);
                    JsonValue attrs = target["attrs"];
                    var families = new HashSet<string>(e["attrs"].ObjectValue.Keys.Select(AxKeys.BaseKey).Where(k => k == "Ax_DistLoad" || k == "Ax_PointLoad" || k == "Ax_NodalLoad"));
                    foreach (string k in attrs.ObjectValue.Keys.ToList()) if (families.Contains(AxKeys.BaseKey(k))) attrs.ObjectValue.Remove(k);
                    foreach (KeyValuePair<string, JsonValue> kv in e["attrs"].ObjectValue)
                        if (kv.Value.AsString("").Length == 0) attrs.ObjectValue.Remove(kv.Key); else attrs.Set(kv.Key, kv.Value);
                }
            }
            foreach (JsonValue g in (ax["deleted"] ?? JsonValue.Arr()).ArrayValue)
                foreach (string list in new[] { "points", "lines", "domains" }) rh[list].ArrayValue.RemoveAll(x => LinkFile.Get(x, "guid") == g.AsString(""));
            rh.Set("seenAxisvmGuids", seen).Set("basedOnAxisvm", JsonValue.Str(LinkFile.Get(ax, "sent"))).Set("sent", JsonValue.Str(sent));
        });
        return created;
    }

    private static int LineLinks() { int n = 0; for (int i = 1; i <= model.LinkElements.Count; i++) if (model.LinkElements.get_LinkElementType(i) == ELinkElementType.letLL) n++; return n; }

    private static int LinkLineCount(string slave)
    {
        int n = 0;
        for (int i = 1; i <= model.Lines.Count; i++) if (LinkState.Guids(model.Lines.Attachments, i).Contains(slave + AxisModelBuilder.LinkLineSuffix)) n++;
        return n;
    }

    /// <summary>
    /// The elements added in 2026-10 (mode "elements"): a line-to-line link, an elastic foundation along a beam, a surface
    /// support under a slab, an edge support - built from a small Rhino model, then updated with only changes.
    /// </summary>
    private static int ElementsMain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        Directory.CreateDirectory(dir);
        string linkPath = Path.Combine(dir, "elements.axlink.json");
        JsonValue lines = JsonValue.Arr()
            .Add(Curve("m-beam", new[] { 0.0, 0, 3 }, new[] { 6.0, 0, 3 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235"))
            .Add(Curve("s-beam", new[] { 0.0, 1, 3 }, new[] { 6.0, 1, 3 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_LineLink", "m-beam"))
            .Add(Curve("f-beam", new[] { 0.0, 4, 0 }, new[] { 6.0, 4, 0 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_Support", "1000,1000,5000,0,0,0"))
            .Add(Curve("edge", new[] { 0.0, 10, 0 }, new[] { 6.0, 10, 0 }, "Ax_Type", "Simple", "Ax_Support", "pinned"))
            .Add(Curve("d1", new[] { 0.0, 20, 6 }, new[] { 6.0, 20, 6 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_Diaphragm", "DX"))
            .Add(Curve("d2", new[] { 6.0, 20, 6 }, new[] { 6.0, 26, 6 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_Diaphragm", "DX"))
            .Add(Curve("d3", new[] { 6.0, 26, 6 }, new[] { 0.0, 20, 6 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_Diaphragm", "DX"))
            .Add(Curve("d4", new[] { 20.0, 20, 6 }, new[] { 26.0, 20, 6 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235", "Ax_Diaphragm", "DX"));
        JsonValue slab = JsonValue.Obj().Set("guid", JsonValue.Str("slab"))
            .Set("contour", JsonValue.Arr().Add(JsonValue.Xyz(0, 10, 0)).Add(JsonValue.Xyz(6, 10, 0)).Add(JsonValue.Xyz(6, 16, 0)).Add(JsonValue.Xyz(0, 16, 0)))
            .Set("attrs", JsonValue.Obj().Set("Ax_Guid", JsonValue.Str("slab")).Set("Ax_FEType", JsonValue.Str("Shell")).Set("Ax_Thickness", JsonValue.Str("0.2"))
                .Set("Ax_Material", JsonValue.Str("C25/30")).Set("Ax_SurfaceSupport", JsonValue.Str("5000")));
        JsonValue rh = JsonValue.Obj().Set("schema", JsonValue.Num(2)).Set("source", JsonValue.Obj().Set("unitSystem", JsonValue.Str("Meters")).Set("tolerance", JsonValue.Num(0.001)))
            .Set("points", JsonValue.Arr()).Set("lines", lines).Set("domains", JsonValue.Arr().Add(slab)).Set("sent", JsonValue.Str("2026-03-01T00:00:00.000Z"));
        File.WriteAllText(linkPath, JsonValue.Obj().Set("link", JsonValue.Num(1)).Set(LinkFile.RhinoSection, rh).ToJson());

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            var notes = new List<string>();
            ImportStats s = AddonLoad(linkPath, notes);
            foreach (string n in notes) Console.WriteLine("      note: " + n);
            Check(LineLinks() == 1 && LinkLineCount("s-beam") == 2, "line link: one line-to-line link with its two link lines (" + LineLinks() + ", " + LinkLineCount("s-beam") + ")");
            Check(C(() => model.LineSupports.Count) >= 2 && s.LineSupportsCreated >= 2, "supports: a foundation along the beam and an edge support (" + s.LineSupportsCreated + " line supports)");
            Check(model.Domains.Count == 1 && model.Domains.Item[1].ElasticFoundationExists == ELongBoolean.lbTrue, "surface support: the slab sits on an elastic foundation");
            RStiffnessesXYZ k = new RStiffnessesXYZ(); RNonLinearityXYZ nl = new RNonLinearityXYZ(); RResistancesXYZ rs = new RResistancesXYZ();
            model.Domains.Item[1].GetElasticFoundation(ref k, ref nl, ref rs);
            Check(Math.Abs(k.z - 5000) < 1e-6 && k.x == 0, "surface support: kz = 5000 kN/m/m² (" + k.x + "," + k.y + "," + k.z + ")");
            SendResult r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0, "send after load: nothing changed, the link lines are not sent as curves (" + r.Changed + "/" + r.Adopted + ")");

            // The master beam changes section: the link goes with it and comes back, once.
            Func<string, Action<JsonValue>, int> change = (guid, edit) =>
            {
                LinkFile.Update(linkPath, root =>
                {
                    foreach (JsonValue e in root[LinkFile.RhinoSection]["lines"].ArrayValue.Concat(root[LinkFile.RhinoSection]["domains"].ArrayValue))
                        if (LinkFile.Get(e, "guid") == guid) edit(e["attrs"]);
                    root[LinkFile.RhinoSection].Set("sent", JsonValue.Str(LinkFile.Now()));
                });
                notes.Clear();
                AddonLoad(linkPath, notes);
                foreach (string n in notes) Console.WriteLine("      note: " + n);
                return lastPlan == null ? -1 : lastPlan.Build.Count;
            };
            int n1 = change("m-beam", a => a.Set("Ax_Section", JsonValue.Str("IPE 330")));
            Check(lastPlan.Build.Contains("s-beam") && LineLinks() == 1 && LinkLineCount("s-beam") == 2,
                "master changed: the slave and its link are rebuilt, still one link with two link lines (" + n1 + " rebuilt, " + LineLinks() + " link, " + LinkLineCount("s-beam") + " lines)");
            int n2 = change("s-beam", a => a.ObjectValue.Remove("Ax_LineLink"));
            Check(LineLinks() == 0 && LinkLineCount("s-beam") == 0, "link removed in Rhino: the link and its link lines are gone (" + LineLinks() + ", " + LinkLineCount("s-beam") + ")");
            change("slab", a => a.Set("Ax_SurfaceSupport", JsonValue.Str("1000,1000,8000")));
            model.Domains.Item[1].GetElasticFoundation(ref k, ref nl, ref rs);
            Check(Math.Abs(k.z - 8000) < 1e-6 && Math.Abs(k.x - 1000) < 1e-6, "surface support changed in Rhino: now 1000,1000,8000 (" + k.x + "," + k.y + "," + k.z + ")");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the updates: nothing changed (" + r.Changed + "/" + r.Adopted + "/" + r.Deleted + ")");

            // ---- AxisVM -> Rhino: supports, releases, hinges, surface supports made or changed in AxisVM -------
            var springs = new SpringParamResolver(model, new List<string>());
            RSpringParamIndexes rigid = new RSpringParamIndexes { x = springs.Translational(1e10), y = springs.Translational(1e10), z = springs.Translational(1e10) };
            RNodalSupportSpringParams nsp = new RNodalSupportSpringParams { SpringParamIndexes = rigid };
            int freeNode = model.Nodes.Add(20, 0, 0);
            Check(model.NodalSupports.AddNodalGlobal_V153(ref nsp, freeNode) > 0, "axisvm edit: a pinned support at a new node");

            int ml = LineIdsOf("m-beam")[0];
            RLineAttr_V161 mv = new RLineAttr_V161();
            {
                Array one = IdArray(new List<int> { ml }), got;
                model.Lines.BulkGetAttr_V161(one, out got);
                mv = (RLineAttr_V161)got.GetValue(got.GetLowerBound(0));
                RReleases_V161 rel = mv.StartRelease;
                rel.yy = new RRelease_V161 { ReleaseType = EReleaseType.rtHinged };
                rel.zz = new RRelease_V161 { ReleaseType = EReleaseType.rtHinged };
                mv.StartRelease = rel;
                Array rec = Array.CreateInstance(typeof(RLineAttr_V161), new[] { 1 }, new[] { 1 }); rec.SetValue(mv, 1);
                Check(model.Lines.BulkSetAttr_V161(one, rec) > 0, "axisvm edit: the master beam is hinged at one end");
            }

            int edgeLine = LineIdsOf("edge")[0];
            REdgeConnectionRec_V172 hr = new REdgeConnectionRec_V172 { LineId = edgeLine, DomainId = 1,
                SpringParamIndexes = new RSpringParamIndexes { x = springs.LineTranslational(1e6), y = springs.LineTranslational(1e6), z = springs.LineTranslational(1e6) } };
            Check(model.EdgeConnections.Add_V172(ref hr) > 0, "axisvm edit: an edge hinge along the slab edge");
            k = new RStiffnessesXYZ { x = 0, y = 0, z = 3000 };
            model.Domains.Item[1].SetElasticFoundation(ref k, ref nl, ref rs);

            r = RhinoLinkSender.Send(model, linkPath);
            JsonValue sec = Section(linkPath);
            Console.WriteLine(string.Format("      send: {0} changed, {1} adopted", r.Changed, r.Adopted));
            foreach (string list in new[] { "points", "lines", "domains" })
                foreach (JsonValue e in sec[list].ArrayValue) if (Changed(e)) Console.WriteLine("        " + list + ": " + e["attrs"].ToJson().Replace("\r\n", " ").Replace("  ", ""));
            JsonValue pt = sec["points"].ArrayValue.FirstOrDefault(e => Changed(e) && e["new"].BoolValue);
            Check(pt != null && LinkFile.Get(pt["attrs"], "Ax_Support").StartsWith("1E+10,1E+10,1E+10,0,0,0"), "send: the new support arrives as a Rhino point with Ax_Support");
            JsonValue mb = Entry(sec, "lines", "m-beam");
            Check(Changed(mb) && LinkFile.Get(mb["attrs"], "Ax_Release").Length > 0 && !mb["attrs"].ObjectValue.ContainsKey("Ax_Section"),
                "send: the master beam carries only its new Ax_Release (" + LinkFile.Get(mb["attrs"], "Ax_Release") + ")");
            JsonValue eh = sec["lines"].ArrayValue.FirstOrDefault(e => LinkFile.Get(e["attrs"], "Ax_Type") == "EdgeHinge");
            Check(eh != null && LinkFile.Get(eh["attrs"], "Ax_HingeDomains") == "slab" && eh["new"].BoolValue, "send: the edge hinge arrives as a new EdgeHinge curve on 'slab'");
            JsonValue sl = Entry(sec, "domains", "slab");
            Check(Changed(sl) && LinkFile.Get(sl["attrs"], "Ax_SurfaceSupport") == "3000", "send: the slab's new surface support (" + LinkFile.Get(sl["attrs"], "Ax_SurfaceSupport") + ")");
            Check(Entry(sec, "lines", "edge") != null && !Changed(Entry(sec, "lines", "edge")), "send: the edge line itself is unchanged (its hinge is a curve of its own)");

            // Rhino loads all that and sends back; AxisVM loads with only changes: AxisVM's edits are kept as they are.
            int createdInRhino = RhinoLoadsAxisVm(linkPath, LinkFile.Now());
            string beforeEcho = Counts();
            notes.Clear();
            AddonLoad(linkPath, notes);
            foreach (string n in notes) Console.WriteLine("      note: " + n);
            Check(createdInRhino == 2 && Counts() == beforeEcho, "echo: Rhino created the point and the hinge curve; loading its model changes no count (" + beforeEcho + " vs " + Counts() + ")");
            // The beam and the slab AxisVM changed, plus the slab's edge hinge curve and the edge line it runs along (a
            // rebuilt domain loses its edge hinges, so the hinge curve is rebuilt with it).
            Check(lastPlan != null && lastPlan.Build.Count <= 4, "echo: only the elements AxisVM changed (and what hangs on them) are rebuilt (" + (lastPlan == null ? -1 : lastPlan.Build.Count) + ")");
            model.Domains.Item[1].GetElasticFoundation(ref k, ref nl, ref rs);
            Check(Math.Abs(k.z - 3000) < 1e-6 && C(() => model.EdgeConnections.Count) == 1 && C(() => model.NodalSupports.Count) == 1,
                "echo: surface support 3000, the edge hinge and the nodal support all still there");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the echo: nothing changed (" + r.Changed + "/" + r.Adopted + "/" + r.Deleted + ")");

            // Rhino removes a support: it must go, not be carried back.
            int supportsBefore = model.LineSupports.Count;
            change("f-beam", a => a.ObjectValue.Remove("Ax_Support"));
            Check(model.LineSupports.Count == supportsBefore - 1, "support removed in Rhino: gone from AxisVM too (" + supportsBefore + " -> " + model.LineSupports.Count + ")");

            // An AxisVM-made foundation on a beam that a new Rhino beam then crosses: it goes onto both pieces.
            {
                int slave = LineIdsOf("s-beam")[0];
                RStiffnessesXYZ kf = new RStiffnessesXYZ { x = 0, y = 0, z = 4000 };
                Check(model.LineSupports.AddBeamElasticFoundation(slave, ref kf, ref nl, ref rs) > 0, "axisvm edit: a foundation under s-beam");
                LinkFile.Update(linkPath, root =>
                {
                    root[LinkFile.RhinoSection]["lines"].Add(Curve("x-beam", new[] { 3.0, -1, 3 }, new[] { 3.0, 2, 3 }, "Ax_Type", "Beam", "Ax_Section", "IPE 300", "Ax_Material", "S 235"));
                    root[LinkFile.RhinoSection].Set("sent", JsonValue.Str(LinkFile.Now()));
                });
                notes.Clear();
                AddonLoad(linkPath, notes);
                foreach (string n in notes.Where(n => n.StartsWith("Update"))) Console.WriteLine("      note: " + n);
                List<int> pieces = LineIdsOf("s-beam");
                int supported = 0;
                for (int i = 1; i <= model.LineSupports.Count; i++) if (pieces.Contains(model.LineSupports.get_LineId(i))) supported++;
                Check(pieces.Count == 2 && supported == 2, "split: s-beam is now 2 lines and both carry its AxisVM foundation (" + pieces.Count + " lines, " + supported + " supported)");
            }

            // A diaphragm member moved out of the plane in Rhino: the other members keep their diaphragm.
            {
                int before = DiaphragmLines();
                LinkFile.Update(linkPath, root =>
                {
                    foreach (JsonValue e in root[LinkFile.RhinoSection]["lines"].ArrayValue)
                        if (LinkFile.Get(e, "guid") == "d4") e.Set("end", JsonValue.Xyz(26, 20, 8));
                    root[LinkFile.RhinoSection].Set("sent", JsonValue.Str(LinkFile.Now()));
                });
                notes.Clear();
                AddonLoad(linkPath, notes);
                foreach (string n in notes.Where(n => n.Contains("diaphragm"))) Console.WriteLine("      note: " + n);
                var inDia = new HashSet<int>();
                for (int d = 1; d <= model.Diaphragm.Count; d++) { Array a; model.Diaphragm.GetLines(d, out a); foreach (int x in LinkState.IntList(a)) inDia.Add(x); }
                Check(new[] { "d1", "d2", "d3" }.All(g => LineIdsOf(g).All(inDia.Contains)) && before > 0,
                    "diaphragm: a member leaving the plane does not cost the others their diaphragm (" + before + " -> " + DiaphragmLines() + " lines)");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All element checks passed." : failures + " element check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally { Quit(); }
    }

    private static string Try(Func<int> f) { try { return f().ToString(); } catch (Exception ex) { return "exception " + ex.Message; } }

    /// <summary>
    /// What re-creating a rigid diaphragm does to the typed members in it: delete diaphragm 1 and add it again over the
    /// same lines, then put the line attributes back - do members, member loads, releases and eccentricities survive?
    /// </summary>
    private static int ProbeDiaphragm(string link)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        string linkPath = Path.Combine(dir, "probe" + Path.GetExtension(link));
        File.Copy(link, linkPath, true);
        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            if (link.EndsWith(".axs", StringComparison.OrdinalIgnoreCase)) model.LoadFromFile(linkPath);
            else AddonLoad(linkPath, new List<string>(), false);

            // The diaphragm whose lines carry the most loads.
            var loadsOn = new Dictionary<int, int>();
            for (int i = 1; i <= model.Loads.Count; i++) { int e = model.Loads.get_ElementId(i); int c; loadsOn.TryGetValue(e, out c); loadsOn[e] = c + 1; }
            int pick = 1, most = -1;
            for (int d = 1; d <= model.Diaphragm.Count; d++)
            {
                Array l; model.Diaphragm.GetLines(d, out l);
                int c = 0; foreach (int x in LinkState.IntList(l)) { int k; if (loadsOn.TryGetValue(x, out k)) c += k; }
                if (c > most) { most = c; pick = d; }
            }
            Console.WriteLine("      diaphragm " + pick + " (" + most + " loads with its line ids as element)");
            var types = new Func<string>(() =>
            {
                var t = new SortedDictionary<string, int>();
                for (int i = 1; i <= model.Loads.Count; i++) { string k = model.Loads.get_LoadType(i).ToString(); int c; t.TryGetValue(k, out c); t[k] = c + 1; }
                return string.Join(", ", t.Select(kv => kv.Key + " " + kv.Value));
            });
            Console.WriteLine("      load types: " + types());
            Array arr;
            model.Diaphragm.GetLines(pick, out arr);
            List<int> ids = LinkState.IntList(arr);
            int dof;
            model.Diaphragm.GetDOF(pick, out dof);
            Array a1 = Array.CreateInstance(typeof(int), new[] { ids.Count }, new[] { 1 });
            for (int i = 0; i < ids.Count; i++) a1.SetValue(ids[i], i + 1);
            Array attrs;
            model.Lines.BulkGetAttr_V161(a1, out attrs);
            // The members those lines are in, and their records (member-level restore).
            Array mids0;
            model.Lines.BulkGetMemberIds(a1, out mids0);
            List<int> memberIds = LinkState.IntList(mids0).Where(m => m > 0).Distinct().ToList();
            Array m1 = Array.CreateInstance(typeof(int), new[] { memberIds.Count }, new[] { 1 });
            for (int i = 0; i < memberIds.Count; i++) m1.SetValue(memberIds[i], i + 1);
            Array memberRecs;
            int gotMembers = model.Members.BulkGetMembers_V161(m1, out memberRecs);
            Console.WriteLine("      " + memberIds.Count + " members hold those lines (records read: " + gotMembers + ")");
            Func<string> state = () =>
            {
                Array at;
                model.Lines.BulkGetAttr(a1, out at);
                int typed = 0; foreach (object o in at) if (LinkState.LineTypeName(((RLineAttr)o).LineType).Length > 0) typed++;
                Array mids; model.Lines.BulkGetMemberIds(a1, out mids);
                int inMember = 0; foreach (object o in mids) if (Convert.ToInt32(o) > 0) inMember++;
                return string.Format("diaphragms {0}, {1} of {2} lines typed, {3} in a member; {4}", model.Diaphragm.Count, typed, ids.Count, inMember, Counts());
            };
            Console.WriteLine("      before:   " + state());
            Console.WriteLine("      delete:   rc " + model.Diaphragm.Delete(pick) + " -> " + state());
            Console.WriteLine("      add:      rc " + model.Diaphragm.Add(dof, a1) + " -> " + state());
            if (Environment.GetEnvironmentVariable("RT_MEMBERS") == "1")
            {
                Console.WriteLine("      member record type: " + memberRecs.GetValue(memberRecs.GetLowerBound(0)).GetType().FullName);
                Array fresh = Array.CreateInstance(typeof(RLineAttr_V161), new[] { memberIds.Count }, new[] { 1 });
                int mlo = memberRecs.GetLowerBound(0);
                for (int i = 0; i < memberIds.Count; i++) fresh.SetValue((RLineAttr_V161)memberRecs.GetValue(mlo + i), i + 1);
                Console.WriteLine("      member restore: rc " + model.Members.BulkSetMembers_V161(m1, fresh) + " -> " + state());
                Console.WriteLine("      load types: " + types());
                return 0;
            }
            Array plain;
            model.Lines.BulkGetAttr(a1, out plain);   // stripped now; the V161 records hold the originals
            int ok = 0, okPlain = 0, shown = 0;
            int lo = attrs.GetLowerBound(0);
            for (int i = 0; i < ids.Count; i++)
            {
                Array one = Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 1 }); one.SetValue(ids[i], 1);
                RLineAttr_V161 v = (RLineAttr_V161)attrs.GetValue(lo + i);
                Array rec = Array.CreateInstance(typeof(RLineAttr_V161), new[] { 1 }, new[] { 1 }); rec.SetValue(v, 1);
                int rc = model.Lines.BulkSetAttr_V161(one, rec);
                if (rc > 0) { ok++; continue; }
                if (shown++ < 6) Console.WriteLine(string.Format("        line {0}: rc {1}, type {2}, ecc {3}/{4}, 7dof {5}, orient {6}", ids[i], rc, v.LineType, v.StartEccentricityType, v.EndEccentricityType, v.Beam7DOF, v.LocalXOrientation));
                // The older record: type, material, sections only.
                RLineAttr pa = new RLineAttr { LineType = v.LineType, MaterialIndex = v.MaterialIndex, StartCrossSectionIndex = v.StartCrossSectionIndex, EndCrossSectionIndex = v.EndCrossSectionIndex };
                Array prec = Array.CreateInstance(typeof(RLineAttr), new[] { 1 }, new[] { 1 }); prec.SetValue(pa, 1);
                if (model.Lines.BulkSetAttr(one, prec) > 0) okPlain++;
            }
            Console.WriteLine(string.Format("      restore one by one: {0} with V161, {1} more with the plain record -> {2}", ok, okPlain, state()));
            Console.WriteLine("      load types: " + types());
            return 0;
        }
        finally { Quit(); }
    }

    /// <summary>"Load from Rhino" of a link file into an empty model (the first load), steps printed.</summary>
    private static int LoadMain(string link)
    {
        string linkPath = Path.Combine(Path.GetTempPath(), "nextra-roundtrip", "load-" + Path.GetFileName(link));
        File.Copy(link, linkPath, true);
        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            var notes = new List<string>();
            ImportStats s = AddonLoad(linkPath, notes, Environment.GetEnvironmentVariable("RT_FULL") != "1");
            Console.WriteLine("      members assembled from several segments: " + s.MembersAssembled);
            File.WriteAllLines(linkPath + ".notes.txt", notes);
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(25)) Console.WriteLine("      note: " + n);
            Console.WriteLine("      after load: " + Counts());
            Check(true, "load: AxisVM survived the first Load from Rhino");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    private static int RoundTripMain(string axs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "roundtrip-model.axs");
        File.Copy(axs, copy, true);
        string linkPath = Path.Combine(dir, "roundtrip.axlink.json");
        if (File.Exists(linkPath)) File.Delete(linkPath);

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Check(model.LoadFromFile(copy) == ELongBoolean.lbTrue && model.Lines.Count > 0, "open: a copy of " + Path.GetFileName(axs));
            string original = Counts();
            double originalHinged = HingedLength();
            originalLoads = LoadTypes();
            Console.WriteLine("      model: " + original);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            SendResult r = RhinoLinkSender.Send(model, linkPath);
            Console.WriteLine(string.Format("      send ({0:0.0} s): {1} changed, {2} unchanged, {3} adopted, {4} deleted", sw.Elapsed.TotalSeconds, r.Changed, r.Unchanged, r.Adopted, r.Deleted));
            Check(r.Adopted > 0, "send: everything drawn in AxisVM is adopted");

            RhinoTakesAxisVmSend(linkPath, "2026-01-01T00:00:00.000Z");
            LinkedItems offered = AxisModelBuilder.FindLinked(model, linkPath);
            Check(offered.Total > 0, "dialog: an AxisVM-first link offers 'Update what an earlier load built' (" + offered + ")");
            bool full = Environment.GetEnvironmentVariable("RT_FULL") == "1";
            var notes = new List<string>();
            sw.Restart();
            ImportStats s = AddonLoad(linkPath, notes, !full);
            Console.WriteLine(string.Format("      load ({0:0.0} s): replaced {1}", sw.Elapsed.TotalSeconds, s.Replaced));
            File.WriteAllLines(linkPath + ".notes.txt", notes);
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(25)) Console.WriteLine("      note: " + n);
            string loaded = Counts();
            Console.WriteLine("      after load: " + loaded);
            Check(true, "load: AxisVM survived Load from Rhino");
            if (!full)
            {
                Check(lastPlan != null && !lastPlan.Everything && lastPlan.Build.Count == 0 && lastPlan.Counts().Total == 0,
                    "only changes, Rhino echoed AxisVM's send unchanged: nothing is rebuilt");
                Check(loaded == original, "only changes: every count is as before (edge hinges, supports, mesh, parts kept)");
            }

            r = RhinoLinkSender.Send(model, linkPath);
            Console.WriteLine(string.Format("      send after load: {0} changed, {1} unchanged, {2} adopted, {3} deleted", r.Changed, r.Unchanged, r.Adopted, r.Deleted));
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after load: nothing changed");
            {
                JsonValue sec0 = Section(linkPath);
                int shown = 0;
                foreach (string list in new[] { "points", "lines", "domains" })
                    foreach (JsonValue e in sec0[list].ArrayValue)
                        if (Changed(e) && shown++ < 8) Console.WriteLine("      changed " + list + ": " + e.ToJson().Replace("\r\n", " ").Replace("  ", ""));
            }
            if (full) goto done;

            // ---- the user's change: one curve end moved 25 mm in Rhino ----------------------------------------
            string movedGuid = MoveCurveEnd(linkPath, e => LinkFile.Get(e["attrs"], "Ax_Type") == "Rib", 0.025, "2026-01-02T00:00:00.000Z");
            JsonValue movedEntry = null;
            foreach (JsonValue e in LinkFile.Read(linkPath)[LinkFile.RhinoSection]["lines"].ArrayValue) if (LinkFile.Get(e, "guid") == movedGuid) movedEntry = e;
            notes.Clear();
            sw.Restart();
            s = AddonLoad(linkPath, notes);
            Console.WriteLine(string.Format("      load after moving one rib end ({0:0.0} s): replaced {1}", sw.Elapsed.TotalSeconds, s.Replaced));
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(25)) Console.WriteLine("      note: " + n);
            string afterMove = Counts();
            Console.WriteLine("      after: " + afterMove);
            Check(lastPlan != null && lastPlan.Changed == 1 && lastPlan.Build.Count < 150,
                "move: only the moved rib and its neighbours are rebuilt (" + (lastPlan == null ? "-" : lastPlan.Build.Count.ToString()) + ")");
            // What the moved rib became: its lines, their member, type and diaphragm, and who else owns them.
            var lineDia = new Dictionary<int, int>();
            for (int d = 1; d <= model.Diaphragm.Count; d++) { Array a; model.Diaphragm.GetLines(d, out a); foreach (int x in LinkState.IntList(a)) lineDia[x] = d; }
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                List<string> own = LinkState.Guids(model.Lines.Attachments, i);
                if (!own.Contains(movedGuid)) continue;
                RLineAttr la; LinkState.TryLineAttr(model, i, out la);
                int dd; lineDia.TryGetValue(i, out dd);
                var surf = new List<int>();
                for (int dm = 1; dm <= model.Domains.Count; dm++) if (LinkState.ContourLines(model, dm).Contains(i)) surf.Add(dm);
                Console.WriteLine(string.Format("      moved rib line {0}: member {1}, type {2}, diaphragm {3}, owners {4}, link {5}, edge of domains {6}",
                    i, model.Lines.Item[i].MemberID, la.LineType, dd, own.Count, LinkState.Read(model.Lines.Attachments, LinkState.Link, i) != null, string.Join(",", surf)));
            }
            foreach (string n in notes.Where(n => n.Contains(movedGuid.Substring(0, 8)))) Console.WriteLine("      note on it: " + n);
            List<double[][]> movedNow = LinesOf(movedGuid);
            double[] wantEnd = { movedEntry["end"].At(0).AsDouble(0), movedEntry["end"].At(1).AsDouble(0), movedEntry["end"].At(2).AsDouble(0) };
            Check(movedNow.Count > 0 && movedNow.Any(l => l.Any(p => Math.Abs(p[0] - wantEnd[0]) + Math.Abs(p[1] - wantEnd[1]) + Math.Abs(p[2] - wantEnd[2]) < 1e-4)),
                "move: the rib now ends at its new point (" + movedNow.Count + " line(s))");
            // Rebuilt domains come back unmeshed, so fewer (longer) hinge lines: compare the hinged length, not the count.
            double hingedNow = HingedLength();
            Check(Math.Abs(hingedNow - originalHinged) <= 0.005 * originalHinged && C(() => model.NodalSupports.Count) == 48,
                string.Format("move: AxisVM's edge hinges and supports are kept ({0} hinges, {1:0.00} of {2:0.00} m hinged, {3} supports)",
                    C(() => model.EdgeConnections.Count), hingedNow, originalHinged, C(() => model.NodalSupports.Count)));
            Check(Without(afterMove, "nodes", "lines", "members", "edge hinges", "diaphragm lines", "loads") == Without(original, "nodes", "lines", "members", "edge hinges", "diaphragm lines", "loads"),
                "move: domains, panels, supports and parts are all still there");
            // The moved rib can now overlap another curve and no longer be one member: its member loads then go line by line.
            Check(Math.Abs(C(() => model.Loads.Count) - 1264) <= 4, "move: no loads lost (" + C(() => model.Loads.Count) + " vs 1264; a rib that is no longer one member takes its loads per line)");

            notes.Clear();
            AddonLoad(linkPath, notes);
            Check(lastPlan != null && lastPlan.Build.Count == 0 && Counts() == afterMove, "load again: nothing to do, nothing changes");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after that: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
            JsonValue sent = Section(linkPath);
            foreach (string list in new[] { "points", "lines", "domains" })
                foreach (JsonValue e in sent[list].ArrayValue)
                    if (Changed(e)) Console.WriteLine("      changed " + list + " " + LinkFile.Get(e, "guid") + (LinkFile.Get(e, "guid") == movedGuid ? " (the moved rib)" : "") + ": " + e.ToJson().Replace("\r\n", " ").Replace("  ", ""));
            Console.WriteLine("      load types now: " + LoadTypes() + "\n      before:         " + originalLoads);
        done:

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All round-trip checks passed." : failures + " round-trip check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    /// <summary>
    /// With a real Rhino (mode "rhino-echo"): AxisVM sends the model, then waits for "&lt;link&gt;.rhino1" - written once
    /// Rhino has run AxLink Load + Send on the file - and loads it (nothing may be rebuilt); then waits for "&lt;link&gt;.rhino2"
    /// (Rhino moved one curve and sent) and loads that (only it and its neighbours).
    /// </summary>
    private static int RhinoEchoMain(string axs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "echo-model.axs");
        File.Copy(axs, copy, true);
        string linkPath = Path.Combine(dir, "echo.axlink.json");
        foreach (string f in new[] { linkPath, linkPath + ".rhino1", linkPath + ".rhino2" }) if (File.Exists(f)) File.Delete(f);

        Func<string, bool> wait = flag =>
        {
            Console.WriteLine("      waiting for " + Path.GetFileName(flag));
            for (int i = 0; i < 3600 && !File.Exists(flag); i++) System.Threading.Thread.Sleep(1000);
            return File.Exists(flag);
        };

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Check(model.LoadFromFile(copy) == ELongBoolean.lbTrue && model.Lines.Count > 0, "open: a copy of " + Path.GetFileName(axs));
            string original = Counts();
            Console.WriteLine("      model: " + original);
            SendResult r = RhinoLinkSender.Send(model, linkPath);
            Console.WriteLine(string.Format("      send: {0} changed, {1} adopted", r.Changed, r.Adopted));
            Console.WriteLine("READY1 " + linkPath);

            var notes = new List<string>();
            if (!wait(linkPath + ".rhino1")) { Check(false, "Rhino answered"); goto done; }
            AddonLoad(linkPath, notes);
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(15)) Console.WriteLine("      note: " + n);
            Console.WriteLine("      " + (lastPlan == null ? "no plan" : lastPlan.Summary()));
            Check(lastPlan != null && !lastPlan.Everything && lastPlan.Build.Count == 0, "real Rhino echo: nothing is rebuilt");
            Check(Counts() == original, "real Rhino echo: every count is as before");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the echo: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
            Console.WriteLine("READY2");

            if (!wait(linkPath + ".rhino2")) { Check(false, "Rhino answered the move"); goto done; }
            notes.Clear();
            AddonLoad(linkPath, notes);
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(15)) Console.WriteLine("      note: " + n);
            Console.WriteLine("      " + (lastPlan == null ? "no plan" : lastPlan.Summary()));
            Check(lastPlan != null && lastPlan.Changed == 1 && lastPlan.Build.Count < 150, "real Rhino move: only the moved curve and its neighbours are rebuilt");
            Check(Without(Counts(), "nodes", "lines", "members", "edge hinges", "diaphragm lines", "loads") == Without(original, "nodes", "lines", "members", "edge hinges", "diaphragm lines", "loads"),
                "real Rhino move: domains, panels, supports and parts are all still there");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the move: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
        done:
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All Rhino echo checks passed." : failures + " Rhino echo check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    private static int LinkCount(ELinkElementType t) { int n = 0; for (int i = 1; i <= model.LinkElements.Count; i++) if (model.LinkElements.get_LinkElementType(i) == t) n++; return n; }

    private static int LinesOfType(ELineType t)
    {
        int n = 0;
        for (int i = 1; i <= model.Lines.Count; i++) { RLineAttr a; if (LinkState.TryLineAttr(model, i, out a) && a.LineType == t) n++; }
        return n;
    }

    private static Array OneBased(params int[] ids)
    {
        Array a = Array.CreateInstance(typeof(int), new[] { ids.Length }, new[] { 1 });
        for (int i = 0; i < ids.Length; i++) a.SetValue(ids[i], i + 1);
        return a;
    }

    /// <summary>
    /// Every element kind through a real Rhino (mode "rhino-elements"). Rhino builds the model and sends it to
    /// "&lt;link&gt;" then writes ".rhino1"; AxisVM loads, checks each kind, adds a spring and a rigid link and sends
    /// (READY2); Rhino loads, echoes (".rhino2"); AxisVM must rebuild nothing; Rhino changes the spring (".rhino3").
    /// </summary>
    private static int RhinoElementsMain(string unused)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextra-roundtrip");
        Directory.CreateDirectory(dir);
        string linkPath = Path.Combine(dir, "el.axlink.json");
        foreach (string f in new[] { linkPath, linkPath + ".rhino1", linkPath + ".rhino2", linkPath + ".rhino3", linkPath + ".rhino4" }) if (File.Exists(f)) File.Delete(f);
        Func<string, bool> wait = flag =>
        {
            Console.WriteLine("      waiting for " + Path.GetFileName(flag));
            for (int i = 0; i < 3600 && !File.Exists(flag); i++) System.Threading.Thread.Sleep(1000);
            return File.Exists(flag);
        };
        Action<List<string>> show = ns => { foreach (string n in ns.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination")).Take(20)) Console.WriteLine("      note: " + n); };

        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            Console.WriteLine("READY1 " + linkPath);
            if (!wait(linkPath + ".rhino1")) { Check(false, "Rhino built the model"); goto done; }

            var notes = new List<string>();
            AddonLoad(linkPath, notes);
            show(notes);
            Console.WriteLine("      after load: " + Counts());
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                RLineAttr la;
                if (LinkState.TryLineAttr(model, i, out la) && (la.LineType == ELineType.ltBeam || la.LineType == ELineType.ltRib))
                    Console.WriteLine("      line " + i + " " + la.LineType + " " + string.Join(";", LinkState.Guids(model.Lines.Attachments, i)) + " state " + LinkState.Read(model.Lines.Attachments, LinkState.State, i));
            }
            Check(LinesOfType(ELineType.ltSpring) == 1, "spring: one spring element (" + LinesOfType(ELineType.ltSpring) + ")");
            Check(LinkCount(ELinkElementType.letNN) == 1, "node link: one node-node link (" + LinkCount(ELinkElementType.letNN) + ")");
            Check(C(() => model.RigidBodies.Count) == 1, "rigid link: one rigid body (" + C(() => model.RigidBodies.Count) + ")");
            Check(LinkCount(ELinkElementType.letLL) == 1, "line link: one line-to-line link (" + LinkCount(ELinkElementType.letLL) + ")");
            Check(LinesOfType(ELineType.ltTruss) == 1, "truss: one truss (" + LinesOfType(ELineType.ltTruss) + ")");
            Check(C(() => model.NodalSupports.Count) == 2, "point supports: two (" + C(() => model.NodalSupports.Count) + ")");
            Check(C(() => model.LineSupports.Count) >= 2, "line supports: the beam's foundation and the slab edge support (" + C(() => model.LineSupports.Count) + ")");
            Check(C(() => model.EdgeConnections.Count) >= 1, "edge hinge: on the slab edge (" + C(() => model.EdgeConnections.Count) + ")");
            bool found = false;
            for (int d = 1; d <= model.Domains.Count; d++) if (model.Domains.Item[d].ElasticFoundationExists == ELongBoolean.lbTrue) found = true;
            Check(found, "surface support: a slab on an elastic foundation");
            Check(C(() => model.Loads.Count) >= 3, "loads: nodal, member and surface loads (" + C(() => model.Loads.Count) + ")");
            string built = Counts();

            SendResult r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the first load: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
            if (r.Changed > 0)
                foreach (string list in new[] { "points", "lines", "domains" })
                    foreach (JsonValue e in Section(linkPath)[list].ArrayValue)
                        if (Changed(e)) Console.WriteLine("      changed " + list + ": " + e.ToJson().Replace("\r\n", " ").Replace("  ", ""));

            // Drawn in AxisVM: a spring and a rigid link, each between two new nodes.
            RLineGeomData g = new RLineGeomData();
            int n1 = model.Nodes.Add(30, 0, 0), n2 = model.Nodes.Add(30, 0, 1), n3 = model.Nodes.Add(32, 0, 0), n4 = model.Nodes.Add(32, 0, 1);
            int ls = model.Lines.Add(n1, n2, ELineGeomType.lgtStraightLine, ref g), lr = model.Lines.Add(n3, n4, ELineGeomType.lgtStraightLine, ref g);
            RStiffnesses st = new RStiffnesses { x = 2000, y = 2000, z = 3000 };
            int sp = model.Lines.Item[ls].DefineAsSpring(ESpringDirection.sdGlobal, ref st);
            int rb = model.RigidBodies.Add(OneBased(lr));
            Check(sp > 0 && rb > 0, "axisvm edit: a new spring and a new rigid link (" + sp + ", " + rb + ")");
            r = RhinoLinkSender.Send(model, linkPath);
            JsonValue sec = Section(linkPath);
            JsonValue spring = sec["lines"].ArrayValue.FirstOrDefault(e => LinkFile.Get(e["attrs"], "Ax_Type") == "Spring" && e["new"] != null && e["new"].BoolValue);
            JsonValue rigid = sec["lines"].ArrayValue.FirstOrDefault(e => LinkFile.Get(e["attrs"], "Ax_Type") == "RigidLink" && e["new"] != null && e["new"].BoolValue);
            Check(r.Adopted == 2 && spring != null && rigid != null, "send: the new spring and rigid link go to Rhino (" + r.Adopted + " adopted)");
            Check(r.Changed == r.Adopted, "send: nothing but the two new elements (" + r.Changed + " changed)");
            foreach (string list in new[] { "points", "lines", "domains" })
                foreach (JsonValue e in sec[list].ArrayValue)
                    if (Changed(e) && !(e["new"] != null && e["new"].BoolValue)) Console.WriteLine("      changed " + list + ": " + e.ToJson().Replace("\r\n", " ").Replace("  ", ""));
            Check(spring != null && LinkFile.Get(spring["attrs"], "Ax_Stiffness").StartsWith("2000,2000,3000"), "send: the spring's stiffness (" + (spring == null ? "-" : LinkFile.Get(spring["attrs"], "Ax_Stiffness")) + ")");
            Check(spring != null && LinkFile.Get(spring["attrs"], "Ax_Section") == "" && LinkFile.Get(spring["attrs"], "Ax_Material") == "",
                "send: the spring has no section or material (" + (spring == null ? "-" : LinkFile.Get(spring["attrs"], "Ax_Section")) + ")");
            string withNew = Counts();
            Console.WriteLine("READY2");

            if (!wait(linkPath + ".rhino2")) { Check(false, "Rhino echoed"); goto done; }
            notes.Clear();
            AddonLoad(linkPath, notes);
            show(notes);
            Console.WriteLine("      " + (lastPlan == null ? "no plan" : lastPlan.Summary()));
            Check(lastPlan != null && lastPlan.Build.Count == 0 && Counts() == withNew, "echo: nothing is rebuilt, every count as before");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the echo: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
            Console.WriteLine("READY3");

            if (!wait(linkPath + ".rhino3")) { Check(false, "Rhino changed the spring"); goto done; }
            notes.Clear();
            AddonLoad(linkPath, notes);
            show(notes);
            Console.WriteLine("      " + (lastPlan == null ? "no plan" : lastPlan.Summary()));
            Check(lastPlan != null && lastPlan.Changed == 1, "spring changed in Rhino: one changed (" + (lastPlan == null ? -1 : lastPlan.Changed) + ", " + (lastPlan == null ? -1 : lastPlan.Build.Count) + " rebuilt)");
            bool stiff = false;
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                RLineAttr a;
                if (!LinkState.TryLineAttr(model, i, out a) || a.LineType != ELineType.ltSpring) continue;
                Dictionary<string, string> ex = LinkExtras.Read(model).Curve(new List<int> { i }, null, l => null);
                string k;
                if (ex.TryGetValue(AxKeys.Stiffness, out k) && k.StartsWith("5000,5000,9000")) stiff = true;
            }
            Check(stiff, "spring changed in Rhino: AxisVM has the new stiffness 5000,5000,9000");
            Check(Counts() == withNew, "spring changed in Rhino: every count as before");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after that: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");

            // Deleted in AxisVM: the truss goes, Rhino deletes it too, and the echo rebuilds nothing.
            for (int i = model.Lines.Count; i >= 1; i--) { RLineAttr a; if (LinkState.TryLineAttr(model, i, out a) && a.LineType == ELineType.ltTruss) model.Lines.Delete(i); }
            string afterDelete = Counts();
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Deleted == 1 && r.Changed == 0, "axisvm delete: the truss is sent as deleted (" + r.Deleted + " deleted, " + r.Changed + " changed)");
            Console.WriteLine("READY4");
            if (!wait(linkPath + ".rhino4")) { Check(false, "Rhino took the delete"); goto done; }
            notes.Clear();
            AddonLoad(linkPath, notes);
            show(notes);
            Console.WriteLine("      " + (lastPlan == null ? "no plan" : lastPlan.Summary()));
            Check(lastPlan != null && lastPlan.Build.Count == 0 && Counts() == afterDelete, "after Rhino deleted it too: nothing is rebuilt, every count as before");
            r = RhinoLinkSender.Send(model, linkPath);
            Check(r.Changed == 0 && r.Adopted == 0 && r.Deleted == 0, "send after the delete: nothing changed (" + r.Changed + " / " + r.Adopted + " / " + r.Deleted + ")");
            Console.WriteLine("      built " + built);
        done:
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All Rhino element checks passed." : failures + " Rhino element check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    /// <summary>
    /// A plain AxExport file (mode "plain &lt;json&gt;"), no link: imported into a new model, then imported again - a
    /// plain file is replaced in full each time, so the second import must leave exactly the same model.
    /// </summary>
    private static int PlainMain(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), "nextra-roundtrip", "plain-" + Path.GetFileName(json));
        File.Copy(json, path, true);
        app = AxisVmDialogGuard.Launch();
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            model = AxisVmDialogGuard.FreshModel(app);
            var notes = new List<string>();
            AddonLoad(path, notes);
            foreach (string n in notes.Where(n => !n.StartsWith("Load case") && !n.StartsWith("Load combination") && !n.StartsWith("Material") && !n.StartsWith("Cross")).Take(20)) Console.WriteLine("      note: " + n);
            string first = Counts();
            Console.WriteLine("      after import: " + first);
            // The exported model is the "rhino-elements" one after its AxisVM edit: two springs and two rigid links.
            Check(LinesOfType(ELineType.ltSpring) == 2 && LinkCount(ELinkElementType.letNN) == 1 && C(() => model.RigidBodies.Count) == 2 && LinkCount(ELinkElementType.letLL) == 1,
                "plain import: springs, node link, rigid links and line link (" + LinesOfType(ELineType.ltSpring) + ", " + LinkCount(ELinkElementType.letNN) + ", "
                + C(() => model.RigidBodies.Count) + ", " + LinkCount(ELinkElementType.letLL) + ")");
            Check(C(() => model.Domains.Count) == 1 && C(() => model.NodalSupports.Count) == 2 && C(() => model.EdgeConnections.Count) >= 1 && C(() => model.Loads.Count) >= 3,
                "plain import: slab, point supports, edge hinge and loads");
            notes.Clear();
            AddonLoad(path, notes);
            string second = Counts();
            Check(second == first, "plain import again: the model is replaced, nothing doubled (" + second + ")");
            Check(!File.ReadAllText(path).Contains("\"axisvm\""), "plain import: nothing written into the plain file");
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All plain import checks passed." : failures + " plain import check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Quit();
        }
    }

    private static int LinesCountAfterLoad(string counts)
    {
        string t = counts.Substring(counts.IndexOf("lines ") + 6);
        return int.Parse(t.Substring(0, t.IndexOf(',')));
    }

    private static RhinoModel WithPart(this RhinoModel rhino, string guid, string part)
    {
        foreach (RhinoLine l in rhino.Lines) if (l.Guid == guid) l.Attrs[AxKeys.Parts] = part;
        return rhino;
    }
}
