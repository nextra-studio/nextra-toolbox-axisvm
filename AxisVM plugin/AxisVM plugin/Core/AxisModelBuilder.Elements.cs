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
                AssembleMembers(job);
            }
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
            if (p.HasEcc && role != LineRole.Truss)
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
            else notes.Add(job.Context + (p.HasEcc ? ": its eccentricity / orientation could not be set." : ": its local x orientation could not be set."));
        }

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

        /// <summary>One AxisVM structural member per straight run of the curve (a polyline gives one per side).</summary>
        private void AssembleMembers(LineJob job)
        {
            int i = 0;
            List<SegRef> segs = job.Chain.Segs;
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
                try
                {
                    if (model.Members.Add(Ids(run.ToArray())) > 0 && run.Count > 1) stats.MembersAssembled++;
                }
                catch { }
            }
        }

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

                foreach (SegRef s in job.Chain.Segs)
                {
                    try
                    {
                        RStiffnesses stiff = ToStiffnesses(k);
                        RNonLinearity nonlin = new RNonLinearity();
                        RResistances resist = new RResistances();
                        int id = model.LineSupports.AddEdgeGlobal(ref stiff, ref nonlin, ref resist, s.Id, 0, 0, 0, 0);
                        if (id > 0) stats.LineSupportsCreated++;
                        else { notes.Add(string.Format("{0}: AxisVM rejected the line support (code {1}).", job.Context, id)); break; }
                    }
                    catch (Exception ex)
                    {
                        notes.Add(string.Format("{0}: its line support could not be created ({1}).", job.Context, ex.Message));
                        break;
                    }
                }
            }
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
