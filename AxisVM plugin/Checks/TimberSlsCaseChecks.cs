using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.Core;
using AxisVM_plugin.Functions;
using AxisVM_plugin.UI;

// Nine verification cases of the Timber SLS add-on against hand calculations, in its OWN scratch AxisVM.
// Simple: S1 simply supported UDL, S2 cantilever UDL, S3 simply supported point load.
// Complicated: C1 beam with overhang (both cantilever references), C2 two-span continuous, C3 several load groups.
// Slanted: X1 inclined rafter, X2 plan-diagonal unequal two-span, X3 3D-sloped rafter with eave overhang in 3 lines.
// Hand values use closed-form beam formulas and an explicit enumeration of combinations, written independently of
// Core/TimberSlsMath. Output: report.csv, report.md and screenshots in <folder>.
// Usage: TimberSlsCaseChecks.exe <output folder>
internal static class TimberSlsCaseChecks
{
    // C24, 100 x 200 mm
    private const double E = 11e9, I = 0.1 * 0.2 * 0.2 * 0.2 / 12, EI = E * I;
    private static int failures;
    private static readonly List<string[]> report = new List<string[]>();
    private static IAxisVMModel m;
    private static int mat, sec, gC, qC, sC, wpC, wmC;

    private class Case
    {
        public string Id, Title, Group;
        public List<int> Lines = new List<int>();
        public int MainLine;
        public Action<TimberSlsSettings> Setup = s => { };
    }

    private static string N(double v) { return TimberSlsMath.Sig(v); }

    private static void Expect(string caseId, string what, string hand, double want, double got, double relTol)
    {
        double diff = want == 0 ? Math.Abs(got) : Math.Abs(got - want) / Math.Abs(want);
        bool ok = diff <= relTol;
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + caseId + "  " + what + ": add-on " + N(got) + ", hand " + N(want) + " (" + (diff * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %)");
        report.Add(new[] { caseId, what, hand, N(want), N(got), (diff * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %", ok ? "PASS" : "FAIL" });
    }

    private static void ExpectTrue(string caseId, string what, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + caseId + "  " + what + "  " + detail);
        report.Add(new[] { caseId, what, "-", "-", detail, "", ok ? "PASS" : "FAIL" });
    }

    // ------------------------------------------------------------------
    // Hand formulas (deflections in mm, q in kN/m, P in kN, L in m)
    // ------------------------------------------------------------------

    /// <summary>Simply supported UDL, w(x) = q x (L³ − 2Lx² + x³)/24EI.</summary>
    private static double UdlW(double q, double L, double x) { return q * 1000 * x * (L * L * L - 2 * L * x * x + x * x * x) / (24 * EI) * 1000; }

    /// <summary>End moment M (kNm, hogging) at the far end (x = L): upward w = M x (L² − x²)/(6 L EI).</summary>
    private static double EndMoment(double M, double L, double x) { return M * 1000 * x * (L * L - x * x) / (6 * L * EI) * 1000; }

    private static double MaxOver(double L, Func<double, double> w)
    {
        double best = 0;
        for (int i = 0; i <= 4000; i++) { double v = w(L * i / 4000.0); if (Math.Abs(v) > Math.Abs(best)) best = v; }
        return Math.Abs(best);
    }

    /// <summary>Overhang tip (root displacement reference, incl. back-span rotation), UDL on span L and overhang a.</summary>
    private static double OverhangTip(double q, double L, double a) { return Math.Abs(q * 1000 * a * (4 * a * a * L - L * L * L + 3 * a * a * a) / (24 * EI) * 1000); }

    private static double Eind(double wG, double wQ, double k, double psi2) { return wG * (1 + k) + wQ * (1 + psi2 * k); }
    private static double Bij(double wG, double wQ, double k, double psi2) { return wG * k + wQ * (1 + psi2 * k - psi2); }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : "TimberSlsCases");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("NEXTRA_TIMBERSLS_DIR", Path.Combine(root, "settings"));
        IAxisVMApplication app = null;
        try
        {
            app = AxisVmDialogGuard.Launch();
            app.AskCloseOnLastReleased = ELongBoolean.lbFalse;
            app.CloseOnLastReleased = ELongBoolean.lbFalse;
            app.Visible = ELongBoolean.lbTrue;
            m = AxisVmDialogGuard.FreshModel(app);
            List<Case> cases = Build();
            m.SaveToFile(Path.Combine(root, "TimberSlsCases.axs"), ELongBoolean.lbFalse);
            ELongBoolean calc = m.Calculation.LinearAnalysis(ECalculationUserInteraction.cuiNoUserInteractionWithAutoCorrectNoShow);
            ExpectTrue("all", "linear analysis", calc == ELongBoolean.lbTrue, calc.ToString());

            TimberSlsBackend backend = new TimberSlsBackend(app, m);
            List<ModelLoadCase> lcs = backend.ReadLoadCases();
            foreach (Case c in cases) Verify(c, backend, lcs, root);
            Overview(cases, backend, root);
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
        WriteReport(root);
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CASES PASSED" : failures + " CHECK(S) FAILED");
        return failures;
    }

    private static TimberSlsSettings Settings(Case c)
    {
        TimberSlsSettings st = new TimberSlsSettings();
        (c.Group == "Roof" ? st.RoofLines : st.FloorLines).AddRange(c.Lines);
        st.Floor.KdefSource = KdefSource.Custom; st.Floor.KdefCustom = 0.6;
        st.Roof.KdefSource = KdefSource.FromAxisVM;
        st.Roof.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[3]);   // other roof: w_eind l/250, w_bij l/250
        c.Setup(st);
        return st;
    }

    private static SpanResult Span(SlsRun run, int line) { return run.Spans.First(r => r.Span.Lines.Contains(line)); }
    private static double Max(SpanResult r, int lc) { return r.Defl.W[lc].Max(v => Math.Abs(v)); }
    private static double Row(SpanResult r, SlsQuantity q) { return r.Rows.First(x => x.Row.Quantity == q).W; }

    private static void Verify(Case c, TimberSlsBackend backend, List<ModelLoadCase> lcs, string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + c.Id + " " + c.Title);
        TimberSlsSettings st = Settings(c);
        SlsRun run = backend.Run(st, lcs);
        foreach (string n in run.Notes) Console.WriteLine("note: " + n);
        const double tol = 0.01;
        switch (c.Id)
        {
            case "S1":
            {
                SpanResult r = Span(run, c.MainLine);
                double wG = UdlW(1, 4, 2), wQ = UdlW(2, 4, 2);
                Expect(c.Id, "w_inst,G midspan", "5qL⁴/384EI, q = 1 kN/m, L = 4 m", wG, Max(r, gC), tol);
                Expect(c.Id, "w_eind", "wG(1+kdef) + wQ(1+ψ2·kdef), kdef 0.6, ψ2 0.3", Eind(wG, wQ, 0.6, 0.3), Row(r, SlsQuantity.WnetFin), tol);
                Expect(c.Id, "w_bij", "wG·kdef + wQ(1+ψ2·kdef−ψ2)", Bij(wG, wQ, 0.6, 0.3), Row(r, SlsQuantity.Wbij), tol);
                Expect(c.Id, "UC", "w_eind / (l/250 = 16 mm)", Eind(wG, wQ, 0.6, 0.3) / 16, r.DeflUc, tol);
                ExpectTrue(c.Id, "checked as", !r.Span.Cantilever, r.Reference);
                // EC5:2004 vibration on the same beam
                st.Vibration.Method = VibrationMethod.Ec5_2004;
                st.Vibration.Spacing = 0.6; st.Vibration.Width = 4; st.Vibration.EiTransverse = 1500; st.Vibration.PermanentLoad = 0.5; st.Vibration.Damping = 0.01;
                SpanResult v = Span(backend.Run(st, lcs), c.MainLine);
                double eiL = EI / 0.6, mass = 0.5 * 1000 / 9.81;
                double f1 = Math.PI / (2 * 16) * Math.Sqrt(eiL / mass);
                double kd = Math.Max(0.38 - 0.08 * Math.Log(14 * 1500e6 / Math.Pow(600, 4)), 0.30);
                double a = 1000 * 64 / (48 * EI) * kd * 1.05 * 1000;
                Expect(c.Id, "EC5:2004 f1", "π/(2L²)·√((EI)_L/m), (EI)_L = EI/0.6, m = 0.5 kN/m² /g", f1, v.Vib04.F1, tol);
                Expect(c.Id, "EC5:2004 a", "F L³/(48 EI)·k_dist·k_amp", a, v.Vib04.A, tol);
                st.Vibration.Method = VibrationMethod.Off;
                break;
            }
            case "S2":
            {
                SpanResult r = Span(run, c.MainLine);
                double k = 0.8;   // AxisVM's kdef of the line (service class 2), roof group reads it
                double wG = 1000.0 * 16 / (8 * EI) * 1000, wQ = 2 * wG;
                Expect(c.Id, "tip w_inst,G", "qL⁴/8EI, q = 1, L = 2 m", wG, Max(r, gC), tol);
                Expect(c.Id, "w_eind", "wG(1+0.8) + wQ(1+0.3·0.8)", Eind(wG, wQ, k, 0.3), Row(r, SlsQuantity.WnetFin), tol);
                Expect(c.Id, "l_rep", "2 × 2 m", 4.0, r.LRep, 1e-9);
                Expect(c.Id, "UC", "w_eind / (l_rep/250 = 16 mm)", Eind(wG, wQ, k, 0.3) / 16, r.DeflUc, tol);
                ExpectTrue(c.Id, "checked as", r.Span.Cantilever && !r.Span.RootAtEnd, r.Reference);
                break;
            }
            case "S3":
            {
                SpanResult r = Span(run, c.MainLine);
                double wG = 2000 * 125 / (48 * EI) * 1000, wQ = 1.5 * wG;
                Expect(c.Id, "w_inst,G midspan", "PL³/48EI, P = 2 kN, L = 5 m", wG, Max(r, gC), tol);
                Expect(c.Id, "w_inst,Q midspan", "PL³/48EI, P = 3 kN", wQ, Max(r, qC), tol);
                Expect(c.Id, "w_bij", "wG·0.6 + wQ(1+0.3·0.6−0.3)", Bij(wG, wQ, 0.6, 0.3), Row(r, SlsQuantity.Wbij), tol);
                Expect(c.Id, "w_eind", "wG·1.6 + wQ·1.18", Eind(wG, wQ, 0.6, 0.3), Row(r, SlsQuantity.WnetFin), tol);
                break;
            }
            case "C1":
            {
                SpanResult span = Span(run, c.Lines[0]), over = Span(run, c.Lines[1]);
                double L = 4, a = 2.5, M = 1 * a * a / 2;
                double wSpan = MaxOver(L, x => UdlW(1, L, x) - EndMoment(M, L, x));
                double tip = OverhangTip(1, L, a);
                ExpectTrue(c.Id, "split at the support", span != over && span.Span.SpansInChain == 2, "2 spans");
                Expect(c.Id, "span max w_inst,G", "max of qx(L³−2Lx²+x³)/24EI − Mx(L²−x²)/6LEI, M = qa²/2", wSpan, Max(span, gC), tol);
                Expect(c.Id, "overhang tip w_inst,G (root displ.)", "qa(4a²L − L³ + 3a³)/24EI, a = 2.5", tip, Max(over, gC), tol);
                Expect(c.Id, "overhang w_eind", "tipG·1.6 + tipQ·1.18", Eind(tip, 2 * tip, 0.6, 0.3), Row(over, SlsQuantity.WnetFin), tol);
                ExpectTrue(c.Id, "overhang checked as", over.Span.Cantilever && !over.Span.RootAtEnd, over.Reference);
                st.General.CantileverRef = CantileverReference.RootTangent;
                SpanResult t = Span(backend.Run(st, lcs), c.Lines[1]);
                Expect(c.Id, "overhang tip w_inst,G (root tangent)", "qa⁴/8EI (own bending only)", 1000 * Math.Pow(a, 4) / (8 * EI) * 1000, Max(t, gC), 0.02);
                ExpectTrue(c.Id, "tangent: root still at the support", t.Span.Cantilever && !t.Span.RootAtEnd, t.Reference);
                st.General.CantileverRef = CantileverReference.RootDisplacement;
                break;
            }
            case "C2":
            {
                SpanResult r = Span(run, c.Lines[0]);
                double w = 0.005416 * 1000 * 81 / EI * 1000;
                ExpectTrue(c.Id, "two spans in one chain", r.Span.SpansInChain == 2, "n = " + r.Span.SpansInChain);
                Expect(c.Id, "max w_inst,G", "0.005416·qL⁴/EI (two equal spans)", w, Max(r, gC), tol);
                Expect(c.Id, "w_eind", "w·1.6 + 2w·1.18", Eind(w, 2 * w, 0.6, 0.3), Row(r, SlsQuantity.WnetFin), tol);
                double eiL = EI / 0.6, mass = (0.5 + 0.175) * 1000 / 9.81;
                double f1 = 1.0 * Math.PI / (2 * 9) * Math.Sqrt(eiL / mass);
                double bef = Math.Min(0.95 * 3 * Math.Pow(1500 / eiL, 0.25), 4);
                Expect(c.Id, "2025 f1", "k_e1(1.0)·π/(2l²)·√((EI)_L/m), m = (0.5 + 0.1·1.75)/g", f1, r.Vib25.F1, tol);
                Expect(c.Id, "2025 w_1kN", "F l³/(48 (EI)_L b_ef), b_ef = 0.95 l ((EI)_T/(EI)_L)^0.25", 1000 * 27 / (48 * eiL * bef) * 1000, r.Vib25.W1kN, tol);
                break;
            }
            case "C3":
            {
                SpanResult r = Span(run, c.MainLine);
                double k = 0.8;
                double unit = UdlW(1, 4.5, 2.25);
                // Load per group (kN/m): G 1.0; Q 1.5 (ψ 0.4/0.5/0.3); S 0.8 (ψ 0/0.2/0); wind exclusive W+ 0.5, W− −0.7 (ψ 0.6/0.2/0)
                double g = 1.0 * unit, q = 1.5 * unit, s = 0.8 * unit;
                double[] wind = { 0.5 * unit, -0.7 * unit };
                double best = 0, bestBij = 0;
                int count = 0;
                // lead Q: wind absent / W+ / W− (snow ψ0 = ψ2 = 0 adds nothing as accompanying)
                foreach (double? w in new double?[] { null, wind[0], wind[1] })
                    Acc(ref best, ref bestBij, ref count, g * (1 + k) + q * (1 + 0.3 * k) + (w.HasValue ? w.Value * 0.6 : 0),
                        g * k + q * (1 + 0.3 * k - 0.3) + (w.HasValue ? w.Value * 0.6 : 0));
                // lead S: Q absent/present × wind absent/W+/W−
                foreach (bool withQ in new[] { false, true })
                    foreach (double? w in new double?[] { null, wind[0], wind[1] })
                        Acc(ref best, ref bestBij, ref count, g * (1 + k) + s + (withQ ? q * (0.4 + 0.3 * k) : 0) + (w.HasValue ? w.Value * 0.6 : 0),
                            g * k + s + (withQ ? q * (0.4 + 0.3 * k - 0.3) : 0) + (w.HasValue ? w.Value * 0.6 : 0));
                // lead W+ or W−: Q absent/present
                foreach (double w in wind)
                    foreach (bool withQ in new[] { false, true })
                        Acc(ref best, ref bestBij, ref count, g * (1 + k) + w + (withQ ? q * (0.4 + 0.3 * k) : 0),
                            g * k + w + (withQ ? q * (0.4 + 0.3 * k - 0.3) : 0));
                Expect(c.Id, "number of combinations", "enumerated by hand", count, run.Combinations.Count, 0);
                Expect(c.Id, "governing w_eind", "max over the 13 hand combinations", best, Row(r, SlsQuantity.WnetFin), tol);
                Expect(c.Id, "governing w_bij", "max over the 13 hand combinations", bestBij, Row(r, SlsQuantity.Wbij), tol);
                Expect(c.Id, "kdef from AxisVM", "line kdef (SC2)", 0.8, r.Kdef, 1e-6);
                break;
            }
            case "X1":
            {
                SpanResult r = Span(run, c.MainLine);
                double cos = 0.8, L = 5;
                double wG = UdlW(cos, L, L / 2), wQ = UdlW(2 * cos, L, L / 2);
                Expect(c.Id, "w_inst,G ⊥ member", "5(q·cosα)L⁴/384EI, L = 5 m, cosα = 0.8", wG, Max(r, gC), tol);
                Expect(c.Id, "w_eind", "wG·1.8 + wQ·1.24", Eind(wG, wQ, 0.8, 0.3), Row(r, SlsQuantity.WnetFin), tol);
                Expect(c.Id, "l", "√(4² + 3²)", 5.0, r.Span.Length, 1e-6);
                break;
            }
            case "X2":
            {
                SpanResult r1 = Span(run, c.Lines[0]), r2 = Span(run, c.Lines[1]);
                double L1 = 4, L2 = 2.5, MB = 1 * (L1 * L1 * L1 + L2 * L2 * L2) / (8 * (L1 + L2));
                double w1 = MaxOver(L1, x => UdlW(1, L1, x) - EndMoment(MB, L1, x));
                double w2 = MaxOver(L2, x => UdlW(1, L2, x) - EndMoment(MB, L2, L2 - x));
                Expect(c.Id, "span 1 max w_inst,G", "three-moment eq.: M_B = q(L1³+L2³)/8(L1+L2); SS UDL − end moment", w1, Max(r1, gC), tol);
                Expect(c.Id, "span 2 max w_inst,G", "same, moment at the start of span 2", w2, Max(r2, gC), 0.02);
                double ke1 = 1.25 + (1.21 - 1.25) * (2.5 / 4 - 0.6) / 0.1;
                double eiL = EI / 0.6, mass = (0.5 + 0.175) * 1000 / 9.81;
                Expect(c.Id, "2025 k_e,1", "Table 9.4 at l2/l = 0.625 (interpolated)", ke1, r1.Vib25.Ke1, 1e-6);
                Expect(c.Id, "2025 f1", "k_e1·π/(2l²)·√((EI)_L/m), l = 4 m", ke1 * Math.PI / (2 * 16) * Math.Sqrt(eiL / mass), r1.Vib25.F1, tol);
                break;
            }
            case "X3":
            {
                SpanResult main = Span(run, c.Lines[1]), over = Span(run, c.Lines[0]);
                double cos = Math.Cos(30 * Math.PI / 180), L = 3.6, a = 1.2, q = cos, M = q * a * a / 2;
                ExpectTrue(c.Id, "3 lines → 2 spans (plain node not split)", main.Span.Lines.Count == 2 && main.Span.SpansInChain == 2, main.Span.Label);
                double wMain = MaxOver(L, x => UdlW(q, L, x) - EndMoment(M, L, L - x));
                Expect(c.Id, "main span max w_inst,G ⊥", "UDL q·cos30 − eave moment q·cos30·a²/2 at the wall plate", wMain, Max(main, gC), tol);
                double tip = OverhangTip(q, L, a);
                Expect(c.Id, "eave tip |w_inst,G| (root displ.)", "|q a(4a²L − L³ + 3a³)|/24EI (tip moves up)", tip, Max(over, gC), tol);
                ExpectTrue(c.Id, "eave checked as cantilever, root at the wall plate", over.Span.Cantilever && over.Span.RootAtEnd, over.Reference);
                Expect(c.Id, "main span w_eind", "wG·1.8 + wQ(1.5×)·1.24", Eind(wMain, 1.5 * wMain, 0.8, 0.3), Row(main, SlsQuantity.WnetFin), tol);
                break;
            }
        }
        Screens(c, st, backend, root);
    }

    private static void Acc(ref double best, ref double bestBij, ref int count, double eind, double bij)
    {
        count++;
        best = Math.Max(best, Math.Abs(eind));
        bestBij = Math.Max(bestBij, Math.Abs(bij));
    }

    // ------------------------------------------------------------------
    // Screenshots
    // ------------------------------------------------------------------

    private static void Snap(Form f, string path)
    {
        for (int k = 0; k < 12; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path);
        }
    }

    private static TimberSlsForm OpenAndRun(TimberSlsSettings st, TimberSlsBackend backend, int selectLine)
    {
        st.Save(backend.ModelPath);
        TimberSlsForm form = new TimberSlsForm(backend);
        form.Show();
        Application.DoEvents();
        typeof(TimberSlsForm).GetMethod("RunCheck", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
        if (selectLine > 0)
        {
            DataGridView grid = (DataGridView)typeof(TimberSlsForm).GetField("resultGrid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            foreach (DataGridViewRow row in grid.Rows)
            {
                SpanResult r = row.Tag as SpanResult;
                if (r != null && r.Span.Lines.Contains(selectLine)) { grid.ClearSelection(); row.Selected = true; break; }
            }
        }
        return form;
    }

    private static void Screens(Case c, TimberSlsSettings st, TimberSlsBackend backend, string root)
    {
        TimberSlsForm form = OpenAndRun(st, backend, c.MainLine);
        Snap(form, Path.Combine(root, c.Id + "-results.png"));
        TabControl inner = FindAll<TabControl>(form).Skip(1).First();
        inner.SelectedIndex = 1;
        Snap(form, Path.Combine(root, c.Id + "-details.png"));
        form.Close();
    }

    /// <summary>All cases at once: the window and the coloured AxisVM model (perspective and top).</summary>
    private static void Overview(List<Case> cases, TimberSlsBackend backend, string root)
    {
        TimberSlsSettings st = new TimberSlsSettings();
        foreach (Case c in cases) (c.Group == "Roof" ? st.RoofLines : st.FloorLines).AddRange(c.Lines);
        st.Floor.KdefSource = KdefSource.Custom; st.Floor.KdefCustom = 0.6;
        st.Roof.KdefSource = KdefSource.FromAxisVM;
        st.Roof.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[3]);
        TimberSlsForm form = OpenAndRun(st, backend, cases[0].MainLine);
        Snap(form, Path.Combine(root, "ALL-results.png"));
        try
        {
            m.View = EView.vPerspective; m.FitInView(); m.Windows.ReDraw();
            Application.DoEvents(); System.Threading.Thread.Sleep(800);
            m.Windows.SaveWindowToBitmap(1, EWindowColourMode.wcmColour, Path.Combine(root, "ALL-axisvm-perspective.bmp"));
            m.View = EView.vTop; m.FitInView(); m.Windows.ReDraw();
            Application.DoEvents(); System.Threading.Thread.Sleep(800);
            m.Windows.SaveWindowToBitmap(1, EWindowColourMode.wcmColour, Path.Combine(root, "ALL-axisvm-top.bmp"));
            ExpectTrue("all", "AxisVM model coloured and saved", File.Exists(Path.Combine(root, "ALL-axisvm-top.bmp")), "bitmaps saved");
        }
        catch (Exception ex) { ExpectTrue("all", "AxisVM window bitmap", false, ex.Message); }
        // The add-on's own exports (report + CSV) for the whole model
        BindingFlags np = BindingFlags.NonPublic | BindingFlags.Instance;
        string reportHtml = (string)typeof(TimberSlsForm).GetMethod("ReportHtml", np).Invoke(form, null);
        string table = (string)typeof(TimberSlsForm).GetMethod("Table", np).Invoke(form, new object[] { ',' });
        File.WriteAllText(Path.Combine(root, "addon-report.html"), reportHtml, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "addon-export.csv"), table, new UTF8Encoding(true));
        ExpectTrue("all", "report export", reportHtml.Contains("1 Summary") && reportHtml.Contains("data:image/png;base64"), (reportHtml.Length / 1024) + " kB");
        ExpectTrue("all", "CSV export", table.Split(new[] { (char)10 }).Length > 12, (table.Split(new[] { (char)10 }).Length - 2) + " rows");
        form.Close();
    }

    private static IEnumerable<T> FindAll<T>(Control c) where T : Control
    {
        foreach (Control k in c.Controls)
        {
            if (k is T) yield return (T)k;
            foreach (T x in FindAll<T>(k)) yield return x;
        }
    }

    private static void WriteReport(string root)
    {
        StringBuilder csv = new StringBuilder("Case,Quantity,Hand calculation,Hand,Add-on,Difference,Result\r\n");
        StringBuilder md = new StringBuilder("| Case | Quantity | Hand calculation | Hand | Add-on | Diff. | Result |\r\n|---|---|---|---|---|---|---|\r\n");
        foreach (string[] r in report)
        {
            csv.AppendLine(string.Join(",", r.Select(v => "\"" + v.Replace("\"", "\"\"") + "\"")));
            md.AppendLine("| " + string.Join(" | ", r) + " |");
        }
        File.WriteAllText(Path.Combine(root, "report.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "report.md"), md.ToString(), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------
    // Model
    // ------------------------------------------------------------------

    private static int Node(double x, double y, double z) { return m.Nodes.Add(x, y, z); }

    private static int Beam(int a, int b)
    {
        RLineGeomData geo = new RLineGeomData();
        int id = m.Lines.Add(a, b, ELineGeomType.lgtStraightLine, ref geo);
        RPoint3d e = new RPoint3d();
        m.Lines.Item[id].DefineAsBeam(mat, sec, sec, ref e, ref e);
        return id;
    }

    /// <summary>Pin in the beam's own axes: translations and torsion about the member axis fixed, bending free.</summary>
    private static void Pin(int n, int line) { Support(n, line, false); }
    private static void Fix(int n, int line) { Support(n, line, true); }

    private static void Support(int n, int line, bool rotations)
    {
        const double k = 1e10;
        RStiffnesses s = new RStiffnesses { x = k, y = k, z = k, xx = k, yy = rotations ? k : 0, zz = rotations ? k : 0 };
        RNonLinearity nl = new RNonLinearity();
        RResistances rs = new RResistances();
        if (m.NodalSupports.AddNodalBeamRelative(ref s, ref nl, ref rs, n, line) <= 0) Console.WriteLine("support rejected at node " + n);
    }

    private static void Udl(int lc, int line, double qz)
    {
        RLoadBeamDistributed r = new RLoadBeamDistributed
        {
            LoadCaseId = lc, LineId = line, SystemGLR = ESystem.sysGlobal, qz1 = qz, qz2 = qz,
            Position1 = 0, Position2 = m.Lines.Item[line].Length, DistributionType = EBeamRibDistributionType.brdtLength, Trapezoid = ELongBoolean.lbFalse
        };
        if (m.Loads.AddBeamDistributed(ref r) <= 0) Console.WriteLine("UDL rejected on line " + line);
    }

    private static void Point(int lc, int line, double fz, double pos)
    {
        RLoadBeamConcentrated r = new RLoadBeamConcentrated { LoadCaseId = lc, LineId = line, SystemGLR = ESystem.sysGlobal, Fgz = fz, Position = pos };
        if (m.Loads.AddBeamConcentrated(ref r) <= 0) Console.WriteLine("point load rejected on line " + line);
    }

    private static void GQ(IEnumerable<int> lines, double g, double q)
    {
        foreach (int l in lines) { Udl(gC, l, -g); Udl(qC, l, -q); }
    }

    private static List<Case> Build()
    {
        try { mat = m.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C24"); } catch { }
        if (mat <= 0) mat = m.Materials.AddTimber(ENationalDesignCode.ndcEuroCode, "EuroCode", "Timber", "C24", 0xFFD2B48C, 0xFF000000, 11E6, 370E3, 370E3, 0.2, 0.2, 0.2, 5E-6, 5E-6, 5E-6, 420.0);
        sec = m.CrossSections.AddRectangular("SLS 100x200", 0.1, 0.2, ECrossSectionProcess.cspOther);

        int gg = m.LoadGroups.Add("Permanent", ELoadGroupType.lgtPermanent, ELongBoolean.lbFalse, EGroupCombinationType.gctAdditive);
        int qg = m.LoadGroups.Add("Floor A", ELoadGroupType.lgtIncidental, ELongBoolean.lbFalse, EGroupCombinationType.gctAdditive);
        // Snow / wind as incidental groups with their own ψ (AxisVM's snow / wind group types need generated load cases)
        int sg = m.LoadGroups.Add("Snow", ELoadGroupType.lgtIncidental, ELongBoolean.lbFalse, EGroupCombinationType.gctAdditive);
        int wg = m.LoadGroups.Add("Wind", ELoadGroupType.lgtIncidental, ELongBoolean.lbFalse, EGroupCombinationType.gctExclusive);
        Console.WriteLine("groups " + gg + " " + qg + " " + sg + " " + wg);
        SetPsi(qg, 0.4, 0.5, 0.3); SetPsi(sg, 0, 0.2, 0); SetPsi(wg, 0.6, 0.2, 0);
        gC = m.LoadCases.AddWithGroup("G", ELoadCaseType.lctStandard, gg);
        qC = m.LoadCases.AddWithGroup("Q", ELoadCaseType.lctStandard, qg);
        sC = m.LoadCases.AddWithGroup("S", ELoadCaseType.lctStandard, sg);
        wpC = m.LoadCases.AddWithGroup("W+", ELoadCaseType.lctStandard, wg);
        wmC = m.LoadCases.AddWithGroup("W-", ELoadCaseType.lctStandard, wg);

        List<Case> cases = new List<Case>();
        // S1 simply supported 4 m, UDL G 1 / Q 2
        {
            int a = Node(0, 0, 0), b = Node(4, 0, 0);
            Case c = new Case { Id = "S1", Title = "Simply supported 4 m, UDL", Group = "Floor" };
            c.MainLine = Beam(a, b); c.Lines.Add(c.MainLine); Pin(a, c.MainLine); Pin(b, c.MainLine); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // S2 cantilever 2 m, roof
        {
            int a = Node(0, 3, 0), b = Node(2, 3, 0);
            Case c = new Case { Id = "S2", Title = "Cantilever 2 m, UDL (roof)", Group = "Roof" };
            c.MainLine = Beam(a, b); c.Lines.Add(c.MainLine); Fix(a, c.MainLine); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // S3 simply supported 5 m, midspan point loads G 2 kN / Q 3 kN
        {
            int a = Node(0, 6, 0), b = Node(5, 6, 0);
            Case c = new Case { Id = "S3", Title = "Simply supported 5 m, point load at midspan", Group = "Floor" };
            c.MainLine = Beam(a, b); c.Lines.Add(c.MainLine); Pin(a, c.MainLine); Pin(b, c.MainLine);
            Point(gC, c.MainLine, -2, 2.5); Point(qC, c.MainLine, -3, 2.5);
            cases.Add(c);
        }
        // C1 span 4 m + overhang 2.5 m
        {
            int a = Node(0, 9, 0), b = Node(4, 9, 0), t = Node(6.5, 9, 0);
            Case c = new Case { Id = "C1", Title = "Span 4 m with 2.5 m overhang, UDL", Group = "Floor" };
            c.Lines.Add(Beam(a, b)); c.Lines.Add(Beam(b, t)); c.MainLine = c.Lines[1]; Pin(a, c.Lines[0]); Pin(b, c.Lines[0]); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // C2 two spans 3 + 3 m, vibration 2025
        {
            int a = Node(0, 12, 0), b = Node(3, 12, 0), d = Node(6, 12, 0);
            Case c = new Case { Id = "C2", Title = "Two equal spans 3 + 3 m, UDL, 2025 vibration", Group = "Floor", Setup = Vib2025 };
            c.Lines.Add(Beam(a, b)); c.Lines.Add(Beam(b, d)); c.MainLine = c.Lines[0]; Pin(a, c.Lines[0]); Pin(b, c.Lines[0]); Pin(d, c.Lines[1]); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // C3 several load groups on a 4.5 m roof beam
        {
            int a = Node(0, 15, 0), b = Node(4.5, 15, 0);
            Case c = new Case { Id = "C3", Title = "4.5 m roof beam: G, Q, snow, exclusive wind ±", Group = "Roof",
                Setup = s => s.Roof.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[2]) };
            c.MainLine = Beam(a, b); c.Lines.Add(c.MainLine); Pin(a, c.MainLine); Pin(b, c.MainLine);
            Udl(gC, c.MainLine, -1.0); Udl(qC, c.MainLine, -1.5); Udl(sC, c.MainLine, -0.8); Udl(wpC, c.MainLine, -0.5); Udl(wmC, c.MainLine, 0.7);
            cases.Add(c);
        }
        // X1 inclined rafter 4 m run, 3 m rise (L = 5 m)
        {
            int a = Node(0, 20, 0), b = Node(4, 20, 3);
            Case c = new Case { Id = "X1", Title = "Inclined rafter, run 4 m / rise 3 m (L = 5 m)", Group = "Roof" };
            c.MainLine = Beam(a, b); c.Lines.Add(c.MainLine); Pin(a, c.MainLine); Pin(b, c.MainLine); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // X2 plan-diagonal two-span 4 + 2.5 m, direction (0.6, 0.8, 0)
        {
            Func<double, int> at = s => Node(8 + 0.6 * s, 0.8 * s, 0);
            int a = at(0), b = at(4), d = at(6.5);
            Case c = new Case { Id = "X2", Title = "Plan-diagonal unequal two-span 4 + 2.5 m, 2025 vibration", Group = "Floor", Setup = Vib2025 };
            c.Lines.Add(Beam(a, b)); c.Lines.Add(Beam(b, d)); c.MainLine = c.Lines[0]; Pin(a, c.Lines[0]); Pin(b, c.Lines[0]); Pin(d, c.Lines[1]); GQ(c.Lines, 1, 2);
            cases.Add(c);
        }
        // X3 3D-sloped rafter (30° pitch, 35° in plan) with 1.2 m eave overhang, 3 collinear lines
        {
            double al = 30 * Math.PI / 180, be = 35 * Math.PI / 180;
            double dx = Math.Cos(al) * Math.Cos(be), dy = Math.Cos(al) * Math.Sin(be), dz = Math.Sin(al);
            Func<double, int> at = s => Node(14 + dx * s, 20 + dy * s, dz * s);
            int tip = at(0), wall = at(1.2), mid = at(3.0), ridge = at(4.8);
            Case c = new Case { Id = "X3", Title = "3D-sloped rafter 30° with 1.2 m eave, 3 lines", Group = "Roof" };
            c.Lines.Add(Beam(tip, wall)); c.Lines.Add(Beam(wall, mid)); c.Lines.Add(Beam(mid, ridge)); c.MainLine = c.Lines[1];
            Pin(wall, c.Lines[1]); Pin(ridge, c.Lines[2]); GQ(c.Lines, 1, 1.5);
            cases.Add(c);
        }
        return cases;
    }

    private static void Vib2025(TimberSlsSettings s)
    {
        s.Vibration.Method = VibrationMethod.En1995_2025;
        s.Vibration.Spacing = 0.6; s.Vibration.Width = 4; s.Vibration.EiTransverse = 1500;
        s.Vibration.PermanentLoad = 0.5; s.Vibration.ImposedLoad = 1.75;
        s.Vibration.FloorType = FloorType.Joisted;
    }

    private static void SetPsi(int group, double p0, double p1, double p2)
    {
        IAxisVMLoadGroup g = m.LoadGroups.Item[group];
        g.Psi0 = p0; g.Psi1 = p1; g.Psi2 = p2;
    }
}
