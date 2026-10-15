using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.Core
{
    public partial class AxisModelBuilder
    {
        /// <summary>Thickness used when a domain does not name one [m].</summary>
        private const double DefaultThicknessM = 0.200;

        // ==================================================================
        // 4e - domains and load panels
        // ==================================================================

        /// <summary>
        /// Builds the one-way load panels before anything else uses line ids, because the edge-strip
        /// generator splits boundary lines. Returns true when the model topology may have changed.
        /// </summary>
        private bool BuildOneWayPanels()
        {
            bool changed = false;
            foreach (SurfaceJob s in surfaceJobs)
            {
                if (s.Role != SurfaceRole.OneWayLoadPanel) continue;
                if (s.OuterChain.Segs.Count < 3)
                {
                    notes.Add(s.Context + ": its boundary could not be resolved to AxisVM lines. Skipped.");
                    continue;
                }
                if (opt.DryRun) { stats.PanelsCreated++; continue; }
                CreateOneWayLoadPanel(s);
                changed = true;

                // The edge-strip generator may have split lines: refresh every chain before the
                // next panel (or anything else) uses an id.
                ResolveChains(Math.Max(opt.MergeToleranceM, 1e-6));
            }
            return changed;
        }

        private void BuildSurfaces()
        {
            foreach (SurfaceJob s in surfaceJobs)
            {
                if (s.Role == SurfaceRole.OneWayLoadPanel) continue;   // built earlier, see BuildOneWayPanels
                if (s.OuterChain.Segs.Count < 3)
                {
                    notes.Add(s.Context + ": its boundary could not be resolved to AxisVM lines. Skipped.");
                    continue;
                }

                if (opt.DryRun)
                {
                    if (IsPanel(s.Role)) stats.PanelsCreated++; else { stats.DomainsCreated++; stats.HolesCreated += s.Holes.Count; }
                    continue;
                }

                switch (s.Role)
                {
                    case SurfaceRole.OneWayLoadPanel: CreateOneWayLoadPanel(s); break;
                    case SurfaceRole.LoadPanel: CreateLoadPanel(s); break;
                    default: CreateDomain(s); break;
                }
            }
        }

        private void CreateDomain(SurfaceJob s)
        {
            RhinoDomain d = s.Src;
            RSurfaceAttr attr = new RSurfaceAttr();
            switch (s.Role)
            {
                case SurfaceRole.Plate: attr.SurfaceType = ESurfaceType.stPlate; break;
                case SurfaceRole.Membrane: attr.SurfaceType = ESurfaceType.stMembraneStress; break;
                default: attr.SurfaceType = ESurfaceType.stShell; break;
            }

            attr.MaterialId = catalog.Material(d.Attr(AxKeys.Material), s.Context);

            double thickness = DefaultThicknessM;
            string thicknessText = d.Attr(AxKeys.Thickness);
            if (thicknessText != null)
            {
                double raw;
                if (double.TryParse(thicknessText.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out raw) && raw > 0)
                    thickness = opt.Units.Length(raw);
                else
                {
                    notes.Add(string.Format("{0}: Ax_Thickness '{1}' is not a positive number - {2} m used instead.", s.Context, thicknessText, DefaultThicknessM));
                    stats.AttributeErrors++;
                }
            }
            else notes.Add(string.Format("{0}: no Ax_Thickness given - {1} m used.", s.Context, DefaultThicknessM));
            attr.Thickness = thickness;

            string axisText = d.Attr(AxKeys.LocalAxis);
            if (!string.IsNullOrEmpty(axisText))
            {
                RhinoXyz dir;
                string err;
                if (AttributeParser.TryParseDirection(axisText, out dir, out err))
                {
                    int refId = catalog.ReferenceVector(dir);
                    if (refId > 0) attr.RefXId = refId;
                }
                else
                {
                    notes.Add(string.Format("{0}: Ax_LocalAxis - {1}. Ignored.", s.Context, err));
                    stats.AttributeErrors++;
                }
            }

            int domainId;
            try { domainId = model.Domains.Add(Ids(s.OuterChain.Ids().ToArray()), ref attr); }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the domain ({1}). Is its boundary planar and closed?", s.Context, ex.Message));
                return;
            }
            if (domainId <= 0)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the domain (code {1}). Is its boundary planar and closed?", s.Context, domainId));
                return;
            }

            s.DomainId = domainId;
            stats.DomainsCreated++;

            for (int h = 0; h < s.HoleChains.Count; h++)
            {
                Chain hole = s.HoleChains[h];
                if (hole.Segs.Count < 3)
                {
                    notes.Add(string.Format("{0}: opening {1} could not be resolved to AxisVM lines. Skipped.", s.Context, h + 1));
                    continue;
                }
                try
                {
                    int rc = model.Domains.Item[domainId].AddHole(Ids(hole.Ids().ToArray()));
                    if (rc > 0) stats.HolesCreated++;
                    else notes.Add(string.Format("{0}: AxisVM rejected opening {1} (code {2}).", s.Context, h + 1, rc));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: opening {1} could not be created ({2}).", s.Context, h + 1, ex.Message));
                }
            }

            ApplyDomainEccentricity(s);
        }

        private void ApplyDomainEccentricity(SurfaceJob s)
        {
            string text = s.Src.Attr(AxKeys.Eccentricity);
            if (text == null) return;

            RhinoXyz v;
            string err;
            if (!AttributeParser.TryParseVector(text, opt.Units, out v, out err))
            {
                // A single number is accepted as the offset itself.
                double one;
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out one))
                    v = new RhinoXyz(0, 0, opt.Units.Length(one));
                else
                {
                    notes.Add(string.Format("{0}: Ax_Eccentricity - {1}. Ignored.", s.Context, err));
                    stats.AttributeErrors++;
                    return;
                }
            }

            try
            {
                RDomainExcentricity ecc = new RDomainExcentricity();
                ecc.ExcentricityType = EDomainExcentricityType.detConstant;

                // A constant domain eccentricity is one offset from the mid-plane along the domain
                // normal, so the vector collapses to one magnitude; z is the natural reading for a slab.
                double offset = Math.Abs(v.Z) > 1e-12 ? v.Z : (Math.Abs(v.Y) > 1e-12 ? v.Y : v.X);
                ecc.exc1 = offset;
                ecc.exc2 = offset;
                ecc.exc3 = offset;

                model.Domains.SetExcentricity(s.DomainId, ref ecc);
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: its eccentricity could not be set ({1}).", s.Context, ex.Message));
            }
        }

        /// <summary>A two-way load panel over the boundary. A load panel is not a domain: no material, thickness or holes.</summary>
        private void CreateLoadPanel(SurfaceJob s)
        {
            Array ids = Ids(s.OuterChain.Ids().ToArray());
            int panelId;
            try { panelId = model.LoadPanels.AddFromLineIDs(ref ids); }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the load panel ({1}).", s.Context, ex.Message));
                return;
            }
            if (panelId <= 0)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the load panel (code {1}).", s.Context, panelId));
                return;
            }

            ApplyLoadTargets(s, panelId);

            stats.PanelsCreated++;
            s.PanelIds.Add(panelId);
            WarnPanelExtras(s);
        }

        /// <summary>One-way load panel(s) through the same engine as the "Lines -> One-Way Panel" addon.</summary>
        private void CreateOneWayLoadPanel(SurfaceJob s)
        {
            RhinoDomain d = s.Src;
            List<ResolvedLine> segments = new List<ResolvedLine>();
            foreach (int lid in s.OuterChain.Ids())
            {
                try { segments.Add(OneWayPanelBuilder.ResolveLine(model, lid)); } catch { }
            }

            OneWayPanelOptions panelOpt = new OneWayPanelOptions();
            panelOpt.AngleToleranceDeg = 5.0;
            string tolText = d.Attr(AxKeys.SpanTolerance);
            if (!string.IsNullOrWhiteSpace(tolText))
            {
                double tol;
                if (double.TryParse(tolText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out tol) && tol >= 0 && tol < 45)
                    panelOpt.AngleToleranceDeg = tol;
                else
                {
                    notes.Add(string.Format("{0}: Ax_SpanTolerance '{1}' must be 0 up to below 45 degrees; 5 was used.", s.Context, tolText));
                    stats.AttributeErrors++;
                }
            }
            panelOpt.NoSpacing = true;

            string spanText = d.Attr(AxKeys.SpanDirection);
            double angle;
            int legacy;
            string err;
            if (AttributeParser.TryParseSpanAngle(spanText, out angle, out legacy, out err))
            {
                panelOpt.UseSpanAngle = true;
                panelOpt.SpanAngleDeg = angle;
            }
            else if (legacy != 0 && legacy != AttributeParser.AutoLongerSpan)
            {
                panelOpt.SpanningDirection = legacy;   // old "1" / "2" (along / across the first edge)
            }
            else
            {
                if (err != null)
                {
                    notes.Add(string.Format("{0}: Ax_SpanDirection - {1}; the shorter span was used.", s.Context, err));
                    stats.AttributeErrors++;
                }
                // Auto: the shorter span of the panel's smallest enclosing rectangle - works for any
                // outline, not only rectangles. "autolong" turns it 90 deg (the longer span).
                bool longer = legacy == AttributeParser.AutoLongerSpan;
                double auto;
                if (ShortSpanAngle(s.Outer, out auto))
                {
                    if (longer) auto += 90.0;
                    panelOpt.UseSpanAngle = true;
                    panelOpt.SpanAngleDeg = auto;
                    notes.Add(string.Format(CultureInfo.InvariantCulture, "{0}: one-way span direction (auto, {1} span) = {2:0.#} deg from global X.",
                        s.Context, longer ? "longer" : "shorter", auto));
                }
            }

            string spacingText = d.Attr(AxKeys.EdgeSpacing);
            if (!string.IsNullOrEmpty(spacingText))
            {
                double sp;
                if (double.TryParse(spacingText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out sp) && sp > 0)
                {
                    panelOpt.NoSpacing = false;
                    panelOpt.EdgeSpacingMm = opt.Units.Length(sp) * 1000.0;
                }
            }

            List<string> builderNotes = new List<string>();
            List<int> panelUids = null;
            try { panelUids = OneWayPanelBuilder.GeneratePanels(model, segments, panelOpt, 1, builderNotes); }
            catch (Exception ex) { builderNotes.Add(ex.Message); }

            foreach (string n in builderNotes) notes.Add(s.Context + ": " + n);

            if (panelUids != null && panelUids.Count > 0)
            {
                foreach (int uid in panelUids)
                {
                    int p = 0;
                    try { p = model.LoadPanels.get_IndexOfUID(uid); } catch { }
                    if (p > 0) s.PanelIds.Add(p);
                }
                stats.PanelsCreated += panelUids.Count;
                WarnPanelExtras(s);
            }
            else
            {
                notes.Add(s.Context + ": the one-way panel could not be generated - a two-way load panel was created instead.");
                CreateLoadPanel(s);
            }
        }

        /// <summary>
        /// Where the panel sends its load. Without Ax_LoadTargets: automatically to every line and node
        /// inside / on its contour. With it: only to the named Rhino curves (all their segments) and
        /// points - e.g. a slab that rests on two beams only.
        /// </summary>
        private void ApplyLoadTargets(SurfaceJob s, int panelId)
        {
            List<string> targets = AttributeParser.SplitList(s.Src.Attr(AxKeys.LoadTargets));
            IAxisVMLoadPanel lp;
            try { lp = model.LoadPanels.Item[panelId]; }
            catch (Exception ex) { notes.Add(s.Context + ": the load panel could not be configured (" + ex.Message + ")."); return; }

            if (targets.Count == 0)
            {
                try { lp.Auto = ELongBoolean.lbTrue; } catch { }
                return;
            }

            HashSet<string> wanted = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            List<int> lineIds = new List<int>();
            List<int> nodeIds = new List<int>();
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (LineJob job in lineJobs)
            {
                if (!wanted.Contains(job.Src.Guid)) continue;
                found.Add(job.Src.Guid);
                foreach (int id in job.Chain.Ids()) if (!lineIds.Contains(id)) lineIds.Add(id);
            }
            foreach (RhinoPoint p in rhino.Points)
            {
                if (!wanted.Contains(p.Guid)) continue;
                found.Add(p.Guid);
                int n = LiveNode(M(p.Xyz));
                if (n > 0 && !nodeIds.Contains(n)) nodeIds.Add(n);
            }

            foreach (string t in targets)
                if (!found.Contains(t))
                {
                    notes.Add(string.Format("{0}: load target {1} is not a curve or point in this export - that member gets no load from the panel.", s.Context, Short(t)));
                    stats.AttributeErrors++;
                }

            if (lineIds.Count == 0 && nodeIds.Count == 0)
            {
                notes.Add(s.Context + ": ERROR - none of its load target members was in the export (deleted, or not exported). " +
                          "The panel was set to distribute AUTOMATICALLY - check it, or re-pick the targets in Rhino and export again.");
                stats.AttributeErrors++;
                try { lp.Auto = ELongBoolean.lbTrue; } catch { }
                return;
            }

            try
            {
                lp.Auto = ELongBoolean.lbFalse;
                if (lineIds.Count > 0)
                {
                    Array la = Ids(lineIds.ToArray());
                    int rc = lp.SetLines(ref la);
                    if (rc <= 0) notes.Add(string.Format("{0}: AxisVM rejected the target members (code {1}).", s.Context, rc));
                }
                if (nodeIds.Count > 0)
                {
                    Array na = Ids(nodeIds.ToArray());
                    int rc = lp.SetNodes(ref na);
                    if (rc <= 0) notes.Add(string.Format("{0}: AxisVM rejected the target nodes (code {1}).", s.Context, rc));
                }
                notes.Add(string.Format("{0}: load goes only to {1} target line(s) and {2} node(s).", s.Context, lineIds.Count, nodeIds.Count));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: the load targets could not be set ({1}); check the panel in AxisVM.", s.Context, ex.Message));
            }
        }

        /// <summary>
        /// The direction of the shorter side of the smallest-area rectangle around the outline
        /// (projected on the global XY plane), in degrees from +X. For a rectangle this is simply its
        /// short side; for an L-shape or a trapezoid it is the narrow direction of the whole panel.
        /// The optimal rectangle always has a side parallel to one of the outline's edges, so trying
        /// every edge direction is exact.
        /// </summary>
        internal static bool ShortSpanAngle(List<double[]> rawOutline, out double angleDeg)
        {
            angleDeg = 0;
            if (rawOutline == null || rawOutline.Count < 3) return false;

            // The minimum rectangle has a side on a CONVEX HULL edge (not necessarily an outline edge
            // of a concave panel), so search the hull. Same rule as the Rhino preview (SpanTools).
            List<double[]> outline = ConvexHullXY(rawOutline);
            if (outline.Count < 3) return false;

            double bestArea = double.MaxValue;
            bool found = false;
            for (int i = 0; i < outline.Count; i++)
            {
                double[] a = outline[i], b = outline[(i + 1) % outline.Count];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                double ux = dx / len, uy = dy / len;     // candidate rectangle axis u, and v = perpendicular

                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (double[] p in outline)
                {
                    double pu = p[0] * ux + p[1] * uy;
                    double pv = -p[0] * uy + p[1] * ux;
                    minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                    minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
                }
                double wu = maxU - minU, wv = maxV - minV;
                double area = wu * wv;
                if (area >= bestArea - 1e-12) continue;
                bestArea = area;
                found = true;

                // Span along the shorter side of this rectangle.
                double sx = wu <= wv ? ux : -uy, sy = wu <= wv ? uy : ux;
                angleDeg = Math.Atan2(sy, sx) * 180.0 / Math.PI;
            }
            return found && bestArea > 1e-12;
        }

        /// <summary>Andrew's monotone chain on the XY projection; counter-clockwise, no repeated point.</summary>
        internal static List<double[]> ConvexHullXY(List<double[]> pts)
        {
            List<double[]> p = new List<double[]>(pts);
            p.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
            if (p.Count < 3) return p;

            Func<double[], double[], double[], double> cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
            List<double[]> h = new List<double[]>();
            for (int i = 0; i < p.Count; i++)
            {
                while (h.Count >= 2 && cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 1e-12) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 1e-12) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        private static string Short(string guid)
        {
            return !string.IsNullOrEmpty(guid) && guid.Length >= 8 ? guid.Substring(0, 8) : guid;
        }

        private void WarnPanelExtras(SurfaceJob s)
        {
            RhinoDomain d = s.Src;
            if (d.Attr(AxKeys.Material) != null || d.Attr(AxKeys.Thickness) != null)
                notes.Add(s.Context + ": it is a load panel, so Ax_Material / Ax_Thickness were ignored (a load panel is not a domain).");
            if (s.Holes.Count > 0)
                notes.Add(s.Context + ": load panels cannot have openings; its holes were ignored.");
        }
    }
}
