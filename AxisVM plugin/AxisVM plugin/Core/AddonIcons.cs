using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Text-free 16 px toolbar pictograms. Retains AxisVM's magenta transparency key and the existing
    /// engineering line-art style: grey geometry, blue structure and an orange action or force arrow.
    /// Coordinates are integral and anti-aliasing is off, keeping edges legible at native toolbar size.
    /// </summary>
    public static class AddonIcons
    {
        private static readonly Color Blue = Color.FromArgb(0, 102, 180);
        private static readonly Color Orange = Color.FromArgb(216, 103, 18);
        private static readonly Color Geometry = Color.FromArgb(65, 77, 90);

        public static byte[] OneWayLoadPanel() { return ToBmp(Preview("OneWayLoadPanel")); }
        public static byte[] LoadPanelTools() { return ToBmp(Preview("LoadPanelTools")); }
        public static byte[] EndPinAssign() { return ToBmp(Preview("EndPinAssign")); }
        public static byte[] ReactionTransfer() { return ToBmp(Preview("ReactionTransfer")); }
        public static byte[] SectionModifier() { return ToBmp(Preview("SectionModifier")); }
        public static byte[] ConnectionLoadExtractor() { return ToBmp(Preview("ConnectionLoadExtractor")); }
        public static byte[] RhinoImport() { return ToBmp(Preview("RhinoImport")); }

        /// <summary>Native-size bitmap; callers own it. Documentation uses the same drawing code.</summary>
        public static Bitmap Preview(string name)
        {
            Bitmap bitmap = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Pen blue = new Pen(Blue, 2))
            using (Pen orange = new Pen(Orange, 1))
            using (Pen grey = new Pen(Geometry, 1))
            using (Brush node = new SolidBrush(Blue))
            {
                g.Clear(Color.Magenta);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                switch (name)
                {
                    case "OneWayLoadPanel":
                        // A panel, its two receiving supports and a double-headed span arrow.
                        g.DrawRectangle(grey, 2, 1, 11, 13);
                        g.DrawLine(blue, 2, 2, 13, 2);
                        g.DrawLine(blue, 2, 13, 13, 13);
                        Arrow(g, orange, 8, 10, 8, 5);
                        g.DrawLine(orange, 8, 10, 6, 8);
                        g.DrawLine(orange, 8, 10, 10, 8);
                        break;
                    case "LoadPanelTools":
                        // Two panel regions separated by a diagonal cutting line.
                        g.DrawRectangle(blue, 2, 2, 11, 11);
                        g.DrawLine(orange, 1, 14, 14, 1);
                        g.DrawLine(orange, 2, 14, 14, 2);
                        break;
                    case "EndPinAssign":
                        // Two beam segments meeting at a hollow hinge.
                        g.DrawLine(blue, 1, 8, 5, 8);
                        g.DrawLine(blue, 10, 8, 14, 8);
                        g.DrawEllipse(orange, 5, 5, 5, 5);
                        g.DrawLine(grey, 3, 12, 12, 12);
                        g.DrawLine(grey, 5, 11, 7, 10);
                        g.DrawLine(grey, 10, 11, 9, 10);
                        break;
                    case "ReactionTransfer":
                        // A source beam, downward force, and receiving foundation.
                        g.DrawLine(blue, 2, 2, 13, 2);
                        Arrow(g, orange, 8, 5, 8, 11);
                        g.DrawLine(blue, 2, 13, 13, 13);
                        for (int x = 3; x <= 12; x += 3) g.DrawLine(grey, x, 14, x - 1, 15);
                        break;
                    case "SectionModifier":
                        // I section with a double-headed depth dimension beside it.
                        g.DrawLine(blue, 1, 2, 9, 2);
                        g.DrawLine(blue, 5, 3, 5, 12);
                        g.DrawLine(blue, 1, 13, 9, 13);
                        Arrow(g, orange, 13, 11, 13, 4);
                        g.DrawLine(orange, 13, 11, 11, 9);
                        g.DrawLine(orange, 13, 11, 15, 9);
                        break;
                    case "ConnectionLoadExtractor":
                        // Member ends at a connection node; an incoming force arrow identifies loads.
                        g.DrawLine(blue, 1, 11, 10, 11);
                        g.DrawLine(grey, 3, 14, 9, 8);
                        g.FillRectangle(node, 10, 9, 4, 4);
                        Arrow(g, orange, 12, 1, 12, 7);
                        break;
                    case "RhinoImport":
                        // A wireframe model entering the target: the standard import metaphor.
                        g.DrawRectangle(grey, 1, 4, 7, 8);
                        g.DrawLine(grey, 1, 4, 4, 1);
                        g.DrawLine(grey, 4, 1, 11, 1);
                        g.DrawLine(grey, 8, 4, 11, 1);
                        g.DrawLine(grey, 11, 1, 11, 5);
                        g.DrawLine(blue, 13, 8, 13, 14);
                        g.DrawLine(blue, 9, 14, 13, 14);
                        Arrow(g, orange, 5, 8, 14, 8);
                        break;
                    default: throw new ArgumentException("Unknown add-on icon", "name");
                }
            }
            return bitmap;
        }

        /// <summary>One small arrow with an integral, two-pixel head, horizontal or vertical.</summary>
        private static void Arrow(Graphics g, Pen pen, int x1, int y1, int x2, int y2)
        {
            g.DrawLine(pen, x1, y1, x2, y2);
            int dx = Math.Sign(x2 - x1), dy = Math.Sign(y2 - y1);
            g.DrawLine(pen, x2, y2, x2 - 2 * dx + 2 * dy, y2 - 2 * dy - 2 * dx);
            g.DrawLine(pen, x2, y2, x2 - 2 * dx - 2 * dy, y2 - 2 * dy + 2 * dx);
        }

        private static byte[] ToBmp(Bitmap bitmap)
        {
            using (bitmap)
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Bmp);
                return stream.ToArray();
            }
        }
    }
}
