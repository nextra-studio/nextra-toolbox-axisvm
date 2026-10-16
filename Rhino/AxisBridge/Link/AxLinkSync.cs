using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AxisBridge.Attributes;
using AxisBridge.Export;
using AxisBridge.Layers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Link
{
    /// <summary>
    /// The Rhino end of the live link with the AxisVM "Rhino Import" add-on (its "Live link" tab).
    ///
    /// One JSON file, one section per side:
    ///   { "link": 1,
    ///     "rhino":  { the AxExport model, "sent", "basedOnAxisvm", "seenAxisvmGuids" },
    ///     "axisvm": { what changed in AxisVM since it last loaded Rhino's model: "lines" / "domains" /
    ///                 "points" (each with "changed" and, for elements drawn in AxisVM, "new"), "deleted",
    ///                 "sent", "unitSystem": "Meters" } }
    /// Each side rewrites only its own section, holding the file exclusively while it does.
    ///
    /// Send   = the AxExport model into "rhino".
    /// Load   = apply AxisVM's changes to the document (one undo step), then Send again, so the "rhino"
    ///          section is the merged model and AxisVM's next "Load from Rhino" keeps those changes.
    /// </summary>
    public static class AxLinkSync
    {
        public const string RhinoSection = "rhino";
        public const string AxisVmSection = "axisvm";
        public const string MembersLayer = "AxisVM::Members";
        public const string DomainsLayer = "AxisVM::Domains";

        // ==================================================================
        // Per-document memory
        // ==================================================================

        private const string PathKey = "AxisBridge.LinkPath";

        public static string LinkPath(RhinoDoc doc)
        {
            return doc == null ? "" : doc.Strings.GetValue(PathKey) ?? "";
        }

        public static void SetLinkPath(RhinoDoc doc, string path)
        {
            if (doc != null) doc.Strings.SetString(PathKey, path ?? "");
        }

        /// <summary>The link file's name, case-insensitive: the same key the AxisVM side marks its elements with.</summary>
        private static string Key(string path)
        {
            return Path.GetFileName(path ?? "").ToLowerInvariant();
        }

        private static string BasedOnAxisvm(RhinoDoc doc, string path)
        {
            return doc.Strings.GetValue("AxisBridge.Link.basedOnAxisvm|" + Key(path)) ?? "";
        }

        private static List<string> Seen(RhinoDoc doc, string path)
        {
            string v = doc.Strings.GetValue("AxisBridge.Link.seen|" + Key(path)) ?? "";
            return v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // ==================================================================
        // File
        // ==================================================================

        public static JObject Read(string path)
        {
            using (FileStream fs = Open(path, FileAccess.Read, FileShare.Read))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
            {
                return Parse(r.ReadToEnd());
            }
        }

        /// <summary>Read-modify-write while holding the file exclusively. A plain AxExport file becomes the "rhino" section.</summary>
        public static void Update(string path, Action<JObject> change)
        {
            using (FileStream fs = Open(path, FileAccess.ReadWrite, FileShare.None))
            {
                StreamReader r = new StreamReader(fs, Encoding.UTF8, true);
                JObject root = Parse(r.ReadToEnd());
                if (root == null) root = new JObject { ["link"] = 1 };
                else if (root["link"] == null) root = new JObject { ["link"] = 1, [RhinoSection] = root };

                change(root);

                byte[] bytes = new UTF8Encoding(false).GetBytes(root.ToString(Formatting.Indented));
                fs.SetLength(0);
                fs.Position = 0;
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Parses with date parsing off: the "sent" stamps are compared as strings with what AxisVM wrote, and
        /// Newtonsoft would otherwise turn them into DateTime and write them back in another format.
        /// </summary>
        private static JObject Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static FileStream Open(string path, FileAccess access, FileShare share)
        {
            FileMode mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, mode, access, share); }
                catch (IOException)
                {
                    if (attempt >= 25 || !File.Exists(path)) throw;
                    Thread.Sleep(200);
                }
            }
        }

        public static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        public static string Local(string stamp)
        {
            DateTime t;
            if (string.IsNullOrEmpty(stamp) || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return stamp ?? "";
            return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Str(JToken t, string key)
        {
            JToken v = t == null ? null : t[key];
            return v == null || v.Type == JTokenType.Null ? "" : v.ToString();
        }

        // ==================================================================
        // Status
        // ==================================================================

        public class LinkStatus
        {
            public string RhinoSent = "", AxisVmSent = "", AxisVmBasedOnRhino = "", LoadedHere = "";
            public int AxisVmChanges;

            /// <summary>AxisVM sent changes this document has not loaded.</summary>
            public bool AxisVmNew { get { return AxisVmSent.Length > 0 && AxisVmChanges > 0 && AxisVmSent != LoadedHere; } }
            /// <summary>This document's last send has not been loaded in AxisVM.</summary>
            public bool RhinoPending { get { return RhinoSent.Length > 0 && RhinoSent != AxisVmBasedOnRhino; } }
        }

        public static LinkStatus Status(RhinoDoc doc, string path)
        {
            LinkStatus s = new LinkStatus { LoadedHere = BasedOnAxisvm(doc, path) };
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;
            JObject root = Read(path);
            if (root == null) return s;
            JToken rh = root["link"] != null ? root[RhinoSection] : root;
            JToken ax = root["link"] != null ? root[AxisVmSection] : null;
            s.RhinoSent = Str(rh, "sent");
            s.AxisVmSent = Str(ax, "sent");
            s.AxisVmBasedOnRhino = Str(ax, "basedOnRhino");
            s.AxisVmChanges = CountChanged(ax);
            return s;
        }

        private static int CountChanged(JToken ax)
        {
            if (ax == null) return 0;
            int n = 0;
            foreach (string list in new[] { "points", "lines", "domains" })
                foreach (JToken e in (ax[list] as JArray) ?? new JArray())
                    if (e["changed"] != null && e["changed"].Type == JTokenType.Boolean && (bool)e["changed"]) n++;
            return n + ((ax["deleted"] as JArray) ?? new JArray()).Count + (ax["libraryNew"] != null ? (int)ax["libraryNew"] : 0);
        }

        // ==================================================================
        // Send to AxisVM
        // ==================================================================

        public static ExportResult Send(RhinoDoc doc, string path)
        {
            ExportResult res;
            ExportModelDto model = ModelExporter.Build(doc, false, false, out res);
            res.FilePath = path;

            JObject rh = JObject.FromObject(model);
            rh["sent"] = Now();
            rh["basedOnAxisvm"] = BasedOnAxisvm(doc, path);
            rh["seenAxisvmGuids"] = new JArray(Seen(doc, path).ToArray());

            Update(path, root => root[RhinoSection] = rh);
            SetLinkPath(doc, path);
            return res;
        }

        // ==================================================================
        // Load from AxisVM
        // ==================================================================

        public class LoadResult
        {
            public int Moved, Updated, Created, Deleted, Unchanged;
            /// <summary>What the merge of AxisVM's library added to this document's library.</summary>
            public AxisBridge.Library.AxLibrary.MergeResult Library = new AxisBridge.Library.AxLibrary.MergeResult();
            public bool NothingNew;
            public readonly List<string> Notes = new List<string>();
            public ExportResult Resent;
        }

        /// <param name="confirmDelete">Asked with the number of objects deleted in AxisVM; true deletes them here too.</param>
        public static LoadResult Load(RhinoDoc doc, string path, Func<int, bool> confirmDelete)
        {
            LoadResult res = new LoadResult();
            JObject root = Read(path);
            JToken ax = root != null && root["link"] != null ? root[AxisVmSection] : null;
            string sent = Str(ax, "sent");
            if (ax == null || sent.Length == 0)
            {
                res.NothingNew = true;
                res.Notes.Add("AxisVM has not sent anything to this link file yet (Rhino Import add-on > Live link > Send to Rhino).");
                return res;
            }

            double scale = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
            double tol = doc.ModelAbsoluteTolerance;
            List<string> created = new List<string>();

            uint undo = doc.BeginUndoRecord("AxLink: load from AxisVM");
            try
            {
                foreach (JToken e in (ax["lines"] as JArray) ?? new JArray()) ApplyLine(doc, e, scale, res, created);
                foreach (JToken e in (ax["domains"] as JArray) ?? new JArray()) ApplyDomain(doc, e, scale, tol, res, created);
                foreach (JToken e in (ax["points"] as JArray) ?? new JArray()) ApplyPoint(doc, e, scale, res);

                List<RhinoObject> gone = new List<RhinoObject>();
                foreach (JToken g in (ax["deleted"] as JArray) ?? new JArray())
                {
                    if (BaseId(g.ToString()) != g.ToString())
                    {
                        // One face of a polysurface: deleting the object would take its other faces with it.
                        res.Notes.Add("Surface " + Label(g.ToString()) + ": one face of a polysurface was deleted in AxisVM; remove it here by hand.");
                        continue;
                    }
                    RhinoObject obj = Find(doc, g.ToString());
                    if (obj != null && !gone.Contains(obj)) gone.Add(obj);
                }
                if (gone.Count > 0)
                {
                    if (confirmDelete(gone.Count))
                    {
                        foreach (RhinoObject obj in gone) if (doc.Objects.Delete(obj, true)) res.Deleted++;
                    }
                    else
                    {
                        doc.Objects.UnselectAll();
                        foreach (RhinoObject obj in gone) obj.Select(true);
                        res.Notes.Add(gone.Count + " object(s) deleted in AxisVM were kept here and are selected. The next AxisVM 'Load from Rhino' brings them back there.");
                    }
                }
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }

            // Materials, sections, load cases and combinations AxisVM defines that this library lacks.
            if (ax["library"] != null)
            {
                AxisBridge.Library.AxLibrary lib = AxisBridge.Library.AxLibrary.Load(doc);
                res.Library = lib.MergeFrom(ax["library"]);
                if (res.Library.Total > 0) lib.Save(doc);
            }

            doc.Strings.SetString("AxisBridge.Link.basedOnAxisvm|" + Key(path), sent);
            List<string> seen = Seen(doc, path);
            foreach (string g in created) if (!seen.Contains(g)) seen.Add(g);
            doc.Strings.SetString("AxisBridge.Link.seen|" + Key(path), string.Join(";", seen));
            doc.Views.Redraw();

            // The rhino section becomes the merged model, so AxisVM's next load keeps these changes.
            res.Resent = Send(doc, path);
            return res;
        }

        private static bool Changed(JToken e)
        {
            return e["changed"] != null && e["changed"].Type == JTokenType.Boolean && (bool)e["changed"];
        }

        /// <summary>The shape changed in AxisVM (property-only changes leave Rhino's geometry alone).</summary>
        private static bool GeomChanged(JToken e)
        {
            return e["geomChanged"] == null || e["geomChanged"].Type != JTokenType.Boolean || (bool)e["geomChanged"];
        }

        private static bool IsNew(JToken e)
        {
            return e["new"] != null && e["new"].Type == JTokenType.Boolean && (bool)e["new"];
        }

        private static string BaseId(string guid)
        {
            int i = guid == null ? -1 : guid.LastIndexOf("_f", StringComparison.Ordinal);
            return i > 0 ? guid.Substring(0, i) : guid;
        }

        private static RhinoObject Find(RhinoDoc doc, string id)
        {
            Guid g;
            if (!Guid.TryParse(id, out g)) return null;
            RhinoObject obj = doc.Objects.FindId(g);
            return obj == null || obj.IsDeleted ? null : obj;
        }

        private static Point3d Pt(JToken t, double scale)
        {
            return new Point3d((double)t[0] * scale, (double)t[1] * scale, (double)t[2] * scale);
        }

        private static string Label(string guid)
        {
            return guid.Length > 8 ? guid.Substring(0, 8) : guid;
        }

        private static void ApplyLine(RhinoDoc doc, JToken e, double scale, LoadResult res, List<string> created)
        {
            if (!Changed(e)) { res.Unchanged++; return; }
            string guid = Str(e, "guid");
            Curve curve = MakeCurve(e, scale);
            RhinoObject obj = Find(doc, guid);

            if (obj == null)
            {
                if (!IsNew(e))
                {
                    res.Notes.Add("Curve " + Label(guid) + ": changed in AxisVM but no longer in Rhino - skipped.");
                    return;
                }
                if (curve == null) return;
                ObjectAttributes a = NewAttributes(doc, guid, MembersLayer);
                SetUserText(a, e["attrs"], scale);
                Guid id = doc.Objects.AddCurve(curve, a);
                if (id != Guid.Empty) { res.Created++; created.Add(guid); }
                return;
            }

            if (curve != null && GeomChanged(e) && !SameCurve(obj.Geometry as Curve, curve, doc.ModelAbsoluteTolerance))
            {
                doc.Objects.Replace(obj.Id, curve);
                res.Moved++;
                obj = doc.Objects.FindId(obj.Id);
            }
            if (UpdateUserText(doc, obj, e["attrs"], scale)) res.Updated++;
        }

        private static Curve MakeCurve(JToken e, double scale)
        {
            if (e["start"] == null || e["end"] == null) return null;
            Point3d s = Pt(e["start"], scale), t = Pt(e["end"], scale);
            string geom = Str(e, "geom");
            if (geom == "arc" && e["arcMid"] != null)
            {
                Arc arc = new Arc(s, Pt(e["arcMid"], scale), t);
                if (arc.IsValid) return new ArcCurve(arc);
            }
            if (geom == "polyline" && e["points"] is JArray pts && pts.Count > 2)
                return new PolylineCurve(pts.Select(p => Pt(p, scale)));
            return new LineCurve(s, t);
        }

        /// <summary>Start, end and middle within tolerance: a curve that did not move keeps its exact Rhino geometry.</summary>
        private static bool SameCurve(Curve a, Curve b, double tol)
        {
            if (a == null || b == null) return false;
            if (a.PointAtStart.DistanceTo(b.PointAtStart) > tol || a.PointAtEnd.DistanceTo(b.PointAtEnd) > tol) return false;
            Polyline pa, pb;
            bool aPoly = a.TryGetPolyline(out pa), bPoly = b.TryGetPolyline(out pb);
            if (aPoly && bPoly)
            {
                // Same corners, ignoring collinear vertices AxisVM split points left behind.
                foreach (Point3d p in pa) if (pb.ClosestPoint(p).DistanceTo(p) > tol) return false;
                foreach (Point3d p in pb) if (pa.ClosestPoint(p).DistanceTo(p) > tol) return false;
                return true;
            }
            return a.PointAtNormalizedLength(0.5).DistanceTo(b.PointAtNormalizedLength(0.5)) <= tol;
        }

        private static void ApplyDomain(RhinoDoc doc, JToken e, double scale, double tol, LoadResult res, List<string> created)
        {
            if (!Changed(e)) { res.Unchanged++; return; }
            string guid = Str(e, "guid");
            string baseId = BaseId(guid);
            bool face = baseId != guid;
            RhinoObject obj = Find(doc, baseId);

            Brep brep = MakeBrep(e, scale, tol);
            if (obj == null)
            {
                if (!IsNew(e))
                {
                    res.Notes.Add("Surface " + Label(guid) + ": changed in AxisVM but no longer in Rhino - skipped.");
                    return;
                }
                if (brep == null) { res.Notes.Add("Surface " + Label(guid) + ": its outline is not planar - not created."); return; }
                ObjectAttributes a = NewAttributes(doc, guid, DomainsLayer);
                SetUserText(a, e["attrs"], scale);
                Guid id = doc.Objects.AddBrep(brep, a);
                if (id != Guid.Empty) { res.Created++; created.Add(guid); }
                return;
            }

            if (brep != null && GeomChanged(e) && !SameOutline(obj, brep, tol))
            {
                if (face)
                    res.Notes.Add("Surface " + Label(guid) + ": one face of a polysurface was reshaped in AxisVM; its shape is not updated here (properties are).");
                else
                {
                    doc.Objects.Replace(obj.Id, brep);
                    res.Moved++;
                    obj = doc.Objects.FindId(obj.Id);
                }
            }
            if (UpdateUserText(doc, obj, e["attrs"], scale)) res.Updated++;
        }

        private static Brep MakeBrep(JToken e, double scale, double tol)
        {
            if (!(e["contour"] is JArray contour) || contour.Count < 3) return null;
            List<Curve> loops = new List<Curve> { Loop(contour, scale) };
            foreach (JToken h in (e["holes"] as JArray) ?? new JArray())
                if (h is JArray hole && hole.Count >= 3) loops.Add(Loop(hole, scale));
            Brep[] breps = Brep.CreatePlanarBreps(loops, tol);
            return breps != null && breps.Length > 0 ? breps[0] : null;
        }

        private static Curve Loop(JArray pts, double scale)
        {
            List<Point3d> p = pts.Select(t => Pt(t, scale)).ToList();
            p.Add(p[0]);
            return new PolylineCurve(p);
        }

        /// <summary>Same outline (every outline vertex of each lies on the other's edges) within tolerance.</summary>
        private static bool SameOutline(RhinoObject obj, Brep b, double tol)
        {
            Brep a = obj.Geometry as Brep ?? (obj.Geometry is Extrusion x ? x.ToBrep() : obj.Geometry is Surface s ? s.ToBrep() : null);
            if (a == null) return false;
            Func<Brep, List<Curve>> edges = br => br.Edges.Select(ed => (Curve)ed.DuplicateCurve()).ToList();
            List<Curve> ea = edges(a), eb = edges(b);
            Func<Point3d, List<Curve>, bool> on = (p, list) => list.Any(c => { double t; return c.ClosestPoint(p, out t) && c.PointAt(t).DistanceTo(p) <= tol; });
            foreach (BrepVertex v in a.Vertices) if (!on(v.Location, eb)) return false;
            foreach (BrepVertex v in b.Vertices) if (!on(v.Location, ea)) return false;
            return true;
        }

        private static void ApplyPoint(RhinoDoc doc, JToken e, double scale, LoadResult res)
        {
            if (!Changed(e)) { res.Unchanged++; return; }
            RhinoObject obj = Find(doc, Str(e, "guid"));
            if (obj == null || e["xyz"] == null) return;
            Point3d p = Pt(e["xyz"], scale);
            Point old = obj.Geometry as Point;
            if (old != null && old.Location.DistanceTo(p) <= doc.ModelAbsoluteTolerance) return;
            doc.Objects.Replace(obj.Id, p);
            res.Moved++;
        }

        private static ObjectAttributes NewAttributes(RhinoDoc doc, string guid, string layer)
        {
            ObjectAttributes a = doc.CreateDefaultAttributes();
            Guid id;
            if (Guid.TryParse(guid, out id) && doc.Objects.FindId(id) == null) a.ObjectId = id;
            int li = AxLayers.EnsureLayer(doc, layer);
            if (li >= 0) a.LayerIndex = li;
            a.SetUserString(AxKeys.Guid, guid);
            return a;
        }

        /// <summary>Writes the sent Ax_* values; Ax_Thickness arrives in metres and is stored in model units.</summary>
        private static void SetUserText(ObjectAttributes a, JToken attrs, double scale)
        {
            if (!(attrs is JObject o)) return;
            foreach (JProperty p in o.Properties())
            {
                string v = p.Value.ToString();
                if (string.Equals(p.Name, AxKeys.Thickness, StringComparison.OrdinalIgnoreCase))
                {
                    double m;
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                        v = (m * scale).ToString("0.###", CultureInfo.InvariantCulture);
                }
                if (v.Length == 0) a.DeleteUserString(p.Name); else a.SetUserString(p.Name, v);
            }
        }

        private static bool UpdateUserText(RhinoDoc doc, RhinoObject obj, JToken attrs, double scale)
        {
            if (obj == null || !(attrs is JObject o) || !o.HasValues) return false;
            ObjectAttributes a = obj.Attributes.Duplicate();
            SetUserText(a, attrs, scale);
            a.SetUserString(AxKeys.Guid, obj.Id.ToString());
            return doc.Objects.ModifyAttributes(obj, a, true);
        }
    }
}
