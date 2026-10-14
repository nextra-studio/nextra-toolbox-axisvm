using System;
using System.Collections.Generic;
using System.Globalization;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// How the numbers written into the Rhino attributes are to be read.
    ///
    /// AxisVM's COM API is always metres and newtons, whatever the program happens to be *displaying*.
    /// So there is only ever one conversion to do: from whatever the user typed in Rhino into SI. The
    /// import dialog asks for the two factors that decides.
    ///
    /// Only geometric quantities take the length factor - coordinates, thickness, eccentricity, a
    /// distance along a beam. Load and stiffness units are written with a fixed metre in them
    /// (kN/m, kN/m2, kNm/rad), so those take the force factor alone. That keeps "5 kN/m" meaning the
    /// same thing whether the Rhino file is drawn in millimetres or metres.
    /// </summary>
    public class AxUnits
    {
        /// <summary>Multiply a Rhino length by this to get metres in AxisVM.</summary>
        public double LengthToM = 0.001;

        /// <summary>Multiply a written force by this to get kN in AxisVM.</summary>
        public double ForceToKN = 1.0;

        public string LengthName = "mm";
        public string ForceName = "kN, m";

        /// <summary>Moments: kNm in AxisVM.</summary>
        public double Moment(double v) { return v * ForceToKN; }

        public double Length(double v) { return v * LengthToM; }
        public double Force(double v) { return v * ForceToKN; }

        /// <summary>Translational stiffness: kN/m in AxisVM.</summary>
        public double Stiffness(double v) { return v * ForceToKN; }

        /// <summary>Rotational stiffness: kNm/rad in AxisVM.</summary>
        public double RotStiffness(double v) { return v * ForceToKN; }

        public static readonly string[] LengthUnitNames = { "mm", "m" };
        public static readonly double[] LengthUnitFactors = { 0.001, 1.0 };

        public static readonly string[] ForceUnitNames = { "kN, m", "N, mm" };
        public static readonly double[] ForceUnitFactors = { 1.0, 0.001 };

        /// <summary>Maps Rhino's unit-system name onto an index into <see cref="LengthUnitNames"/>.</summary>
        public static int LengthIndexFromRhinoName(string rhinoUnitSystem)
        {
            if (string.IsNullOrEmpty(rhinoUnitSystem)) return 0;
            switch (rhinoUnitSystem.Trim().ToLowerInvariant())
            {
                case "meters": case "metres": case "m": return 1;
                default: return 0; // mm
            }
        }
    }

    /// <summary>Per-DOF release, as AxisVM wants it: a type plus a stiffness for the semi-rigid case.</summary>
    public struct ReleaseDof
    {
        /// <summary>0 = hinged, 1 = rigid, 2 = semi-rigid.</summary>
        public int Kind;
        public double Stiffness;

        public static ReleaseDof Rigid { get { return new ReleaseDof { Kind = 1 }; } }
        public static ReleaseDof Hinged { get { return new ReleaseDof { Kind = 0 }; } }
        public static ReleaseDof Semi(double k) { return new ReleaseDof { Kind = 2, Stiffness = k }; }
    }

    /// <summary>Releases at one end of a beam or rib: x, y, z, xx, yy, zz.</summary>
    public class ReleaseSet
    {
        public ReleaseDof[] Dof = new ReleaseDof[6];

        public static ReleaseSet AllRigid()
        {
            ReleaseSet r = new ReleaseSet();
            for (int i = 0; i < 6; i++) r.Dof[i] = ReleaseDof.Rigid;
            return r;
        }
    }

    /// <summary>Both ends of one line. Either end may be null, meaning "leave AxisVM's default alone".</summary>
    public class ReleaseSpec
    {
        public ReleaseSet Start;
        public ReleaseSet End;
    }

    /// <summary>A concentrated load, in SI once parsed.</summary>
    public class PointLoadSpec
    {
        public string LoadCase = "";
        public double[] F = new double[6];      // Fx Fy Fz Mx My Mz, in N and Nm

        /// <summary>true when the position is a 0..1 ratio, false when it is a distance in metres.</summary>
        public bool PositionIsRatio = true;
        public double Position = 0.5;

        /// <summary>For a load on a domain: where it sits, in metres.</summary>
        public RhinoXyz At;
        public bool HasAt;
    }

    /// <summary>A distributed load, in SI once parsed.</summary>
    public class DistLoadSpec
    {
        public string LoadCase = "";
        public double[] Q1 = new double[6];     // qx qy qz mx my mz at the start, N/m and Nm/m
        public double[] Q2 = new double[6];     // ... at the end
        public bool PositionIsRatio = true;
        public double From = 0.0;
        public double To = 1.0;

        /// <summary>true when Q1 and Q2 differ, which is what AxisVM calls a trapezoidal load.</summary>
        public bool IsTrapezoid
        {
            get
            {
                for (int i = 0; i < 6; i++)
                    if (Math.Abs(Q1[i] - Q2[i]) > 1e-12) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Turns the attribute strings written in Rhino into typed, SI values.
    ///
    /// Every entry point returns false and fills <c>err</c> rather than throwing: a mistyped attribute
    /// on one object should cost that one attribute, be reported in the run summary, and let the rest
    /// of the import proceed.
    /// </summary>
    public static class AttributeParser
    {
        /// <summary>Stiffness at or above this counts as rigid rather than semi-rigid [N/m, Nm/rad].</summary>
        public const double RigidThreshold = 1e9;

        /// <summary>What a "fixed" support or a coupled link DOF gets [N/m, Nm/rad].</summary>
        public const double RigidStiffness = 1e10;

        /// <summary>What a coupled DOF of a rigid/node link gets - stiffer again, since a link is meant to be rigid.</summary>
        public const double LinkRigidStiffness = 1e12;

        // ==================================================================
        // Primitives
        // ==================================================================

        /// <summary>
        /// Splits "LC=Dead; F=0,0,-10,0,0,0" into its named fields. Values keep their internal commas,
        /// which is exactly why the format is named-field rather than positional.
        /// </summary>
        public static Dictionary<string, string> ParseFields(string value)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value)) return fields;

            string[] parts = value.Split(';');
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;

                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    // A bare token with no "=" - keep it under "" so callers can offer a shorthand.
                    if (!fields.ContainsKey("")) fields[""] = part;
                    continue;
                }

                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                if (key.Length > 0) fields[key] = val;
            }
            return fields;
        }

        /// <summary>Splits a semicolon-separated list ("Roof; Grid A") into trimmed, non-empty entries.</summary>
        public static List<string> SplitList(string value)
        {
            List<string> items = new List<string>();
            if (string.IsNullOrEmpty(value)) return items;

            foreach (string raw in value.Split(';', ','))
            {
                string s = raw.Trim();
                if (s.Length > 0 && s != "-") items.Add(s);
            }
            return items;
        }

        /// <summary>Parses "1,2,3" or "1e10, 0, 0" into exactly <paramref name="expected"/> numbers.</summary>
        public static bool TryParseNumbers(string value, int expected, out double[] result, out string err)
        {
            result = null;
            err = null;

            if (string.IsNullOrEmpty(value)) { err = "the value is empty"; return false; }

            string trimmed = value.Trim().Trim('[', ']', '(', ')');
            string[] parts = trimmed.Split(',');
            if (parts.Length != expected)
            {
                err = string.Format("expected {0} comma-separated numbers but found {1} ('{2}')", expected, parts.Length, value);
                return false;
            }

            double[] values = new double[expected];
            for (int i = 0; i < expected; i++)
            {
                string token = parts[i].Trim();
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    err = string.Format("'{0}' is not a number (in '{1}')", token, value);
                    return false;
                }
            }

            result = values;
            return true;
        }

        // ==================================================================
        // Point attributes
        // ==================================================================

        /// <summary>
        /// Reads Ax_Support: either six stiffnesses, or one of the shorthands. Result is in
        /// N/m for the three translations and Nm/rad for the three rotations.
        /// </summary>
        public static bool TryParseSupport(string value, AxUnits units, out double[] stiff, out string err)
        {
            stiff = null;
            err = null;

            string keyword = value.Trim().ToLowerInvariant();
            switch (keyword)
            {
                case "fixed":
                case "fix":
                    stiff = new[] { RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness };
                    return true;

                case "pinned":
                case "pin":
                    stiff = new[] { RigidStiffness, RigidStiffness, RigidStiffness, 0.0, 0.0, 0.0 };
                    return true;

                case "roller-z":
                case "rollerz":
                    // Free to slide in the horizontal plane, held vertically, free to rotate.
                    stiff = new[] { 0.0, 0.0, RigidStiffness, 0.0, 0.0, 0.0 };
                    return true;
            }

            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            stiff = new double[6];
            for (int i = 0; i < 3; i++) stiff[i] = units.Stiffness(raw[i]);
            for (int i = 3; i < 6; i++) stiff[i] = units.RotStiffness(raw[i]);
            return true;
        }

        /// <summary>Reads one Ax_NodalLoad.n: "LC=Dead; F=0,0,-10,0,0,0".</summary>
        public static bool TryParseNodalLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;
            err = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(forces, 6, out raw, out err)) return false;

            load = new PointLoadSpec { LoadCase = lc };
            for (int i = 0; i < 3; i++) load.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) load.F[i] = units.Moment(raw[i]);
            return true;
        }

        // ==================================================================
        // Line attributes
        // ==================================================================

        /// <summary>
        /// Reads Ax_Release. Accepts the three shorthands, or explicit per-end stiffnesses:
        /// "start=1e10,1e10,1e10,1e10,0,0; end=...". A stiffness of 0 is a hinge, anything at or above
        /// <see cref="RigidThreshold"/> is rigid, everything between is semi-rigid at that stiffness.
        /// </summary>
        public static bool TryParseRelease(string value, AxUnits units, out ReleaseSpec spec, out string err)
        {
            spec = null;
            err = null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "fix":
                case "fixed":
                    spec = new ReleaseSpec { Start = ReleaseSet.AllRigid(), End = ReleaseSet.AllRigid() };
                    return true;

                case "pin":
                case "pinned":
                    spec = new ReleaseSpec { Start = Pinned(), End = Pinned() };
                    return true;

                case "semi-pin":
                case "semipin":
                    // A nearly-hinged connection: 1 Nm/rad about both bending axes, as the existing
                    // EndPinAssign addon uses. Deliberately an absolute SI value, so the shorthand
                    // means the same thing whichever force unit the file was written in.
                    spec = new ReleaseSpec { Start = SemiPinned(), End = SemiPinned() };
                    return true;
            }

            Dictionary<string, string> f = ParseFields(value);
            ReleaseSpec result = new ReleaseSpec();

            string startText = Field(f, "start", "s", "");
            string endText = Field(f, "end", "e", "");

            if (string.IsNullOrEmpty(startText) && string.IsNullOrEmpty(endText))
            {
                // Bare six numbers - apply the same set to both ends.
                ReleaseSet both;
                if (!TryParseReleaseSet(value, units, out both, out err)) return false;
                spec = new ReleaseSpec { Start = both, End = both };
                return true;
            }

            if (!string.IsNullOrEmpty(startText) && !TryParseReleaseSet(startText, units, out result.Start, out err))
            {
                err = "start: " + err;
                return false;
            }
            if (!string.IsNullOrEmpty(endText) && !TryParseReleaseSet(endText, units, out result.End, out err))
            {
                err = "end: " + err;
                return false;
            }

            spec = result;
            return true;
        }

        private static ReleaseSet Pinned()
        {
            ReleaseSet r = ReleaseSet.AllRigid();
            r.Dof[4] = ReleaseDof.Hinged;   // yy
            r.Dof[5] = ReleaseDof.Hinged;   // zz
            return r;
        }

        private static ReleaseSet SemiPinned()
        {
            ReleaseSet r = ReleaseSet.AllRigid();
            r.Dof[4] = ReleaseDof.Semi(1.0);
            r.Dof[5] = ReleaseDof.Semi(1.0);
            return r;
        }

        private static bool TryParseReleaseSet(string value, AxUnits units, out ReleaseSet set, out string err)
        {
            set = null;

            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            set = new ReleaseSet();
            for (int i = 0; i < 6; i++)
            {
                double si = i < 3 ? units.Stiffness(raw[i]) : units.RotStiffness(raw[i]);
                if (si <= 0) set.Dof[i] = ReleaseDof.Hinged;
                else if (si >= RigidThreshold) set.Dof[i] = ReleaseDof.Rigid;
                else set.Dof[i] = ReleaseDof.Semi(si);
            }
            return true;
        }

        /// <summary>Reads Ax_Section: "HEA200" or "HEA200; HEA300" for a tapered member.</summary>
        public static void ParseSectionNames(string value, out string startName, out string endName)
        {
            List<string> names = SplitList(value);
            if (names.Count == 0) { startName = null; endName = null; return; }
            startName = names[0];
            endName = names.Count > 1 ? names[1] : names[0];
        }

        /// <summary>Reads Ax_Eccentricity: one vector for both ends, or "start=...; end=...". Metres out.</summary>
        public static bool TryParseEccentricity(string value, AxUnits units,
                                                out RhinoXyz start, out RhinoXyz end, out string err)
        {
            start = new RhinoXyz();
            end = new RhinoXyz();

            Dictionary<string, string> f = ParseFields(value);
            string startText = Field(f, "start", "s", "");
            string endText = Field(f, "end", "e", "");

            if (string.IsNullOrEmpty(startText) && string.IsNullOrEmpty(endText))
            {
                RhinoXyz both;
                if (!TryParseVector(value, units, out both, out err)) return false;
                start = both;
                end = both;
                return true;
            }

            if (!string.IsNullOrEmpty(startText) && !TryParseVector(startText, units, out start, out err)) return false;
            if (!string.IsNullOrEmpty(endText) && !TryParseVector(endText, units, out end, out err)) return false;
            if (string.IsNullOrEmpty(endText)) end = start;
            if (string.IsNullOrEmpty(startText)) start = end;

            err = null;
            return true;
        }

        /// <summary>Reads a length-valued vector such as Ax_Eccentricity. Metres out.</summary>
        public static bool TryParseVector(string value, AxUnits units, out RhinoXyz v, out string err)
        {
            v = new RhinoXyz();
            double[] raw;
            if (!TryParseNumbers(value, 3, out raw, out err)) return false;
            v = new RhinoXyz(units.Length(raw[0]), units.Length(raw[1]), units.Length(raw[2]));
            return true;
        }

        /// <summary>Reads a direction such as Ax_LocalAxis. Not a length - no unit conversion applies.</summary>
        public static bool TryParseDirection(string value, out RhinoXyz v, out string err)
        {
            v = new RhinoXyz();
            double[] raw;
            if (!TryParseNumbers(value, 3, out raw, out err)) return false;

            if (Math.Abs(raw[0]) < 1e-12 && Math.Abs(raw[1]) < 1e-12 && Math.Abs(raw[2]) < 1e-12)
            {
                err = "the direction is a zero vector";
                return false;
            }

            v = new RhinoXyz(raw[0], raw[1], raw[2]);
            return true;
        }

        /// <summary>Reads Ax_Stiffness (spring or edge hinge): six values, SI out.</summary>
        public static bool TryParseStiffness(string value, AxUnits units, out double[] stiff, out string err)
        {
            stiff = null;
            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            stiff = new double[6];
            for (int i = 0; i < 3; i++) stiff[i] = units.Stiffness(raw[i]);
            for (int i = 3; i < 6; i++) stiff[i] = units.RotStiffness(raw[i]);
            return true;
        }

        /// <summary>
        /// Reads Ax_CoupledDOF: six flags, each "rigid"/"free" or 1/0. Coupled DOFs come back as
        /// <see cref="LinkRigidStiffness"/>, free ones as 0, ready to drop into a link's stiffnesses.
        /// </summary>
        public static bool TryParseCoupledDofs(string value, out double[] stiff, out string err)
        {
            stiff = null;
            err = null;

            string trimmed = value.Trim().Trim('[', ']');
            string[] parts = trimmed.Split(',');
            if (parts.Length != 6)
            {
                err = string.Format("expected 6 flags but found {0} ('{1}')", parts.Length, value);
                return false;
            }

            stiff = new double[6];
            for (int i = 0; i < 6; i++)
            {
                string token = parts[i].Trim().ToLowerInvariant();
                if (token == "rigid" || token == "1" || token == "fix" || token == "fixed" || token == "true")
                    stiff[i] = LinkRigidStiffness;
                else if (token == "free" || token == "0" || token == "false")
                    stiff[i] = 0.0;
                else
                {
                    err = string.Format("'{0}' is neither rigid/1 nor free/0 (in '{1}')", parts[i].Trim(), value);
                    stiff = null;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Reads one Ax_PointLoad.n on a beam or rib:
        /// "LC=Dead; at=ratio:0.5; F=0,0,-10,0,0,0" or "LC=Dead; at=len:2.4; F=...".
        /// </summary>
        public static bool TryParseBeamPointLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(forces, 6, out raw, out err)) return false;

            PointLoadSpec spec = new PointLoadSpec { LoadCase = lc };
            for (int i = 0; i < 3; i++) spec.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) spec.F[i] = units.Moment(raw[i]);

            string at = Field(f, "at", "pos", "position");
            if (!string.IsNullOrEmpty(at))
            {
                bool isRatio;
                double pos;
                if (!TryParsePosition(at, units, out isRatio, out pos, out err)) return false;
                spec.PositionIsRatio = isRatio;
                spec.Position = pos;
            }

            load = spec;
            err = null;
            return true;
        }

        /// <summary>
        /// Reads one Ax_DistLoad.n on a beam or rib:
        /// "LC=Dead; from=ratio:0; to=ratio:1; q1=0,0,-5,0,0,0; q2=0,0,-8,0,0,0".
        /// A single "q=" sets both ends, giving a uniform load.
        /// </summary>
        public static bool TryParseBeamDistLoad(string value, AxUnits units, out DistLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string q1Text = Field(f, "q1", "q", "");
            string q2Text = Field(f, "q2", "", "");
            if (string.IsNullOrEmpty(q1Text)) { err = "no q= or q1= (intensity) given"; return false; }

            double[] q1;
            if (!TryParseNumbers(q1Text, 6, out q1, out err)) { err = "q1: " + err; return false; }

            double[] q2 = q1;
            if (!string.IsNullOrEmpty(q2Text) && !TryParseNumbers(q2Text, 6, out q2, out err)) { err = "q2: " + err; return false; }

            DistLoadSpec spec = new DistLoadSpec { LoadCase = lc };
            for (int i = 0; i < 3; i++)
            {
                spec.Q1[i] = units.Force(q1[i]);
                spec.Q2[i] = units.Force(q2[i]);
            }
            for (int i = 3; i < 6; i++)
            {
                spec.Q1[i] = units.Moment(q1[i]);
                spec.Q2[i] = units.Moment(q2[i]);
            }

            string fromText = Field(f, "from", "start", "");
            string toText = Field(f, "to", "end", "");

            bool fromIsRatio = true, toIsRatio = true;
            if (!string.IsNullOrEmpty(fromText) &&
                !TryParsePosition(fromText, units, out fromIsRatio, out spec.From, out err)) { err = "from: " + err; return false; }
            if (!string.IsNullOrEmpty(toText) &&
                !TryParsePosition(toText, units, out toIsRatio, out spec.To, out err)) { err = "to: " + err; return false; }

            if (string.IsNullOrEmpty(fromText) && string.IsNullOrEmpty(toText))
            {
                spec.From = 0.0;
                spec.To = 1.0;
                spec.PositionIsRatio = true;
            }
            else if (fromIsRatio != toIsRatio)
            {
                err = "from= and to= must both be ratios or both be lengths";
                return false;
            }
            else
            {
                spec.PositionIsRatio = fromIsRatio;
            }

            load = spec;
            err = null;
            return true;
        }

        /// <summary>Reads one Ax_PointLoad.n on a domain: "LC=Dead; at=1.2,3.4,0; F=0,0,-10,0,0,0".</summary>
        public static bool TryParseDomainPointLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(forces, 6, out raw, out err)) return false;

            string at = Field(f, "at", "pos", "position");
            if (string.IsNullOrEmpty(at)) { err = "no at= (position) given - a domain load needs a point"; return false; }

            RhinoXyz where;
            if (!TryParseVector(at, units, out where, out err)) { err = "at: " + err; return false; }

            PointLoadSpec spec = new PointLoadSpec { LoadCase = lc, At = where, HasAt = true };
            for (int i = 0; i < 3; i++) spec.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) spec.F[i] = units.Moment(raw[i]);

            load = spec;
            err = null;
            return true;
        }

        /// <summary>
        /// Reads one Ax_DistLoad.n on a domain: "LC=Dead; q=0,0,-2.5". Uniform only - AxisVM's
        /// non-uniform surface loads need a polygon this schema does not carry.
        /// </summary>
        public static bool TryParseDomainDistLoad(string value, AxUnits units, out DistLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string qText = Field(f, "q", "q1", "");
            if (string.IsNullOrEmpty(qText)) { err = "no q= (intensity) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(qText, 3, out raw, out err)) return false;

            DistLoadSpec spec = new DistLoadSpec { LoadCase = lc };
            for (int i = 0; i < 3; i++)
            {
                spec.Q1[i] = units.Force(raw[i]);
                spec.Q2[i] = spec.Q1[i];
            }

            load = spec;
            err = null;
            return true;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Parses "ratio:0.5", "len:2.4", a bare "0.5" (taken as a ratio), or "50%".
        /// A length comes back in metres.
        /// </summary>
        private static bool TryParsePosition(string text, AxUnits units, out bool isRatio, out double value, out string err)
        {
            isRatio = true;
            value = 0;
            err = null;

            string s = text.Trim();

            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                double pct;
                if (!double.TryParse(s.Substring(0, s.Length - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    err = string.Format("'{0}' is not a percentage", s);
                    return false;
                }
                value = pct / 100.0;
                return true;
            }

            int colon = s.IndexOf(':');
            string kind = colon > 0 ? s.Substring(0, colon).Trim().ToLowerInvariant() : "ratio";
            string number = colon > 0 ? s.Substring(colon + 1).Trim() : s;

            double raw;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out raw))
            {
                err = string.Format("'{0}' is not a number", number);
                return false;
            }

            switch (kind)
            {
                case "ratio":
                case "rel":
                case "r":
                    isRatio = true;
                    value = raw;
                    return true;

                case "len":
                case "length":
                case "abs":
                case "l":
                    isRatio = false;
                    value = units.Length(raw);
                    return true;

                default:
                    err = string.Format("'{0}' is not a position kind - use ratio: or len:", kind);
                    return false;
            }
        }

        /// <summary>First of up to three alternative field names that is present and non-empty.</summary>
        private static string Field(Dictionary<string, string> fields, string a, string b, string c)
        {
            string v;
            if (!string.IsNullOrEmpty(a) && fields.TryGetValue(a, out v) && !string.IsNullOrEmpty(v)) return v;
            if (!string.IsNullOrEmpty(b) && fields.TryGetValue(b, out v) && !string.IsNullOrEmpty(v)) return v;
            if (!string.IsNullOrEmpty(c) && fields.TryGetValue(c, out v) && !string.IsNullOrEmpty(v)) return v;
            return null;
        }
    }
}
