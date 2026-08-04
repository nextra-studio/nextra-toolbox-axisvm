using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.Generators
{
    public class WallGenerator
    {
        public static void GenerateWall(IAxisVMModel model, UI.SettingsForm form, System.Array selectedLines, System.Array lineDataArr, RPoint3d p0, double nx, double ny, double nz)
        {
            int panelMatId = form.cmbPanelMaterial.SelectedIndex + 1;
            double panelThickness = (double)form.numPanelThickness.Value;
            int studMatId = form.cmbStudMaterial.SelectedIndex + 1;
            int studSecId = form.cmbStudSection.SelectedIndex + 1;
            double spacing = (double)form.numStudSpacing.Value;
            double studOffset = (double)form.numStudOffset.Value;

            int topBeamMatId = form.cmbTopBeamMaterial.SelectedIndex + 1;
            int topBeamSecId = form.cmbTopBeamSection.SelectedIndex + 1;
            double fastenerSpacing_m = (double)form.numFastenerSpacing.Value;
            double slipModulus_kNm = (double)form.numSlipModulus.Value;

            // 1. Create the Domain on original nodes
            RSurfaceAttr surfaceAttr = new RSurfaceAttr();
            surfaceAttr.Thickness = panelThickness;
            surfaceAttr.SurfaceType = ESurfaceType.stShell;
            surfaceAttr.MaterialId = panelMatId;
            int domainId = model.Domains.Add(selectedLines, ref surfaceAttr);
            
            try
            {
                // Try setting Timber properties for the Domain
                dynamic dynDomain = model.Domains.Item[domainId];
                dynDomain.Timber_ServiceClass = 1;
                dynDomain.Timber_kdef = 0.8;
            }
            catch { }

            List<int> linesToIntersectList = new List<int>();

            model.BeginUpdate();
            try
            {
                // 2. Determine Local Axes for the Wall
                double Ux, Uy, Uz, Vx, Vy, Vz;
                if (Math.Abs(nz) < 0.99)
                {
                    Vx = 0; Vy = 0; Vz = 1.0;
                    Ux = ny * Vz - nz * Vy; Uy = nz * Vx - nx * Vz; Uz = nx * Vy - ny * Vx;
                    double lenU = Math.Sqrt(Ux * Ux + Uy * Uy + Uz * Uz);
                    Ux /= lenU; Uy /= lenU; Uz /= lenU;
                    Vx = Uy * nz - Uz * ny; Vy = Uz * nx - Ux * nz; Vz = Ux * ny - Uy * nx;
                }
                else
                {
                    Ux = 1; Uy = 0; Uz = 0; Vx = 0; Vy = 1; Vz = 0;
                }

                // Project line segments to (U, V)
                double uMin = double.MaxValue, uMax = double.MinValue;
                double vMin = double.MaxValue, vMax = double.MinValue;
                
                List<Tuple<double, double, double, double>> linesUV = new List<Tuple<double, double, double, double>>();
                for (int k = 1; k <= lineDataArr.Length; k++)
                {
                    RLineData lineData = (RLineData)lineDataArr.GetValue(k);
                    RPoint3d pt1 = new RPoint3d(), pt2 = new RPoint3d();
                    model.Nodes.GetNodeCoord(lineData.NodeId1, ref pt1);
                    model.Nodes.GetNodeCoord(lineData.NodeId2, ref pt2);
                    
                    double u1 = (pt1.x - p0.x) * Ux + (pt1.y - p0.y) * Uy + (pt1.z - p0.z) * Uz;
                    double v1 = (pt1.x - p0.x) * Vx + (pt1.y - p0.y) * Vy + (pt1.z - p0.z) * Vz;
                    double u2 = (pt2.x - p0.x) * Ux + (pt2.y - p0.y) * Uy + (pt2.z - p0.z) * Uz;
                    double v2 = (pt2.x - p0.x) * Vx + (pt2.y - p0.y) * Vy + (pt2.z - p0.z) * Vz;
                    
                    linesUV.Add(new Tuple<double, double, double, double>(u1, v1, u2, v2));
                    if (u1 < uMin) uMin = u1; if (u1 > uMax) uMax = u1;
                    if (u2 < uMin) uMin = u2; if (u2 > uMax) uMax = u2;
                    if (v1 < vMin) vMin = v1; if (v1 > vMax) vMax = v1;
                    if (v2 < vMin) vMin = v2; if (v2 > vMax) vMax = v2;
                }

                double ox = nx * studOffset;
                double oy = ny * studOffset;
                double oz = nz * studOffset;
                
                int refId = AxisVMHelper.CreateReferenceVector(model, 0, 0, -1);
                HashSet<string> springTracker = new HashSet<string>();
                
                double wallHeight = vMax - vMin;
                double totalStiffness = (wallHeight / fastenerSpacing_m) * slipModulus_kNm;
                double nodeSpringStiffness = totalStiffness / 2.0;

                // 3. Calculate gaps and spaces (needed for springs and studs)
                double L = uMax - uMin;
                int numSpaces = (int)Math.Ceiling(L / spacing);
                double gap = (L - (numSpaces * spacing)) / 2.0; // split remaining space
                if (gap < 0) gap = 0; // fallback if exact
                if (numSpaces * spacing > L)
                {
                    gap = (L - ((numSpaces - 1) * spacing)) / 2.0;
                    numSpaces -= 1;
                }

                // We need to auto-intersect the boundary lines and springs to avoid meshing errors.
                foreach (int lineId in selectedLines)
                {
                    linesToIntersectList.Add(lineId);
                }
                
                // Track springs to intersect
                List<int> springLineIds = new List<int>();

                // 4. Generate Boundary Beams and End Studs
                for (int i = 0; i < selectedLines.Length; i++)
                {
                    var uv = linesUV[i];
                    double midV = (uv.Item2 + uv.Item4) / 2.0;
                    double dU = Math.Abs(uv.Item3 - uv.Item1);
                    double dV = Math.Abs(uv.Item4 - uv.Item2);
                    
                    int matId, secId;
                    bool isStud = (dV > dU);
                    if (isStud) 
                    {
                        matId = studMatId; secId = studSecId; // End Stud
                        int lineId = (int)selectedLines.GetValue(i + 1); // 1-based array from COM
                        AxisVMHelper.SetLineBeamAttributes(model, lineId, matId, secId, isRib: true);
                        AxisVMHelper.SetLineReleases(model, lineId, isPin: true);
                    }
                    else
                    {
                        matId = topBeamMatId; secId = topBeamSecId; // Top/Bottom Beam
                        int lineId = (int)selectedLines.GetValue(i + 1); // 1-based array from COM
                        AxisVMHelper.SetLineBeamAttributes(model, lineId, matId, secId, isRib: false);
                    }

                    if (isStud && studOffset != 0)
                    {
                        // Generate offset lines for boundary studs
                        double lu1 = uv.Item1;
                        double lu2 = uv.Item3;
                        double lv1 = uv.Item2;
                        double lv2 = uv.Item4;
                        
                        double x1 = p0.x + lu1 * Ux + lv1 * Vx + ox;
                        double y1 = p0.y + lu1 * Uy + lv1 * Vy + oy;
                        double z1 = p0.z + lu1 * Uz + lv1 * Vz + oz;
                        
                        double x2 = p0.x + lu2 * Ux + lv2 * Vx + ox;
                        double y2 = p0.y + lu2 * Uy + lv2 * Vy + oy;
                        double z2 = p0.z + lu2 * Uz + lv2 * Vz + oz;
                        
                        int offN1 = model.Nodes.Add(x1, y1, z1);
                        int offN2 = model.Nodes.Add(x2, y2, z2);
                        
                        RLineGeomData geomData = new RLineGeomData();
                        int lineId = model.Lines.Add(offN1, offN2, ELineGeomType.lgtStraightLine, ref geomData);
                        AxisVMHelper.SetLineBeamAttributes(model, lineId, matId, secId, isRib: false);
                        AxisVMHelper.SetLineReleases(model, lineId, isPin: false);

                        if (studOffset != 0)
                        {
                            double dx1 = p0.x + lu1 * Ux + lv1 * Vx;
                            double dy1 = p0.y + lu1 * Uy + lv1 * Vy;
                            double dz1 = p0.z + lu1 * Uz + lv1 * Vz;
                            
                            double dx2 = p0.x + lu2 * Ux + lv2 * Vx;
                            double dy2 = p0.y + lu2 * Uy + lv2 * Vy;
                            double dz2 = p0.z + lu2 * Uz + lv2 * Vz;
                            
                            int domN1 = model.Nodes.Add(dx1, dy1, dz1);
                            int domN2 = model.Nodes.Add(dx2, dy2, dz2);
                            
                            double cornerStiffY = (gap / 2.0) / fastenerSpacing_m * slipModulus_kNm;
                            double cornerStiffZ = 1e10; // Rigid in Z for corners

                            string key1 = string.Format("{0}_{1}", domN1, offN1);
                            if (!springTracker.Contains(key1))
                            {
                                RLineGeomData sGeom1 = new RLineGeomData();
                                int spring1 = model.Lines.Add(domN1, offN1, ELineGeomType.lgtStraightLine, ref sGeom1);
                                AxisVMHelper.SetLineSpringAttributes(model, spring1, cornerStiffY, cornerStiffZ, 1e10, refId);
                                springTracker.Add(key1);
                                springLineIds.Add(spring1);
                                linesToIntersectList.Add(spring1);
                            }
                            
                            string key2 = string.Format("{0}_{1}", domN2, offN2);
                            if (!springTracker.Contains(key2))
                            {
                                RLineGeomData sGeom2 = new RLineGeomData();
                                int spring2 = model.Lines.Add(domN2, offN2, ELineGeomType.lgtStraightLine, ref sGeom2);
                                AxisVMHelper.SetLineSpringAttributes(model, spring2, cornerStiffY, cornerStiffZ, 1e10, refId);
                                springTracker.Add(key2);
                                springLineIds.Add(spring2);
                                linesToIntersectList.Add(spring2);
                            }
                        }
                    }
                }

                // 5. Generate Internal Studs and Springs
                double u = uMin + gap;
                for (int i = 0; i <= numSpaces; i++)
                {
                    if (Math.Abs(u - uMin) < 1e-3 || Math.Abs(u - uMax) < 1e-3)
                    {
                        u += spacing;
                        continue; // Skip boundary studs (handled in step 4)
                    }

                    List<double> vIntersects = new List<double>();
                    foreach (var l in linesUV)
                    {
                        double lu1 = l.Item1, lv1 = l.Item2, lu2 = l.Item3, lv2 = l.Item4;
                        if ((lu1 <= u && u <= lu2) || (lu2 <= u && u <= lu1))
                        {
                            if (Math.Abs(lu1 - lu2) > 1e-6)
                            {
                                double v = lv1 + (u - lu1) * (lv2 - lv1) / (lu2 - lu1);
                                vIntersects.Add(v);
                            }
                        }
                    }
                    vIntersects.Sort();
                    
                    for (int j = 0; j < vIntersects.Count - 1; j += 2)
                    {
                        double vStart = vIntersects[j];
                        double vEnd = vIntersects[j+1];
                        if (Math.Abs(vStart - vEnd) < 1e-3) continue;
                        
                        double dx1 = p0.x + u * Ux + vStart * Vx, dy1 = p0.y + u * Uy + vStart * Vy, dz1 = p0.z + u * Uz + vStart * Vz;
                        double dx2 = p0.x + u * Ux + vEnd * Vx, dy2 = p0.y + u * Uy + vEnd * Vy, dz2 = p0.z + u * Uz + vEnd * Vz;
                        
                        double x1 = dx1 + ox, y1 = dy1 + oy, z1 = dz1 + oz;
                        double x2 = dx2 + ox, y2 = dy2 + oy, z2 = dz2 + oz;
                        
                        int domN1 = model.Nodes.Add(dx1, dy1, dz1);
                        int domN2 = model.Nodes.Add(dx2, dy2, dz2);
                        
                        int offN1 = model.Nodes.Add(x1, y1, z1);
                        int offN2 = model.Nodes.Add(x2, y2, z2);
                        
                        RLineGeomData geomData = new RLineGeomData();
                        int studId = model.Lines.Add(offN1, offN2, ELineGeomType.lgtStraightLine, ref geomData);
                        
                        AxisVMHelper.SetLineBeamAttributes(model, studId, studMatId, studSecId, isRib: false);
                        AxisVMHelper.SetLineReleases(model, studId, isPin: true); // internal studs are pinned
                        
                        if (studOffset != 0)
                        {
                            double dLeft = (i == 0) ? gap : spacing;
                            double dRight = (i == numSpaces) ? gap : spacing;
                            double tribLength = (dLeft + dRight) / 2.0;
                            double internalStiffY = (tribLength / fastenerSpacing_m) * slipModulus_kNm;
                            double internalStiffZ = 0; // Free to slip in Z

                            string key1 = string.Format("{0}_{1}", domN1, offN1);
                            if (!springTracker.Contains(key1))
                            {
                                RLineGeomData sGeom1 = new RLineGeomData();
                                int spring1 = model.Lines.Add(domN1, offN1, ELineGeomType.lgtStraightLine, ref sGeom1);
                                AxisVMHelper.SetLineSpringAttributes(model, spring1, internalStiffY, internalStiffZ, 0, refId);
                                springTracker.Add(key1);
                                springLineIds.Add(spring1);
                                linesToIntersectList.Add(spring1);
                            }
                            
                            string key2 = string.Format("{0}_{1}", domN2, offN2);
                            if (!springTracker.Contains(key2))
                            {
                                RLineGeomData sGeom2 = new RLineGeomData();
                                int spring2 = model.Lines.Add(domN2, offN2, ELineGeomType.lgtStraightLine, ref sGeom2);
                                AxisVMHelper.SetLineSpringAttributes(model, spring2, internalStiffY, internalStiffZ, 0, refId);
                                springTracker.Add(key2);
                                springLineIds.Add(spring2);
                                linesToIntersectList.Add(spring2);
                            }
                        }
                    }
                    u += spacing;
                }
            }
            finally
            {
                model.EndUpdate();
            }

            // Perform intersection of boundary lines and spring lines AFTER EndUpdate so the spatial database is updated
            if (linesToIntersectList.Count > 0)
            {
                Array linesArray = Array.CreateInstance(typeof(int), new int[] { linesToIntersectList.Count }, new int[] { 1 });
                for (int i = 0; i < linesToIntersectList.Count; i++)
                {
                    linesArray.SetValue(linesToIntersectList[i], i + 1);
                }
                try
                {
                    int crossRes = model.Lines.CrossLines(linesArray);
                    if (crossRes < 0)
                    {
                        System.Diagnostics.Debug.WriteLine("CrossLines failed with error: " + crossRes);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
            }

            MessageBox.Show("Successfully generated Domain, Offset Studs/Beams, and Connection Springs!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
