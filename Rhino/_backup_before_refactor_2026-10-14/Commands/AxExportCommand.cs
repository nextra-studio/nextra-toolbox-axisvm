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
    public class AxExportCommand : Command
    {
        public override string EnglishName => "AxExport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            // ---- scope: All vs OnlySelected ---------------------------------
            int selectedCount = 0;
            foreach (RhinoObject _ in doc.Objects.GetSelectedObjects(false, false)) selectedCount++;

            bool selectedOnly = selectedCount > 0;   // sensible default: selection present -> selected

            using (GetOption go = new GetOption())
            {
                go.SetCommandPrompt("AxisBridge export scope");
                go.AddOption("All");
                go.AddOption("OnlySelected");
                go.AcceptNothing(true);   // Enter keeps the default above

                GetResult gr = go.Get();
                if (gr == GetResult.Option)
                    selectedOnly = go.Option().EnglishName == "OnlySelected";
                else if (gr != GetResult.Nothing)
                    return Result.Cancel;
            }

            if (selectedOnly && selectedCount == 0)
            {
                RhinoApp.WriteLine("AxExport: OnlySelected chosen but nothing is selected.");
                MessageBox.Show("Nothing is selected. Select the objects to export, or choose All.",
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

                IntPtr rhinoHwnd = RhinoApp.MainWindowHandle();
                IWin32Window parent = (rhinoHwnd != IntPtr.Zero) ? Control.FromHandle(rhinoHwnd) : null;

                if (sfd.ShowDialog(parent) != DialogResult.OK)
                    return Result.Cancel;

                filePath = sfd.FileName;
            }

            try
            {
                var res = ModelExporter.Export(doc, filePath, selectedOnly);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AxisBridge Export Complete!");
                sb.AppendLine();
                sb.AppendLine($"Scope:             {(selectedOnly ? "Selected only" : "All objects")}");
                sb.AppendLine($"Points / Supports: {res.PointCount}");
                sb.AppendLine($"Lines / Members:   {res.LineCount}");
                sb.AppendLine($"Domains / Panels:  {res.DomainCount}");
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
