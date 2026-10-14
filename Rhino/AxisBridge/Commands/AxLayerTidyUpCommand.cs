using System.Collections.Generic;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Moves every Ax-tagged object into a tidy layer tree:
    ///   AxLayer :: AxLine|AxDomain|AxLoadPanel|AxPoint :: &lt;structural type&gt;
    /// Object attributes, user strings and Ax_Guid are preserved. The AxVisualize tag tree is skipped.
    /// </summary>
    public class AxLayerTidyUpCommand : Command
    {
        public override string EnglishName => "AxLayerTidyUp";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            var counts = new SortedDictionary<string, int>();
            int moved = 0;

            foreach (RhinoObject obj in doc.Objects.FindByFilter(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true
            }))
            {
                if (obj == null || obj.Geometry == null) continue;
                if (AxLayers.IsOnTagTree(doc, obj)) continue;

                Dictionary<string, string> attrs = AttrReader.GetAxAttributes(obj);
                if (attrs.Count == 0) continue;

                string fullPath = TargetLayer(obj, attrs);
                int li = AxLayers.EnsureLayer(doc, fullPath);
                if (li < 0) continue;

                if (obj.Attributes.LayerIndex != li)
                {
                    ObjectAttributes a = obj.Attributes.Duplicate();
                    a.LayerIndex = li;
                    if (doc.Objects.ModifyAttributes(obj, a, true))
                    {
                        moved++;
                        counts.TryGetValue(fullPath, out int c);
                        counts[fullPath] = c + 1;
                    }
                }
            }

            doc.Views.Redraw();

            RhinoApp.WriteLine($"AxLayerTidyUp: moved {moved} object(s).");
            foreach (var kv in counts)
                RhinoApp.WriteLine($"  {kv.Key}  ->  {kv.Value}");

            return Result.Success;
        }

        private static string TargetLayer(RhinoObject obj, Dictionary<string, string> attrs)
        {
            string S = AxLayers.Sep;
            switch (AxClassify.KindOf(obj, attrs))
            {
                case AxGeomKind.Point:
                    return AxLayers.TidyRoot + S + AxLayers.TidyPoints + S + AxClassify.PointTypeName(attrs);
                case AxGeomKind.Domain:
                    // Load panels are not domains in AxisVM, so they get their own branch.
                    return AxLayers.TidyRoot + S + (AxClassify.IsLoadPanel(attrs) ? AxLayers.TidyPanels : AxLayers.TidyDomains)
                         + S + AxClassify.DomainTypeName(attrs);
                case AxGeomKind.Line:
                    return AxLayers.TidyRoot + S + AxLayers.TidyLines + S + AxClassify.LineTypeName(attrs);
                default:
                    return AxLayers.TidyRoot + S + "Other";
            }
        }
    }
}
