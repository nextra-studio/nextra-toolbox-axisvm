using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;
using AxisVM_plugin.UI;

// Live check of the Timber SLS add-on in its OWN scratch AxisVM (AxisVmDialogGuard): a simply supported beam,
// a cantilever and a two-span beam with G and Q load cases are analysed, then the add-on's results are compared
// with beam formulas. The window is opened and every tab is saved as a PNG for a visual check.
// Usage: TimberSlsLiveChecks.exe <output folder>
internal static class TimberSlsLiveChecks
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    private static void Near(double got, double want, double relTol, string name)
    {
        bool ok = Math.Abs(got - want) <= relTol * Math.Abs(want);
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "  got " + TimberSlsMath.Sig(got) + ", want " + TimberSlsMath.Sig(want));
        if (!ok) failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : "TimberSlsLive");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("NEXTRA_TIMBERSLS_DIR", Path.Combine(root, "settings"));
        IAxisVMApplication app = null;
        try
        {
            app = AxisVmDialogGuard.Launch();
            app.AskCloseOnLastReleased = ELongBoolean.lbFalse;
            app.CloseOnLastReleased = ELongBoolean.lbFalse;
            IAxisVMModel model = AxisVmDialogGuard.FreshModel(app);
            Build(model, out int ss, out int cant, out int[] two, out int gCase, out int qCase);
            model.SaveToFile(Path.Combine(root, "TimberSlsLiveTest.axs"), ELongBoolean.lbFalse);
            ELongBoolean calc = model.Calculation.LinearAnalysis(ECalculationUserInteraction.cuiNoUserInteractionWithAutoCorrectNoShow);
            Check(calc == ELongBoolean.lbTrue, "linear analysis ran");

            TimberSlsBackend backend = new TimberSlsBackend(app, model);
            List<ModelLoadCase> cases = backend.ReadLoadCases();
            ModelLoadCase g = cases.First(c => c.Id == gCase), q = cases.First(c => c.Id == qCase);
            Check(g.Default.Role == LoadRole.Permanent, "G case read as permanent from its load group");
            Check(q.Default.Role == LoadRole.Variable && Math.Abs(q.Default.Psi2 - 0.3) < 1e-9, "Q case variable with ψ2 = 0.3 from its group");

            TimberSlsSettings st = new TimberSlsSettings();
            st.FloorLines.Add(ss);
            st.FloorLines.AddRange(two);
            st.RoofLines.Add(cant);
            st.Floor.KdefSource = KdefSource.Custom; st.Floor.KdefCustom = 0.6;
            st.Roof.KdefSource = KdefSource.Custom; st.Roof.KdefCustom = 0.6;
            st.Vibration.Method = VibrationMethod.En1995_2025;
            st.Vibration.Spacing = 0.6;
            SlsRun run = backend.Run(st, cases);
            foreach (string n in run.Notes) Console.WriteLine("note: " + n);
            Check(run.Combinations.Count == 1, "one combination (G + Q lead), got " + run.Combinations.Count);

            double E = 11e9, I = 0.1 * Math.Pow(0.2, 3) / 12, EI = E * I;   // C24, 100 x 200
            SpanResult a = run.Spans.First(r => r.Span.Lines.Contains(ss));
            Console.WriteLine("A: E = " + a.Span.E + ", Iy = " + a.Span.Iy + ", AxisVM kdef = " + a.Span.KdefModel + ", SC " + a.Span.ServiceClass + ", timber " + a.Span.IsTimber + ", checked as " + a.Reference);
            Console.WriteLine("C spans: " + string.Join("; ", run.Spans.Where(r => r.Span.Lines.Any(l => two.Contains(l))).Select(r => r.Span.Label + " n=" + r.Span.SpansInChain)));
            Near(a.Span.E * a.Span.Iy, EI, 0.12, "A: E·I read from AxisVM (C24 100x200)");
            double eiA = a.Span.E * a.Span.Iy;
            double wG = 5 * 1000 * Math.Pow(4, 4) / (384 * eiA) * 1000;
            Near(a.Defl.W[gCase].Max(Math.Abs), wG, 0.03, "A: w_G midspan = 5qL⁴/384EI");
            Check(!a.Span.Cantilever, "A: auto picks 'span'");
            double wEind = wG * 1.6 + 2 * wG * (1 + 0.3 * 0.6);
            RowResult eind = a.Rows.First(r => r.Row.Quantity == SlsQuantity.WnetFin);
            Near(eind.W, wEind, 0.03, "A: w_eind = wG(1+kdef) + wQ(1+ψ2 kdef)");
            Near(eind.Limit, 16.0, 1e-9, "A: limit l/250 = 16 mm");

            SpanResult b = run.Spans.First(r => r.Span.Lines.Contains(cant));
            double eiB = b.Span.E * b.Span.Iy;
            Check(b.Span.Cantilever && !b.Span.RootAtEnd, "B: auto picks cantilever with the root at the fixed start (" + b.Reference + ")");
            Near(b.Defl.W[gCase].Max(Math.Abs), 1000 * Math.Pow(2, 4) / (8 * eiB) * 1000, 0.03, "B: tip w_G = qL⁴/8EI");
            Near(b.LRep, 4.0, 1e-9, "B: l_rep = 2 × 2 m");

            List<SpanResult> c2 = run.Spans.Where(r => r.Span.Lines.Any(l => two.Contains(l))).ToList();
            Check(c2.Count == 2 && c2.All(r => r.Span.SpansInChain == 2), "C: two-span beam split at the middle support");
            double wTwo = 1000 * Math.Pow(3, 4) / (185 * c2[0].Span.E * c2[0].Span.Iy) * 1000;   // max of a two-span continuous beam, qL⁴/(185EI)
            Near(c2[0].Defl.W[gCase].Max(Math.Abs), wTwo, 0.05, "C: w_G two-span ≈ qL⁴/185EI");
            Check(a.Vib25 != null && a.Vib25.F1 > 0, "A: 2025 vibration evaluated, f1 = " + (a.Vib25 == null ? "-" : TimberSlsMath.Sig(a.Vib25.F1)));
            Check(c2[0].Vib25 != null && c2[0].Vib25.Ke1 > 1.0 - 1e-9, "C: double span floor k_e,1 = " + (c2[0].Vib25 == null ? "-" : TimberSlsMath.Sig(c2[0].Vib25.Ke1)));
            Check(b.Vib25 == null, "B: roof cantilever has no vibration check");

            // Each-line mode: the two-span beam is still two spans; a forced 'span' type on the cantilever
            st.General.SpanMode = SpanMode.EachLine;
            st.LineTypes[cant] = SpanType.Span;
            SlsRun run2 = backend.Run(st, cases);
            SpanResult b2 = run2.Spans.First(r => r.Span.Lines.Contains(cant));
            Check(!b2.Span.Cantilever, "B: type 'Span' is respected");
            st.LineTypes[cant] = SpanType.Cantilever;
            run2 = backend.Run(st, cases);
            b2 = run2.Spans.First(r => r.Span.Lines.Contains(cant));
            Check(b2.Span.Cantilever && !b2.Span.RootAtEnd, "B: type 'Cantilever' finds the root from the deflection");
            st.LineTypes.Clear();
            st.General.SpanMode = SpanMode.WholeBeam;

            // Overlay
            List<TimberSlsBackend.Piece> pieces = run.Spans.Select(r => new TimberSlsBackend.Piece { Span = r.Span, X0 = 0, X1 = r.Span.Length, Uc = r.Uc }).ToList();
            string err = backend.ShowOverlay(pieces, run.Spans.Select(r => new KeyValuePair<SpanInfo, string>(r.Span, TimberSlsMath.Num(r.Uc, 2))).ToList(), new DisplaySettings());
            Check(err == null, "overlay drawn" + (err == null ? "" : ": " + err));
            Check(LayerCount(model) == 1, "overlay layer exists");
            backend.HideOverlay();
            Check(LayerCount(model) == 0, "overlay layer removed");

            // The window: every tab as a PNG, then a run from the button
            st.Save(backend.ModelPath);
            Screens(backend, root);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  exception: " + ex);
            failures++;
        }
        finally
        {
            if (app != null) { try { app.Quit(); } catch { } AxisVmDialogGuard.WaitClosed(60); }
        }
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL LIVE CHECKS PASSED" : failures + " LIVE CHECK(S) FAILED");
        return failures;
    }

    private static int LayerCount(IAxisVMModel model)
    {
        int n = 0;
        for (int i = 1; i <= model.Layers.Count; i++) if (model.Layers.get_Name(i) == TimberSlsFunction.LayerName) n++;
        return n;
    }

    private static void Screens(TimberSlsBackend backend, string root)
    {
        TimberSlsForm form = new TimberSlsForm(backend);
        form.Show();
        Application.DoEvents();
        TabControl tabs = Find<TabControl>(form);
        for (int i = 0; i < tabs.TabCount; i++)
        {
            tabs.SelectedIndex = i;
            if (i == 4) typeof(TimberSlsForm).GetMethod("RunCheck", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
            for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
            using (Bitmap bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(Path.Combine(root, "TimberSlsForm-tab" + (i + 1) + ".png"));
            }
        }
        // Results tab, details sub-tab
        TabControl inner = FindAll<TabControl>(form).First(t => t != tabs);
        inner.SelectedIndex = 1;
        Application.DoEvents();
        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(root, "TimberSlsForm-tab5-details.png"));
        }
        form.Close();
        Check(true, "window screenshots saved to " + root);
    }

    private static T Find<T>(Control c) where T : Control { return FindAll<T>(c).First(); }

    private static IEnumerable<T> FindAll<T>(Control c) where T : Control
    {
        foreach (Control k in c.Controls)
        {
            if (k is T) yield return (T)k;
            foreach (T x in FindAll<T>(k)) yield return x;
        }
    }

    private static void Build(IAxisVMModel m, out int ss, out int cant, out int[] two, out int gCase, out int qCase)
    {
        int mat = 0;
        try { mat = m.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C24"); } catch { }
        if (mat <= 0) mat = m.Materials.AddTimber(ENationalDesignCode.ndcEuroCode, "EuroCode", "Timber", "C24", 0xFFD2B48C, 0xFF000000, 11E6, 370E3, 370E3, 0.2, 0.2, 0.2, 5E-6, 5E-6, 5E-6, 420.0);
        int sec = m.CrossSections.AddRectangular("SLS 100x200", 0.1, 0.2, ECrossSectionProcess.cspOther);
        Console.WriteLine("material " + mat + ", section " + sec);
        Func<double, double, int> node = (x, y) => m.Nodes.Add(x, y, 0);
        Func<int, int, int> beam = (n1, n2) =>
        {
            RLineGeomData geo = new RLineGeomData();
            int id = m.Lines.Add(n1, n2, ELineGeomType.lgtStraightLine, ref geo);
            RPoint3d e = new RPoint3d();
            m.Lines.Item[id].DefineAsBeam(mat, sec, sec, ref e, ref e);
            return id;
        };
        // A: simply supported 4 m; B: cantilever 2 m; C: two spans 3 + 3 m
        int a0 = node(0, 0), a1 = node(4, 0);
        int b0 = node(0, 2), b1 = node(2, 2);
        int c0 = node(0, 4), c1 = node(3, 4), c2 = node(6, 4);
        ss = beam(a0, a1);
        cant = beam(b0, b1);
        two = new[] { beam(c0, c1), beam(c1, c2) };
        Support(m, a0, true, true, true, false, false, true);
        Support(m, a1, false, true, true, false, false, false);
        Support(m, b0, true, true, true, true, true, true);
        Support(m, c0, true, true, true, false, false, true);
        Support(m, c1, false, true, true, false, false, false);
        Support(m, c2, false, true, true, false, false, false);

        int gg = m.LoadGroups.Add("Permanent", ELoadGroupType.lgtPermanent, ELongBoolean.lbFalse, EGroupCombinationType.gctAdditive);
        int qg = m.LoadGroups.Add("Floor A", ELoadGroupType.lgtIncidental, ELongBoolean.lbFalse, EGroupCombinationType.gctAdditive);
        IAxisVMLoadGroup qgroup = m.LoadGroups.Item[qg];
        qgroup.Psi0 = 0.4; qgroup.Psi1 = 0.5; qgroup.Psi2 = 0.3;
        gCase = m.LoadCases.AddWithGroup("G", ELoadCaseType.lctStandard, gg);
        qCase = m.LoadCases.AddWithGroup("Q", ELoadCaseType.lctStandard, qg);
        foreach (int line in new[] { ss, cant, two[0], two[1] })
        {
            Load(m, gCase, line, -1.0);
            Load(m, qCase, line, -2.0);
        }
    }

    private static void Support(IAxisVMModel m, int node, bool x, bool y, bool z, bool xx, bool yy, bool zz)
    {
        const double k = 1e10;
        RStiffnesses s = new RStiffnesses { x = x ? k : 0, y = y ? k : 0, z = z ? k : 0, xx = xx || true ? k : 0, yy = yy ? k : 0, zz = zz ? k : 0 };
        RNonLinearity nl = new RNonLinearity();
        RResistances rs = new RResistances();
        int id = m.NodalSupports.AddNodalGlobal(ref s, ref nl, ref rs, node);
        if (id <= 0) Console.WriteLine("support at node " + node + " rejected: " + id);
    }

    private static void Load(IAxisVMModel m, int lc, int line, double qz)
    {
        RLoadBeamDistributed r = new RLoadBeamDistributed
        {
            LoadCaseId = lc, LineId = line, SystemGLR = ESystem.sysGlobal, qz1 = qz, qz2 = qz,
            Position1 = 0, Position2 = m.Lines.Item[line].Length, DistributionType = EBeamRibDistributionType.brdtLength, Trapezoid = ELongBoolean.lbFalse
        };
        int id = m.Loads.AddBeamDistributed(ref r);
        if (id <= 0) Console.WriteLine("load on line " + line + " rejected: " + id);
    }
}
