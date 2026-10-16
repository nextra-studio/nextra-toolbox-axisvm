using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Commands
{
    /// <summary>Assigns surface attributes (domain or load panel, loads) to surfaces, polysurfaces, meshes or closed curves.</summary>
    public class AxDomainCommand : Command
    {
        public override string EnglishName => "AxDomain";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // A mesh is exported by its outline: each flat region becomes one domain, which AxisVM meshes itself.
            return AttrCommandRunner.Run(doc,
                "Select planar surfaces, polysurfaces, meshes or closed planar curves for domain / load panel attributes",
                ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.Curve,
                (obj, geom, ci) => !(geom is Curve c) || c.IsClosed,
                AxGeomKind.Domain,
                DomainAttrForm.ScalarKeys, DomainAttrForm.FamilyKeys,
                (state, lib, d) => new DomainAttrForm(state, lib, d), "surface(s)");
        }
    }
}
