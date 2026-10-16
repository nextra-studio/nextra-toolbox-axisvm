using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// The loads of an AxisVM model as Rhino Ax_* attribute values, for "Send to Rhino" (formats: Rhino AxisBridge LoadRow,
    /// read back by AttributeParser on import):
    ///   Ax_DistLoad on a member     "LC=Dead; sys=Global; from=ratio:0; to=ratio:1; q1=qx,qy,qz,mx,my,mz[; q2=...]"
    ///   Ax_PointLoad on a member    "LC=Dead; sys=Global; at=ratio:0.5; F=fx,fy,fz,mx,my,mz"
    ///   Ax_NodalLoad on a point     "LC=Dead; F=fx,fy,fz,mx,my,mz"
    ///   Ax_DistLoad on a surface    "LC=Dead; sys=Global|Local|Projected; q=qx,qy,qz"            (kN/m2)
    ///   Ax_PointLoad on a surface   "LC=Dead; sys=Global; at=x,y,z; F=fx,fy,fz,mx,my,mz"        (x,y,z in metres; Rhino scales)
    ///
    /// AxisVM returns most load records only as raw bytes (IAxisVMLoads.GetLoad); the offsets below follow the record
    /// structures of the type library (natural alignment) and were checked against a real model: member distributed
    /// 160 bytes, member concentrated 88, nodal force 64, domain constant 40, beam / rib distributed 136, beam / rib
    /// concentrated 72, and the load panel records of Functions/PanelLoadCarrier (type -1: area 120, point 88).
    /// Positions on members and lines are lengths [m] from the member / line start; they are sent as ratios.
    /// </summary>
    public class LinkLoads
    {
        public readonly Dictionary<int, List<string>> MemberDist = new Dictionary<int, List<string>>();
        public readonly Dictionary<int, List<string>> MemberPoint = new Dictionary<int, List<string>>();
        /// <summary>Line-based beam / rib loads: (from, to) in metres along the line, ready to be turned into ratios of the curve.</summary>
        public readonly Dictionary<int, List<LineLoad>> LineLoads = new Dictionary<int, List<LineLoad>>();
        public readonly Dictionary<int, List<string>> Nodal = new Dictionary<int, List<string>>();
        public readonly Dictionary<int, List<string>> DomainArea = new Dictionary<int, List<string>>();
        public readonly Dictionary<int, List<string>> PanelArea = new Dictionary<int, List<string>>();
        public readonly Dictionary<int, List<string>> PanelPoint = new Dictionary<int, List<string>>();

        /// <summary>Loads of kinds the link does not carry (self weight, thermal, fluid, polygon loads ...), by type.</summary>
        public readonly Dictionary<string, int> NotSent = new Dictionary<string, int>();
        /// <summary>Panel area loads that vary over the panel: sent with their average intensity.</summary>
        public int Averaged;

        public class LineLoad
        {
            public bool Distributed;
            public string Case, System;
            public double From, To;          // metres from the line start (To unused for point loads)
            public double[] V1 = new double[6], V2 = new double[6];
        }

        private const int PanelLoadType = -1;

        public static LinkLoads Read(IAxisVMModel model)
        {
            LinkLoads r = new LinkLoads();
            Dictionary<int, string> cases = new Dictionary<int, string>();
            int count = 0;
            try { count = model.Loads.Count; } catch { }
            for (int i = 1; i <= count; i++)
            {
                int type;
                try { type = (int)model.Loads.get_LoadType(i); } catch { continue; }
                byte[] raw = Raw(model, i);
                if (raw == null) { r.Skip("unreadable"); continue; }
                Func<int, string> caseName = id =>
                {
                    string n;
                    if (!cases.TryGetValue(id, out n))
                    {
                        try { n = (model.LoadCases.get_Name(id) ?? "").Trim(); } catch { n = ""; }
                        cases[id] = n;
                    }
                    return n.Length > 0 ? n.Replace(";", " ").Replace("=", " ") : "LC" + id;
                };

                switch (type)
                {
                    case (int)ELoadType.ltMemberDistributed:
                    case (int)ELoadType.ltBeamMemberDistributed:
                    case (int)ELoadType.ltRibMemberDistributed:
                        if (raw.Length < 136) { r.Skip("member distributed (short record)"); break; }
                        Add(r.MemberDist, I(raw, 4), r.MemberDistText(caseName(I(raw, 0)), Sys(I(raw, 104), I(raw, 128) == 1), Doubles(raw, 8), Doubles(raw, 56), D(raw, 112), D(raw, 120), model, I(raw, 4)));
                        break;
                    case (int)ELoadType.ltMemberConcentrated:
                    case (int)ELoadType.ltBeamMemberConcentrated:
                    case (int)ELoadType.ltRibMemberConcentrated:
                        if (raw.Length < 72) { r.Skip("member concentrated (short record)"); break; }
                        Add(r.MemberPoint, I(raw, 4), PointText(caseName(I(raw, 0)), Sys(I(raw, 64), false), Doubles(raw, 8), r.MemberRatio(model, I(raw, 4), D(raw, 56))));
                        break;
                    case (int)ELoadType.ltBeamDistributed:
                    case (int)ELoadType.ltRibDistributed:
                        if (raw.Length < 136) { r.Skip("line distributed (short record)"); break; }
                        AddLine(r, I(raw, 4), new LineLoad { Distributed = true, Case = caseName(I(raw, 0)), System = Sys(I(raw, 104), I(raw, 128) == 1), From = D(raw, 112), To = D(raw, 120), V1 = Doubles(raw, 8), V2 = Doubles(raw, 56) });
                        break;
                    case (int)ELoadType.ltBeamConcentrated:
                    case (int)ELoadType.ltRibConcentrated:
                        if (raw.Length < 72) { r.Skip("line concentrated (short record)"); break; }
                        AddLine(r, I(raw, 4), new LineLoad { Distributed = false, Case = caseName(I(raw, 0)), System = Sys(I(raw, 64), false), From = D(raw, 56), V1 = Doubles(raw, 8) });
                        break;
                    case (int)ELoadType.ltNodalForce:
                        if (raw.Length < 56) { r.Skip("nodal force (short record)"); break; }
                        Add(r.Nodal, I(raw, 4), "LC=" + caseName(I(raw, 0)) + "; F=" + Nums(Doubles(raw, 8), 6));
                        break;
                    case (int)ELoadType.ltDomainDistributed:   // = ltDomainConstant (same value)
                        if (raw.Length < 40) { r.Skip("domain distributed (short record)"); break; }
                        string dsys = I(raw, 12) == 1 ? "Projected" : I(raw, 8) == 1 ? "Local" : "Global";
                        Add(r.DomainArea, I(raw, 4), "LC=" + caseName(I(raw, 0)) + "; sys=" + dsys + "; q=" + Nums(new[] { D(raw, 16), D(raw, 24), D(raw, 32) }, 3));
                        break;
                    case PanelLoadType:
                        if (raw.Length >= 120) r.PanelAreaLoad(raw, caseName);
                        else if (raw.Length == 88)
                            Add(r.PanelPoint, I(raw, 4), "LC=" + caseName(I(raw, 0)) + "; sys=" + Sys(I(raw, 80), false) + "; at=" + Nums(new[] { D(raw, 56), D(raw, 64), D(raw, 72) }, 3) + "; F=" + Nums(Doubles(raw, 8), 6));
                        else r.Skip("load panel (unknown record)");
                        break;
                    default:
                        string name;
                        try { name = model.Loads.get_LoadType(i).ToString(); } catch { name = "type " + type; }
                        r.Skip(name.StartsWith("lt", StringComparison.Ordinal) ? name.Substring(2) : name);
                        break;
                }
            }
            return r;
        }

        /// <summary>A whole-panel area load: the axis' slot holds the intensity of a constant load (PanelLoadCarrier).</summary>
        private void PanelAreaLoad(byte[] raw, Func<int, string> caseName)
        {
            int panel = I(raw, 4), dist = I(raw, 8), linear = I(raw, 12), component = I(raw, 16);
            if (component < 0 || component > 2) { Skip("load panel moment load"); return; }
            double[] p = { D(raw, 24), D(raw, 32), D(raw, 40) };
            double[] q = new double[3];
            if (linear == 1)
            {
                // Linear: P1..P3 are intensities at the three reference points, in the direction of Component.
                q[component] = (p[0] + p[1] + p[2]) / 3.0;
                Averaged++;
            }
            else
            {
                // Constant: each slot holds the intensity in its own axis (a Z load sits in P3 while Component stays 0).
                q = p;
            }
            string sys = dist == 1 ? "Local" : dist == 2 ? "Projected" : "Global";
            Add(PanelArea, panel, "LC=" + caseName(I(raw, 0)) + "; sys=" + sys + "; q=" + Nums(q, 3));
        }

        // ==================================================================
        // Turning lengths into ratios
        // ==================================================================

        private string MemberDistText(string lc, string sys, double[] q1, double[] q2, double p1, double p2, IAxisVMModel model, int member)
        {
            double len = MemberLength(model, member);
            double a = len > 1e-9 ? Clamp(p1 / len) : 0, b = len > 1e-9 ? Clamp(p2 / len) : 1;
            if (b <= a) { a = 0; b = 1; }
            return DistText(lc, sys, q1, q2, a, b);
        }

        public static string DistText(string lc, string sys, double[] q1, double[] q2, double a, double b)
        {
            string s = "LC=" + lc + "; sys=" + sys + "; from=ratio:" + N(a) + "; to=ratio:" + N(b) + "; q1=" + Nums(q1, 6);
            if (!Same(q1, q2)) s += "; q2=" + Nums(q2, 6);
            return s;
        }

        private double MemberRatio(IAxisVMModel model, int member, double pos)
        {
            double len = MemberLength(model, member);
            return len > 1e-9 ? Clamp(pos / len) : 0.5;
        }

        public static string PointText(string lc, string sys, double[] f, double ratio)
        {
            return "LC=" + lc + "; sys=" + sys + "; at=ratio:" + N(ratio) + "; F=" + Nums(f, 6);
        }

        private readonly Dictionary<int, double> memberLengths = new Dictionary<int, double>();

        private double MemberLength(IAxisVMModel model, int member)
        {
            double len;
            if (memberLengths.TryGetValue(member, out len)) return len;
            try { len = model.Members.Item[member].Length; } catch { len = 0; }
            memberLengths[member] = len;
            return len;
        }

        private static double Clamp(double v) { return Math.Max(0.0, Math.Min(1.0, v)); }

        private static string Sys(int glr, bool projected)
        {
            if (glr == 1) return "Local";
            return projected ? "Projected" : "Global";
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>Ax_* keys of a family: Ax_DistLoad, Ax_DistLoad.1, ...</summary>
        public static string FamilyKey(string baseKey, int index)
        {
            return index == 0 ? baseKey : baseKey + "." + index.ToString(CultureInfo.InvariantCulture);
        }

        private static void Add(Dictionary<int, List<string>> d, int id, string text)
        {
            List<string> l;
            if (!d.TryGetValue(id, out l)) { l = new List<string>(); d[id] = l; }
            l.Add(text);
        }

        private static void AddLine(LinkLoads r, int line, LineLoad l)
        {
            List<LineLoad> list;
            if (!r.LineLoads.TryGetValue(line, out list)) { list = new List<LineLoad>(); r.LineLoads[line] = list; }
            list.Add(l);
        }

        private void Skip(string what)
        {
            int n;
            NotSent.TryGetValue(what, out n);
            NotSent[what] = n + 1;
        }

        private static byte[] Raw(IAxisVMModel model, int index)
        {
            Array data;
            try { model.Loads.GetLoad(index, out data); } catch { return null; }
            if (data == null || data.Length == 0) return null;
            byte[] raw = new byte[data.Length];
            int lower = data.GetLowerBound(0);
            for (int b = 0; b < raw.Length; b++) raw[b] = Convert.ToByte(data.GetValue(lower + b));
            return raw;
        }

        private static int I(byte[] raw, int offset) { return offset + 4 <= raw.Length ? BitConverter.ToInt32(raw, offset) : 0; }
        private static double D(byte[] raw, int offset) { return offset + 8 <= raw.Length ? BitConverter.ToDouble(raw, offset) : 0.0; }

        private static double[] Doubles(byte[] raw, int offset)
        {
            double[] v = new double[6];
            for (int k = 0; k < 6; k++) v[k] = D(raw, offset + 8 * k);
            return v;
        }

        private static bool Same(double[] a, double[] b)
        {
            for (int i = 0; i < 6; i++) if (Math.Abs(a[i] - b[i]) > 1e-12) return false;
            return true;
        }

        public static string N(double v)
        {
            if (Math.Abs(v) < 1e-12) return "0";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static string Nums(double[] v, int count)
        {
            string[] s = new string[count];
            for (int i = 0; i < count; i++) s[i] = N(i < v.Length ? v[i] : 0);
            return string.Join(",", s);
        }
    }
}
