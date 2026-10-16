using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What one "Send to Rhino" wrote.</summary>
    public class SendResult
    {
        public int Changed;
        public int Unchanged;
        public int Adopted;
        public int Deleted;
        /// <summary>Materials, cross-sections, load cases and load combinations in the library that was sent.</summary>
        public int[] Library = new int[4];
        /// <summary>Library entries Rhino does not have yet.</summary>
        public int LibraryNew;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// "Send to Rhino": writes what was changed in AxisVM into the "axisvm" section of the live link file.
    ///
    /// For every entity standing for a Rhino object (RhinoGuid, from an import of this link) the current
    /// state is compared with its RhinoState baseline: moved geometry is sent as the rebuilt curve /
    /// outline / point, and of the properties only those that differ (Ax_Type, Ax_Section, Ax_Material,
    /// Ax_FEType, Ax_Thickness). Unchanged entities are listed too (changed:false), and "deleted" lists the
    /// objects the last "Load from Rhino" built that are gone, so Rhino can tell what was removed.
    ///
    /// Beams, ribs, trusses and domains drawn in AxisVM get a new id and are "adopted" (RhinoAdopted): sent
    /// with all their properties and "new": true (Rhino creates only those, so a curve deleted in Rhino is
    /// never brought back), and replaced by a later "Load from Rhino" only once Rhino holds them.
    ///
    /// The baseline is NOT refreshed here: until AxisVM loads Rhino's model again, every send carries every
    /// change since then, so sending twice before Rhino has loaded cannot drop the first send's changes.
    /// Coordinates and Ax_Thickness are in metres ("unitSystem": "Meters"); Rhino scales them.
    /// </summary>
    public static class RhinoLinkSender
    {
        private const double Tol = 1e-6;

        public static SendResult Send(IAxisVMModel model, string linkPath)
        {
            SendResult res = new SendResult();
            string key = AxisModelBuilder.LinkKey(linkPath);

            JsonValue current = File.Exists(linkPath) && new FileInfo(linkPath).Length > 0 ? LinkFile.Read(linkPath) : null;
            HashSet<string> rhinoKnows = LinkFile.RhinoKnownGuids(current);
            JsonValue oldSection = LinkFile.IsLink(current) ? current[LinkFile.AxisVmSection] : null;
            Dictionary<string, double[]> rhinoStarts = RhinoLineStarts(current);

            JsonValue section = JsonValue.Obj()
                .Set("sent", JsonValue.Str(LinkFile.Now()))
                .Set("basedOnRhino", JsonValue.Str(LinkFile.Get(oldSection, "basedOnRhino")))
                .Set("built", oldSection != null && oldSection["built"] != null ? oldSection["built"] : JsonValue.Arr())
                .Set("segments", oldSection != null && oldSection["segments"] != null ? oldSection["segments"] : JsonValue.Obj())
                .Set("unitSystem", JsonValue.Str("Meters"))
                .Set("model", JsonValue.Str(SafeFileName(model)));

            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            section.Set("lines", Lines(model, key, rhinoKnows, rhinoStarts, present, section, res));
            JsonValue domains = Domains(model, key, rhinoKnows, present, section, res);
            Panels(model, key, rhinoKnows, present, section, domains, res);
            section.Set("domains", domains);
            section.Set("points", Points(model, key, present, res));

            JsonValue library = LinkLibrary.Read(model, out res.Library);
            res.LibraryNew = LinkLibrary.CountNew(library, LinkFile.IsLink(current) ? current[LinkFile.RhinoSection] : current);
            section.Set("library", library).Set("libraryNew", JsonValue.Num(res.LibraryNew));

            JsonValue deleted = JsonValue.Arr();
            JsonValue built = section["built"];
            for (int i = 0; built != null && i < built.Count; i++)
            {
                string g = built.At(i).AsString("");
                if (g.Length > 0 && !present.Contains(g)) { deleted.Add(JsonValue.Str(g)); res.Deleted++; }
            }
            section.Set("deleted", deleted);

            res.Notes.Add("Supports, loads, releases, springs and links made or changed in AxisVM are not sent to Rhino. " +
                          "The next 'Load from Rhino' rebuilds these elements from Rhino, so add them in Rhino to keep them.");
            JsonValue notes = JsonValue.Arr();
            foreach (string n in res.Notes) notes.Add(JsonValue.Str(n));
            section.Set("notes", notes);

            LinkFile.Update(linkPath, root => root.Set(LinkFile.AxisVmSection, section));
            return res;
        }

        /// <summary>
        /// After "Load from Rhino": AxisVM is in line with that Rhino model. The axisvm section is reset
        /// (no changes) and lists what was built, so a later send can tell what was deleted in AxisVM.
        /// </summary>
        public static void MarkLoaded(IAxisVMModel model, string linkPath, string rhinoSent)
        {
            string key = AxisModelBuilder.LinkKey(linkPath);
            HashSet<string> built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> segments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CollectGuids(model, key, built, segments);

            JsonValue arr = JsonValue.Arr();
            foreach (string g in built) arr.Add(JsonValue.Str(g));
            JsonValue segs = JsonValue.Obj();
            foreach (KeyValuePair<string, int> kv in segments) segs.Set(kv.Key, JsonValue.Num(kv.Value));
            JsonValue section = JsonValue.Obj()
                .Set("sent", JsonValue.Str(""))
                .Set("basedOnRhino", JsonValue.Str(rhinoSent ?? ""))
                .Set("built", arr)
                .Set("segments", segs)
                .Set("unitSystem", JsonValue.Str("Meters"))
                .Set("model", JsonValue.Str(SafeFileName(model)))
                .Set("lines", JsonValue.Arr()).Set("domains", JsonValue.Arr()).Set("points", JsonValue.Arr())
                .Set("deleted", JsonValue.Arr()).Set("notes", JsonValue.Arr());
            LinkFile.Update(linkPath, root => root.Set(LinkFile.AxisVmSection, section));
        }

        /// <summary>Every Rhino id the link's entities stand for, and how many AxisVM lines each curve became.</summary>
        private static void CollectGuids(IAxisVMModel model, string key, HashSet<string> into, Dictionary<string, int> lineSegments)
        {
            Action<AxisVMAttachments, int, Dictionary<string, int>> take = (a, n, count) =>
            {
                for (int i = 1; i <= n; i++)
                {
                    if (!Ours(a, i, key)) continue;
                    foreach (string g in LinkState.Guids(a, i))
                    {
                        into.Add(g);
                        if (count == null) continue;
                        int c;
                        count.TryGetValue(g, out c);
                        count[g] = c + 1;
                    }
                }
            };
            try { take(model.Lines.Attachments, model.Lines.Count, lineSegments); } catch { }
            try { take(model.Domains.Attachments, model.Domains.Count, null); } catch { }
            try { take(model.Nodes.Attachments, model.Nodes.Count, null); } catch { }
        }

        /// <summary>Imported from this link, or adopted into it.</summary>
        private static bool Ours(AxisVMAttachments a, int i, string key)
        {
            if (string.Equals(LinkState.Read(a, LinkState.Link, i), key, StringComparison.Ordinal)) return true;
            List<string> owners;
            return string.Equals(LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, i), out owners), key, StringComparison.Ordinal);
        }

        private static bool Native(AxisVMAttachments a, int i)
        {
            return LinkState.Read(a, LinkState.Link, i) == null && LinkState.Read(a, LinkState.Adopted, i) == null && LinkState.Read(a, LinkState.Guid, i) == null;
        }

        // ==================================================================
        // Lines
        // ==================================================================

        private static JsonValue Lines(IAxisVMModel model, string key, HashSet<string> rhinoKnows, Dictionary<string, double[]> rhinoStarts,
                                       HashSet<string> present, JsonValue section, SendResult res)
        {
            AxisVMAttachments la = model.Lines.Attachments;
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = model.Lines.Count;

            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(la, i);
                if (owners.Count > 0)
                {
                    if (!Ours(la, i, key)) continue;
                    List<string> adoptedOwners;
                    bool adopted = LinkState.AdoptedKey(LinkState.Read(la, LinkState.Adopted, i), out adoptedOwners) != null;
                    foreach (string g in owners)
                    {
                        Group(groups, g, i);
                        if (adopted && !rhinoKnows.Contains(g)) full.Add(g);
                    }
                    continue;
                }

                // Drawn in AxisVM: only typed members become Rhino curves (plain lines are slab edges & co.).
                if (!Native(la, i)) continue;
                RLineAttr attr;
                if (!LinkState.TryLineAttr(model, i, out attr) || LinkState.LineTypeName(attr.LineType).Length == 0) continue;

                string guid = System.Guid.NewGuid().ToString();
                LinkState.Write(la, LinkState.Guid, i, guid);
                LinkState.Write(la, LinkState.Adopted, i, key + "|" + guid);
                LinkState.Write(la, LinkState.State, i, LinkState.LineState(model, i));
                Group(groups, guid, i);
                full.Add(guid);
                Built(section, guid, 1);
                res.Adopted++;
            }

            JsonValue arr = JsonValue.Arr();
            foreach (KeyValuePair<string, List<int>> kv in groups)
            {
                string guid = kv.Key;
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                double[] start;
                rhinoStarts.TryGetValue(guid, out start);
                if (!WriteCurve(model, kv.Value, start, e))
                    res.Notes.Add(string.Format("Curve {0}: its AxisVM lines no longer form one chain; its shape was not sent.", Short(guid)));

                // A curve some of whose AxisVM lines were deleted has changed shape even if the rest did not move.
                JsonValue expected = section["segments"][guid];
                bool geomChanged = expected != null && expected.AsInt(kv.Value.Count) != kv.Value.Count;
                string[] now = null, then = null;
                foreach (int id in kv.Value)
                {
                    string[] cur = LinkState.Fields(LinkState.LineState(model, id), 5);
                    string[] baseState = LinkState.Fields(LinkState.Read(la, LinkState.State, id), 5);
                    if (cur[0] != baseState[0]) geomChanged = true;
                    if (now == null) { now = cur; then = baseState; }
                }

                JsonValue attrs = JsonValue.Obj();
                bool isFull = full.Contains(guid);
                if (now != null)
                {
                    if (!now[1].StartsWith("other", StringComparison.Ordinal) && (isFull || now[1] != then[1])) attrs.Set("Ax_Type", JsonValue.Str(now[1]));
                    if (isFull || now[2] != then[2] || now[3] != then[3])
                        attrs.Set("Ax_Section", JsonValue.Str(now[2] == now[3] || now[3].Length == 0 ? now[2] : now[2] + ";" + now[3]));
                    if (isFull || now[4] != then[4]) attrs.Set("Ax_Material", JsonValue.Str(now[4]));
                }

                bool changed = isFull || geomChanged || attrs.Count > 0;
                e.Set("changed", JsonValue.Bool(changed)).Set("geomChanged", JsonValue.Bool(isFull || geomChanged))
                 .Set("new", JsonValue.Bool(isFull)).Set("attrs", attrs);
                if (changed) res.Changed++; else res.Unchanged++;
                arr.Add(e);
            }
            return arr;
        }

        /// <summary>Records an adopted entity as built, so deleting it in AxisVM later is sent as a deletion.</summary>
        private static void Built(JsonValue section, string guid, int segments)
        {
            section["built"].Add(JsonValue.Str(guid));
            if (segments > 0) section["segments"].Set(guid, JsonValue.Num(segments));
        }

        private static void Group(Dictionary<string, List<int>> groups, string guid, int id)
        {
            List<int> l;
            if (!groups.TryGetValue(guid, out l)) { l = new List<int>(); groups[guid] = l; }
            l.Add(id);
        }

        /// <summary>
        /// Rebuilds a curve from its AxisVM lines: one arc stays an arc, otherwise the chain becomes a line or
        /// polyline (vertices where it only continues straight are dropped), running the way the Rhino curve did.
        /// </summary>
        private static bool WriteCurve(IAxisVMModel model, List<int> lineIds, double[] rhinoStart, JsonValue e)
        {
            if (lineIds.Count == 1 && LinkState.IsArc(model, lineIds[0]))
            {
                int[] ends = LinkState.LineNodes(model, lineIds[0]);
                double[] a = ends == null ? null : LinkState.NodeXyz(model, ends[0]);
                double[] b = ends == null ? null : LinkState.NodeXyz(model, ends[1]);
                double[] m = LinkState.ArcMid(model, lineIds[0]);
                if (a == null || b == null || m == null) return false;
                if (rhinoStart != null && Dist(b, rhinoStart) < Dist(a, rhinoStart)) { double[] t = a; a = b; b = t; }
                e.Set("geom", JsonValue.Str("arc")).Set("start", P(a)).Set("end", P(b)).Set("arcMid", P(m));
                return true;
            }

            List<int> nodes = Walk(model, lineIds, false);
            if (nodes == null) return false;
            List<double[]> pts = new List<double[]>();
            foreach (int nd in nodes)
            {
                double[] p = LinkState.NodeXyz(model, nd);
                if (p == null) return false;
                pts.Add(p);
            }
            if (rhinoStart != null && Dist(pts[pts.Count - 1], rhinoStart) < Dist(pts[0], rhinoStart)) pts.Reverse();
            pts = DropStraight(pts, false);

            e.Set("geom", JsonValue.Str(pts.Count == 2 ? "line" : "polyline")).Set("start", P(pts[0])).Set("end", P(pts[pts.Count - 1]));
            if (pts.Count > 2)
            {
                JsonValue list = JsonValue.Arr();
                foreach (double[] p in pts) list.Add(P(p));
                e.Set("points", list);
            }
            return true;
        }

        /// <summary>
        /// The node ids along a chain of lines, end to end (a loop when <paramref name="closed"/>: the
        /// first node is not repeated). Null when the lines do not form one simple chain.
        /// </summary>
        private static List<int> Walk(IAxisVMModel model, List<int> lineIds, bool closed)
        {
            Dictionary<int, List<int>> at = new Dictionary<int, List<int>>();
            Dictionary<int, int[]> ends = new Dictionary<int, int[]>();
            foreach (int id in lineIds)
            {
                int[] e = LinkState.LineNodes(model, id);
                if (e == null || e[0] == e[1]) return null;
                ends[id] = e;
                foreach (int nd in e)
                {
                    List<int> l;
                    if (!at.TryGetValue(nd, out l)) { l = new List<int>(); at[nd] = l; }
                    l.Add(id);
                }
            }

            int start = -1;
            foreach (KeyValuePair<int, List<int>> kv in at)
            {
                if (kv.Value.Count > 2) return null;
                if (kv.Value.Count == 1) { if (closed) return null; if (start < 0) start = kv.Key; }
            }
            if (start < 0)
            {
                if (!closed) return null;
                foreach (int k in at.Keys) { start = k; break; }
            }

            List<int> path = new List<int> { start };
            HashSet<int> used = new HashSet<int>();
            int node = start;
            while (used.Count < lineIds.Count)
            {
                int next = -1;
                foreach (int id in at[node]) if (!used.Contains(id)) { next = id; break; }
                if (next < 0) return null;
                used.Add(next);
                node = ends[next][0] == node ? ends[next][1] : ends[next][0];
                if (closed && node == start) break;
                path.Add(node);
            }
            return used.Count == lineIds.Count ? path : null;
        }

        private static List<double[]> DropStraight(List<double[]> pts, bool closed)
        {
            if (pts.Count < 3) return pts;
            List<double[]> keep = new List<double[]>();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                bool end = !closed && (i == 0 || i == n - 1);
                if (end) { keep.Add(pts[i]); continue; }
                double[] a = pts[(i - 1 + n) % n], b = pts[i], c = pts[(i + 1) % n];
                if (OffLine(a, c, b) > Tol * 100) keep.Add(b);
            }
            return keep.Count >= (closed ? 3 : 2) ? keep : pts;
        }

        private static double OffLine(double[] a, double[] c, double[] b)
        {
            double[] u = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
            double len = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            if (len < Tol) return Dist(a, b);
            double[] w = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double[] x = { u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0] };
            return Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]) / len;
        }

        // ==================================================================
        // Domains
        // ==================================================================

        private static JsonValue Domains(IAxisVMModel model, string key, HashSet<string> rhinoKnows, HashSet<string> present, JsonValue section, SendResult res)
        {
            AxisVMAttachments da = model.Domains.Attachments;
            AxisVMAttachments la = model.Lines.Attachments;
            JsonValue arr = JsonValue.Arr();
            int n = model.Domains.Count;

            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(da, i);
                bool isFull;
                string guid;
                if (owners.Count > 0)
                {
                    if (!Ours(da, i, key)) continue;
                    guid = owners[0];
                    List<string> adoptedOwners;
                    isFull = LinkState.AdoptedKey(LinkState.Read(da, LinkState.Adopted, i), out adoptedOwners) != null && !rhinoKnows.Contains(guid);
                }
                else
                {
                    if (!Native(da, i)) continue;
                    guid = System.Guid.NewGuid().ToString();
                    LinkState.Write(da, LinkState.Guid, i, guid);
                    LinkState.Write(da, LinkState.Adopted, i, key + "|" + guid);
                    LinkState.Write(da, LinkState.State, i, LinkState.DomainState(model, i));
                    // Its edge lines go with it, so a later Rhino load does not draw them a second time.
                    List<int> edges = LinkState.ContourLines(model, i);
                    foreach (List<int> h in LinkState.HoleLines(model, i)) edges.AddRange(h);
                    foreach (int l in edges) AdoptEdge(la, l, key, guid);
                    isFull = true;
                    Built(section, guid, 0);
                    res.Adopted++;
                }
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                if (!WriteOutline(model, i, e))
                    res.Notes.Add(string.Format("Surface {0}: its outline could not be traced; its shape was not sent.", Short(guid)));

                string[] now = LinkState.Fields(LinkState.DomainState(model, i), 4);
                string[] then = LinkState.Fields(LinkState.Read(da, LinkState.State, i), 4);
                JsonValue attrs = JsonValue.Obj();
                if (isFull || now[1] != then[1]) attrs.Set("Ax_FEType", JsonValue.Str(now[1]));
                if (isFull || now[2] != then[2]) attrs.Set("Ax_Thickness", JsonValue.Str(now[2]));
                if (isFull || now[3] != then[3]) attrs.Set("Ax_Material", JsonValue.Str(now[3]));

                bool changed = isFull || now[0] != then[0] || attrs.Count > 0;
                e.Set("changed", JsonValue.Bool(changed)).Set("geomChanged", JsonValue.Bool(isFull || now[0] != then[0]))
                 .Set("new", JsonValue.Bool(isFull)).Set("attrs", attrs);
                if (changed) res.Changed++; else res.Unchanged++;
                arr.Add(e);
            }
            return arr;
        }

        private static void AdoptEdge(AxisVMAttachments la, int lineId, string key, string owner)
        {
            if (LinkState.Read(la, LinkState.Link, lineId) != null) return;   // imported: replaced anyway
            List<string> owners;
            string k = LinkState.AdoptedKey(LinkState.Read(la, LinkState.Adopted, lineId), out owners);
            if (k != null && k != key) return;
            if (!owners.Contains(owner)) owners.Add(owner);
            LinkState.Write(la, LinkState.Adopted, lineId, key + "|" + string.Join(";", owners));
        }

        private static bool WriteOutline(IAxisVMModel model, int domainId, JsonValue e)
        {
            List<double[]> outer = Loop(model, LinkState.ContourLines(model, domainId));
            if (outer == null) return false;
            JsonValue contour = JsonValue.Arr();
            foreach (double[] p in outer) contour.Add(P(p));
            e.Set("contour", contour);

            JsonValue holes = JsonValue.Arr();
            foreach (List<int> h in LinkState.HoleLines(model, domainId))
            {
                List<double[]> loop = Loop(model, h);
                if (loop == null) continue;
                JsonValue hole = JsonValue.Arr();
                foreach (double[] p in loop) hole.Add(P(p));
                holes.Add(hole);
            }
            if (holes.Count > 0) e.Set("holes", holes);
            return true;
        }

        private static List<double[]> Loop(IAxisVMModel model, List<int> lineIds)
        {
            if (lineIds.Count < 3) return null;
            List<int> nodes = Walk(model, lineIds, true);
            if (nodes == null) return null;
            List<double[]> pts = new List<double[]>();
            foreach (int nd in nodes)
            {
                double[] p = LinkState.NodeXyz(model, nd);
                if (p == null) return null;
                pts.Add(p);
            }
            return DropStraight(pts, true);
        }

        // ==================================================================
        // Load panels
        // ==================================================================

        /// <summary>
        /// "guid|outline state" on every contour line of a load panel drawn in AxisVM. Load panels cannot carry
        /// attachments themselves; their contour lines do, and AxisModelBuilder.Link deletes the panel with its lines.
        /// </summary>
        private const string PanelAttachment = "RhinoPanel";

        /// <summary>
        /// Load panels drawn in AxisVM become Rhino surfaces with Ax_FEType = LoadPanel (two-way). A panel whose contour
        /// lines all came from an import is Rhino's own and is left alone. Sent in the "domains" list.
        /// </summary>
        private static void Panels(IAxisVMModel model, string key, HashSet<string> rhinoKnows, HashSet<string> present,
                                   JsonValue section, JsonValue arr, SendResult res)
        {
            AxisVMAttachments la = model.Lines.Attachments;
            int n;
            try { n = model.LoadPanels.Count; } catch { return; }

            for (int i = 1; i <= n; i++)
            {
                List<int> lines;
                try
                {
                    Array ids = null;
                    model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                    lines = LinkState.IntList(ids);
                }
                catch { continue; }
                if (lines.Count < 3) continue;

                string guid = null, baseline = "";
                bool allImported = true;
                foreach (int l in lines)
                {
                    string stamp = LinkState.Read(la, PanelAttachment, l);
                    if (stamp != null && guid == null)
                    {
                        int bar = stamp.IndexOf('|');
                        guid = bar < 0 ? stamp : stamp.Substring(0, bar);
                        baseline = bar < 0 ? "" : stamp.Substring(bar + 1);
                    }
                    if (LinkState.Read(la, LinkState.Link, l) == null) allImported = false;
                }

                string state = LinkState.SegmentsState(model, lines);
                bool isNew;
                if (guid == null)
                {
                    if (allImported) continue;   // Rhino's own panel
                    guid = System.Guid.NewGuid().ToString();
                    foreach (int l in lines)
                    {
                        LinkState.Write(la, PanelAttachment, l, guid + "|" + state);
                        AdoptEdge(la, l, key, guid);
                    }
                    Built(section, guid, 0);
                    res.Adopted++;
                    isNew = true;
                }
                else isNew = !rhinoKnows.Contains(guid);
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                List<double[]> outline = Loop(model, lines);
                if (outline == null)
                {
                    res.Notes.Add(string.Format("Load panel {0}: its outline could not be traced; its shape was not sent.", Short(guid)));
                    continue;
                }
                JsonValue contour = JsonValue.Arr();
                foreach (double[] p in outline) contour.Add(P(p));
                e.Set("contour", contour);

                bool geomChanged = isNew || state != baseline;
                e.Set("changed", JsonValue.Bool(geomChanged)).Set("geomChanged", JsonValue.Bool(geomChanged)).Set("new", JsonValue.Bool(isNew))
                 .Set("attrs", JsonValue.Obj().Set("Ax_FEType", JsonValue.Str("LoadPanel")));
                if (geomChanged) res.Changed++; else res.Unchanged++;
                arr.Add(e);
            }
        }

        // ==================================================================
        // Points
        // ==================================================================

        private static JsonValue Points(IAxisVMModel model, string key, HashSet<string> present, SendResult res)
        {
            AxisVMAttachments na = model.Nodes.Attachments;
            JsonValue arr = JsonValue.Arr();
            int n = model.Nodes.Count;
            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(na, i);
                if (owners.Count == 0 || !Ours(na, i, key)) continue;
                double[] p = LinkState.NodeXyz(model, i);
                if (p == null) continue;
                bool changed = LinkState.NodeState(model, i) != LinkState.Read(na, LinkState.State, i);
                foreach (string g in owners)
                {
                    present.Add(g);
                    arr.Add(JsonValue.Obj().Set("guid", JsonValue.Str(g)).Set("xyz", P(p))
                        .Set("changed", JsonValue.Bool(changed)).Set("attrs", JsonValue.Obj()));
                    if (changed) res.Changed++; else res.Unchanged++;
                }
            }
            return arr;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>Start point [m] of every curve in the Rhino section, to send curves back the same way round.</summary>
        private static Dictionary<string, double[]> RhinoLineStarts(JsonValue root)
        {
            Dictionary<string, double[]> starts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return starts;
            JsonValue r;
            try { r = LinkFile.RhinoPart(root); } catch { return starts; }
            double scale = AxUnits.LengthUnitFactors[AxUnits.LengthIndexFromRhinoName(LinkFile.Get(r["source"], "unitSystem"))];
            JsonValue lines = r["lines"];
            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                JsonValue l = lines.At(i), s = l["start"];
                if (s == null || s.Count < 3) continue;
                starts[LinkFile.Get(l, "guid")] = new[] { s.At(0).AsDouble(0) * scale, s.At(1).AsDouble(0) * scale, s.At(2).AsDouble(0) * scale };
            }
            return starts;
        }

        private static JsonValue P(double[] p)
        {
            return JsonValue.Xyz(Math.Round(p[0], 9), Math.Round(p[1], 9), Math.Round(p[2], 9));
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string Short(string guid)
        {
            return guid != null && guid.Length > 8 ? guid.Substring(0, 8) : guid;
        }

        private static string SafeFileName(IAxisVMModel model)
        {
            try { return model.FileName ?? ""; } catch { return ""; }
        }
    }
}
