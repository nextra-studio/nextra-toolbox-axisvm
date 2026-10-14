using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Commands
{
    /// <summary>Assigns surface attributes (domain or load panel, loads) to surfaces, polysurfaces or closed curves.</summary>
    public class AxDomainCommand : Command
    {
        public override string EnglishName => "AxDomain";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Meshes are not offered: AxisVM domains are built from boundary lines, which a mesh does not have.
            return AttrCommandRunner.Run(doc,
                "Select planar surfaces, polysurfaces or closed planar curves for domain / load panel attributes",
                ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Curve,
                (obj, geom, ci) => !(geom is Curve c) || c.IsClosed,
                AxGeomKind.Domain,
                DomainAttrForm.ScalarKeys, DomainAttrForm.FamilyKeys,
                (state, lib, d) => new DomainAttrForm(state, lib, d), "surface(s)");
        }
    }
}
