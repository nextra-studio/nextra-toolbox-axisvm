using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public partial class AxisModelBuilder
    {
        // ==================================================================
        // 4f - loads
        // ==================================================================

        private void BuildLoads()
        {
            foreach (RhinoPoint p in rhino.Points) NodalLoads(p);
            foreach (LineJob job in lineJobs) MemberLoads(job);
            foreach (SurfaceJob s in surfaceJobs) SurfaceLoads(s);
        }

        private void NodalLoads(RhinoPoint p)
        {
            List<string> specs = p.AttrList(AxKeys.NodalLoad);
            if (specs.Count == 0) return;
            string context = p.Label("point");

            foreach (string text in specs)
            {
                PointLoadSpec load;
                string err;
                if (!AttributeParser.TryParseNodalLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_NodalLoad - {1}. Skipped.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                int nodeId = LiveNode(M(p.Xyz));
                if (lc <= 0 || nodeId <= 0) continue;

                try
                {
                    RLoadNodalForce rec = new RLoadNodalForce();
                    rec.LoadCaseId = lc;
                    rec.NodeId = nodeId;
                    rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                    rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];

                    if (model.Loads.AddNodalForce(ref rec) > 0) stats.LoadsCreated++;
                    else notes.Add(context + ": AxisVM rejected a nodal load.");
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a nodal load could not be created ({1}).", context, ex.Message));
                }
            }
        }

        // ------------------------------------------------------------------
        // Member loads
        // ------------------------------------------------------------------

        /// <summary>
        /// Loads on a beam / rib curve. Positions are measured along the WHOLE Rhino curve (from its
        /// start), so they stay right when the curve was split by auto-intersection or is a polyline;
        /// each load is cut into per-segment loads, and segments that run against the curve direction
        /// get their positions and start/end intensities mirrored.
        /// </summary>
        private void MemberLoads(LineJob job)
        {
            List<string> pointSpecs = job.Src.AttrList(AxKeys.PointLoad);
            List<string> distSpecs = job.Src.AttrList(AxKeys.DistLoad);
            if (pointSpecs.Count == 0 && distSpecs.Count == 0) return;

            if (job.Role != LineRole.Beam && job.Role != LineRole.Rib)
            {
                notes.Add(string.Format("{0}: member loads are only supported on beams and ribs (this is {1}). {2} load(s) skipped.",
                    job.Context, job.Role == LineRole.None ? "an untyped line" : "a " + job.Role, pointSpecs.Count + distSpecs.Count));
                stats.AttributeErrors++;
                return;
            }
            if (job.Chain.IsEmpty)
            {
                notes.Add(job.Context + ": it carries loads but never became an AxisVM line. They were skipped.");
                return;
            }

            double total = job.Chain.Total;

            foreach (string text in pointSpecs)
            {
                PointLoadSpec load;
                string err;
                if (!AttributeParser.TryParseBeamPointLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_PointLoad - {1}. Skipped.", job.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                double at = load.PositionIsRatio ? load.Position * total : load.Position;
                if (at < -1e-9 || at > total + 1e-6)
                {
                    notes.Add(string.Format("{0}: a point load at {1:0.###} m lies outside the member (length {2:0.###} m). Skipped.", job.Context, at, total));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                double local;
                SegRef seg = Locate(job.Chain, Math.Max(0, Math.Min(total, at)), out local);
                if (seg == null) continue;
                AddMemberPointLoad(job, seg, local, lc, load);
            }

            foreach (string text in distSpecs)
            {
                DistLoadSpec load;
                string err;
                if (!AttributeParser.TryParseBeamDistLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_DistLoad - {1}. Skipped.", job.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                // An open end ("from=len:2000" with no "to") runs to the member end. Anything else
                // reaching past the member is rejected rather than clipped - clipping would silently
                // change the slope (and resultant) of a trapezoidal load.
                double from = load.PositionIsRatio ? load.From * total : load.From;
                double to = double.IsPositiveInfinity(load.To) ? total : (load.PositionIsRatio ? load.To * total : load.To);
                double slack = Math.Max(1e-6, opt.MergeToleranceM);
                if (from < -slack || to > total + slack || to <= from + 1e-9)
                {
                    notes.Add(string.Format("{0}: a distributed load from {1:0.###} to {2:0.###} m does not lie on the member (length {3:0.###} m). Skipped.",
                        job.Context, from, to, total));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                AddMemberDistLoad(job, from, to, lc, load);
            }
        }

        /// <summary>
        /// The segment containing curve distance <paramref name="s"/> and the position along that
        /// segment measured from its local-x start. Every member segment's local x was set to follow
        /// its owning curve (OrientAndOffset), so this is only mirrored where the segment belongs to
        /// another curve drawn the other way.
        /// </summary>
        private SegRef Locate(Chain chain, double s, out double localPos)
        {
            double cum = 0;
            localPos = 0;
            for (int i = 0; i < chain.Segs.Count; i++)
            {
                SegRef seg = chain.Segs[i];
                bool last = i == chain.Segs.Count - 1;
                if (s <= cum + seg.Length + 1e-9 || last)
                {
                    double along = Math.Max(0, Math.Min(seg.Length, s - cum));
                    localPos = LocalAgainstCurve(seg) ? seg.Length - along : along;
                    return seg;
                }
                cum += seg.Length;
            }
            return null;
        }

        /// <summary>
        /// A local load on a segment whose local x runs against the curve: x reverses and, with the
        /// local z reference unchanged, y reverses too (right-handed system). Moments are vectors in
        /// the same system, so Mx and My reverse as well.
        /// </summary>
        private static void FlipLocal(double[] v)
        {
            v[0] = -v[0]; v[1] = -v[1];
            v[3] = -v[3]; v[4] = -v[4];
        }

        private void AddMemberPointLoad(LineJob job, SegRef seg, double pos, int lc, PointLoadSpec load)
        {
            ESystem sys = load.System == LoadSystem.Local ? ESystem.sysLocal : ESystem.sysGlobal;
            double[] f = (double[])load.F.Clone();
            if (sys == ESystem.sysLocal && LocalAgainstCurve(seg)) FlipLocal(f);

            try
            {
                int id;
                if (RoleOf(seg.Id, job) == LineRole.Rib)
                {
                    RLoadRibConcentrated rec = new RLoadRibConcentrated();
                    rec.LoadCaseId = lc; rec.LineId = seg.Id; rec.SystemGLR = sys;
                    rec.Fgx = f[0]; rec.Fgy = f[1]; rec.Fgz = f[2];
                    rec.Mgx = f[3]; rec.Mgy = f[4]; rec.Mgz = f[5];
                    rec.Position = pos;
                    id = model.Loads.AddRibConcentrated(ref rec);
                }
                else
                {
                    RLoadBeamConcentrated rec = new RLoadBeamConcentrated();
                    rec.LoadCaseId = lc; rec.LineId = seg.Id; rec.SystemGLR = sys;
                    rec.Fgx = f[0]; rec.Fgy = f[1]; rec.Fgz = f[2];
                    rec.Mgx = f[3]; rec.Mgy = f[4]; rec.Mgz = f[5];
                    rec.Position = pos;
                    id = model.Loads.AddBeamConcentrated(ref rec);
                }
                if (id > 0) stats.LoadsCreated++;
                else notes.Add(string.Format("{0}: AxisVM rejected a point load (code {1}).", job.Context, id));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: a point load could not be created ({1}).", job.Context, ex.Message));
            }
        }

        /// <summary>Cuts a (possibly trapezoidal) load between curve distances from..to into per-segment loads.</summary>
        private void AddMemberDistLoad(LineJob job, double from, double to, int lc, DistLoadSpec load)
        {
            ESystem sys = load.System == LoadSystem.Local ? ESystem.sysLocal : ESystem.sysGlobal;
            // Interpolate over the interval exactly as written; only the applied pieces are limited
            // to the member, so a hair-line overrun never changes the load's slope.
            double span = to - from;
            double cum = 0;
            int made = 0;

            foreach (SegRef seg in job.Chain.Segs)
            {
                double a = Math.Max(from, cum);
                double b = Math.Min(to, cum + seg.Length);
                cum += seg.Length;
                if (b - a <= 1e-9) continue;

                // Intensities at a and b, interpolated along the whole loaded length.
                double[] qa = Lerp(load.Q1, load.Q2, (a - from) / span);
                double[] qb = Lerp(load.Q1, load.Q2, (b - from) / span);

                // Positions along this segment from its local-x start.
                double la = a - (cum - seg.Length), lb = b - (cum - seg.Length);
                bool against = LocalAgainstCurve(seg);
                if (against)
                {
                    double t = la;
                    la = seg.Length - lb;
                    lb = seg.Length - t;
                    double[] tq = qa; qa = qb; qb = tq;
                    if (sys == ESystem.sysLocal) { FlipLocal(qa); FlipLocal(qb); }
                }

                bool trapezoid = false;
                for (int i = 0; i < 6; i++) if (Math.Abs(qa[i] - qb[i]) > 1e-12) trapezoid = true;

                try
                {
                    int id;
                    if (RoleOf(seg.Id, job) == LineRole.Rib)
                    {
                        RLoadRibDistributed rec = new RLoadRibDistributed();
                        rec.LoadCaseId = lc; rec.LineId = seg.Id; rec.SystemGLR = sys;
                        rec.qx1 = qa[0]; rec.qy1 = qa[1]; rec.qz1 = qa[2]; rec.mx1 = qa[3]; rec.my1 = qa[4]; rec.mz1 = qa[5];
                        rec.qx2 = qb[0]; rec.qy2 = qb[1]; rec.qz2 = qb[2]; rec.mx2 = qb[3]; rec.my2 = qb[4]; rec.mz2 = qb[5];
                        rec.Position1 = Math.Max(0, la); rec.Position2 = Math.Max(0, lb);
                        rec.DistributionType = EBeamRibDistributionType.brdtLength;
                        rec.Trapezoid = trapezoid ? ELongBoolean.lbTrue : ELongBoolean.lbFalse;
                        id = model.Loads.AddRibDistributed(ref rec);
                    }
                    else
                    {
                        RLoadBeamDistributed rec = new RLoadBeamDistributed();
                        rec.LoadCaseId = lc; rec.LineId = seg.Id; rec.SystemGLR = sys;
                        rec.qx1 = qa[0]; rec.qy1 = qa[1]; rec.qz1 = qa[2]; rec.mx1 = qa[3]; rec.my1 = qa[4]; rec.mz1 = qa[5];
                        rec.qx2 = qb[0]; rec.qy2 = qb[1]; rec.qz2 = qb[2]; rec.mx2 = qb[3]; rec.my2 = qb[4]; rec.mz2 = qb[5];
                        rec.Position1 = Math.Max(0, la); rec.Position2 = Math.Max(0, lb);
                        rec.DistributionType = EBeamRibDistributionType.brdtLength;
                        rec.Trapezoid = trapezoid ? ELongBoolean.lbTrue : ELongBoolean.lbFalse;
                        id = model.Loads.AddBeamDistributed(ref rec);
                    }
                    if (id > 0) made++;
                    else notes.Add(string.Format("{0}: AxisVM rejected a distributed load (code {1}).", job.Context, id));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a distributed load could not be created ({1}).", job.Context, ex.Message));
                }
            }

            if (made > 0) stats.LoadsCreated++;
        }

        private LineRole RoleOf(int segId, LineJob job)
        {
            LineRole r;
            return segmentRole.TryGetValue(segId, out r) ? r : job.Role;
        }

        private static double[] Lerp(double[] a, double[] b, double t)
        {
            double[] r = new double[6];
            for (int i = 0; i < 6; i++) r[i] = a[i] + (b[i] - a[i]) * t;
            return r;
        }

        // ------------------------------------------------------------------
        // Surface loads
        // ------------------------------------------------------------------

        private void SurfaceLoads(SurfaceJob s)
        {
            RhinoDomain d = s.Src;
            List<string> pointSpecs = d.AttrList(AxKeys.PointLoad);
            List<string> distSpecs = d.AttrList(AxKeys.DistLoad);
            if (pointSpecs.Count == 0 && distSpecs.Count == 0) return;

            bool isDomain = s.DomainId > 0;
            bool isPanel = s.PanelIds.Count > 0;
            if (!opt.DryRun && !isDomain && !isPanel)
            {
                notes.Add(s.Context + ": it carries loads but never became a domain or load panel. They were skipped.");
                return;
            }

            foreach (string text in pointSpecs)
            {
                PointLoadSpec load;
                string err;
                if (!AttributeParser.TryParseDomainPointLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_PointLoad - {1}. Skipped.", s.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;
                ESystem sys = load.System == LoadSystem.Local ? ESystem.sysLocal : ESystem.sysGlobal;

                try
                {
                    if (isDomain)
                    {
                        RLoadDomainConcentrated rec = new RLoadDomainConcentrated();
                        rec.LoadCaseId = lc; rec.DomainId = s.DomainId; rec.SystemGLR = sys;
                        rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                        rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];
                        rec.x = load.At.X; rec.y = load.At.Y; rec.z = load.At.Z;
                        if (model.Loads.AddDomainConcentrated(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(s.Context + ": AxisVM rejected a point load (is it inside the surface?).");
                    }
                    else
                    {
                        // Several one-way strips: the load belongs to the one it falls in - try each.
                        bool ok = false;
                        foreach (int pid in s.PanelIds)
                        {
                            RLoadPanelConcentrated rec = new RLoadPanelConcentrated();
                            rec.LoadCaseId = lc; rec.LoadPanelId = pid; rec.SystemGLR = sys;
                            rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                            rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];
                            rec.x = load.At.X; rec.y = load.At.Y; rec.z = load.At.Z;
                            int id;
                            try { id = model.Loads.AddLoadPanelConcentrated(ref rec); } catch { id = 0; }
                            if (id > 0) { ok = true; break; }
                        }
                        if (ok) stats.LoadsCreated++;
                        else notes.Add(s.Context + ": AxisVM rejected a point load on the load panel (is it inside the panel?).");
                    }
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a point load could not be created ({1}).", s.Context, ex.Message));
                }
            }

            foreach (string text in distSpecs)
            {
                DistLoadSpec load;
                string err;
                if (!AttributeParser.TryParseDomainDistLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_DistLoad - {1}. Skipped.", s.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                if (isDomain) AddDomainUniformLoad(s, lc, load);
                else foreach (int pid in s.PanelIds) AddPanelUniformLoad(s, pid, lc, load);
            }
        }

        private void AddDomainUniformLoad(SurfaceJob s, int loadCaseId, DistLoadSpec load)
        {
            try
            {
                RLoadDomainConstant rec = new RLoadDomainConstant();
                rec.LoadCaseId = loadCaseId;
                rec.DomainId = s.DomainId;
                rec.SystemGLR = load.System == LoadSystem.Local ? ESystem.sysLocal : ESystem.sysGlobal;
                rec.DistributionType = load.System == LoadSystem.Projected
                    ? ESurfaceDomainDistributionType.sddtProjected
                    : ESurfaceDomainDistributionType.sddtSurface;
                rec.qx = load.Q1[0];
                rec.qy = load.Q1[1];
                rec.qz = load.Q1[2];

                if (model.Loads.AddDomainConstant(ref rec) > 0) stats.LoadsCreated++;
                else notes.Add(s.Context + ": AxisVM rejected a uniform domain load.");
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: a uniform domain load could not be created ({1}).", s.Context, ex.Message));
            }
        }

        /// <summary>
        /// A uniform load on a load panel. AddLoadPanelLinear with a constant distribution only uses P1
        /// in the Component direction, so each non-zero component is its own load.
        /// </summary>
        private void AddPanelUniformLoad(SurfaceJob s, int panelId, int loadCaseId, DistLoadSpec load)
        {
            EAxis[] axes = { EAxis.aX, EAxis.aY, EAxis.aZ };
            EDistributionType dist = load.System == LoadSystem.Local ? EDistributionType.dtLocal
                                   : load.System == LoadSystem.Projected ? EDistributionType.dtProjected
                                   : EDistributionType.dtGlobal;

            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(load.Q1[i]) < 1e-12) continue;

                int loadId = 0;
                string errDetail = "";
                try
                {
                    RLoadPanelLinear rec = new RLoadPanelLinear();
                    rec.LoadCaseId = loadCaseId;
                    rec.LoadPanelId = panelId;
                    rec.DistributionType = dist;
                    rec.LoadDistributionType = ELoadDistributionType.ldtConst;
                    rec.Component = axes[i];
                    rec.P1 = load.Q1[i];
                    rec.P2 = load.Q1[i];
                    rec.P3 = load.Q1[i];
                    loadId = model.Loads.AddLoadPanelLinear(ref rec);
                    if (loadId <= 0) errDetail = "code " + loadId;
                }
                catch (Exception ex) { errDetail = ex.Message; }

                if (loadId <= 0) loadId = AddPanelPolyAreaLoad(panelId, loadCaseId, axes[i], dist, load.Q1[i]);

                if (loadId > 0) stats.LoadsCreated++;
                else notes.Add(string.Format("{0}: AxisVM rejected a load on load panel {1} ({2}).", s.Context, panelId, errDetail));
            }
        }

        /// <summary>Fallback: the same uniform load as a polygon area load over the panel's own contour.</summary>
        private int AddPanelPolyAreaLoad(int panelId, int loadCaseId, EAxis axis, EDistributionType dist, double q)
        {
            try
            {
                AxisVMLines3d contour = null;
                model.LoadPanels.Item[panelId].GetContourPolygon(out contour);
                if (contour == null) return 0;

                RLoadPanelPolyArea rec = new RLoadPanelPolyArea();
                rec.LoadCaseId = loadCaseId;
                rec.DistributionType = dist;
                rec.LoadDistributionType = ELoadDistributionType.ldtConst;
                rec.Component = axis;
                rec.P1 = q;
                rec.P2 = q;
                rec.P3 = q;
                rec.WindowLoad = ELongBoolean.lbFalse;
                return model.Loads.AddLoadPanelPolyArea(contour, ref rec);
            }
            catch { return 0; }
        }
    }
}
