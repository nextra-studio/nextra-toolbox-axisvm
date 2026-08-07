using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    public class OneWayLoadPanelLogic
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                using (OneWayLoadPanelForm form = new OneWayLoadPanelForm())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        model.BeginUpdate();
                        try
                        {
                            // 1. Get selected lines or load panels
                            // For this skeleton, we assume the user selected 4 boundary lines
                            Array selectedLines;
                            model.Lines.GetSelectedItemIds(out selectedLines);

                            if (selectedLines == null || selectedLines.Length != 4)
                            {
                                MessageBox.Show("Please select exactly 4 lines to define the load panel boundary.", "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                return 0;
                            }

                            if (form.NoSpacing)
                            {
                                // Just create a single load panel or domain
                                // This requires creating a domain from the 4 lines, then a load panel
                                MessageBox.Show("No Spacing selected. Would create 1 one-way load panel here.");
                            }
                            else
                            {
                                // Create 3 load panels based on edge spacing
                                MessageBox.Show(string.Format("Spacing = {0}mm. Would create 3 separate load panels here.", form.EdgeSpacing));
                            }
                            
                            // To actually create the Load Panel, you would:
                            // 1. Create a Domain using model.Domains.Add(...)
                            // 2. Call model.LoadPanels.AddFromDomain(domainId)
                            // 3. Set the load panel properties (span direction, etc.)
                        }
                        finally
                        {
                            model.EndUpdate();
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
