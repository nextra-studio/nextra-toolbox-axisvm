using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>Assigns node attributes (support, nodal loads, parts) to points.</summary>
    public class AxPointCommand : Command
    {
        public override string EnglishName => "AxPoint";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            return AttrCommandRunner.Run(doc,
                "Select points for support / nodal load attributes",
                ObjectType.Point, null, AxGeomKind.Point,
                PointAttrForm.ScalarKeys, PointAttrForm.FamilyKeys,
                (state, lib, d) => new PointAttrForm(state, lib, d), "point(s)");
        }
    }
}
