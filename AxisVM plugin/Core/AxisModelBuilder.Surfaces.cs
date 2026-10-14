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

        private void BuildSurfaces()
        {
            foreach (SurfaceJob s in surfaceJobs)
            {
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

            try
            {
                var lp = model.LoadPanels.Item[panelId];
                if (lp != null)
                {
                    lp.Auto = ELongBoolean.lbTrue;
                    lp.SetLines(ref ids);
                }
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: the load panel's load transfer lines could not be set ({1}).", s.Context, ex.Message));
            }

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
            else
            {
                if (err != null)
                {
                    notes.Add(string.Format("{0}: Ax_SpanDirection - {1}; the shorter span was used.", s.Context, err));
                    stats.AttributeErrors++;
                }
                panelOpt.SpanningDirection = legacy;
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
