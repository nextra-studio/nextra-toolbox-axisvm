using AxisBridge.UI;
using AxisBridge.Visualize;
using Rhino;
using Rhino.Commands;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Toggles the AxVisualize window and its display-only overlay (supports, releases, load arrows of
    /// one load case, element info). Nothing is added to the document, so nothing is exported.
    /// </summary>
    public class AxVisualizeCommand : Command
    {
        public override string EnglishName => "AxVisualize";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            // Files saved with the old text-dot tags: remove them, the overlay replaces them.
            if (TagBuilder.IsOn(doc))
            {
                int removed = TagBuilder.Clear(doc);
                RhinoApp.WriteLine($"AxVisualize: removed {removed} old text-dot tag(s) and the AxTag layers.");
            }

            bool wasOpen = VisualizeForm.Current != null;
            VisualizeForm.Toggle();
            RhinoApp.WriteLine(wasOpen ? "AxVisualize OFF." : "AxVisualize ON: pick what to show in the AxVisualize window.");
            return Result.Success;
        }
    }
}
