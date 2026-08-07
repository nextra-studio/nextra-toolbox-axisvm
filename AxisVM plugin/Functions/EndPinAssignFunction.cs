using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    internal struct Coord : IEquatable<Coord>
    {
        public double X, Y, Z;
        public Coord(double x, double y, double z) { X = Math.Round(x, 4); Y = Math.Round(y, 4); Z = Math.Round(z, 4); }
        public bool Equals(Coord other) { return X == other.X && Y == other.Y && Z == other.Z; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode(); }
    }

    public class EndPinAssignFunction
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                Array selectedLines;
                model.Lines.GetSelectedItemIds(out selectedLines);

                if (selectedLines == null || selectedLines.Length == 0)
                {
                    MessageBox.Show("No lines selected.", "End Pin Assign", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                using (EndPinAssignForm form = new EndPinAssignForm())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        model.BeginUpdate();
                        try
                        {
                            Array lineDataArr;
                            model.Lines.BulkGetLineData(selectedLines, out lineDataArr);

                            Dictionary<Coord, List<int>> nodeCounts = new Dictionary<Coord, List<int>>();
                            Dictionary<int, RLineData> lineDataMap = new Dictionary<int, RLineData>();
                            Dictionary<int, Coord> lineStartCoords = new Dictionary<int, Coord>();
                            Dictionary<int, Coord> lineEndCoords = new Dictionary<int, Coord>();

                            for (int i = 1; i <= lineDataArr.Length; i++)
                            {
                                int lineId = (int)selectedLines.GetValue(i);
                                RLineData lineData = (RLineData)lineDataArr.GetValue(i);
                                lineDataMap[lineId] = lineData;
                                
                                AxisVM.RNode n1 = new AxisVM.RNode();
                                model.Nodes.GetNode(lineData.NodeId1, ref n1);
                                Coord c1 = new Coord(n1.x, n1.y, n1.z);
                                lineStartCoords[lineId] = c1;

                                AxisVM.RNode n2 = new AxisVM.RNode();
                                model.Nodes.GetNode(lineData.NodeId2, ref n2);
                                Coord c2 = new Coord(n2.x, n2.y, n2.z);
                                lineEndCoords[lineId] = c2;

                                if (!nodeCounts.ContainsKey(c1)) nodeCounts[c1] = new List<int>();
                                if (!nodeCounts.ContainsKey(c2)) nodeCounts[c2] = new List<int>();

                                nodeCounts[c1].Add(lineId);
                                nodeCounts[c2].Add(lineId);
                            }

                            // Find all line IDs we need to consider. 
                            // If user selected 1 line, we automatically find all lines connected to its nodes
                            // to determine continuity.
                            List<int> linesToProcess = new List<int>();
                            for (int i = 1; i <= lineDataArr.Length; i++)
                            {
                                linesToProcess.Add((int)selectedLines.GetValue(i));
                            }

                            if (selectedLines.Length == 1)
                            {
                                int selId = linesToProcess[0];
                                Coord c1 = lineStartCoords[selId];
                                Coord c2 = lineEndCoords[selId];
                                
                                // To find connected lines efficiently, we will just scan a reasonable range of IDs
                                // until we find enough lines, or just scan 1 to Count*5
                                int found = 0;
                                int target = model.Lines.Count;
                                for (int i = 1; i <= target * 5; i++)
                                {
                                    try
                                    {
                                        if (i == selId) { found++; continue; }
                                        var testLine = model.Lines.Item[i];
                                        if (testLine != null)
                                        {
                                            found++;
                                            // Get its nodes
                                            AxisVM.RNode tn1 = new AxisVM.RNode();
                                            AxisVM.RNode tn2 = new AxisVM.RNode();
                                            // To get line data safely without slow COM exception, we can just use GetLineData
                                            // But since we just need nodes, we can use the BulkGetLineData logic for a batch, 
                                            // or just GetLineData one by one.
                                        }
                                        if (found >= target) break;
                                    }
                                    catch { }
                                }
                            }
                            
                            // To make this perfectly robust and fast, let's just use the selected lines for the chain graph,
                            // BUT if selectedLines.Length == 1, we will specifically search the model for lines sharing its nodes.
                            if (selectedLines.Length == 1)
                            {
                                int selId = linesToProcess[0];
                                AxisVM.RNode n1 = new AxisVM.RNode(); model.Nodes.GetNode(lineDataMap[selId].NodeId1, ref n1);
                                AxisVM.RNode n2 = new AxisVM.RNode(); model.Nodes.GetNode(lineDataMap[selId].NodeId2, ref n2);
                                
                                // Let's just find lines connected to NodeId1 or NodeId2
                                // We can do this by iterating all members, which is usually smaller and faster
                                for (int i = 1; i <= model.Members.Count * 2; i++)
                                {
                                    try
                                    {
                                        var mem = model.Members.Item[i];
                                        if (mem != null)
                                        {
                                            Array mLines;
                                            mem.GetLines(out mLines);
                                            if (mLines != null)
                                            {
                                                for (int j = 1; j <= mLines.Length; j++)
                                                {
                                                    int mLineId = (int)mLines.GetValue(j);
                                                    if (!linesToProcess.Contains(mLineId))
                                                    {
                                                        System.Array ids = System.Array.CreateInstance(typeof(int), new int[] { 1 }, new int[] { 1 });
                                                        ids.SetValue(mLineId, 1);
                                                        System.Array lData;
                                                        model.Lines.BulkGetLineData(ids, out lData);
                                                        if (lData != null && lData.Length > 0)
                                                        {
                                                            RLineData d = (RLineData)lData.GetValue(1);
                                                            if (d.NodeId1 == lineDataMap[selId].NodeId1 || d.NodeId2 == lineDataMap[selId].NodeId1 ||
                                                                d.NodeId1 == lineDataMap[selId].NodeId2 || d.NodeId2 == lineDataMap[selId].NodeId2)
                                                            {
                                                                linesToProcess.Add(mLineId);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch {}
                                }

                                // Now re-populate lineDataMap, lineStartCoords, lineEndCoords, nodeCounts with ALL linesToProcess
                                System.Array allIds = System.Array.CreateInstance(typeof(int), new int[] { linesToProcess.Count }, new int[] { 1 });
                                for (int i = 0; i < linesToProcess.Count; i++) allIds.SetValue(linesToProcess[i], i + 1);
                                
                                model.Lines.BulkGetLineData(allIds, out lineDataArr);
                                
                                lineDataMap.Clear();
                                lineStartCoords.Clear();
                                lineEndCoords.Clear();
                                nodeCounts.Clear();

                                for (int i = 1; i <= lineDataArr.Length; i++)
                                {
                                    int lineId = (int)allIds.GetValue(i);
                                    RLineData lineData = (RLineData)lineDataArr.GetValue(i);
                                    lineDataMap[lineId] = lineData;
                                    
                                    AxisVM.RNode t1 = new AxisVM.RNode();
                                    model.Nodes.GetNode(lineData.NodeId1, ref t1);
                                    Coord c1 = new Coord(t1.x, t1.y, t1.z);
                                    lineStartCoords[lineId] = c1;

                                    AxisVM.RNode t2 = new AxisVM.RNode();
                                    model.Nodes.GetNode(lineData.NodeId2, ref t2);
                                    Coord c2 = new Coord(t2.x, t2.y, t2.z);
                                    lineEndCoords[lineId] = c2;

                                    if (!nodeCounts.ContainsKey(c1)) nodeCounts[c1] = new List<int>();
                                    if (!nodeCounts.ContainsKey(c2)) nodeCounts[c2] = new List<int>();

                                    nodeCounts[c1].Add(lineId);
                                    nodeCounts[c2].Add(lineId);
                                }
                            }

                            // Build strictly continuous chains based on Angle Tolerance
                            Dictionary<int, int> lineToChainMap = new Dictionary<int, int>();
                            int currentChain = 0;
                            foreach (int lId in lineDataMap.Keys)
                            {
                                if (lineToChainMap.ContainsKey(lId)) continue;
                                
                                Queue<int> q = new Queue<int>();
                                q.Enqueue(lId);
                                lineToChainMap[lId] = currentChain;
                                
                                while (q.Count > 0)
                                {
                                    int curr = q.Dequeue();
                                    Coord c1 = lineStartCoords[curr];
                                    Coord c2 = lineEndCoords[curr];
                                    
                                    // Check neighbors at c1
                                    foreach (int neighbor in nodeCounts[c1])
                                    {
                                        if (neighbor != curr && !lineToChainMap.ContainsKey(neighbor))
                                        {
                                            // Only group if they are continuous within tolerance
                                            if (IsContinuousStrict(curr, neighbor, c1, form.AngleTolerance, lineStartCoords, lineEndCoords))
                                            {
                                                lineToChainMap[neighbor] = currentChain;
                                                q.Enqueue(neighbor);
                                            }
                                        }
                                    }
                                    // Check neighbors at c2
                                    foreach (int neighbor in nodeCounts[c2])
                                    {
                                        if (neighbor != curr && !lineToChainMap.ContainsKey(neighbor))
                                        {
                                            if (IsContinuousStrict(curr, neighbor, c2, form.AngleTolerance, lineStartCoords, lineEndCoords))
                                            {
                                                lineToChainMap[neighbor] = currentChain;
                                                q.Enqueue(neighbor);
                                            }
                                        }
                                    }
                                }
                                currentChain++;
                            }

                            // Now identify the true "free ends" for each chain
                            Dictionary<int, List<Coord>> chainFreeEnds = new Dictionary<int, List<Coord>>();
                            
                            // Re-build node counts but STRICTLY PER CHAIN
                            Dictionary<int, Dictionary<Coord, int>> chainNodeCounts = new Dictionary<int, Dictionary<Coord, int>>();
                            
                            for (int i = 1; i <= lineDataMap.Count; i++)
                            {
                                int lineId = linesToProcess[i - 1];
                                int chainId = lineToChainMap[lineId];
                                Coord c1 = lineStartCoords[lineId];
                                Coord c2 = lineEndCoords[lineId];
                                
                                if (!chainNodeCounts.ContainsKey(chainId)) chainNodeCounts[chainId] = new Dictionary<Coord, int>();
                                
                                if (!chainNodeCounts[chainId].ContainsKey(c1)) chainNodeCounts[chainId][c1] = 0;
                                if (!chainNodeCounts[chainId].ContainsKey(c2)) chainNodeCounts[chainId][c2] = 0;
                                
                                chainNodeCounts[chainId][c1]++;
                                chainNodeCounts[chainId][c2]++;
                            }

                            for (int i = 1; i <= lineDataMap.Count; i++)
                            {
                                // We iterate over linesToProcess instead of selectedLines
                                int lineId = linesToProcess[i - 1];
                                int chainId = lineToChainMap[lineId];
                                Coord c1 = lineStartCoords[lineId];
                                Coord c2 = lineEndCoords[lineId];
                                
                                if (!chainFreeEnds.ContainsKey(chainId)) chainFreeEnds[chainId] = new List<Coord>();
                                
                                // A node is a true free end of the chain if it appears exactly ONCE in the chain's lines
                                if (chainNodeCounts[chainId][c1] == 1 && !chainFreeEnds[chainId].Contains(c1)) chainFreeEnds[chainId].Add(c1);
                                if (chainNodeCounts[chainId][c2] == 1 && !chainFreeEnds[chainId].Contains(c2)) chainFreeEnds[chainId].Add(c2);
                            }

                            foreach (var kvp in chainFreeEnds)
                            {
                                kvp.Value.Sort((a, b) => {
                                    int zCmp = a.Z.CompareTo(b.Z);
                                    if (zCmp != 0) return zCmp;
                                    int xCmp = a.X.CompareTo(b.X);
                                    if (xCmp != 0) return xCmp;
                                    return a.Y.CompareTo(b.Y);
                                });
                            }

                            HashSet<int> chainTorsionReleased = new HashSet<int>();

                            for (int i = 1; i <= selectedLines.Length; i++)
                            {
                                int lineId = (int)selectedLines.GetValue(i);
                                Coord c1 = lineStartCoords[lineId];
                                Coord c2 = lineEndCoords[lineId];
                                int chainId = lineToChainMap[lineId];

                                bool isStartFree = false;
                                bool isEndFree = false;

                                List<Coord> freeEnds = chainFreeEnds.ContainsKey(chainId) ? chainFreeEnds[chainId] : new List<Coord>();
                                Coord? chainStart = freeEnds.Count > 0 ? freeEnds[0] : (Coord?)null;
                                Coord? chainEnd = freeEnds.Count > 1 ? freeEnds[freeEnds.Count - 1] : (Coord?)null;
                                
                                bool c1Free = chainNodeCounts[chainId][c1] == 1;
                                bool c2Free = chainNodeCounts[chainId][c2] == 1;

                                if (form.Target == PinTarget.ChainBothEnds)
                                {
                                    isStartFree = c1Free;
                                    isEndFree = c2Free;
                                }
                                else if (form.Target == PinTarget.ChainStartNode)
                                {
                                    isStartFree = c1Free && chainStart.HasValue && c1.Equals(chainStart.Value);
                                    isEndFree = c2Free && chainStart.HasValue && c2.Equals(chainStart.Value);
                                }
                                else if (form.Target == PinTarget.ChainEndNode)
                                {
                                    isStartFree = c1Free && chainEnd.HasValue && c1.Equals(chainEnd.Value);
                                    isEndFree = c2Free && chainEnd.HasValue && c2.Equals(chainEnd.Value);
                                }

                                if (isStartFree || isEndFree)
                                {
                                    var lineItem = model.Lines.Item[lineId];

                                    bool applyTorsionStart = false;
                                    bool applyTorsionEnd = false;
                                    
                                    if (form.ReleaseTorsion && isStartFree)
                                    {
                                        if (!chainTorsionReleased.Contains(chainId))
                                        {
                                            applyTorsionStart = true;
                                            chainTorsionReleased.Add(chainId);
                                        }
                                    }
                                    
                                    if (form.ReleaseTorsion && isEndFree)
                                    {
                                        if (!chainTorsionReleased.Contains(chainId))
                                        {
                                            applyTorsionEnd = true;
                                            chainTorsionReleased.Add(chainId);
                                        }
                                    }

                                    // Fetch EXISTING releases so we do not overwrite the other end!
                                    RReleases relStart = new RReleases();
                                    RReleases relEnd = new RReleases();
                                    lineItem.GetStartReleases(ref relStart);
                                    lineItem.GetEndReleases(ref relEnd);

                                    if (isStartFree) relStart = ModifyRelease(relStart, form, applyTorsionStart);
                                    if (isEndFree) relEnd = ModifyRelease(relEnd, form, applyTorsionEnd);

                                    if (isStartFree) lineItem.SetStartReleases(ref relStart);
                                    if (isEndFree) lineItem.SetEndReleases(ref relEnd);
                                }
                            }
                        }
                        finally
                        {
                            model.EndUpdate();
                        }

                        MessageBox.Show("End pins assigned successfully.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        private static RReleases ModifyRelease(RReleases rel, EndPinAssignForm form, bool applyTorsion)
        {
            if (form.IsSemiRigid)
            {
                rel.yy = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };
                rel.zz = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };
                
                if (applyTorsion)
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = form.Stiffness };
                else
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtRigid };
            }
            else
            {
                rel.yy = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                rel.zz = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                
                if (applyTorsion)
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                else
                    rel.xx = new RRelease() { ReleaseType = EReleaseType.rtRigid };
            }
            
            // Note: We intentionally DO NOT modify x, y, and z translational releases.
            // We only touch rotations.
            
            return rel;
        }

        private static bool IsContinuousStrict(int currentLineId, int otherLineId, Coord sharedCoord, double angleTolerance, Dictionary<int, Coord> lineStartCoords, Dictionary<int, Coord> lineEndCoords)
        {
            Coord cN1 = lineStartCoords[currentLineId];
            Coord cN2 = lineEndCoords[currentLineId];
            
            double cx, cy, cz;
            if (cN1.Equals(sharedCoord)) { cx = cN2.X - cN1.X; cy = cN2.Y - cN1.Y; cz = cN2.Z - cN1.Z; }
            else { cx = cN1.X - cN2.X; cy = cN1.Y - cN2.Y; cz = cN1.Z - cN2.Z; }
            
            double cLen = Math.Sqrt(cx*cx + cy*cy + cz*cz);
            if (cLen < 1e-6) return false;
            cx /= cLen; cy /= cLen; cz /= cLen;
            
            Coord oN1 = lineStartCoords[otherLineId];
            Coord oN2 = lineEndCoords[otherLineId];
            
            double ox, oy, oz;
            if (oN1.Equals(sharedCoord)) { ox = oN2.X - oN1.X; oy = oN2.Y - oN1.Y; oz = oN2.Z - oN1.Z; }
            else { ox = oN1.X - oN2.X; oy = oN1.Y - oN2.Y; oz = oN1.Z - oN2.Z; }
            
            double oLen = Math.Sqrt(ox*ox + oy*oy + oz*oz);
            if (oLen < 1e-6) return false;
            ox /= oLen; oy /= oLen; oz /= oLen;
            
            double dot = cx*ox + cy*oy + cz*oz;
            if (dot < -1) dot = -1;
            if (dot > 1) dot = 1;
            
            double angleRad = Math.Acos(dot);
            double angleDeg = angleRad * 180.0 / Math.PI;
            
            double deviation = Math.Abs(180.0 - angleDeg);
            
            if (deviation <= angleTolerance + 1e-5)
            {
                return true;
            }
            
            return false;
        }
    }
}
