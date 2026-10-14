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

        /// <summary>
        /// False: clone the source to the target path and trim it (the original behaviour).
        /// True: open the target model as it is and add the reactions to it.
        /// </summary>
        public bool ModifyExistingFile { get; set; }

        public bool TransferSupports { get; set; }
        public int ResultType { get; set; } // 0=Linear, 1=NonLinear, 2=Envelope

        /// <summary>Coordinate tolerance when matching a reaction onto a node of an existing target model, in metres.</summary>
        public double NodeMatchToleranceM { get; set; }

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

        /// <summary>One extracted reaction.</summary>
        class MemReaction
        {
            /// <summary>Node UID in the source model. Used by the clone path, where the target IS the source.</summary>
            public int SourceNodeUID;

            /// <summary>Node coordinates. Used by the existing-file path, where UIDs mean nothing across models.</summary>
            public double X, Y, Z;

            public int LoadCaseId;
            public AxisVM.RNodalSupportForceValues Forces;
        }

        /// <summary>A nodal support in the source model that the user selected.</summary>
        class MemSupport
        {
            public int SupportIndex;  // index in the source model's NodalSupports collection
            public int NodeUID;       // UID of the supported node (survives trimming)
            public double X, Y, Z;    // node coordinates (survive being copied into a different model)
        }

        /// <summary>A load case of the source model, remembered by name so it can be found again in a foreign target.</summary>
        class SourceLoadCase
        {
            public string Name;
            public AxisVM.ELoadCaseType Type;
            public int GroupIndex; // index into MemoryData.LoadGroups, 0 when the case has no group
        }

        /// <summary>A load group of the source model, with enough detail to recreate it faithfully.</summary>
        class SourceLoadGroup
        {
            public string Name;
            public AxisVM.ELoadGroupType Type;
            public AxisVM.ELongBoolean SimultExc;
            public AxisVM.EGroupCombinationType CombinationType;
            public double GammaSup, GammaInf, Psi0, Psi1, Psi2;
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

            /// <summary>Source load cases by index, and the groups they belong to.</summary>
            public Dictionary<int, SourceLoadCase> LoadCases = new Dictionary<int, SourceLoadCase>();
            public Dictionary<int, SourceLoadGroup> LoadGroups = new Dictionary<int, SourceLoadGroup>();
        }

        /// <summary>Counters gathered during the run so the user gets one informative summary instead of six dialogs.</summary>
        class Stats
        {
            public int ReactionsRead;
            public int NodeLoads;
            public int SkippedZero;
            public int SkippedUnusableLoadCase;
            public int SkippedNoLoadCase;
            public int UnresolvedNodes;
            public int FailedLoadCaseReads;

            // Existing-file mode only
            public int NodesMatched;
            public int NodesCreated;
            public int LoadCasesMatched;
            public int LoadCasesCreated;
            public int LoadGroupsMatched;
            public int LoadGroupsCreated;
            public string BackupPath;

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

            // Refuse to write onto the source file - that would destroy or self-load the model we are reading from.
            if (PathsMatch(originalFile, settings.TargetFilePath))
            {
                MessageBox.Show("The target file is the same as the current model file.\nChoose a different target path.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (settings.ModifyExistingFile && !System.IO.File.Exists(settings.TargetFilePath))
            {
                MessageBox.Show("The target file does not exist:\n\n" + settings.TargetFilePath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Save the original file to ensure we don't lose anything. This matters even more in
            // existing-file mode, where LoadFromFile replaces the in-memory model outright.
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
                ExtractLoadCaseCatalogue(sourceModel, mem, errors);
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

            bool ok = settings.ModifyExistingFile
                ? TransferIntoExistingModel(sourceModel, mem, settings, stats, errors, sw)
                : TransferIntoClone(sourceModel, mem, settings, stats, errors, sw);

            if (!ok) return false;

            ShowSummary(settings, stats, errors);
            return stats.NodeLoads > 0;
        }

        // ------------------------------------------------------------------
        // Mode A - clone the source, trim it, apply the reactions
        // ------------------------------------------------------------------

        private static bool TransferIntoClone(IAxisVMModel sourceModel, MemoryData mem, ReactionTransferSettings settings, Stats stats, List<string> errors, Stopwatch sw)
        {
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
                ApplyReactionsByUID(targetModel, mem, settings, stats);
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

            return SaveTarget(targetModel, settings, stats);
        }

        // ------------------------------------------------------------------
        // Mode B - open an existing model and add to it
        // ------------------------------------------------------------------

        private static bool TransferIntoExistingModel(IAxisVMModel model, MemoryData mem, ReactionTransferSettings settings, Stats stats, List<string> errors, Stopwatch sw)
        {
            // Back the target up before touching it - this mode writes into a file the user cares about
            // rather than one it just created.
            try
            {
                stats.BackupPath = MakeBackup(settings.TargetFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not back up the target model, so nothing was changed:\n\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                sw.Restart();
                if (model.LoadFromFile(settings.TargetFilePath) != ELongBoolean.lbTrue)
                {
                    MessageBox.Show("AxisVM could not open the target model:\n\n" + settings.TargetFilePath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                sw.Stop();
                stats.CloneMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open the target model:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            bool updating = false;
            try
            {
                model.BeginUpdate();
                updating = true;

                sw.Restart();
                ApplyReactionsByCoordinate(model, mem, settings, stats, errors);
                sw.Stop();
                stats.ApplyMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error applying loads in the target model:\n\n" + ex +
                                "\n\nA backup of the original target file is at:\n" + stats.BackupPath,
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                if (updating)
                {
                    try { model.EndUpdate(); } catch { }
                    try { model.Refresh(); } catch { }
                }
            }

            return SaveTarget(model, settings, stats);
        }

        private static bool SaveTarget(IAxisVMModel targetModel, ReactionTransferSettings settings, Stats stats)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                targetModel.SaveToFile(settings.TargetFilePath, ELongBoolean.lbFalse);
                sw.Stop();
                stats.SaveMs = sw.ElapsedMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Loads were applied but the target model could not be saved:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>Copies the target next to itself as a timestamped .axs, so the user can just open it if the run goes wrong.</summary>
        private static string MakeBackup(string path)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            string backup = System.IO.Path.Combine(dir, string.Format("{0}_backup_{1:yyyyMMdd_HHmmss}{2}", name, DateTime.Now, ext));

            System.IO.File.Copy(path, backup, false);
            return backup;
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

        private static void ShowSummary(ReactionTransferSettings settings, Stats stats, List<string> errors)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(stats.NodeLoads > 0
                ? string.Format("Transferred {0} nodal point loads.", stats.NodeLoads)
                : "Transfer completed, but no loads were applied.");
            sb.AppendLine();
            sb.AppendLine("Mode:        " + (settings.ModifyExistingFile ? "Modify existing file" : "Create new file"));
            sb.AppendLine("Target file: " + settings.TargetFilePath);
            if (!string.IsNullOrEmpty(stats.BackupPath))
                sb.AppendLine("Backup:      " + stats.BackupPath);
            sb.AppendLine();
            sb.AppendLine("Reactions read:          " + stats.ReactionsRead);
            sb.AppendLine("Nodal point loads:       " + stats.NodeLoads);

            if (settings.ModifyExistingFile)
            {
                sb.AppendLine("Nodes reused / created:  " + stats.NodesMatched + " / " + stats.NodesCreated);
                sb.AppendLine("Load cases reused / new: " + stats.LoadCasesMatched + " / " + stats.LoadCasesCreated);
                sb.AppendLine("Load groups reused / new:" + stats.LoadGroupsMatched + " / " + stats.LoadGroupsCreated);
            }

            if (stats.SkippedZero > 0)
                sb.AppendLine("Skipped (zero force):    " + stats.SkippedZero);
            if (stats.SkippedUnusableLoadCase > 0)
                sb.AppendLine("Skipped (self-weight LC):" + stats.SkippedUnusableLoadCase);
            if (stats.SkippedNoLoadCase > 0)
                sb.AppendLine("Skipped (no load case):  " + stats.SkippedNoLoadCase);
            if (stats.UnresolvedNodes > 0)
                sb.AppendLine("Nodes that could not be resolved: " + stats.UnresolvedNodes);
            if (stats.FailedLoadCaseReads > 0)
                sb.AppendLine("Load cases with no results: " + stats.FailedLoadCaseReads);

            sb.AppendLine();
            sb.AppendFormat("Timing (ms): extract {0}, {1} {2}, trim {3}, apply {4}, save {5}",
                stats.ExtractMs, settings.ModifyExistingFile ? "open" : "clone", stats.CloneMs, stats.TrimMs, stats.ApplyMs, stats.SaveMs);

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
                stats.NodeLoads > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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
        /// Records the source model's load cases and load groups by name. The clone path never needs this -
        /// the target already has identical cases - but the existing-file path has to decide, per case,
        /// whether the target already has one by that name or a new one has to be created.
        /// </summary>
        private static void ExtractLoadCaseCatalogue(IAxisVMModel sourceModel, MemoryData mem, List<string> errors)
        {
            try
            {
                for (int i = 1; i <= sourceModel.LoadGroups.Count; i++)
                {
                    var g = sourceModel.LoadGroups.Item[i];
                    mem.LoadGroups[i] = new SourceLoadGroup
                    {
                        Name = g.Name,
                        Type = g.LoadGroupType,
                        SimultExc = g.SimultExc,
                        CombinationType = g.CombinationType,
                        GammaSup = g.GammaSup,
                        GammaInf = g.GammaInf,
                        Psi0 = g.Psi0,
                        Psi1 = g.Psi1,
                        Psi2 = g.Psi2
                    };
                }
            }
            catch (Exception ex) { errors.Add("Could not read the source load groups: " + ex.Message); }

            try
            {
                for (int i = 1; i <= sourceModel.LoadCases.Count; i++)
                {
                    mem.LoadCases[i] = new SourceLoadCase
                    {
                        Name = sourceModel.LoadCases.get_Name(i),
                        Type = sourceModel.LoadCases.get_LoadCaseType(i),
                        GroupIndex = sourceModel.LoadCases.get_GroupID(i)
                    };
                }
            }
            catch (Exception ex) { errors.Add("Could not read the source load cases: " + ex.Message); }
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

                AxisVM.RNode node = new AxisVM.RNode();
                try { sourceModel.Nodes.GetNode(nodeId, ref node); } catch { }

                mem.Supports.Add(new MemSupport { SupportIndex = i, NodeUID = nodeUid, X = node.x, Y = node.y, Z = node.z });
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
                        int okCode = 0;
                        try { okCode = sourceModel.Results.Forces.GetEnvelopeNodalSupportForce(s.SupportIndex, settings.EnvelopeMinMax, analysisType, settings.EnvelopeComponent, ref f, out err); }
                        catch { }
                        if (okCode > 0)
                        {
                            mem.Reactions.Add(NewReaction(s, 0, f));
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
                    int okCode = 0;
                    try { okCode = sourceModel.Results.Forces.GetNodalSupportForceByLoadCaseId(s.SupportIndex, lc, 0, analysisType, ref f, out err); }
                    catch { }
                    if (okCode > 0)
                    {
                        mem.Reactions.Add(NewReaction(s, lc, f));
                        stats.ReactionsRead++;
                    }
                }
                if (mem.Reactions.Count == before) stats.FailedLoadCaseReads++;
            }
        }

        private static MemReaction NewReaction(MemSupport s, int loadCaseId, AxisVM.RNodalSupportForceValues forces)
        {
            return new MemReaction
            {
                SourceNodeUID = s.NodeUID,
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                LoadCaseId = loadCaseId,
                Forces = forces
            };
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

                mem.Reactions.Add(NewReaction(s, loadCaseId, (AxisVM.RNodalSupportForceValues)raw));
                stats.ReactionsRead++;
                harvested++;
            }

            return harvested;
        }

        // ------------------------------------------------------------------
        // Mode A, step 2 - trimming
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
        // Applying the reactions
        // ------------------------------------------------------------------

        /// <summary>Clone path: the target started as a copy of the source, so nodes and load cases already line up.</summary>
        private static void ApplyReactionsByUID(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings, Stats stats)
        {
            // Node indices shifted during trimming, so every reaction is resolved through its node UID.
            Dictionary<int, int> uidToNode = BuildNodeUidMap(targetModel);

            // Load cases whose type forbids adding loads (self weight is generated by AxisVM itself).
            HashSet<int> unusableLoadCases = FindUnusableLoadCases(targetModel);

            // Envelope results go into their own load case so no real load case is contaminated.
            int envelopeLoadCase = 0;
            if (settings.ResultType == 2)
            {
                envelopeLoadCase = EnsureEnvelopeLoadCase(targetModel, stats);
                if (envelopeLoadCase <= 0) return;
            }

            List<AxisVM.RLoadNodalForce> batch = new List<AxisVM.RLoadNodalForce>();

            foreach (MemReaction r in mem.Reactions)
            {
                int targetLc = (settings.ResultType == 2) ? envelopeLoadCase : r.LoadCaseId;
                if (unusableLoadCases.Contains(targetLc)) { stats.SkippedUnusableLoadCase++; continue; }

                if (IsNegligible(r.Forces)) { stats.SkippedZero++; continue; }

                int targetNodeId;
                if (!uidToNode.TryGetValue(r.SourceNodeUID, out targetNodeId)) { stats.UnresolvedNodes++; continue; }

                batch.Add(MakeNodalForce(targetNodeId, targetLc, r.Forces));
            }

            stats.NodeLoads = AddNodalForceBatch(targetModel, batch);
        }

        /// <summary>
        /// Existing-file path: the target is an unrelated model, so everything is matched by identity
        /// rather than by index - nodes by coordinate, load cases and load groups by name. Anything that
        /// is not already there is created.
        /// </summary>
        private static void ApplyReactionsByCoordinate(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings, Stats stats, List<string> errors)
        {
            HashSet<int> unusableLoadCases = FindUnusableLoadCases(targetModel);

            int envelopeLoadCase = 0;
            if (settings.ResultType == 2)
            {
                envelopeLoadCase = EnsureEnvelopeLoadCase(targetModel, stats);
                if (envelopeLoadCase <= 0)
                {
                    errors.Add("Could not create the envelope load case in the target model.");
                    return;
                }
            }

            Dictionary<int, int> sourceLcToTargetLc = new Dictionary<int, int>();   // resolved on demand, -1 = unusable
            Dictionary<string, int> groupCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> nodeCache = new Dictionary<string, int>();

            List<AxisVM.RLoadNodalForce> batch = new List<AxisVM.RLoadNodalForce>();

            foreach (MemReaction r in mem.Reactions)
            {
                if (IsNegligible(r.Forces)) { stats.SkippedZero++; continue; }

                int targetLc;
                if (settings.ResultType == 2)
                {
                    targetLc = envelopeLoadCase;
                }
                else if (!sourceLcToTargetLc.TryGetValue(r.LoadCaseId, out targetLc))
                {
                    targetLc = ResolveLoadCase(targetModel, mem, r.LoadCaseId, unusableLoadCases, groupCache, stats, errors);
                    sourceLcToTargetLc[r.LoadCaseId] = targetLc;
                }

                if (targetLc == -1) { stats.SkippedUnusableLoadCase++; continue; }
                if (targetLc <= 0) { stats.SkippedNoLoadCase++; continue; }

                int nodeId = ResolveNode(targetModel, r, settings.NodeMatchToleranceM, nodeCache, stats);
                if (nodeId <= 0) { stats.UnresolvedNodes++; continue; }

                batch.Add(MakeNodalForce(nodeId, targetLc, r.Forces));
            }

            stats.NodeLoads = AddNodalForceBatch(targetModel, batch);
        }

        /// <summary>
        /// Finds the node of the target model that sits at the reaction's coordinates, creating it when
        /// the target has nothing there. Results are cached per coordinate, so the second load case's
        /// reaction at the same support reuses the node the first one resolved (or created).
        /// </summary>
        private static int ResolveNode(IAxisVMModel targetModel, MemReaction r, double toleranceM, Dictionary<string, int> cache, Stats stats)
        {
            string key = string.Format("{0:F6}|{1:F6}|{2:F6}", r.X, r.Y, r.Z);

            int cached;
            if (cache.TryGetValue(key, out cached)) return cached;

            int nodeId = 0;
            try
            {
                // IndexOf wants a start index inside 1..Count, so an empty model has to skip straight to Add.
                if (targetModel.Nodes.Count > 0) nodeId = targetModel.Nodes.IndexOf(r.X, r.Y, r.Z, toleranceM, 1);
            }
            catch { nodeId = 0; }

            if (nodeId > 0)
            {
                stats.NodesMatched++;
            }
            else
            {
                try { nodeId = targetModel.Nodes.Add(r.X, r.Y, r.Z); } catch { nodeId = 0; }
                if (nodeId > 0) stats.NodesCreated++;
            }

            cache[key] = nodeId;
            return nodeId;
        }

        /// <summary>
        /// Maps a source load case onto the target model by name. Returns the target index, -1 when the
        /// matching target case cannot take loads (self weight), or 0 when it could not be resolved.
        /// </summary>
        private static int ResolveLoadCase(IAxisVMModel targetModel, MemoryData mem, int sourceLc, HashSet<int> unusableLoadCases, Dictionary<string, int> groupCache, Stats stats, List<string> errors)
        {
            SourceLoadCase source;
            if (!mem.LoadCases.TryGetValue(sourceLc, out source) || string.IsNullOrEmpty(source.Name))
            {
                errors.Add("Source load case " + sourceLc + " has no name; its reactions were skipped.");
                return 0;
            }

            // A self-weight case in the source produces reactions, but no target case can receive them:
            // AxisVM generates self-weight loads itself and rejects additions.
            if (source.Type == AxisVM.ELoadCaseType.lctSelfWeight) return -1;

            int existing = FindLoadCaseByName(targetModel, source.Name);
            if (existing > 0)
            {
                stats.LoadCasesMatched++;
                return unusableLoadCases.Contains(existing) ? -1 : existing;
            }

            int groupId = ResolveLoadGroup(targetModel, mem, source.GroupIndex, groupCache, stats);

            int created = 0;
            try
            {
                created = (groupId > 0)
                    ? targetModel.LoadCases.AddWithGroup(source.Name, source.Type, groupId)
                    : targetModel.LoadCases.Add(source.Name, source.Type);
            }
            catch { created = 0; }

            // Some case types (snow, wind, seismic) are only creatable through their own generators.
            // A plain standard case under the same name still carries the reactions, which is the point.
            if (created <= 0 && source.Type != AxisVM.ELoadCaseType.lctStandard)
            {
                try
                {
                    created = (groupId > 0)
                        ? targetModel.LoadCases.AddWithGroup(source.Name, AxisVM.ELoadCaseType.lctStandard, groupId)
                        : targetModel.LoadCases.Add(source.Name, AxisVM.ELoadCaseType.lctStandard);
                }
                catch { created = 0; }

                if (created > 0)
                    errors.Add(string.Format("Load case '{0}' was recreated as a standard case; the target model could not take its original type.", source.Name));
            }

            if (created <= 0)
            {
                errors.Add(string.Format("Could not create load case '{0}' in the target model.", source.Name));
                return 0;
            }

            stats.LoadCasesCreated++;
            return created;
        }

        /// <summary>Matches the source load group by name, creating it (with the same parameters) when absent.</summary>
        private static int ResolveLoadGroup(IAxisVMModel targetModel, MemoryData mem, int sourceGroupIndex, Dictionary<string, int> cache, Stats stats)
        {
            SourceLoadGroup source;
            if (sourceGroupIndex <= 0 || !mem.LoadGroups.TryGetValue(sourceGroupIndex, out source) || string.IsNullOrEmpty(source.Name)) return 0;

            int cached;
            if (cache.TryGetValue(source.Name, out cached)) return cached;

            int result = 0;
            try
            {
                for (int i = 1; i <= targetModel.LoadGroups.Count; i++)
                {
                    if (string.Equals(targetModel.LoadGroups.Item[i].Name, source.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result = i;
                        stats.LoadGroupsMatched++;
                        break;
                    }
                }
            }
            catch { }

            if (result <= 0)
            {
                try { result = targetModel.LoadGroups.Add(source.Name, source.Type, source.SimultExc, source.CombinationType); }
                catch { result = 0; }

                if (result > 0)
                {
                    stats.LoadGroupsCreated++;
                    // Safety factors are not part of Add, so they are copied afterwards.
                    try
                    {
                        var g = targetModel.LoadGroups.Item[result];
                        g.GammaSup = source.GammaSup;
                        g.GammaInf = source.GammaInf;
                        g.Psi0 = source.Psi0;
                        g.Psi1 = source.Psi1;
                        g.Psi2 = source.Psi2;
                    }
                    catch { }
                }
            }

            cache[source.Name] = result;
            return result;
        }

        private static int FindLoadCaseByName(IAxisVMModel targetModel, string name)
        {
            try
            {
                for (int i = 1; i <= targetModel.LoadCases.Count; i++)
                {
                    if (string.Equals(targetModel.LoadCases.get_Name(i), name, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            catch { }
            return 0;
        }

        private static AxisVM.RLoadNodalForce MakeNodalForce(int nodeId, int loadCaseId, AxisVM.RNodalSupportForceValues f)
        {
            AxisVM.RLoadNodalForce load = new AxisVM.RLoadNodalForce();
            load.NodeId = nodeId;
            load.LoadCaseId = loadCaseId;
            load.Fx = f.Rx;
            load.Fy = f.Ry;
            load.Fz = f.Rz;
            load.Mx = f.Rxx;
            load.My = f.Ryy;
            load.Mz = f.Rzz;
            return load;
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

        private static int EnsureEnvelopeLoadCase(IAxisVMModel targetModel, Stats stats)
        {
            int existing = FindLoadCaseByName(targetModel, EnvelopeLoadCaseName);
            if (existing > 0)
            {
                stats.LoadCasesMatched++;
                return existing;
            }

            int created;
            try { created = targetModel.LoadCases.Add(EnvelopeLoadCaseName, AxisVM.ELoadCaseType.lctStandard); }
            catch { return 0; }

            if (created > 0) stats.LoadCasesCreated++;
            return created;
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
