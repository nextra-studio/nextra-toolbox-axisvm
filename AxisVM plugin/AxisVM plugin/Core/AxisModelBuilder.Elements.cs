using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public partial class AxisModelBuilder
    {
        // ==================================================================
        // 4a - member types (beam / rib / truss), releases, members
        // ==================================================================

        /// <summary>
        /// Gives every segment its element type. Where several Rhino curves share a segment (a beam
        /// drawn on a slab edge, two overlapping beams) the first curve with a type decides; a
        /// conflicting second type is reported, never silently mixed.
        /// </summary>
        private void ApplyMemberTypes()
        {
            HashSet<int> domainEdges = new HashSet<int>();
            foreach (SurfaceJob s in surfaceJobs)
            {
                if (IsPanel(s.Role)) continue;   // a beam on a load panel edge stays a beam
                foreach (int id in s.OuterChain.Ids()) domainEdges.Add(id);
                foreach (Chain h in s.HoleChains) foreach (int id in h.Ids()) domainEdges.Add(id);
            }

            HashSet<string> conflicts = new HashSet<string>(StringComparer.Ordinal);
            // Only typed members claim segments: a Simple line, a line support or an edge hinge drawn on
            // top of a beam must never stop that beam from being typed.
            foreach (LineJob job in lineJobs)
            {
                if (!IsFrame(job.Role)) continue;
                foreach (SegRef s in job.Chain.Segs)
                {
                    LineJob owner;
                    if (segmentOwner.TryGetValue(s.Id, out owner))
                    {
                        if (owner.Role != job.Role && conflicts.Add(owner.Src.Guid + "|" + job.Src.Guid))
                        {
                            notes.Add(string.Format("{0} and {1} overlap but ask for different element types ({2} / {3}); {2} was used where they overlap.",
                                owner.Context, job.Context, owner.Role, job.Role));
                            stats.AttributeErrors++;
                        }
                        continue;
                    }
                    segmentOwner[s.Id] = job;
                }
            }

            HashSet<string> autoRib = new HashSet<string>(StringComparer.Ordinal);
            foreach (LineJob job in lineJobs)
            {
                if (!IsFrame(job.Role)) continue;

                if (opt.DryRun) { ParseFrameAttributes(job); continue; }

                ResolvedProfile prof = ResolveProfile(job);
                List<SegRef> owned = new List<SegRef>();
                double cum = 0;

                foreach (SegRef s in job.Chain.Segs)
                {
                    // Where this segment starts / ends along the whole curve (0..1), for eccentricity.
                    double t0 = job.Chain.Total > 0 ? cum / job.Chain.Total : 0;
                    cum += s.Length;
                    double t1 = job.Chain.Total > 0 ? cum / job.Chain.Total : 1;

                    LineJob owner;
                    if (!segmentOwner.TryGetValue(s.Id, out owner) || owner != job) continue;

                    LineRole role = job.Role;
                    if (role == LineRole.Beam && domainEdges.Contains(s.Id))
                    {
                        // In AxisVM a member lying on a domain edge must be a rib.
                        role = LineRole.Rib;
                        if (autoRib.Add(job.Src.Guid))
                            notes.Add(job.Context + ": lies on a domain edge - created as a Rib instead of a Beam.");
                    }

                    if (ApplyFrameType(s, job, ref role, prof, t0, t1)) { segmentRole[s.Id] = role; owned.Add(s); }
                }

                if (owned.Count == 0) continue;

                if (job.Role != LineRole.Truss)
                {
                    ApplyLocalAxis(owned, job);
                    ApplyChainReleases(job);
                }
                PlanMembers(job);
            }

            if (orientRefused.Count > 0)
                notes.Add(string.Format("{0} curve(s), e.g. {1}: AxisVM did not take the local x orientation along the Rhino curve (usual for round " +
                    "sections); local member loads on them follow AxisVM's own direction.", orientRefused.Count, orientRefusedExample));
        }

        private class ResolvedProfile
        {
            public int Material;
            public int StartSection;
            public int EndSection;
            public bool HasEcc;
            public RhinoXyz EccStart, EccEnd;
        }

        private void ParseFrameAttributes(LineJob job)
        {
            string text = job.Src.Attr(AxKeys.Eccentricity);
            RhinoXyz a, b;
            string err;
            if (text != null && !AttributeParser.TryParseEccentricity(text, opt.Units, out a, out b, out err))
            {
                notes.Add(string.Format("{0}: Ax_Eccentricity - {1}.", job.Context, err));
                stats.AttributeErrors++;
            }
            text = job.Src.Attr(AxKeys.Release);
            ReleaseSpec rs;
            if (text != null && job.Role != LineRole.Truss && !AttributeParser.TryParseRelease(text, opt.Units, out rs, out err))
            {
                notes.Add(string.Format("{0}: Ax_Release - {1}.", job.Context, err));
                stats.AttributeErrors++;
            }
        }

        private ResolvedProfile ResolveProfile(LineJob job)
        {
            ResolvedProfile p = new ResolvedProfile();
            RhinoLine src = job.Src;

            p.Material = catalog.Material(src.Attr(AxKeys.Material), job.Context);

            string startName, endName;
            AttributeParser.ParseSectionNames(src.Attr(AxKeys.Section) ?? "", out startName, out endName);
            p.StartSection = catalog.Section(startName, job.Context);
            p.EndSection = string.Equals(startName, endName, StringComparison.OrdinalIgnoreCase) ? p.StartSection : catalog.Section(endName, job.Context);
            if (p.StartSection != p.EndSection && job.Chain.Segs.Count > 1)
            {
                notes.Add(job.Context + ": a tapered section on a curve that was split into several elements - every element uses the start section.");
                p.EndSection = p.StartSection;
            }

            string eccText = src.Attr(AxKeys.Eccentricity);
            if (eccText != null && job.Role != LineRole.Truss)
            {
                string err;
                if (AttributeParser.TryParseEccentricity(eccText, opt.Units, out p.EccStart, out p.EccEnd, out err)) p.HasEcc = true;
                else
                {
                    notes.Add(string.Format("{0}: Ax_Eccentricity - {1}. Ignored.", job.Context, err));
                    stats.AttributeErrors++;
                }
            }
            return p;
        }

        /// <summary>Types one segment as beam / rib / truss with material, section and eccentricity.</summary>
        private bool ApplyFrameType(SegRef s, LineJob job, ref LineRole role, ResolvedProfile p, double t0, double t1)
        {
            if (!DefineFrame(s, job, ref role, p)) return false;
            OrientAndOffset(s, job, role, p, t0, t1);
            return true;
        }

        /// <summary>Sets type, material and sections. Return codes are checked - AxisVM reports most rejections that way, not by throwing.</summary>
        private bool DefineFrame(SegRef s, LineJob job, ref LineRole role, ResolvedProfile p)
        {
            ELineType lineType = role == LineRole.Rib ? ELineType.ltRib : role == LineRole.Truss ? ELineType.ltTruss : ELineType.ltBeam;

            RLineAttr attr;
            if (TryGetLineAttr(s.Id, out attr))
            {
                attr.LineType = lineType;
                if (p.Material > 0) attr.MaterialIndex = p.Material;
                if (p.StartSection > 0) attr.StartCrossSectionIndex = p.StartSection;
                if (p.EndSection > 0) attr.EndCrossSectionIndex = p.EndSection;
                if (TrySetLineAttr(s.Id, attr) && LineTypeIs(s.Id, lineType)) return true;
            }

            // Fallback: the explicit Define* calls (eccentricity is written afterwards via the V161 record).
            RPoint3d e1 = new RPoint3d(), e2 = new RPoint3d();
            try
            {
                IAxisVMLine line = model.Lines.Item[s.Id];
                if (role == LineRole.Truss)
                {
                    if (line.DefineAsTruss(p.Material, p.StartSection, ELineNonLinearity.lnlTensionAndCompression, 0.0) > 0) return true;
                    notes.Add(job.Context + ": AxisVM rejected the truss definition.");
                    return false;
                }
                if (role == LineRole.Beam)
                {
                    int rc;
                    try { rc = line.DefineAsBeam(p.Material, p.StartSection, p.EndSection, ref e1, ref e2); } catch { rc = 0; }
                    if (rc > 0) return true;
                    notes.Add(job.Context + ": AxisVM rejected a Beam here - defined as a Rib instead.");
                    role = LineRole.Rib;
                }
                if (line.DefineAsRib(p.Material, p.StartSection, p.EndSection, ref e1, ref e2) > 0) return true;
                if (role == LineRole.Rib)
                {
                    // A rib moved off its slab: AxisVM has no surface to attach it to, so it becomes a beam.
                    int rc;
                    try { rc = line.DefineAsBeam(p.Material, p.StartSection, p.EndSection, ref e1, ref e2); } catch { rc = 0; }
                    if (rc > 0)
                    {
                        role = LineRole.Beam;
                        notes.Add(job.Context + ": a Rib that is not on a slab edge or in a slab - created as a Beam instead.");
                        return true;
                    }
                }
                notes.Add(job.Context + ": AxisVM rejected the member definition (check its material and section).");
                return false;
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: AxisVM rejected its element type ({1}).", job.Context, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Makes the segment's local x run in the Rhino curve direction (so every piece of a split
        /// member has the same local axes and local loads mean the same thing on all of them), and
        /// writes the eccentricity as a custom offset at the matching physical ends.
        /// </summary>
        private void OrientAndOffset(SegRef s, LineJob job, LineRole role, ResolvedProfile p, double t0, double t1)
        {
            if (s.CurveStartNode <= 0 || s.CurveEndNode <= 0) return;
            bool curveStartIsLower = s.CurveStartNode < s.CurveEndNode;

            RLineAttr_V161 a;
            if (!TryGetLineAttrV161(s.Id, out a)) return;

            a.LocalXOrientation = curveStartIsLower ? EMemberLocXOrientation.mlxo_ij : EMemberLocXOrientation.mlxo_ji;
            RLineAttr_V161 withoutEcc = a;   // value copy: the orientation alone, as a fallback
            withoutEcc.Beam7DOF = ELongBoolean.lbFalse;
            bool wantsEcc = p.HasEcc && role != LineRole.Truss;
            if (wantsEcc)
            {
                // A curve split into pieces gets its offset interpolated along its length, so a
                // tapering eccentricity stays continuous. Start/End fields = lower / higher index node.
                RhinoXyz e0 = LerpXyz(p.EccStart, p.EccEnd, t0), e1 = LerpXyz(p.EccStart, p.EccEnd, t1);
                a.StartEccentricityType = ELEEccType.leet_CustomOffset;
                a.EndEccentricityType = ELEEccType.leet_CustomOffset;
                a.StartEccentricity = Point(curveStartIsLower ? e0 : e1);
                a.EndEccentricity = Point(curveStartIsLower ? e1 : e0);
            }
            a.Beam7DOF = ELongBoolean.lbFalse;

            if (TrySetLineAttrV161(s.Id, a))
            {
                // Only cache what AxisVM accepted - load placement relies on it.
                localXStartNode[s.Id] = s.CurveStartNode;
            }
            else if (wantsEcc && TrySetLineAttrV161(s.Id, withoutEcc))
            {
                // AxisVM refuses a custom offset on some members (a rib takes its offset from the domain it
                // lies on); the orientation still matters for local loads, so it is kept.
                localXStartNode[s.Id] = s.CurveStartNode;
                notes.Add(job.Context + ": AxisVM did not accept its eccentricity (a rib on a domain edge takes its offset from the domain); the local x orientation was set.");
            }
            else if (wantsEcc) notes.Add(job.Context + ": its eccentricity / orientation could not be set.");
            else if (orientRefused.Add(job.Src.Guid) && orientRefusedExample == null) orientRefusedExample = job.Context;
        }

        /// <summary>Curves whose local x orientation AxisVM refused - reported once, counted, after the member types.</summary>
        private readonly HashSet<string> orientRefused = new HashSet<string>(StringComparer.Ordinal);
        private string orientRefusedExample;

        private static RhinoXyz LerpXyz(RhinoXyz a, RhinoXyz b, double t)
        {
            return new RhinoXyz(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }

        /// <summary>Live line id -> the node its local x starts at (set by <see cref="OrientAndOffset"/>).</summary>
        private readonly Dictionary<int, int> localXStartNode = new Dictionary<int, int>();

        /// <summary>
        /// True when the segment's local x runs against <paramref name="s"/>'s curve direction - only
        /// possible where another curve owns (and therefore oriented) the segment.
        /// </summary>
        private bool LocalAgainstCurve(SegRef s)
        {
            int localStart;
            if (!localXStartNode.TryGetValue(s.Id, out localStart))
            {
                // Not oriented by us: AxisVM's StartNode is where local x starts.
                int startNode, endNode;
                if (!LineNodes(s.Id, out startNode, out endNode)) return s.Reversed;
                localStart = startNode;
            }
            return s.CurveStartNode != localStart;
        }

        private bool LineNodes(int lineId, out int startNode, out int endNode)
        {
            startNode = endNode = 0;
            try
            {
                IAxisVMLine ln = model.Lines.Item[lineId];
                startNode = ln.StartNode;
                endNode = ln.EndNode;
                return true;
            }
            catch { return false; }
        }

        private void ApplyLocalAxis(List<SegRef> segs, LineJob job)
        {
            string text = job.Src.Attr(AxKeys.LocalAxis);
            if (string.IsNullOrEmpty(text)) return;   // automatic orientation

            RhinoXyz dir;
            string err;
            if (!AttributeParser.TryParseDirection(text, out dir, out err))
            {
                notes.Add(string.Format("{0}: Ax_LocalAxis - {1}. Ignored.", job.Context, err));
                stats.AttributeErrors++;
                return;
            }

            int refId = catalog.ReferenceVector(dir);
            if (refId <= 0) return;

            foreach (SegRef s in segs)
            {
                try { model.Lines.Item[s.Id].Reference = refId; }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: its reference vector could not be set ({1}).", job.Context, ex.Message));
                    return;
                }
            }
        }

        /// <summary>
        /// Writes the curve's releases at its two far ends only. Interior split points stay rigid, so a
        /// beam cut in three by crossing members still behaves as one continuous beam.
        /// </summary>
        private void ApplyChainReleases(LineJob job)
        {
            string text = job.Src.Attr(AxKeys.Release);
            if (text == null) return;

            ReleaseSpec spec;
            string err;
            if (!AttributeParser.TryParseRelease(text, opt.Units, out spec, out err))
            {
                notes.Add(string.Format("{0}: Ax_Release - {1}. Ignored.", job.Context, err));
                stats.AttributeErrors++;
                return;
            }

            SegRef first = job.Chain.Segs[0];
            SegRef last = job.Chain.Segs[job.Chain.Segs.Count - 1];

            string e1 = null, e2 = null;
            if (spec.Start != null && !spec.Start.IsAllRigid && !SetEndRelease(first, true, spec.Start, out e1))
                notes.Add(string.Format("{0}: its start release could not be set ({1}).", job.Context, e1));
            if (spec.End != null && !spec.End.IsAllRigid && !SetEndRelease(last, false, spec.End, out e2))
                notes.Add(string.Format("{0}: its end release could not be set ({1}).", job.Context, e2));
        }

        /// <summary>
        /// Sets the release at the Rhino start (<paramref name="atRhinoStart"/>) or end of one segment.
        /// RLineAttr_V161 stores StartRelease at the line's lower-index node and EndRelease at the
        /// higher one, so the physical end is mapped onto that.
        /// </summary>
        private bool SetEndRelease(SegRef s, bool atRhinoStart, ReleaseSet set, out string error)
        {
            error = null;
            // The physical node at the requested curve end; the record's StartRelease belongs to the
            // lower-index node whatever the line's current local x orientation is.
            int node = atRhinoStart ? s.CurveStartNode : s.CurveEndNode;
            if (node <= 0) { error = "the end node is unknown"; return false; }
            bool atLowerIndex = node == Math.Min(s.CurveStartNode, s.CurveEndNode);

            RLineAttr_V161 a;
            if (!TryGetLineAttrV161(s.Id, out a)) { error = "BulkGetAttr_V161 failed"; return false; }
            if (atLowerIndex) a.StartRelease = ToReleasesV161(set);
            else a.EndRelease = ToReleasesV161(set);
            a.Beam7DOF = ELongBoolean.lbFalse;
            if (!TrySetLineAttrV161(s.Id, a)) { error = "AxisVM rejected the release"; return false; }
            return true;
        }

        /// <summary>A straight run of a curve split into several lines, to be joined into one AxisVM member.</summary>
        private class MemberRun
        {
            public LineJob Job;
            public int[] Lines;
        }

        private readonly List<MemberRun> pendingRuns = new List<MemberRun>();

        /// <summary>
        /// One AxisVM structural member per straight run of the curve (a polyline gives one per side). A line typed as beam
        /// or rib already is a member of its own, so only runs of several lines need joining - done later by
        /// <see cref="JoinMembers"/>, outside BeginUpdate.
        /// </summary>
        private void PlanMembers(LineJob job)
        {
            int i = 0, runs = 0, owned = 0;
            List<SegRef> segs = job.Chain.Segs;
            List<int> last = null;
            while (i < segs.Count)
            {
                int piece = segs[i].Piece;
                List<int> run = new List<int>();
                while (i < segs.Count && segs[i].Piece == piece)
                {
                    LineJob owner;
                    if (segmentOwner.TryGetValue(segs[i].Id, out owner) && owner == job) run.Add(segs[i].Id);
                    i++;
                }
                if (run.Count == 0) continue;
                runs++;
                owned += run.Count;
                last = run;
                if (run.Count > 1) pendingRuns.Add(new MemberRun { Job = job, Lines = run.ToArray() });
            }
            // The whole curve is one member: its loads go on the member, as AxisVM's own member loads.
            if (runs == 1 && owned == segs.Count) memberLineOfJob[job] = last[0];
        }

        /// <summary>
        /// Joins the planned runs into members. Members.Add while the model is in BeginUpdate corrupts AxisVM's heap on large
        /// models (it crashed at EndUpdate, or shortly after, loading 190 runs into an empty model - verified 2026-10-23), so
        /// Run calls this between two update blocks. A run AxisVM refuses keeps its lines as separate members, and its curve
        /// then takes its loads line by line.
        /// </summary>
        private void JoinMembers()
        {
            foreach (MemberRun r in pendingRuns)
            {
                int member;
                try { member = model.Members.Add(Ids(r.Lines)); }
                catch { member = 0; }
                if (member > 0) stats.MembersAssembled++;
                else memberLineOfJob.Remove(r.Job);
            }
        }

        /// <summary>
        /// The member of every curve that became exactly one member, read in one call just before the loads: joining renumbers
        /// members, so ids returned along the way go stale.
        /// </summary>
        private void ResolveMembers()
        {
            memberOfJob.Clear();
            if (memberLineOfJob.Count == 0) return;
            List<LineJob> jobs = new List<LineJob>(memberLineOfJob.Keys);
            int[] lines = new int[jobs.Count];
            for (int k = 0; k < jobs.Count; k++) lines[k] = memberLineOfJob[jobs[k]];
            try
            {
                Array ids;
                if (model.Lines.BulkGetMemberIds(Ids(lines), out ids) > 0 && ids != null && ids.Length >= jobs.Count)
                {
                    int lo = ids.GetLowerBound(0);
                    for (int k = 0; k < jobs.Count; k++)
                    {
                        int m = Convert.ToInt32(ids.GetValue(lo + k));
                        if (m > 0) memberOfJob[jobs[k]] = m;
                    }
                    return;
                }
            }
            catch { }
            for (int k = 0; k < jobs.Count; k++)
            {
                try { int m = model.Lines.Item[lines[k]].MemberID; if (m > 0) memberOfJob[jobs[k]] = m; } catch { }
            }
        }

        /// <summary>A line of each curve that became exactly one member (the member is looked up from it, see ResolveMembers).</summary>
        private readonly Dictionary<LineJob, int> memberLineOfJob = new Dictionary<LineJob, int>();

        /// <summary>Curves that became exactly one AxisVM structural member (all their segments, one straight run).</summary>
        private readonly Dictionary<LineJob, int> memberOfJob = new Dictionary<LineJob, int>();

        // ==================================================================
        // 4b - supports (nodal and line)
        // ==================================================================

        private void ApplySupports()
        {
            foreach (RhinoPoint p in rhino.Points)
            {
                string text = p.Attr(AxKeys.Support);
                if (text == null) continue;
                string context = p.Label("point");

                double[] k;
                string err;
                if (!AttributeParser.TryParseSupport(text, opt.Units, false, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_Support - {1}. Ignored.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.SupportsCreated++; continue; }

                int nodeId = LiveNode(M(p.Xyz));
                if (nodeId <= 0) { notes.Add(context + ": its node is missing, so no support was added."); continue; }

                try
                {
                    RSpringParamIndexes ix = new RSpringParamIndexes();
                    ix.x = springs.Translational(k[0]);
                    ix.y = springs.Translational(k[1]);
                    ix.z = springs.Translational(k[2]);
                    ix.xx = springs.Rotational(k[3]);
                    ix.yy = springs.Rotational(k[4]);
                    ix.zz = springs.Rotational(k[5]);

                    RNodalSupportSpringParams nsp = new RNodalSupportSpringParams();
                    nsp.SpringParamIndexes = ix;
                    nsp.IsolatorParamIndex = 0;
                    nsp.IsolatorD2 = 0.0;

                    int id = model.NodalSupports.AddNodalGlobal_V153(ref nsp, nodeId);
                    if (id <= 0)
                    {
                        RStiffnesses stiff = ToStiffnesses(k);
                        RNonLinearity nonlin = new RNonLinearity();
                        RResistances resist = new RResistances();
                        id = model.NodalSupports.AddNodalGlobal(ref stiff, ref nonlin, ref resist, nodeId);
                    }

                    if (id > 0) stats.SupportsCreated++;
                    else notes.Add(string.Format("{0}: AxisVM rejected the support (code {1}).", context, id));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: its support could not be created ({1}).", context, ex.Message));
                }
            }

            // Line supports: Ax_Support on a curve, along every segment of it.
            foreach (LineJob job in lineJobs)
            {
                string text = job.Src.Attr(AxKeys.Support);
                if (text == null) continue;
                if (IsConnector(job.Role) || job.Role == LineRole.EdgeHinge)
                {
                    notes.Add(job.Context + ": a line support cannot sit on a " + job.Role + ". Ignored.");
                    stats.AttributeErrors++;
                    continue;
                }

                double[] k;
                string err;
                if (!AttributeParser.TryParseSupport(text, opt.Units, true, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_Support - {1}. Ignored.", job.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }
                if (opt.DryRun) { stats.LineSupportsCreated++; continue; }

                bool rotationsIgnored = false;
                foreach (SegRef s in job.Chain.Segs)
                {
                    try
                    {
                        // A beam or rib: an elastic foundation along it (translations only, in its local x, y, z).
                        LineRole applied;
                        if (segmentRole.TryGetValue(s.Id, out applied) && (applied == LineRole.Beam || applied == LineRole.Rib))
                        {
                            RStiffnessesXYZ kf = new RStiffnessesXYZ { x = k[0], y = k[1], z = k[2] };
                            RNonLinearityXYZ nlf = new RNonLinearityXYZ();
                            RResistancesXYZ rsf = new RResistancesXYZ();
                            int fid = applied == LineRole.Beam
                                ? model.LineSupports.AddBeamElasticFoundation(s.Id, ref kf, ref nlf, ref rsf)
                                : model.LineSupports.AddRibElasticFoundation(s.Id, ref kf, ref nlf, ref rsf);
                            if (fid > 0) stats.LineSupportsCreated++;
                            else { notes.Add(string.Format("{0}: AxisVM rejected the elastic foundation (code {1}).", job.Context, fid)); break; }
                            if (k[3] != 0 || k[4] != 0 || k[5] != 0) rotationsIgnored = true;
                            continue;
                        }

                        // A plain line: an edge support. AxisVM only accepts one when it is told which domain the edge
                        // belongs to (code -103 otherwise), so it must lie on a slab / wall / plate edge.
                        int domainId = DomainOwningEdge(s.Id);
                        if (domainId <= 0)
                        {
                            notes.Add(job.Context + ": a support on a plain line must lie on the edge of a domain (slab, wall, plate) - this one is not; " +
                                      "make it a Beam or Rib for an elastic foundation along it. Ignored.");
                            break;
                        }
                        RStiffnesses stiff = ToStiffnesses(k);
                        RNonLinearity nonlin = new RNonLinearity();
                        RResistances resist = new RResistances();
                        int id = model.LineSupports.AddEdgeGlobal(ref stiff, ref nonlin, ref resist, s.Id, 0, 0, domainId, 0);
                        if (id > 0) stats.LineSupportsCreated++;
                        else { notes.Add(string.Format("{0}: AxisVM rejected the line support (code {1}).", job.Context, id)); break; }
                    }
                    catch (Exception ex)
                    {
                        notes.Add(string.Format("{0}: its line support could not be created ({1}).", job.Context, ex.Message));
                        break;
                    }
                }
                if (rotationsIgnored)
                    notes.Add(job.Context + ": an elastic foundation along a beam or rib takes the three translations only - the rotational stiffnesses of Ax_Support were ignored.");
            }
        }

        /// <summary>The domain (not a load panel) that has this line on its outer boundary or around a hole, or 0.</summary>
        private int DomainOwningEdge(int lineId)
        {
            foreach (SurfaceJob s in surfaceJobs)
            {
                if (IsPanel(s.Role) || s.DomainId <= 0) continue;
                foreach (int id in s.OuterChain.Ids()) if (id == lineId) return s.DomainId;
                foreach (Chain h in s.HoleChains) foreach (int id in h.Ids()) if (id == lineId) return s.DomainId;
            }
            return 0;
        }

        // ==================================================================
        // 4c - springs, node-node links and rigid links
        // ==================================================================

        private void BuildConnectors()
        {
            foreach (LineJob job in lineJobs)
            {
                if (!IsConnector(job.Role)) continue;

                if (job.Chain.IsEmpty)
                {
                    notes.Add(job.Context + ": no AxisVM line was found for this " + job.Role + ".");
                    continue;
                }
                int lineId = job.Chain.Segs[0].Id;

                switch (job.Role)
                {
                    case LineRole.Spring: CreateSpring(job, lineId); break;
                    case LineRole.NodeLink: CreateNodeLink(job, lineId); break;
                    case LineRole.RigidLink: CreateRigidLink(job, lineId); break;
                }
            }
        }

        /// <summary>
        /// A spring element. Written straight through the versioned line attribute (type + spring
        /// characteristics in one call) - setting the line type first on its own is rejected by AxisVM
        /// for springs, which is what made the earlier version leave springs as plain lines.
        /// </summary>
        private void CreateSpring(LineJob job, int lineId)
        {
            string text = job.Src.Attr(AxKeys.Stiffness);
            if (text == null)
            {
                notes.Add(job.Context + ": a Spring needs Ax_Stiffness. It was left as a plain line.");
                stats.AttributeErrors++;
                return;
            }

            double[] k;
            string err;
            if (!AttributeParser.TryParseStiffness(text, opt.Units, out k, out err))
            {
                notes.Add(string.Format("{0}: Ax_Stiffness - {1}. It was left as a plain line.", job.Context, err));
                stats.AttributeErrors++;
                return;
            }
            if (opt.DryRun) { stats.SpringsCreated++; return; }

            RLineAttr_V161 attr;
            if (TryGetLineAttrV161(lineId, out attr))
            {
                attr.LineType = ELineType.ltSpring;
                attr.SpringType = ESpringDirection.sdGlobal;
                RSpringCharacteristics sc = new RSpringCharacteristics();
                sc.x = springs.Translational(k[0]);
                sc.y = springs.Translational(k[1]);
                sc.z = springs.Translational(k[2]);
                sc.xx = springs.Rotational(k[3]);
                sc.yy = springs.Rotational(k[4]);
                sc.zz = springs.Rotational(k[5]);
                attr.SpringCharacteristics = sc;
                if (TrySetLineAttrV161(lineId, attr) && LineTypeIs(lineId, ELineType.ltSpring))
                {
                    stats.SpringsCreated++;
                    segmentRole[lineId] = LineRole.Spring;
                    return;
                }
            }

            // Fallback: the older definition call.
            try
            {
                RStiffnesses s = ToStiffnesses(k);
                int rc = model.Lines.Item[lineId].DefineAsSpring(ESpringDirection.sdGlobal, ref s);
                if (rc > 0)
                {
                    stats.SpringsCreated++;
                    segmentRole[lineId] = LineRole.Spring;
                }
                else notes.Add(string.Format("{0}: AxisVM rejected the spring definition (code {1}).", job.Context, rc));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: it could not be defined as a spring ({1}).", job.Context, ex.Message));
            }
        }

        /// <summary>
        /// A node-to-node link. RNNLinkElementRec needs MasterPoint (1 = start node) and an interface
        /// position; leaving those at 0 - as the earlier version did - makes AddNN fail.
        /// </summary>
        private void CreateNodeLink(LineJob job, int lineId)
        {
            double[] k;
            string coupled = job.Src.Attr(AxKeys.CoupledDof);
            string err;
            if (coupled == null)
            {
                notes.Add(job.Context + ": a NodeLink without Ax_CoupledDOF - all six DOFs were coupled.");
                k = AllCoupled();
            }
            else if (!AttributeParser.TryParseCoupledDofs(coupled, opt.Units, out k, out err))
            {
                notes.Add(string.Format("{0}: Ax_CoupledDOF - {1}. All six were coupled instead.", job.Context, err));
                stats.AttributeErrors++;
                k = AllCoupled();
            }
            if (opt.DryRun) { stats.LinksCreated++; return; }

            try
            {
                RNNLinkElementRec rec = new RNNLinkElementRec();
                rec.LineId = lineId;
                rec.SystemGLR = ESystem.sysGlobal;
                rec.MasterPoint = 1;
                rec.RefZId = 0;
                rec.PositionType = EBeamRibDistributionType.brdtLength;
                rec.Position = 0.0;
                rec.Stiffnesses = ToStiffnesses(k);
                rec.Resistances = new RResistances();
                rec.NonLinearity = new RNonLinearity();

                int id = model.LinkElements.AddNN(ref rec);
                if (id > 0) { stats.LinksCreated++; segmentRole[lineId] = LineRole.NodeLink; }
                else notes.Add(string.Format("{0}: AxisVM rejected the node-node link (code {1}).", job.Context, id));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: its node-node link could not be created ({1}).", job.Context, ex.Message));
            }
        }

        /// <summary>A rigid link is an AxisVM rigid body - truly rigid, no artificial stiffness.</summary>
        private void CreateRigidLink(LineJob job, int lineId)
        {
            if (opt.DryRun) { stats.RigidBodiesCreated++; return; }
            try
            {
                int rc = model.RigidBodies.Add(Ids(lineId));
                if (rc > 0) { stats.RigidBodiesCreated++; segmentRole[lineId] = LineRole.RigidLink; return; }
                notes.Add(string.Format("{0}: AxisVM rejected the rigid body (code {1}); a fully coupled node-node link was used instead.", job.Context, rc));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: the rigid body could not be created ({1}); a fully coupled node-node link was used instead.", job.Context, ex.Message));
            }
            CreateNodeLink(job, lineId);
        }

        // ==================================================================
        // 4c' - line-to-line links
        // ==================================================================

        /// <summary>Suffix of the RhinoGuid on the two link lines of a line-to-line link: "&lt;slave curve id&gt;#link".</summary>
        public const string LinkLineSuffix = "#link";

        /// <summary>Slave curve -> the two link lines drawn for its line-to-line link (stamped in StampRhinoIds).</summary>
        private readonly Dictionary<LineJob, int[]> linkLines = new Dictionary<LineJob, int[]>();

        /// <summary>
        /// Ax_LineLink on a curve: a line-to-line link from the curve it names (master) to this one (slave). AxisVM defines
        /// one by the two lines and two link lines joining their ends (RLLLinkElementRec.MasterStartLink / MasterEndLink are
        /// line ids - verified 2026-10-23), so those are drawn here, master start to the nearer slave end. Both curves must
        /// have stayed one AxisVM line each.
        /// </summary>
        private void BuildLineLinks()
        {
            Dictionary<string, LineJob> byGuid = new Dictionary<string, LineJob>(StringComparer.OrdinalIgnoreCase);
            foreach (LineJob j in lineJobs) byGuid[j.Src.Guid] = j;

            foreach (LineJob job in lineJobs)
            {
                string masterGuid = job.Src.Attr(AxKeys.LineLink);
                if (masterGuid == null) continue;
                LineJob master;
                if (!byGuid.TryGetValue(masterGuid.Trim(), out master) || master == job)
                {
                    notes.Add(job.Context + ": Ax_LineLink names '" + masterGuid + "', which is not a curve of this model. No link was created.");
                    stats.AttributeErrors++;
                    continue;
                }
                if (job.Chain.Segs.Count != 1 || master.Chain.Segs.Count != 1)
                {
                    notes.Add(string.Format("{0}: a line-to-line link needs both curves to stay one AxisVM line each; this one or {1} was split by crossing elements. No link was created.", job.Context, master.Context));
                    continue;
                }

                double[] k = AllCoupled();
                string stiffText = job.Src.Attr(AxKeys.Stiffness);
                string err;
                if (stiffText != null && !AttributeParser.TryParseStiffness(stiffText, opt.Units, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_Stiffness - {1}. The link was made rigid.", job.Context, err));
                    stats.AttributeErrors++;
                    k = AllCoupled();
                }
                if (opt.DryRun) { stats.LineLinksCreated++; continue; }

                SegRef m = master.Chain.Segs[0], s = job.Chain.Segs[0];
                double[] ms = NodeXyz(m.CurveStartNode), me = NodeXyz(m.CurveEndNode), ss = NodeXyz(s.CurveStartNode), se = NodeXyz(s.CurveEndNode);
                if (ms == null || me == null || ss == null || se == null) { notes.Add(job.Context + ": its line-to-line link could not be placed."); continue; }
                bool straight = Dist(ms, ss) + Dist(me, se) <= Dist(ms, se) + Dist(me, ss);
                int toStart = straight ? s.CurveStartNode : s.CurveEndNode, toEnd = straight ? s.CurveEndNode : s.CurveStartNode;

                int l1 = 0, l2 = 0;
                try
                {
                    RLineGeomData g = new RLineGeomData();
                    l1 = model.Lines.Add(m.CurveStartNode, toStart, ELineGeomType.lgtStraightLine, ref g);
                    l2 = model.Lines.Add(m.CurveEndNode, toEnd, ELineGeomType.lgtStraightLine, ref g);
                    RLLLinkElementRec rec = new RLLLinkElementRec
                    {
                        MasterLine = m.Id, SlaveLine = s.Id, MasterStartLink = l1, MasterEndLink = l2,
                        PositionType = EBeamRibDistributionType.brdtLength, Position = 0.0,
                        Stiffnesses = ToStiffnesses(k), Resistances = new RResistances(), NonLinearity = new RNonLinearity()
                    };
                    int id = l1 > 0 && l2 > 0 ? model.LinkElements.AddLL(ref rec) : 0;
                    if (id > 0)
                    {
                        stats.LineLinksCreated++;
                        linkLines[job] = new[] { l1, l2 };
                        // AxisVM turns linked beams into ribs at some later model update (seen after adding a spring); do
                        // it now, so what this load records is what AxisVM keeps and Send reports no change later.
                        foreach (int l in new[] { m.Id, s.Id }) MakeRib(l, job.Context);
                        continue;
                    }
                    notes.Add(string.Format("{0}: AxisVM rejected the line-to-line link to {1} (code {2}).", job.Context, master.Context, id));
                }
                catch (Exception ex) { notes.Add(string.Format("{0}: its line-to-line link could not be created ({1}).", job.Context, ex.Message)); }
                // Not linked: the two helper lines go again (higher index first).
                foreach (int l in new[] { Math.Max(l1, l2), Math.Min(l1, l2) }) { try { if (l > 0) model.Lines.Delete(l); } catch { } }
            }
        }

        private void MakeRib(int lineId, string context)
        {
            RLineAttr_V161 a;
            if (!TryGetLineAttrV161(lineId, out a) || a.LineType != ELineType.ltBeam) return;
            a.LineType = ELineType.ltRib;
            if (TrySetLineAttrV161(lineId, a) && LineTypeIs(lineId, ELineType.ltRib))
            {
                segmentRole[lineId] = LineRole.Rib;
                return;
            }
            notes.Add(context + ": a beam in a line-to-line link could not be made a rib; AxisVM may change it to one later.");
        }

        private static double[] AllCoupled()
        {
            double[] k = new double[6];
            for (int i = 0; i < 6; i++) k[i] = AttributeParser.LinkRigidStiffness;
            return k;
        }

        // ==================================================================
        // 4d - edge hinges
        // ==================================================================

        /// <summary>
        /// An EdgeHinge curve releases the domains along it. Without Ax_HingeDomains every domain whose
        /// edge runs along the curve is released; with it, only the named ones (a polysurface may be
        /// named by its plain id - all its faces then match).
        /// </summary>
        private void BuildEdgeHinges()
        {
            foreach (LineJob job in lineJobs)
            {
                bool wanted = job.Role == LineRole.EdgeHinge || (job.Role == LineRole.None && job.Src.Attr(AxKeys.HingeDomains) != null);
                if (!wanted) continue;

                // All-zero stiffness would disconnect the domain completely, so there is no default.
                string stiffText = job.Src.Attr(AxKeys.Stiffness);
                if (stiffText == null)
                {
                    notes.Add(job.Context + ": an EdgeHinge needs Ax_Stiffness (e.g. 1e6,1e6,1e6,0,0,0 for a line hinge). No hinge was created.");
                    stats.AttributeErrors++;
                    continue;
                }
                double[] k;
                string err;
                if (!AttributeParser.TryParseLineStiffness(stiffText, opt.Units, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_Stiffness - {1}. No hinge was created.", job.Context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                List<string> named = AttributeParser.SplitList(job.Src.Attr(AxKeys.HingeDomains));
                HashSet<int> chainIds = new HashSet<int>(job.Chain.Ids());
                int made = 0;

                foreach (SurfaceJob s in surfaceJobs)
                {
                    if (named.Count > 0 && !Names(named, s.Src)) continue;

                    if (IsPanel(s.Role))
                    {
                        if (named.Count > 0) notes.Add(string.Format("{0}: {1} is a load panel - load panels have no edge hinges.", job.Context, s.Context));
                        continue;
                    }

                    List<int> shared = new List<int>();
                    foreach (int id in s.OuterChain.Ids()) if (chainIds.Contains(id)) shared.Add(id);
                    foreach (Chain h in s.HoleChains) foreach (int id in h.Ids()) if (chainIds.Contains(id)) shared.Add(id);

                    if (shared.Count == 0)
                    {
                        if (named.Count > 0) notes.Add(string.Format("{0}: it does not run along an edge of {1}. No hinge there.", job.Context, s.Context));
                        continue;
                    }

                    if (opt.DryRun) { stats.HingesCreated += shared.Count; made++; continue; }
                    if (s.DomainId <= 0) continue;

                    foreach (int lineId in shared)
                        if (AddEdgeHinge(lineId, s.DomainId, k, job.Context)) { stats.HingesCreated++; made++; }
                }

                if (made == 0)
                    notes.Add(job.Context + ": no domain edge was found along this edge hinge - nothing was released.");
            }
        }

        // ==================================================================
        // 4e - rigid diaphragms
        // ==================================================================

        /// <summary>
        /// Ax_Diaphragm: curves and surfaces with the same name become one AxisVM rigid diaphragm (on a surface the
        /// name stands for all its edges, holes included). Their lines are first taken out of any diaphragm they were
        /// in, so a re-import moves them instead of leaving them in two.
        /// </summary>
        private void BuildDiaphragms()
        {
            var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            Action<string, IEnumerable<int>> take = (name, ids) =>
            {
                List<int> l;
                if (!byName.TryGetValue(name, out l)) { l = new List<int>(); byName[name] = l; order.Add(name); }
                foreach (int id in ids) if (id > 0 && !l.Contains(id)) l.Add(id);
            };

            foreach (LineJob job in lineJobs)
            {
                string name = job.Src.Attr(AxKeys.Diaphragm);
                if (name != null) take(name, job.Chain.Ids());
            }
            foreach (SurfaceJob s in surfaceJobs)
            {
                string name = s.Src.Attr(AxKeys.Diaphragm);
                if (name == null) continue;
                if (IsPanel(s.Role)) { notes.Add(s.Context + ": a load panel cannot be a rigid diaphragm - Ax_Diaphragm ignored."); continue; }
                take(name, s.OuterChain.Ids());
                foreach (Chain h in s.HoleChains) take(name, h.Ids());
            }

            foreach (string name in order)
            {
                List<int> ids = byName[name];
                if (ids.Count == 0) { notes.Add("Rigid diaphragm '" + name + "': none of its lines were found."); continue; }
                if (opt.DryRun) { stats.DiaphragmsCreated++; continue; }
                try { model.Diaphragm.RemoveLinesFromDiaphragm(Ids(ids.ToArray())); } catch { }

                // "Only changes": the kept members of this diaphragm join (see TakeKeptDiaphragm).
                List<int> kept = new List<int>();
                Action restore = null, dropOld = null;
                if (Partial) kept = TakeKeptDiaphragm(name, ids, out restore, out dropOld);
                ids.AddRange(kept);

                string plane;
                int dof = DiaphragmDof(ids, out plane);
                if (dof == 0)
                {
                    // The kept diaphragm stays as it was; only the rebuilt lines are left out.
                    notes.Add("Rigid diaphragm '" + name + "': its lines do not lie in one XY, XZ or YZ plane - " +
                              (kept.Count > 0 ? "the rebuilt lines were not added to it." : "not created."));
                    continue;
                }
                if (dropOld != null) dropOld();
                int rc;
                try { rc = model.Diaphragm.Add(dof, Ids(ids.ToArray())); }
                catch (Exception ex) { rc = 0; notes.Add("Rigid diaphragm '" + name + "': AxisVM refused it (" + ex.Message + ")."); }
                if (rc > 0) stats.DiaphragmsCreated++;
                else if (rc < 0) notes.Add(string.Format("Rigid diaphragm '{0}' ({1} plane): AxisVM refused it (code {2}).", name, plane, rc));
                if (restore != null) restore();
            }
        }

        private Dictionary<string, List<int>> linesByGuid;

        /// <summary>
        /// "Only changes": the AxisVM diaphragm(s) holding the kept members named <paramref name="name"/> are deleted and
        /// their lines returned, with <paramref name="restore"/> to run once the diaphragm is made again with the rebuilt
        /// lines added. Empty when no kept member is in that diaphragm.
        ///
        /// Diaphragm.Add makes every beam and rib in it a plain line; their members (and member loads) stay. Restoring the
        /// MEMBER records (BulkSetMembers_V161) types them again as they were. Restoring line by line does not do: each
        /// re-typed line gets a member of its own with a copy of the member's loads (513 lines: +361 members, +258 loads,
        /// verified 2026-10-23). Typed lines outside any member are restored line by line.
        /// </summary>
        private List<int> TakeKeptDiaphragm(string name, List<int> rebuilt, out Action restore, out Action dropOld)
        {
            restore = null;
            dropOld = null;
            List<string> guids;
            if (!opt.Diff.KeptDiaphragms.TryGetValue(name, out guids) || guids.Count == 0) return new List<int>();

            if (linesByGuid == null)
            {
                linesByGuid = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
                int n = SafeLineCount();
                for (int i = 1; i <= n && la != null; i++)
                    foreach (string g in LinkState.Guids(la, i))
                    {
                        List<int> l;
                        if (!linesByGuid.TryGetValue(g, out l)) { l = new List<int>(); linesByGuid[g] = l; }
                        l.Add(i);
                    }
            }
            HashSet<int> members = new HashSet<int>();
            foreach (string g in guids) { List<int> l; if (linesByGuid.TryGetValue(g, out l)) members.UnionWith(l); }
            // Kept surfaces in the diaphragm: their domain's edges.
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            HashSet<string> keptSet = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);
            int nd = TryCount(() => model.Domains.Count);
            for (int i = 1; i <= nd && da != null; i++)
                if (LinkState.Guids(da, i).Exists(keptSet.Contains))
                {
                    foreach (int l in LinkState.ContourLines(model, i)) members.Add(l);
                    foreach (List<int> h in LinkState.HoleLines(model, i)) foreach (int l in h) members.Add(l);
                }

            List<int> found = new List<int>();
            HashSet<int> lines = new HashSet<int>();
            int count = TryCount(() => model.Diaphragm.Count);
            for (int d = 1; d <= count; d++)
            {
                try
                {
                    Array arr;
                    model.Diaphragm.GetLines(d, out arr);
                    List<int> dl = LinkState.IntList(arr);
                    if (!dl.Exists(members.Contains)) continue;
                    found.Add(d);
                    lines.UnionWith(dl);
                }
                catch { }
            }
            lines.ExceptWith(rebuilt);
            if (lines.Count == 0) return new List<int>();

            List<int> keep = new List<int>(lines);

            // What to put back: the members the typed lines are in, and typed lines in no member.
            var memberRecs = new List<KeyValuePair<int, RLineAttr_V161>>();
            var lineRecs = new List<KeyValuePair<int, RLineAttr_V161>>();
            try
            {
                Array mids;
                model.Lines.BulkGetMemberIds(Ids(keep.ToArray()), out mids);
                List<int> memberOf = LinkState.IntList(mids);
                HashSet<int> ofLines = new HashSet<int>();
                for (int k = 0; k < keep.Count && k < memberOf.Count; k++)
                {
                    if (memberOf[k] > 0) { ofLines.Add(memberOf[k]); continue; }
                    RLineAttr_V161 a;
                    if (TryGetLineAttrV161(keep[k], out a) && LinkState.LineTypeName(a.LineType).Length > 0) lineRecs.Add(new KeyValuePair<int, RLineAttr_V161>(keep[k], a));
                }
                if (ofLines.Count > 0)
                {
                    List<int> ml = new List<int>(ofLines);
                    Array recs;
                    if (model.Members.BulkGetMembers_V161(Ids(ml.ToArray()), out recs) > 0 && recs != null)
                    {
                        int lo = recs.GetLowerBound(0);
                        for (int k = 0; k < ml.Count && k < recs.Length; k++) memberRecs.Add(new KeyValuePair<int, RLineAttr_V161>(ml[k], (RLineAttr_V161)recs.GetValue(lo + k)));
                    }
                }
            }
            catch { }

            restore = () =>
            {
                int failed = 0;
                if (memberRecs.Count > 0)
                {
                    // A fresh array of our own record type (the returned one is only type-equivalent, see TrySetLineAttrV161).
                    Array ids = Array.CreateInstance(typeof(int), new[] { memberRecs.Count }, new[] { 1 });
                    Array recs = Array.CreateInstance(typeof(RLineAttr_V161), new[] { memberRecs.Count }, new[] { 1 });
                    for (int k = 0; k < memberRecs.Count; k++) { ids.SetValue(memberRecs[k].Key, k + 1); recs.SetValue(memberRecs[k].Value, k + 1); }
                    try { if (model.Members.BulkSetMembers_V161(ids, recs) < memberRecs.Count) failed++; }
                    catch { failed++; }
                }
                foreach (KeyValuePair<int, RLineAttr_V161> kv in lineRecs) if (!TrySetLineAttrV161(kv.Key, kv.Value)) failed++;
                if (failed > 0) notes.Add("Rigid diaphragm '" + name + "': some members already in it could not be given their type back - check them.");
            };
            // Deleted only once the new diaphragm is known to be valid (BuildDiaphragms).
            dropOld = () => { for (int k = found.Count - 1; k >= 0; k--) { try { model.Diaphragm.Delete(found[k]); } catch { } } };
            return keep;
        }

        /// <summary>
        /// The dof code IAxisVMDiaphragm.Add takes for the plane the lines lie in: the in-plane freedoms as bits
        /// (ux 1, uy 2, uz 4, rx 8, ry 16, rz 32) - XY 35, XZ 21, YZ 14, the only values AxisVM accepts. 0 when the
        /// lines are not flat in one of those planes.
        /// </summary>
        private int DiaphragmDof(List<int> lineIds, out string plane)
        {
            plane = "";
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            foreach (int id in lineIds)
            {
                int[] ends = LinkState.LineNodes(model, id);
                if (ends == null) continue;
                foreach (int nd in ends)
                {
                    double[] p = LinkState.NodeXyz(model, Math.Abs(nd));
                    if (p == null) continue;
                    for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], p[k]); hi[k] = Math.Max(hi[k], p[k]); }
                }
            }
            if (lo[0] > hi[0]) return 0;
            double tol = Math.Max(opt.MergeToleranceM, 1e-6) * 2;
            double dx = hi[0] - lo[0], dy = hi[1] - lo[1], dz = hi[2] - lo[2];
            if (dz <= tol) { plane = "XY"; return 35; }
            if (dy <= tol) { plane = "XZ"; return 21; }
            if (dx <= tol) { plane = "YZ"; return 14; }
            return 0;
        }

        private static bool Names(List<string> named, RhinoDomain d)
        {
            foreach (string n in named)
                if (string.Equals(n, d.Guid, StringComparison.OrdinalIgnoreCase) || string.Equals(n, d.BaseGuid, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private bool AddEdgeHinge(int lineId, int domainId, double[] k, string context)
        {
            try
            {
                REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172();
                rec.LineId = lineId;
                rec.DomainId = domainId;
                RSpringParamIndexes ix = new RSpringParamIndexes();
                ix.x = springs.LineTranslational(k[0]);
                ix.y = springs.LineTranslational(k[1]);
                ix.z = springs.LineTranslational(k[2]);
                ix.xx = springs.LineRotational(k[3]);
                ix.yy = springs.LineRotational(k[4]);
                ix.zz = springs.LineRotational(k[5]);
                rec.SpringParamIndexes = ix;
                int id = model.EdgeConnections.Add_V172(ref rec);
                if (id > 0) return true;
            }
            catch { }

            // Fallback: the older record with plain stiffness values.
            try
            {
                REdgeConnectionRec rec = new REdgeConnectionRec();
                rec.LineId = lineId;
                rec.DomainId = domainId;
                rec.Stiffnesses = ToStiffnesses(k);
                rec.Resistances = new RResistances();
                int id = model.EdgeConnections.Add(ref rec);
                if (id > 0) return true;
                notes.Add(string.Format("{0}: AxisVM rejected the edge hinge onto domain {1} (code {2}).", context, domainId, id));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: the edge hinge onto domain {1} could not be created ({2}).", context, domainId, ex.Message));
            }
            return false;
        }
    }
}
