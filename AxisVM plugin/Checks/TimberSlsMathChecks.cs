using System;
using System.Collections.Generic;
using System.Linq;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.Checks
{
    /// <summary>
    /// Checks of TimberSlsMath against the worked examples in docs/reference/ec5-sls-timber.md (§4, §7, §8).
    /// Each check prints PASS / FAIL; the exit code is the number of failures.
    /// </summary>
    internal static class Program
    {
        private static int failures;

        private static int Main()
        {
            BastionDeflection();
            CombinationFactors();
            ChordAndPrecamber();
            Generator();
            Ec5VibrationSheet();
            En2025VibrationSheet();
            Ke1Table();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures;
        }

        private static void Check(string name, double got, double want, double tol)
        {
            bool ok = Math.Abs(got - want) <= tol;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "  got " + TimberSlsMath.Sig(got) + ", want " + TimberSlsMath.Sig(want));
            if (!ok) failures++;
        }

        private static void CheckTrue(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failures++;
        }

        /// <summary>A one-point "span" carrying given G and Q deflections at midspan.</summary>
        private static SpanDeflections Point(double wG, double wQ)
        {
            SpanDeflections s = new SpanDeflections { Length = 2, X = new[] { 0.0, 1.0, 2.0 } };
            s.W[1] = new[] { 0, wG, 0 };
            s.W[2] = new[] { 0, wQ, 0 };
            return s;
        }

        private static SlsLoadCase G() { return new SlsLoadCase { Id = 1, Name = "G", Role = LoadRole.Permanent, Psi0 = 1, Psi1 = 1, Psi2 = 1 }; }
        private static SlsLoadCase Q(double psi0, double psi1, double psi2) { return new SlsLoadCase { Id = 2, Name = "Q", Role = LoadRole.Variable, Group = "Q", Psi0 = psi0, Psi1 = psi1, Psi2 = psi2 }; }

        private static double Max(SpanDeflections s, SlsCombination c, SlsQuantity q, double kdef, double wc = 0)
        {
            return TimberSlsMath.Evaluate(s, c, q, kdef, wc).Max(v => Math.Abs(v));
        }

        /// <summary>Doc §4: Bastion IX, kdef 2.0, psi2 0.4, wG 0.04, wQ 2.24 mm -> w_eind 4.15, w_bij 3.22 mm.</summary>
        private static void BastionDeflection()
        {
            SpanDeflections s = Point(0.04, 2.24);
            SlsCombination c = new SlsCombination { Name = "G+Q" };
            c.Terms.Add(TimberSlsMath.PermanentTerm(1, 1));
            c.Terms.Add(TimberSlsMath.VariableTerm(Q(0.4, 0.5, 0.4), 1, true));
            Check("Bastion w_eind", Max(s, c, SlsQuantity.WnetFin, 2.0), 4.152, 1e-9);
            Check("Bastion w_bij", Max(s, c, SlsQuantity.Wbij, 2.0), 3.216, 1e-9);
            Check("Bastion w_inst", Max(s, c, SlsQuantity.Winst, 2.0), 2.28, 1e-9);
            Check("Bastion w_creep = kdef(wG + psi2 wQ)", Max(s, c, SlsQuantity.Wcreep, 2.0), 2.0 * (0.04 + 0.4 * 2.24), 1e-9);
            // NA frequent variant: kdef(G + psi2 Q) + psi1 Q
            Check("Bastion w_bij NA frequent", Max(s, c, SlsQuantity.WbijFreq, 2.0), 2.0 * (0.04 + 0.4 * 2.24) + 0.5 * 2.24, 1e-9);
            // Conservative: (G + Q)(1 + kdef)
            var cases = new Dictionary<int, SlsLoadCase> { { 1, G() }, { 2, Q(0.4, 0.5, 0.4) } };
            SlsCombination cons = TimberSlsMath.ApplyCreepModel(c, CreepModel.Conservative, 1, -1, cases);
            Check("Conservative w_fin", Max(s, cons, SlsQuantity.Wfin, 2.0), (0.04 + 2.24) * 3.0, 1e-9);
            // Roof, psi2 = 0: w_bij = kdef·wG + wQ
            SlsCombination roof = new SlsCombination();
            roof.Terms.Add(TimberSlsMath.PermanentTerm(1, 1));
            roof.Terms.Add(TimberSlsMath.VariableTerm(Q(0, 0, 0), 1, true));
            Check("Roof w_bij = kdef·G + Q", Max(s, roof, SlsQuantity.Wbij, 0.8), 0.8 * 0.04 + 2.24, 1e-9);
        }

        /// <summary>EC5 eq. 2.5 accompanying action and a quasi-permanent AxisVM combination.</summary>
        private static void CombinationFactors()
        {
            SpanDeflections s = Point(1.0, 2.0);
            SlsLoadCase q = Q(0.4, 0.5, 0.3);
            SlsCombination acc = new SlsCombination();
            acc.Terms.Add(TimberSlsMath.PermanentTerm(1, 1));
            acc.Terms.Add(TimberSlsMath.VariableTerm(q, q.Psi0, false));
            Check("Accompanying w_fin = G(1+k) + Q(psi0 + psi2 k)", Max(s, acc, SlsQuantity.Wfin, 0.6), 1.6 + 2.0 * (0.4 + 0.3 * 0.6), 1e-9);

            var cases = new Dictionary<int, SlsLoadCase> { { 1, G() }, { 2, q } };
            SlsCombination qp = TimberSlsMath.FromFactors("QP", new[] { 1, 2 }, new[] { 1.0, 0.3 }, cases);
            Check("Quasi-permanent w_fin = (G + psi2 Q)(1+k)", Max(s, qp, SlsQuantity.Wfin, 0.6), (1 + 0.3 * 2) * 1.6, 1e-9);
            SlsCombination ch = TimberSlsMath.FromFactors("Char", new[] { 1, 2 }, new[] { 1.0, 1.0 }, cases);
            Check("Characteristic from factors = EC5 leading", Max(s, ch, SlsQuantity.Wfin, 0.6), 1.6 + 2.0 * (1 + 0.3 * 0.6), 1e-9);
        }

        private static void ChordAndPrecamber()
        {
            double[] x = { 0, 1, 2, 3, 4 };
            double[] w = { 2, 1, 0, 1, 6 };            // ends at 2 and 6
            double[] r = TimberSlsMath.RelativeToChord(x, w);
            Check("Chord removed at start", r[0], 0, 1e-12);
            Check("Chord removed at end", r[4], 0, 1e-12);
            Check("Chord midspan", r[2], 0 - 4, 1e-12);
            Check("Precamber peak at midspan", TimberSlsMath.Precamber(2, 4, false, 10), 10, 1e-12);
            Check("Precamber cantilever tip", TimberSlsMath.Precamber(4, 4, true, 10), 10, 1e-12);

            SpanDeflections s = new SpanDeflections { Length = 4, X = x };
            s.W[1] = new[] { 0, 7.5, 10, 7.5, 0 };
            SlsCombination c = new SlsCombination();
            c.Terms.Add(TimberSlsMath.PermanentTerm(1, 1));
            Check("w_net,fin with precamber 5 mm", Max(s, c, SlsQuantity.WnetFin, 0, 5), 5, 1e-9);
            Check("w_bij ignores precamber", Max(s, c, SlsQuantity.Wbij, 0.6, 5), 6, 1e-9);
            Check("l/250 limit on 5 m", new LimitRow(SlsQuantity.WnetFin, 250, 0).LimitMm(5), 20, 1e-12);
            Check("Both limits: smaller wins", new LimitRow(SlsQuantity.Wbij, 250, 15).LimitMm(5), 15, 1e-12);
        }

        private static void Generator()
        {
            List<SlsLoadCase> cases = new List<SlsLoadCase>
            {
                new SlsLoadCase { Id = 1, Name = "G", Role = LoadRole.Permanent },
                new SlsLoadCase { Id = 2, Name = "Q1", Role = LoadRole.Variable, Group = "Floor", Psi0 = 0.4, Psi1 = 0.5, Psi2 = 0.3 },
                new SlsLoadCase { Id = 3, Name = "S", Role = LoadRole.Variable, Group = "Snow", Psi0 = 0, Psi1 = 0.2, Psi2 = 0 },
                new SlsLoadCase { Id = 4, Name = "W+", Role = LoadRole.Variable, Group = "Wind", Exclusive = true, Psi0 = 0.6, Psi1 = 0.2, Psi2 = 0 },
                new SlsLoadCase { Id = 5, Name = "W-", Role = LoadRole.Variable, Group = "Wind", Exclusive = true, Psi0 = 0.6, Psi1 = 0.2, Psi2 = 0 },
            };
            string note;
            List<SlsCombination> combos = TimberSlsMath.Generate(cases, 1000, out note);
            // Floor lead: wind absent/W+/W- (snow skipped, psi0 = psi2 = 0) = 3
            // Snow lead: floor absent/present x wind absent/W+/W- = 6
            // Wind lead (2 options): floor absent/present = 2 each = 4
            Check("Generated combination count", combos.Count, 13, 0);
            CheckTrue("Never both wind cases together", combos.All(c => !(c.Terms.Any(t => t.CaseId == 4) && c.Terms.Any(t => t.CaseId == 5))));
            CheckTrue("Permanent in every combination", combos.All(c => c.Terms.Any(t => t.CaseId == 1 && t.Inst == 1)));
            CheckTrue("No reduction note", note == null);
        }

        /// <summary>Doc §7 worked check (the user's sheet).</summary>
        private static void Ec5VibrationSheet()
        {
            TimberSlsMath.Ec5VibInput i = new TimberSlsMath.Ec5VibInput
            {
                Length = 6.527, Width = 4.0, Spacing = 0.6, EiJoist = 2928750 * 0.6, EiB = 1458,
                Mass = (0.261 + 0.850) * 1000 / 9.81, Zeta = 0.01, KAmp = 1.05, BMode = 2, BCustom = 87.6
            };
            TimberSlsMath.Ec5VibResult r = TimberSlsMath.Ec5Vibration(i);
            Check("EC5 m", i.Mass, 113.3, 0.05);
            Check("EC5 f1", r.F1, 5.93, 0.005);
            Check("EC5 k_dist", r.KDist, 0.528, 0.0005);
            Check("EC5 a", r.A, 1.827, 0.0005);
            Check("EC5 a_lim UK", r.ALim, 1.050, 0.0005);
            Check("EC5 n40", r.N40, 10.60, 0.005);
            Check("EC5 v", r.V, 0.00856, 0.000005);
            Check("EC5 v_lim", r.VLim, 0.01488, 0.000005);
            CheckTrue("EC5 f1 ≤ 8 Hz flagged", !r.F1Above8);
        }

        /// <summary>Doc §8.7 worked check.</summary>
        private static void En2025VibrationSheet()
        {
            TimberSlsMath.En2025Input i = new TimberSlsMath.En2025Input
            {
                Span = 6.527, Width = 4.0, EiL = 2928750, EiT = 1458, Mass = 113.3, Zeta = 0.02, Fw = 1.5,
                MultiFamily = false, Joisted = true, TargetLevel = 2
            };
            TimberSlsMath.En2025Result r = TimberSlsMath.En2025Vibration(i);
            Check("2025 f1", r.F1, 5.93, 0.005);
            Check("2025 f1,lim level III", r.F1Lim[2], 6.0, 1e-9);
            CheckTrue("2025 resonant at level III", r.Resonant[2]);
            Check("2025 b_ef", r.Bef, 0.926, 0.0005);
            Check("2025 w_1kN", r.W1kN, 2.136, 0.0005);
            Check("2025 I_mod,mean", r.IMod, 7.42, 0.005);
            Check("2025 M*", r.MStar, 739.5, 0.05);
            Check("2025 v1,peak", r.V1Peak, 0.00641, 0.000005);
            Check("2025 k_imp", r.KImp, 1.969, 0.0005);
            Check("2025 eta", r.Eta, 0.59, 1e-9);
            Check("2025 v_rms", r.VRms, 0.00440, 0.000005);
            Check("2025 k_res", r.KRes, 1.0, 1e-9);
            Check("2025 a_rms", r.ARms, 0.4781, 0.00005);
            Check("2025 achieved level (fails VIII)", r.AchievedLevel, -1, 0);
            Check("2025 w_lim level IV, l = 6.527", i.Table.WLim(3, 6.527), 0.5516, 0.0001);
            Check("2025 w_lim level III", i.Table.WLim(2, 6.527), 0.5, 1e-12);
        }

        private static void Ke1Table()
        {
            Check("k_e,1 at 1.0", TimberSlsMath.Ke1(1.0), 1.00, 1e-9);
            Check("k_e,1 at 0.5", TimberSlsMath.Ke1(0.5), 1.28, 1e-9);
            Check("k_e,1 at 0.55 (interpolated)", TimberSlsMath.Ke1(0.55), 1.265, 1e-9);
            Check("k_e,1 at 0.2", TimberSlsMath.Ke1(0.2), 1.41, 1e-9);
            Check("k_def joint (2.13) 0.6/0.8", TimberSlsMath.KdefJoint(0.6, 0.8), 2 * Math.Sqrt(0.48), 1e-12);
        }
    }
}
