using System;
using System.Text;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Add-on entry point: reads the selection, asks which operation to run, runs it inside a single
    /// BeginUpdate / EndUpdate pair and reports the outcome. The operations themselves live in
    /// <see cref="LoadPanelOps"/>.
    /// </summary>
    public static class LoadPanelToolsLogic
    {
        public static int Execute(IAxisVMApplication app)
        {
            try
            {
                IAxisVMModel model = app.Models.Item[1];
                if (model == null) return -1;

                string materialName;
                int materialId = LoadPanelOps.FindDefaultMaterial(model, out materialName);
                string defaultsText = string.Format("  thickness {0:F0} mm, type Shell, material '{1}'",
                    LoadPanelOps.DefaultThicknessM * 1000.0, materialName ?? "(none - add a material first)");

                using (LoadPanelToolsForm form = new LoadPanelToolsForm(defaultsText))
                {
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    LoadPanelOpResult result;
                    model.BeginUpdate();
                    try
                    {
                        switch (form.Operation)
                        {
                            case LoadPanelOperation.PanelToDomain:
                                result = LoadPanelOps.PanelsToDomains(model, LoadPanelOps.SelectedPanelUids(model), materialId, form.DeleteOriginal);
                                break;

                            case LoadPanelOperation.DomainToPanel:
                                result = LoadPanelOps.DomainsToPanels(model, LoadPanelOps.SelectedDomainIds(model), form.DeleteOriginal);
                                break;

                            default:
                                result = LoadPanelOps.SplitPanels(model, LoadPanelOps.SelectedPanelUids(model), LoadPanelOps.SelectedLineUids(model));
                                break;
                        }
                    }
                    finally
                    {
                        model.EndUpdate();
                        try { model.Refresh(); } catch { }
                    }

                    ShowSummary(form.Operation, result);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }

            return 1;
        }

        private static void ShowSummary(LoadPanelOperation operation, LoadPanelOpResult r)
        {
            StringBuilder sb = new StringBuilder();

            if (operation == LoadPanelOperation.PanelToDomain)
            {
                sb.AppendLine(string.Format("Created {0} domain(s) from the selected load panels.", r.Converted));
                if (r.Deleted > 0) sb.AppendLine(string.Format("Deleted {0} source load panel(s).", r.Deleted));
                if (r.LoadsCopied > 0) sb.AppendLine(string.Format("Moved {0} panel load(s) onto the new domains.", r.LoadsCopied));
            }
            else if (operation == LoadPanelOperation.DomainToPanel)
            {
                sb.AppendLine(string.Format("Created {0} load panel(s) from the selected domains.", r.Converted));
                if (r.Deleted > 0) sb.AppendLine(string.Format("Deleted {0} source domain(s).", r.Deleted));
            }
            else
            {
                sb.AppendLine(string.Format("Split {0} load panel(s) into {1} piece(s) using {2} cut(s).",
                    r.Converted, r.PiecesCreated, r.SplittersUsed));
                if (r.LoadsCopied > 0) sb.AppendLine(string.Format("Copied {0} panel load(s) onto the pieces.", r.LoadsCopied));
            }

            if (r.Notes.Count > 0)
            {
                sb.AppendLine();
                int shown = Math.Min(r.Notes.Count, 12);
                for (int i = 0; i < shown; i++) sb.AppendLine(r.Notes[i]);
                if (r.Notes.Count > shown) sb.AppendLine("... and " + (r.Notes.Count - shown) + " more.");
            }

            MessageBox.Show(sb.ToString(), "Load Panel Tools", MessageBoxButtons.OK,
                r.Notes.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
