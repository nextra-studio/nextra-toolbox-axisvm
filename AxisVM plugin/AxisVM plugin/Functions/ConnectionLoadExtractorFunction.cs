using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.UI;

/*
 * =====================================================================================
 * CONNECTION LOAD EXTRACTOR (CRITICAL FINDER) - LOGIC DOCUMENTATION
 * =====================================================================================
 *
 * Reads the end forces of the selected beams / ribs at the selected nodes for the chosen load
 * combinations, expresses them as the force each member exerts on the node (local or global axes),
 * and finds the governing combination (Max+ and Min-) for each of the six components in those axes.
 *
 * WORKFLOW:
 * 1. GATHER - selected lines + the lines of selected members; only ltBeam / ltRib are kept. Selected
 *    nodes pick which member ends are reported; with no node selected, both ends of every member are.
 * 2. LOCAL AXES (ResolveFrames) - IAxisVMMember.GetTrMatrix of the member each line belongs to. Whether
 *    the matrix stores the local axes as rows or as columns is decided from the data (the candidate whose
 *    x axis is parallel to the line is the right one) and reported in the Checks tab. A line running
 *    against its member's x gets the frame turned 180 degrees about z.
 * 3. FORCES - IAxisVMForces.GetLineForcesByLoadCombinationId per line and combination. The section with
 *    the smallest PosX is the start node, the largest PosX the end node.
 * 4. SIGN + TRANSFORM - ConnectionLoadMath.RawToNodeLocal (start +S, end -S) then LocalToGlobal.
 * 5. CRITICAL - for each of the six components: per member end, the combination with the largest and
 *    with the smallest value, plus the same over all reported ends.
 * 6. CHECKS - frame orthonormality / parallelism, PosX vs length, joint equilibrium at nodes where every
 *    connected line is reported, and a cross-check against AxisVM's own node reader
 *    (GetLineForcesByLoadCombinationIdConnectedToNode).
 *
 * The addon never edits the model; the only thing it changes is the selection ("Select in model").
 * =====================================================================================
 */

namespace AxisVM_plugin.Functions
{
    /// <summary>One end of one selected line, at a node the user is interested in.</summary>
    public class MemberEnd
    {
        public int NodeId;          // the node this end sits on
        public int LineId;          // AxisVM line index
        public int MemberId;        // AxisVM member index the line belongs to
        public bool IsStart;        // true = start of local x (PosX = 0), false = end (PosX = L)
        public LocalFrame Frame;    // the line's local axes in global coordinates
        public Vec3 NodePoint;      // the node's coordinates
        public Vec3 Inward;         // unit vector from the node into the member
        public double LineLength;

        /// <summary>"Start" or "End" for the tables.</summary>
        public string EndLabel { get { return IsStart ? "Start" : "End"; } }
    }

    /// <summary>The load on one member end for one load combination.</summary>
    public class EndLoadRow
    {
        public MemberEnd End;
        public int ComboId;         // 1-based load combination index
        public string ComboName;
        public double[] Raw;        // AxisVM internal forces at the section (N, Vy, Vz, Tx, My, Mz)
        public double[] NodeLocal;  // force the member exerts on the node, member local axes
        public double[] Global;     // the same, global axes
        public double[] Custom;     // the same, custom axes (null until a custom system is set)

        /// <summary>The six components in the requested system.</summary>
        public double[] In(LoadSystem system)
        {
            return system == LoadSystem.Local ? NodeLocal : system == LoadSystem.Custom ? Custom : Global;
        }
    }

    /// <summary>A Max+ or Min- result of one component, either for one member end or (End == null) over all ends.</summary>
    public class CriticalRow
    {
        public MemberEnd End;       // null = governing over every reported end
        public int Component;       // 0..5 in the result's axes (forces 0..2, moments 3..5)
        public CriticalKind Kind;
        public EndLoadRow Row;      // the combination that governs, with its full concurrent vector
        public bool SignMatches;    // false when "Max+" is not positive or "Min-" is not negative
    }

    /// <summary>Everything the result window shows.</summary>
    public class ExtractionResult
    {
        public LoadSystem Axes;                               // output AND critical search axes
        public CustomAxes Custom = new CustomAxes(0, 0);      // the angles of the custom system (kept when another system is shown)
        public List<MemberEnd> Ends = new List<MemberEnd>();  // every reported member end, in table order
        public List<EndLoadRow> All = new List<EndLoadRow>();

        /// <summary>"Global axes", "Local axes" or "Custom: αz = 30°, αx = 0°" for titles and exports.</summary>
        public string AxesText { get { return Axes == LoadSystem.Custom ? Custom.Describe() : Axes + " axes"; } }
        public List<CriticalRow> Critical = new List<CriticalRow>(); // all six components
        public List<string> Checks = new List<string>();
        public List<int> TargetNodeIds = new List<int>();
    }

    public class ConnectionLoadExtractorFunction
    {
        private const string Title = "Connection Load Extractor";

        /// <summary>A selected line as read from the model.</summary>
        private class LineInfo
        {
            public int LineId;
            public int MemberId;
            public int StartNodeId;
            public int EndNodeId;
            public Vec3 StartPoint, EndPoint;
            public Vec3 Direction;      // unit vector start node -> end node
            public double Length;
            public RMatrix3x3 Matrix;   // member transformation matrix, as returned
            public bool HasMatrix;
            public LocalFrame Frame;    // resolved local axes (null = unusable line)
        }

        /// <summary>Wraps a native window handle so a WinForms window can be owned by the AxisVM main form.</summary>
        private class HandleOwner : IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public HandleOwner(int handle) { Handle = new IntPtr(handle); }
        }

        // The currently open result window; a new run closes the previous one so they don't pile up
        private static ConnectionLoadResultForm openResultForm;

        public static int Execute(IAxisVMApplication app, int ownerHandle = 0)
        {
            IAxisVMModel model = null;
            try
            {
                model = app.Models.Item[1];
                if (model == null) return -1;

                // ------------------------------------------------------------------
                // 1. Gather the selection
                // ------------------------------------------------------------------
                List<string> checks = new List<string>();
                List<int> lineIds = GatherSelectedLineIds(model);
                if (lineIds.Count == 0)
                {
                    MessageBox.Show("Select the beams / ribs (lines or members) and, optionally, the nodes of interest first.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                // Read every selected beam / rib; everything else is skipped and reported
                Dictionary<int, LineInfo> lines = new Dictionary<int, LineInfo>();
                List<int> skippedType = new List<int>();
                foreach (int id in lineIds)
                {
                    LineInfo info = ReadLine(model, id, skippedType);
                    if (info != null) lines[id] = info;
                }
                if (skippedType.Count > 0)
                    checks.Add(string.Format("⚠ {0} selected line(s) are not beams / ribs and were skipped: {1}", skippedType.Count, ListIds(skippedType)));
                if (lines.Count == 0)
                {
                    MessageBox.Show("None of the selected lines is a beam or a rib.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                // Local axes for every line (rows-vs-columns decided from the data, see ResolveFrames)
                ResolveFrames(lines.Values, checks);

                // Selected nodes decide which member ends are reported
                HashSet<int> selectedNodes = new HashSet<int>();
                try
                {
                    Array nodeArr;
                    model.Nodes.GetSelectedItemIds(out nodeArr);
                    if (nodeArr != null) foreach (object o in nodeArr) selectedNodes.Add(Convert.ToInt32(o));
                }
                catch { }

                List<MemberEnd> ends = BuildTargetEnds(lines.Values, selectedNodes, checks);
                if (ends.Count == 0)
                {
                    MessageBox.Show("No usable member end found at the selected node(s). Check that the nodes are ends of the selected beams / ribs, and see that each line has valid local axes.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                // Load combinations available in the model (1-based)
                List<string> comboNames = new List<string>();
                int comboCount = model.LoadCombinations.Count;
                for (int i = 1; i <= comboCount; i++)
                {
                    string name;
                    try { name = model.LoadCombinations.get_Name(i); } catch { name = "Combination " + i; }
                    comboNames.Add(name);
                }
                if (comboNames.Count == 0)
                {
                    MessageBox.Show("The model has no load combinations.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                // ------------------------------------------------------------------
                // 2. Ask the user
                // ------------------------------------------------------------------
                string summary = string.Format("{0} beam/rib line(s), {1} member end(s) at {2} node(s){3}",
                    lines.Count, ends.Count, ends.Select(e => e.NodeId).Distinct().Count(),
                    selectedNodes.Count == 0 ? " (no node selected: all member ends)" : "");

                // The in-model axis triad: drawn on request from the dialog and the result window, for the ends found here
                Func<LoadSystem, CustomAxes, string> showAxes = (system, custom) =>
                    AxisTriadDrawer.Show(app, model, BuildTriads(ends, system, custom));
                Action hideAxes = () => AxisTriadDrawer.Hide(model);

                // A previous result window's triad would otherwise stay behind, and so would one saved in the .axs
                if (openResultForm != null && !openResultForm.IsDisposed) openResultForm.Close();
                hideAxes();

                using (ConnectionLoadExtractorForm form = new ConnectionLoadExtractorForm(summary, comboNames, ends[0].Frame, showAxes, hideAxes))
                {
                    if (form.ShowDialog() != DialogResult.OK) { hideAxes(); return 1; }

                    ExtractionResult result = new ExtractionResult
                    {
                        Axes = form.Axes,
                        Custom = form.Custom,
                        Ends = ends,
                        TargetNodeIds = ends.Select(e => e.NodeId).Distinct().OrderBy(n => n).ToList()
                    };
                    result.Checks.AddRange(checks);
                    EAnalysisType analysis = form.NonLinear ? EAnalysisType.atNonLinearStatic : EAnalysisType.atLinearStatic;

                    Cursor.Current = Cursors.WaitCursor;
                    try
                    {
                        // --------------------------------------------------------------
                        // 3. Read forces, 4. sign + transform
                        // --------------------------------------------------------------
                        ReadAllRows(model, lines, ends, form.SelectedComboIds, comboNames, analysis, result);

                        // --------------------------------------------------------------
                        // 5. Custom components + critical combinations
                        // --------------------------------------------------------------
                        Recompute(result, result.Axes, result.Custom);

                        // --------------------------------------------------------------
                        // 6. Checks that need forces
                        // --------------------------------------------------------------
                        CheckJointEquilibrium(model, lines, ends, result);
                        CrossCheckNodeReader(model, ends, analysis, result);
                    }
                    finally { Cursor.Current = Cursors.Default; }

                    if (result.All.Count == 0)
                    {
                        hideAxes();
                        MessageBox.Show("No results could be read for the chosen combinations. Has the model been calculated (" +
                            (form.NonLinear ? "non-linear" : "linear") + " static)?\n\n" + string.Join("\n", result.Checks),
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 0;
                    }

                    // ------------------------------------------------------------------
                    // 7. Show results MODELESS, so AxisVM stays usable (pan, zoom, look at the selection) while the
                    //    window is open. The window calls back to select items in the model. It is not disposed here:
                    //    a modeless form disposes itself when the user closes it.
                    // ------------------------------------------------------------------
                    ConnectionLoadResultForm resultForm = new ConnectionLoadResultForm(result,
                        (nodes, selLines) => SelectInModel(model, nodes, selLines), showAxes, hideAxes, form.AxesShown);
                    openResultForm = resultForm;
                    resultForm.StartPosition = FormStartPosition.Manual;
                    resultForm.Location = new System.Drawing.Point(80, 80);
                    if (ownerHandle != 0) resultForm.Show(new HandleOwner(ownerHandle)); else resultForm.Show();
                }
                return 1;
            }
            catch (Exception ex)
            {
                // A triad drawn from the dialog must not stay behind when the run fails before the result window owns it
                if (model != null && (openResultForm == null || openResultForm.IsDisposed)) AxisTriadDrawer.Hide(model);
                MessageBox.Show("Error: " + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------

        /// <summary>Selected lines plus every line of every selected member, without duplicates, in order.</summary>
        private static List<int> GatherSelectedLineIds(IAxisVMModel model)
        {
            List<int> ids = new List<int>();
            HashSet<int> seen = new HashSet<int>();

            // Directly selected lines
            try
            {
                Array arr;
                model.Lines.GetSelectedItemIds(out arr);
                if (arr != null) foreach (object o in arr) { int id = Convert.ToInt32(o); if (seen.Add(id)) ids.Add(id); }
            }
            catch { }

            // Lines of selected members (same approach as SectionModifierFunction)
            try
            {
                if (model.Members.SelCount > 0)
                {
                    Array members;
                    model.Members.GetSelectedItemIds(out members);
                    if (members != null)
                    {
                        foreach (object m in members)
                        {
                            Array memberLines;
                            model.Members.Item[Convert.ToInt32(m)].GetLines(out memberLines);
                            if (memberLines == null) continue;
                            foreach (object o in memberLines) { int id = Convert.ToInt32(o); if (seen.Add(id)) ids.Add(id); }
                        }
                    }
                }
            }
            catch { }
            return ids;
        }

        /// <summary>Reads one line; returns null (and records it) when it is not a beam or rib.</summary>
        private static LineInfo ReadLine(IAxisVMModel model, int lineId, List<int> skippedType)
        {
            try
            {
                var line = model.Lines.Item[lineId];
                ELineType type = line.LineType;
                if (type != ELineType.ltBeam && type != ELineType.ltRib) { skippedType.Add(lineId); return null; }

                // StartNode / EndNode follow the local x axis (see EndPinAssignAddon.md)
                LineInfo info = new LineInfo { LineId = lineId, StartNodeId = line.StartNode, EndNodeId = line.EndNode };
                RNode n1 = new RNode(), n2 = new RNode();
                model.Nodes.GetNode(info.StartNodeId, ref n1);
                model.Nodes.GetNode(info.EndNodeId, ref n2);
                info.StartPoint = new Vec3(n1.x, n1.y, n1.z);
                info.EndPoint = new Vec3(n2.x, n2.y, n2.z);
                Vec3 d = info.EndPoint - info.StartPoint;
                info.Length = d.Length;
                info.Direction = d.Normalized();

                // The member owns the local system; lines have no transformation matrix of their own
                info.MemberId = line.MemberID;
                if (info.MemberId > 0)
                {
                    RMatrix3x3 m = new RMatrix3x3();
                    int rc = model.Members.Item[info.MemberId].GetTrMatrix(ref m);
                    info.Matrix = m;
                    info.HasMatrix = rc >= 0;
                }
                return info;
            }
            catch { skippedType.Add(lineId); return null; }
        }

        /// <summary>
        /// Turns each line's member matrix into a local frame. The COM docs do not say whether the local axes
        /// are the rows or the columns of RMatrix3x3, so it is decided from the data: for a line not parallel
        /// to a global axis, only one of the two has its x axis along the line. The majority over all such lines
        /// wins (rows if none can decide). Each frame is then checked and, if the line runs against its
        /// member's x, turned 180 degrees about z.
        /// </summary>
        private static void ResolveFrames(IEnumerable<LineInfo> lines, List<string> checks)
        {
            const double parallelTol = 0.999; // |cos| of the angle between the line and the frame's x
            int rowVotes = 0, colVotes = 0;
            foreach (LineInfo li in lines.Where(l => l.HasMatrix))
            {
                bool rowsOk = Math.Abs(Vec3.Dot(Frame(li, true).Ex, li.Direction)) > parallelTol;
                bool colsOk = Math.Abs(Vec3.Dot(Frame(li, false).Ex, li.Direction)) > parallelTol;
                if (rowsOk && !colsOk) rowVotes++;
                if (colsOk && !rowsOk) colVotes++;
            }
            bool useRows = rowVotes >= colVotes;
            if (rowVotes > 0 && colVotes > 0)
                checks.Add(string.Format("⚠ Member matrix layout is inconsistent (rows {0} / columns {1} lines). Using {2}.", rowVotes, colVotes, useRows ? "rows" : "columns"));
            else if (rowVotes + colVotes == 0)
                checks.Add("ℹ Member matrix layout could not be decided from these lines (all parallel to a global axis); assumed rows = local axes. Verify on a skewed member.");
            else
                checks.Add(string.Format("✔ Member matrix layout: local axes are the {0} of GetTrMatrix ({1} line(s) decided it).", useRows ? "rows" : "columns", rowVotes + colVotes));

            List<int> noMatrix = new List<int>(), badFrame = new List<int>(), flipped = new List<int>();
            foreach (LineInfo li in lines)
            {
                if (!li.HasMatrix) { noMatrix.Add(li.LineId); continue; }
                LocalFrame f = Frame(li, useRows);
                double cos = Vec3.Dot(f.Ex, li.Direction);
                if (f.OrthonormalError() > 1e-3 || Math.Abs(cos) < parallelTol) { badFrame.Add(li.LineId); continue; }
                if (cos < 0) { f = f.FlippedAboutZ(); flipped.Add(li.LineId); }
                li.Frame = f;
            }
            if (noMatrix.Count > 0) checks.Add("⚠ No member / transformation matrix, line(s) excluded: " + ListIds(noMatrix));
            if (badFrame.Count > 0) checks.Add("⚠ Local axes not orthonormal or not along the line, line(s) excluded: " + ListIds(badFrame));
            if (flipped.Count > 0) checks.Add("ℹ Line runs against its member's local x, frame turned 180° about z: " + ListIds(flipped));
            if (noMatrix.Count + badFrame.Count == 0) checks.Add("✔ Local axes orthonormal, right-handed and along the line for every line.");
        }

        /// <summary>The line's member matrix read as rows (true) or columns (false).</summary>
        private static LocalFrame Frame(LineInfo li, bool rows)
        {
            RMatrix3x3 m = li.Matrix;
            return LocalFrame.FromMatrix(m.e11, m.e12, m.e13, m.e21, m.e22, m.e23, m.e31, m.e32, m.e33, rows);
        }

        /// <summary>The member ends to report: those on a selected node, or every end if no node is selected.</summary>
        private static List<MemberEnd> BuildTargetEnds(IEnumerable<LineInfo> lines, HashSet<int> selectedNodes, List<string> checks)
        {
            List<MemberEnd> ends = new List<MemberEnd>();
            HashSet<int> usedNodes = new HashSet<int>();
            foreach (LineInfo li in lines.Where(l => l.Frame != null))
            {
                // A line can have both ends on selected nodes - both are reported
                if (selectedNodes.Count == 0 || selectedNodes.Contains(li.StartNodeId))
                    ends.Add(new MemberEnd { NodeId = li.StartNodeId, LineId = li.LineId, MemberId = li.MemberId, IsStart = true, Frame = li.Frame,
                        NodePoint = li.StartPoint, Inward = li.Direction, LineLength = li.Length });
                if (selectedNodes.Count == 0 || selectedNodes.Contains(li.EndNodeId))
                    ends.Add(new MemberEnd { NodeId = li.EndNodeId, LineId = li.LineId, MemberId = li.MemberId, IsStart = false, Frame = li.Frame,
                        NodePoint = li.EndPoint, Inward = -li.Direction, LineLength = li.Length });
            }
            foreach (MemberEnd e in ends) usedNodes.Add(e.NodeId);

            List<int> ignored = selectedNodes.Where(n => !usedNodes.Contains(n)).OrderBy(n => n).ToList();
            if (ignored.Count > 0)
                checks.Add("ℹ Selected node(s) that are not an end of any selected beam / rib, ignored: " + ListIds(ignored));

            // Stable, readable order: node, then line, then start before end
            return ends.OrderBy(e => e.NodeId).ThenBy(e => e.LineId).ThenBy(e => e.IsStart ? 0 : 1).ToList();
        }

        // ------------------------------------------------------------------
        // Forces
        // ------------------------------------------------------------------

        /// <summary>Reads every (line, combination) once and fills result.All for every target end.</summary>
        private static void ReadAllRows(IAxisVMModel model, Dictionary<int, LineInfo> lines, List<MemberEnd> ends,
            List<int> comboIds, List<string> comboNames, EAnalysisType analysis, ExtractionResult result)
        {
            var forces = model.Results.Forces;
            List<int> neededLines = ends.Select(e => e.LineId).Distinct().ToList();
            List<string> failedCombos = new List<string>();
            List<string> posxIssues = new List<string>();

            foreach (int comboId in comboIds)
            {
                string comboName = comboNames[comboId - 1];
                int failures = 0;
                foreach (int lineId in neededLines)
                {
                    double[] rawStart, rawEnd;
                    string posxNote;
                    if (!ReadLineEnds(forces, lineId, comboId, analysis, lines[lineId].Length, out rawStart, out rawEnd, out posxNote))
                    {
                        failures++;
                        continue;
                    }
                    if (posxNote != null && posxIssues.Count < 20) posxIssues.Add("line " + lineId + ": " + posxNote);

                    // One row per target end on this line
                    foreach (MemberEnd e in ends.Where(x => x.LineId == lineId))
                    {
                        double[] raw = e.IsStart ? rawStart : rawEnd;
                        double[] nodeLocal = ConnectionLoadMath.RawToNodeLocal(raw, e.IsStart);
                        result.All.Add(new EndLoadRow
                        {
                            End = e,
                            ComboId = comboId,
                            ComboName = comboName,
                            Raw = raw,
                            NodeLocal = nodeLocal,
                            Global = ConnectionLoadMath.LocalToGlobal(nodeLocal, e.Frame)
                        });
                    }
                }
                if (failures > 0) failedCombos.Add(string.Format("{0} ({1}/{2} lines)", comboName, failures, neededLines.Count));
            }

            // Keep rows ordered by end, then combination, which is how the tables read best
            result.All = result.All.OrderBy(r => ends.IndexOf(r.End)).ThenBy(r => r.ComboId).ToList();

            if (failedCombos.Count > 0)
                result.Checks.Add("⚠ No results (not calculated?) for: " + string.Join(", ", failedCombos));
            if (posxIssues.Count > 0)
                result.Checks.Add("⚠ End section not at the line end: " + string.Join("; ", posxIssues));
            else if (result.All.Count > 0)
                result.Checks.Add("✔ First / last result section at x = 0 and x = L on every line read.");
        }

        /// <summary>
        /// Raw internal forces at x = 0 and x = L of one line for one combination. The section with the
        /// smallest PosX is taken as the start and the largest as the end, so the order of the returned array
        /// does not matter. <paramref name="posxNote"/> is set when those sections are not at 0 / L.
        /// </summary>
        private static bool ReadLineEnds(IAxisVMForces forces, int lineId, int comboId, EAnalysisType analysis, double length,
            out double[] rawStart, out double[] rawEnd, out string posxNote)
        {
            rawStart = rawEnd = null;
            posxNote = null;
            Array fArr = null, xArr = null;
            int rc = -1;

            // Load level 0 is what the linear readers elsewhere in this plugin use; for non-linear results
            // level 1 is tried as well before giving up.
            try { rc = forces.GetLineForcesByLoadCombinationId(lineId, comboId, 0, analysis, out fArr, out xArr); } catch { rc = -1; }
            if ((rc <= 0 || fArr == null) && analysis == EAnalysisType.atNonLinearStatic)
            {
                try { rc = forces.GetLineForcesByLoadCombinationId(lineId, comboId, 1, analysis, out fArr, out xArr); } catch { rc = -1; }
            }
            if (rc <= 0 || fArr == null || xArr == null || fArr.Length == 0) return false;

            // Locate the first / last section by position
            int lo = xArr.GetLowerBound(0), hi = xArr.GetUpperBound(0);
            int iMin = lo, iMax = lo;
            for (int i = lo; i <= hi; i++)
            {
                double x = Convert.ToDouble(xArr.GetValue(i));
                if (x < Convert.ToDouble(xArr.GetValue(iMin))) iMin = i;
                if (x > Convert.ToDouble(xArr.GetValue(iMax))) iMax = i;
            }
            double x0 = Convert.ToDouble(xArr.GetValue(iMin)), x1 = Convert.ToDouble(xArr.GetValue(iMax));
            double tol = Math.Max(1e-3, 1e-3 * length);
            if (Math.Abs(x0) > tol || Math.Abs(x1 - length) > tol)
                posxNote = string.Format("x = {0:0.###} .. {1:0.###} m, L = {2:0.###} m", x0, x1, length);

            // The forces array is indexed like the PosX array
            int shift = fArr.GetLowerBound(0) - lo;
            rawStart = ToVector((RLineForceValues)fArr.GetValue(iMin + shift));
            rawEnd = ToVector((RLineForceValues)fArr.GetValue(iMax + shift));
            return true;
        }

        /// <summary>AxisVM record -> (N, Vy, Vz, Tx, My, Mz).</summary>
        private static double[] ToVector(RLineForceValues v)
        {
            return new[] { v.lfvNx, v.lfvVy, v.lfvVz, v.lfvTx, v.lfvMy, v.lfvMz };
        }

        // ------------------------------------------------------------------
        // Critical
        // ------------------------------------------------------------------

        /// <summary>
        /// Switches the result to another system / custom angles without reading AxisVM again: the custom
        /// components are recomputed from the global ones, then the critical rows are searched afresh.
        /// Called after reading and every time the result window changes the axes.
        /// </summary>
        public static void Recompute(ExtractionResult result, LoadSystem axes, CustomAxes custom)
        {
            result.Axes = axes;
            result.Custom = custom;
            LocalFrame f = custom.Frame;
            foreach (EndLoadRow r in result.All) r.Custom = ConnectionLoadMath.GlobalToLocal(r.Global, f);

            result.Critical.Clear();
            FindCriticalRows(result.Ends, result);

            // One line in the Checks tab says which axes the tables use (replaced on every change)
            result.Checks.RemoveAll(c => c.StartsWith(AxesCheckPrefix));
            string text = axes == LoadSystem.Custom
                ? string.Format("{0} {1}: x' = {2}, y' = {3}, z' = {4} in global axes{5}", AxesCheckPrefix, custom.Describe(), f.Ex, f.Ey, f.Ez,
                    f.OrthonormalError() < 1e-9 ? "" : " ⚠ NOT orthonormal")
                : AxesCheckPrefix + " " + result.AxesText;
            result.Checks.Insert(0, text);
        }

        private const string AxesCheckPrefix = "ℹ Axes";

        /// <summary>
        /// The arrow triads that show the given system on the model: Global / Custom one per node, Local one per
        /// member end, set 20 % along the member so several members at one node do not hide each other.
        /// </summary>
        public static List<AxisTriad> BuildTriads(IList<MemberEnd> ends, LoadSystem system, CustomAxes custom)
        {
            const int maxTriads = 60;
            List<AxisTriad> triads = new List<AxisTriad>();
            if (system == LoadSystem.Local)
            {
                foreach (MemberEnd e in ends.Take(maxTriads))
                {
                    double size = Clamp(0.3 * e.LineLength);
                    triads.Add(new AxisTriad
                    {
                        Origin = e.NodePoint + (Math.Min(0.2 * e.LineLength, 2 * size)) * e.Inward,
                        Axes = e.Frame,
                        Size = size,
                        Labels = new[] { "x", "y", "z" }
                    });
                }
                return triads;
            }

            LocalFrame frame = system == LoadSystem.Custom ? custom.Frame : LocalFrame.GlobalAxes;
            string[] labels = system == LoadSystem.Custom ? new[] { "x'", "y'", "z'" } : new[] { "X", "Y", "Z" };
            foreach (var g in ends.GroupBy(e => e.NodeId).Take(maxTriads))
                triads.Add(new AxisTriad { Origin = g.First().NodePoint, Axes = frame, Size = Clamp(0.3 * g.Min(e => e.LineLength)), Labels = labels });
            return triads;
        }

        /// <summary>Arrow length kept readable: 0.2 .. 1.5 m.</summary>
        private static double Clamp(double size) { return Math.Max(0.2, Math.Min(1.5, size)); }

        /// <summary>
        /// Max+ and Min- of every one of the six components (in the result's axes), per member end and over
        /// all ends. Order: component, then member ends, then the "All ends" pair.
        /// </summary>
        private static void FindCriticalRows(List<MemberEnd> ends, ExtractionResult result)
        {
            LoadSystem s = result.Axes;
            for (int c = 0; c < 6; c++)
            {
                foreach (MemberEnd e in ends)
                {
                    List<EndLoadRow> rows = result.All.Where(r => r.End == e).ToList();
                    AddCritical(result, e, c, rows, rows.Select(r => r.In(s)[c]).ToList());
                }
                // Governing over every reported end (End = null)
                AddCritical(result, null, c, result.All, result.All.Select(r => r.In(s)[c]).ToList());
            }
        }

        /// <summary>Appends the Max+ and Min- rows of one component for one set of rows.</summary>
        private static void AddCritical(ExtractionResult result, MemberEnd end, int component, List<EndLoadRow> rows, List<double> values)
        {
            CriticalPick pick = ConnectionLoadMath.FindCritical(values);
            if (pick.MaxIndex < 0) return; // no rows at all for this end
            result.Critical.Add(new CriticalRow { End = end, Component = component, Kind = CriticalKind.MaxPositive, Row = rows[pick.MaxIndex], SignMatches = pick.HasPositive });
            result.Critical.Add(new CriticalRow { End = end, Component = component, Kind = CriticalKind.MinNegative, Row = rows[pick.MinIndex], SignMatches = pick.HasNegative });
        }

        // ------------------------------------------------------------------
        // Checks
        // ------------------------------------------------------------------

        /// <summary>
        /// At a node where every connected line is a reported beam / rib end and there is no support, the
        /// forces the members put on the node must add up to the nodal load - zero for an unloaded joint.
        /// This exercises reading, the sign table and the transform together.
        /// </summary>
        private static void CheckJointEquilibrium(IAxisVMModel model, Dictionary<int, LineInfo> lines, List<MemberEnd> ends, ExtractionResult result)
        {
            // Nodes carrying a support are not free joints
            HashSet<int> supported = new HashSet<int>();
            try
            {
                var ns = model.NodesSupports;
                for (int i = 1; i <= ns.Count; i++) supported.Add(ns.get_NodeId(i));
            }
            catch { }

            int checkedNodes = 0, balanced = 0;
            List<string> unbalanced = new List<string>();
            foreach (int nodeId in result.TargetNodeIds)
            {
                if (supported.Contains(nodeId)) continue;

                // Every line meeting at the node must be one of the reported ends at this node
                Array conn = null;
                try { model.Nodes.GetConnectedLines(nodeId, out conn); } catch { }
                if (conn == null || conn.Length == 0) continue;
                List<MemberEnd> atNode = ends.Where(e => e.NodeId == nodeId).ToList();
                bool complete = true;
                foreach (object o in conn)
                {
                    int lid = Convert.ToInt32(o);
                    if (!atNode.Any(e => e.LineId == lid)) { complete = false; break; }
                }
                if (!complete) continue;
                checkedNodes++;

                // Sum per combination, in global axes; tolerance relative to the largest member force
                double worst = 0, worstScale = 0;
                string worstCombo = null;
                foreach (var g in result.All.Where(r => r.End.NodeId == nodeId).GroupBy(r => r.ComboId))
                {
                    double[] sum = new double[6];
                    double scale = 0;
                    foreach (EndLoadRow r in g)
                        for (int i = 0; i < 6; i++) { sum[i] += r.Global[i]; scale = Math.Max(scale, Math.Abs(r.Global[i])); }
                    double res = sum.Max(v => Math.Abs(v));
                    if (res > worst) { worst = res; worstScale = scale; worstCombo = g.First().ComboName; }
                }
                if (worst <= 1e-3 + 1e-3 * worstScale) balanced++;
                else unbalanced.Add(string.Format("node {0}: residual {1:0.###} ({2})", nodeId, worst, worstCombo));
            }

            if (checkedNodes == 0)
                result.Checks.Add("ℹ Joint equilibrium: no target node has all its connected lines selected and no support - nothing to check.");
            else if (unbalanced.Count == 0)
                result.Checks.Add(string.Format("✔ Joint equilibrium: forces on node sum to 0 at {0} free joint(s).", balanced));
            else
                result.Checks.Add(string.Format("⚠ Joint equilibrium: {0} of {1} free joint(s) do not sum to 0 - expected only where a nodal load or a surface acts on the node; otherwise a sign / axis problem: {2}",
                    unbalanced.Count, checkedNodes, string.Join("; ", unbalanced.Take(10))));
        }

        /// <summary>
        /// Compares the end section values with AxisVM's own "forces at node" reader for the first
        /// combination that has results. Reported per component as "same" or "negated", separately for start
        /// and end ends - this documents the convention that reader uses and catches a wrong end pick.
        /// </summary>
        private static void CrossCheckNodeReader(IAxisVMModel model, List<MemberEnd> ends, EAnalysisType analysis, ExtractionResult result)
        {
            if (result.All.Count == 0) return;
            int comboId = result.All[0].ComboId;
            string comboName = result.All[0].ComboName;

            // [start/end][component] -> counts of "same" / "negated" / "different"
            int[,] same = new int[2, 6], neg = new int[2, 6], diff = new int[2, 6];
            int compared = 0;

            foreach (int nodeId in result.TargetNodeIds)
            {
                Array fArr = null, idArr = null, names = null;
                int rc;
                try
                {
                    rc = model.Results.Forces.GetLineForcesByLoadCombinationIdConnectedToNode(nodeId, 1, comboId, 0, analysis,
                        EConnectedToNodeType.ctntAll, ESeismicComponentSumType.scstUsual, out fArr, out idArr, out names);
                }
                catch { rc = -1; }
                if (rc <= 0 || fArr == null || idArr == null) continue;

                int shift = fArr.GetLowerBound(0) - idArr.GetLowerBound(0);
                for (int i = idArr.GetLowerBound(0); i <= idArr.GetUpperBound(0); i++)
                {
                    int lineId = Convert.ToInt32(idArr.GetValue(i));
                    EndLoadRow row = result.All.FirstOrDefault(r => r.ComboId == comboId && r.End.NodeId == nodeId && r.End.LineId == lineId);
                    if (row == null) continue;
                    double[] theirs = ToVector((RLineForceValues)fArr.GetValue(i + shift));
                    int k = row.End.IsStart ? 0 : 1;
                    for (int c = 0; c < 6; c++)
                    {
                        double a = row.Raw[c], b = theirs[c];
                        double tol = 1e-3 + 1e-3 * Math.Max(Math.Abs(a), Math.Abs(b));
                        if (Math.Abs(a) <= tol && Math.Abs(b) <= tol) continue; // both ~0 say nothing about the sign
                        if (Math.Abs(a - b) <= tol) same[k, c]++;
                        else if (Math.Abs(a + b) <= tol) neg[k, c]++;
                        else diff[k, c]++;
                    }
                    compared++;
                }
            }

            if (compared == 0)
            {
                result.Checks.Add("ℹ Cross-check with AxisVM node reader: not available for this model / combination.");
                return;
            }
            List<string> parts = new List<string>();
            int totalDiff = 0;
            for (int k = 0; k < 2; k++)
            {
                List<string> comps = new List<string>();
                for (int c = 0; c < 6; c++)
                {
                    totalDiff += diff[k, c];
                    string tag = diff[k, c] > 0 ? "DIFFERENT" : same[k, c] > 0 && neg[k, c] == 0 ? "same" : neg[k, c] > 0 && same[k, c] == 0 ? "negated" : same[k, c] + neg[k, c] == 0 ? "n/a" : "mixed";
                    comps.Add(ConnectionLoadMath.LocalNames[c] + " " + tag);
                }
                parts.Add((k == 0 ? "start ends: " : "end ends: ") + string.Join(", ", comps));
            }
            result.Checks.Add(string.Format("{0} Cross-check with AxisVM node reader ({1}, {2} member end(s)): {3}",
                totalDiff == 0 ? "✔" : "⚠", comboName, compared, string.Join(" | ", parts)));
        }

        // ------------------------------------------------------------------
        // Visual check
        // ------------------------------------------------------------------

        /// <summary>Replaces the AxisVM selection with the given nodes and lines so they can be seen in the model.</summary>
        private static void SelectInModel(IAxisVMModel model, IEnumerable<int> nodeIds, IEnumerable<int> lineIds)
        {
            try
            {
                model.Nodes.SelectAll(ELongBoolean.lbFalse);
                model.Lines.SelectAll(ELongBoolean.lbFalse);
                model.Members.SelectAll(ELongBoolean.lbFalse);
                foreach (int n in nodeIds.Distinct()) model.Nodes.set_Selected(n, ELongBoolean.lbTrue);
                foreach (int l in lineIds.Distinct()) model.Lines.set_Selected(l, ELongBoolean.lbTrue);
                model.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the selection: " + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>"1, 2, 3, ... (+n more)" for messages.</summary>
        private static string ListIds(List<int> ids)
        {
            string s = string.Join(", ", ids.Take(30));
            return ids.Count > 30 ? s + string.Format(" ... (+{0} more)", ids.Count - 30) : s;
        }
    }
}
