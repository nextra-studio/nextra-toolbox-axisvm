using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public class ReactionTransferSettings
    {
        public string TargetFilePath { get; set; }
        public bool IsNewFile { get; set; }
        public bool TransferSelectedElements { get; set; }
        public bool TransferParts { get; set; }
        public bool TransferSupports { get; set; }
        public bool TransferBottomBeams { get; set; }
        public bool TransferLoadCases { get; set; }
        public bool TransferLoadGroups { get; set; }
        public bool TransferLoadCombinations { get; set; }
        public bool MergeLoads { get; set; }
        public bool ReplaceLoads { get; set; }
        public int ResultType { get; set; } // 0=Linear, 1=NonLinear, 2=Envelope
        public bool ApplyAsNodePointLoads { get; set; }
        public bool ApplyAsBeamPointLoads { get; set; }
        public bool TidyUpLines { get; set; }
    }

    public static class ReactionTransferLogic
    {
        class MemReaction { 
            public int SourceNodeId; 
            public int SourceNodeUID;
            public int LoadCaseId; 
            public AxisVM.RNodalSupportForceValues Forces; 
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
            
            public List<MemReaction> Reactions = new List<MemReaction>();
        }

        public static bool ExecuteTransfer(IAxisVMApplication axisApp, IAxisVMModel sourceModel, ReactionTransferSettings settings)
        {
            string originalFile = sourceModel.FileName;
            if (string.IsNullOrEmpty(originalFile))
            {
                MessageBox.Show("Please save your current source model before running this transfer.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            
            // Save the original file to ensure we don't lose anything
            sourceModel.SaveToFile(originalFile, ELongBoolean.lbFalse);

            List<string> errors = new List<string>();
            MemoryData mem = new MemoryData();

            try
            {
                // STEP 1: EXTRACT SELECTION AND REACTIONS INTO MEMORY
                ExtractSelection(sourceModel, mem);
                MessageBox.Show(string.Format("Extracted Selection:\nNodes: {0}\nLines: {1}\nMembers: {2}\nSurfaces: {3}\nDomains: {4}\nLoad Panels: {5}", 
                    mem.SelectedNodes.Count, mem.SelectedLines.Count, mem.SelectedMembers.Count, mem.SelectedSurfaces.Count, mem.SelectedDomains.Count, mem.SelectedLoadPanels.Count), 
                    "Debug - Extraction", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    
                ExtractSupportsAndReactions(sourceModel, mem, settings, errors);
                MessageBox.Show(string.Format("Extracted Reactions:\nTotal Reactions saved in memory: {0}", mem.Reactions.Count), 
                    "Debug - Reactions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                
                if (mem.Reactions.Count == 0)
                {
                    MessageBox.Show("Zero reactions were extracted. Did you run the analysis in the source model? Are there nodal supports in the selection?", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Error during reaction extraction: " + ex.ToString());
                MessageBox.Show("CRASH in ExtractSupportsAndReactions:\n" + ex.ToString(), "Debug Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (errors.Count > 0)
            {
                MessageBox.Show("Errors encountered during extraction:\n" + string.Join("\n", errors), "Warnings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // STEP 2: CLONE MODEL TO TARGET PATH
            try
            {
                // SaveToFile creates a 100% clone of the file, preserving all geometry, properties, and load cases!
                sourceModel.SaveToFile(settings.TargetFilePath, ELongBoolean.lbFalse);
                MessageBox.Show("Model cloned successfully to target file.", "Debug - Clone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                
                // AxisVM Singleton means the model we just saved is now the active window (Target File).
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save target model: " + ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // targetModel is now the active model (sourceModel variable points to it since COM object is Singleton instance)
            IAxisVMModel targetModel = sourceModel;

            // STEP 3: TRIM UNSELECTED ELEMENTS AND APPLY REACTIONS
            int totalReactionsTransferred = 0;
            try
            {
                targetModel.BeginUpdate();

                // Trim anything not selected
                TrimUnselected(targetModel, mem, settings);
                MessageBox.Show("TrimUnselected completed successfully.", "Debug - Trim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                
                // Apply the reactions as Point Loads
                totalReactionsTransferred = ApplyReactions(targetModel, mem, settings);
                MessageBox.Show("ApplyReactions completed successfully.", "Debug - Apply", MessageBoxButtons.OK, MessageBoxIcon.Information);

                targetModel.EndUpdate();
                targetModel.SaveToFile(settings.TargetFilePath, ELongBoolean.lbFalse);
            }
            catch (Exception ex)
            {
                targetModel.EndUpdate();
                MessageBox.Show("Error trimming data or applying loads in target model:\n" + ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Show success message, we leave the user in the New Target File
            if (totalReactionsTransferred > 0)
            {
                MessageBox.Show(string.Format("Successfully transferred {0} point loads!\nTarget file saved to: {1}", totalReactionsTransferred, settings.TargetFilePath), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            else
            {
                MessageBox.Show("Transfer completed but no reactions were applied to the target model.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private static void ExtractSelection(IAxisVMModel sourceModel, MemoryData mem)
        {
            Array selected;
            
            sourceModel.Nodes.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedNodes.Add(Convert.ToInt32(obj)); }

            sourceModel.Lines.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedLines.Add(Convert.ToInt32(obj)); }

            sourceModel.Members.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedMembers.Add(Convert.ToInt32(obj)); }

            sourceModel.Surfaces.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedSurfaces.Add(Convert.ToInt32(obj)); }

            sourceModel.Domains.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedDomains.Add(Convert.ToInt32(obj)); }

            if (sourceModel.LoadPanels != null) {
                for (int i = 1; i <= sourceModel.LoadPanels.Count; i++) {
                    try { if (sourceModel.LoadPanels.get_Selected(i) == AxisVM.ELongBoolean.lbTrue) mem.SelectedLoadPanels.Add(i); } catch {}
                }
            }

            sourceModel.NodalSupports.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedSupports.Add(Convert.ToInt32(obj)); }

            sourceModel.LinkElements.GetSelectedItemIds(out selected);
            if (selected != null) { foreach(object obj in selected) mem.SelectedLinkElements.Add(Convert.ToInt32(obj)); }
        }

        private static void ExtractSupportsAndReactions(IAxisVMModel sourceModel, MemoryData mem, ReactionTransferSettings settings, List<string> errors)
        {
            int validFound = 0;
            for (int i = 1; i <= 100000 && validFound < sourceModel.NodalSupports.Count; i++)
            {
                AxisVM.IAxisVMNodalSupport sup = null;
                try { sup = sourceModel.NodalSupports.Item[i]; } catch { continue; }
                if (sup != null)
                {
                    validFound++;
                    int nodeId = sup.NodeId;
                    
                    // Only extract reactions from SELECTED supports/nodes
                    if (!mem.SelectedNodes.Contains(nodeId) && !mem.SelectedSupports.Contains(i)) continue;
                    
                    int nUid = 0;
                    try { nUid = sourceModel.Nodes.get_UID(nodeId); } catch { try { nUid = sourceModel.Nodes.UID[nodeId]; } catch {} }
                    
                    AxisVM.EAnalysisType analysisType = (settings.ResultType == 1) ? AxisVM.EAnalysisType.atNonLinearStatic : AxisVM.EAnalysisType.atLinearStatic;
                    
                    if (settings.ResultType == 2)
                    {
                        AxisVM.RNodalSupportForceValues forces = new AxisVM.RNodalSupportForceValues();
                        string errorStr = "";
                        int success = sourceModel.Results.Forces.GetEnvelopeNodalSupportForce(i, (AxisVM.EMinMaxType)1, analysisType, AxisVM.ENodalSupportForce.nsfRz, ref forces, out errorStr);
                        if (success > 0) mem.Reactions.Add(new MemReaction { SourceNodeId = nodeId, SourceNodeUID = nUid, LoadCaseId = 1, Forces = forces });
                    }
                    else
                    {
                        for (int lc = 1; lc <= sourceModel.LoadCases.Count; lc++)
                        {
                            AxisVM.RNodalSupportForceValues forces = new AxisVM.RNodalSupportForceValues();
                            string errorStr = "";
                            int success = sourceModel.Results.Forces.GetNodalSupportForceByLoadCaseId(i, lc, 0, analysisType, ref forces, out errorStr);
                            if (success > 0) mem.Reactions.Add(new MemReaction { SourceNodeId = nodeId, SourceNodeUID = nUid, LoadCaseId = lc, Forces = forces });
                        }
                    }
                }
            }
        }

        private static void TrimUnselected(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings)
        {
            // Protect sub-elements (lines/nodes) of selected top-level items so they don't get deleted and break the members!
            foreach (int mId in mem.SelectedMembers) {
                try {
                    Array mLines; targetModel.Members.Item[mId].GetLines(out mLines);
                    if (mLines != null) {
                        foreach (object obj in mLines) {
                            int lid = Convert.ToInt32(obj);
                            mem.SelectedLines.Add(lid);
                            var line = targetModel.Lines.Item[lid];
                            mem.SelectedNodes.Add(line.StartNode);
                            mem.SelectedNodes.Add(line.EndNode);
                        }
                    }
                } catch {}
            }
            foreach (int dId in mem.SelectedDomains) {
                try {
                    Array dLines = targetModel.Domains.Item[dId].ContourLines;
                    if (dLines != null) {
                        foreach (object obj in dLines) {
                            int lid = Convert.ToInt32(obj);
                            mem.SelectedLines.Add(lid);
                            var line = targetModel.Lines.Item[lid];
                            mem.SelectedNodes.Add(line.StartNode);
                            mem.SelectedNodes.Add(line.EndNode);
                        }
                    }
                } catch {}
            }
            foreach (int sId in mem.SelectedSurfaces) {
                try {
                    Array sLines = targetModel.Surfaces.Item[sId].ContourLines;
                    if (sLines != null) {
                        foreach (object obj in sLines) {
                            int lid = Convert.ToInt32(obj);
                            mem.SelectedLines.Add(lid);
                            var line = targetModel.Lines.Item[lid];
                            mem.SelectedNodes.Add(line.StartNode);
                            mem.SelectedNodes.Add(line.EndNode);
                        }
                    }
                } catch {}
            }
            if (targetModel.LoadPanels != null) {
                foreach (int lpId in mem.SelectedLoadPanels) {
                    try {
                        Array lpLines; targetModel.LoadPanels.Item[lpId].GetLines(out lpLines);
                        if (lpLines != null) {
                            foreach (object obj in lpLines) {
                                int lid = Convert.ToInt32(obj);
                                mem.SelectedLines.Add(lid);
                                var line = targetModel.Lines.Item[lid];
                                mem.SelectedNodes.Add(line.StartNode);
                                mem.SelectedNodes.Add(line.EndNode);
                            }
                        }
                    } catch {}
                }
            }
            foreach (int lId in mem.SelectedLines) {
                try {
                    var line = targetModel.Lines.Item[lId];
                    mem.SelectedNodes.Add(line.StartNode);
                    mem.SelectedNodes.Add(line.EndNode);
                } catch {}
            }

            // Clear any elements that could lock geometry (Design Members, Links, etc.)
            try { if (targetModel.SteelDesignMembers != null) { for (int i = targetModel.SteelDesignMembers.Count; i >= 1; i--) { try { targetModel.SteelDesignMembers.Delete(i); } catch {} } } } catch {}
            try { if (targetModel.TimberDesignMembers != null) { for (int i = targetModel.TimberDesignMembers.Count; i >= 1; i--) { try { targetModel.TimberDesignMembers.Delete(i); } catch {} } } } catch {}
            try { if (targetModel.VirtualBeams != null) { for (int i = targetModel.VirtualBeams.Count; i >= 1; i--) { try { targetModel.VirtualBeams.Delete(i); } catch {} } } } catch {}
            
            // Link Elements (delete unselected)
            if (targetModel.LinkElements != null) {
                targetModel.LinkElements.SelectAll(AxisVM.ELongBoolean.lbTrue);
                Array allLinks; targetModel.LinkElements.GetSelectedItemIds(out allLinks);
                targetModel.LinkElements.SelectAll(AxisVM.ELongBoolean.lbFalse);
                if (allLinks != null) {
                    foreach (object obj in allLinks) {
                        int id = Convert.ToInt32(obj);
                        try { if (!mem.SelectedLinkElements.Contains(id)) targetModel.LinkElements.Delete(id); } catch {}
                    }
                }
            }
            
            // Domains
            if (targetModel.Domains != null) {
                for (int i = targetModel.Domains.Count; i >= 1; i--) {
                    try { if (!mem.SelectedDomains.Contains(i)) targetModel.Domains.Delete(i); } catch {}
                }
            }

            // Surfaces
            if (targetModel.Surfaces != null) {
                for (int i = targetModel.Surfaces.Count; i >= 1; i--) {
                    // Since Domains might have shifted Surfaces, we catch exceptions. If the user didn't select surfaces, this just clears whatever is left.
                    try { if (!mem.SelectedSurfaces.Contains(i)) targetModel.Surfaces.Delete(i); } catch {}
                }
            }

            // Members
            if (targetModel.Members != null) {
                for (int i = targetModel.Members.Count; i >= 1; i--) {
                    try { if (!mem.SelectedMembers.Contains(i)) targetModel.Members.Delete(i); } catch {}
                }
            }

            // Load Panels
            if (targetModel.LoadPanels != null) {
                for (int i = targetModel.LoadPanels.Count; i >= 1; i--) {
                    try { 
                        // For LoadPanels, ExtractSelection uses Index (1 to Count)
                        if (!mem.SelectedLoadPanels.Contains(i)) targetModel.LoadPanels.Delete(i); 
                    } catch {}
                }
            }

            // Lines
            if (targetModel.Lines != null) {
                for (int i = targetModel.Lines.Count; i >= 1; i--) {
                    try { if (!mem.SelectedLines.Contains(i)) targetModel.Lines.Delete(i); } catch {}
                }
            }

            // Nodes
            if (targetModel.Nodes != null) {
                for (int i = targetModel.Nodes.Count; i >= 1; i--) {
                    try { if (!mem.SelectedNodes.Contains(i)) targetModel.Nodes.Delete(i); } catch {}
                }
            }

            // Nodal Supports
            if (targetModel.NodalSupports != null) {
                for (int i = targetModel.NodalSupports.Count; i >= 1; i--) {
                    try {
                        if (!settings.TransferSupports || !mem.SelectedSupports.Contains(i)) {
                            targetModel.NodalSupports.Delete(i); 
                        }
                    } catch {}
                }
            }
        }

        private static int ApplyReactions(IAxisVMModel targetModel, MemoryData mem, ReactionTransferSettings settings)
        {
            int appliedCount = 0;
            foreach (var r in mem.Reactions)
            {
                // In Save-And-Trim, load cases are preserved perfectly 1:1, so r.LoadCaseId matches the target model exactly!
                int targetLc = (settings.ResultType == 2) ? 1 : r.LoadCaseId;

                // Because unselected nodes were deleted, the node indices have shifted! We MUST find the new NodeID using UID!
                int targetNodeId = -1;
                try {
                    // get_IndexOfUID is the C# translation for IndexOfUID property taking an argument
                    targetNodeId = targetModel.Nodes.get_IndexOfUID(r.SourceNodeUID);
                } catch {
                    // Fallback to property array syntax if get_IndexOfUID isn't available
                    try { targetNodeId = targetModel.Nodes.IndexOfUID[r.SourceNodeUID]; } catch {}
                }

                if (targetNodeId <= 0) {
                    // If we couldn't find the node by UID, try using the original ID as a last resort (e.g., if no nodes were deleted before it)
                    targetNodeId = r.SourceNodeId;
                }

                bool beamLoadApplied = false;
                if (settings.ApplyAsBeamPointLoads && targetModel.Lines != null) {
                    int targetLine = -1;
                    double position = 0;
                    
                    // Search backwards through lines to find one that contains this node
                    for (int i = targetModel.Lines.Count; i >= 1; i--) {
                        try {
                            var line = targetModel.Lines.Item[i];
                            if (line.StartNode == targetNodeId) { targetLine = i; position = 0; break; }
                            if (line.EndNode == targetNodeId) { targetLine = i; position = line.Length; break; }
                        } catch {}
                    }

                    if (targetLine > 0) {
                        AxisVM.RLoadBeamConcentrated newLoad = new AxisVM.RLoadBeamConcentrated();
                        newLoad.LineId = targetLine;
                        newLoad.LoadCaseId = targetLc;
                        newLoad.Fgx = r.Forces.Rx;
                        newLoad.Fgy = r.Forces.Ry;
                        newLoad.Fgz = r.Forces.Rz;
                        newLoad.Mgx = r.Forces.Rxx;
                        newLoad.Mgy = r.Forces.Ryy;
                        newLoad.Mgz = r.Forces.Rzz;
                        newLoad.Position = position;
                        newLoad.SystemGLR = AxisVM.ESystem.sysGlobal;
                        try { targetModel.Loads.AddBeamConcentrated(ref newLoad); beamLoadApplied = true; appliedCount++; } catch {}
                    }
                }

                if (!beamLoadApplied) {
                    AxisVM.RLoadNodalForce newLoad = new AxisVM.RLoadNodalForce();
                    newLoad.NodeId = targetNodeId;
                    newLoad.LoadCaseId = targetLc;
                    newLoad.Fx = r.Forces.Rx;
                    newLoad.Fy = r.Forces.Ry;
                    newLoad.Fz = r.Forces.Rz;
                    newLoad.Mx = r.Forces.Rxx;
                    newLoad.My = r.Forces.Ryy;
                    newLoad.Mz = r.Forces.Rzz;
                    try { targetModel.Loads.AddNodalForce(ref newLoad); appliedCount++; } catch {}
                }
            }
            return appliedCount;
        }
    }
}
