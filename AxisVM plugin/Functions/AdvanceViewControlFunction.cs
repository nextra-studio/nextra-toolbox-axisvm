using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    public class AdvanceViewControlFunction
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                // Gather materials for filter
                List<string> materials = new List<string>();
                if (model.Materials != null)
                {
                    for (int i = 1; i <= model.Materials.Count; i++)
                    {
                        materials.Add(model.Materials.Item[i].Name);
                    }
                }

                using (AdvanceViewControlForm form = new AdvanceViewControlForm(materials))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        // Execute based on user selection
                        if (form.SelectedAction == AdvanceViewControlForm.ActionType.ShowAll)
                        {
                            ShowAll(model);
                        }
                        else if (form.SelectedAction == AdvanceViewControlForm.ActionType.ShowOnlySelected)
                        {
                            ApplyStrictMode(model, form);
                            IsolateSelected(model);
                        }
                        else if (form.SelectedAction == AdvanceViewControlForm.ActionType.HideSelected)
                        {
                            // Hide selected means we invert the selection, apply strict mode, then isolate
                            InvertSelection(model);
                            ApplyStrictMode(model, form);
                            IsolateSelected(model);
                        }
                        else if (form.SelectedAction == AdvanceViewControlForm.ActionType.FilterByAttribute)
                        {
                            ApplyAttributeFilter(model, form.SelectedMaterial, form.MinWidth, form);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        private static void ShowAll(IAxisVMModel model)
        {
            if (model.Windows != null && model.Windows.Count > 0)
            {
                // Clear any display part filter
                System.Array emptyUids = new int[0];
                model.Windows.Item[1].SetWindowDisplayPartUIDs(ref emptyUids);

                model.Windows.Item[1].ShowOnlySelected = ELongBoolean.lbFalse;
                model.Windows.Item[1].ReDraw();
            }
            CleanupTempCustomPart(model);
        }

        private static void IsolateSelected(IAxisVMModel model)
        {
            if (model.Windows != null && model.Windows.Count > 0)
            {
                // First ensure any old temp part is removed
                CleanupTempCustomPart(model);
                
                // Create a new Custom Part from whatever is currently selected
                int partId = model.CustomParts.RootFolder.AddPartFromSelectedItems("$$AdvanceViewControl_Temp$$");
                if (partId > 0)
                {
                    int uid = model.CustomParts.RootFolder.get_PartUID(partId);
                    System.Array uids = new int[] { uid };
                    
                    // Force the window to ONLY show this Custom Part
                    model.Windows.Item[1].SetWindowDisplayPartUIDs(ref uids);
                    
                    // Turn off ShowOnlySelected because we are using Custom Parts for isolation now
                    // This bypasses AxisVM's topological link feature!
                    model.Windows.Item[1].ShowOnlySelected = ELongBoolean.lbFalse; 
                    model.Windows.Item[1].ReDraw();
                }
                else
                {
                    // Fallback just in case nothing was selected
                    model.Windows.Item[1].ShowOnlySelected = ELongBoolean.lbTrue;
                    model.Windows.Item[1].ReDraw();
                }
            }
        }

        private static void CleanupTempCustomPart(IAxisVMModel model)
        {
            try
            {
                int count = model.CustomParts.Count;
                // Go backwards when deleting to avoid index shifting issues
                for (int i = count; i >= 1; i--)
                {
                    string name = model.CustomParts.get_Name(i);
                    if (name == "$$AdvanceViewControl_Temp$$")
                    {
                        model.CustomParts.DeletePart(i);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Strict mode guarantees that domains and load panels don't get accidentally shown
        /// if their edge line was selected. It forcefully unselects them.
        /// </summary>
        private static void ApplyStrictMode(IAxisVMModel model, AdvanceViewControlForm form)
        {
            // If the user wants strict lines-only, we ensure these surface types are NOT selected
            if (form.ExcludeDomains)
            {
                for (int i = 1; i <= model.Domains.Count; i++)
                {
                    model.Domains.set_Selected(i, ELongBoolean.lbFalse);
                }
            }
            
            if (form.ExcludeLoadPanels)
            {
                for (int i = 1; i <= model.LoadPanels.Count; i++)
                {
                    model.LoadPanels.set_Selected(i, ELongBoolean.lbFalse);
                }
            }
            
            if (form.ExcludeSurfaces)
            {
                for (int i = 1; i <= model.Surfaces.Count; i++)
                {
                    model.Surfaces.set_Selected(i, ELongBoolean.lbFalse);
                }
            }
        }

        /// <summary>
        /// Custom engine to invert selection across lines, nodes, surfaces, domains, etc.
        /// This creates a pseudo "Hide Selected" feature in AxisVM.
        /// </summary>
        private static void InvertSelection(IAxisVMModel model)
        {
            // Invert Nodes
            for (int i = 1; i <= model.Nodes.Count; i++)
            {
                ELongBoolean sel = model.Nodes.get_Selected(i);
                model.Nodes.set_Selected(i, sel == ELongBoolean.lbTrue ? ELongBoolean.lbFalse : ELongBoolean.lbTrue);
            }

            // Invert Lines
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                ELongBoolean sel = model.Lines.get_Selected(i);
                model.Lines.set_Selected(i, sel == ELongBoolean.lbTrue ? ELongBoolean.lbFalse : ELongBoolean.lbTrue);
            }

            // Invert Surfaces
            for (int i = 1; i <= model.Surfaces.Count; i++)
            {
                ELongBoolean sel = model.Surfaces.get_Selected(i);
                model.Surfaces.set_Selected(i, sel == ELongBoolean.lbTrue ? ELongBoolean.lbFalse : ELongBoolean.lbTrue);
            }

            // Invert Domains
            for (int i = 1; i <= model.Domains.Count; i++)
            {
                ELongBoolean sel = model.Domains.get_Selected(i);
                model.Domains.set_Selected(i, sel == ELongBoolean.lbTrue ? ELongBoolean.lbFalse : ELongBoolean.lbTrue);
            }

            // Invert LoadPanels
            for (int i = 1; i <= model.LoadPanels.Count; i++)
            {
                ELongBoolean sel = model.LoadPanels.get_Selected(i);
                model.LoadPanels.set_Selected(i, sel == ELongBoolean.lbTrue ? ELongBoolean.lbFalse : ELongBoolean.lbTrue);
            }
        }

        private static void ApplyAttributeFilter(IAxisVMModel model, string matName, double minWidth, AdvanceViewControlForm form)
        {
            List<int> linesToSelect = new List<int>();
            
            for (int i = 1; i <= model.Lines.Count; i++)
            {
                try
                {
                    var line = model.Lines.Item[i];
                    System.Array attrs;
                    
                    System.Array lineIdArr = System.Array.CreateInstance(typeof(int), 2);
                    lineIdArr.SetValue(i, 1);
                    
                    model.Lines.BulkGetAttr(lineIdArr, out attrs);
                    if (attrs != null && attrs.Length >= 1)
                    {
                        RLineAttr attr = (RLineAttr)attrs.GetValue(1);
                        
                        bool matchMat = true;
                        if (matName != "Any" && attr.MaterialIndex > 0)
                        {
                            string mName = model.Materials.Item[attr.MaterialIndex].Name;
                            if (mName != matName) matchMat = false;
                        }

                        bool matchWidth = true;
                        if (minWidth > 0 && attr.StartCrossSectionIndex > 0)
                        {
                            var sec = model.CrossSections.Item[attr.StartCrossSectionIndex];
                            
                            // Simplistic string check if it has AxB format
                            string sName = sec.Name;
                            if (sName.Contains("x"))
                            {
                                string[] parts = sName.Split('x');
                                if (parts.Length == 2)
                                {
                                    double w;
                                    if (double.TryParse(parts[0], out w) || double.TryParse(parts[1], out w))
                                    {
                                        if (w < minWidth && (w / 1000.0) < minWidth) // mm vs m
                                        {
                                            matchWidth = false;
                                        }
                                    }
                                }
                            }
                        }

                        if (matchMat && matchWidth)
                        {
                            linesToSelect.Add(i);
                        }
                    }
                }
                catch { }
            }

            if (linesToSelect.Count > 0)
            {
                // Clear selection
                for (int i = 1; i <= model.Lines.Count; i++)
                {
                    model.Lines.set_Selected(i, ELongBoolean.lbFalse);
                }

                // Select matching lines
                foreach (int lineId in linesToSelect)
                {
                    model.Lines.set_Selected(lineId, ELongBoolean.lbTrue);
                }

                ApplyStrictMode(model, form);
                IsolateSelected(model);
                
                MessageBox.Show(string.Format("Found and strictly isolated {0} lines matching criteria.", linesToSelect.Count), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No lines matched the filter criteria.", "Filter Result");
            }
        }
    }
}
