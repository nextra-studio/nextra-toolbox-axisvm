using System;
using System.Collections.Generic;
using AxisVM;
using AxisVM_plugin.Core;

/*
 * =====================================================================================
 * AXIS TRIAD DRAWER - x / y / z arrows drawn on the AxisVM model
 * =====================================================================================
 *
 * Draws one arrow triad per point on a drawing layer of its own: a shaft, a filled arrowhead and a label per axis,
 * x red, y green, z blue. Layer shapes and texts are not structural (no effect on the analysis). The layer is
 * temporary: Hide deletes it, and Show deletes the previous one first, so at most one exists at a time.
 * =====================================================================================
 */

namespace AxisVM_plugin.Functions
{
    /// <summary>One arrow triad: where, which axes, how long and what the arrows are called.</summary>
    public class AxisTriad
    {
        public Vec3 Origin;
        public LocalFrame Axes;
        public double Size;          // arrow length in m
        public string[] Labels;      // e.g. { "x'", "y'", "z'" }
    }

    public static class AxisTriadDrawer
    {
        /// <summary>Name of the drawing layer; also how an old one is found again.</summary>
        public const string LayerName = "Connection axes (temporary)";

        /// <summary>Axis colours as RGB, shared with the dialog preview: x red, y green, z blue.</summary>
        public static readonly int[] Rgb = { 0xE0201C, 0x1E9E2E, 0x1F5FE0 };

        /// <summary>
        /// Replaces the triad layer with the given triads. Returns null when it worked, otherwise a message.
        /// </summary>
        public static string Show(IAxisVMApplication app, IAxisVMModel model, IList<AxisTriad> triads)
        {
            try
            {
                Hide(model, false);
                if (triads.Count == 0) return "Nothing to draw.";

                IAxisVMLayers layers = model.Layers;
                int layer = layers.AddLayer(LayerName, Colour(Rgb[0]), ELayerPenStyle.lpsSolid, 0.5);
                if (layer <= 0) return "AxisVM refused to add the drawing layer (error " + layer + ").";

                int failed = 0;
                foreach (AxisTriad t in triads)
                {
                    Vec3[] axes = { t.Axes.Ex, t.Axes.Ey, t.Axes.Ez };
                    for (int i = 0; i < 3; i++)
                    {
                        Vec3 a = axes[i], side = axes[(i + 1) % 3];  // the arrowhead lies in the plane of a and the next axis
                        Vec3 tip = t.Origin + t.Size * a;
                        Vec3 neck = t.Origin + (0.78 * t.Size) * a;
                        double half = 0.07 * t.Size;
                        RLayerShapeAttributes attr = new RLayerShapeAttributes
                        {
                            LayerIndex = layer,
                            Colour = Colour(Rgb[i]),
                            PenStyle = ELayerPenStyle.lpsSolid,
                            PenWidth = 0.7
                        };

                        if (AddShape(app, layers, ELayerShapeType.lst3DLine, attr, t.Origin, neck) <= 0) failed++;
                        if (AddShape(app, layers, ELayerShapeType.lst3DPolygonFilled, attr,
                                tip, neck + half * side, neck - half * side, tip) <= 0) failed++;
                        if (AddLabel(layers, layer, Colour(Rgb[i]), t.Labels[i], tip + (0.12 * t.Size) * a, a, side, 0.16 * t.Size) <= 0) failed++;
                    }
                }
                Redraw(model);
                return failed == 0 ? null : failed + " arrow part(s) could not be drawn.";
            }
            catch (Exception ex)
            {
                return "Could not draw the axes: " + ex.Message;
            }
        }

        /// <summary>Deletes the triad layer (and everything on it) if there is one.</summary>
        public static void Hide(IAxisVMModel model, bool redraw = true)
        {
            try
            {
                IAxisVMLayers layers = model.Layers;
                bool any = false;
                for (int i = layers.Count; i >= 1; i--)
                {
                    string name;
                    try { name = layers.get_Name(i); } catch { continue; }
                    if (name != LayerName) continue;
                    layers.DeleteLayer(i);
                    any = true;
                }
                if (any && redraw) Redraw(model);
            }
            catch { }
        }

        /// <summary>A closed or open 3D polyline through the given points.</summary>
        private static int AddShape(IAxisVMApplication app, IAxisVMLayers layers, ELayerShapeType type, RLayerShapeAttributes attr, params Vec3[] pts)
        {
            AxisVMLines3d poly = NewLines(app);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                RLine3d seg = new RLine3d { LineType = ELine3dType.ltStraightLine3d, P1 = P(pts[i]), P2 = P(pts[i + 1]) };
                poly.Add(ref seg);
            }
            return layers.AddShape(type, poly, ref attr);
        }

        /// <summary>A text whose baseline runs along the axis, so it reads in the axis direction.</summary>
        private static int AddLabel(IAxisVMLayers layers, int layer, int colour, string text, Vec3 at, Vec3 along, Vec3 up, double height)
        {
            RLayerTextParams p = new RLayerTextParams
            {
                LayerIndex = layer,
                Colour = colour,
                PenStyle = ELayerPenStyle.lpsSolid,
                PenWidth = 0.5,
                FontSize = height,
                Angle = 0,
                AlignmentPoint1 = P(at),
                AlignmentPoint2 = P(at + height * along),
                NormalVector = P(Vec3.Cross(along, up).Normalized()),
                HorizontalAlignment = ETextHorizontalAlignment.thaLeft,
                VerticalAlignment = ETextVerticalAlignment.tvaCenter
            };
            return layers.AddText(text, "Arial", ref p);
        }

        /// <summary>A polyline object owned by this AxisVM (falls back to a plain new one).</summary>
        private static AxisVMLines3d NewLines(IAxisVMApplication app)
        {
            try { AxisVMLines3d l = app.ObjectCreator.NewLines3d(); if (l != null) return l; } catch { }
            return new AxisVMLines3d();
        }

        private static void Redraw(IAxisVMModel model)
        {
            try { model.Windows.ReDraw(); } catch { }
            try { model.Refresh(); } catch { }
        }

        private static RPoint3d P(Vec3 v) { return new RPoint3d { x = v.X, y = v.Y, z = v.Z }; }

        /// <summary>RGB -> the colour int AxisVM expects (Windows COLORREF order, 0x00BBGGRR).</summary>
        private static int Colour(int rgb) { return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF); }
    }
}
