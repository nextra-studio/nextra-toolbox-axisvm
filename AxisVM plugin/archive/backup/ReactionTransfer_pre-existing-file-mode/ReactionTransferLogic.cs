using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public class ReactionTransferSettings
    {
        public string TargetFilePath { get; set; }
        public bool TransferSupports { get; set; }
        public int ResultType { get; set; } // 0=Linear, 1=NonLinear, 2=Envelope
        public bool ApplyAsNodePointLoads { get; set; }
        public bool ApplyAsBeamPointLoads { get; set; }

        // Only meaningful when ResultType == 2 (Envelope)
        public AxisVM.ENodalSupportForce EnvelopeComponent { get; set; }
        public AxisVM.EMinMaxType EnvelopeMinMax { get; set; }
    }

    public static class ReactionTransferLogic
    {
        /// <summary>Name of the load case created in the target model for envelope results.</summary>
        private const string EnvelopeLoadCaseName = "Transferred Reactions (Env)";

        /// <summary>Reactions whose components are all below this are not worth creating a load for.</summary>
        private const double ZeroForceEpsilon = 1e-9;

        /// <summary>One extracted reaction. Nodes are keyed by UID because indices shift during trimming.</summary>
        class MemReaction
        {
            public int SourceNodeUID;
            public int LoadCaseId;
            public AxisVM.RNodalSupportForceValues Forces;
        }

        /// <summary>A nodal support in the source model that the user selected.</summary>
        class MemSupport
        {
            public int SupportIndex;  // index in the source model's NodalSupports collection
            public int NodeUID;       // UID of the supported node (survives trimming)
        }

        class MemoryData
        {
            public HashSet<int> SelectedNodes = new HashSet<int>();
            public HashSet<int> SelectedLines = new HashSet<int>();
            public HashSet<int> SelectedMembers = new HashSet<int>();
            public HashSet<int> SelectedSurfaces = new HashSet<int>();
            public HashSet<int> SelectedDomains = new HashSet<int>();
            public HashSet<int> SelectedLoadPanels = new HashSet<int>();
            public HashSet<int> SelectedSupports = new HashSet<int>();
            public HashSet<int> SelectedLinkElements = new HashSet<int>();

            public List<MemSupport> Supports = new List<MemSupport>();
            public List<MemReaction> Reactions = new List<MemReaction>();

            /// <summary>UIDs of nodes whose nodal support the user wants to keep in the target model.</summary>
            public HashSet<int> KeepSupportNodeUIDs = new HashSet<int>();
        }

        /// <summary>Counters gathered during the run so the user gets one informative summary instead of six dialogs.</summary>
        class Stats
        {
            public int ReactionsRead;
            public int NodeLoads;
            public int BeamLoads;
            public int SkippedZero;
            public int SkippedUnusableLoadCase;
            public int UnresolvedNodes;
            public int BeamFallbackToNode;
            public int FailedLoadCaseReads;

            public long ExtractMs;
            public long CloneMs;
            public long TrimMs;
            public long ApplyMs;
            public long SaveMs;
        }

        public static bool ExecuteTransfer(IAxisVMApplication axisApp, IAxisVMModel sourceModel, ReactionTransferSettings settings)
        {
            string originalFile = sourceModel.FileName;
            if (string.IsNullOrEmpty(originalFile))
            {
                MessageBox.Show("Please save your current source model before running this transfer.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // Refuse to "clone" onto the source file - that would destroy the model we are reading from.
            if (PathsMatch(originalFile, settings.TargetFilePath))
            {
                MessageBox.Show("The target file is the same as the current model file.\nChoose a different target path.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Save the original file to ensure we don't lose anything
            sourceModel.SaveToFile(originalFile, ELongBoolean.lbFalse);

            Stats stats = new Stats();
            List<string> errors = new List<string>();
            MemoryData mem = new MemoryData();
            Stopwatch sw = new Stopwatch();

            // STEP 1: EXTRACT SELECTION AND REACTIONS INTO MEMORY (read-only, no BeginUpdate needed)
            try
            {
                sw.Restart();
                ExtractSelection(sourceModel, mem);
                ExtractSupportsAndReactions(sourceModel, mem, settings, stats, errors);
                sw.Stop();
                stats.ExtractMs = sw.ElapsedMilliseconds;

                if (mem.Reactions.Count == 0)
                {
                    MessageBox.Show(
                        "Zero reactions were extracted.\n\n" +
                        "Check that the analysis has been run in the source model and that the selection contains nodal supports.\n\n" +
                        "Selected nodes: " + mem.SelectedNodes.Count + "\n" +
                        "Selected supports: " + mem.SelectedSupports.Count + "\n" +
                        "Supports matched by the selection: " + mem.Supports.Count,
                        "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error during reaction extraction:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // STEP 2: CLONE MODEL TO TARGET PATH
            // SaveToFile creates a full clone of the file, preserving geometry, properties and load cases.
            // AxisVM keeps a single model instance, so afterwards the in-memory model IS the target file.
            try
            {
                sw.Restart();
                sourceModel.SaveToFile(settings.TargetFilePath, ELongBoolean.lbFalse);
                sw.Stop();
                stats.CloneMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save target model:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            IAxisVMModel targetModel = sourceModel;

            // STEP 3: TRIM UNSELECTED ELEMENTS AND APPLY REACTIONS
            bool updating = false;
            try
            {
                targetModel.BeginUpdate();
                updating = true;

                sw.Restart();
                TrimUnselected(targetModel, mem, settings);
                sw.Stop();
                stats.TrimMs = sw.ElapsedMilliseconds;

                sw.Restart();
                ApplyReactions(targetModel, mem, settings, stats);
                sw.Stop();
                stats.ApplyMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error trimming data or applying loads in target model:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                if (updating)
                {
                    try { targetModel.EndUpdate(); } catch { }
                    try { targetModel.Refresh(); } catch { }
                }
            }

            try
            {
                sw.Restart();
                targetModel.SaveToFile(settings.TargetFilePath, ELongBoolean.lbFalse);
                sw.Stop();
                stats.SaveMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Loads were applied but the target model could not be saved:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            int totalApplied = stats.NodeLoads + stats.BeamLoads;
            ShowSummary(settings, stats, errors, totalApplied);
            return totalApplied > 0;
        }

        private static bool PathsMatch(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(
                    System.IO.Path.GetFullPath(a).TrimEnd('\\'),
                    System.IO.Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void ShowSummary(ReactionTransferSettings settings, Stats stats, List<string> errors, int totalApplied)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(totalApplied > 0
                ? string.Format("Transferred {0} point loads.", totalApplied)
                : "Transfer completed, but no loads were applied.");
            sb.AppendLine();
            sb.AppendLine("Target file: " + settings.TargetFilePath);
            sb.AppendLine();
            sb.AppendLine("Reactions read:          " + stats.ReactionsRead);
            sb.AppendLine("Nodal point loads:       " + stats.NodeLoads);
            sb.AppendLine("Beam point loads:        " + stats.BeamLoads);

            if (stats.SkippedZero > 0)
                sb.AppendLine("Skipped (zero force):    " + stats.SkippedZero);
            if (stats.SkippedUnusableLoadCase > 0)
                sb.AppendLine("Skipped (self-weight LC):" + stats.SkippedUnusableLoadCase);
            if (stats.BeamFallbackToNode > 0)
                sb.AppendLine("No host beam, used node: " + stats.BeamFallbackToNode);
            if (stats.UnresolvedNodes > 0)
                sb.AppendLine("Nodes not found by UID:  " + stats.UnresolvedNodes);
            if (stats.FailedLoadCaseReads > 0)
                sb.AppendLine("Load cases with no results: " + stats.FailedLoadCaseReads);

            sb.AppendLine();
            sb.AppendFormat("Timing (ms): extract {0}, clone {1}, trim {2}, apply {3}, save {4}",
                stats.ExtractMs, stats.CloneMs, stats.TrimMs, stats.ApplyMs, stats.SaveMs);

            if (errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                int shown = Math.Min(errors.Count, 10);
                for (int i = 0; i < shown; i++) sb.AppendLine("  " + errors[i]);
                if (errors.Count > shown) sb.AppendLine("  ... and " + (errors.Count - shown) + " more");
            }

            MessageBox.Show(sb.ToString(), "Reaction Transfer",
                MessageBoxButtons.OK,
                totalApplied > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------
        // STEP 1 - extraction
        // ------------------------------------------------------------------

        private static void ExtractSelection(IAxisVMModel sourceModel, MemoryData mem)
        {
            Array selected;

            sourceModel.Nodes.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedNodes);

            sourceModel.Lines.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedLines);

            sourceModel.Members.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedMembers);

            sourceModel.Surfaces.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedSurfaces);

            sourceModel.Domains.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedDomains);

            if (sourceModel.LoadPanels != null)
            {
                for (int i = 1; i <= sourceModel.LoadPanels.Count; i++)
                {
                    if (sourceModel.LoadPanels.get_Selected(i) == AxisVM.ELongBoolean.lbTrue) mem.SelectedLoadPanels.Add(i);
                }
            }

            sourceModel.NodalSupports.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedSupports);

            sourceModel.LinkElements.GetSelectedItemIds(out selected);
            AddAll(selected, mem.SelectedLinkElements);
        }

        private static void AddAll(Array source, HashSet<int> target)
        {
            if (source == null) return;
            foreach (object obj in source) target.Add(Convert.ToInt32(obj));
        }

        /// <summary>
        /// Collects the selected nodal supports, then reads their reactions.
        /// Reactions are read with one bulk call per load case (GetAllNodalSupportForcesByLoadCaseId)
        /// rather than one call per support per load case.
        /// </summary>
        private static void ExtractSupportsAndReactions(IAxisVMModel sourceModel, MemoryData mem, ReactionTransferSettings settings, Stats stats, List<string> errors)
        {
            int supportCount = sourceModel.NodalSupports.Count;

            // Nodal support indices are dense 1..Count - no sparse scan needed.
            for (int i = 1; i <= supportCount; i++)
            {
                int nodeId;
                try { nodeId = sourceModel.NodalSupports.Item[i].NodeId; }
                catch { continue; }

                if (!mem.SelectedNodes.Contains(nodeId) && !mem.SelectedSupports.Contains(i)) continue;

                int nodeUid = 0;
                try { nodeUid = sourceModel.Nodes.get_UID(nodeId); } catch { }
                if (nodeUid <= 0) continue;

                mem.Supports.Add(new MemSupport { SupportIndex = i, NodeUID = nodeUid });
                mem.KeepSupportNodeUIDs.Add(nodeUid);
            }

            if (mem.Supports.Count == 0) return;

            AxisVM.EAnalysisType analysisType = (settings.ResultType == 1)
                ? AxisVM.EAnalysisType.atNonLinearStatic
                : AxisVM.EAnalysisType.atLinearStatic;

            if (settings.ResultType == 2)
            {
                // Envelope: one bulk call for the whole model. LoadCaseId is assigned later,
                // when the dedicated envelope load case has been created in the target.
                Array forcesArr = null;
                int read = 0;
                try { read = sourceModel.Results.Forces.GetAllEnvelopeNodalSupportForces(settings.EnvelopeMinMax, analysisType, settings.EnvelopeComponent, out forcesArr); }
                catch (Exception ex) { errors.Add("Envelope read failed: " + ex.Message); }

                int harvested = 0;
                if (read > 0 && forcesArr != null) harvested = HarvestBulk(forcesArr, mem, 0, stats);

                if (harvested == 0)
                {
                    errors.Add("Bulk envelope read returned no usable data; falling back to per-support reads.");
                    foreach (MemSupport s in mem.Supports)
                    {
                        AxisVM.RNodalSupportForceValues f = new AxisVM.RNodalSupportForceValues();
                        string err;
                        int ok = 0;
                        try { ok = sourceModel.Results.Forces.GetEnvelopeNodalSupportForce(s.SupportIndex, settings.EnvelopeMinMax, analysisType, settings.EnvelopeComponent, ref f, out err); }
                        catch { }
                        if (ok > 0)
                        {
                            mem.Reactions.Add(new MemReaction { SourceNodeUID = s.NodeUID, LoadCaseId = 0, Forces = f });
                            stats.ReactionsRead++;
                        }
                    }
                }
                return;
            }

            int loadCaseCount = sourceModel.LoadCases.Count;
            for (int lc = 1; lc <= loadCaseCount; lc++)
            {
                Array forcesArr = null;
                int read = 0;
                try { read = sourceModel.Results.Forces.GetAllNodalSupportForcesByLoadCaseId(lc, 0, analysisType, out forcesArr); }
                catch (Exception ex) { errors.Add(string.Format("Load case {0} read failed: {1}", lc, ex.Message)); }

                if (read > 0 && forcesArr != null && HarvestBulk(forcesArr, mem, lc, stats) > 0) continue;

                // Bulk read unavailable or unusable for this load case - fall back to per-support reads.
                int before = mem.Reactions.Count;
                foreach (MemSupport s in mem.Supports)
                {
                    AxisVM.RNodalSupportForceValues f = new AxisVM.RNodalSupportForceValues();
                    string err;
                    int ok = 0;
                    try { ok = sourceModel.Results.Forces.GetNodalSupportForceByLoadCaseId(s.SupportIndex, lc, 0, analysisType, ref f, out err); }
                    catch { }
                    if (ok > 0)
                    {
                        mem.Reactions.Add(new MemReaction { SourceNodeUID = s.NodeUID, LoadCaseId = lc, Forces = f });
                        stats.ReactionsRead++;
                    }
                }
                if (mem.Reactions.Count == before) stats.FailedLoadCaseReads++;
            }
        }

        /// <summary>
        /// Picks the entries belonging to the selected supports out of a full-model bulk result array.
        /// The array holds one record per nodal support, ordered by support index.
        /// Returns how many reactions were harvested, so the caller can fall back if the shape is unexpected.
        /// </summary>
        private static int HarvestBulk(Array forcesArr, MemoryData mem, int loadCaseId, Stats stats)
        {
            int lower = forcesArr.GetLowerBound(0);
            int upper = forcesArr.GetUpperBound(0);
            int harvested = 0;

            foreach (MemSupport s in mem.Supports)
            {
                int idx = lower + (s.SupportIndex - 1);
                if (idx < lower || idx > upper) continue;

                object raw;
                try { raw = forcesArr.GetValue(idx); } catch { continue; }
                if (!(raw is AxisVM.RNodalSupportForceValues)) continue;

                mem.Reactions.Add(new MemReaction
                {
                    SourceNodeUID = s.NodeUID,
                    LoadCaseId = loadCaseId,
                    Forces = (AxisVM.RNodalSupportForceValues)raw
                });
                stats.ReactionsRead++;
                harvested++;
            }

            return harvested;
        }

        // ------------------------------------------------------------------
        // STEP 3a - trimming
        // ------------------------------------------------------------------

        /// <summary>
        /// Deletes everything the user did not select.
        /// Everything is removed with a single DeleteSelected() call per collection: marking items and
        /// deleting once avoids both the per-item Delete cost and the index-shift bugs that come with
        /// comparing pre-deletion indices against a collection that renumbers itself as you delete.
        /// </summary>
        private static void TrimUnselected(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings)
        {
            // Protect sub-elements (lines/nodes) of selected top-level items so they don't get deleted
            // and break the members. The target is still a byte-for-byte clone here, so source indices apply.
            // "harvested" stops us reading the same line's end nodes twice when several owners share it.
            HashSet<int> harvested = new HashSet<int>();

            foreach (int mId in mem.SelectedMembers)
            {
                try
                {
                    Array mLines; targetModel.Members.Item[mId].GetLines(out mLines);
                    KeepLinesAndTheirNodes(targetModel, mLines, mem, harvested);
                }
                catch { }
            }
            foreach (int dId in mem.SelectedDomains)
            {
                try { KeepLinesAndTheirNodes(targetModel, targetModel.Domains.Item[dId].ContourLines, mem, harvested); }
                catch { }
            }
            foreach (int sId in mem.SelectedSurfaces)
            {
                try { KeepLinesAndTheirNodes(targetModel, targetModel.Surfaces.Item[sId].ContourLines, mem, harvested); }
                catch { }
            }
            if (targetModel.LoadPanels != null)
            {
                foreach (int lpId in mem.SelectedLoadPanels)
                {
                    try
                    {
                        Array lpLines; targetModel.LoadPanels.Item[lpId].GetLines(out lpLines);
                        KeepLinesAndTheirNodes(targetModel, lpLines, mem, harvested);
                    }
                    catch { }
                }
            }
            // Lines selected directly by the user still need their end nodes protected.
            foreach (int lId in new List<int>(mem.SelectedLines))
            {
                if (!harvested.Add(lId)) continue;
                try
                {
                    var line = targetModel.Lines.Item[lId];
                    mem.SelectedNodes.Add(line.StartNode);
                    mem.SelectedNodes.Add(line.EndNode);
                }
                catch { }
            }

            // Design members and virtual beams lock geometry - clear them outright.
            try { if (targetModel.SteelDesignMembers != null) targetModel.SteelDesignMembers.Clear(); } catch { }
            try { if (targetModel.TimberDesignMembers != null) targetModel.TimberDesignMembers.Clear(); } catch { }
            try { if (targetModel.VirtualBeams != null) targetModel.VirtualBeams.Clear(); } catch { }

            // Order matters: top-level elements first, then lines, then nodes. Deleting a node cascades
            // to its lines, and every kept line's end nodes are already in SelectedNodes, so the cascade is safe.
            if (targetModel.LinkElements != null)
                DeleteAllExcept(targetModel.LinkElements.Count, mem.SelectedLinkElements,
                    targetModel.LinkElements.set_Selected,
                    v => targetModel.LinkElements.SelectAll(v),
                    targetModel.LinkElements.DeleteSelected);

            if (targetModel.Domains != null)
                DeleteAllExcept(targetModel.Domains.Count, mem.SelectedDomains,
                    targetModel.Domains.set_Selected,
                    null, // IAxisVMDomains has no plain SelectAll; DeleteAllExcept falls back to a per-item pass
                    targetModel.Domains.DeleteSelected);

            if (targetModel.Surfaces != null)
                DeleteAllExcept(targetModel.Surfaces.Count, mem.SelectedSurfaces,
                    targetModel.Surfaces.set_Selected,
                    v => targetModel.Surfaces.SelectAll(v),
                    targetModel.Surfaces.DeleteSelected);

            if (targetModel.Members != null)
                DeleteAllExcept(targetModel.Members.Count, mem.SelectedMembers,
                    targetModel.Members.set_Selected,
                    v => targetModel.Members.SelectAll(v),
                    targetModel.Members.DeleteSelected);

            if (targetModel.LoadPanels != null)
                DeleteAllExcept(targetModel.LoadPanels.Count, mem.SelectedLoadPanels,
                    targetModel.LoadPanels.set_Selected,
                    v => targetModel.LoadPanels.SelectAll(v),
                    targetModel.LoadPanels.DeleteSelected);

            if (targetModel.Lines != null)
                DeleteAllExcept(targetModel.Lines.Count, mem.SelectedLines,
                    targetModel.Lines.set_Selected,
                    v => targetModel.Lines.SelectAll(v),
                    targetModel.Lines.DeleteSelected);

            if (targetModel.Nodes != null)
                DeleteNodesExcept(targetModel, mem.SelectedNodes);

            TrimNodalSupports(targetModel, mem, settings);
        }

        private static void KeepLinesAndTheirNodes(IAxisVMModel targetModel, Array lineIds, MemoryData mem, HashSet<int> harvested)
        {
            if (lineIds == null) return;
            foreach (object obj in lineIds)
            {
                int lid = Convert.ToInt32(obj);
                mem.SelectedLines.Add(lid);
                if (!harvested.Add(lid)) continue;
                try
                {
                    var line = targetModel.Lines.Item[lid];
                    mem.SelectedNodes.Add(line.StartNode);
                    mem.SelectedNodes.Add(line.EndNode);
                }
                catch { }
            }
        }

        /// <summary>
        /// Marks every item except <paramref name="keep"/> and removes them with one DeleteSelected call.
        /// <paramref name="selectAll"/> may be null for collections that have no SelectAll (Domains);
        /// in that case selection is set item by item.
        /// </summary>
        private static void DeleteAllExcept(int count, HashSet<int> keep, Action<int, ELongBoolean> setSelected, Action<ELongBoolean> selectAll, Func<int> deleteSelected)
        {
            if (count <= 0) return;

            if (selectAll != null && keep.Count == 0)
            {
                // Nothing to keep: two COM calls instead of one per item.
                try { selectAll(ELongBoolean.lbTrue); } catch { }
                try { deleteSelected(); } catch { }
                return;
            }

            if (selectAll != null) { try { selectAll(ELongBoolean.lbFalse); } catch { } }

            for (int i = 1; i <= count; i++)
            {
                ELongBoolean want = keep.Contains(i) ? ELongBoolean.lbFalse : ELongBoolean.lbTrue;
                // With selectAll available everything is already deselected, so only mark the doomed ones.
                if (selectAll == null || want == ELongBoolean.lbTrue)
                {
                    try { setSelected(i, want); } catch { }
                }
            }

            try { deleteSelected(); } catch { }
        }

        /// <summary>Nodes support BulkSelect/BulkDelete, which is the fastest path of all.</summary>
        private static void DeleteNodesExcept(IAxisVMModel targetModel, HashSet<int> keep)
        {
            int count = targetModel.Nodes.Count;
            if (count <= 0) return;

            List<int> doomed = new List<int>(count);
            for (int i = 1; i <= count; i++)
                if (!keep.Contains(i)) doomed.Add(i);

            if (doomed.Count == 0) return;

            try
            {
                if (targetModel.Nodes.BulkDelete(doomed.ToArray()) > 0) return;
            }
            catch { }

            // Fallback: select then delete in one call.
            try { targetModel.Nodes.SelectAll(ELongBoolean.lbFalse); } catch { }
            try { targetModel.Nodes.BulkSelect(doomed.ToArray(), ELongBoolean.lbTrue); }
            catch
            {
                foreach (int id in doomed) { try { targetModel.Nodes.set_Selected(id, ELongBoolean.lbTrue); } catch { } }
            }
            try { targetModel.Nodes.DeleteSelected(); } catch { }
        }

        /// <summary>
        /// Nodal supports have neither UID nor SelectAll, so they are matched through the UID of the node
        /// they sit on. Deleting a node already removes its support, so by this point only supports on kept
        /// nodes remain - we just have to drop the ones the user did not ask to keep.
        /// </summary>
        private static void TrimNodalSupports(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings)
        {
            if (targetModel.NodalSupports == null) return;

            int count = targetModel.NodalSupports.Count;
            if (count <= 0) return;

            bool anyMarked = false;
            for (int i = 1; i <= count; i++)
            {
                bool doomed = true;

                if (settings.TransferSupports)
                {
                    try
                    {
                        int nodeId = targetModel.NodalSupports.Item[i].NodeId;
                        int uid = targetModel.Nodes.get_UID(nodeId);
                        doomed = !mem.KeepSupportNodeUIDs.Contains(uid);
                    }
                    catch { doomed = true; }
                }

                try { targetModel.NodalSupports.set_Selected(i, doomed ? ELongBoolean.lbTrue : ELongBoolean.lbFalse); }
                catch { continue; }

                if (doomed) anyMarked = true;
            }

            if (anyMarked)
            {
                try { targetModel.NodalSupports.DeleteSelected(); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // STEP 3b - applying the reactions
        // ------------------------------------------------------------------

        /// <summary>Cached beam host for a node: which line to hang a concentrated load on, and where.</summary>
        struct BeamTarget
        {
            public int LineId;      // <= 0 means "no usable line at this node"
            public double Position; // distance from the line's start node, in metres
        }

        private static int ApplyReactions(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings, Stats stats)
        {
            // Node indices shifted during trimming, so every reaction is resolved through its node UID.
            Dictionary<int, int> uidToNode = BuildNodeUidMap(targetModel);

            // Load cases whose type forbids adding loads (self weight is generated by AxisVM itself).
            HashSet<int> unusableLoadCases = FindUnusableLoadCases(targetModel);

            // Envelope results go into their own load case so no real load case is contaminated.
            int envelopeLoadCase = 0;
            if (settings.ResultType == 2)
            {
                envelopeLoadCase = EnsureEnvelopeLoadCase(targetModel);
                if (envelopeLoadCase <= 0) return 0;
            }

            Dictionary<int, BeamTarget> beamTargets = new Dictionary<int, BeamTarget>();
            List<AxisVM.RLoadNodalForce> nodalBatch = new List<AxisVM.RLoadNodalForce>();

            foreach (MemReaction r in mem.Reactions)
            {
                int targetLc = (settings.ResultType == 2) ? envelopeLoadCase : r.LoadCaseId;
                if (unusableLoadCases.Contains(targetLc)) { stats.SkippedUnusableLoadCase++; continue; }

                if (IsNegligible(r.Forces)) { stats.SkippedZero++; continue; }

                int targetNodeId;
                if (!uidToNode.TryGetValue(r.SourceNodeUID, out targetNodeId)) { stats.UnresolvedNodes++; continue; }

                if (settings.ApplyAsBeamPointLoads)
                {
                    BeamTarget bt;
                    if (!beamTargets.TryGetValue(targetNodeId, out bt))
                    {
                        bt = FindBeamTarget(targetModel, targetNodeId);
                        beamTargets[targetNodeId] = bt;
                    }

                    if (bt.LineId > 0)
                    {
                        AxisVM.RLoadBeamConcentrated beamLoad = new AxisVM.RLoadBeamConcentrated();
                        beamLoad.LineId = bt.LineId;
                        beamLoad.LoadCaseId = targetLc;
                        beamLoad.Fgx = r.Forces.Rx;
                        beamLoad.Fgy = r.Forces.Ry;
                        beamLoad.Fgz = r.Forces.Rz;
                        beamLoad.Mgx = r.Forces.Rxx;
                        beamLoad.Mgy = r.Forces.Ryy;
                        beamLoad.Mgz = r.Forces.Rzz;
                        beamLoad.Position = bt.Position;
                        beamLoad.SystemGLR = AxisVM.ESystem.sysGlobal;

                        bool ok = false;
                        try { ok = targetModel.Loads.AddBeamConcentrated(ref beamLoad) > 0; } catch { }
                        if (ok) { stats.BeamLoads++; continue; }
                    }

                    // No host line, or AxisVM rejected the beam load - fall back to a nodal load.
                    stats.BeamFallbackToNode++;
                }

                AxisVM.RLoadNodalForce nodalLoad = new AxisVM.RLoadNodalForce();
                nodalLoad.NodeId = targetNodeId;
                nodalLoad.LoadCaseId = targetLc;
                nodalLoad.Fx = r.Forces.Rx;
                nodalLoad.Fy = r.Forces.Ry;
                nodalLoad.Fz = r.Forces.Rz;
                nodalLoad.Mx = r.Forces.Rxx;
                nodalLoad.My = r.Forces.Ryy;
                nodalLoad.Mz = r.Forces.Rzz;
                nodalBatch.Add(nodalLoad);
            }

            stats.NodeLoads = AddNodalForceBatch(targetModel, nodalBatch);
            return stats.NodeLoads + stats.BeamLoads;
        }

        private static bool IsNegligible(AxisVM.RNodalSupportForceValues f)
        {
            return Math.Abs(f.Rx) < ZeroForceEpsilon
                && Math.Abs(f.Ry) < ZeroForceEpsilon
                && Math.Abs(f.Rz) < ZeroForceEpsilon
                && Math.Abs(f.Rxx) < ZeroForceEpsilon
                && Math.Abs(f.Ryy) < ZeroForceEpsilon
                && Math.Abs(f.Rzz) < ZeroForceEpsilon;
        }

        /// <summary>Reads every node UID once instead of calling IndexOfUID per reaction.</summary>
        private static Dictionary<int, int> BuildNodeUidMap(IAxisVMModel targetModel)
        {
            int count = targetModel.Nodes.Count;
            Dictionary<int, int> map = new Dictionary<int, int>(count);
            if (count <= 0) return map;

            int[] indices = new int[count];
            for (int i = 0; i < count; i++) indices[i] = i + 1;

            Array uids = null;
            int read = 0;
            try { read = targetModel.Nodes.BulkGetUIDs(indices, out uids); } catch { }

            if (read > 0 && uids != null)
            {
                int lower = uids.GetLowerBound(0);
                int upper = uids.GetUpperBound(0);
                for (int i = 0; i < count && lower + i <= upper; i++)
                {
                    int uid = Convert.ToInt32(uids.GetValue(lower + i));
                    if (uid > 0) map[uid] = i + 1;
                }
                return map;
            }

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    int uid = targetModel.Nodes.get_UID(i);
                    if (uid > 0) map[uid] = i;
                }
                catch { }
            }
            return map;
        }

        /// <summary>AxisVM generates self-weight loads itself and rejects loads added to such cases.</summary>
        private static HashSet<int> FindUnusableLoadCases(IAxisVMModel targetModel)
        {
            HashSet<int> result = new HashSet<int>();
            try
            {
                for (int i = 1; i <= targetModel.LoadCases.Count; i++)
                {
                    if (targetModel.LoadCases.get_LoadCaseType(i) == AxisVM.ELoadCaseType.lctSelfWeight) result.Add(i);
                }
            }
            catch { }
            return result;
        }

        private static int EnsureEnvelopeLoadCase(IAxisVMModel targetModel)
        {
            try
            {
                for (int i = 1; i <= targetModel.LoadCases.Count; i++)
                {
                    if (string.Equals(targetModel.LoadCases.get_Name(i), EnvelopeLoadCaseName, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            catch { }

            try { return targetModel.LoadCases.Add(EnvelopeLoadCaseName, AxisVM.ELoadCaseType.lctStandard); }
            catch { return 0; }
        }

        /// <summary>
        /// Finds the line that a concentrated load for this node should sit on.
        /// Only lines actually connected to the node are examined (GetConnectedLines), and beams win over
        /// columns so a column-base reaction lands on the foundation beam rather than the column above it.
        /// </summary>
        private static BeamTarget FindBeamTarget(IAxisVMModel targetModel, int nodeId)
        {
            BeamTarget result = new BeamTarget { LineId = 0, Position = 0 };
            if (targetModel.Lines == null) return result;

            Array connected = null;
            try { targetModel.Nodes.GetConnectedLines(nodeId, out connected); } catch { }
            if (connected == null) return result;

            bool haveBeam = false;

            foreach (object obj in connected)
            {
                int lid = Convert.ToInt32(obj);
                if (lid <= 0) continue;

                try
                {
                    var line = targetModel.Lines.Item[lid];

                    double position;
                    if (line.StartNode == nodeId) position = 0;
                    else if (line.EndNode == nodeId) position = line.Length;
                    else continue; // node sits mid-line; a concentrated load there needs no special casing here

                    bool isBeam = line.LineType == AxisVM.ELineType.ltBeam;

                    // Prefer beams; among equals take the lowest line id so the result is deterministic.
                    if (isBeam && !haveBeam)
                    {
                        result.LineId = lid; result.Position = position; haveBeam = true;
                    }
                    else if (isBeam == haveBeam && (result.LineId == 0 || lid < result.LineId))
                    {
                        result.LineId = lid; result.Position = position;
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>Adds all nodal forces in one COM call, falling back to individual adds if that fails.</summary>
        private static int AddNodalForceBatch(IAxisVMModel targetModel, List<AxisVM.RLoadNodalForce> batch)
        {
            if (batch.Count == 0) return 0;

            Array createdIds = null;
            try
            {
                int created = targetModel.Loads.AddNodalForces(batch.ToArray(), out createdIds);
                if (created > 0) return created;
            }
            catch { }

            int applied = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                AxisVM.RLoadNodalForce load = batch[i];
                try { if (targetModel.Loads.AddNodalForce(ref load) > 0) applied++; } catch { }
            }
            return applied;
        }
    }
}
