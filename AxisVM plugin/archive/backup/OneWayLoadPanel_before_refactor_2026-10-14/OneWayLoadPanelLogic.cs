using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Addon "N. Lines -> One-Way Panel": builds one-way load panel(s) from the boundary lines the
    /// user has selected.
    ///
    /// This class only handles the selection, the dialog and the run summary. All of the geometry -
    /// reconstructing subdivided sides, classifying support vs edge beams, creating the panels and
    /// the edge-strip split - lives in <see cref="OneWayPanelBuilder"/>, which is shared with the
    /// "Convert Load Panel" addon.
    /// </summary>
    public class OneWayLoadPanelLogic
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
                    MessageBox.Show("Please select lines to define the load panel boundary.", "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 0;
                }

                using (OneWayLoadPanelForm form = new OneWayLoadPanelForm())
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    OneWayPanelOptions opt = new OneWayPanelOptions
                    {
                        SpanningDirection = form.SpanningDirection,
                        NoSpacing = form.NoSpacing,
                        EdgeSpacingMm = form.EdgeSpacing,
                        AngleToleranceDeg = form.AngleTolerance
                    };

                    // Resolve every selected line to its endpoint nodes.
                    List<ResolvedLine> allSelected = new List<ResolvedLine>();
                    for (int i = 1; i <= selectedLines.Length; i++)
                    {
                        allSelected.Add(OneWayPanelBuilder.ResolveLine(model, (int)selectedLines.GetValue(i)));
                    }

                    List<List<ResolvedLine>> groups = OneWayPanelBuilder.GroupByConnectivity(allSelected);

                    // Edge spacing splits lines, which renumbers every line index after the split
                    // point. Remember each group by UID and re-resolve immediately before it is built.
                    List<List<int>> groupUids = new List<List<int>>();
                    foreach (List<ResolvedLine> group in groups)
                    {
                        List<int> uids = new List<int>(group.Count);
                        foreach (ResolvedLine line in group) uids.Add(model.Lines.get_UID(line.LineId));
                        groupUids.Add(uids);
                    }

                    int panelsCreated = 0;
                    int boundariesDone = 0;
                    List<string> notes = new List<string>();

                    model.BeginUpdate();
                    try
                    {
                        for (int g = 0; g < groupUids.Count; g++)
                        {
                            List<ResolvedLine> current = new List<ResolvedLine>(groupUids[g].Count);
                            bool resolved = true;

                            foreach (int uid in groupUids[g])
                            {
                                int lineId = model.Lines.get_IndexOfUID(uid);
                                if (lineId <= 0) { resolved = false; break; }
                                current.Add(OneWayPanelBuilder.ResolveLine(model, lineId));
                            }

                            if (!resolved || current.Count == 0)
                            {
                                notes.Add(string.Format("Boundary {0}: skipped, its lines no longer exist in the model.", g + 1));
                                continue;
                            }

                            int created = OneWayPanelBuilder.GeneratePanels(model, current, opt, g + 1, notes).Count;
                            panelsCreated += created;
                            if (created > 0) boundariesDone++;
                        }
                    }
                    finally
                    {
                        model.EndUpdate();
                    }

                    ShowSummary(groups.Count, boundariesDone, panelsCreated, notes);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        private static void ShowSummary(int boundaryCount, int boundariesDone, int panelsCreated, List<string> notes)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("Created {0} load panel(s) from {1} of {2} selected boundary group(s).",
                panelsCreated, boundariesDone, boundaryCount);

            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                int shown = Math.Min(notes.Count, 12);
                for (int i = 0; i < shown; i++) sb.AppendLine(notes[i]);
                if (notes.Count > shown) sb.AppendLine("... and " + (notes.Count - shown) + " more.");
            }

            MessageBox.Show(sb.ToString(), "One-Way Load Panel", MessageBoxButtons.OK,
                boundariesDone == boundaryCount ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
