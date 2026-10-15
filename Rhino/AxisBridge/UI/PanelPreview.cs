using System;
using System.Collections.Generic;
using System.Drawing;
using AxisBridge.Loads;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.UI
{
    /// <summary>
    /// Live viewport preview while the AxDomain dialog is open: a blue double arrow along the span
    /// of every selected one-way load panel, and the chosen load targets of a two-way panel drawn
    /// thick orange. It follows the dialog as you change the settings.
    /// </summary>
    public class PanelPreview : DisplayConduit
    {
        private readonly RhinoDoc doc;
        private readonly List<List<Point3d>> outlines = new List<List<Point3d>>();
        private readonly List<GeometryBase> targets = new List<GeometryBase>();

        /// <summary>Current Ax_SpanDirection value, or null when no span arrow should be drawn.</summary>
        public string SpanValue;

        private static readonly Color SpanColor = Color.FromArgb(0, 110, 220);
        private static readonly Color TargetColor = Color.FromArgb(240, 120, 0);

        public PanelPreview(RhinoDoc doc, IEnumerable<RhinoObject> panels)
        {
            this.doc = doc;
            double tol = doc != null ? doc.ModelAbsoluteTolerance : 0.001;
            foreach (RhinoObject o in panels) outlines.AddRange(SpanTools.Outlines(o, tol));
        }

        /// <summary>Replaces the highlighted target members / nodes (Rhino ids).</summary>
        public void SetTargets(IEnumerable<Guid> ids)
        {
            targets.Clear();
            if (doc == null || ids == null) return;
            foreach (Guid id in ids)
            {
                RhinoObject o = doc.Objects.FindId(id);
                if (o?.Geometry != null) targets.Add(o.Geometry);
            }
        }

        public void Refresh()
        {
            doc?.Views.Redraw();
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            foreach (List<Point3d> o in outlines) e.IncludeBoundingBox(new BoundingBox(o));
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (SpanValue != null)
            {
                foreach (List<Point3d> o in outlines)
                {
                    Vector3d dir;
                    if (!SpanTools.Resolve(SpanValue, o, out dir)) continue;

                    BoundingBox bb = new BoundingBox(o);
                    Point3d c = bb.Center;
                    double half = 0.3 * Math.Max(bb.Diagonal.X, bb.Diagonal.Y);
                    Line l = new Line(c - dir * half, c + dir * half);

                    e.Display.DrawArrow(l, SpanColor, 0, 0.12);
                    e.Display.DrawArrow(new Line(l.To, l.From), SpanColor, 0, 0.12);
                    e.Display.Draw2dText("span", SpanColor, c + dir * half * 0.15, true, 14);
                }
            }

            foreach (GeometryBase g in targets)
            {
                if (g is Curve crv) e.Display.DrawCurve(crv, TargetColor, 5);
                else if (g is Rhino.Geometry.Point pt) e.Display.DrawPoint(pt.Location, PointStyle.RoundSimple, 9, TargetColor);
            }
        }
    }
}
