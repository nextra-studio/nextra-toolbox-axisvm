using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AxisBridge.Attributes;
using AxisBridge.Layers;
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
        public List<string> Warnings = new List<string>();
        public string FilePath;
    }

    public static class ModelExporter
    {
        public static ExportResult Export(RhinoDoc doc, string filePath, bool selectedOnly = false)
        {
            ExportResult res = new ExportResult { FilePath = filePath };

            ExportModelDto model = new ExportModelDto
            {
                Schema = 1,
                Source = new SourceDto
                {
                    File = doc.Path ?? "",
                    UnitSystem = doc.ModelUnitSystem.ToString(),
                    Tolerance = doc.ModelAbsoluteTolerance
                }
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
                string guid = obj.Id.ToString();
                attrs[AxKeys.Guid] = guid;

                GeometryBase geom = obj.Geometry;

                // 1. Points
                if (geom is Point || attrs.ContainsKey(AxKeys.Support) || attrs.ContainsKey(AxKeys.NodalLoad))
                {
                    if (GeometryFlattener.TryFlattenPoint(geom, out double[] xyz))
                    {
                        model.Points.Add(new PointDto
                        {
                            Guid = guid,
                            Xyz = xyz,
                            Attrs = attrs
                        });
                        res.PointCount++;
                        continue;
                    }
                }

                // 2. Domains (Brep, Surface, Extrusion, or closed curve marked with Ax_FEType)
                if (geom is Brep || geom is Surface || geom is Extrusion || (geom is Curve c && c.IsClosed && attrs.ContainsKey(AxKeys.FeType)))
                {
                    if (GeometryFlattener.TryFlattenDomain(geom, tol, out List<double[]> contour, out List<string> domainWarnings))
                    {
                        foreach (string w in domainWarnings)
                            res.Warnings.Add($"Object {guid.Substring(0, 8)}: {w}");

                        model.Domains.Add(new DomainDto
                        {
                            Guid = guid,
                            Contour = contour,
                            Attrs = attrs
                        });
                        res.DomainCount++;
                        continue;
                    }
                }

                // 3. Curves / Lines
                if (geom is Curve)
                {
                    if (GeometryFlattener.TryFlattenCurve(geom, tol, out List<FlattenedLine> lines, out List<string> curveWarnings))
                    {
                        foreach (string w in curveWarnings)
                            res.Warnings.Add($"Object {guid.Substring(0, 8)}: {w}");

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var l = lines[i];
                            string segGuid = lines.Count == 1 ? guid : $"{guid}_{i}";
                            var segAttrs = new Dictionary<string, string>(attrs, StringComparer.OrdinalIgnoreCase);
                            segAttrs[AxKeys.Guid] = segGuid;

                            model.Lines.Add(new LineDto
                            {
                                Guid = segGuid,
                                Geom = l.Geom,
                                Start = l.Start,
                                End = l.End,
                                ArcMid = l.ArcMid,
                                Attrs = segAttrs
                            });
                            res.LineCount++;
                        }
                    }
                }
            }

            model.Warnings = res.Warnings;

            string json = JsonConvert.SerializeObject(model, Formatting.Indented);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            return res;
        }
    }
}
