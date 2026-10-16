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
    }

    private static int C(Func<int> f) { try { return f(); } catch { return -1; } }

    private static string Counts()
    {
        return string.Format("nodes {0}, lines {1}, domains {2}, load panels {3}, members {4}, nodal supports {5}, line supports {6}, links {7}, rigid bodies {8}, edge hinges {9}, parts {10}, loads {11}",
            C(() => model.Nodes.Count), C(() => model.Lines.Count), C(() => model.Domains.Count), C(() => model.LoadPanels.Count),
            C(() => model.Members.Count), C(() => model.NodalSupports.Count), C(() => model.LineSupports.Count),
            C(() => model.LinkElements.Count), C(() => model.RigidBodies.Count), C(() => model.EdgeConnections.Count),
            C(() => model.CustomParts.RootFolder.PartCount), C(() => model.Loads.Count));
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

    private static ImportStats Import(RhinoModel rhino, string path, double lengthToM, bool update, List<string> notes, HashSet<string> known = null)
    {
        var opt = new ImportOptions
        {
            Units = new AxUnits { LengthToM = lengthToM, LengthName = lengthToM < 1 ? "mm" : "m" },
            MergeToleranceM = 0.001, AutoIntersect = true, SourcePath = path, ReplacePrevious = update, RhinoKnownGuids = known
        };
        var builder = new AxisModelBuilder(app, model, rhino, opt, notes);
        model.BeginUpdate();
        try { builder.Run(); }
        finally { model.EndUpdate(); }
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
        double lengthToM = args.Length > 1 && args[1] == "m" ? 1.0 : 0.001;
        if (args.Length > 2 && args[2] == "link") return LinkMain(json, lengthToM);
        // The mark is the file name, so every step imports "under" the same one.
        string linkPath = Path.Combine(Path.GetTempPath(), "nextra-update-check.json");

        AxisVmDialogGuard.Start();
        app = (IAxisVMApplication)Activator.CreateInstance(Type.GetTypeFromProgID("AxisVM.AxisVMApplication"));
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            if (app.Models.Count < 1) app.Models.New();
            model = app.Models.Item[1];
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
        Import(rhino, linkPath, lengthToM, true, notes, LinkFile.RhinoKnownGuids(root));
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

        AxisVmDialogGuard.Start();
        app = (IAxisVMApplication)Activator.CreateInstance(Type.GetTypeFromProgID("AxisVM.AxisVMApplication"));
        try
        {
            while (app.Loaded != ELongBoolean.lbTrue) System.Threading.Thread.Sleep(200);
            if (app.Models.Count < 1) app.Models.New();
            model = app.Models.Item[1];
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

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All live link checks passed." : failures + " live link check(s) failed.");
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
