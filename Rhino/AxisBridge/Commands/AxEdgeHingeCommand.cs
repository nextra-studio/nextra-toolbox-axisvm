using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Creates edge hinges in three steps:
    ///   1. pick the edges (surface / polysurface edges or existing curves),
    ///   2. pick the domain(s) to release - or Enter for every domain along the edge,
    ///   3. choose the hinge type in a small dialog.
    /// Each edge becomes a NEW curve on AxLayer::AxLine::EdgeHinge tagged Ax_Type=EdgeHinge, so the
    /// original slab edges and member curves are never changed.
    /// </summary>
    public class AxEdgeHingeCommand : Command
    {
        public override string EnglishName => "AxEdgeHinge";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // ---- 1. edges ---------------------------------------------------
            var ge = new GetObject();
            ge.SetCommandPrompt("Select the edges to hinge (surface edges or curves)");
            ge.GeometryFilter = ObjectType.EdgeFilter | ObjectType.Curve;
            ge.SubObjectSelect = true;
            ge.GetMultiple(1, 0);
            if (ge.CommandResult() != Result.Success) return ge.CommandResult();

            var curves = new List<Curve>();
            for (int i = 0; i < ge.ObjectCount; i++)
            {
                ObjRef r = ge.Object(i);
                Curve c = r.Edge() != null ? r.Edge().DuplicateCurve() : r.Curve()?.DuplicateCurve();
                if (c != null) curves.Add(c);
            }
            if (curves.Count == 0) return Result.Nothing;

            // ---- 2. domains -------------------------------------------------
            var gd = new GetObject();
            gd.SetCommandPrompt("Select the domain(s) to release along these edges - press Enter for ALL domains along them");
            gd.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
            gd.SubObjectSelect = true;          // a single face of a polysurface may be picked
            gd.EnablePreSelect(false, true);
            gd.DeselectAllBeforePostSelect = true;
            gd.AcceptNothing(true);
            GetResult gr = gd.GetMultiple(1, 0);
            if (gr == GetResult.Cancel) return Result.Cancel;

            var domains = new List<string>();
            if (gr == GetResult.Object)
            {
                for (int i = 0; i < gd.ObjectCount; i++)
                {
                    ObjRef r = gd.Object(i);
                    string id = r.ObjectId.ToString();
                    // A picked face of a multi-face polysurface is exported as "<id>_f<faceIndex>".
                    BrepFace face = r.Face();
                    Brep brep = r.Brep();
                    if (face != null && brep != null && brep.Faces.Count > 1) id += "_f" + face.FaceIndex;
                    if (!domains.Contains(id)) domains.Add(id);
                }
            }

            string summary = domains.Count == 0 ? "automatic (every domain along the edges)" : domains.Count + " picked domain(s)";

            // ---- 3. hinge type ----------------------------------------------
            string stiffness;
            using (var f = new EdgeHingeForm(curves.Count, summary))
            {
                if (f.ShowDialog(AttrCommandRunner.RhinoOwner()) != DialogResult.OK || f.Stiffness == null) return Result.Cancel;
                stiffness = f.Stiffness;
            }

            // ---- create the hinge curves ------------------------------------
            int layer = AxLayers.EnsureLayer(doc,
                AxLayers.TidyRoot + AxLayers.Sep + AxLayers.TidyLines + AxLayers.Sep + AxSchema.EdgeHinge,
                System.Drawing.Color.FromArgb(200, 60, 160));

            var created = new List<RhinoObject>();
            foreach (Curve c in curves)
            {
                var oa = new ObjectAttributes();
                if (layer >= 0) oa.LayerIndex = layer;
                Guid id = doc.Objects.AddCurve(c, oa);
                RhinoObject o = doc.Objects.FindId(id);
                if (o != null) created.Add(o);
            }

            var edit = new AttrEdit();
            edit.Scalars[AxKeys.Type] = AxSchema.EdgeHinge;
            edit.Scalars[AxKeys.Stiffness] = stiffness;
            edit.Scalars[AxKeys.HingeDomains] = string.Join("; ", domains.ToArray());
            AttrWriter.ApplyEdit(doc, created, edit, AxGeomKind.Line);

            doc.Objects.UnselectAll();
            foreach (RhinoObject o in created) o.Select(true);
            doc.Views.Redraw();

            RhinoApp.WriteLine(string.Format("AxEdgeHinge: created {0} edge hinge line(s); released domains: {1}. Edit them later with AxLine.",
                created.Count, summary));
            return Result.Success;
        }
    }
}
