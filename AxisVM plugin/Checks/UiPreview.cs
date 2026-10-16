using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;

// Offline visual check: renders synthetic data, never connects to or modifies an AxisVM model.
internal static class UiPreview
{
    private static string output;
    private static string binaries;
    private static bool stress;
    [STAThread]
    private static void Main(string[] args)
    {
        stress = args.Length > 2;
        binaries = Path.GetFullPath(args[0]);
        output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string dll = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { binaries, @"C:\AXISVM X8" })
                if (File.Exists(Path.Combine(dir, dll))) return Assembly.LoadFrom(Path.Combine(dir, dll));
            return null;
        };
        Application.EnableVisualStyles();
        Render("EndPinAssign", "EndPinAssignForm");
        Render("SectionModifier", "SectionModifierForm");
        Render("ReactionTransfer", "ReactionTransferForm");
        Render("LoadPanelTools", "LoadPanelToolsForm", "200 mm shell; first concrete material in the active model.");
        Render("RhinoImport", "RhinoImportForm", "");
        Render("ConnectionLoadExtractor", "ConnectionLoadExtractorForm", "12 members, 8 target nodes", new List<string> { "ULS 01 - permanent + imposed load", "ULS 02 - wind X", "SLS - characteristic" });
        Render("OneWayLoadPanel", "OneWayLoadPanelForm", false, "Create panels from 8 selected boundary lines.", new List<string> { "Permanent", "Imposed" });
        Render("OneWayLoadPanel", "OneWayLoadPanelForm", true, "Convert 3 selected load panels; retain their loads.", new List<string>());
        Render("OneWayLoadPanel", "OneWayPanelReportForm", "3 panels converted. 6 edge strips created.", new List<string> { "Example report using synthetic data.", "No AxisVM model was opened or changed." }, true);
        Render("RhinoImport", "RhinoImportReportForm", "Rhino Import", "12 curves and 2 surfaces imported.", "Synthetic preview data.\r\nUnits: m / kN.\r\nNo AxisVM model was opened or changed.");
        ExtractionResult result = new ExtractionResult { Axes = LoadSystem.Global };
        EndLoadRow row = new EndLoadRow { End = new MemberEnd { NodeId = 12, LineId = 4, MemberId = 2, IsStart = true }, ComboId = 1, ComboName = "ULS 01 - permanent + imposed", Global = new double[] { 12, -6, 40, 1, 8, -3 }, NodeLocal = new double[] { 12, -6, 40, 1, 8, -3 } };
        result.All.Add(row);
        for (int c = 0; c < 6; c++)
            foreach (CriticalKind kind in new[] { CriticalKind.MaxPositive, CriticalKind.MinNegative })
                result.Critical.Add(new CriticalRow { Component = c, Kind = kind, Row = row, SignMatches = kind == CriticalKind.MaxPositive ? row.Global[c] > 0 : row.Global[c] < 0 });
        result.Checks.Add("Synthetic data for visual verification only.");
        using (Form form = new AxisVM_plugin.UI.ConnectionLoadResultForm(result, delegate { }))
        {
            Save(form, "ConnectionLoadResultForm");
            if (stress)
            {
                form.Size = form.MinimumSize;
                Save(form, "ConnectionLoadResultForm-minimum");
            }
        }
        Icons();
        Console.WriteLine("Rendered dialogs and toolbar icons to " + output);
    }

    private static void Render(string addon, string type, params object[] args)
    {
        Assembly assembly = Assembly.LoadFrom(Path.Combine(binaries, addon + "Addon.dll"));
        using (Form form = (Form)Activator.CreateInstance(assembly.GetType("AxisVM_plugin.UI." + type, true), args))
        {
            string name = type + (args.Length > 0 && args[0] is bool ? ((bool)args[0] ? "-convert" : "-create") : "");
            Save(form, name);
            if (stress)
            {
                if (type == "EndPinAssignForm")
                {
                    ((ComboBox)form.GetType().GetField("cmbPinType", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).SelectedIndex = 0;
                    Save(form, name + "-complete-pin");
                    ((CheckBox)form.GetType().GetField("chkModifyIntermediate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).Checked = false;
                    Save(form, name + "-preserve-intermediate");
                }
                if (type == "ReactionTransferForm")
                {
                    ((RadioButton)assembly.GetType("AxisVM_plugin.UI." + type).GetField("rbExistingFile").GetValue(form)).Checked = true;
                    ((ComboBox)assembly.GetType("AxisVM_plugin.UI." + type).GetField("cbResultType").GetValue(form)).SelectedIndex = 2;
                    Save(form, name + "-existing-envelope");
                }
                if (type == "LoadPanelToolsForm")
                    foreach (string field in new[] { "radDomainToPanel", "radSplit", "radMerge" })
                    {
                        ((RadioButton)form.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form)).Checked = true;
                        Save(form, name + "-" + field);
                    }
                using (Form scaled = (Form)Activator.CreateInstance(assembly.GetType("AxisVM_plugin.UI." + type, true), args))
                {
                    scaled.Font = new Font("Segoe UI", 13.5f);
                    scaled.Scale(new SizeF(1.5f, 1.5f));
                    Save(scaled, name + "-150-percent-layout");
                }
            }
        }
    }

    private static void Save(Form form, string name)
    {
        // This is a self-contained rendering fixture with synthetic inputs, never the user's app.
        // An off-screen form lets native ComboBoxes render their selected text in WM_PRINT.
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        Audit(form, name);
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        }
        Console.WriteLine(name + ": " + form.ClientSize + "; Accept=" + (form.AcceptButton == null ? "none" : ((Control)form.AcceptButton).Text) + "; Cancel=" + (form.CancelButton == null ? "none" : ((Control)form.CancelButton).Text));
        form.Hide();
    }

    private static void Audit(Control parent, string name)
    {
        foreach (Control child in parent.Controls)
        {
            Label label = child as Label;
            if (label != null && label.Visible && !label.AutoSize && label.Text.Length > 0)
            {
                Size text = TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                if (text.Height > label.ClientSize.Height - label.Padding.Vertical + 2)
                    Console.WriteLine("TEXT HEIGHT " + name + ": " + label.Text.Replace("\n", " ") + " needs " + text.Height + " has " + label.Height);
            }
            Audit(child, name);
        }
    }

    private static void Icons()
    {
        string[] names = { "RhinoImport", "OneWayLoadPanel", "LoadPanelTools", "EndPinAssign", "ReactionTransfer", "SectionModifier", "ConnectionLoadExtractor" };
        using (Bitmap sheet = new Bitmap(770, 186))
        using (Graphics g = Graphics.FromImage(sheet))
        using (Font font = new Font("Segoe UI", 9))
        {
            g.Clear(Color.White);
            for (int i = 0; i < names.Length; i++)
            using (Bitmap icon = AddonIcons.Preview(names[i]))
            {
                icon.MakeTransparent(Color.Magenta);
                icon.Save(Path.Combine(output, names[i] + "-icon.png"), ImageFormat.Png);
                int x = 18 + i * 108;
                g.DrawImageUnscaled(icon, x + 30, 12);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(icon, new Rectangle(x + 6, 42, 64, 64));
                g.DrawString(names[i].Replace("LoadPanel", "Panel\n").Replace("Connection", "Connection\n").Replace("Reaction", "Reaction\n").Replace("Section", "Section\n"), font, Brushes.Black, new RectangleF(x, 120, 104, 60));
            }
            sheet.Save(Path.Combine(output, "toolbar-icons.png"), ImageFormat.Png);
        }
    }
}
