using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    public class SectionModifierFunction
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                List<int> linesToModify = new List<int>();

                // Get selected lines
                if (model.Lines.SelCount > 0)
                {
                    Array selectedLines;
                    model.Lines.GetSelectedItemIds(out selectedLines);
                    if (selectedLines != null)
                    {
                        for (int i = 1; i <= selectedLines.Length; i++)
                        {
                            int lineId = (int)selectedLines.GetValue(i);
                            if (!linesToModify.Contains(lineId))
                                linesToModify.Add(lineId);
                        }
                    }
                }

                // Get selected members' lines
                if (model.Members.SelCount > 0)
                {
                    Array selectedMembers;
                    model.Members.GetSelectedItemIds(out selectedMembers);
                    if (selectedMembers != null)
                    {
                        for (int i = 1; i <= selectedMembers.Length; i++)
                        {
                            int memberId = (int)selectedMembers.GetValue(i);
                            var member = model.Members.Item[memberId];
                            Array memberLines;
                            member.GetLines(out memberLines);
                            if (memberLines != null)
                            {
                                for (int j = 1; j <= memberLines.Length; j++)
                                {
                                    int lineId = (int)memberLines.GetValue(j);
                                    if (!linesToModify.Contains(lineId))
                                        linesToModify.Add(lineId);
                                }
                            }
                        }
                    }
                }

                if (linesToModify.Count == 0)
                {
                    MessageBox.Show("No lines or members selected. Please select items to modify their sections.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                using (SectionModifierForm form = new SectionModifierForm())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        model.BeginUpdate();
                        int modifiedCount = 0;
                        try
                        {
                            foreach (int lineId in linesToModify)
                            {
                                var lineItem = model.Lines.Item[lineId];
                                ELineType lType = lineItem.LineType;
                                
                                int matId = 0, startCs = 0, endCs = 0;
                                AxisVM.RPoint3d startExc = new AxisVM.RPoint3d();
                                AxisVM.RPoint3d endExc = new AxisVM.RPoint3d();
                                AxisVM.ELineNonLinearity nlType = AxisVM.ELineNonLinearity.lnlTensionAndCompression;
                                double resistance = 0.0;
                                
                                try 
                                {
                                    if (lType == ELineType.ltBeam)
                                    {
                                        lineItem.GetBeamData(out matId, out startCs, out endCs, ref startExc, ref endExc);
                                    }
                                    else if (lType == ELineType.ltTruss)
                                    {
                                        lineItem.GetTrussData(out matId, out startCs, out nlType, out resistance);
                                    }
                                    else if (lType == ELineType.ltRib)
                                    {
                                        lineItem.GetRibData(out matId, out startCs, out endCs, ref startExc, ref endExc);
                                    }
                                } 
                                catch { continue; } // Skip if failed to get data
                                
                                if (startCs > 0)
                                {
                                    var currentSec = model.CrossSections.Item[startCs];
                                    string oldName = currentSec.Name;
                                    
                                    double currentWidth = 0.1;
                                    double currentDepth = 0.1;
                                    
                                    if (oldName.Contains("x"))
                                    {
                                        string[] parts = oldName.Split('x');
                                        if (parts.Length == 2)
                                        {
                                            double w, d;
                                            if (double.TryParse(parts[0], out w) && double.TryParse(parts[1], out d))
                                            {
                                                currentWidth = w / 1000.0;
                                                currentDepth = d / 1000.0;
                                            }
                                        }
                                    }

                                    double newWidthMeters = form.ModifyWidth ? (form.NewWidth / 1000.0) : currentWidth;
                                    double newDepthMeters = form.ModifyDepth ? (form.NewDepth / 1000.0) : currentDepth;

                                    string newName = string.Format("{0}x{1}", (int)Math.Round(newWidthMeters * 1000), (int)Math.Round(newDepthMeters * 1000));
                                    
                                    int newSecId = model.CrossSections.IndexOf(newName);
                                    if (newSecId <= 0)
                                    {
                                        newSecId = model.CrossSections.AddRectangular(newName, newWidthMeters, newDepthMeters, ECrossSectionProcess.cspOther);
                                    }

                                    if (newSecId > 0)
                                    {
                                        try
                                        {
                                            if (lType == ELineType.ltBeam)
                                            {
                                                lineItem.DefineAsBeam(matId, newSecId, newSecId, ref startExc, ref endExc);
                                            }
                                            else if (lType == ELineType.ltTruss)
                                            {
                                                lineItem.DefineAsTruss(matId, newSecId, nlType, resistance);
                                            }
                                            else if (lType == ELineType.ltRib)
                                            {
                                                lineItem.DefineAsRib(matId, newSecId, newSecId, ref startExc, ref endExc);
                                            }
                                            modifiedCount++;
                                        }
                                        catch {}
                                    }
                                }
                            }
                        }
                        finally
                        {
                            model.EndUpdate();
                        }
                        
                        if (model.Windows != null && model.Windows.Count > 0)
                        {
                            model.Windows.Item[1].ReDraw();
                        }

                        if (modifiedCount > 0)
                        {
                            MessageBox.Show(string.Format("Successfully modified {0} sections.", modifiedCount), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("No sections were modified. Ensure selected items are rectangular beams/columns with 'WxD' naming.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    }
}
