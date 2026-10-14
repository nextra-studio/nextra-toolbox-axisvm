using System;
using System.Drawing;
using Rhino;
using Rhino.DocObjects;

namespace AxisBridge.Layers
{
    /// <summary>
    /// Layer names owned by the plugin and helpers to create / find / delete the layer trees that
    /// AxVisualize (AxTag::*) and AxLayerTidyUp (AxLayer::*) build.
    /// </summary>
    public static class AxLayers
    {
        public const string Sep = "::";

        /// <summary>Parent layer for AxVisualize text-dot tags.</summary>
        public const string TagRoot = "AxTag";

        /// <summary>Parent layer for the AxLayerTidyUp organised copy of the model.</summary>
        public const string TidyRoot = "AxLayer";
        public const string TidyLines = "AxLine";
        public const string TidyDomains = "AxDomain";
        public const string TidyPanels = "AxLoadPanel";
        public const string TidyPoints = "AxPoint";

        /// <summary>True if the object sits anywhere under the AxTag layer tree.</summary>
        public static bool IsOnTagTree(RhinoDoc doc, RhinoObject obj)
        {
            return IsUnder(doc, obj, TagRoot);
        }

        public static bool IsOnTidyTree(RhinoDoc doc, RhinoObject obj)
        {
            return IsUnder(doc, obj, TidyRoot);
        }

        public static bool IsUnder(RhinoDoc doc, RhinoObject obj, string rootName)
        {
            if (doc == null || obj == null) return false;
            Layer layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
            if (layer == null) return false;
            string path = layer.FullPath;
            return path.Equals(rootName, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(rootName + Sep, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds or creates the layer at the given "A::B::C" full path, creating every missing level,
        /// and returns the leaf layer index. <paramref name="leafColor"/> is applied to the leaf only.
        /// </summary>
        public static int EnsureLayer(RhinoDoc doc, string fullPath, Color? leafColor = null)
        {
            if (doc == null || string.IsNullOrEmpty(fullPath)) return -1;

            string[] parts = fullPath.Split(new[] { Sep }, StringSplitOptions.RemoveEmptyEntries);
            Guid parentId = Guid.Empty;
            string running = null;
            int index = -1;

            foreach (string part in parts)
            {
                running = running == null ? part : running + Sep + part;
                int found = doc.Layers.FindByFullPath(running, RhinoMath.UnsetIntIndex);
                if (found >= 0)
                {
                    index = found;
                    parentId = doc.Layers[found].Id;
                    continue;
                }

                Layer layer = new Layer { Name = part };
                if (parentId != Guid.Empty) layer.ParentLayerId = parentId;
                index = doc.Layers.Add(layer);
                if (index < 0) return -1;
                parentId = doc.Layers[index].Id;
            }

            if (leafColor.HasValue && index >= 0)
            {
                Layer leaf = doc.Layers[index];
                leaf.Color = leafColor.Value;
            }

            return index;
        }

        /// <summary>Deletes the whole "rootName::*" tree, purging any objects on it first.</summary>
        public static int DeleteTree(RhinoDoc doc, string rootName)
        {
            if (doc == null) return 0;

            int rootIndex = doc.Layers.FindByFullPath(rootName, RhinoMath.UnsetIntIndex);
            if (rootIndex < 0) return 0;

            Guid rootId = doc.Layers[rootIndex].Id;

            // Collect the root plus every descendant.
            var toDelete = new System.Collections.Generic.List<int>();
            for (int i = 0; i < doc.Layers.Count; i++)
            {
                Layer l = doc.Layers[i];
                if (l.IsDeleted) continue;
                if (i == rootIndex ||
                    l.FullPath.StartsWith(rootName + Sep, StringComparison.OrdinalIgnoreCase))
                    toDelete.Add(i);
            }

            // Purge objects on those layers.
            int objectsRemoved = 0;
            foreach (int li in toDelete)
            {
                RhinoObject[] objs = doc.Objects.FindByLayer(doc.Layers[li]);
                if (objs == null) continue;
                foreach (RhinoObject o in objs)
                {
                    if (doc.Objects.Delete(o, true)) objectsRemoved++;
                }
            }

            // Delete deepest layers first.
            toDelete.Sort((a, b) => doc.Layers[b].FullPath.Length.CompareTo(doc.Layers[a].FullPath.Length));
            foreach (int li in toDelete)
                doc.Layers.Delete(li, true);

            return objectsRemoved;
        }
    }
}
