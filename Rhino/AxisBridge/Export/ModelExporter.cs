using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using AxisBridge.Library;
using AxisBridge.Loads;
using Newtonsoft.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Export
{
    public class ExportResult
    {
        public int PointCount;
        public int LineCount;
        public int DomainCount;
        public int SkippedUntagged;
        public List<string> Warnings = new List<string>();
        public string FilePath;
    }

    /// <summary>
    /// Writes the AxisBridge JSON that the AxisVM "N. Rhino Import" addon reads (schema 2).
    /// </summary>
    public static class ModelExporter
    {
        /// <param name="selectedOnly">Only the current selection.</param>
        /// <param name="includeUntagged">Also export objects without Ax_* attributes (as plain nodes / lines / shells).</param>
        public static ExportResult Export(RhinoDoc doc, string filePath, bool selectedOnly, bool includeUntagged)
        {
            ExportResult res = new ExportResult { FilePath = filePath };

            ExportModelDto model = new ExportModelDto
            {
                Source = new SourceDto
                {
                    File = doc.Path ?? "",
                    UnitSystem = doc.ModelUnitSystem.ToString(),
                    Tolerance = doc.ModelAbsoluteTolerance
                },
                Library = AxLibrary.Load(doc)
            };

            IEnumerable<RhinoObject> objects = selectedOnly
                ? doc.Objects.GetSelectedObjects(false, false)
                : doc.Objects.FindByFilter(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true });

            double tol = doc.ModelAbsoluteTolerance;

            foreach (RhinoObject obj in objects)
            {
                if (obj == null || obj.Geometry == null) continue;

                // AxVisualize text-dot tags are throwaway annotation - never part of the model.
                if (obj.Geometry is TextDot) continue;
                if (AxLayers.IsOnTagTree(doc, obj)) continue;

                var attrs = AttrReader.GetAxAttributes(obj);
                if (!includeUntagged && !AxClassify.IsTagged(attrs))
                {
                    if (obj.Geometry is Point || obj.Geometry is Curve || obj.Geometry is Brep || obj.Geometry is Surface || obj.Geometry is Extrusion)
                        res.SkippedUntagged++;
                    continue;
                }

                string guid = obj.Id.ToString();
                attrs[AxKeys.Guid] = guid;
                string label = ObjLabel(obj);

                if (obj.Geometry is Mesh)
                {
                    res.Warnings.Add(label + ": meshes are not supported - convert it to a planar surface. Skipped.");
                    continue;
                }

                switch (AxClassify.KindOf(obj, attrs))
                {
                    case AxGeomKind.Point:
                    {
                        double[] xyz;
                        if (GeometryFlattener.TryFlattenPoint(obj.Geometry, out xyz))
                        {
                            model.Points.Add(new PointDto { Guid = guid, Xyz = xyz, Attrs = attrs });
                            res.PointCount++;
                        }
                        break;
                    }

                    case AxGeomKind.Domain:
                        ExportDomain(obj, guid, attrs, tol, label, model, res);
                        break;

                    case AxGeomKind.Line:
                    {
                        FlattenedLine fl;
                        string warn;
                        if (!GeometryFlattener.TryFlattenCurve((Curve)obj.Geometry, tol, out fl, out warn))
                        {
                            res.Warnings.Add(label + ": the curve could not be converted. Skipped.");
                            break;
                        }
                        if (warn != null) res.Warnings.Add(label + ": " + warn + ".");

                        model.Lines.Add(new LineDto
                        {
                            Guid = guid,
                            Geom = fl.Geom,
                            Start = fl.Start,
                            End = fl.End,
                            ArcMid = fl.ArcMid,
                            Points = fl.Points,
                            Attrs = attrs
                        });
                        res.LineCount++;
                        break;
                    }

                    default:
                        if (AxClassify.IsTagged(attrs))
                            res.Warnings.Add(label + ": this geometry type (" + obj.Geometry.ObjectType + ") cannot be exported. Skipped.");
                        break;
                }
            }

            model.Warnings = res.Warnings;

            string json = JsonConvert.SerializeObject(model, Formatting.Indented);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            return res;
        }

        /// <summary>
        /// One DomainDto per planar face. A polysurface's faces get "id_f0", "id_f1"... and share the
        /// attributes - except surface point loads, which go only to the face nearest to the load.
        /// </summary>
        private static void ExportDomain(RhinoObject obj, string guid, Dictionary<string, string> attrs, double tol,
                                         string label, ExportModelDto model, ExportResult res)
        {
            List<FlattenedFace> faces;
            List<string> warnings;
            if (!GeometryFlattener.TryFlattenDomain(obj.Geometry, tol, out faces, out warnings))
            {
                res.Warnings.Add(label + ": no planar boundary could be extracted. Skipped.");
                return;
            }
            foreach (string w in warnings) res.Warnings.Add(label + ": " + w + ".");

            List<string> pointLoads = AttrReader.GetFamily(attrs, AxKeys.PointLoad);
            int[] owner = new int[pointLoads.Count];
            if (faces.Count > 1)
                for (int i = 0; i < pointLoads.Count; i++) owner[i] = NearestFace(faces, pointLoads[i]);

            for (int fi = 0; fi < faces.Count; fi++)
            {
                string faceGuid = faces.Count == 1 ? guid : guid + "_f" + fi;
                var fa = new Dictionary<string, string>(attrs, StringComparer.OrdinalIgnoreCase);
                fa[AxKeys.Guid] = faceGuid;

                if (faces.Count > 1)
                {
                    // Rewrite this face's own point load list.
                    foreach (string k in new List<string>(fa.Keys))
                        if (string.Equals(AxKeys.BaseKey(k), AxKeys.PointLoad, StringComparison.OrdinalIgnoreCase)) fa.Remove(k);
                    int n = 0;
                    for (int i = 0; i < pointLoads.Count; i++)
                        if (owner[i] == fi) fa[AxKeys.FamilyKey(AxKeys.PointLoad, n++)] = pointLoads[i];
                }

                model.Domains.Add(new DomainDto
                {
                    Guid = faceGuid,
                    Contour = faces[fi].Outer,
                    Holes = faces[fi].Holes.Count > 0 ? faces[fi].Holes : null,
                    Attrs = fa
                });
                res.DomainCount++;
            }
        }

        private static int NearestFace(List<FlattenedFace> faces, string load)
        {
            LoadRow row;
            if (!LoadRow.TryParse(LoadKind.SurfacePoint, load, out row)) return 0;
            var p = new Point3d(row.X, row.Y, row.Z);

            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < faces.Count; i++)
            {
                BrepFace f = faces[i].Face;
                if (f == null) continue;
                double u, v;
                if (!f.ClosestPoint(p, out u, out v)) continue;
                double d = f.PointAt(u, v).DistanceTo(p);
                if (f.IsPointOnFace(u, v) == PointFaceRelation.Exterior) d += 1e9;   // prefer faces that really contain it
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static string ObjLabel(RhinoObject obj)
        {
            string name = obj.Attributes.Name;
            string id = obj.Id.ToString().Substring(0, 8);
            return string.IsNullOrEmpty(name) ? "Object " + id : "'" + name + "' (" + id + ")";
        }
    }
}
