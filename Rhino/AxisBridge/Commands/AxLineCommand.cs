using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Assigns member attributes (type, profile, releases, springs/links, edge hinges, line support,
    /// loads) to curves. Picked surface / mesh edges become new edge hinge curves first.
    /// </summary>
    public class AxLineCommand : Command
    {
        public override string EnglishName => "AxLine";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            return AttrCommandRunner.Run(doc,
                "Select curves, or surface / mesh edges for edge hinges",
                ObjectType.Curve, null, AxGeomKind.Line,
                LineAttrForm.ScalarKeys, LineAttrForm.FamilyKeys,
                (state, lib, d) => new LineAttrForm(state, lib, d), "curve(s)", acceptEdges: true);
        }
    }
}
