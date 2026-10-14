using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AxisVM;
using AxisVM_plugin.Functions;

namespace AxisVM_plugin.Core
{
    /// <summary>What the import dialog collected.</summary>
    public class ImportOptions
    {
        public AxUnits Units = new AxUnits();

        /// <summary>Two vertices closer than this become one AxisVM node [m].</summary>
        public double MergeToleranceM = 0.001;

        /// <summary>Parse and report only - nothing is written to the model.</summary>
        public bool DryRun;

        /// <summary>
        /// After the build, run AxisVM's line auto-intersection (CrossLines) so lines that only cross
        /// visually get a shared node, then re-assemble each Rhino curve's resulting segments into one
        /// structural member with releases kept only at its two far ends.
        /// </summary>
        public bool AutoIntersect = true;

        /// <summary>Reserved for the GUID-keyed re-sync mode; v1 only reports that it was asked for.</summary>
        public bool UpdateExisting;

        /// <summary>Where the JSON came from, used to place the id-map sidecar if one is needed.</summary>
        public string SourcePath = "";
    }

    /// <summary>Counters for the run summary.</summary>
    public class ImportStats
    {
        public int NodesCreated;
        public int NodesShared;
        public int LinesCreated;
        public int LinesMerged;
        public int DomainsCreated;
        public int PanelsCreated;
        public int SupportsCreated;
        public int LinksCreated;
        public int HingesCreated;
        public int LoadsCreated;
        public int PartsCreated;
        public int AttributeErrors;
        public int MembersAssembled;
    }

    /// <summary>
    /// Rebuilds an exported Rhino document as AxisVM geometry, in the order AxisVM's own object model
    /// forces: nodes exist before lines can reference them, lines exist before a domain can be built
    /// from their ids, domains exist before an edge hinge can name one, and everything exists before
    /// it can be put into a part.
    ///
    /// Two rules run through the whole class and are worth stating once:
    ///
    ///   * A coordinate is only ever turned into a node through <see cref="NodeIndex"/>. A beam
    ///     endpoint and a slab corner at the same place therefore become the same node, which is what
    ///     lets the model actually mesh.
    ///
    ///   * A line is identified by its ordered node pair, not by its Rhino id. Drawing a member line
    ///     and a hinge line on top of each other in Rhino - which is normal practice - yields one
    ///     AxisVM line carrying both roles. Nothing is ever created twice and nothing is deleted.
    /// </summary>
    public class AxisModelBuilder
    {
        private readonly IAxisVMApplication app;
        private readonly IAxisVMModel model;
        private readonly RhinoModel rhino;
        private readonly ImportOptions opt;
        private readonly List<string> notes;
        private readonly CatalogResolver catalog;
        private readonly SpringParamResolver springs;
        private readonly ImportStats stats = new ImportStats();

        private readonly NodeIndex nodes;

        /// <summary>"loNodeId|hiNodeId" -> the one AxisVM line for that pair.</summary>
        private readonly Dictionary<string, LineJob> lineByPair = new Dictionary<string, LineJob>(StringComparer.Ordinal);

        /// <summary>Rhino line id -> the AxisVM line it resolved to (several Rhino lines may share one).</summary>
        private readonly Dictionary<string, LineJob> lineByRhinoGuid = new Dictionary<string, LineJob>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int> domainByGuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> panelByGuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<int>> panelsByGuid = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Part name -> the items to put in it, gathered across all three element passes.</summary>
        private readonly Dictionary<string, List<RPartItem>> partItems =
            new Dictionary<string, List<RPartItem>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rhino id -> what it became, for the GUID stamp and the sidecar fallback.</summary>
        private readonly List<string> idMap = new List<string>();

        private bool attachmentsWork = true;

        public ImportStats Stats { get { return stats; } }

        public AxisModelBuilder(IAxisVMApplication app, IAxisVMModel model, RhinoModel rhino, ImportOptions opt, List<string> notes)
        {
            this.app = app;
            this.model = model;
            this.rhino = rhino;
            this.opt = opt;
            this.notes = notes;
            this.catalog = new CatalogResolver(model, notes);
            this.springs = new SpringParamResolver(model, notes);
            this.nodes = new NodeIndex(opt.MergeToleranceM);
        }

        // ==================================================================
        // Entry point
        // ==================================================================

        public void Run()
        {
            ValidateAttributeNames();

            BuildNodes();
            BuildLines();
            ApplyLineAttributes();
            BuildDomains();
            ApplySupports();
            BuildLinksAndHinges();
            BuildLoads();
            if (opt.AutoIntersect && !opt.DryRun) IntersectAndAssemble();
            BuildParts();
            StampRhinoIds();
        }

        /// <summary>
        /// Flags Ax_* keys this version does not know, before anything is written. A typo such as
        /// "Ax_Materal" would otherwise be silently ignored and the beam would quietly get the
        /// fallback material - the single most confusing way for this import to go wrong.
        /// </summary>
        private void ValidateAttributeNames()
        {
            List<RhinoEntity> all = new List<RhinoEntity>();
            foreach (RhinoPoint p in rhino.Points) all.Add(p);
            foreach (RhinoLine l in rhino.Lines) all.Add(l);
            foreach (RhinoDomain d in rhino.Domains) all.Add(d);

            Dictionary<string, bool> reported = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (RhinoEntity e in all)
            {
                foreach (string key in e.Attrs.Keys)
                {
                    if (!key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (AxKeys.IsKnown(key)) continue;
                    if (reported.ContainsKey(key)) continue;
                    reported[key] = true;

                    notes.Add(string.Format("Attribute '{0}' is not one this addon knows and was ignored. Check it for a typo.", key));
                    stats.AttributeErrors++;
                }
            }
        }

        // ==================================================================
        // Pass 1 - nodes
        // ==================================================================

        private void BuildNodes()
        {
            foreach (RhinoPoint p in rhino.Points)
                NodeFor(p.Xyz);

            foreach (RhinoLine l in rhino.Lines)
            {
                NodeFor(l.Start);
                NodeFor(l.End);
            }

            foreach (RhinoDomain d in rhino.Domains)
                foreach (RhinoXyz v in d.Contour)
                    NodeFor(v);
        }

        /// <summary>
        /// The single door through which every coordinate becomes a node. Converts to metres, looks
        /// for an existing node within the merge tolerance, and only adds one when there is none.
        /// </summary>
        private int NodeFor(RhinoXyz p)
        {
            double x = opt.Units.Length(p.X);
            double y = opt.Units.Length(p.Y);
            double z = opt.Units.Length(p.Z);

            int existing;
            if (nodes.TryFind(x, y, z, out existing))
            {
                stats.NodesShared++;
                return existing;
            }

            int id;
            if (!opt.DryRun)
            {
                try { id = model.Nodes.Add(x, y, z); }
                catch (Exception ex)
                {
                    notes.Add(string.Format("A node at {0} could not be created ({1}).", Fmt(x, y, z), ex.Message));
                    return 0;
                }
                if (id <= 0)
                {
                    notes.Add(string.Format("AxisVM rejected a node at {0} (code {1}).", Fmt(x, y, z), id));
                    return 0;
                }
            }
            else
            {
                // On a dry run the ids are synthetic, so the counts and the pair-merge logic still work.
                id = -(stats.NodesCreated + 1);
            }

            nodes.Add(x, y, z, id);
            stats.NodesCreated++;
            return id;
        }

        // ==================================================================
        // Pass 2 - lines
        // ==================================================================

        /// <summary>One AxisVM line, together with every Rhino line that resolved onto it.</summary>
        private class LineJob
        {
            public int LineId;
            public int NodeA;
            public int NodeB;
            public List<RhinoLine> Sources = new List<RhinoLine>();

            /// <summary>The source that decided the structural type, if any did.</summary>
            public RhinoLine Structural;
            public string TypeName = "";
            public bool IsDomainContour;
        }

        private void BuildLines()
        {
            // Explicit Rhino lines first, so a domain contour reuses them rather than the other way
            // round - that is what makes an edge beam and the slab edge the same AxisVM line.
            foreach (RhinoLine l in rhino.Lines)
            {
                int a = NodeFor(l.Start);
                int b = NodeFor(l.End);

                if (a == 0 || b == 0) continue;
                if (a == b)
                {
                    notes.Add(string.Format("{0}: its two ends land on the same node - it is shorter than the merge tolerance. Skipped.",
                        l.Label("line")));
                    continue;
                }

                LineJob job = LineFor(a, b);
                if (job == null) continue;

                job.Sources.Add(l);
                lineByRhinoGuid[l.Guid] = job;
            }

            // Then every domain contour edge.
            foreach (RhinoDomain d in rhino.Domains)
            {
                for (int i = 0; i < d.Contour.Count; i++)
                {
                    int a = NodeFor(d.Contour[i]);
                    int b = NodeFor(d.Contour[(i + 1) % d.Contour.Count]);
                    if (a == 0 || b == 0 || a == b) continue;

                    LineJob job = LineFor(a, b);
                    if (job != null) job.IsDomainContour = true;
                }
            }

            ReportMerges();
        }

        /// <summary>Finds or creates the single AxisVM line joining two nodes.</summary>
        private LineJob LineFor(int nodeA, int nodeB)
        {
            string key = PairKey(nodeA, nodeB);

            LineJob existing;
            if (lineByPair.TryGetValue(key, out existing)) return existing;

            int id;
            if (!opt.DryRun)
            {
                RLineGeomData geom = new RLineGeomData();
                try { id = model.Lines.Add(nodeA, nodeB, ELineGeomType.lgtStraightLine, ref geom); }
                catch (Exception ex)
                {
                    notes.Add(string.Format("A line between nodes {0} and {1} could not be created ({2}).", nodeA, nodeB, ex.Message));
                    return null;
                }
                if (id <= 0)
                {
                    notes.Add(string.Format("AxisVM rejected a line between nodes {0} and {1} (code {2}).", nodeA, nodeB, id));
                    return null;
                }
            }
            else
            {
                id = -(stats.LinesCreated + 1);
            }

            LineJob job = new LineJob { LineId = id, NodeA = nodeA, NodeB = nodeB };
            lineByPair[key] = job;
            stats.LinesCreated++;
            return job;
        }

        /// <summary>
        /// Reports every place two or more Rhino lines collapsed onto one AxisVM line. This is normal
        /// and intended, but it changes the element count, so it must be visible in the summary rather
        /// than silent.
        /// </summary>
        private void ReportMerges()
        {
            foreach (KeyValuePair<string, LineJob> kv in lineByPair)
            {
                LineJob job = kv.Value;
                if (job.Sources.Count < 2) continue;

                stats.LinesMerged += job.Sources.Count - 1;

                List<string> which = new List<string>();
                foreach (RhinoLine src in job.Sources)
                {
                    string type = src.Attr(AxKeys.Type);
                    which.Add(Short(src.Guid) + (type != null ? " (" + type + ")" : ""));
                }

                notes.Add(string.Format("{0} Rhino lines share the same two endpoints and became one AxisVM line: {1}.",
                    job.Sources.Count, string.Join(", ", which.ToArray())));
            }
        }

        private static string PairKey(int a, int b)
        {
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            return lo.ToString(CultureInfo.InvariantCulture) + "|" + hi.ToString(CultureInfo.InvariantCulture);
        }

        // ==================================================================
        // Pass 3 - line attributes
        // ==================================================================

        private void ApplyLineAttributes()
        {
            foreach (KeyValuePair<string, LineJob> kv in lineByPair)
            {
                LineJob job = kv.Value;
                if (job.Sources.Count == 0) continue;   // a bare contour edge - AxisVM types it itself

                ResolveStructuralRole(job);
                if (job.Structural == null) continue;

                ApplyOneLineType(job);
            }
        }

        /// <summary>
        /// Decides which of the Rhino lines sharing this AxisVM line sets its element type.
        /// Ax_Type=EdgeHinge is deliberately *not* a structural role: an edge hinge is an extra record
        /// attached to a line, so a rib along a slab edge keeps being a rib and gains a hinge.
        /// </summary>
        private void ResolveStructuralRole(LineJob job)
        {
            foreach (RhinoLine src in job.Sources)
            {
                string type = src.Attr(AxKeys.Type);
                if (type == null) continue;

                string norm = type.Trim().ToLowerInvariant();
                if (norm == "edgehinge" || norm == "edge hinge" || norm == "hinge") continue;

                if (job.Structural == null)
                {
                    job.Structural = src;
                    job.TypeName = norm;
                    continue;
                }

                if (norm != job.TypeName)
                {
                    notes.Add(string.Format(
                        "{0} and {1} sit on the same two endpoints but ask for different element types ('{2}' and '{3}'). '{2}' was used.",
                        job.Structural.Label("line"), src.Label("line"), job.TypeName, norm));
                    stats.AttributeErrors++;
                }
            }
        }

        private void ApplyOneLineType(LineJob job)
        {
            RhinoLine src = job.Structural;
            string context = src.Label("line");

            ELineType lineType;
            switch (job.TypeName)
            {
                case "beam": lineType = ELineType.ltBeam; break;
                case "rib": lineType = ELineType.ltRib; break;
                case "truss": lineType = ELineType.ltTruss; break;
                case "spring": lineType = ELineType.ltSpring; break;
                case "rigidlink":
                case "rigid link":
                case "nodelink":
                case "nodenodelink":
                case "node link": lineType = ELineType.ltNNLink; break;
                case "simple":
                case "simpleline":
                case "simple line": lineType = ELineType.ltSimpleLine; break;
                default:
                    notes.Add(string.Format("{0}: '{1}' is not a line type this addon knows. Left as drawn.", context, job.TypeName));
                    stats.AttributeErrors++;
                    return;
            }

            // In AxisVM, a line on a domain boundary / overlapping a domain must be a Rib, not a Beam.
            if (lineType == ELineType.ltBeam && job.IsDomainContour)
            {
                lineType = ELineType.ltRib;
                notes.Add(string.Format("{0}: sits on a domain boundary/contour - automatically converted from Beam to Rib.", context));
            }

            if (opt.DryRun) return;

            bool isBeamOrRib = lineType == ELineType.ltBeam || lineType == ELineType.ltRib || lineType == ELineType.ltTruss;

            RLineAttr attr;
            if (!TryGetLineAttr(job.LineId, out attr))
            {
                notes.Add(string.Format("{0}: its attributes could not be read back from AxisVM.", context));
                return;
            }

            attr.LineType = lineType;

            if (isBeamOrRib)
            {
                int matId = catalog.Material(src.Attr(AxKeys.Material), context);
                if (matId > 0) attr.MaterialIndex = matId;

                string startName, endName;
                AttributeParser.ParseSectionNames(src.Attr(AxKeys.Section) ?? "", out startName, out endName);
                int startSec = catalog.Section(startName, context);
                int endSec = string.Equals(startName, endName, StringComparison.OrdinalIgnoreCase)
                    ? startSec
                    : catalog.Section(endName, context);
                if (startSec > 0) attr.StartCrossSectionIndex = startSec;
                if (endSec > 0) attr.EndCrossSectionIndex = endSec;

                string eccText = src.Attr(AxKeys.Eccentricity);
                if (eccText != null)
                {
                    RhinoXyz e1, e2;
                    string err;
                    if (AttributeParser.TryParseEccentricity(eccText, opt.Units, out e1, out e2, out err))
                    {
                        attr.StartEccentricity = Point(e1);
                        attr.EndEccentricity = Point(e2);
                    }
                    else
                    {
                        notes.Add(string.Format("{0}: Ax_Eccentricity - {1}. Ignored.", context, err));
                        stats.AttributeErrors++;
                    }
                }
            }

            if (!TrySetLineAttr(job.LineId, attr))
            {
                bool fallbackOk = false;
                if (isBeamOrRib)
                {
                    try
                    {
                        IAxisVMLine lineItem = model.Lines.Item[job.LineId];
                        RPoint3d startExc = attr.StartEccentricity;
                        RPoint3d endExc = attr.EndEccentricity;
                        if (lineType == ELineType.ltBeam)
                        {
                            try
                            {
                                lineItem.DefineAsBeam(attr.MaterialIndex, attr.StartCrossSectionIndex, attr.EndCrossSectionIndex, ref startExc, ref endExc);
                                fallbackOk = true;
                            }
                            catch
                            {
                                // Fallback: If DefineAsBeam is rejected (e.g. domain overlap), define as Rib
                                try
                                {
                                    lineItem.DefineAsRib(attr.MaterialIndex, attr.StartCrossSectionIndex, attr.EndCrossSectionIndex, ref startExc, ref endExc);
                                    fallbackOk = true;
                                    notes.Add(string.Format("{0}: AxisVM rejected Beam definition - automatically defined as Rib instead.", context));
                                }
                                catch { }
                            }
                        }
                        else if (lineType == ELineType.ltTruss)
                        {
                            lineItem.DefineAsTruss(attr.MaterialIndex, attr.StartCrossSectionIndex, ELineNonLinearity.lnlTensionAndCompression, 0.0);
                            fallbackOk = true;
                        }
                        else if (lineType == ELineType.ltRib)
                        {
                            lineItem.DefineAsRib(attr.MaterialIndex, attr.StartCrossSectionIndex, attr.EndCrossSectionIndex, ref startExc, ref endExc);
                            fallbackOk = true;
                        }
                    }
                    catch { }
                }

                if (!fallbackOk)
                {
                    notes.Add(string.Format("{0}: AxisVM rejected its attributes.", context));
                    return;
                }
            }

            if (isBeamOrRib)
            {
                ApplyLocalAxis(job.LineId, src, context);
                ApplyReleases(job, src, context);
                try { model.Members.Add(Ids(job.LineId)); } catch { }
            }
            else if (lineType == ELineType.ltSpring)
            {
                ApplySpring(job.LineId, src, context);
            }
        }

        private void ApplyLocalAxis(int lineId, RhinoLine src, string context)
        {
            string text = src.Attr(AxKeys.LocalAxis);
            if (string.IsNullOrEmpty(text)) return; // Auto by default (Reference = 0)

            string norm = text.Trim().ToLowerInvariant();
            if (norm == "auto" || norm == "automatic" || norm == "none" || norm == "default" || norm == "-")
            {
                // Explicitly asked for AxisVM automatic orientation
                return;
            }

            RhinoXyz dir;
            string err;
            if (!AttributeParser.TryParseDirection(text, out dir, out err))
            {
                notes.Add(string.Format("{0}: Ax_LocalAxis - {1}. Ignored.", context, err));
                stats.AttributeErrors++;
                return;
            }

            int refId = catalog.ReferenceVector(dir);
            if (refId <= 0) return;

            try { model.Lines.Item[lineId].Reference = refId; }
            catch (Exception ex) { notes.Add(string.Format("{0}: its reference vector could not be set ({1}).", context, ex.Message)); }
        }

        private void ApplyReleases(LineJob job, RhinoLine src, string context)
        {
            string text = src.Attr(AxKeys.Release);
            if (text == null) return;

            ReleaseSpec spec;
            string err;
            if (!AttributeParser.TryParseRelease(text, opt.Units, out spec, out err))
            {
                notes.Add(string.Format("{0}: Ax_Release - {1}. Ignored.", context, err));
                stats.AttributeErrors++;
                return;
            }

            // In the RLineAttr_V161 record the two release sets are stored against the lower / higher
            // node index (not the local x direction), so map each parsed set to the right physical end.
            int startNode = NodeForCached(src.Start);
            int lo = Math.Min(job.NodeA, job.NodeB);
            bool startIsLo = startNode == lo;
            ReleaseSet atLo = startIsLo ? spec.Start : spec.End;
            ReleaseSet atHi = startIsLo ? spec.End : spec.Start;

            string relErr;
            if (!WriteReleasesV161(job.LineId, atLo, atHi, out relErr))
                notes.Add(string.Format("{0}: its releases could not be set ({1}).", context, relErr));
        }

        /// <summary>
        /// Writes end releases through the versioned line-attribute record. The warping (ew) slot is
        /// forced to a definite rtRigid/no-function state and Beam7DOF to false, so AxisVM shows the
        /// e_w row disabled and no capacity (s_x / F_xR) values. A null side keeps the model's value.
        /// </summary>
        private bool WriteReleasesV161(int lineId, ReleaseSet atLo, ReleaseSet atHi, out string error)
        {
            error = null;
            if (atLo == null && atHi == null) return true;

            RLineAttr_V161 attr;
            if (!TryGetLineAttrV161(lineId, out attr)) { error = "BulkGetAttr_V161 failed"; return false; }

            if (atLo != null) attr.StartRelease = ToReleasesV161(atLo);
            if (atHi != null) attr.EndRelease = ToReleasesV161(atHi);
            attr.Beam7DOF = ELongBoolean.lbFalse;

            if (!TrySetLineAttrV161(lineId, attr)) { error = "BulkSetAttr_V161 rejected the releases"; return false; }
            return true;
        }

        private void ApplySpring(int lineId, RhinoLine src, string context)
        {
            string text = src.Attr(AxKeys.Stiffness);
            if (text == null)
            {
                notes.Add(string.Format("{0}: a Spring needs Ax_Stiffness. It was left as a plain line.", context));
                stats.AttributeErrors++;
                return;
            }

            double[] k;
            string err;
            if (!AttributeParser.TryParseStiffness(text, opt.Units, out k, out err))
            {
                notes.Add(string.Format("{0}: Ax_Stiffness - {1}. Ignored.", context, err));
                stats.AttributeErrors++;
                return;
            }

            RLineAttr_V161 attr;
            if (TryGetLineAttrV161(lineId, out attr))
            {
                attr.LineType = ELineType.ltSpring;
                attr.SpringType = ESpringDirection.sdGlobal;   // field name is SpringType, type ESpringDirection
                RSpringCharacteristics sc = new RSpringCharacteristics();
                sc.x = springs.Translational(k[0]);
                sc.y = springs.Translational(k[1]);
                sc.z = springs.Translational(k[2]);
                sc.xx = springs.Rotational(k[3]);
                sc.yy = springs.Rotational(k[4]);
                sc.zz = springs.Rotational(k[5]);
                attr.SpringCharacteristics = sc;
                if (TrySetLineAttrV161(lineId, attr)) return;
            }

            // Fallback: the obsolete definition still yields a working (if resistance-tagged) spring.
            try
            {
                RStiffnesses s = ToStiffnesses(k);
                model.Lines.Item[lineId].DefineAsSpring(ESpringDirection.sdGlobal, ref s);
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: it could not be defined as a spring ({1}).", context, ex.Message));
            }
        }

        // ==================================================================
        // Pass 4 - domains and load panels
        // ==================================================================

        private void BuildDomains()
        {
            foreach (RhinoDomain d in rhino.Domains)
            {
                string context = d.Label("domain");

                string feType = (d.Attr(AxKeys.FeType) ?? "shell").Trim().ToLowerInvariant();
                bool isOneWay = feType == "onewayloadpanel" || feType == "onewaypanel" || feType == "loadpaneloneway" || feType == "oneway" ||
                                ((feType == "loadpanel" || feType == "load panel" || feType == "panel") && (d.Attr(AxKeys.SpanDirection) != null || (d.Attr("Ax_Distribution") != null && d.Attr("Ax_Distribution").ToLowerInvariant().Contains("oneway"))));
                bool isTwoWayPanel = feType == "loadpanel" || feType == "load panel" || feType == "panel";

                List<int> contourLines;
                if (!TryContourLineIds(d, out contourLines))
                {
                    notes.Add(string.Format("{0}: its contour could not be resolved to AxisVM lines. Skipped.", context));
                    continue;
                }

                if (opt.DryRun)
                {
                    if (isOneWay || isTwoWayPanel) stats.PanelsCreated++; else stats.DomainsCreated++;
                    continue;
                }

                if (isOneWay) CreateOneWayLoadPanel(d, contourLines, context);
                else if (isTwoWayPanel) CreateLoadPanel(d, contourLines, context);
                else CreateDomain(d, contourLines, context, feType);
            }
        }

        private bool TryContourLineIds(RhinoDomain d, out List<int> lineIds)
        {
            lineIds = new List<int>();

            for (int i = 0; i < d.Contour.Count; i++)
            {
                int a = NodeForCached(d.Contour[i]);
                int b = NodeForCached(d.Contour[(i + 1) % d.Contour.Count]);
                if (a == 0 || b == 0 || a == b) continue;

                LineJob job;
                if (!lineByPair.TryGetValue(PairKey(a, b), out job)) return false;
                lineIds.Add(job.LineId);
            }

            return lineIds.Count >= 3;
        }

        private void CreateDomain(RhinoDomain d, List<int> contourLines, string context, string feType)
        {
            RSurfaceAttr attr = new RSurfaceAttr();

            switch (feType)
            {
                case "plate": attr.SurfaceType = ESurfaceType.stPlate; break;
                case "membrane": attr.SurfaceType = ESurfaceType.stMembraneStress; break;
                case "shell": attr.SurfaceType = ESurfaceType.stShell; break;
                default:
                    notes.Add(string.Format("{0}: Ax_FEType '{1}' is not one this addon knows - Shell used instead.", context, feType));
                    stats.AttributeErrors++;
                    attr.SurfaceType = ESurfaceType.stShell;
                    break;
            }

            attr.MaterialId = catalog.Material(d.Attr(AxKeys.Material), context);

            string thicknessText = d.Attr(AxKeys.Thickness);
            double thickness = DefaultThicknessM;
            if (thicknessText != null)
            {
                double raw;
                if (double.TryParse(thicknessText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out raw))
                {
                    thickness = opt.Units.Length(raw);
                }
                else
                {
                    notes.Add(string.Format("{0}: Ax_Thickness '{1}' is not a number - {2} m used instead.",
                        context, thicknessText, DefaultThicknessM));
                    stats.AttributeErrors++;
                }
            }
            else
            {
                notes.Add(string.Format("{0}: no Ax_Thickness given - {1} m used.", context, DefaultThicknessM));
            }
            attr.Thickness = thickness;

            string axisText = d.Attr(AxKeys.LocalAxis);
            if (!string.IsNullOrEmpty(axisText))
            {
                string norm = axisText.Trim().ToLowerInvariant();
                if (norm != "auto" && norm != "automatic" && norm != "none" && norm != "default" && norm != "-")
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
                        notes.Add(string.Format("{0}: Ax_LocalAxis - {1}. Ignored.", context, err));
                        stats.AttributeErrors++;
                    }
                }
            }

            Array ids = Ids(contourLines.ToArray());
            int domainId;
            try { domainId = model.Domains.Add(ids, ref attr); }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the domain ({1}). Its contour may not be planar or closed.",
                    context, ex.Message));
                return;
            }

            if (domainId <= 0)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the domain (code {1}). Its contour may not be planar or closed.",
                    context, domainId));
                return;
            }

            stats.DomainsCreated++;
            domainByGuid[d.Guid] = domainId;
            idMap.Add(string.Format("domain\t{0}\t{1}", d.Guid, domainId));

            ApplyDomainEccentricity(domainId, d, context);
            CollectParts(d, EPartItemType.pitDomain, domainId);
            StampDomain(domainId, d.Guid);
        }

        private void ApplyDomainEccentricity(int domainId, RhinoDomain d, string context)
        {
            string text = d.Attr(AxKeys.Eccentricity);
            if (text == null) return;

            RhinoXyz v;
            string err;
            if (!AttributeParser.TryParseVector(text, opt.Units, out v, out err))
            {
                notes.Add(string.Format("{0}: Ax_Eccentricity - {1}. Ignored.", context, err));
                stats.AttributeErrors++;
                return;
            }

            try
            {
                RDomainExcentricity ecc = new RDomainExcentricity();
                ecc.ExcentricityType = EDomainExcentricityType.detConstant;

                // A constant domain eccentricity is one offset from the mid-plane, measured along the
                // domain normal - so the vector collapses to a single magnitude here. Its z is the
                // natural reading for the usual case of a horizontal slab.
                double offset = Math.Abs(v.Z) > 1e-12 ? v.Z : (Math.Abs(v.Y) > 1e-12 ? v.Y : v.X);
                ecc.exc1 = offset;
                ecc.exc2 = offset;
                ecc.exc3 = offset;

                model.Domains.SetExcentricity(domainId, ref ecc);
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: its eccentricity could not be set ({1}).", context, ex.Message));
            }
        }

        private void CreateLoadPanel(RhinoDomain d, List<int> contourLines, string context)
        {
            Array ids = Ids(contourLines.ToArray());
            int panelId;
            try { panelId = model.LoadPanels.AddFromLineIDs(ref ids); }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the load panel ({1}).", context, ex.Message));
                return;
            }

            if (panelId <= 0)
            {
                notes.Add(string.Format("{0}: AxisVM rejected the load panel (code {1}).", context, panelId));
                return;
            }

            try
            {
                var lp = model.LoadPanels.Item[panelId];
                if (lp != null)
                {
                    lp.Auto = (ELongBoolean)1;
                    lp.SetLines(ref ids);
                }
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: load panel line linkage could not be configured ({1}).", context, ex.Message));
            }

            stats.PanelsCreated++;
            panelByGuid[d.Guid] = panelId;
            panelsByGuid[d.Guid] = new List<int> { panelId };
            idMap.Add(string.Format("loadpanel\t{0}\t{1}", d.Guid, panelId));

            if (d.Attr(AxKeys.Material) != null || d.Attr(AxKeys.Thickness) != null)
                notes.Add(string.Format("{0}: it is a load panel, so its Ax_Material / Ax_Thickness were ignored.", context));
        }

        private void CreateOneWayLoadPanel(RhinoDomain d, List<int> contourLines, string context)
        {
            List<ResolvedLine> segments = new List<ResolvedLine>(contourLines.Count);
            foreach (int lid in contourLines)
            {
                var line = model.Lines.Item[lid];
                segments.Add(new ResolvedLine { LineId = lid, NodeId1 = line.StartNode, NodeId2 = line.EndNode });
            }

            OneWayPanelOptions panelOpt = new OneWayPanelOptions();
            panelOpt.AngleToleranceDeg = 5.0;
            panelOpt.NoSpacing = true;

            string spanDir = d.Attr(AxKeys.SpanDirection) ?? d.Attr("Ax_SpanDir") ?? "0";
            if (spanDir.Equals("1", StringComparison.OrdinalIgnoreCase) || spanDir.Equals("dir1", StringComparison.OrdinalIgnoreCase) || spanDir.Equals("x", StringComparison.OrdinalIgnoreCase) || spanDir.Equals("long", StringComparison.OrdinalIgnoreCase))
            {
                panelOpt.SpanningDirection = 1;
            }
            else if (spanDir.Equals("2", StringComparison.OrdinalIgnoreCase) || spanDir.Equals("dir2", StringComparison.OrdinalIgnoreCase) || spanDir.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                panelOpt.SpanningDirection = 2;
            }
            else
            {
                panelOpt.SpanningDirection = 0; // Auto (Short span)
            }

            string spacingText = d.Attr(AxKeys.EdgeSpacing);
            if (!string.IsNullOrEmpty(spacingText))
            {
                double sp;
                if (double.TryParse(spacingText, NumberStyles.Float, CultureInfo.InvariantCulture, out sp) && sp > 0)
                {
                    panelOpt.NoSpacing = false;
                    panelOpt.EdgeSpacingMm = opt.Units.LengthName == "m" ? sp * 1000.0 : sp;
                }
            }

            List<string> builderNotes = new List<string>();
            List<int> panelUids = OneWayPanelBuilder.GeneratePanels(model, segments, panelOpt, 1, builderNotes);

            if (panelUids != null && panelUids.Count > 0)
            {
                List<int> pids = new List<int>();
                foreach (int uid in panelUids)
                {
                    int p = model.LoadPanels.get_IndexOfUID(uid);
                    if (p > 0) pids.Add(p);
                }

                int primaryPanelId = pids.Count > 0 ? pids[0] : model.LoadPanels.get_IndexOfUID(panelUids[0]);
                stats.PanelsCreated += panelUids.Count;
                panelByGuid[d.Guid] = primaryPanelId;
                panelsByGuid[d.Guid] = pids;
                idMap.Add(string.Format("onewayloadpanel\t{0}\t{1}", d.Guid, primaryPanelId));
                notes.Add(string.Format("{0}: created one-way load panel (ID {1}, {2} panel(s)).", context, primaryPanelId, panelUids.Count));
            }
            else
            {
                notes.Add(string.Format("{0}: one-way panel classification fell back to standard panel.", context));
                CreateLoadPanel(d, contourLines, context);
            }
        }

        // ==================================================================
        // Pass 5 - supports
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
                if (!AttributeParser.TryParseSupport(text, opt.Units, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_Support - {1}. Ignored.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.SupportsCreated++; continue; }

                int nodeId = NodeForCached(p.Xyz);
                if (nodeId <= 0)
                {
                    notes.Add(string.Format("{0}: its node is missing, so no support was added.", context));
                    continue;
                }

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
                        // Fall back to the obsolete call so a support still exists.
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
        }

        // ==================================================================
        // Pass 6 - links and edge hinges
        // ==================================================================

        private void BuildLinksAndHinges()
        {
            foreach (RhinoLine src in rhino.Lines)
            {
                LineJob job;
                if (!lineByRhinoGuid.TryGetValue(src.Guid, out job)) continue;

                string type = (src.Attr(AxKeys.Type) ?? "").Trim().ToLowerInvariant();
                string context = src.Label("line");

                if (type == "rigidlink" || type == "rigid link" || type == "nodelink" ||
                    type == "nodenodelink" || type == "node link")
                    CreateLink(job, src, context, type);

                if (src.Attr(AxKeys.HingeDomains) != null)
                    CreateEdgeHinges(job, src, context);
            }
        }

        private void CreateLink(LineJob job, RhinoLine src, string context, string type)
        {
            double[] k;
            string coupled = src.Attr(AxKeys.CoupledDof);

            if (coupled != null)
            {
                string err;
                if (!AttributeParser.TryParseCoupledDofs(coupled, out k, out err))
                {
                    notes.Add(string.Format("{0}: Ax_CoupledDOF - {1}. All six were coupled instead.", context, err));
                    stats.AttributeErrors++;
                    k = AllCoupled();
                }
            }
            else if (type == "rigidlink" || type == "rigid link")
            {
                k = AllCoupled();
            }
            else
            {
                notes.Add(string.Format("{0}: a NodeLink needs Ax_CoupledDOF. All six were coupled instead.", context));
                k = AllCoupled();
            }

            if (opt.DryRun) { stats.LinksCreated++; return; }

            try
            {
                RNNLinkElementRec rec = new RNNLinkElementRec();
                rec.LineId = job.LineId;
                rec.SystemGLR = ESystem.sysGlobal;
                rec.Stiffnesses = ToStiffnesses(k);
                rec.Resistances = new RResistances();
                rec.NonLinearity = new RNonLinearity();

                int id = model.LinkElements.AddNN(ref rec);
                if (id > 0) stats.LinksCreated++;
                else notes.Add(string.Format("{0}: AxisVM rejected the link (code {1}).", context, id));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: its link could not be created ({1}).", context, ex.Message));
            }
        }

        private static double[] AllCoupled()
        {
            double[] k = new double[6];
            for (int i = 0; i < 6; i++) k[i] = AttributeParser.LinkRigidStiffness;
            return k;
        }

        /// <summary>
        /// One edge connection per named domain. A line naming three domains yields three separate
        /// hinges, which is what the attribute means - and because the line itself was already merged
        /// with whatever member was drawn on top of it, the hinge lands on the real member.
        /// </summary>
        private void CreateEdgeHinges(LineJob job, RhinoLine src, string context)
        {
            string stiffText = src.Attr(AxKeys.Stiffness);
            if (stiffText == null)
            {
                notes.Add(string.Format("{0}: an EdgeHinge needs Ax_Stiffness. No hinge was created.", context));
                stats.AttributeErrors++;
                return;
            }

            double[] k;
            string err;
            if (!AttributeParser.TryParseStiffness(stiffText, opt.Units, out k, out err))
            {
                notes.Add(string.Format("{0}: Ax_Stiffness - {1}. No hinge was created.", context, err));
                stats.AttributeErrors++;
                return;
            }

            List<string> targets = AttributeParser.SplitList(src.Attr(AxKeys.HingeDomains));
            if (targets.Count == 0)
            {
                notes.Add(string.Format("{0}: Ax_HingeDomains named no domains. No hinge was created.", context));
                stats.AttributeErrors++;
                return;
            }

            foreach (string guid in targets)
            {
                int domainId;
                if (!domainByGuid.TryGetValue(guid, out domainId))
                {
                    if (panelByGuid.ContainsKey(guid))
                        notes.Add(string.Format("{0}: Ax_HingeDomains names {1}, which imported as a load panel. A load panel has no edge hinges, so it was skipped.",
                            context, Short(guid)));
                    else
                        notes.Add(string.Format("{0}: Ax_HingeDomains names {1}, which is not a domain in this file. Skipped.",
                            context, Short(guid)));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.HingesCreated++; continue; }

                try
                {
                    REdgeConnectionRec rec = new REdgeConnectionRec();
                    rec.LineId = job.LineId;
                    rec.DomainId = domainId;
                    rec.Stiffnesses = ToStiffnesses(k);
                    rec.Resistances = new RResistances();

                    int id = model.EdgeConnections.Add(ref rec);
                    if (id > 0) stats.HingesCreated++;
                    else notes.Add(string.Format("{0}: AxisVM rejected the edge hinge onto domain {1} (code {2}).", context, domainId, id));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: its edge hinge onto domain {1} could not be created ({2}).", context, domainId, ex.Message));
                }
            }
        }

        // ==================================================================
        // Pass 7 - loads
        // ==================================================================

        private void BuildLoads()
        {
            foreach (RhinoPoint p in rhino.Points) NodalLoads(p);
            foreach (RhinoLine l in rhino.Lines) LineLoads(l);
            foreach (RhinoDomain d in rhino.Domains) DomainLoads(d);
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
                if (lc <= 0) continue;

                int nodeId = NodeForCached(p.Xyz);
                if (nodeId <= 0) continue;

                try
                {
                    RLoadNodalForce rec = new RLoadNodalForce();
                    rec.LoadCaseId = lc;
                    rec.NodeId = nodeId;
                    rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                    rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];

                    if (model.Loads.AddNodalForce(ref rec) > 0) stats.LoadsCreated++;
                    else notes.Add(string.Format("{0}: AxisVM rejected a nodal load.", context));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a nodal load could not be created ({1}).", context, ex.Message));
                }
            }
        }

        private void LineLoads(RhinoLine src)
        {
            List<string> pointSpecs = src.AttrList(AxKeys.PointLoad);
            List<string> distSpecs = src.AttrList(AxKeys.DistLoad);
            if (pointSpecs.Count == 0 && distSpecs.Count == 0) return;

            string context = src.Label("line");

            LineJob job;
            if (!lineByRhinoGuid.TryGetValue(src.Guid, out job))
            {
                notes.Add(string.Format("{0}: it carries loads but never became an AxisVM line. They were skipped.", context));
                return;
            }

            bool isRib = job.TypeName == "rib";

            foreach (string text in pointSpecs)
            {
                PointLoadSpec load;
                string err;
                if (!AttributeParser.TryParseBeamPointLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_PointLoad - {1}. Skipped.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                double posVal = ToAxisVMPosition(job.LineId, load.PositionIsRatio, load.Position, context);
                if (double.IsNaN(posVal)) continue;

                try
                {
                    if (isRib)
                    {
                        RLoadRibConcentrated rec = new RLoadRibConcentrated();
                        rec.LoadCaseId = lc; rec.LineId = job.LineId; rec.SystemGLR = ESystem.sysGlobal;
                        rec.Fgx = load.F[0]; rec.Fgy = load.F[1]; rec.Fgz = load.F[2];
                        rec.Mgx = load.F[3]; rec.Mgy = load.F[4]; rec.Mgz = load.F[5];
                        rec.Position = posVal;
                        if (model.Loads.AddRibConcentrated(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(string.Format("{0}: AxisVM rejected a point load.", context));
                    }
                    else
                    {
                        RLoadBeamConcentrated rec = new RLoadBeamConcentrated();
                        rec.LoadCaseId = lc; rec.LineId = job.LineId; rec.SystemGLR = ESystem.sysGlobal;
                        rec.Fgx = load.F[0]; rec.Fgy = load.F[1]; rec.Fgz = load.F[2];
                        rec.Mgx = load.F[3]; rec.Mgy = load.F[4]; rec.Mgz = load.F[5];
                        rec.Position = posVal;
                        if (model.Loads.AddBeamConcentrated(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(string.Format("{0}: AxisVM rejected a point load.", context));
                    }
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a point load could not be created ({1}).", context, ex.Message));
                }
            }

            foreach (string text in distSpecs)
            {
                DistLoadSpec load;
                string err;
                if (!AttributeParser.TryParseBeamDistLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_DistLoad - {1}. Skipped.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                double fromVal = ToAxisVMPosition(job.LineId, load.PositionIsRatio, load.From, context);
                double toVal = ToAxisVMPosition(job.LineId, load.PositionIsRatio, load.To, context);
                if (double.IsNaN(fromVal) || double.IsNaN(toVal)) continue;

                try
                {
                    if (isRib)
                    {
                        RLoadRibDistributed rec = new RLoadRibDistributed();
                        rec.LoadCaseId = lc; rec.LineId = job.LineId; rec.SystemGLR = ESystem.sysGlobal;
                        rec.qx1 = load.Q1[0]; rec.qy1 = load.Q1[1]; rec.qz1 = load.Q1[2];
                        rec.mx1 = load.Q1[3]; rec.my1 = load.Q1[4]; rec.mz1 = load.Q1[5];
                        rec.qx2 = load.Q2[0]; rec.qy2 = load.Q2[1]; rec.qz2 = load.Q2[2];
                        rec.mx2 = load.Q2[3]; rec.my2 = load.Q2[4]; rec.mz2 = load.Q2[5];
                        rec.Position1 = fromVal; rec.Position2 = toVal;
                        rec.DistributionType = EBeamRibDistributionType.brdtLength;
                        rec.Trapezoid = load.IsTrapezoid ? ELongBoolean.lbTrue : ELongBoolean.lbFalse;
                        if (model.Loads.AddRibDistributed(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(string.Format("{0}: AxisVM rejected a distributed load.", context));
                    }
                    else
                    {
                        RLoadBeamDistributed rec = new RLoadBeamDistributed();
                        rec.LoadCaseId = lc; rec.LineId = job.LineId; rec.SystemGLR = ESystem.sysGlobal;
                        rec.qx1 = load.Q1[0]; rec.qy1 = load.Q1[1]; rec.qz1 = load.Q1[2];
                        rec.mx1 = load.Q1[3]; rec.my1 = load.Q1[4]; rec.mz1 = load.Q1[5];
                        rec.qx2 = load.Q2[0]; rec.qy2 = load.Q2[1]; rec.qz2 = load.Q2[2];
                        rec.mx2 = load.Q2[3]; rec.my2 = load.Q2[4]; rec.mz2 = load.Q2[5];
                        rec.Position1 = fromVal; rec.Position2 = toVal;
                        rec.DistributionType = EBeamRibDistributionType.brdtLength;
                        rec.Trapezoid = load.IsTrapezoid ? ELongBoolean.lbTrue : ELongBoolean.lbFalse;
                        if (model.Loads.AddBeamDistributed(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(string.Format("{0}: AxisVM rejected a distributed load.", context));
                    }
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a distributed load could not be created ({1}).", context, ex.Message));
                }
            }
        }

        /// <summary>
        /// In AxisVM COM API:
        /// - Position >= 0 is interpreted as absolute distance in metres from the start.
        /// - Position &lt; 0 is interpreted as relative ratio |Position| (0..1) along the member length.
        /// </summary>
        private double ToAxisVMPosition(int lineId, bool isRatio, double value, string context)
        {
            if (isRatio)
            {
                if (value < -1e-9 || value > 1 + 1e-9)
                {
                    notes.Add(string.Format("{0}: a load position ratio of {1} is outside 0..1. Skipped.", context, value));
                    stats.AttributeErrors++;
                    return double.NaN;
                }
                double r = Math.Min(1.0, Math.Max(0.0, value));
                // AxisVM COM relative ratio is encoded as negative number:
                return r <= 1e-7 ? -1e-6 : -r;
            }

            // Absolute distance in metres:
            if (value < -1e-9)
            {
                notes.Add(string.Format("{0}: a load position distance of {1:0.###} m is negative. Skipped.", context, value));
                stats.AttributeErrors++;
                return double.NaN;
            }
            return Math.Max(0.0, value);
        }

        private void DomainLoads(RhinoDomain d)
        {
            List<string> pointSpecs = d.AttrList(AxKeys.PointLoad);
            List<string> distSpecs = d.AttrList(AxKeys.DistLoad);
            if (pointSpecs.Count == 0 && distSpecs.Count == 0) return;

            string context = d.Label("domain");

            int domainId = 0;
            bool isDomain = domainByGuid.TryGetValue(d.Guid, out domainId);
            List<int> panelIds = null;
            bool isPanel = !isDomain && ((panelsByGuid.TryGetValue(d.Guid, out panelIds) && panelIds.Count > 0) ||
                                         (panelByGuid.TryGetValue(d.Guid, out int singleP) && (panelIds = new List<int> { singleP }) != null));

            if (!opt.DryRun && !isDomain && !isPanel)
            {
                notes.Add(string.Format("{0}: it carries loads but never became a domain or load panel. They were skipped.", context));
                return;
            }

            foreach (string text in pointSpecs)
            {
                PointLoadSpec load;
                string err;
                if (!AttributeParser.TryParseDomainPointLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_PointLoad - {1}. Skipped.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                try
                {
                    if (isDomain)
                    {
                        RLoadDomainConcentrated rec = new RLoadDomainConcentrated();
                        rec.LoadCaseId = lc; rec.DomainId = domainId; rec.SystemGLR = ESystem.sysGlobal;
                        rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                        rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];
                        rec.x = load.At.X; rec.y = load.At.Y; rec.z = load.At.Z;
                        if (model.Loads.AddDomainConcentrated(ref rec) > 0) stats.LoadsCreated++;
                        else notes.Add(string.Format("{0}: AxisVM rejected a point load.", context));
                    }
                    else if (panelIds != null)
                    {
                        foreach (int pid in panelIds)
                        {
                            RLoadPanelConcentrated rec = new RLoadPanelConcentrated();
                            rec.LoadCaseId = lc; rec.LoadPanelId = pid; rec.SystemGLR = ESystem.sysGlobal;
                            rec.Fx = load.F[0]; rec.Fy = load.F[1]; rec.Fz = load.F[2];
                            rec.Mx = load.F[3]; rec.My = load.F[4]; rec.Mz = load.F[5];
                            rec.x = load.At.X; rec.y = load.At.Y; rec.z = load.At.Z;
                            if (model.Loads.AddLoadPanelConcentrated(ref rec) > 0) stats.LoadsCreated++;
                            else notes.Add(string.Format("{0}: AxisVM rejected a point load.", context));
                        }
                    }
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("{0}: a point load could not be created ({1}).", context, ex.Message));
                }
            }

            foreach (string text in distSpecs)
            {
                DistLoadSpec load;
                string err;
                if (!AttributeParser.TryParseDomainDistLoad(text, opt.Units, out load, out err))
                {
                    notes.Add(string.Format("{0}: Ax_DistLoad - {1}. Skipped.", context, err));
                    stats.AttributeErrors++;
                    continue;
                }

                if (opt.DryRun) { stats.LoadsCreated++; continue; }

                int lc = catalog.LoadCase(load.LoadCase);
                if (lc <= 0) continue;

                if (isDomain) AddDomainUniformLoad(domainId, lc, load, context);
                else if (panelIds != null)
                {
                    foreach (int pid in panelIds)
                    {
                        AddPanelUniformLoad(pid, lc, load, context, d);
                    }
                }
            }
        }

        private void AddDomainUniformLoad(int domainId, int loadCaseId, DistLoadSpec load, string context)
        {
            try
            {
                RLoadDomainConstant rec = new RLoadDomainConstant();
                rec.LoadCaseId = loadCaseId;
                rec.DomainId = domainId;
                rec.SystemGLR = ESystem.sysGlobal;
                rec.DistributionType = ESurfaceDomainDistributionType.sddtSurface;
                rec.qx = load.Q1[0];
                rec.qy = load.Q1[1];
                rec.qz = load.Q1[2];

                if (model.Loads.AddDomainConstant(ref rec) > 0) stats.LoadsCreated++;
                else notes.Add(string.Format("{0}: AxisVM rejected a uniform domain load.", context));
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("{0}: a uniform domain load could not be created ({1}).", context, ex.Message));
            }
        }

        /// <summary>
        /// A load panel takes an area load via AddLoadPanelLinear bound by LoadPanelId, with
        /// fallback to AddLoadPanelPolyArea using the 3D boundary polygon.
        /// </summary>
        private void AddPanelUniformLoad(int panelId, int loadCaseId, DistLoadSpec load, string context, RhinoDomain d)
        {
            EAxis[] axes = { EAxis.aX, EAxis.aY, EAxis.aZ };

            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(load.Q1[i]) < 1e-12) continue;

                int loadId = 0;
                string errDetail = "";

                // 1. Primary path: AddLoadPanelLinear directly attached to panelId
                try
                {
                    RLoadPanelLinear recLinear = new RLoadPanelLinear();
                    recLinear.LoadCaseId = loadCaseId;
                    recLinear.LoadPanelId = panelId;
                    recLinear.DistributionType = EDistributionType.dtGlobal;
                    recLinear.LoadDistributionType = ELoadDistributionType.ldtConst;
                    recLinear.Component = axes[i];
                    recLinear.P1 = load.Q1[i];
                    recLinear.P2 = load.Q1[i];
                    recLinear.P3 = load.Q1[i];

                    loadId = model.Loads.AddLoadPanelLinear(ref recLinear);
                    if (loadId <= 0) errDetail = string.Format("AddLoadPanelLinear code {0}", loadId);
                }
                catch (Exception ex)
                {
                    errDetail = "AddLoadPanelLinear threw: " + ex.Message;
                }

                // 2. Secondary path: AddLoadPanelPolyArea using the 3D contour polygon if linear failed
                if (loadId <= 0)
                {
                    AxisVMLines3d contour = null;
                    try { model.LoadPanels.Item[panelId].GetContourPolygon(out contour); } catch { }

                    if (contour == null && d != null && d.Contour != null && d.Contour.Count >= 3 && app != null && app.ObjectCreator != null)
                    {
                        try
                        {
                            IAxisVMLines3d l3d = (IAxisVMLines3d)app.ObjectCreator.NewLines3d();
                            if (l3d != null)
                            {
                                int n = d.Contour.Count;
                                Array coords = Array.CreateInstance(typeof(RPoint3d), new int[] { n + 1 }, new int[] { 1 });
                                for (int j = 0; j < n; j++)
                                {
                                    RPoint3d pt = new RPoint3d();
                                    pt.x = opt.Units.Length(d.Contour[j].X);
                                    pt.y = opt.Units.Length(d.Contour[j].Y);
                                    pt.z = opt.Units.Length(d.Contour[j].Z);
                                    coords.SetValue(pt, j + 1);
                                }
                                RPoint3d ptClose = new RPoint3d();
                                ptClose.x = opt.Units.Length(d.Contour[0].X);
                                ptClose.y = opt.Units.Length(d.Contour[0].Y);
                                ptClose.z = opt.Units.Length(d.Contour[0].Z);
                                coords.SetValue(ptClose, n + 1);

                                l3d.StraightLines(coords);
                                contour = (AxisVMLines3d)l3d;
                            }
                        }
                        catch { }
                    }

                    if (contour != null)
                    {
                        try
                        {
                            RLoadPanelPolyArea recPoly = new RLoadPanelPolyArea();
                            recPoly.LoadCaseId = loadCaseId;
                            recPoly.DistributionType = EDistributionType.dtGlobal;
                            recPoly.LoadDistributionType = ELoadDistributionType.ldtConst;
                            recPoly.Component = axes[i];
                            recPoly.P1 = load.Q1[i];
                            recPoly.P2 = load.Q1[i];
                            recPoly.P3 = load.Q1[i];
                            recPoly.WindowLoad = ELongBoolean.lbFalse;

                            loadId = model.Loads.AddLoadPanelPolyArea(contour, ref recPoly);
                        }
                        catch { }
                    }
                }

                if (loadId > 0)
                {
                    stats.LoadsCreated++;
                    notes.Add(string.Format("{0}: applied {1:0.##} kN/m2 ({2}) on panel {3} (Load ID {4}).",
                        context, load.Q1[i], axes[i], panelId, loadId));
                }
                else
                {
                    notes.Add(string.Format("{0}: AxisVM rejected load on panel {1} ({2}).", context, panelId, errDetail));
                }
            }
        }

        // ==================================================================
        // Pass 8 - parts
        // ==================================================================

        private void BuildParts()
        {
            foreach (RhinoPoint p in rhino.Points)
                CollectParts(p, EPartItemType.pitNode, NodeForCached(p.Xyz));

            foreach (RhinoLine l in rhino.Lines)
            {
                LineJob job;
                if (lineByRhinoGuid.TryGetValue(l.Guid, out job))
                    CollectParts(l, EPartItemType.pitLine, job.LineId);
            }

            // Domains added themselves as they were created, since only then was the id known.

            if (opt.DryRun) { stats.PartsCreated = partItems.Count; return; }

            IAxisVMCustomPartFolder root;
            try { root = model.CustomParts.RootFolder; }
            catch (Exception ex)
            {
                notes.Add("Parts could not be created: the custom parts folder is unavailable (" + ex.Message + ").");
                return;
            }

            foreach (KeyValuePair<string, List<RPartItem>> kv in partItems)
            {
                if (kv.Value.Count == 0) continue;

                Array items = Array.CreateInstance(typeof(RPartItem), new[] { kv.Value.Count }, new[] { 1 });
                for (int i = 0; i < kv.Value.Count; i++) items.SetValue(kv.Value[i], i + 1);

                try
                {
                    int id = root.AddPart(kv.Key, ref items);
                    if (id > 0) stats.PartsCreated++;
                    else notes.Add(string.Format("Part '{0}' was rejected by AxisVM (code {1}).", kv.Key, id));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("Part '{0}' could not be created ({1}).", kv.Key, ex.Message));
                }
            }
        }

        private void CollectParts(RhinoEntity entity, EPartItemType type, int id)
        {
            if (id <= 0) return;

            string text = entity.Attr(AxKeys.Parts);
            if (text == null) return;

            foreach (string name in AttributeParser.SplitList(text))
            {
                List<RPartItem> list;
                if (!partItems.TryGetValue(name, out list))
                {
                    list = new List<RPartItem>();
                    partItems[name] = list;
                }
                list.Add(new RPartItem { ItemType = type, Id = id });
            }
        }

        // ==================================================================
        // Pass 8b - auto-intersection and member re-assembly
        // ==================================================================

        private class MemberSnapshot
        {
            public string Guid;
            public double[] P0;
            public double[] P1;
            public ReleaseSet StartSpec;
            public ReleaseSet EndSpec;
            public LineJob Job;
        }

        private class LiveSeg
        {
            public int Id;
            public int NodeA;
            public int NodeB;
            public double[] A;
            public double[] B;
        }

        /// <summary>
        /// Runs AxisVM's own line auto-intersection over every line, then walks each straight structural
        /// Rhino curve's resulting collinear segments back into a single structural member, keeping end
        /// releases only at the two far ends. CrossLines re-indexes the whole model, so every lookup used
        /// after it is rebuilt from the live model. The whole pass is best-effort: on any failure the
        /// model is simply left as the normal build produced it.
        /// </summary>
        private void IntersectAndAssemble()
        {
            try
            {
                double tol = Math.Max(opt.MergeToleranceM, 1e-6);

                // 1. Snapshot before CrossLines invalidates the ids.
                List<MemberSnapshot> snaps = new List<MemberSnapshot>();
                List<int> allLineIds = new List<int>();

                foreach (KeyValuePair<string, LineJob> kv in lineByPair)
                {
                    LineJob job = kv.Value;
                    if (job.LineId > 0) allLineIds.Add(job.LineId);

                    if (job.Structural == null) continue;
                    if (job.TypeName != "beam" && job.TypeName != "rib" && job.TypeName != "truss") continue;

                    double[] p0 = NodeXyz(job.NodeA);
                    double[] p1 = NodeXyz(job.NodeB);
                    if (p0 == null || p1 == null) continue;

                    ReleaseSet startSet = null, endSet = null;
                    string relText = job.Structural.Attr(AxKeys.Release);
                    if (relText != null)
                    {
                        ReleaseSpec spec; string e;
                        if (AttributeParser.TryParseRelease(relText, opt.Units, out spec, out e))
                        {
                            // job.NodeA is the Rhino curve start, job.NodeB its end.
                            startSet = spec.Start;
                            endSet = spec.End;
                        }
                    }

                    snaps.Add(new MemberSnapshot
                    {
                        Guid = job.Structural.Guid,
                        P0 = p0,
                        P1 = p1,
                        StartSpec = startSet,
                        EndSpec = endSet,
                        Job = job
                    });
                }

                if (snaps.Count == 0) return;

                // 2. Intersect every line.
                int linesBefore = SafeLineCount();
                int rc;
                try { rc = model.Lines.CrossLines(Ids(allLineIds.ToArray())); }
                catch (Exception ex)
                {
                    notes.Add("Auto-intersection was skipped: CrossLines failed (" + ex.Message + ").");
                    return;
                }
                int linesAfter = SafeLineCount();

                // 3. Rebuild geometry lookups from the re-indexed model.
                NodeIndex live = new NodeIndex(tol);
                int nodeCount = SafeNodeCount();
                for (int i = 1; i <= nodeCount; i++)
                {
                    double[] c = NodeXyz(i);
                    if (c != null) live.Add(c[0], c[1], c[2], i);
                }

                List<LiveSeg> segs = ReadLiveSegments();

                // 4. Re-assemble each snapshot.
                int assembled = 0;
                foreach (MemberSnapshot s in snaps)
                {
                    List<int> chain = ChainAlong(s.P0, s.P1, segs, tol);
                    if (chain.Count == 0)
                    {
                        notes.Add(string.Format(
                            "Auto-intersect: the segments of line {0} could not be re-identified; it was left as separate elements.",
                            Short(s.Guid)));
                        continue;
                    }

                    try
                    {
                        model.Members.Add(Ids(chain.ToArray()));
                        assembled++;
                    }
                    catch (Exception ex)
                    {
                        notes.Add(string.Format("Auto-intersect: {0} could not be assembled into one member ({1}).",
                            Short(s.Guid), ex.Message));
                    }

                    if (s.StartSpec != null || s.EndSpec != null)
                        ApplyChainReleases(chain, s.P0, s.P1, s.StartSpec, s.EndSpec, live, tol);

                    // Keep StampRhinoIds pointing at something real.
                    s.Job.LineId = chain[0];
                }

                stats.MembersAssembled = assembled;
                if (linesAfter > linesBefore) stats.LinesCreated += linesAfter - linesBefore;

                notes.Add(string.Format(
                    "Auto-intersect: {0} line(s) went in, {1} came out; {2} member(s) reassembled from their split segments.",
                    linesBefore, linesAfter, assembled));
            }
            catch (Exception ex)
            {
                notes.Add("Auto-intersection pass failed and was skipped (" + ex.Message + ").");
            }
        }

        private int SafeLineCount() { try { return model.Lines.Count; } catch { return 0; } }
        private int SafeNodeCount() { try { return model.Nodes.Count; } catch { return 0; } }

        private double[] NodeXyz(int nodeId)
        {
            if (nodeId <= 0) return null;
            try
            {
                RPoint3d p = new RPoint3d();
                if (model.Nodes.GetNodeCoord(nodeId, ref p) <= 0) return null;
                return new[] { p.x, p.y, p.z };
            }
            catch { return null; }
        }

        private List<LiveSeg> ReadLiveSegments()
        {
            List<LiveSeg> list = new List<LiveSeg>();
            int count = SafeLineCount();
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    IAxisVMLine ln = model.Lines.Item[i];
                    int na = ln.StartNode;
                    int nb = ln.EndNode;
                    double[] a = NodeXyz(na);
                    double[] b = NodeXyz(nb);
                    if (a == null || b == null) continue;
                    list.Add(new LiveSeg { Id = i, NodeA = na, NodeB = nb, A = a, B = b });
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// The ordered ids of the live segments that together span P0..P1 along a straight line, or an
        /// empty list when the run cannot be matched contiguously (in which case the caller leaves the
        /// line as separate elements).
        /// </summary>
        private static List<int> ChainAlong(double[] p0, double[] p1, List<LiveSeg> segs, double tol)
        {
            List<int> result = new List<int>();

            double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
            double len = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (len <= tol) return result;
            ux /= len; uy /= len; uz /= len;

            double slack = tol * 4.0;

            var hits = new List<KeyValuePair<double, LiveSeg>>();  // key = low t along the line
            foreach (LiveSeg s in segs)
            {
                double ta, tb;
                if (!OnLine(p0, ux, uy, uz, s.A, tol, out ta)) continue;
                if (!OnLine(p0, ux, uy, uz, s.B, tol, out tb)) continue;

                double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
                if (hi - lo <= tol) continue;                          // degenerate
                if (lo < -slack || hi > len + slack) continue;         // sticks out past the ends

                hits.Add(new KeyValuePair<double, LiveSeg>(lo, s));
            }

            if (hits.Count == 0) return result;
            hits.Sort((x, y) => x.Key.CompareTo(y.Key));

            double cursor = 0.0;
            if (hits[0].Key > slack) return result;   // gap at the start

            foreach (var h in hits)
            {
                double lo = h.Key;
                if (lo > cursor + slack) return result;   // gap in the middle
                double ta, tb;
                OnLine(p0, ux, uy, uz, h.Value.A, tol, out ta);
                OnLine(p0, ux, uy, uz, h.Value.B, tol, out tb);
                double hi = Math.Max(ta, tb);
                if (hi > cursor) { result.Add(h.Value.Id); cursor = hi; }
            }

            if (cursor < len - slack) return new List<int>();   // did not reach the far end
            return result;
        }

        private static bool OnLine(double[] p0, double ux, double uy, double uz, double[] q, double tol, out double t)
        {
            double dx = q[0] - p0[0], dy = q[1] - p0[1], dz = q[2] - p0[2];
            t = dx * ux + dy * uy + dz * uz;
            double px = dx - t * ux, py = dy - t * uy, pz = dz - t * uz;
            return Math.Sqrt(px * px + py * py + pz * pz) <= Math.Max(tol, 1e-6) * 10.0;
        }

        /// <summary>
        /// Clears every interior release on the chain and writes the parsed start/end sets only at the
        /// two outer ends (the segment ends nearest P0 and P1).
        /// </summary>
        private void ApplyChainReleases(List<int> chain, double[] p0, double[] p1,
            ReleaseSet startSet, ReleaseSet endSet, NodeIndex live, double tol)
        {
            int startNode, endNode;
            if (!live.TryFind(p0[0], p0[1], p0[2], out startNode)) startNode = 0;
            if (!live.TryFind(p1[0], p1[1], p1[2], out endNode)) endNode = 0;

            // Clear all releases on every segment first, so a split point never keeps a hinge.
            RReleases_V161 rigid = AllRigidV161();
            foreach (int segId in chain)
            {
                RLineAttr_V161 a;
                if (!TryGetLineAttrV161(segId, out a)) continue;
                a.StartRelease = rigid;
                a.EndRelease = rigid;
                a.Beam7DOF = ELongBoolean.lbFalse;
                TrySetLineAttrV161(segId, a);
            }

            SetOuterRelease(chain[0], startNode, startSet);
            SetOuterRelease(chain[chain.Count - 1], endNode, endSet);
        }

        private void SetOuterRelease(int segId, int outerNode, ReleaseSet set)
        {
            if (set == null || outerNode <= 0) return;

            RLineAttr_V161 a;
            if (!TryGetLineAttrV161(segId, out a)) return;

            IAxisVMLine ln;
            try { ln = model.Lines.Item[segId]; }
            catch { return; }

            int lo = Math.Min(ln.StartNode, ln.EndNode);
            if (outerNode == lo) a.StartRelease = ToReleasesV161(set);
            else a.EndRelease = ToReleasesV161(set);
            a.Beam7DOF = ELongBoolean.lbFalse;

            TrySetLineAttrV161(segId, a);
        }

        private static RReleases_V161 AllRigidV161()
        {
            RRelease_V161 r = new RRelease_V161 { ReleaseType = EReleaseType.rtRigid, FunctionId = 0 };
            return new RReleases_V161 { x = r, y = r, z = r, xx = r, yy = r, zz = r};
        }

        // ==================================================================
        // Pass 9 - Rhino id stamping
        // ==================================================================

        /// <summary>
        /// Records which Rhino object each AxisVM entity came from. Nothing in v1 reads this back, but
        /// it is what makes the GUID-keyed re-sync possible later without changing the export format,
        /// so it is worth writing now while the mapping is in hand.
        /// </summary>
        private void StampRhinoIds()
        {
            if (opt.DryRun) return;

            foreach (RhinoPoint p in rhino.Points)
            {
                int nodeId = NodeForCached(p.Xyz);
                if (nodeId <= 0) continue;
                idMap.Add(string.Format("point\t{0}\t{1}", p.Guid, nodeId));
                StampNode(nodeId, p.Guid);
            }

            foreach (RhinoLine l in rhino.Lines)
            {
                LineJob job;
                if (!lineByRhinoGuid.TryGetValue(l.Guid, out job)) continue;
                idMap.Add(string.Format("line\t{0}\t{1}", l.Guid, job.LineId));
                StampLine(job.LineId, l.Guid);
            }

            if (!attachmentsWork) WriteSidecar();
        }

        private void StampNode(int nodeId, string guid)
        {
            if (!attachmentsWork) return;
            try { Stamp(model.Nodes.Attachments, nodeId, guid); }
            catch (Exception ex) { StampFailed(ex); }
        }

        private void StampLine(int lineId, string guid)
        {
            if (!attachmentsWork) return;
            try { Stamp(model.Lines.Attachments, lineId, guid); }
            catch (Exception ex) { StampFailed(ex); }
        }

        private void StampDomain(int domainId, string guid)
        {
            if (!attachmentsWork) return;
            try { Stamp(model.Domains.Attachments, domainId, guid); }
            catch (Exception ex) { StampFailed(ex); }
        }

        private void Stamp(AxisVMAttachments attachments, int itemIndex, string guid)
        {
            if (attachments == null || string.IsNullOrEmpty(guid)) return;

            byte[] payload = Encoding.UTF8.GetBytes(guid);
            Array data = Array.CreateInstance(typeof(byte), new[] { payload.Length }, new[] { 1 });
            for (int i = 0; i < payload.Length; i++) data.SetValue(payload[i], i + 1);

            attachments.AddData("RhinoGuid", itemIndex, data);
        }

        /// <summary>
        /// One failure means the mechanism is unavailable in this AxisVM build, so stop trying and
        /// fall back to the sidecar file rather than logging the same problem thousands of times.
        /// </summary>
        private void StampFailed(Exception ex)
        {
            if (!attachmentsWork) return;
            attachmentsWork = false;
            notes.Add("Rhino ids could not be attached to the AxisVM entities (" + ex.Message +
                      "), so they were written to a sidecar file instead.");
        }

        /// <summary>Fallback id map, written beside the JSON when AxisVM will not hold attachments.</summary>
        private void WriteSidecar()
        {
            if (string.IsNullOrEmpty(opt.SourcePath) || idMap.Count == 0) return;

            try
            {
                string path = Path.ChangeExtension(opt.SourcePath, ".axismap.txt");
                using (StreamWriter w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# kind\trhinoGuid\taxisVmId");
                    foreach (string line in idMap) w.WriteLine(line);
                }
                notes.Add("Rhino id map written to " + path);
            }
            catch (Exception ex)
            {
                notes.Add("The Rhino id map could not be written (" + ex.Message + ").");
            }
        }

        // ==================================================================
        // Small helpers
        // ==================================================================

        /// <summary>Thickness used when a domain does not name one [m].</summary>
        private const double DefaultThicknessM = 0.200;

        /// <summary>Looks up a node that pass 1 must already have created. 0 when it is missing.</summary>
        private int NodeForCached(RhinoXyz p)
        {
            int id;
            return nodes.TryFind(opt.Units.Length(p.X), opt.Units.Length(p.Y), opt.Units.Length(p.Z), out id) ? id : 0;
        }

        private bool TryGetLineAttr(int lineId, out RLineAttr attr)
        {
            attr = new RLineAttr();
            try
            {
                Array attrsOut;
                model.Lines.BulkGetAttr(Ids(lineId), out attrsOut);
                if (attrsOut == null || attrsOut.Length < 1) return false;
                attr = (RLineAttr)attrsOut.GetValue(1);
                return true;
            }
            catch { return false; }
        }

        private bool TrySetLineAttr(int lineId, RLineAttr attr)
        {
            try
            {
                Array attrsIn = Array.CreateInstance(typeof(RLineAttr), new[] { 1 }, new[] { 1 });
                attrsIn.SetValue(attr, 1);
                model.Lines.BulkSetAttr(Ids(lineId), attrsIn);
                return true;
            }
            catch { return false; }
        }

        /// <summary>AxisVM's COM arrays are 1-based, so they cannot be a plain C# array.</summary>
        private static Array Ids(params int[] values)
        {
            Array a = Array.CreateInstance(typeof(int), new[] { values.Length }, new[] { 1 });
            for (int i = 0; i < values.Length; i++) a.SetValue(values[i], i + 1);
            return a;
        }

        private static RStiffnesses ToStiffnesses(double[] k)
        {
            RStiffnesses s = new RStiffnesses();
            s.x = k[0]; s.y = k[1]; s.z = k[2];
            s.xx = k[3]; s.yy = k[4]; s.zz = k[5];
            return s;
        }

        private static RReleases ToReleases(ReleaseSet set)
        {
            RReleases r = new RReleases();
            r.x = ToRelease(set.Dof[0]);
            r.y = ToRelease(set.Dof[1]);
            r.z = ToRelease(set.Dof[2]);
            r.xx = ToRelease(set.Dof[3]);
            r.yy = ToRelease(set.Dof[4]);
            r.zz = ToRelease(set.Dof[5]);
            return r;
        }

        private static RRelease ToRelease(ReleaseDof dof)
        {
            RRelease r = new RRelease();
            switch (dof.Kind)
            {
                case 0: r.ReleaseType = EReleaseType.rtHinged; break;
                case 2: r.ReleaseType = EReleaseType.rtSemiRigid; r.Init = dof.Stiffness; break;
                default: r.ReleaseType = EReleaseType.rtRigid; break;
            }
            return r;
        }

        // ---- versioned release records (7-DOF, function-index semi-rigid) -----------------

        private RReleases_V161 ToReleasesV161(ReleaseSet set)
        {
            RReleases_V161 r = new RReleases_V161();
            r.x = DofV161(set.Dof[0], true);
            r.y = DofV161(set.Dof[1], true);
            r.z = DofV161(set.Dof[2], true);
            r.xx = DofV161(set.Dof[3], false);
            r.yy = DofV161(set.Dof[4], false);
            r.zz = DofV161(set.Dof[5], false);
            r.ew = new RRelease_V161 { ReleaseType = EReleaseType.rtRigid, FunctionId = 0 };
            return r;
        }

        private RRelease_V161 DofV161(ReleaseDof dof, bool translational)
        {
            switch (dof.Kind)
            {
                case 0:
                    return new RRelease_V161 { ReleaseType = EReleaseType.rtHinged, FunctionId = 0 };
                case 2:
                    int fi = translational ? springs.Translational(dof.Stiffness) : springs.Rotational(dof.Stiffness);
                    return fi > 0
                        ? new RRelease_V161 { ReleaseType = EReleaseType.rtSemiRigid, FunctionId = fi }
                        : new RRelease_V161 { ReleaseType = EReleaseType.rtHinged, FunctionId = 0 };
                default:
                    return new RRelease_V161 { ReleaseType = EReleaseType.rtRigid, FunctionId = 0 };
            }
        }

        private bool TryGetLineAttrV161(int lineId, out RLineAttr_V161 attr)
        {
            attr = new RLineAttr_V161();
            try
            {
                Array got;
                int n = model.Lines.BulkGetAttr_V161(Ids(lineId), out got);
                if (n < 1 || got == null || got.Length < 1) return false;
                object raw = got.GetValue(got.GetLowerBound(0));
                if (!(raw is RLineAttr_V161)) return false;
                attr = (RLineAttr_V161)raw;
                return true;
            }
            catch { return false; }
        }

        private bool TrySetLineAttrV161(int lineId, RLineAttr_V161 attr)
        {
            try
            {
                // Fresh 1-based array of our own type - the array BulkGetAttr_V161 returns is only
                // type-equivalent (EmbedInteropTypes) and SetValue would reject writes into it.
                Array arr = Array.CreateInstance(typeof(RLineAttr_V161), new[] { 1 }, new[] { 1 });
                arr.SetValue(attr, 1);
                return model.Lines.BulkSetAttr_V161(Ids(lineId), arr) > 0;
            }
            catch { return false; }
        }

        private static RPoint3d Point(RhinoXyz v)
        {
            RPoint3d p = new RPoint3d();
            p.x = v.X; p.y = v.Y; p.z = v.Z;
            return p;
        }

        private static string Fmt(double x, double y, double z)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", x, y, z);
        }

        private static string Short(string guid)
        {
            return !string.IsNullOrEmpty(guid) && guid.Length >= 8 ? guid.Substring(0, 8) : guid;
        }

        // ==================================================================
        // Node lookup
        // ==================================================================

        /// <summary>
        /// A spatial hash over the nodes created so far.
        ///
        /// A plain "round the coordinates and use them as a key" map is one line shorter but wrong at
        /// the bucket edges: two points a tenth of a millimetre apart can round to different keys and
        /// become two nodes, and a model that will not mesh is the result. Probing the 26 neighbouring
        /// cells as well and then measuring the real distance makes the lookup exact within the
        /// tolerance while staying O(1) per point.
        /// </summary>
        private class NodeIndex
        {
            private class Entry
            {
                public double X, Y, Z;
                public int NodeId;
            }

            private readonly double cell;
            private readonly double tolerance;
            private readonly Dictionary<string, List<Entry>> buckets = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

            public NodeIndex(double tolerance)
            {
                this.tolerance = Math.Max(tolerance, 1e-9);
                this.cell = this.tolerance;
            }

            public bool TryFind(double x, double y, double z, out int nodeId)
            {
                nodeId = 0;

                int cx = (int)Math.Floor(x / cell);
                int cy = (int)Math.Floor(y / cell);
                int cz = (int)Math.Floor(z / cell);

                double best = tolerance * tolerance;
                bool found = false;

                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            List<Entry> bucket;
                            if (!buckets.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out bucket)) continue;

                            foreach (Entry e in bucket)
                            {
                                double ex = e.X - x, ey = e.Y - y, ez = e.Z - z;
                                double d2 = ex * ex + ey * ey + ez * ez;
                                if (d2 > best) continue;

                                best = d2;
                                nodeId = e.NodeId;
                                found = true;
                            }
                        }

                return found;
            }

            public void Add(double x, double y, double z, int nodeId)
            {
                string key = Key((int)Math.Floor(x / cell), (int)Math.Floor(y / cell), (int)Math.Floor(z / cell));

                List<Entry> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new List<Entry>();
                    buckets[key] = bucket;
                }
                bucket.Add(new Entry { X = x, Y = y, Z = z, NodeId = nodeId });
            }

            private static string Key(int x, int y, int z)
            {
                return x.ToString(CultureInfo.InvariantCulture) + ":" +
                       y.ToString(CultureInfo.InvariantCulture) + ":" +
                       z.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
