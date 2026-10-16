using System;
using System.Drawing;
using Rhino;
using Rhino.DocObjects;

namespace AxisBridge.Layers
{
    /// <summary>
    /// Colours of the AxLayerTidyUp tree, one per element type, and see-through slabs and load panels so they do not hide
    /// the members behind them. Every layer under AxLayer::&lt;group&gt;::&lt;type&gt; (and below) gets its type's colour; the
    /// Domains and LoadPanels layers also get a transparent render material in that colour (Rhino has no layer
    /// transparency of its own; the shaded and rendered views take it from the material).
    /// </summary>
    public static class AxLayerStyle
    {
        public static void Apply(RhinoDoc doc)
        {
            if (doc == null) return;
            for (int i = 0; i < doc.Layers.Count; i++)
            {
                Layer l = doc.Layers[i];
                if (l == null || l.IsDeleted) continue;
                string[] path = l.FullPath.Split(new[] { AxLayers.Sep }, StringSplitOptions.None);
                if (path.Length < 2 || !path[0].Equals(AxLayers.TidyRoot, StringComparison.OrdinalIgnoreCase)) continue;

                string group = path[1], type = path.Length > 2 ? path[2] : "";
                Color? opaque = ColorOf(group, type);
                if (!opaque.HasValue) continue;

                // Shaded views take see-through from the layer colour's alpha, rendered views from the material.
                double transparency = TransparencyOf(group);
                Color color = transparency > 0 ? Color.FromArgb((int)Math.Round(255 * (1 - transparency * 0.85)), opaque.Value) : opaque.Value;

                bool changed = false;
                if (l.Color.ToArgb() != color.ToArgb()) { l.Color = color; changed = true; }

                if (transparency > 0)
                {
                    int mi = MaterialFor(doc, group + " " + type, opaque.Value, transparency);
                    if (mi >= 0 && l.RenderMaterialIndex != mi) { l.RenderMaterialIndex = mi; changed = true; }
                }
                if (changed) l.CommitChanges();
            }
        }

        private static Color? ColorOf(string group, string type)
        {
            if (group.Equals(AxLayers.TidyLines, StringComparison.OrdinalIgnoreCase))
            {
                switch (type)
                {
                    case "Beam": return Color.FromArgb(0, 102, 180);
                    case "Rib": return Color.FromArgb(0, 150, 136);
                    case "Truss": return Color.FromArgb(230, 126, 34);
                    case "Simple": return Color.FromArgb(120, 120, 120);
                    case "Spring": return Color.FromArgb(142, 68, 173);
                    case "RigidLink": return Color.FromArgb(192, 57, 43);
                    case "NodeLink": return Color.FromArgb(155, 89, 182);
                    case "EdgeHinge": return Color.FromArgb(211, 84, 0);
                    default: return Color.FromArgb(80, 80, 80);
                }
            }
            if (group.Equals(AxLayers.TidyDomains, StringComparison.OrdinalIgnoreCase))
            {
                switch (type)
                {
                    case "Plate": return Color.FromArgb(91, 155, 213);
                    case "Membrane": return Color.FromArgb(171, 130, 200);
                    default: return Color.FromArgb(112, 173, 71);   // Shell
                }
            }
            if (group.Equals(AxLayers.TidyPanels, StringComparison.OrdinalIgnoreCase))
                return type.Equals("OneWay", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(244, 162, 54) : Color.FromArgb(241, 196, 15);
            if (group.Equals(AxLayers.TidyPoints, StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb(200, 30, 30);
            return null;
        }

        private static double TransparencyOf(string group)
        {
            if (group.Equals(AxLayers.TidyDomains, StringComparison.OrdinalIgnoreCase)) return 0.6;
            if (group.Equals(AxLayers.TidyPanels, StringComparison.OrdinalIgnoreCase)) return 0.75;
            return 0;
        }

        /// <summary>A named document material "AxLayer &lt;group&gt; &lt;type&gt;" in the colour, made transparent; reused when it exists.</summary>
        private static int MaterialFor(RhinoDoc doc, string name, Color color, double transparency)
        {
            string full = "AxLayer " + name.Trim();
            int index = doc.Materials.Find(full, true);
            Material m = index >= 0 ? doc.Materials[index] : new Material();
            m.Name = full;
            m.DiffuseColor = color;
            m.Transparency = transparency;
            if (index >= 0)
            {
                doc.Materials.Modify(m, index, true);
                return index;
            }
            return doc.Materials.Add(m);
        }
    }
}
