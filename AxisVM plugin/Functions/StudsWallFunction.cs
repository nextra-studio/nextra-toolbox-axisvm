using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;
using AxisVM_plugin.UI;
using AxisVM_plugin.Core;
using AxisVM_plugin.Generators;

namespace AxisVM_plugin.Functions
{
    public class StudsWallFunction
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1]; // Typically item 1 is the active model
                if (model == null) return -1;

                // Ensure main form is enabled so the selection dialog responds
                app.EnableMainForm();

                // Prompt user to select lines
                string selTypes = ((char)3).ToString(); // seltAllLines = 3
                int res = model.StartModalSelection("Please select boundary lines for the studs wall.", 0 /* False */, selTypes);

                if (res == 0) // Canceled
                {
                    return 0;
                }

                // Get selected lines
                Array selectedLines;
                model.Lines.GetSelectedItemIds(out selectedLines);

                if (selectedLines == null || selectedLines.Length == 0)
                {
                    MessageBox.Show("No lines were selected. Please select closed planar lines to create a studs wall.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                
                int numLines = selectedLines.Length;

                Array lineDataArr;
                model.Lines.BulkGetLineData(selectedLines, out lineDataArr);
                
                if (lineDataArr == null || lineDataArr.Length == 0)
                {
                     MessageBox.Show("Failed to retrieve line data.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                     return 0;
                }

                // Node adjacency map to validate closed loop
                Dictionary<int, List<int>> nodeLines = new Dictionary<int, List<int>>();
                for (int i = 1; i <= lineDataArr.Length; i++)
                {
                    RLineData lineData = (RLineData)lineDataArr.GetValue(i);
                    if (lineData.GeomType != ELineGeomType.lgtStraightLine)
                    {
                        MessageBox.Show("All selected lines must be straight lines.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 0;
                    }

                    int n1 = lineData.NodeId1;
                    int n2 = lineData.NodeId2;
                    
                    if (!nodeLines.ContainsKey(n1)) nodeLines[n1] = new List<int>();
                    if (!nodeLines.ContainsKey(n2)) nodeLines[n2] = new List<int>();
                    
                    nodeLines[n1].Add(n2);
                    nodeLines[n2].Add(n1);
                }

                // Validate closed loop
                foreach (var kvp in nodeLines)
                {
                    if (kvp.Value.Count != 2)
                    {
                        MessageBox.Show(string.Format("The selected lines do not form a closed loop. Node {0} connects to {1} lines.", kvp.Key, kvp.Value.Count), "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 0;
                    }
                }
                
                // Get node coordinates and check planarity
                List<int> nodeIds = new List<int>(nodeLines.Keys);
                List<RPoint3d> coords = new List<RPoint3d>();
                foreach (int nid in nodeIds)
                {
                    RPoint3d pt = new RPoint3d();
                    model.Nodes.GetNodeCoord(nid, ref pt);
                    coords.Add(pt);
                }
                RPoint3d p0 = coords[0];
                double nx = 0, ny = 0, nz = 1; // Default to horizontal slab normal
                
                if (coords.Count >= 3)
                {
                    // Check planarity
                    var p1 = coords[1];
                    var p2 = coords[2];
                    
                    // Normal vector N = (P1 - P0) x (P2 - P0)
                    double vx1 = p1.x - p0.x, vy1 = p1.y - p0.y, vz1 = p1.z - p0.z;
                    double vx2 = p2.x - p0.x, vy2 = p2.y - p0.y, vz2 = p2.z - p0.z;
                    
                    nx = vy1 * vz2 - vz1 * vy2;
                    ny = vz1 * vx2 - vx1 * vz2;
                    nz = vx1 * vy2 - vy1 * vx2;
                    
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-6)
                    {
                        nx /= len; ny /= len; nz /= len;
                        
                        for (int i = 3; i < coords.Count; i++)
                        {
                            var pi = coords[i];
                            double dx = pi.x - p0.x, dy = pi.y - p0.y, dz = pi.z - p0.z;
                            double dot = dx * nx + dy * ny + dz * nz;
                            if (Math.Abs(dot) > 1e-3)
                            {
                                MessageBox.Show("The selected lines are not planar.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return 0;
                            }
                        }
                    }
                }

                // Automatically generate default catalog before opening UI
                AxisVMHelper.GenerateDefaultCatalog(model);

                // Fetch materials
                List<string> materials = new List<string>();
                if (model.Materials != null)
                {
                    for (int i = 1; i <= model.Materials.Count; i++)
                    {
                        materials.Add(model.Materials.Item[i].Name);
                    }
                }

                // Fetch sections
                List<string> sections = new List<string>();
                if (model.CrossSections != null)
                {
                    for (int i = 1; i <= model.CrossSections.Count; i++)
                    {
                        sections.Add(model.CrossSections.Item[i].Name);
                    }
                }

                // Open Windows Forms UI for the plugin settings
                using (SettingsForm form = new SettingsForm(model, materials, sections))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        WallGenerator.GenerateWall(model, form, selectedLines, lineDataArr, p0, nx, ny, nz);
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
