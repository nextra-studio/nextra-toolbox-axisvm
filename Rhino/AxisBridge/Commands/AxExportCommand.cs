using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AxisBridge.Export;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Writes the structural model to JSON for the AxisVM "N. Rhino Import" addon.
    /// Options: Scope (All / OnlySelected) and Untagged (Skip / Include). By default only objects that
    /// carry Ax_* attributes are exported, so unrelated geometry in the file never ends up in AxisVM.
    /// </summary>
    public class AxExportCommand : Command
    {
        private static bool includeUntagged;

        public override string EnglishName => "AxExport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            int selectedCount = 0;
            foreach (RhinoObject _ in doc.Objects.GetSelectedObjects(false, false)) selectedCount++;
            bool selectedOnly = selectedCount > 0;   // sensible default: selection present -> selected

            var scope = new OptionToggle(selectedOnly, "All", "OnlySelected");
            var untagged = new OptionToggle(includeUntagged, "Skip", "Include");

            using (GetOption go = new GetOption())
            {
                go.SetCommandPrompt("AxisBridge export - press Enter to continue");
                go.AddOptionToggle("Scope", ref scope);
                go.AddOptionToggle("Untagged", ref untagged);
                go.AcceptNothing(true);

                while (true)
                {
                    GetResult gr = go.Get();
                    if (gr == GetResult.Option) continue;
                    if (gr == GetResult.Nothing) break;
                    return Result.Cancel;
                }
            }

            selectedOnly = scope.CurrentValue;
            includeUntagged = untagged.CurrentValue;

            if (selectedOnly && selectedCount == 0)
            {
                RhinoApp.WriteLine("AxExport: OnlySelected chosen but nothing is selected.");
                MessageBox.Show("Nothing is selected. Select the objects to export, or choose Scope=All.",
                    "AxisBridge Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return Result.Nothing;
            }

            string defaultDir = !string.IsNullOrEmpty(doc.Path) ? Path.GetDirectoryName(doc.Path) : System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string defaultName = !string.IsNullOrEmpty(doc.Name) ? Path.GetFileNameWithoutExtension(doc.Name) + "_axisbridge.json" : "model_axisbridge.json";

            string filePath;
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Export AxisBridge Structural JSON";
                sfd.Filter = "AxisBridge JSON (*.json)|*.json|All Files (*.*)|*.*";
                sfd.InitialDirectory = defaultDir;
                sfd.FileName = defaultName;

                if (sfd.ShowDialog(AttrCommandRunner.RhinoOwner()) != DialogResult.OK)
                    return Result.Cancel;

                filePath = sfd.FileName;
            }

            try
            {
                var res = ModelExporter.Export(doc, filePath, selectedOnly, includeUntagged);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AxisBridge Export Complete!");
                sb.AppendLine();
                sb.AppendLine($"Scope:             {(selectedOnly ? "Selected only" : "All objects")}{(includeUntagged ? " (incl. untagged)" : "")}");
                sb.AppendLine($"Points / Nodes:    {res.PointCount}");
                sb.AppendLine($"Lines / Members:   {res.LineCount}");
                sb.AppendLine($"Surfaces (faces):  {res.DomainCount}");
                if (res.SkippedUntagged > 0)
                    sb.AppendLine($"Untagged skipped:  {res.SkippedUntagged}  (run again with Untagged=Include to export them as plain geometry)");
                sb.AppendLine();
                sb.AppendLine($"Output: {filePath}");

                if (res.Warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Warnings ({res.Warnings.Count}):");
                    foreach (string w in res.Warnings)
                    {
                        sb.AppendLine("  • " + w);
                        RhinoApp.WriteLine("AxisBridge Warning: " + w);
                    }
                }

                RhinoApp.WriteLine(sb.ToString());
                MessageBox.Show(sb.ToString(), "AxisBridge Export", MessageBoxButtons.OK, res.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"AxisBridge Export Failed: {ex.Message}");
                MessageBox.Show($"Export failed: {ex.Message}", "AxisBridge Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return Result.Failure;
            }
        }
    }
}
