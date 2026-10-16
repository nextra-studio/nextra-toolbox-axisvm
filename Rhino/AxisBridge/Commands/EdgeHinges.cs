using System;
using System.Collections.Generic;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Edge hinge helpers for AxLine: picked surface / mesh edges become new hinge curves (the slab
    /// edges themselves are never changed), and picked domains become the ids the exporter uses.
    /// </summary>
    internal static class EdgeHinges
    {
        /// <summary>Line hinge: free rotation about the edge only (edge local x), everything else rigid.</summary>
        public const string LineHingeStiffness = "1e10,1e10,1e10,0,1e10,1e10";

        public static readonly string LayerPath =
            AxLayers.TidyRoot + AxLayers.Sep + AxLayers.TidyLines + AxLayers.Sep + AxSchema.EdgeHinge;

        /// <summary>The picked surface or mesh edge as a curve, or null when the pick is not an edge.</summary>
        public static Curve EdgeCurve(ObjRef r)
        {
            if (r.Edge() != null) return r.Edge().DuplicateCurve();
            Mesh m = r.Mesh();
            ComponentIndex ci = r.GeometryComponentIndex;
            if (m == null || ci.ComponentIndexType != ComponentIndexType.MeshTopologyEdge) return null;
            if (ci.Index < 0 || ci.Index >= m.TopologyEdges.Count) return null;
            return new LineCurve(m.TopologyEdges.EdgeLine(ci.Index));
        }

        /// <summary>Adds the edge as a new curve on the hinge layer, tagged as a line hinge.</summary>
        public static RhinoObject AddHingeCurve(RhinoDoc doc, Curve c)
        {
            int layer = AxLayers.EnsureLayer(doc, LayerPath, System.Drawing.Color.FromArgb(200, 60, 160));
            var oa = new ObjectAttributes();
            if (layer >= 0) oa.LayerIndex = layer;
            RhinoObject o = doc.Objects.FindId(doc.Objects.AddCurve(c, oa));
            if (o == null) return null;

            var edit = new AttrEdit();
            edit.Scalars[AxKeys.Type] = AxSchema.EdgeHinge;
            edit.Scalars[AxKeys.Stiffness] = LineHingeStiffness;
            AttrWriter.ApplyEdit(doc, new[] { o }, edit, AxGeomKind.Line);
            return doc.Objects.FindId(o.Id);
        }

        /// <summary>
        /// Lets the user pick the domains a hinge releases. Returns their export ids ("&lt;id&gt;", or
        /// "&lt;id&gt;_f&lt;i&gt;" for one face of a polysurface / one flat region of a mesh), or null when cancelled.
        /// </summary>
        public static List<string> PickDomains(RhinoDoc doc, string prompt)
        {
            var gd = new GetObject();
            gd.SetCommandPrompt(prompt);
            gd.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh;
            gd.SubObjectSelect = true;          // a single face of a polysurface may be picked
            gd.EnablePreSelect(false, true);
            gd.DeselectAllBeforePostSelect = true;
            if (gd.GetMultiple(1, 0) != GetResult.Object) return null;

            var domains = new List<string>();
            for (int i = 0; i < gd.ObjectCount; i++)
            {
                ObjRef r = gd.Object(i);
                string id = r.ObjectId.ToString();
                Mesh mesh = r.Mesh();
                if (mesh != null)
                {
                    foreach (string mid in MeshDomainIds(doc, r, mesh, id))
                        if (!domains.Contains(mid)) domains.Add(mid);
                    continue;
                }
                // A picked face of a multi-face polysurface is exported as "<id>_f<faceIndex>".
                BrepFace face = r.Face();
                Brep brep = r.Brep();
                if (face != null && brep != null && brep.Faces.Count > 1) id += "_f" + face.FaceIndex;
                if (!domains.Contains(id)) domains.Add(id);
            }
            return domains;
        }

        /// <summary>
        /// Domain ids of a picked mesh. Export splits a mesh into one domain per flat region
        /// ("<id>_f<i>" when there are several), so the region nearest the pick point is used; without
        /// a pick point (window selection) every region of the mesh is released.
        /// </summary>
        private static List<string> MeshDomainIds(RhinoDoc doc, ObjRef r, Mesh mesh, string id)
        {
            var ids = new List<string>();
            Brep b = Export.GeometryFlattener.MeshToBrep(mesh, doc.ModelAbsoluteTolerance);
            if (b == null || b.Faces.Count <= 1) { ids.Add(id); return ids; }

            Point3d p = r.SelectionPoint();
            if (!p.IsValid)
            {
                for (int f = 0; f < b.Faces.Count; f++) ids.Add(id + "_f" + f);
                return ids;
            }

            int best = 0;
            double bestD = double.MaxValue;
            foreach (BrepFace f in b.Faces)
            {
                double u, v;
                if (!f.ClosestPoint(p, out u, out v)) continue;
                double d = f.PointAt(u, v).DistanceTo(p);
                if (f.IsPointOnFace(u, v) == PointFaceRelation.Exterior) d += 1e9;
                if (d < bestD) { bestD = d; best = f.FaceIndex; }
            }
            ids.Add(id + "_f" + best);
            return ids;
        }
    }
}
