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
            double panelThickness = (double)form.numPanelThickness.Value / 1000.0;
            int studMatId = form.cmbStudMaterial.SelectedIndex + 1;
            int studSecId = form.cmbStudSection.SelectedIndex + 1;
            double spacing = (double)form.numStudSpacing.Value / 1000.0;
            double studOffset = (double)form.numStudOffset.Value / 1000.0;

            int topBeamMatId = form.cmbTopBeamMaterial.SelectedIndex + 1;
            int topBeamSecId = form.cmbTopBeamSection.SelectedIndex + 1;
            double fastenerSpacing_m = (double)form.numFastenerSpacing.Value / 1000.0;
            double slipModulus_kNm = (double)form.numSlipModulus.Value; // 1 N/mm = 1 kN/m

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
                // Create reference vectors for stud orientation (local Z axis)
                int refLeftId = AxisVMHelper.CreateReferenceVector(model, nx, ny, nz);
                int refRightId = AxisVMHelper.CreateReferenceVector(model, -nx, -ny, -nz);
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
                    
                    int matId = 0, secId = 0;
                    bool isStud = (dV > dU);
                    if (isStud) 
                    {
                        matId = studMatId; secId = studSecId; // End Stud
                        int lineId = (int)selectedLines.GetValue(i + 1); // 1-based array from COM
                        AxisVMHelper.SetLineBeamAttributes(model, lineId, matId, secId, isRib: true);
                        AxisVMHelper.SetLineReleases(model, lineId, isPin: true);
                        
                        double lu = uv.Item1;
                        if (Math.Abs(lu - uMax) < 1e-3) {
                            model.Lines.Item[lineId].Reference = refRightId;
                        } else {
                            model.Lines.Item[lineId].Reference = refLeftId;
                        }
                    }
                }

                int prevOffN1 = -1, prevOffN2 = -1;
                double u = uMin;
                for (int i = 0; i <= numSpaces + 1; i++) // include uMin and uMax
                {
                    if (i == 0) u = uMin;
                    else if (i == numSpaces + 1) u = uMax;
                    else u = uMin + gap + (i - 1) * spacing;

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
                            else if (Math.Abs(u - lu1) < 1e-3)
                            {
                                vIntersects.Add(lv1);
                                vIntersects.Add(lv2);
                            }
                        }
                    }
                    vIntersects.Sort();
                    
                    if (vIntersects.Count >= 2)
                    {
                        double vStart = vIntersects[0];
                        double vEnd = vIntersects[vIntersects.Count - 1];
                        if (Math.Abs(vStart - vEnd) < 1e-3) continue;
                        
                        double dx1 = p0.x + u * Ux + vStart * Vx, dy1 = p0.y + u * Uy + vStart * Vy, dz1 = p0.z + u * Uz + vStart * Vz;
                        double dx2 = p0.x + u * Ux + vEnd * Vx, dy2 = p0.y + u * Uy + vEnd * Vy, dz2 = p0.z + u * Uz + vEnd * Vz;
                        
                        double x1 = dx1 + ox, y1 = dy1 + oy, z1 = dz1 + oz;
                        double x2 = dx2 + ox, y2 = dy2 + oy, z2 = dz2 + oz;

                        int domN1 = model.Nodes.Add(dx1, dy1, dz1);
                        int domN2 = model.Nodes.Add(dx2, dy2, dz2);

                        int offN1, offN2;
                        if (i == 0 || i == numSpaces + 1) {
                            offN1 = domN1;
                            offN2 = domN2;
                        } else {
                            offN1 = model.Nodes.Add(x1, y1, z1); // Bottom Offset Node
                            offN2 = model.Nodes.Add(x2, y2, z2); // Top Offset Node
                        }

                        // Generate segmented beams between offset studs
                        if (prevOffN1 != -1 && prevOffN2 != -1)
                        {
                            bool startSemiRigid = (i == 1);
                            bool endSemiRigid = (i == numSpaces + 1);

                            RLineGeomData bGeom1 = new RLineGeomData();
                            int bLine1 = model.Lines.Add(prevOffN1, offN1, ELineGeomType.lgtStraightLine, ref bGeom1);
                            AxisVMHelper.SetLineBeamAttributes(model, bLine1, topBeamMatId, topBeamSecId, isRib: false);
                            AxisVMHelper.SetLineEndReleases(model, bLine1, startSemiRigid, endSemiRigid);

                            RLineGeomData bGeom2 = new RLineGeomData();
                            int bLine2 = model.Lines.Add(prevOffN2, offN2, ELineGeomType.lgtStraightLine, ref bGeom2);
                            AxisVMHelper.SetLineBeamAttributes(model, bLine2, topBeamMatId, topBeamSecId, isRib: false);
                            AxisVMHelper.SetLineEndReleases(model, bLine2, startSemiRigid, endSemiRigid);
                        }

                        prevOffN1 = offN1;
                        prevOffN2 = offN2;

                        if (Math.Abs(u - uMin) < 1e-3 || Math.Abs(u - uMax) < 1e-3)
                        {
                            continue; // Skip creating the vertical stud line, it was already handled in Step 4
                        }
                        

                        
                        RLineGeomData geomData = new RLineGeomData();
                        int studId = model.Lines.Add(offN1, offN2, ELineGeomType.lgtStraightLine, ref geomData);
                        
                        AxisVMHelper.SetLineBeamAttributes(model, studId, studMatId, studSecId, isRib: false);
                        AxisVMHelper.SetLineReleases(model, studId, isPin: true); // internal studs are pinned
                        model.Lines.Item[studId].Reference = refLeftId; // Internal studs orient like the left boundary
                        
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
