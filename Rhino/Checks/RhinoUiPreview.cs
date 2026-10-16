// Offline renderer for the AxisBridge dialogs. It builds each form with synthetic selection data and no
// Rhino document, then saves one PNG per tab. No Rhino window or model is touched.
// Run through scripts/Preview-RhinoUI.ps1.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Library;
using AxisBridge.UI;

internal static class RhinoUiPreview
{
    private static string output;

    [STAThread]
    private static int Main(string[] args)
    {
        string rhp = args[0];
        output = args[1];
        Directory.CreateDirectory(output);
        Assembly.LoadFrom(rhp);
        Application.EnableVisualStyles();
        AxLibrary lib = AxLibrary.Defaults();

        SelectionState point = State(2);
        point.Scalars[AxKeys.Support] = FieldState.Of("Fixed");
        Family(point, AxKeys.NodalLoad, "LC=Dead; F=0,0,-25,0,0,0", "LC=Wind; F=8,0,0,0,0,0");
        Render(new PointAttrForm(point, lib, null), "AxPoint", true);

        SelectionState line = State(3);
        line.Scalars[AxKeys.Type] = FieldState.Of("Beam");
        line.Scalars[AxKeys.Material] = FieldState.Of("S355");
        line.Scalars[AxKeys.Section] = FieldState.Mixed;
        Family(line, AxKeys.DistLoad, "LC=Dead; sys=Global; from=ratio:0; to=ratio:1; q1=0,0,-6,0,0,0; q2=0,0,-6,0,0,0");
        Render(new LineAttrForm(line, lib, null), "AxLine", true);

        SelectionState domain = State(1);
        domain.Scalars[AxKeys.FeType] = FieldState.Of("Plate");
        domain.Scalars[AxKeys.Material] = FieldState.Of("C30/37");
        domain.Scalars[AxKeys.Thickness] = FieldState.Of("250");
        Family(domain, AxKeys.DistLoad, "LC=Dead; sys=Global; q=0,0,-5");
        Render(new DomainAttrForm(domain, lib, null), "AxDomain", true);

        Render(new EdgeHingeForm(4, "1 domain, 4 edges"), "AxEdgeHinge", false);
        Render(new LibraryForm(lib, 0), "AxLibrary", false);
        Console.WriteLine("Rendered AxisBridge dialogs to " + output);
        return 0;
    }

    private static SelectionState State(int count)
    {
        return new SelectionState { Count = count };
    }

    private static void Family(SelectionState s, string key, params string[] rows)
    {
        s.Families[key] = new List<string>(rows);
        s.FamilyStates[key] = FieldState.Of(string.Join("|", rows));
        foreach (string row in rows)
        {
            string lc;
            if (AttrReader.ParseFields(row).TryGetValue("LC", out lc) && !s.UsedLoadCases.Contains(lc)) s.UsedLoadCases.Add(lc);
        }
    }

    // Saves every tab of the form (or the form itself when it has no tab control).
    private static void Render(Form form, string name, bool tabsOnForm)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        TabControl tabs = FindTabs(form);
        if (tabs == null || tabs.TabPages.Count == 0) { Save(form, name + ".png"); form.Dispose(); return; }
        for (int i = 0; i < tabs.TabPages.Count; i++)
        {
            tabs.SelectedIndex = i;
            form.PerformLayout();
            Application.DoEvents();
            string suffix = tabs.TabPages[i].Text.ToLowerInvariant().Replace(" ", "-").Replace("/", "").Replace("--", "-");
            Save(form, name + "-" + suffix + ".png");
        }
        form.Dispose();
    }

    private static TabControl FindTabs(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            TabControl t = c as TabControl;
            if (t != null) return t;
            t = FindTabs(c);
            if (t != null) return t;
        }
        return null;
    }

    private static void Save(Form form, string file)
    {
        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(output, file), ImageFormat.Png);
        }
        Console.WriteLine(file + " " + form.Width + "x" + form.Height);
    }
}
