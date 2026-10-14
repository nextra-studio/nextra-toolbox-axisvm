using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Addon "N. Convert Load Panel -> One-Way": takes the load panels the user has selected and
    /// rebuilds each one as its one-way equivalent (a single one-way panel, or the two edge strips
    /// + centre panel set), re-applying the uniform area load that was on the original.
    ///
    /// The panel geometry is produced by the shared <see cref="OneWayPanelBuilder"/>. Everything in
    /// this file is specific to the "convert an existing panel" workflow: reading the panel
    /// selection, turning its outline into lines, and carrying its area load across.
    /// </summary>
    public class ConvertLoadPanelLogic
    {
        public static int ExecuteConvert(IAxisVMApplication app)
        {
            try
            {
                var model = app.Models.Item[1];
                if (model == null) return -1;

                List<int> panelUids = SelectedPanelUids(model);
                if (panelUids.Count == 0)
                {
                    MessageBox.Show("Select one or more load panels to convert to one-way.", "Convert Load Panel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 0;
                }

                using (ConvertLoadPanelForm form = new ConvertLoadPanelForm())
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    OneWayPanelOptions opt = new OneWayPanelOptions
                    {
                        SpanningDirection = form.SpanningDirection,
                        NoSpacing = form.NoSpacing,
                        EdgeSpacingMm = form.EdgeSpacing,
                        AngleToleranceDeg = form.AngleTolerance
                    };

                    int panelsConverted = 0;
                    int newPanels = 0;
                    int loadsReapplied = 0;
                    List<string> notes = new List<string>();

                    model.BeginUpdate();
                    try
                    {
                        for (int p = 0; p < panelUids.Count; p++)
                        {
                            ConvertOnePanel(model, panelUids[p], p + 1, opt, notes,
                                            ref panelsConverted, ref newPanels, ref loadsReapplied);
                        }
                    }
                    finally
                    {
                        model.EndUpdate();
                        try { model.Refresh(); } catch { }
                    }

                    ShowConvertSummary(panelUids.Count, panelsConverted, newPanels, loadsReapplied, notes);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        private static void ConvertOnePanel(IAxisVMModel model, int panelUid, int label, OneWayPanelOptions opt, List<string> notes,
                                            ref int panelsConverted, ref int newPanels, ref int loadsReapplied)
        {
            int idx = model.LoadPanels.get_IndexOfUID(panelUid);
            if (idx <= 0) { notes.Add(string.Format("Panel {0}: no longer exists.", label)); return; }

            // 1. Boundary segments. Prefer the panel's real contour lines; if it is a user-defined
            //    polygon (GetContourLineIDs returns nothing), materialise straight lines from its
            //    outline so the rest of the pipeline has something to chain and split.
            List<ResolvedLine> segments = new List<ResolvedLine>();

            Array contourArr = null;
            int edgeCount = 0;
            try { edgeCount = model.LoadPanels.Item[idx].GetContourLineIDs(ref contourArr); }
            catch { edgeCount = 0; }

            if (edgeCount > 0 && contourArr != null)
            {
                foreach (int lineId in ToIdList(contourArr)) segments.Add(OneWayPanelBuilder.ResolveLine(model, lineId));
            }

            if (segments.Count < 3)
            {
                string polyErr;
                List<ResolvedLine> fromPoly;
                if (TryBuildSegmentsFromContourPolygon(model, idx, out fromPoly, out polyErr) && fromPoly.Count >= 3)
                {
                    segments = fromPoly;
                    notes.Add(string.Format("Panel {0}: contour had no lines - {1} boundary line(s) were created from its outline.", label, segments.Count));
                }
                else
                {
                    notes.Add(string.Format("Panel {0} skipped: its contour could not be resolved into lines ({1}).", label, polyErr ?? "unknown"));
                    return;
                }
            }

            // Keep the boundary by UID so it can be rebuilt even if generation splits lines.
            List<int> segUids = new List<int>(segments.Count);
            foreach (ResolvedLine s in segments) segUids.Add(model.Lines.get_UID(s.LineId));

            // 2. Capture the area load(s) sitting on the panel.
            List<CapturedLoad> loads = CapturePanelLoads(model, idx, label, notes);

            // 3. Delete the original - AddFromLineIDs on the same contour would otherwise be rejected
            //    with lopeSameLoadPanelExists. The contour lines themselves are untouched.
            try { model.LoadPanels.Delete(idx); }
            catch (Exception ex) { notes.Add(string.Format("Panel {0}: could not be deleted ({1}); skipped.", label, ex.Message)); return; }

            // 4. Regenerate as one-way panel(s).
            List<int> madeUids = OneWayPanelBuilder.GeneratePanels(model, segments, opt, label, notes);

            // 5. Safety net - if nothing could be built, put a plain panel back so the user is not
            //    left with a hole, and still re-apply the captured load.
            if (madeUids.Count == 0)
            {
                notes.Add(string.Format("Panel {0}: no one-way panel could be built - a plain load panel was restored.", label));
                List<int> restoreIds = new List<int>();
                foreach (int uid in segUids)
                {
                    int id = model.Lines.get_IndexOfUID(uid);
                    if (id > 0) restoreIds.Add(id);
                }
                if (restoreIds.Count >= 3)
                {
                    Array arr = Array.CreateInstance(typeof(int), new int[] { restoreIds.Count }, new int[] { 1 });
                    for (int i = 0; i < restoreIds.Count; i++) arr.SetValue(restoreIds[i], i + 1);
                    int rid = 0;
                    try { rid = model.LoadPanels.AddFromLineIDs(ref arr); } catch { rid = 0; }
                    if (rid > 0) madeUids.Add(model.LoadPanels.get_UID(rid));
                }
                if (madeUids.Count == 0) return;
            }
            else
            {
                panelsConverted++;
                newPanels += madeUids.Count;
            }

            // 6. Re-apply the captured load(s) to every regenerated panel.
            loadsReapplied += ReapplyLoads(model, madeUids, loads, notes, label);
        }

        /// <summary>
        /// Builds straight boundary lines from a load panel's contour polygon. This works even for
        /// lpctUserDefined panels (drawn as a free polygon), which have no contour lines at all.
        /// Every distinct vertex is turned into a node once - via a local coordinate map so shared
        /// corners stay a single node even if IAxisVMNodes.Add were to return duplicates - and
        /// consecutive vertices are joined by a straight line.
        /// </summary>
        private static bool TryBuildSegmentsFromContourPolygon(IAxisVMModel model, int panelIdx, out List<ResolvedLine> segments, out string err)
        {
            segments = new List<ResolvedLine>();
            err = null;

            AxisVM.AxisVMLines3d poly = null;
            int edges = 0;
            try { edges = model.LoadPanels.Item[panelIdx].GetContourPolygon(out poly); }
            catch (Exception ex) { err = ex.Message; return false; }

            if (poly == null || edges <= 0) { err = "no contour polygon"; return false; }

            int count;
            try { count = poly.Count; } catch { count = edges; }
            if (count < 3) { err = "polygon has fewer than 3 edges"; return false; }

            // rounded "x|y|z" -> node index, so a vertex shared by two edges maps to one node.
            Dictionary<string, int> nodeAt = new Dictionary<string, int>();
            Func<AxisVM.RPoint3d, int> nodeFor = delegate (AxisVM.RPoint3d pt)
            {
                string key = Math.Round(pt.x, 4) + "|" + Math.Round(pt.y, 4) + "|" + Math.Round(pt.z, 4);
                int nid;
                if (nodeAt.TryGetValue(key, out nid)) return nid;
                nid = model.Nodes.Add(pt.x, pt.y, pt.z);
                nodeAt[key] = nid;
                return nid;
            };

            for (int i = 1; i <= count; i++)
            {
                RLine3d seg = new RLine3d();
                try { poly.GetItem(i, ref seg); }
                catch (Exception ex) { err = "GetItem failed (" + ex.Message + ")"; return false; }

                int n1 = nodeFor(seg.P1);
                int n2 = nodeFor(seg.P2);
                if (n1 <= 0 || n2 <= 0 || n1 == n2) { err = "degenerate polygon edge"; return false; }

                int lineId = OneWayPanelBuilder.AddStraightLine(model, n1, n2);
                if (lineId <= 0) { err = "could not add a boundary line"; return false; }
                segments.Add(OneWayPanelBuilder.ResolveLine(model, lineId));
            }

            return true;
        }

        /// <summary>Load panels have no GetSelectedItemIds; the selection is read index by index.</summary>
        private static List<int> SelectedPanelUids(IAxisVMModel model)
        {
            List<int> uids = new List<int>();
            if (model.LoadPanels == null) return uids;

            int count = 0;
            try { count = model.LoadPanels.Count; } catch { return uids; }

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    if (model.LoadPanels.get_Selected(i) != ELongBoolean.lbTrue) continue;
                    uids.Add(model.LoadPanels.get_UID(i));
                }
                catch { }
            }
            return uids;
        }

        // ------------------------------------------------------------------
        // Load capture / re-apply
        // ------------------------------------------------------------------

        /// <summary>An area load read off a load panel, with enough to recreate it.</summary>
        private class CapturedLoad
        {
            public int LoadIndex;
            public int LoadType;              // (int)ELoadType
            public int LoadCaseId;
            public int Component;             // (int)EAxis
            public int DistributionType;      // (int)EDistributionType
            public int LoadDistributionType;  // (int)ELoadDistributionType
            public double P1, P2, P3;
            public int WindowLoad;            // (int)ELongBoolean
            public AxisVM.AxisVMLines3d Poly; // the original load polygon (may be null)
            public Array RawRecord;           // the original GetLoad array (may be null)

            /// <summary>
            /// True when the load is bound to the load panel itself and would be deleted with it, so
            /// it must be re-created. False when it sits on a surviving domain - then it is left in
            /// place and the new one-way panels distribute it automatically.
            /// </summary>
            public bool PanelBound;
        }

        /// <summary>
        /// Scans every load in the model and returns those whose target set includes
        /// <paramref name="panelId"/>. Diagnostic notes describe what was found so the run summary
        /// makes it obvious when a panel's load could not be located.
        /// </summary>
        private static List<CapturedLoad> CapturePanelLoads(IAxisVMModel model, int panelId, int label, List<string> notes)
        {
            List<CapturedLoad> result = new List<CapturedLoad>();

            int count = 0;
            try { count = model.Loads.Count; } catch { }

            // The domain(s) sitting under this panel - a domain area load on one of them is really
            // "the panel's load" too (the one-way panels will end up distributing it).
            HashSet<int> panelDomains = DomainsUnderPanel(model, panelId);

            int inventoryShown = 0;

            for (int i = 1; i <= count; i++)
            {
                int lt = -1;
                try { lt = (int)model.Loads.get_LoadType(i); } catch { }

                int elem = 0;
                try { elem = model.Loads.get_ElementId(i); } catch { }

                int lcase = 1;
                try { lcase = model.Loads.get_LoadCaseId(i); } catch { }

                Array domIds = null, srfIds = null, lpIds = null;
                try { model.Loads.GetDomains_Surfaces_LoadPanels(i, out domIds, out srfIds, out lpIds); }
                catch { }
                List<int> lpList = ToIdList(lpIds);
                List<int> domList = ToIdList(domIds);

                // Diagnostic inventory - first 25 loads.
                if (inventoryShown < 25)
                {
                    notes.Add(string.Format("  load #{0}: type={1} case={2} elem={3} lp=[{4}] dom=[{5}]",
                        i, lt, lcase, elem, string.Join(",", lpList), string.Join(",", domList)));
                    inventoryShown++;
                }

                bool areaLoadType =
                    lt == 0x21 /*ltDomainPolyArea*/ || lt == 0x28 /*ltDomainPolyAssoc*/ ||
                    lt == 0x22 /*ltDomainLinear*/   || lt == 0x13 /*ltDomainConstant/Distributed*/ ||
                    lt == 0x1C /*ltDomainConcentrated*/ || lt == 0x26 /*ltLoadDomainPolyLine*/ ||
                    lt == -1;

                bool onThisPanel = lpList.Contains(panelId) || (elem == panelId && domList.Count == 0);
                bool onPanelDomain = panelDomains.Overlaps(domList) || (elem > 0 && panelDomains.Contains(elem));
                if (!onThisPanel && !onPanelDomain) continue;

                // A load that is ALSO on a domain survives the panel deletion - do not re-create it.
                bool panelBound = onThisPanel && domList.Count == 0 && !panelDomains.Contains(elem);

                Array data = null;
                int getLoadRet = 0;
                string getLoadErr = "";
                try { getLoadRet = model.Loads.GetLoad(i, out data); }
                catch (Exception ex) { getLoadErr = ex.Message; }

                byte[] rawBytes = null;
                if (data != null && data.Length > 0)
                {
                    int lb = data.GetLowerBound(0);
                    int len = data.Length;
                    rawBytes = new byte[len];
                    for (int b = 0; b < len; b++)
                    {
                        rawBytes[b] = Convert.ToByte(data.GetValue(lb + b));
                    }
                }

                AxisVM.AxisVMLines3d poly = null;
                try { model.Loads.GetPoly(i, out poly); } catch { }

                CapturedLoad cl = new CapturedLoad
                {
                    LoadIndex = i,
                    LoadType = lt,
                    RawRecord = data,
                    Poly = poly,
                    LoadCaseId = lcase,
                    Component = 2,
                    DistributionType = 0,
                    LoadDistributionType = 0,
                    P1 = 0, P2 = 0, P3 = 0,
                    WindowLoad = 0,
                    PanelBound = panelBound
                };

                if (rawBytes != null && rawBytes.Length >= 24)
                {
                    int rLc = BitConverter.ToInt32(rawBytes, 0);
                    if (rLc > 0 && rLc <= 1000) cl.LoadCaseId = rLc;

                    // Unpack doubles
                    double p1_20 = rawBytes.Length >= 28 ? BitConverter.ToDouble(rawBytes, 20) : 0;
                    double p2_28 = rawBytes.Length >= 36 ? BitConverter.ToDouble(rawBytes, 28) : 0;
                    double p3_36 = rawBytes.Length >= 44 ? BitConverter.ToDouble(rawBytes, 36) : 0;

                    double p1_24 = rawBytes.Length >= 32 ? BitConverter.ToDouble(rawBytes, 24) : 0;
                    double p2_32 = rawBytes.Length >= 40 ? BitConverter.ToDouble(rawBytes, 32) : 0;
                    double p3_40 = rawBytes.Length >= 48 ? BitConverter.ToDouble(rawBytes, 40) : 0;

                    int comp = 2; // Default to Z (gravity / floor load)
                    double val = 0;

                    if (Math.Abs(p3_36) > 1e-7 || Math.Abs(p3_40) > 1e-7)
                    {
                        comp = 2;
                        val = Math.Abs(p3_36) > 1e-7 ? p3_36 : p3_40;
                    }
                    else if (Math.Abs(p2_28) > 1e-7 || Math.Abs(p2_32) > 1e-7)
                    {
                        comp = 1;
                        val = Math.Abs(p2_28) > 1e-7 ? p2_28 : p2_32;
                    }
                    else if (Math.Abs(p1_20) > 1e-7 || Math.Abs(p1_24) > 1e-7)
                    {
                        val = Math.Abs(p1_20) > 1e-7 ? p1_20 : p1_24;
                        int c16 = BitConverter.ToInt32(rawBytes, 16);
                        int c12 = BitConverter.ToInt32(rawBytes, 12);
                        int c20 = rawBytes.Length >= 24 ? BitConverter.ToInt32(rawBytes, 20) : 0;

                        if (c16 == 2 || c12 == 2 || c20 == 2) comp = 2;
                        else if (c16 == 1 || c12 == 1 || c20 == 1) comp = 1;
                        else if (c16 == 0 && c12 == 0) comp = 2; // Default gravity Z
                        else comp = c16;
                    }
                    else
                    {
                        // Search any non-zero double
                        for (int off = 16; off <= rawBytes.Length - 8; off += 4)
                        {
                            double cand = BitConverter.ToDouble(rawBytes, off);
                            if (!double.IsNaN(cand) && !double.IsInfinity(cand) && Math.Abs(cand) > 1e-7 && Math.Abs(cand) < 1e9)
                            {
                                val = cand;
                                comp = 2; // Default Z
                                break;
                            }
                        }
                    }

                    cl.Component = comp;
                    cl.P1 = val;
                    cl.P2 = val;
                    cl.P3 = val;
                }

                result.Add(cl);

                notes.Add(string.Format("Panel {0}: {1} load #{2} (case {3}, P1 {4:F3}, comp {5}).",
                    label, panelBound ? "captured" : "on domain",
                    i, cl.LoadCaseId, cl.P1, cl.Component));
            }

            notes.Add(string.Format("Panel {0}: scanned {1} load(s); captured {2}.",
                label, count, result.Count));

            return result;
        }

        /// <summary>
        /// Domain indexes whose contour polygon roughly coincides with the load panel's contour
        /// (same centroid and area within 5%). Used to recognise a domain area load as belonging to
        /// the panel.
        /// </summary>
        private static HashSet<int> DomainsUnderPanel(IAxisVMModel model, int panelId)
        {
            HashSet<int> hits = new HashSet<int>();

            double pcx, pcy, parea;
            AxisVM.AxisVMLines3d panelPoly = null;
            try { model.LoadPanels.Item[panelId].GetContourPolygon(out panelPoly); } catch { }
            if (!PolyCentroidArea(panelPoly, out pcx, out pcy, out parea) || parea <= 1e-9) return hits;

            int dcount = 0;
            try { dcount = model.Domains.Count; } catch { }

            for (int d = 1; d <= dcount; d++)
            {
                AxisVM.AxisVMLines3d dpoly = null;
                try { dpoly = model.Domains.Item[d].ContourPolygon; } catch { }

                double dcx, dcy, darea;
                if (!PolyCentroidArea(dpoly, out dcx, out dcy, out darea) || darea <= 1e-9) continue;

                if (Math.Abs(darea - parea) / parea > 0.05) continue;
                if (Math.Abs(dcx - pcx) > 0.05 || Math.Abs(dcy - pcy) > 0.05) continue;

                hits.Add(d);
            }

            return hits;
        }

        /// <summary>Polygon centroid (XY) and absolute area from an AxisVMLines3d contour.</summary>
        private static bool PolyCentroidArea(AxisVM.AxisVMLines3d poly, out double cx, out double cy, out double area)
        {
            cx = cy = area = 0.0;
            if (poly == null) return false;

            int count = 0;
            try { count = poly.Count; } catch { }
            if (count < 3) return false;

            List<Tuple<double, double>> pts = new List<Tuple<double, double>>();
            for (int i = 1; i <= count; i++)
            {
                RLine3d l = new RLine3d();
                try { poly.GetItem(i, ref l); } catch { return false; }
                pts.Add(Tuple.Create(l.P1.x, l.P1.y));
            }

            double a = 0.0, sx = 0.0, sy = 0.0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = pts[i];
                var p1 = pts[(i + 1) % n];
                double cross = p0.Item1 * p1.Item2 - p1.Item1 * p0.Item2;
                a += cross;
                sx += (p0.Item1 + p1.Item1) * cross;
                sy += (p0.Item2 + p1.Item2) * cross;
            }
            a *= 0.5;
            if (Math.Abs(a) < 1e-9) return false;

            cx = sx / (6.0 * a);
            cy = sy / (6.0 * a);
            area = Math.Abs(a);
            return true;
        }

        /// <summary>
        /// Re-adds each captured load. The original load polygon (spanning the whole old panel area)
        /// is reused, so AxisVM attaches it to every new panel it overlaps; where that polygon is
        /// missing, each new panel's own contour polygon is used as a fallback. Returns how many
        /// AxisVM accepted.
        /// </summary>
        private static int ReapplyLoads(IAxisVMModel model, List<int> newPanelUids, List<CapturedLoad> loads, List<string> notes, int label)
        {
            if (loads.Count == 0) return 0;
            int applied = 0;

            foreach (CapturedLoad cl in loads)
            {
                // Domain loads survive the panel deletion; the new panels distribute them as-is.
                if (!cl.PanelBound) continue;

                bool loadApplied = false;

                // Try applying directly to each new load panel by ID
                foreach (int uid in newPanelUids)
                {
                    int pidx = model.LoadPanels.get_IndexOfUID(uid);
                    if (pidx <= 0) continue;

                    int lastErr = 0;
                    try
                    {
                        RLoadPanelLinear lin = new RLoadPanelLinear();
                        lin.LoadCaseId = cl.LoadCaseId;
                        lin.LoadPanelId = pidx;
                        lin.DistributionType = (EDistributionType)cl.DistributionType;
                        lin.LoadDistributionType = (ELoadDistributionType)cl.LoadDistributionType;
                        lin.Component = (EAxis)cl.Component;
                        lin.P1 = cl.P1;
                        lin.P2 = cl.P2;
                        lin.P3 = cl.P3;

                        int newIndex = model.Loads.AddLoadPanelLinear(ref lin);
                        if (newIndex > 0)
                        {
                            applied++;
                            loadApplied = true;
                        }
                        else
                        {
                            lastErr = newIndex;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add(string.Format("Panel {0}: AddLoadPanelLinear threw ({1}).", label, ex.Message));
                    }

                    if (!loadApplied && lastErr != 0)
                    {
                        notes.Add(string.Format("Panel {0}: AddLoadPanelLinear returned code {1}.", label, lastErr));
                    }
                }

                if (!loadApplied && cl.Poly != null)
                {
                    try
                    {
                        RLoadPanelPolyArea d = new RLoadPanelPolyArea();
                        d.LoadCaseId = cl.LoadCaseId;
                        d.Component = (EAxis)cl.Component;
                        d.DistributionType = (EDistributionType)cl.DistributionType;
                        d.LoadDistributionType = (ELoadDistributionType)cl.LoadDistributionType;
                        d.P1 = cl.P1;
                        d.P2 = cl.P2;
                        d.P3 = cl.P3;
                        d.WindowLoad = (ELongBoolean)cl.WindowLoad;

                        int newIndex = model.Loads.AddLoadPanelPolyArea(cl.Poly, ref d);
                        if (newIndex > 0)
                        {
                            applied++;
                            loadApplied = true;
                        }
                    }
                    catch { }
                }

                if (!loadApplied)
                {
                    notes.Add(string.Format("Panel {0}: load #{1} could not be re-applied to new panels (P1={2:0.###}).", label, cl.LoadIndex, cl.P1));
                }
            }

            return applied;
        }

        private static double ReadDouble(object rec, string field, double dflt)
        {
            if (rec == null) return dflt;
            try
            {
                var t = rec.GetType();
                var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (f != null) return Convert.ToDouble(f.GetValue(rec));
                var p = t.GetProperty(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p != null) return Convert.ToDouble(p.GetValue(rec, null));
            }
            catch { }
            return dflt;
        }

        private static int ReadInt(object rec, string field, int dflt)
        {
            if (rec == null) return dflt;
            try
            {
                var t = rec.GetType();
                var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (f != null) return Convert.ToInt32(f.GetValue(rec)); // works for boxed enums too
                var p = t.GetProperty(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p != null) return Convert.ToInt32(p.GetValue(rec, null));
            }
            catch { }
            return dflt;
        }

        private static List<int> ToIdList(Array arr)
        {
            List<int> ids = new List<int>();
            if (arr == null) return ids;

            int lower = arr.GetLowerBound(0);
            int upper = arr.GetUpperBound(0);
            for (int i = lower; i <= upper; i++)
            {
                try
                {
                    int id = Convert.ToInt32(arr.GetValue(i));
                    if (id > 0) ids.Add(id);
                }
                catch { }
            }
            return ids;
        }

        private static void ShowConvertSummary(int selected, int converted, int newPanels, int loadsReapplied, List<string> notes)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("Converted {0} of {1} selected load panel(s) into {2} one-way panel(s).",
                converted, selected, newPanels);
            sb.AppendLine();
            sb.AppendFormat("Panel area loads re-applied: {0}.", loadsReapplied);

            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                int shown = Math.Min(notes.Count, 30);
                for (int i = 0; i < shown; i++) sb.AppendLine(notes[i]);
                if (notes.Count > shown) sb.AppendLine("... and " + (notes.Count - shown) + " more.");
            }

            MessageBox.Show(sb.ToString(), "Convert Load Panel -> One-Way", MessageBoxButtons.OK,
                converted == selected ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
