using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/*
 * =====================================================================================
 * TIMBER SLS MATH - deflection (EN 1995-1-1:2004 + NL NA / Harff) and floor vibration
 * (EN 1995-1-1:2004 §7.3 and EN 1995-1-1:2025 §9.3)
 * =====================================================================================
 *
 * Pure math, no COM and no WinForms, so Checks/TimberSlsMathChecks can test exactly what the add-on ships.
 * The specification is docs/reference/ec5-sls-timber.md; every formula below cites its section there.
 *
 * Deflection model: AxisVM gives the instantaneous relative deflection w_i(x) of every load case (E_mean). A
 * combination is a list of terms, one per load case, each with four multipliers:
 *   Inst  - share in the instantaneous (characteristic) deflection
 *   Creep - share that creeps (times kdef)
 *   W1    - share in w1, the elastic deflection under G + psi2*Q (NL NA / Harff)
 *   Freq  - variable share of the frequent combination (NA "frequent" w_bij variant)
 * Every check quantity is a linear sum of the w_i with these multipliers (doc §4).
 * =====================================================================================
 */

namespace AxisVM_plugin.Core
{
    public enum LoadRole { Permanent, Variable, Ignore }

    /// <summary>Deflection quantities that a limit row can check (doc §2, §4).</summary>
    public enum SlsQuantity
    {
        Winst,      // instantaneous, characteristic
        Wcreep,     // creep part
        Wfin,       // final = inst + creep
        WnetFin,    // final minus precamber (= w_eind, NL)
        Wbij,       // additional deflection w2 + w3 (Harff, quasi-permanent)
        WbijFreq,   // additional deflection, NA frequent reading (w1 = G)
        WqpFin      // quasi-permanent final minus precamber (w_max, appearance)
    }

    public enum CreepModel { Ec5, Conservative, Custom }

    /// <summary>One load case as the SLS check sees it.</summary>
    public class SlsLoadCase
    {
        public int Id;
        public string Name;
        public LoadRole Role;
        public string Group;          // variable cases of one group are additive or exclusive
        public bool Exclusive;
        public double Psi0, Psi1, Psi2;
        public bool Imposed;          // counted in the 2025 floor mass (10 %)
    }

    /// <summary>One load case inside a combination.</summary>
    public class SlsTerm
    {
        public int CaseId;
        public double Inst, Creep, W1, Freq;
    }

    public class SlsCombination
    {
        public string Name;
        public List<SlsTerm> Terms = new List<SlsTerm>();
    }

    /// <summary>One limit row of a group (doc §5.2): relative L/x and / or absolute mm; 0 = not used.</summary>
    public class LimitRow
    {
        public bool Enabled = true;
        public SlsQuantity Quantity;
        public double Ratio;   // l_rep / Ratio
        public double AbsMm;   // absolute limit in mm

        public LimitRow() { }
        public LimitRow(SlsQuantity q, double ratio, double absMm) { Quantity = q; Ratio = ratio; AbsMm = absMm; }

        /// <summary>Governing limit in mm for this l_rep (m): the smaller of the two that are set.</summary>
        public double LimitMm(double lRepM)
        {
            double lim = double.PositiveInfinity;
            if (Ratio > 0) lim = Math.Min(lim, lRepM * 1000.0 / Ratio);
            if (AbsMm > 0) lim = Math.Min(lim, AbsMm);
            return lim;
        }

        public string Describe()
        {
            List<string> p = new List<string>();
            if (Ratio > 0) p.Add("l/" + TimberSlsMath.Num(Ratio, 0));
            if (AbsMm > 0) p.Add(TimberSlsMath.Num(AbsMm, 1) + " mm");
            return TimberSlsMath.QuantityName(Quantity) + " ≤ " + (p.Count == 0 ? "-" : string.Join(" and ", p));
        }
    }

    /// <summary>Relative deflection lines of one span, per load case, in mm, x from the root / start in m.</summary>
    public class SpanDeflections
    {
        public double Length;
        public bool Cantilever;
        public double[] X;
        public Dictionary<int, double[]> W = new Dictionary<int, double[]>();
    }

    /// <summary>The result of one limit row on one span for one combination.</summary>
    public class RowResult
    {
        public LimitRow Row;
        public string Combination;
        public double W;        // mm, governing |value|
        public double X;        // m, where
        public double Limit;    // mm
        public double Uc;
    }

    public static class TimberSlsMath
    {
        // ------------------------------------------------------------------
        // Presets (doc §3, §5)
        // ------------------------------------------------------------------

        /// <summary>kdef, Table 3.2, for solid timber / glulam / LVL by service class 1..3.</summary>
        public static double KdefSolid(int serviceClass)
        {
            switch (serviceClass) { case 1: return 0.60; case 2: return 0.80; default: return 2.00; }
        }

        /// <summary>Mixed materials, formula (2.13): kdef = 2·sqrt(kdef1·kdef2).</summary>
        public static double KdefJoint(double k1, double k2) { return 2.0 * Math.Sqrt(k1 * k2); }

        public static string QuantityName(SlsQuantity q)
        {
            switch (q)
            {
                case SlsQuantity.Winst: return "w_inst";
                case SlsQuantity.Wcreep: return "w_creep";
                case SlsQuantity.Wfin: return "w_fin";
                case SlsQuantity.WnetFin: return "w_net,fin (w_eind)";
                case SlsQuantity.Wbij: return "w_bij (Harff)";
                case SlsQuantity.WbijFreq: return "w_bij (NA frequent)";
                default: return "w_max (quasi-perm.)";
            }
        }

        // ------------------------------------------------------------------
        // Combinations (doc §6)
        // ------------------------------------------------------------------

        /// <summary>A permanent case: everything acts and creeps fully.</summary>
        public static SlsTerm PermanentTerm(int caseId, double factor)
        {
            return new SlsTerm { CaseId = caseId, Inst = factor, Creep = factor, W1 = factor, Freq = 0 };
        }

        /// <summary>
        /// A variable case with combination factor <paramref name="factor"/> (1 = leading, psi0 = accompanying,
        /// psi2 = quasi-permanent). Creep and w1 use psi2 (2.2.3(5), eq. 2.4 / 2.5); the frequent share is psi1
        /// for the leading action and psi2 for the others.
        /// </summary>
        public static SlsTerm VariableTerm(SlsLoadCase c, double factor, bool leading)
        {
            double sign = factor < 0 ? -1 : factor > 0 ? 1 : 0;
            return new SlsTerm
            {
                CaseId = c.Id,
                Inst = factor,
                Creep = sign * c.Psi2,
                W1 = sign * c.Psi2,
                Freq = sign * (leading ? c.Psi1 : c.Psi2)
            };
        }

        /// <summary>Applies the creep model to a combination built per EC5 (doc §4 A / C / D).</summary>
        public static SlsCombination ApplyCreepModel(SlsCombination c, CreepModel model, double creepG, double creepQ,
            IDictionary<int, SlsLoadCase> cases)
        {
            if (model == CreepModel.Ec5) return c;
            SlsCombination r = new SlsCombination { Name = c.Name };
            foreach (SlsTerm t in c.Terms)
            {
                SlsTerm n = new SlsTerm { CaseId = t.CaseId, Inst = t.Inst, Creep = t.Creep, W1 = t.W1, Freq = t.Freq };
                SlsLoadCase lc;
                bool permanent = cases.TryGetValue(t.CaseId, out lc) && lc.Role == LoadRole.Permanent;
                if (model == CreepModel.Conservative) n.Creep = t.Inst;
                else
                {
                    double sign = t.Inst < 0 ? -1 : t.Inst > 0 ? 1 : 0;
                    if (permanent) n.Creep = t.Inst * creepG;
                    else if (creepQ >= 0) n.Creep = sign * creepQ;
                }
                r.Terms.Add(n);
            }
            return r;
        }

        /// <summary>
        /// All SLS combinations the way AxisVM's auto-combination builds them (doc §6, source "Auto"): permanent
        /// cases always; each variable group leading in turn (exclusive groups one case at a time, additive groups
        /// all cases together), every other group absent or present with psi0. Groups with psi0 = psi2 = 0 add
        /// nothing as accompanying actions and are skipped. When the product exceeds <paramref name="cap"/>, the
        /// accompanying groups are taken all present or all absent only, and <paramref name="note"/> says so.
        /// </summary>
        public static List<SlsCombination> Generate(IList<SlsLoadCase> cases, int cap, out string note)
        {
            note = null;
            List<SlsLoadCase> perm = cases.Where(c => c.Role == LoadRole.Permanent).ToList();
            var groups = cases.Where(c => c.Role == LoadRole.Variable)
                .GroupBy(c => string.IsNullOrEmpty(c.Group) ? "#" + c.Id : c.Group)
                .Select(g => new
                {
                    Name = g.Key,
                    Options = g.First().Exclusive ? g.Select(c => new List<SlsLoadCase> { c }).ToList()
                                                  : new List<List<SlsLoadCase>> { g.ToList() }
                }).ToList();

            List<SlsCombination> result = new List<SlsCombination>();
            Func<SlsCombination> start = () =>
            {
                SlsCombination c = new SlsCombination();
                foreach (SlsLoadCase p in perm) c.Terms.Add(PermanentTerm(p.Id, 1.0));
                return c;
            };
            string gName = perm.Count > 0 ? "G" : "";

            if (groups.Count == 0)
            {
                SlsCombination c = start();
                c.Name = gName.Length > 0 ? gName : "(empty)";
                result.Add(c);
                return result;
            }

            for (int li = 0; li < groups.Count; li++)
            {
                var lead = groups[li];
                var others = groups.Where((g, i) => i != li &&
                    g.Options.Any(o => o.Any(c => c.Psi0 != 0 || c.Psi2 != 0))).ToList();

                // Product of (absent + options) over the accompanying groups
                double product = 1;
                foreach (var o in others) product *= o.Options.Count + 1;
                bool reduced = product * lead.Options.Count > cap;
                if (reduced) note = "Many load groups: accompanying actions taken all present or all absent only.";

                foreach (List<SlsLoadCase> leadOpt in lead.Options)
                {
                    List<List<List<SlsLoadCase>>> choices = new List<List<List<SlsLoadCase>>>();
                    if (reduced)
                    {
                        choices.Add(new List<List<SlsLoadCase>>());
                        choices.Add(others.Select(o => o.Options[0]).ToList());
                    }
                    else
                    {
                        choices.Add(new List<List<SlsLoadCase>>());
                        foreach (var o in others)
                        {
                            List<List<List<SlsLoadCase>>> next = new List<List<List<SlsLoadCase>>>();
                            foreach (var ch in choices)
                            {
                                next.Add(ch);
                                foreach (var opt in o.Options) next.Add(new List<List<SlsLoadCase>>(ch) { opt });
                            }
                            choices = next;
                        }
                    }

                    foreach (var ch in choices)
                    {
                        SlsCombination c = start();
                        List<string> name = new List<string>();
                        if (gName.Length > 0) name.Add(gName);
                        foreach (SlsLoadCase q in leadOpt) c.Terms.Add(VariableTerm(q, 1.0, true));
                        name.Add(string.Join("+", leadOpt.Select(q => q.Name)) + " (lead)");
                        foreach (var opt in ch)
                            foreach (SlsLoadCase q in opt)
                            {
                                c.Terms.Add(VariableTerm(q, q.Psi0, false));
                                name.Add(Num(q.Psi0, 2) + "·" + q.Name);
                            }
                        c.Name = string.Join(" + ", name);
                        result.Add(c);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// An existing AxisVM combination (factors per load case) as SLS terms: permanent cases creep fully,
        /// variable cases with psi2 of their group; a factor of 1 marks the leading action (doc §6, source 2).
        /// </summary>
        public static SlsCombination FromFactors(string name, IList<int> caseIds, IList<double> factors, IDictionary<int, SlsLoadCase> cases)
        {
            SlsCombination c = new SlsCombination { Name = name };
            for (int i = 0; i < caseIds.Count; i++)
            {
                double f = factors[i];
                SlsLoadCase lc;
                if (f == 0 || !cases.TryGetValue(caseIds[i], out lc) || lc.Role == LoadRole.Ignore) continue;
                if (lc.Role == LoadRole.Permanent) c.Terms.Add(PermanentTerm(lc.Id, f));
                else c.Terms.Add(VariableTerm(lc, f, Math.Abs(f) >= 0.999));
            }
            return c;
        }

        // ------------------------------------------------------------------
        // Deflection (doc §4, §5)
        // ------------------------------------------------------------------

        /// <summary>Precamber shape: parabola on a span, (x/L)² from the root on a cantilever; peak = wc.</summary>
        public static double Precamber(double x, double length, bool cantilever, double wcMm)
        {
            if (wcMm == 0 || length <= 0) return 0;
            double t = Math.Max(0, Math.Min(1, x / length));
            return cantilever ? wcMm * t * t : wcMm * 4 * t * (1 - t);
        }

        /// <summary>
        /// The quantity along the span for one combination, in mm. Signs are normalised so that the
        /// instantaneous deflection is positive where it is largest; the precamber then works against it.
        /// </summary>
        public static double[] Evaluate(SpanDeflections s, SlsCombination c, SlsQuantity q, double kdef, double wcMm)
        {
            int n = s.X.Length;
            double[] inst = new double[n], creep = new double[n], w1 = new double[n], freq = new double[n];
            foreach (SlsTerm t in c.Terms)
            {
                double[] w;
                if (!s.W.TryGetValue(t.CaseId, out w)) continue;
                for (int i = 0; i < n; i++)
                {
                    inst[i] += t.Inst * w[i];
                    creep[i] += kdef * t.Creep * w[i];
                    w1[i] += t.W1 * w[i];
                    freq[i] += t.Freq * w[i];
                }
            }
            double sign = Sign(inst, creep);
            double[] r = new double[n];
            for (int i = 0; i < n; i++)
            {
                double fin = inst[i] + creep[i];
                double wc = Precamber(s.X[i], s.Length, s.Cantilever, wcMm);
                double v;
                switch (q)
                {
                    case SlsQuantity.Winst: v = sign * inst[i]; break;
                    case SlsQuantity.Wcreep: v = sign * creep[i]; break;
                    case SlsQuantity.Wfin: v = sign * fin; break;
                    case SlsQuantity.WnetFin: v = sign * fin - wc; break;
                    case SlsQuantity.Wbij: v = sign * (fin - w1[i]); break;
                    case SlsQuantity.WbijFreq: v = sign * (kdef * w1[i] + freq[i]); break;
                    default: v = sign * (1 + kdef) * w1[i] - wc; break;
                }
                r[i] = v;
            }
            return r;
        }

        /// <summary>+1 / -1 so that the largest |inst + creep| comes out positive.</summary>
        private static double Sign(double[] inst, double[] creep)
        {
            double best = 0;
            for (int i = 0; i < inst.Length; i++)
            {
                double f = inst[i] + creep[i];
                if (Math.Abs(f) > Math.Abs(best)) best = f;
            }
            return best < 0 ? -1 : 1;
        }

        /// <summary>Index of the largest |value|.</summary>
        public static int ArgMaxAbs(double[] v)
        {
            int k = 0;
            for (int i = 1; i < v.Length; i++) if (Math.Abs(v[i]) > Math.Abs(v[k])) k = i;
            return k;
        }

        /// <summary>Checks one row for every combination and returns the governing one.</summary>
        public static RowResult CheckRow(SpanDeflections s, IList<SlsCombination> combos, LimitRow row, double kdef, double wcMm, double lRepM)
        {
            RowResult best = null;
            double limit = row.LimitMm(lRepM);
            foreach (SlsCombination c in combos)
            {
                double[] v = Evaluate(s, c, row.Quantity, kdef, wcMm);
                int k = ArgMaxAbs(v);
                double w = Math.Abs(v[k]);
                double uc = double.IsInfinity(limit) ? 0 : w / limit;
                if (best == null || uc > best.Uc || (uc == best.Uc && w > best.W))
                    best = new RowResult { Row = row, Combination = c.Name, W = w, X = s.X[k], Limit = limit, Uc = uc };
            }
            return best;
        }

        /// <summary>Relative deflection: the chord between the two span ends removed (doc §5.1).</summary>
        public static double[] RelativeToChord(double[] x, double[] w)
        {
            int n = x.Length;
            double[] r = new double[n];
            if (n == 0) return r;
            double x0 = x[0], x1 = x[n - 1], w0 = w[0], w1 = w[n - 1];
            double len = x1 - x0;
            for (int i = 0; i < n; i++)
                r[i] = w[i] - (len > 0 ? w0 + (w1 - w0) * (x[i] - x0) / len : w0);
            return r;
        }

        /// <summary>
        /// Relative deflection of a cantilever, x from the root: minus the root displacement, and optionally minus
        /// the root rotation (tangent reference) given as slope dw/dx in mm/m.
        /// </summary>
        public static double[] RelativeToRoot(double[] x, double[] w, double rootSlope)
        {
            double[] r = new double[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = w[i] - w[0] - rootSlope * (x[i] - x[0]);
            return r;
        }

        // ------------------------------------------------------------------
        // Vibration EN 1995-1-1:2004 §7.3 (doc §7)
        // ------------------------------------------------------------------

        public enum Boundary { PinnedPinned, PinnedFixed, FixedFixed }

        public static double Lambda(Boundary b)
        {
            switch (b) { case Boundary.PinnedFixed: return 3.9266; case Boundary.FixedFixed: return 4.7300; default: return Math.PI; }
        }

        /// <summary>Midspan point load coefficient c in w = c·F·L³/EI.</summary>
        public static double PointLoadCoefficient(Boundary b)
        {
            switch (b) { case Boundary.PinnedFixed: return 7.0 / 768.0; case Boundary.FixedFixed: return 1.0 / 192.0; default: return 1.0 / 48.0; }
        }

        /// <summary>f1 = λ²/(2π·L²)·sqrt(EI/m), generalised (7.5). EI in Nm²/m, m in kg/m².</summary>
        public static double F1Beam(double lambda, double lengthM, double eiPerM, double massKgM2)
        {
            if (lengthM <= 0 || massKgM2 <= 0 || eiPerM <= 0) return 0;
            return lambda * lambda / (2 * Math.PI * lengthM * lengthM) * Math.Sqrt(eiPerM / massKgM2);
        }

        /// <summary>k_dist = max{0.38 - 0.08·ln[14·(EI)_b/s⁴]; 0.30}, (EI)_b in N·mm²/m, s in mm (UK NA form).</summary>
        public static double KDist(double eiBNm2PerM, double spacingM)
        {
            double eiB = eiBNm2PerM * 1e6;          // Nm²/m -> N·mm²/m
            double s = spacingM * 1000.0;           // m -> mm
            if (eiB <= 0 || s <= 0) return 1.0;
            return Math.Max(0.38 - 0.08 * Math.Log(14.0 * eiB / Math.Pow(s, 4)), 0.30);
        }

        /// <summary>UK NA a-limit in mm/kN: 1.8 for L ≤ 4 m, 16500/L^1.1 (L in mm) above.</summary>
        public static double ALimUk(double lengthM)
        {
            double l = lengthM * 1000.0;
            return l <= 4000 ? 1.8 : 16500.0 / Math.Pow(l, 1.1);
        }

        /// <summary>UK NA relation between a [mm/kN] and b: 180 - 60a (a ≤ 1), 160 - 40a (a > 1).</summary>
        public static double BFromA(double a) { return a <= 1 ? 180 - 60 * a : 160 - 40 * a; }

        /// <summary>n40 (7.7); 0 when f1 ≥ 40 Hz.</summary>
        public static double N40(double f1, double widthM, double lengthM, double eiL, double eiB)
        {
            if (f1 <= 0 || eiB <= 0 || lengthM <= 0) return 0;
            double t = (Math.Pow(40.0 / f1, 2) - 1) * Math.Pow(widthM / lengthM, 4) * eiL / eiB;
            return t <= 0 ? 0 : Math.Pow(t, 0.25);
        }

        /// <summary>Unit impulse velocity response (7.6), m/(Ns²).</summary>
        public static double UnitImpulseVelocity(double n40, double massKgM2, double widthM, double lengthM)
        {
            return 4 * (0.4 + 0.6 * n40) / (massKgM2 * widthM * lengthM + 200);
        }

        public class Ec5VibInput
        {
            public double Length, Width, Spacing;    // m
            public double EiJoist;                   // Nm² (one joist, E_mean·I)
            public double EiDeckL;                   // Nm²/m extra along the span (deck), may be 0
            public double EiB;                       // Nm²/m crosswise
            public double Mass;                      // kg/m²
            public double Zeta = 0.01;
            public Boundary Boundary = Boundary.PinnedPinned;
            public double KDistCustom = double.NaN;  // NaN = auto
            public double KAmp = 1.05;
            public double ALimCustom = double.NaN;   // NaN = UK NA
            public int BMode;                        // 0 = from a (calc), 1 = from a_lim, 2 = custom
            public double BCustom = 100;
            public double F1Given = double.NaN;      // NaN = formula
        }

        public class Ec5VibResult
        {
            public double EiL, F1, KDist, A, ALim, N40, V, B, VLim;
            public double UcA, UcV, Uc;
            public bool F1Above8;
            public List<string> Lines = new List<string>();
        }

        /// <summary>The full §7.3.3 check (doc §7).</summary>
        public static Ec5VibResult Ec5Vibration(Ec5VibInput i)
        {
            Ec5VibResult r = new Ec5VibResult();
            r.EiL = (i.Spacing > 0 ? i.EiJoist / i.Spacing : 0) + i.EiDeckL;
            double lambda = Lambda(i.Boundary);
            r.F1 = double.IsNaN(i.F1Given) ? F1Beam(lambda, i.Length, r.EiL, i.Mass) : i.F1Given;
            r.F1Above8 = r.F1 > 8;
            r.KDist = double.IsNaN(i.KDistCustom) ? KDist(i.EiB, i.Spacing) : i.KDistCustom;
            double c = PointLoadCoefficient(i.Boundary);
            r.A = i.EiJoist > 0 ? c * 1000.0 * Math.Pow(i.Length, 3) / i.EiJoist * r.KDist * i.KAmp * 1000.0 : 0;  // mm/kN
            r.ALim = double.IsNaN(i.ALimCustom) ? ALimUk(i.Length) : i.ALimCustom;
            r.N40 = N40(r.F1, i.Width, i.Length, r.EiL, i.EiB);
            r.V = UnitImpulseVelocity(r.N40, i.Mass, i.Width, i.Length);
            r.B = i.BMode == 2 ? i.BCustom : BFromA(i.BMode == 1 ? r.ALim : r.A);
            r.VLim = r.B > 0 ? Math.Pow(r.B, r.F1 * i.Zeta - 1) : 0;
            r.UcA = r.ALim > 0 ? r.A / r.ALim : 0;
            r.UcV = r.VLim > 0 ? r.V / r.VLim : 0;
            r.Uc = Math.Max(r.UcA, r.UcV);

            r.Lines.Add("EN 1995-1-1:2004 §7.3.3");
            r.Lines.Add(L("(EI)_L = EI_joist/s + deck", r.EiL, "Nm²/m"));
            r.Lines.Add(L("m", i.Mass, "kg/m²") + "   ζ = " + Num(i.Zeta, 3));
            r.Lines.Add(L("f1 = λ²/(2πL²)·√((EI)_L/m), λ = " + Num(lambda, 4), r.F1, "Hz") +
                (double.IsNaN(i.F1Given) ? "" : " (given)") + (r.F1Above8 ? "  > 8 Hz" : "  ≤ 8 Hz: special investigation (7.3.3(1))"));
            r.Lines.Add(L("k_dist" + (double.IsNaN(i.KDistCustom) ? " = max{0.38 − 0.08·ln[14(EI)_b/s⁴]; 0.30}" : " (custom)"), r.KDist, ""));
            r.Lines.Add(L("k_amp", i.KAmp, ""));
            r.Lines.Add(L("a = c·F·L³/EI_joist·k_dist·k_amp, c = " + Num(c, 5), r.A, "mm/kN") + "   a_lim = " + Num(r.ALim, 3) +
                (double.IsNaN(i.ALimCustom) ? " (UK NA)" : " (custom)") + "   UC = " + Num(r.UcA, 2));
            r.Lines.Add(L("n40 (7.7)", r.N40, ""));
            r.Lines.Add(L("v = 4(0.4+0.6·n40)/(m·B·L+200) (7.6)", r.V, "m/(Ns²)"));
            r.Lines.Add(L("b" + (i.BMode == 2 ? " (custom)" : i.BMode == 1 ? " from a_lim" : " from a"), r.B, "") +
                "   v_lim = b^(f1·ζ−1) = " + Num(r.VLim, 5) + "   UC = " + Num(r.UcV, 2));
            return r;
        }

        // ------------------------------------------------------------------
        // Vibration EN 1995-1-1:2025 §9.3 (doc §8)
        // ------------------------------------------------------------------

        public static readonly string[] Levels = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII" };

        /// <summary>Table 9.1 defaults; every array is editable through the settings.</summary>
        public class Table91
        {
            public double[] WLimMax = { 0.25, 0.25, 0.5, 1.0, 1.25, 1.5, 1.75, 2.0 };
            public double[] VRmsLim = { 0.0004, 0.0008, 0.0012, 0.0016, 0.0024, 0.0036, 0.0042, 0.0048 };
            public double[] ARmsLim = { 0.02, 0.04, 0.06, 0.08, double.NaN, double.NaN, double.NaN, double.NaN };
            public double F1Min = 4.5;          // levels I-IV
            public int LastFixedFrequency = 3;  // index of the last level with f1 ≥ 4.5 Hz (IV)
            public int LastEqualWLim = 2;       // index of the last level with w_lim = w_lim,max (III)

            public double WLim(int level, double spanM)
            {
                double max = WLimMax[level];
                if (level <= LastEqualWLim || spanM <= 0) return max;
                return Math.Min(Math.Max(max * 3.6 / spanM, 0.5), max);
            }
        }

        /// <summary>k_e,1 for a double span floor, Table 9.4 by linear interpolation; x = l2/l.</summary>
        public static double Ke1(double x)
        {
            double[] xs = { 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };
            double[] ks = { 1.41, 1.36, 1.32, 1.28, 1.25, 1.21, 1.16, 1.09, 1.00 };
            if (x <= 0) return 1.0;
            if (x <= xs[0]) return ks[0];
            if (x >= 1) return 1.0;
            for (int i = 0; i < xs.Length - 1; i++)
                if (x <= xs[i + 1]) return ks[i] + (ks[i + 1] - ks[i]) * (x - xs[i]) / (xs[i + 1] - xs[i]);
            return 1.0;
        }

        public class En2025Input
        {
            public double Span, Width;            // m (longer span for a double span floor)
            public double SecondSpan;             // m, 0 = single span
            public double EiL, EiT;               // Nm²/m
            public double Mass;                   // kg/m² (9.3.1.2, incl. 10 % imposed)
            public double Zeta = 0.02;
            public double Fw = 1.5;
            public bool MultiFamily = true;
            public bool Joisted = true;           // η in (9.27)
            public bool FourEdges;                // k_e,2 from (9.14), else 1.0
            public double F1Given = double.NaN;   // NaN = (9.13) / (9.15)
            public double WSysMm = double.NaN;    // (9.15) when set
            public double F1Beam1, F1Beam2;       // Hz, 0 = rigid support (9.16)
            public double EiStiffener;            // Nm², 0 = none (9.19)
            public double WBeam1, WBeam2;         // mm under 0.5 kN (9.20)
            public double KRed = 0.7;
            public double MuRes = 0.4;
            public double FDyn = 50;
            public double ModalMassDivisor = 4;   // (9.23): 4, or 2 for two edges without transverse connection
            public int TargetLevel = 3;           // index into Levels (IV)
            public Table91 Table = new Table91();
        }

        public class En2025Result
        {
            public double Ke1, Ke2, F1Rigid, F1, Bef, WRigid, W1kN, IMod, MStar, V1Peak, KImp, Eta, VTotPeak, VRms, KRes, ARms;
            public double[] F1Lim = new double[8];
            public bool[] Resonant = new bool[8];
            public bool[] Pass = new bool[8];
            public int AchievedLevel = -1;        // -1 = none
            public double Uc;                     // against the target level
            public string Governing;
            public List<string> Lines = new List<string>();
        }

        /// <summary>The full §9.3.2 check: frequency, stiffness, velocity, acceleration, every level (doc §8).</summary>
        public static En2025Result En2025Vibration(En2025Input i)
        {
            En2025Result r = new En2025Result();
            double l = i.Span, b = i.Width;
            double ratio = i.EiT > 0 && i.EiL > 0 ? Math.Pow(i.EiL / i.EiT, 0.25) : 0;   // ((EI)_L/(EI)_T)^0.25

            // Fundamental frequency (9.13) - (9.16)
            r.Ke1 = i.SecondSpan > 0 && l > 0 ? Ke1(Math.Min(i.SecondSpan, l) / Math.Max(i.SecondSpan, l)) : 1.0;
            r.Ke2 = i.FourEdges && i.EiL > 0 && b > 0 ? Math.Sqrt(1 + Math.Pow(l / b, 4) * i.EiT / i.EiL) : 1.0;
            if (!double.IsNaN(i.WSysMm) && i.WSysMm > 0) r.F1Rigid = r.Ke1 * r.Ke2 * 18.0 / Math.Sqrt(i.WSysMm);
            else r.F1Rigid = r.Ke1 * r.Ke2 * F1Beam(Math.PI, l, i.EiL, i.Mass);
            double inv = 1 / (r.F1Rigid * r.F1Rigid);
            if (i.F1Beam1 > 0) inv += 1 / (3 * i.F1Beam1 * i.F1Beam1);
            if (i.F1Beam2 > 0) inv += 1 / (3 * i.F1Beam2 * i.F1Beam2);
            r.F1 = double.IsNaN(i.F1Given) ? (r.F1Rigid > 0 ? 1 / Math.Sqrt(inv) : 0) : i.F1Given;

            // Stiffness (9.17) - (9.20)
            if (i.EiStiffener > 0)
                r.Bef = Math.Min(1.075 * Math.Pow(l, 0.75) * Math.Pow((i.EiStiffener + 0.63 * l * i.EiT) / i.EiL, 0.25), b);
            else
                r.Bef = Math.Min(0.95 * l * Math.Pow(i.EiT / i.EiL, 0.25), b);
            r.WRigid = r.Bef > 0 && i.EiL > 0 ? 1000.0 * Math.Pow(l, 3) / (48 * i.EiL * r.Bef) * 1000.0 : 0;  // mm
            r.W1kN = 0.5 * i.WBeam1 + r.WRigid + 0.5 * i.WBeam2;

            // Velocity (9.21) - (9.27)
            r.IMod = r.F1 > 0 ? 42 * Math.Pow(i.Fw, 1.43) / Math.Pow(r.F1, 1.3) : 0;
            r.MStar = i.Mass * l * b / i.ModalMassDivisor;
            r.V1Peak = i.KRed * r.IMod / (r.MStar + 70);
            r.KImp = Math.Max(0.48 * (b / l) * ratio, 1.0);
            double kMax = i.Joisted ? 1.9 : 1.7;
            r.Eta = r.KImp >= 1.0 && r.KImp <= kMax ? 1.35 - 0.4 * r.KImp : (i.Joisted ? 0.59 : 0.67);
            r.VTotPeak = r.KImp * r.V1Peak;
            r.VRms = r.VTotPeak * (0.65 - 0.01 * r.F1) * (1.22 - 11.0 * i.Zeta) * r.Eta;

            // Acceleration (9.28), (9.29)
            r.KRes = Math.Max(0.19 * (b / l) * ratio, 1.0);
            r.ARms = r.MStar > 0 && i.Zeta > 0 ? r.KRes * i.MuRes * i.FDyn / (Math.Sqrt(2) * 2 * i.Zeta * r.MStar) : double.PositiveInfinity;

            // Every level (Table 9.1); f1,lim = 4·f_w (9.12), ≥ 8 Hz for multi-family or levels I and II
            Table91 t = i.Table;
            double ucTarget = 0;
            string gov = "";
            for (int k = 0; k < 8; k++)
            {
                double flim = 4 * i.Fw;
                if (i.MultiFamily || k <= 1) flim = Math.Max(flim, 8.0);
                r.F1Lim[k] = flim;
                r.Resonant[k] = r.F1 < flim;
                double fMin = k <= t.LastFixedFrequency ? t.F1Min : flim;
                double wLim = t.WLim(k, l);
                List<KeyValuePair<string, double>> uc = new List<KeyValuePair<string, double>>
                {
                    new KeyValuePair<string, double>("frequency", r.F1 > 0 ? fMin / r.F1 : double.PositiveInfinity),
                    new KeyValuePair<string, double>("stiffness", r.W1kN / wLim),
                    new KeyValuePair<string, double>("velocity", r.VRms / t.VRmsLim[k])
                };
                if (r.Resonant[k])
                    uc.Add(new KeyValuePair<string, double>("acceleration",
                        double.IsNaN(t.ARmsLim[k]) ? double.PositiveInfinity : r.ARms / t.ARmsLim[k]));
                KeyValuePair<string, double> worst = uc.OrderByDescending(p => p.Value).First();
                r.Pass[k] = worst.Value <= 1.0;
                if (k == i.TargetLevel) { ucTarget = worst.Value; gov = worst.Key; }
            }
            for (int k = 0; k < 8; k++) if (r.Pass[k]) { r.AchievedLevel = k; break; }
            r.Uc = ucTarget;
            r.Governing = gov;

            int tl = i.TargetLevel;
            r.Lines.Add("EN 1995-1-1:2025 §9.3.2, target level " + Levels[tl]);
            r.Lines.Add(L("l", l, "m") + "   " + L("b", b, "m") + (i.SecondSpan > 0 ? "   " + L("l2", i.SecondSpan, "m") : ""));
            r.Lines.Add(L("(EI)_L", i.EiL, "Nm²/m") + "   " + L("(EI)_T", i.EiT, "Nm²/m"));
            r.Lines.Add(L("m (9.3.1.2)", i.Mass, "kg/m²") + "   ζ = " + Num(i.Zeta, 3) + "   f_w = " + Num(i.Fw, 2) + " Hz");
            r.Lines.Add(L("k_e,1 (Table 9.4)", r.Ke1, "") + "   " + L("k_e,2 (9.14)", r.Ke2, ""));
            r.Lines.Add(L(!double.IsNaN(i.WSysMm) && i.WSysMm > 0 ? "f1,rigid = k_e1·k_e2·18/√w_sys (9.15)" : "f1,rigid (9.13)", r.F1Rigid, "Hz"));
            r.Lines.Add(L("f1" + (double.IsNaN(i.F1Given) ? (i.F1Beam1 > 0 || i.F1Beam2 > 0 ? " (9.16)" : "") : " (given)"), r.F1, "Hz") +
                "   f1,lim = " + Num(r.F1Lim[tl], 2) + " Hz → " + (r.Resonant[tl] ? "resonant" : "transient"));
            r.Lines.Add(L(i.EiStiffener > 0 ? "b_ef (9.19)" : "b_ef (9.18)", r.Bef, "m"));
            r.Lines.Add(L("w_1kN (9.17)/(9.20)", r.W1kN, "mm") + "   w_lim = " + Num(t.WLim(tl, l), 3) + " mm");
            r.Lines.Add(L("I_mod,mean (9.21)", r.IMod, "Ns") + "   " + L("M* (9.23)", r.MStar, "kg"));
            r.Lines.Add(L("v1,peak (9.22)", r.V1Peak, "m/s") + "   " + L("k_imp (9.24)", r.KImp, "") + "   η = " + Num(r.Eta, 3));
            r.Lines.Add(L("v_tot,peak (9.26)", r.VTotPeak, "m/s") + "   " + L("v_rms (9.25)", r.VRms, "m/s") +
                "   v_rms,lim = " + Num(t.VRmsLim[tl], 4));
            r.Lines.Add(L("k_res (9.29)", r.KRes, "") + "   " + L("a_rms (9.28)", r.ARms, "m/s²") +
                (r.Resonant[tl] ? "   a_rms,lim = " + (double.IsNaN(t.ARmsLim[tl]) ? "n/a" : Num(t.ARmsLim[tl], 3)) : "   (transient: not checked)"));
            r.Lines.Add("Achieved level: " + (r.AchievedLevel < 0 ? "none (fails VIII)" : Levels[r.AchievedLevel]) +
                "   UC vs " + Levels[tl] + " = " + Num(r.Uc, 2) + " (" + gov + ")");
            return r;
        }

        // ------------------------------------------------------------------
        // Formatting
        // ------------------------------------------------------------------

        public static string Num(double v, int decimals)
        {
            if (double.IsNaN(v)) return "-";
            if (double.IsInfinity(v)) return "∞";
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        /// <summary>Significant-digit formatting for detail lines.</summary>
        public static string Sig(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (double.IsInfinity(v)) return "∞";
            double a = Math.Abs(v);
            if (a == 0) return "0";
            if (a >= 1000) return v.ToString("#,0", CultureInfo.InvariantCulture);
            if (a >= 1) return v.ToString("0.###", CultureInfo.InvariantCulture);
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static string L(string label, double v, string unit) { return label + " = " + Sig(v) + (unit.Length > 0 ? " " + unit : ""); }
    }
}
