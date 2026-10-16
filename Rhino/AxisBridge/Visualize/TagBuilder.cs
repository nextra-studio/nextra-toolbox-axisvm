using AxisBridge.Layers;
using Rhino;

namespace AxisBridge.Visualize
{
    /// <summary>
    /// Clean-up of the old AxVisualize text-dot tags (the "AxTag" layer tree). The overlay is now drawn by
    /// <see cref="AxVisualConduit"/>; this only removes tags left in files saved with an earlier version.
    /// </summary>
    public static class TagBuilder
    {
        public static bool IsOn(RhinoDoc doc)
        {
            return doc != null && doc.Layers.FindByFullPath(AxLayers.TagRoot, RhinoMath.UnsetIntIndex) >= 0;
        }

        public static int Clear(RhinoDoc doc)
        {
            int removed = AxLayers.DeleteTree(doc, AxLayers.TagRoot);
            doc?.Views.Redraw();
            return removed;
        }
    }
}
