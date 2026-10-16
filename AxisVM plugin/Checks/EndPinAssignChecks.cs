using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AxisVM;
using AxisVM_plugin.Functions;
using AxisVM_plugin.UI;

// Compiles the production logic and dialog directly. Does not open or change an AxisVM model.
internal static class EndPinAssignChecks
{
    private static readonly Type Logic = typeof(EndPinAssignFunction);
    private static readonly Type Line = Logic.GetNestedType("LineInfo", BindingFlags.NonPublic);
    private static readonly Type LineMap = typeof(Dictionary<,>).MakeGenericType(typeof(int), Line);
    private static int checks;

    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static object Call(string name, params object[] args)
    {
        return Logic.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    }

    private static object Member(int id, Coord a, Coord b)
    {
        object line = Activator.CreateInstance(Line, true);
        Line.GetField("LineId").SetValue(line, id);
        Line.GetField("StartNodeId").SetValue(line, a.NodeId);
        Line.GetField("EndNodeId").SetValue(line, b.NodeId);
        Line.GetField("StartCoord").SetValue(line, a);
        Line.GetField("EndCoord").SetValue(line, b);
        Line.GetField("CanTakeReleases").SetValue(line, true);
        return line;
    }

    private static bool Continuous(object a, object b, Coord shared, double tolerance)
    {
        return (bool)Call("IsContinuousStrict", a, b, shared, tolerance);
    }

    private static IDictionary Map(params object[] members)
    {
        IDictionary map = (IDictionary)Activator.CreateInstance(LineMap);
        foreach (object member in members) map.Add(Line.GetField("LineId").GetValue(member), member);
        return map;
    }

    private static void Setting(EndPinAssignForm form, string name, object value)
    {
        typeof(EndPinAssignForm).GetProperty(name).GetSetMethod(true).Invoke(form, new object[] { value });
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            Coord a = new Coord(1, 0, 0, 0), b = new Coord(2, 1, 0, 0), c = new Coord(3, 2, 0, 0);
            Check(!a.Equals(new Coord(99, 0, 0, 0)), "coincident separate nodes stay disconnected");
            Check(a.Equals(new Coord(1, 0, 0, 0)), "same node connects");
            Check(new HashSet<Coord> { a, new Coord(99, 0, 0, 0) }.Count == 2, "node identity used in graph keys");
            object ab = Member(1, a, b), bc = Member(2, b, c), cb = Member(2, c, b);
            Check(Continuous(ab, bc, b, 0), "straight continuation at zero tolerance");
            Check(Continuous(ab, cb, b, 0), "continuity independent of local x reversal");
            object corner = Member(3, b, new Coord(4, 1, 1, 0));
            Check(!Continuous(ab, corner, b, 15), "right angle splits below tolerance");
            Check(Continuous(ab, corner, b, 90), "right angle joins at inclusive tolerance");
            double radians = 15 * Math.PI / 180;
            object bend = Member(3, b, new Coord(4, 1 + Math.Cos(radians), Math.Sin(radians), 0));
            Check(Continuous(ab, bend, b, 15), "angle threshold inclusive");
            Check(!Continuous(ab, bend, b, 14.9), "bend above tolerance splits");
            object sameSide = Member(3, b, a);
            Check(Continuous(ab, sameSide, b, 0), "documented smaller-angle behavior retained");
            Check(!Continuous(ab, bc, new Coord(99, 1, 0, 0), 90), "unrelated shared node rejected");
            Coord tiny = new Coord(5, 0.00001, 0, 0);
            Check(Continuous(Member(4, a, tiny), Member(5, tiny, b), tiny, 0), "short lines retain coordinate precision");
            Check(!Continuous(Member(4, a, new Coord(5, 0, 0, 0)), ab, a, 90), "zero length cannot continue");

            IDictionary lines = Map(ab, cb);
            object trace = Call("TraceChain", new List<int> { 1, 2 }, a, lines);
            Check(trace != null, "mixed orientation chain traced");
            IList forward = (IList)trace.GetType().GetField("Forward").GetValue(trace);
            Check((bool)forward[0] && !(bool)forward[1], "mixed local directions recognized");
            Check(((Coord)trace.GetType().GetField("End").GetValue(trace)).Equals(c), "trace ends at physical terminal node");
            Check(Call("TraceChain", new List<int> { 1, 2 }, b, lines) == null, "trace cannot start at interior node");
            Check(Call("TraceChain", new List<int> { 1, 2 }, a, Map(ab, Member(2, new Coord(99, 1, 0, 0), c))) == null,
                "coincident disconnected lines cannot form trace");

            var chains = new Dictionary<int, List<int>> { { 0, new List<int> { 1, 2, 3 } } };
            Coord d = new Coord(4, 3, 0, 0);
            var ends = new Dictionary<int, List<Coord>> { { 0, new List<Coord> { a, d } } };
            var warnings = new List<string>();
            var unordered = new HashSet<int>();
            // Two lines point backwards. Tracing must choose d -> a even with auto-orient off.
            int flips = (int)Call("NormaliseChainDirections", null, chains, ends,
                Map(ab, cb, Member(3, d, c)), warnings, false, unordered);
            Check(flips == 0 && ends[0][0].Equals(d) && ends[0][1].Equals(a), "majority orders ends without model edits");
            Check(warnings.Count == 1 && unordered.Count == 0, "opposing axes reported for simple chain");

            chains[0] = new List<int> { 1, 2, 3 };
            ends[0] = new List<Coord> { a, c, d };
            unordered.Clear();
            Call("NormaliseChainDirections", null, chains, ends, Map(ab, bc, Member(3, b, d)), warnings, false, unordered);
            Check(unordered.Contains(0), "branch has no unique Start/End target");
            // A loop with two tails has two leaves but is still not a simple chain.
            Coord e = new Coord(5, 1, 1, 0);
            Check(Call("TraceChain", new List<int> { 1, 2, 3, 4, 5 }, a,
                Map(ab, bc, Member(3, c, e), Member(4, e, b), Member(5, b, d))) == null,
                "loop with two tails rejected despite two free ends");
            ends[0] = new List<Coord>();
            unordered.Clear();
            Call("NormaliseChainDirections", null, chains, ends, Map(ab, bc, Member(3, c, a)), warnings, false, unordered);
            Check(unordered.Contains(0), "closed loop has no terminal targets");

            using (var form = new EndPinAssignForm())
            {
                var intermediate = (System.Windows.Forms.CheckBox)typeof(EndPinAssignForm)
                    .GetField("chkModifyIntermediate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Check(intermediate.Checked, "intermediate modification enabled by default");
                var accept = typeof(EndPinAssignForm).GetMethod("BtnOk_Click", BindingFlags.NonPublic | BindingFlags.Instance);
                accept.Invoke(form, new object[] { null, EventArgs.Empty });
                Check(form.ModifyIntermediateReleases, "enabled intermediate option passed to logic");
                intermediate.Checked = false;
                accept.Invoke(form, new object[] { null, EventArgs.Empty });
                Check(!form.ModifyIntermediateReleases, "preserve intermediate option passed to logic");
                Setting(form, "IsSemiRigid", false);
                var existing = new RReleases
                {
                    x = new RRelease { ReleaseType = EReleaseType.rtHinged },
                    y = new RRelease { ReleaseType = EReleaseType.rtSemiRigid, Init = 17 },
                    xx = new RRelease { ReleaseType = EReleaseType.rtSemiRigid, Init = 23 }
                };
                RReleases changed = (RReleases)Call("ModifyRelease", existing, form, false);
                Check(changed.yy.ReleaseType == EReleaseType.rtHinged && changed.zz.ReleaseType == EReleaseType.rtHinged, "complete pin releases both bending axes");
                Check(changed.x.ReleaseType == existing.x.ReleaseType && changed.y.Init == 17, "translational releases preserved");
                Check(changed.xx.ReleaseType == existing.xx.ReleaseType && changed.xx.Init == 23, "existing torsion preserved when not requested");
                changed = (RReleases)Call("ModifyRelease", existing, form, true);
                Check(changed.xx.ReleaseType == EReleaseType.rtHinged, "complete torsion release when requested");
                Setting(form, "IsSemiRigid", true);
                Setting(form, "Stiffness", 12.5);
                changed = (RReleases)Call("ModifyRelease", existing, form, true);
                Check(changed.yy.ReleaseType == EReleaseType.rtSemiRigid && changed.zz.Init == 12.5 && changed.xx.Init == 12.5,
                    "semi-rigid stiffness assigned to requested rotations");
                changed = (RReleases)Call("ModifyRelease", existing, form, false);
                Check(changed.xx.Init == 23, "semi-rigid pin preserves other torsion");
                RReleases fixedJoint = (RReleases)Call("FullyFixed");
                Check(!(bool)Call("HasAnyRelease", fixedJoint), "reset end is fully fixed");
                Check(fixedJoint.xx.Init == 0 && fixedJoint.yy.Init == 0 && fixedJoint.zz.Init == 0,
                    "reset clears spring stiffness");
                Check((bool)Call("HasAnyRelease", changed) && (bool)Call("HasAnyRelease", existing),
                    "pinned and translation-released ends need a reset write");
            }

            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
                double number;
                Check(EndPinAssignForm.TryFiniteNonNegative("15.0", out number) && number == 15, "dot decimal defaults work in Dutch locale");
                Check(EndPinAssignForm.TryFiniteNonNegative("1,5", out number) && number == 1.5, "local decimal separator supported");
                Check(EndPinAssignForm.TryFiniteNonNegative("0", out number) && number == 0, "zero stiffness and tolerance accepted");
                Check(!EndPinAssignForm.TryFiniteNonNegative("NaN", out number), "NaN rejected");
                Check(!EndPinAssignForm.TryFiniteNonNegative("Infinity", out number), "infinity rejected");
                Check(!EndPinAssignForm.TryFiniteNonNegative("-1", out number), "negative value rejected");
                Check(!EndPinAssignForm.TryFiniteNonNegative("garbage", out number), "invalid text rejected");
            }
            finally { CultureInfo.CurrentCulture = previous; }
            Console.WriteLine("All " + checks + " EndPinAssign checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
