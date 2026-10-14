using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>Assigns member attributes (type, profile, releases, springs/links, line support, loads) to curves.</summary>
    public class AxLineCommand : Command
    {
        public override string EnglishName => "AxLine";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            return AttrCommandRunner.Run(doc,
                "Select curves for structural member attributes",
                ObjectType.Curve, null, AxGeomKind.Line,
                LineAttrForm.ScalarKeys, LineAttrForm.FamilyKeys,
                (state, lib, d) => new LineAttrForm(state, lib, d), "curve(s)");
        }
    }
}
