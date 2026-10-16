using System.Collections.Generic;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// AxPickup: pick an object, tick which of its structural properties to copy (type, section, material, thickness, loads
    /// ...), then select the objects to copy them onto. Only objects of the same geometry kind can be selected - curves
    /// for a curve, surfaces for a surface, points for a point - so a section never lands on a slab. One Undo reverts it.
    /// </summary>
    public class AxPickupCommand : Command
    {
        public override string EnglishName => "AxPickup";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            RhinoObject source;
            using (var go = new GetObject())
            {
                go.SetCommandPrompt("Pick the object to copy properties from");
                go.GeometryFilter = ObjectType.Curve | ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.Point;
                go.SubObjectSelect = false;
                if (go.Get() != GetResult.Object) return Result.Cancel;
                source = go.Object(0).Object();
            }
            if (source == null) return Result.Failure;

            Dictionary<string, string> attrs = AttrReader.GetAxAttributes(source);
            AxGeomKind kind = AxClassify.KindOf(source, attrs);
            if (kind == AxGeomKind.Other) { RhinoApp.WriteLine("AxPickup: this object is not a point, curve or surface."); return Result.Nothing; }
            if (!AxClassify.IsTagged(attrs)) { RhinoApp.WriteLine("AxPickup: the picked object has no Ax_* properties to copy."); return Result.Nothing; }

            List<AxPickup.Group> chosen;
            string label = "this " + (kind == AxGeomKind.Line ? AxClassify.LineTypeName(attrs).ToLowerInvariant()
                                    : kind == AxGeomKind.Domain ? AxClassify.DomainTypeName(attrs).ToLowerInvariant() : "point");
            using (var form = new PickupForm(label, kind, attrs))
            {
                if (form.ShowDialog(AttrCommandRunner.RhinoOwner()) != DialogResult.OK) return Result.Cancel;
                chosen = form.Chosen;
            }
            if (chosen.Count == 0) { RhinoApp.WriteLine("AxPickup: nothing ticked."); return Result.Nothing; }

            var targets = new List<RhinoObject>();
            using (var go = new GetObject())
            {
                go.SetCommandPrompt("Select the " + (kind == AxGeomKind.Line ? "curves" : kind == AxGeomKind.Domain ? "surfaces" : "points") + " to copy onto (Enter when done)");
                go.GeometryFilter = kind == AxGeomKind.Line ? ObjectType.Curve
                                  : kind == AxGeomKind.Point ? ObjectType.Point
                                  : ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Extrusion | ObjectType.Mesh;
                go.SetCustomGeometryFilter((obj, geom, ci) => obj != null && obj.Id != source.Id && AxClassify.KindOf(obj, AttrReader.GetAxAttributes(obj)) == kind);
                go.EnablePreSelect(false, true);
                go.SubObjectSelect = false;
                if (go.GetMultiple(1, 0) != GetResult.Object) return Result.Cancel;
                for (int i = 0; i < go.ObjectCount; i++) targets.Add(go.Object(i).Object());
            }

            uint undo = doc.BeginUndoRecord("AxPickup");
            int changed;
            try { changed = AxPickup.Apply(doc, source, targets, chosen); }
            finally { doc.EndUndoRecord(undo); }
            doc.Views.Redraw();

            RhinoApp.WriteLine("AxPickup: copied " + string.Join(", ", chosen.ConvertAll(g => g.Name)) + " onto " + changed + " object(s).");
            return Result.Success;
        }
    }
}
