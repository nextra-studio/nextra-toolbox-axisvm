using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What an earlier import of one Rhino file left in the model, found by its RhinoLink mark.</summary>
    public class LinkedItems
    {
        public int Nodes;
        public int Lines;
        public int Domains;
        public int LoadPanels;
        public int Parts;

        public int Total { get { return Nodes + Lines + Domains + LoadPanels + Parts; } }

        public override string ToString()
        {
            return string.Format("{0} nodes, {1} lines, {2} domains, {3} load panels, {4} parts", Nodes, Lines, Domains, LoadPanels, Parts);
        }
    }

    /// <summary>
    /// Re-import as an update: every node, line, domain and part an import creates is marked with the
    /// attachment "RhinoLink" = the export's file name. Importing the same file again with
    /// ImportOptions.ReplacePrevious first deletes everything carrying that mark (load panels are found
    /// through their contour lines, which carry it) and then builds as usual, so a curve moved in Rhino
    /// arrives moved instead of next to its old copy.
    ///
    /// Marking by "created by this run" (UIDs absent before it) rather than per Rhino object is what
    /// catches the lines and nodes no Rhino object owns on its own: slab edges, intersection splits,
    /// one-way panel strips. AxisVM keeps attachments with their entity when indices shift and saves
    /// them in the .axs, and deleting the lines and nodes removes their members, supports, links,
    /// rigid bodies, edge connections and loads with them.
    ///
    /// Entities drawn in AxisVM and handed to Rhino by "Send to Rhino" carry RhinoAdopted instead
    /// (see RhinoLinkSender). They are replaced only once the incoming Rhino model knows the objects
    /// they became (ImportOptions.RhinoKnownGuids); until then Rhino has never seen them, and deleting
    /// them would lose them.
    /// </summary>
    public partial class AxisModelBuilder
    {
        public const string LinkAttachment = LinkState.Link;

        /// <summary>The mark an import of this file writes: its file name, case-insensitive.</summary>
        public static string LinkKey(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath)) return "";
            try { return Path.GetFileName(jsonPath.Trim()).ToLowerInvariant(); }
            catch { return jsonPath.Trim().ToLowerInvariant(); }
        }

        private HashSet<int> nodeUidsBefore = new HashSet<int>();
        private HashSet<int> domainUidsBefore = new HashSet<int>();
        private HashSet<int> partUidsBefore = new HashSet<int>();

        // ==================================================================
        // Finding an earlier import
        // ==================================================================

        /// <summary>UIDs of everything an earlier import (or an adoption Rhino has caught up with) left.</summary>
        private class LinkedUids
        {
            public readonly List<int> Nodes = new List<int>();
            public readonly List<int> Lines = new List<int>();
            public readonly List<int> Domains = new List<int>();
            public readonly List<int> LoadPanels = new List<int>();
            /// <summary>Part ids (the custom parts collection addresses parts by id, not by index).</summary>
            public readonly List<int> Parts = new List<int>();

            public LinkedItems Counts()
            {
                return new LinkedItems { Nodes = Nodes.Count, Lines = Lines.Count, Domains = Domains.Count, LoadPanels = LoadPanels.Count, Parts = Parts.Count };
            }
        }

        /// <summary>What an earlier import of the file at <paramref name="jsonPath"/> left in the model (for the dialog).</summary>
        public static LinkedItems FindLinked(IAxisVMModel model, string jsonPath)
        {
            string key = LinkKey(jsonPath);
            if (model == null || key.Length == 0) return new LinkedItems();
            try { return Linked(model, key, null, null).Counts(); }
            catch { return new LinkedItems(); }
        }

        /// <summary>True when an entity carries the import mark, or is adopted and Rhino now knows what it became.</summary>
        private static bool Replaceable(AxisVMAttachments a, int index, string key, HashSet<string> rhinoKnows)
        {
            if (string.Equals(LinkState.Read(a, LinkState.Link, index), key, StringComparison.Ordinal)) return true;
            if (rhinoKnows == null) return false;
            List<string> owners;
            string adoptedKey = LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, index), out owners);
            if (!string.Equals(adoptedKey, key, StringComparison.Ordinal)) return false;
            foreach (string g in owners) if (rhinoKnows.Contains(g)) return true;
            return false;
        }

        private static LinkedUids Linked(IAxisVMModel model, string key, HashSet<string> rhinoKnows, Dictionary<string, string> adoptedPanels)
        {
            LinkedUids found = new LinkedUids();

            HashSet<int> lineIndices = new HashSet<int>();
            AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
            int[] uids = LineUids(model);
            for (int i = 1; i <= uids.Length && la != null; i++)
            {
                if (!Replaceable(la, i, key, rhinoKnows)) continue;
                lineIndices.Add(i);
                found.Lines.Add(uids[i - 1]);
            }

            AxisVMAttachments na = TryGet(() => model.Nodes.Attachments);
            uids = NodeUids(model);
            for (int i = 1; i <= uids.Length && na != null; i++)
                if (Replaceable(na, i, key, rhinoKnows)) found.Nodes.Add(uids[i - 1]);

            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            uids = DomainUids(model);
            for (int i = 1; i <= uids.Length && da != null; i++)
                if (Replaceable(da, i, key, rhinoKnows)) found.Domains.Add(uids[i - 1]);

            int n;

            // Load panels cannot carry attachments: one whose contour runs only along marked lines is ours; a free polygon
            // panel "Send to Rhino" adopted is recognised by its outline once Rhino holds it.
            n = TryCount(() => model.LoadPanels.Count);
            for (int i = 1; i <= n; i++)
            {
                try
                {
                    Array ids = null;
                    model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                    if (ids == null || ids.Length == 0)
                    {
                        if (rhinoKnows == null || adoptedPanels == null || adoptedPanels.Count == 0) continue;
                        List<double[]> outline = LinkState.PanelPolygon(model, i);
                        string guid;
                        if (outline != null && adoptedPanels.TryGetValue(LinkState.PolygonState(outline), out guid) && rhinoKnows.Contains(guid))
                            found.LoadPanels.Add(model.LoadPanels.get_UID(i));
                        continue;
                    }
                    if (lineIndices.Count == 0) continue;
                    bool all = true;
                    foreach (object o in ids) if (!lineIndices.Contains(Convert.ToInt32(o))) { all = false; break; }
                    if (all) found.LoadPanels.Add(model.LoadPanels.get_UID(i));
                }
                catch { }
            }

            try
            {
                AxisVMAttachments pa = model.CustomParts.Attachments;
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                int parts = root.PartCount;
                for (int i = 1; i <= parts; i++)
                {
                    int id = root.get_PartId(i);
                    if (string.Equals(LinkState.Read(pa, LinkState.Link, id), key, StringComparison.Ordinal)) found.Parts.Add(id);
                }
            }
            catch { }

            return found;
        }

        // ==================================================================
        // Removing it (before the build)
        // ==================================================================

        /// <summary>
        /// Deletes what an earlier import of this file created: load panels, domains, lines, parts, then
        /// the marked nodes nothing else uses any more. A marked node that a line drawn in AxisVM still
        /// uses is kept (and unmarked), so that line is not deleted with it.
        /// </summary>
        private void RemovePrevious()
        {
            string key = LinkKey(opt.SourcePath);
            if (key.Length == 0) return;

            LinkedUids old;
            try { old = Linked(model, key, opt.RhinoKnownGuids, opt.AdoptedPanels); }
            catch (Exception ex)
            {
                notes.Add("The earlier import could not be looked up (" + ex.Message + "); nothing was replaced.");
                return;
            }

            LinkedItems counts = old.Counts();
            if (counts.Total == 0)
            {
                notes.Add("Update: nothing from an earlier import of '" + key + "' was found in this model, so everything was imported as new.");
                return;
            }

            if (opt.DryRun)
            {
                stats.Replaced = counts;
                return;
            }

            // The end nodes of the lines about to go: those an adoption brought in carry no mark of
            // their own, and are deleted below too when nothing else uses them.
            // Everything is read in bulk and deleted in one go per kind: item-by-item calls made an update of a large
            // model (15 000 lines) take minutes.
            HashSet<int> nodeUids = new HashSet<int>(old.Nodes);
            {
                int[] lineUids = LineUids(), nodeUidOf = NodeUids();
                int[][] ends = LineEnds(model);
                HashSet<int> oldLines = new HashSet<int>(old.Lines);
                for (int i = 0; i < lineUids.Length && i < ends.Length; i++)
                {
                    if (!oldLines.Contains(lineUids[i]) || ends[i] == null) continue;
                    foreach (int e in ends[i]) if (e > 0 && e <= nodeUidOf.Length) nodeUids.Add(nodeUidOf[e - 1]);
                }
            }

            RememberMeshes(old.Domains);
            DeleteMany(old.LoadPanels, u => model.LoadPanels.get_IndexOfUID(u), (i, on) => model.LoadPanels.set_Selected(i, on),
                () => model.LoadPanels.DeleteSelected(), i => model.LoadPanels.Delete(i), () => model.LoadPanels.Count);
            DeleteMany(old.Domains, u => model.Domains.get_IndexOfUID(u), (i, on) => model.Domains.set_Selected(i, on),
                () => model.Domains.DeleteSelected(), i => model.Domains.Delete(i), () => model.Domains.Count);
            DeleteMany(old.Lines, u => model.Lines.get_IndexOfUID(u), (i, on) => model.Lines.set_Selected(i, on),
                () => model.Lines.DeleteSelected(), i => model.Lines.Delete(i), () => model.Lines.Count);
            RemoveMeshLeftovers();

            // Parts by descending id so the ids still to delete stay valid.
            old.Parts.Sort();
            for (int k = old.Parts.Count - 1; k >= 0; k--)
            {
                try { model.CustomParts.DeletePart(old.Parts[k]); } catch { }
            }

            // UIDs: deleting nodes below shifts the indices of the rest.
            HashSet<int> used = new HashSet<int>();
            {
                int[] nodeUidOf = NodeUids();
                foreach (int[] e in LineEnds(model))
                {
                    if (e == null) continue;
                    foreach (int nd in e) if (nd > 0 && nd <= nodeUidOf.Length) used.Add(nodeUidOf[nd - 1]);
                }
            }

            HashSet<int> markedNodes = new HashSet<int>(old.Nodes);
            HashSet<int> keptNodes = new HashSet<int>();
            List<int> unusedNodes = new List<int>();
            int kept = 0;
            foreach (int uid in nodeUids)
            {
                if (!used.Contains(uid)) { unusedNodes.Add(uid); continue; }
                if (!markedNodes.Contains(uid)) continue;
                int index;
                try { index = model.Nodes.get_IndexOfUID(uid); } catch { continue; }
                if (index <= 0) continue;
                try { model.Nodes.Attachments.Delete(LinkAttachment, index); } catch { }
                try { model.Nodes.Attachments.Delete(LinkState.Adopted, index); } catch { }
                try { model.Nodes.Attachments.Delete(LinkState.Guid, index); } catch { }
                keptNodes.Add(uid);
                kept++;
            }
            DeleteNodes(unusedNodes);
            DeleteNodalLoads(keptNodes);

            stats.Replaced = counts;
            if (kept > 0)
                notes.Add(string.Format("Update: {0} node(s) of the earlier import are also used by lines drawn in AxisVM and were kept.", kept));
        }

        /// <summary>
        /// The nodal loads of nodes that stay (other lines use them) while the Rhino points they stood for are rebuilt:
        /// the rebuilt point brings its loads again, so the old ones would double.
        /// </summary>
        private void DeleteNodalLoads(HashSet<int> nodeUids)
        {
            if (nodeUids.Count == 0) return;
            HashSet<int> indices = new HashSet<int>();
            foreach (int uid in nodeUids)
            {
                try { int i = model.Nodes.get_IndexOfUID(uid); if (i > 0) indices.Add(i); } catch { }
            }
            int count = TryCount(() => model.Loads.Count);
            for (int i = count; i >= 1; i--)
            {
                try
                {
                    if (model.Loads.get_LoadType(i) == ELoadType.ltNodalForce && indices.Contains(model.Loads.get_ElementId(i))) model.Loads.Delete(i);
                }
                catch { }
            }
        }

        /// <summary>Mesh settings of the replaced domains that had a mesh, by the Rhino id they stand for.</summary>
        private readonly Dictionary<string, RDomainMeshParameters_V171> meshByGuid =
            new Dictionary<string, RDomainMeshParameters_V171>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The mesh is never part of the link. A replaced domain is deleted with its mesh (and the mesh's pieces of its
        /// lines); its rebuilt successor gets a mesh again from <see cref="RestoreMeshes"/>. Meshes are deliberately NOT
        /// deleted up front: that joins the lines the mesh had split into new lines without their link marks, which then
        /// look like elements drawn in AxisVM and come back as duplicates. Domains the update does not replace keep their mesh.
        /// </summary>
        private void RememberMeshes(List<int> replacedDomainUids)
        {
            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            if (da == null) return;
            int[] lineUids = null, nodeUids = null;
            int[][] ends = null;
            foreach (int uid in replacedDomainUids)
            {
                try
                {
                    int i = model.Domains.get_IndexOfUID(uid);
                    if (i <= 0) continue;
                    IAxisVMDomain d = model.Domains.Item[i];
                    if (d.MeshExists != ELongBoolean.lbTrue) continue;
                    RDomainMeshParameters_V171 p = new RDomainMeshParameters_V171();
                    if (d.GetMeshParameters_V171(ref p) > 0)
                        foreach (string g in LinkState.Guids(da, i)) meshByGuid[g] = p;

                    // The lines and nodes of its mesh elements: deleting the domain leaves them behind (loose nodes the
                    // next mesh fails on), so they are removed after the old elements (RemoveMeshLeftovers).
                    if (lineUids == null) { lineUids = LineUids(); nodeUids = NodeUids(); ends = LineEnds(model); }
                    Array sids = d.MeshSurfaceIds, surfaces;
                    if (sids == null || sids.Length == 0 || model.Surfaces.BulkGetSurfaces(sids, out surfaces) <= 0 || surfaces == null) continue;
                    foreach (object o in surfaces)
                    {
                        RSurface s = (RSurface)o;
                        foreach (int l in new[] { s.LineIndex1, s.LineIndex2, s.LineIndex3, s.LineIndex4 })
                        {
                            if (l <= 0 || l > lineUids.Length) continue;
                            meshLineUids.Add(lineUids[l - 1]);
                            if (ends[l - 1] == null) continue;
                            foreach (int nd in ends[l - 1]) if (nd > 0 && nd <= nodeUids.Length) meshNodeUids.Add(nodeUids[nd - 1]);
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>Lines and nodes of the replaced domains' mesh elements (UIDs).</summary>
        private readonly HashSet<int> meshLineUids = new HashSet<int>(), meshNodeUids = new HashSet<int>();

        /// <summary>
        /// After the old elements are deleted: removes what is left of their mesh - mesh lines still there that are plain
        /// lines without a link mark, and mesh nodes no line uses any more that carry no support, nodal load or link mark.
        /// Everything else (the user's own nodes and lines) stays.
        /// </summary>
        private void RemoveMeshLeftovers()
        {
            if (meshLineUids.Count == 0 && meshNodeUids.Count == 0) return;
            AxisVMAttachments la = TryGet(() => model.Lines.Attachments), na = TryGet(() => model.Nodes.Attachments);

            // Edges of the domains and load panels that stay are never touched, even where they were also mesh lines.
            HashSet<int> edges = new HashSet<int>();
            int domainCount = TryCount(() => model.Domains.Count);
            if (domainCount > 0)
            {
                try
                {
                    Array counts, ids, attrs;
                    model.Domains.BulkGetDomains(Range(domainCount), out counts, out ids, out attrs);
                    foreach (int l in LinkState.IntList(ids)) edges.Add(l);
                }
                catch
                {
                    for (int i = 1; i <= domainCount; i++)
                    {
                        foreach (int l in LinkState.ContourLines(model, i)) edges.Add(l);
                        foreach (List<int> h in LinkState.HoleLines(model, i)) foreach (int l in h) edges.Add(l);
                    }
                }
            }
            int np = TryCount(() => model.LoadPanels.Count);
            for (int i = 1; i <= np; i++)
            {
                try
                {
                    Array ids = null;
                    model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                    foreach (int l in LinkState.IntList(ids)) edges.Add(l);
                }
                catch { }
            }

            List<int> lines = new List<int>();
            int[] lineUids = LineUids();
            LinkState.BeginCache(model);   // line types in bulk
            try
            {
                for (int i = 1; i <= lineUids.Length; i++)
                {
                    if (!meshLineUids.Contains(lineUids[i - 1]) || edges.Contains(i)) continue;
                    RLineAttr a;
                    if (LinkState.TryLineAttr(model, i, out a) && LinkState.LineTypeName(a.LineType).Length > 0) continue;
                    if (HasLinkMark(la, i)) continue;
                    lines.Add(lineUids[i - 1]);
                }
            }
            finally { LinkState.EndCache(); }
            DeleteMany(lines, u => model.Lines.get_IndexOfUID(u), (i, on) => model.Lines.set_Selected(i, on),
                () => model.Lines.DeleteSelected(), i => model.Lines.Delete(i), () => model.Lines.Count);

            HashSet<int> keep = new HashSet<int>();
            int[] nodeUids = NodeUids();
            foreach (int[] e in LineEnds(model))
                if (e != null) foreach (int nd in e) if (nd > 0 && nd <= nodeUids.Length) keep.Add(nodeUids[nd - 1]);
            try
            {
                for (int s = 1; s <= model.NodalSupports.Count; s++)
                {
                    int nd = model.NodalSupports.Item[s].NodeId;
                    if (nd > 0 && nd <= nodeUids.Length) keep.Add(nodeUids[nd - 1]);
                }
            }
            catch { }
            int loads = TryCount(() => model.Loads.Count);
            for (int i = 1; i <= loads; i++)
            {
                try
                {
                    if (model.Loads.get_LoadType(i) != ELoadType.ltNodalForce) continue;
                    int nd = model.Loads.get_ElementId(i);
                    if (nd > 0 && nd <= nodeUids.Length) keep.Add(nodeUids[nd - 1]);
                }
                catch { }
            }

            List<int> nodes = new List<int>();
            for (int i = 1; i <= nodeUids.Length; i++)
                if (meshNodeUids.Contains(nodeUids[i - 1]) && !keep.Contains(nodeUids[i - 1]) && !HasLinkMark(na, i)) nodes.Add(nodeUids[i - 1]);
            DeleteNodes(nodes);
        }

        /// <summary>
        /// Stands for a Rhino object (RhinoGuid). One read: every entity the link keeps carries it (imported and adopted
        /// alike); a RhinoLink mark alone means "created by an import", and such leftovers may go.
        /// </summary>
        private static bool HasLinkMark(AxisVMAttachments a, int index)
        {
            return a != null && LinkState.Read(a, LinkState.Guid, index) != null;
        }

        /// <summary>
        /// Settings AxisVM can mesh with. A domain whose mesh came from an older AxisVM (or none) reports uninitialised
        /// values (a size of 1e-268, random fit values); those get the size the other domains use, or 0.5 m, and plain
        /// adaptive mixed quad/triangle settings.
        /// </summary>
        private static RDomainMeshParameters_V171 Usable(RDomainMeshParameters_V171 p, double fallbackSize, out bool replaced)
        {
            replaced = false;
            if (Plausible(p)) return p;
            replaced = true;
            return new RDomainMeshParameters_V171
            {
                MeshSize = fallbackSize,
                MeshType = EMeshType.mtAdaptive,
                MeshGeometryType = EMeshGeometryType.mgtMixedQuadTriangle,
                QuadMeshQuality = 6
            };
        }

        private static bool Plausible(RDomainMeshParameters_V171 p)
        {
            Func<ELongBoolean, double, bool> fit = (on, v) => on != ELongBoolean.lbTrue || (v > 1e-4 && v < 100);
            return p.MeshSize > 0.005 && p.MeshSize < 100 && p.QuadMeshQuality >= 0 && p.QuadMeshQuality <= 100
                && Enum.IsDefined(typeof(EMeshType), p.MeshType) && Enum.IsDefined(typeof(EMeshGeometryType), p.MeshGeometryType)
                && fit(p.IsFitToPointLoad, p.FitToPointLoadValue) && fit(p.IsFitToLineLoad, p.FitToLineLoadValue) && fit(p.IsFitToSurfaceLoad, p.FitToSurfaceLoadValue);
        }

        /// <summary>
        /// Meshes the rebuilt domains whose predecessor had a mesh (ImportOptions.RemeshRebuilt). All in ONE call with one
        /// set of settings - the ones most of them had - so the new mesh is conforming everywhere; no domain intersection
        /// is generated (it would change the geometry just linked). Runs before the Rhino ids are stamped: meshing splits
        /// ribs and edges into pieces, so every curve is re-identified as its pieces afterwards. With the option off, the
        /// report says how many domains to mesh.
        /// </summary>
        private void RestoreMeshes()
        {
            if (opt.DryRun || meshByGuid.Count == 0) return;
            List<int> targets = new List<int>();
            var votes = new Dictionary<string, KeyValuePair<RDomainMeshParameters_V171, int>>(StringComparer.Ordinal);
            foreach (SurfaceJob s in surfaceJobs)
            {
                RDomainMeshParameters_V171 p;
                if (s.DomainId <= 0 || IsPanel(s.Role) || !meshByGuid.TryGetValue(s.Src.Guid, out p)) continue;
                targets.Add(s.DomainId);
                if (!Plausible(p)) continue;
                string k = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}", p.MeshSize, p.MeshType, p.IsFitToPointLoad,
                    p.FitToPointLoadValue, p.IsFitToLineLoad, p.FitToLineLoadValue, p.IsFitToSurfaceLoad, p.FitToSurfaceLoadValue, p.MeshGeometryType, p.QuadMeshQuality, p.FitToColumnhead);
                KeyValuePair<RDomainMeshParameters_V171, int> v;
                votes.TryGetValue(k, out v);
                votes[k] = new KeyValuePair<RDomainMeshParameters_V171, int>(p, v.Value + 1);
            }
            if (targets.Count == 0) return;
            if (!opt.RemeshRebuilt)
            {
                notes.Add(string.Format("Update: {0} rebuilt domain(s) had a mesh before and are now without one - mesh them in AxisVM " +
                    "(or tick 'Mesh the rebuilt domains again' on the Live link tab).", targets.Count));
                return;
            }

            bool guessed;
            RDomainMeshParameters_V171 settings = Usable(new RDomainMeshParameters_V171(), 0.5, out guessed);
            int most = 0;
            foreach (KeyValuePair<RDomainMeshParameters_V171, int> v in votes.Values)
                if (v.Value > most) { most = v.Value; settings = v.Key; guessed = false; }
            settings.MeshOnlyUnmeshed = ELongBoolean.lbTrue;
            settings.GenDomainIntersection = ELongBoolean.lbFalse;
            settings.KeepGuideLinesAfterFailure = ELongBoolean.lbFalse;

            string error = null;
            try
            {
                model.SelectAll(ELongBoolean.lbFalse);
                foreach (int id in targets) model.Domains.set_Selected(id, ELongBoolean.lbTrue);
                Array codes, points, lines;
                int rc = model.Domains.GenerateMeshOnSelectedDomains_V171(ref settings, out codes, out points, out lines);
                if (rc <= 0) error = "code " + rc + (codes != null && codes.Length > 0 ? " (" + string.Join(",", LinkState.IntList(codes)) + ")" : "");
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                try { model.SelectAll(ELongBoolean.lbFalse); } catch { }
            }

            int meshed = 0;
            foreach (int id in targets)
            {
                try { if (model.Domains.Item[id].MeshExists == ELongBoolean.lbTrue) meshed++; } catch { }
            }
            notes.Add(string.Format(CultureInfo.InvariantCulture, "Update: {0} of {1} rebuilt domain(s) meshed again in one batch ({2:0.###} m{3}){4}.",
                meshed, targets.Count, settings.MeshSize, guessed ? ", default - no readable earlier settings" : "", error == null ? "" : " - AxisVM: " + error));
            if (meshed > 0) ResolveChains(Math.Max(opt.MergeToleranceM, 1e-6));
        }

        /// <summary>
        /// Deletes the entities with these UIDs: selected and deleted in one call (one by one, each delete re-indexes the
        /// rest, which takes minutes on a large model). Whatever the bulk delete did not remove is deleted one by one.
        /// The selection is cleared before and after.
        /// </summary>
        private void DeleteMany(List<int> uidList, Func<int, int> indexOfUid, Action<int, ELongBoolean> select,
                                Func<int> deleteSelected, Func<int, int> deleteOne, Func<int> count)
        {
            if (uidList.Count == 0) return;
            bool done = false;
            try
            {
                List<int> indices = new List<int>();
                foreach (int uid in uidList)
                {
                    try { int i = indexOfUid(uid); if (i > 0) indices.Add(i); } catch { }
                }
                if (indices.Count == 0) return;
                model.SelectAll(ELongBoolean.lbFalse);
                foreach (int i in indices) select(i, ELongBoolean.lbTrue);
                int before = count();
                deleteSelected();
                done = count() == before - indices.Count;
            }
            catch { }
            finally
            {
                try { model.SelectAll(ELongBoolean.lbFalse); } catch { }
            }
            if (done) return;
            foreach (int uid in uidList) DeleteByUid(deleteOne, indexOfUid, uid);
        }

        /// <summary>Deletes nodes by UID with one bulk call, one by one for whatever that leaves.</summary>
        private void DeleteNodes(List<int> uidList)
        {
            if (uidList.Count == 0) return;
            List<int> indices = new List<int>();
            foreach (int uid in uidList)
            {
                try { int i = model.Nodes.get_IndexOfUID(uid); if (i > 0) indices.Add(i); } catch { }
            }
            if (indices.Count == 0) return;
            try
            {
                int before = SafeNodeCount();
                model.Nodes.BulkDelete(Ids(indices.ToArray()));
                if (SafeNodeCount() == before - indices.Count) return;
            }
            catch { }
            foreach (int uid in uidList) DeleteByUid(i => model.Nodes.Delete(i), u => model.Nodes.get_IndexOfUID(u), uid);
        }

        private static void DeleteByUid(Func<int, int> delete, Func<int, int> indexOfUid, int uid)
        {
            try
            {
                int index = indexOfUid(uid);
                if (index > 0) delete(index);
            }
            catch { }
        }

        // ==================================================================
        // Marking this import (after the build)
        // ==================================================================

        /// <summary>Remembers what exists before the build, so MarkCreated can tell what the build added.</summary>
        private void CaptureBefore()
        {
            nodeUidsBefore = new HashSet<int>(NodeUids());
            domainUidsBefore = new HashSet<int>(DomainUids());
            partUidsBefore = new HashSet<int>();
            try
            {
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                for (int i = 1; i <= root.PartCount; i++) partUidsBefore.Add(root.get_PartUID(i));
            }
            catch { }
        }

        /// <summary>
        /// Marks everything this run created with RhinoLink, and stores the state of every entity that
        /// stands for a Rhino object (RhinoState) - the baseline "Send to Rhino" compares against.
        /// </summary>
        private void MarkCreated()
        {
            if (opt.DryRun) return;
            string key = LinkKey(opt.SourcePath);
            if (key.Length == 0) return;

            int marked = 0, failed = 0;
            Action<AxisVMAttachments, int> mark = (a, index) =>
            {
                if (LinkState.Write(a, LinkState.Link, index, key)) marked++; else failed++;
            };

            // The states read node coordinates, line ends and attributes: in bulk through the cache.
            LinkState.BeginCache(model);
            try
            {
                AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
                int[] lineUids = LineUids();
                for (int i = 1; i <= lineUids.Length; i++)
                {
                    if (preexistingLineUids.Contains(lineUids[i - 1])) continue;
                    mark(la, i);
                    if (LinkState.Read(la, LinkState.Guid, i) != null) LinkState.Write(la, LinkState.State, i, LinkState.LineState(model, i));
                }

                AxisVMAttachments na = TryGet(() => model.Nodes.Attachments);
                int[] nodeUids = NodeUids();
                for (int i = 1; i <= nodeUids.Length; i++)
                {
                    if (nodeUidsBefore.Contains(nodeUids[i - 1])) continue;
                    mark(na, i);
                    if (LinkState.Read(na, LinkState.Guid, i) != null) LinkState.Write(na, LinkState.State, i, LinkState.NodeState(model, i));
                }

                AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
                int[] domainUids = DomainUids();
                for (int i = 1; i <= domainUids.Length; i++)
                {
                    if (domainUidsBefore.Contains(domainUids[i - 1])) continue;
                    mark(da, i);
                    LinkState.Write(da, LinkState.State, i, LinkState.DomainState(model, i));
                }
            }
            finally { LinkState.EndCache(); }

            try
            {
                AxisVMAttachments pa = model.CustomParts.Attachments;
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                for (int i = 1; i <= root.PartCount; i++)
                    if (!partUidsBefore.Contains(root.get_PartUID(i))) mark(pa, root.get_PartId(i));
            }
            catch { }

            if (failed > 0)
                notes.Add(string.Format("{0} of {1} imported entities could not be marked as coming from '{2}'; a later update cannot replace those and may duplicate them.", failed, failed + marked, key));
        }

        // UIDs of every line / node / domain, index - 1 -> UID, in one bulk call each (falls back to item by item).
        private int[] LineUids() { return LineUids(model); }
        private int[] NodeUids() { return NodeUids(model); }
        private int[] DomainUids() { return DomainUids(model); }

        private static int[] LineUids(IAxisVMModel model)
        {
            int n = TryCount(() => model.Lines.Count);
            return BulkUids(n, ids => { Array u; model.Lines.BulkGetUIDs(ids, out u); return u; }, i => model.Lines.get_UID(i));
        }

        private static int[] NodeUids(IAxisVMModel model)
        {
            int n = TryCount(() => model.Nodes.Count);
            return BulkUids(n, ids => { Array u; model.Nodes.BulkGetUIDs(ids, out u); return u; }, i => model.Nodes.get_UID(i));
        }

        private static int[] DomainUids(IAxisVMModel model)
        {
            int n = TryCount(() => model.Domains.Count);
            return BulkUids(n, ids => { Array u; model.Domains.BulkGetUIDs(ids, out u); return u; }, i => model.Domains.get_UID(i));
        }

        /// <summary>Start and end node of every line, index - 1 -> {node1, node2}, in one bulk call (item by item if refused).</summary>
        private static int[][] LineEnds(IAxisVMModel model)
        {
            int n = TryCount(() => model.Lines.Count);
            int[][] ends = new int[Math.Max(0, n)][];
            if (n <= 0) return ends;
            try
            {
                Array data;
                if (model.Lines.BulkGetLineData(Range(n), out data) > 0 && data != null && data.Length >= n)
                {
                    int lo = data.GetLowerBound(0);
                    for (int i = 0; i < n; i++)
                    {
                        RLineData d = (RLineData)data.GetValue(lo + i);
                        ends[i] = new[] { d.NodeId1, d.NodeId2 };
                    }
                    return ends;
                }
            }
            catch { }
            for (int i = 1; i <= n; i++) ends[i - 1] = LinkState.LineNodes(model, i);
            return ends;
        }

        private static int[] BulkUids(int count, Func<Array, Array> bulk, Func<int, int> single)
        {
            int[] uids = new int[Math.Max(0, count)];
            if (count <= 0) return uids;
            try
            {
                Array got = bulk(Range(count));
                if (got != null && got.Length >= count)
                {
                    int lo = got.GetLowerBound(0);
                    for (int i = 0; i < count; i++) uids[i] = Convert.ToInt32(got.GetValue(lo + i));
                    return uids;
                }
            }
            catch { }
            for (int i = 1; i <= count; i++)
            {
                try { uids[i - 1] = single(i); } catch { uids[i - 1] = 0; }
            }
            return uids;
        }

        private static HashSet<int> Uids(int count, Func<int, int> uid)
        {
            HashSet<int> set = new HashSet<int>();
            for (int i = 1; i <= count; i++)
            {
                try { set.Add(uid(i)); } catch { }
            }
            return set;
        }

        private static T TryGet<T>(Func<T> get) where T : class
        {
            try { return get(); } catch { return null; }
        }

        private static int TryCount(Func<int> get)
        {
            try { return get(); } catch { return 0; }
        }
    }
}
