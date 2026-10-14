using AxisBridge.Visualize;
using Rhino;
using Rhino.Commands;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Toggles the AxVisualize overlay: builds text-dot tags for every Ax_* attribute on their own
    /// "AxTag" child layers, or removes them if already on. The tags are excluded from AxExport.
    /// </summary>
    public class AxVisualizeCommand : Command
    {
        public override string EnglishName => "AxVisualize";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            if (TagBuilder.IsOn(doc))
            {
                int removed = TagBuilder.Clear(doc);
                RhinoApp.WriteLine($"AxVisualize OFF: removed {removed} tag(s) and the AxTag layers.");
                return Result.Success;
            }

            int dots = TagBuilder.Build(doc, out int categories);
            if (dots == 0)
            {
                TagBuilder.Clear(doc);
                RhinoApp.WriteLine("AxVisualize: no Ax_* attributes found to tag.");
                return Result.Nothing;
            }

            RhinoApp.WriteLine($"AxVisualize ON: {dots} tag(s) across {categories} categor(ies). Toggle child layers to filter.");
            return Result.Success;
        }
    }
}
