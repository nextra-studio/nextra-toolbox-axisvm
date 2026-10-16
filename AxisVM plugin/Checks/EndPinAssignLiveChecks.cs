using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Functions;
using AxisVM_plugin.UI;

// Creates its own AxisVM COM server and scratch model. Never attaches to a user's model.
// The production settings dialog is configured and confirmed in-process by a UI timer, and the
// summary message box is dismissed the same way, so every case runs unattended.
internal static class EndPinAssignLiveChecks
{
    private static IAxisVMModel model;
    private static int[] lines;
    private static int checks;

    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetDlgItemText(IntPtr hDlg, int id, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static IAxisVMApplication app;

    private static void Set(EndPinAssignForm form, string field, Action<Control> apply)
    {
        apply((Control)typeof(EndPinAssignForm).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form));
    }

    /// <summary>Runs the production command, configuring its dialog and closing its summary automatically.</summary>
    private static int Run(bool modifyIntermediate, bool torsion, int target)
    {
        bool configured = false;
        uint thread = GetCurrentThreadId();
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += delegate
        {
            foreach (Form open in Application.OpenForms)
            {
                var form = open as EndPinAssignForm;
                if (form == null || configured || !form.Visible) continue;
                configured = true;
                Set(form, "chkModifyIntermediate", c => ((CheckBox)c).Checked = modifyIntermediate);
                Set(form, "chkReleaseTorsion", c => ((CheckBox)c).Checked = torsion);
                Set(form, "cmbPinTarget", c => ((ComboBox)c).SelectedIndex = target);
                Set(form, "btnOk", c => ((Button)c).PerformClick());
                return;
            }
            EnumThreadWindows(thread, (h, l) =>
            {
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                var title = new StringBuilder(128); GetWindowText(h, title, 128);
                if (cls.ToString() != "#32770" || title.ToString() != "End Pin Assign") return true;
                var body = new StringBuilder(4096); GetDlgItemText(h, 0xFFFF, body, 4096);
                Console.WriteLine("  summary: " + body.ToString().Replace("\r\n", " | ").Trim());
                IntPtr ok = FindWindowEx(h, IntPtr.Zero, "Button", null);
                if (ok != IntPtr.Zero) SendMessage(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                else SendMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return false;
            }, IntPtr.Zero);
        };
        // AxisVM clears the selection after the command edits the model; select the chain again.
        foreach (int id in lines) model.Lines.set_Selected(id, ELongBoolean.lbTrue);
        timer.Start();
        try { return EndPinAssignFunction.Execute(app); }
        finally
        {
            timer.Stop(); timer.Dispose();
            if (!configured) throw new Exception("Settings dialog was never shown.");
        }
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static RReleases Read(int line, bool start)
    {
        RReleases rel = new RReleases();
        int rc = start ? model.Lines.Item[line].GetStartReleases(ref rel) : model.Lines.Item[line].GetEndReleases(ref rel);
        if (rc <= 0) throw new Exception("Could not read releases.");
        return rel;
    }

    private static void Write(int line, bool start, RReleases rel)
    {
        int rc = start ? model.Lines.Item[line].SetStartReleases(ref rel) : model.Lines.Item[line].SetEndReleases(ref rel);
        if (rc <= 0) throw new Exception("Could not seed releases.");
    }

    private static RRelease Spring(double stiffness)
    {
        return new RRelease { ReleaseType = EReleaseType.rtSemiRigid, Init = stiffness };
    }

    private static bool Fixed(RReleases r)
    {
        return r.x.ReleaseType == EReleaseType.rtRigid && r.y.ReleaseType == EReleaseType.rtRigid && r.z.ReleaseType == EReleaseType.rtRigid &&
            r.xx.ReleaseType == EReleaseType.rtRigid && r.yy.ReleaseType == EReleaseType.rtRigid && r.zz.ReleaseType == EReleaseType.rtRigid;
    }

    private static bool Pinned(RReleases r)
    {
        return r.yy.ReleaseType == EReleaseType.rtSemiRigid && r.yy.Init == 1 && r.zz.ReleaseType == EReleaseType.rtSemiRigid && r.zz.Init == 1;
    }

    private static void Seed()
    {
        foreach (int id in lines)
        {
            Write(id, true, new RReleases());
            Write(id, false, new RReleases());
        }
        // Mixed directions: line 2 runs backwards. Its two ends are both intermediate.
        Write(lines[0], false, new RReleases { yy = new RRelease { ReleaseType = EReleaseType.rtHinged }, zz = Spring(17) });
        Write(lines[1], false, new RReleases { yy = Spring(31), zz = new RRelease { ReleaseType = EReleaseType.rtHinged } });
        Write(lines[1], true, new RReleases { xx = new RRelease { ReleaseType = EReleaseType.rtHinged }, yy = Spring(29), zz = Spring(41) });
        Write(lines[2], true, new RReleases { x = new RRelease { ReleaseType = EReleaseType.rtHinged }, yy = Spring(37) });
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            app = (IAxisVMApplication)Activator.CreateInstance(Type.GetTypeFromProgID("AxisVM.AxisVMApplication", true));
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (app.Loaded != ELongBoolean.lbTrue)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("AxisVM startup timed out.");
                Thread.Sleep(250);
            }
            app.AskCloseOnLastReleased = ELongBoolean.lbFalse;
            app.CloseOnLastReleased = ELongBoolean.lbFalse;
            app.Visible = ELongBoolean.lbTrue;
            app.MainFormWindowState = EWindowState.wsNormal;
            Check(app.Models.Count == 0 || (app.Models.Count == 1 && app.Models.Item[1].Nodes.Count == 0), "new AxisVM instance has no existing geometry");
            int index = app.Models.New();
            model = app.Models.Item[index];
            Check(index == 1, "scratch model is the add-on's active model 1");
            int material = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "S235");
            if (material <= 0) material = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "S 235");
            int section = model.CrossSections.AddRectangular("EndPinLiveTest", 0.2, 0.3, ECrossSectionProcess.cspOther);
            Console.WriteLine("Material result: " + material + "; section result: " + section);
            Check(material > 0 && section > 0, "test material and section created");
            int[] nodes = new int[4];
            for (int i = 0; i < 4; i++) nodes[i] = model.Nodes.Add(i * 3, 0, 0);
            lines = new int[3];
            for (int i = 0; i < 3; i++)
            {
                var geometry = new RLineGeomData();
                int a = i == 1 ? nodes[i + 1] : nodes[i];
                int b = i == 1 ? nodes[i] : nodes[i + 1];
                lines[i] = model.Lines.Add(a, b, ELineGeomType.lgtStraightLine, ref geometry);
                var eccentricity = new RPoint3d();
                Check(model.Lines.Item[lines[i]].DefineAsBeam(material, section, section, ref eccentricity, ref eccentricity) > 0, "beam " + (i + 1) + " created");
                model.Lines.set_Selected(lines[i], ELongBoolean.lbTrue);
            }
            Seed();
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            model.SaveToFile(Path.Combine(root, "EndPinLiveTest-initial.axs"), ELongBoolean.lbFalse);

            Console.WriteLine("CASE 1: Modify intermediate OFF, defaults (semi-rigid, both ends, no torsion).");
            Check(Run(false, false, 0) == 1, "preserve run completed");
            // Default auto-orient has flipped line 2; physical joints must retain their releases.
            Check(Read(lines[0], false).zz.Init == 17 && Read(lines[0], false).yy.ReleaseType == EReleaseType.rtHinged, "OFF: first joint keeps its pin and spring");
            Check(Read(lines[1], true).yy.Init == 31 && Read(lines[1], true).zz.ReleaseType == EReleaseType.rtHinged, "OFF: reversed line keeps physical joint releases");
            Check(Read(lines[1], false).xx.ReleaseType == EReleaseType.rtHinged && Read(lines[1], false).zz.Init == 41, "OFF: intermediate torsion and springs kept");
            Check(Read(lines[2], true).yy.Init == 37 && Read(lines[2], true).x.ReleaseType == EReleaseType.rtHinged, "OFF: second joint and translation kept");
            Check(Pinned(Read(lines[0], true)) && Pinned(Read(lines[2], false)), "OFF: specified semi-rigid pin at both chain ends");
            model.SaveToFile(Path.Combine(root, "EndPinLiveTest-1-off.axs"), ELongBoolean.lbFalse);

            Console.WriteLine("CASE 2: Modify intermediate ON, torsion ON, both ends.");
            Check(Run(true, true, 0) == 1, "fix run completed");
            Check(Fixed(Read(lines[0], false)) && Fixed(Read(lines[1], true)) && Fixed(Read(lines[1], false)) && Fixed(Read(lines[2], true)),
                "ON: all four intermediate ends fully fixed (incl. translational hinge)");
            Check(Pinned(Read(lines[0], true)) && Pinned(Read(lines[2], false)), "ON: specified pin at both chain ends");
            int torsion = (Read(lines[0], true).xx.ReleaseType != EReleaseType.rtRigid ? 1 : 0) + (Read(lines[2], false).xx.ReleaseType != EReleaseType.rtRigid ? 1 : 0);
            Check(torsion == 1, "ON: exactly one torsion release in the chain");
            model.SaveToFile(Path.Combine(root, "EndPinLiveTest-2-on-both.axs"), ELongBoolean.lbFalse);

            Console.WriteLine("CASE 3: Modify intermediate ON, torsion ON, Start Node Only, after re-seeding old pins.");
            Seed();
            var opposite = Read(lines[2], false);
            opposite.yy = Spring(83); opposite.zz = Spring(89);
            Write(lines[2], false, opposite);
            Check(Run(true, true, 1) == 1, "start-only ON run completed");
            Check(Fixed(Read(lines[0], false)) && Fixed(Read(lines[1], true)) && Fixed(Read(lines[1], false)) && Fixed(Read(lines[2], true)),
                "ON start-only: old intermediate pins removed");
            Check(Fixed(Read(lines[2], false)), "ON start-only: old pin at the untargeted chain end removed");
            Check(Pinned(Read(lines[0], true)) && Read(lines[0], true).xx.ReleaseType != EReleaseType.rtRigid, "ON start-only: start pinned with torsion");
            model.SaveToFile(Path.Combine(root, "EndPinLiveTest-3-on-start.axs"), ELongBoolean.lbFalse);

            Console.WriteLine("CASE 4: Modify intermediate OFF, End Node Only, after re-seeding old pins.");
            Seed();
            Write(lines[0], true, new RReleases { yy = Spring(83), zz = Spring(89) });
            Check(Run(false, false, 2) == 1, "end-only OFF run completed");
            Check(Read(lines[0], false).zz.Init == 17 && Read(lines[1], true).xx.ReleaseType == EReleaseType.rtHinged && Read(lines[2], true).yy.Init == 37,
                "OFF end-only: intermediate pins stay pins");
            Check(Read(lines[0], true).yy.Init == 83 && Read(lines[0], true).zz.Init == 89, "OFF end-only: untargeted chain start kept");
            Check(Pinned(Read(lines[2], false)), "OFF end-only: chain end receives the specified pin");
            model.SaveToFile(Path.Combine(root, "EndPinLiveTest-4-off-end.axs"), ELongBoolean.lbFalse);
            model.Refresh();
            Console.WriteLine("ALL " + checks + " LIVE CHECKS PASSED. Scratch model left open: " + model.FileName);
            GC.KeepAlive(app);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
