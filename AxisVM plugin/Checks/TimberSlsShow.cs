using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;
using AxisVM_plugin.UI;

// Shows the Timber SLS window on the nine-case test model in an AxisVM that is ALREADY running (pid in AXISVM_PID).
// The model is reused when it is already open, otherwise loaded into a new model and analysed. With a shots folder,
// every tab is saved as a PNG first; the window then stays on the Results tab until it is closed.
// Usage: TimberSlsShow.exe <model.axs> <settings folder> [shots folder]
internal static class TimberSlsShow
{
    [STAThread]
    private static int Main(string[] args)
    {
        string model = Path.GetFullPath(args[0]);
        Environment.SetEnvironmentVariable("NEXTRA_TIMBERSLS_DIR", Path.GetFullPath(args[1]));
        string shots = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
        IAxisVMApplication app = AxisVmDialogGuard.Attach(int.Parse(Environment.GetEnvironmentVariable("AXISVM_PID")));
        IAxisVMModel m = null;
        for (int i = 1; i <= app.Models.Count; i++)
            if (string.Equals(app.Models.Item[i].FileName, model, StringComparison.OrdinalIgnoreCase)) m = app.Models.Item[i];
        if (m == null)
        {
            m = app.Models.Item[app.Models.New()];
            Console.WriteLine("load: " + m.LoadFromFile(model));
        }
        Console.WriteLine("analysis: " + m.Calculation.LinearAnalysis(ECalculationUserInteraction.cuiNoUserInteractionWithAutoCorrectNoShow));
        m.View = EView.vPerspective; m.FitInView(); m.Windows.ReDraw();

        TimberSlsSettings st = new TimberSlsSettings();
        st.FloorLines.AddRange(new[] { 1, 3, 4, 5, 6, 7, 10, 11 });
        st.RoofLines.AddRange(new[] { 2, 8, 9, 12, 13, 14 });
        st.Floor.KdefSource = KdefSource.Custom; st.Floor.KdefCustom = 0.6;
        st.Roof.KdefSource = KdefSource.FromAxisVM;
        st.Roof.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[3]);
        st.Vibration.Method = VibrationMethod.En1995_2025;
        // Example of a member's own floor field: the plan-diagonal joists (X2) sit in a narrower, closer-spaced bay
        st.VibOverrides[10] = new VibOverride { Line = 10, Width = 3.0, Spacing = 0.4 };
        st.VibOverrides[11] = new VibOverride { Line = 11, Width = 3.0, Spacing = 0.4 };
        TimberSlsBackend backend = new TimberSlsBackend(app, m);
        st.Save(backend.ModelPath);

        TimberSlsForm form = new TimberSlsForm(backend);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(20, 20);
        form.ShowInTaskbar = true;
        form.Shown += delegate
        {
            BindingFlags np = BindingFlags.NonPublic | BindingFlags.Instance;
            TabControl tabs = (TabControl)typeof(TimberSlsForm).GetField("tabs", np).GetValue(form);
            typeof(TimberSlsForm).GetMethod("RunCheck", np).Invoke(form, null);
            if (shots != null)
            {
                Directory.CreateDirectory(shots);
                for (int i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i;
                    if (i == 2)
                    {
                        // Select the w_bij row so the drawing highlights it
                        DataGridView rows = (DataGridView)typeof(TimberSlsForm).GetField("rowGrid", np).GetValue(form);
                        if (rows.Rows.Count > 1) rows.CurrentCell = rows.Rows[1].Cells[1];
                    }
                    Snap(form, Path.Combine(shots, "tab" + (i + 1) + ".png"));
                }
                tabs.SelectedIndex = 4;
                Snap(form, Path.Combine(shots, "tab5.png"));
            }
            tabs.SelectedIndex = 4;
            if (shots != null)
            {
                try
                {
                    m.View = EView.vTop; m.FitInView(); m.Windows.ReDraw();
                    m.Windows.SaveWindowToBitmap(1, EWindowColourMode.wcmColour, Path.Combine(shots, "axisvm-top.bmp"));
                    m.View = EView.vPerspective; m.FitInView(); m.Windows.ReDraw();
                }
                catch (Exception ex) { Console.WriteLine("bitmap: " + ex.Message); }
            }
            form.Activate();
        };
        Application.Run(form);
        return 0;
    }

    private static void Snap(Form f, string path)
    {
        for (int k = 0; k < 15; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path);
        }
    }
}
