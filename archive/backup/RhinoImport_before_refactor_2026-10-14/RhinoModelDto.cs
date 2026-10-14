using System;
using System.Collections.Generic;
using System.Globalization;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// The attribute key names shared with the Rhino side. Both AxisBridge.rhp and this addon must
    /// agree on these strings exactly, so they live in one place on each side and are kept identical.
    ///
    /// Every key is prefixed "Ax_" so nothing collides with the other user text a Rhino object might
    /// carry (rendering settings, BIM exports, the user's own notes). Repeatable keys - the loads -
    /// take a ".1", ".2", ... suffix, because Rhino user text is a flat string->string dictionary and
    /// a single key cannot hold two loads on one beam.
    /// </summary>
    public static class AxKeys
    {
        public const string Prefix = "Ax_";

        // Common
        public const string Guid = "Ax_Guid";
        public const string Parts = "Ax_Parts";

        // Point
        public const string Support = "Ax_Support";
        public const string NodalLoad = "Ax_NodalLoad";     // repeatable

        // Line
        public const string Type = "Ax_Type";
        public const string Material = "Ax_Material";
        public const string Section = "Ax_Section";
        public const string Release = "Ax_Release";
        public const string LocalAxis = "Ax_LocalAxis";
        public const string Eccentricity = "Ax_Eccentricity";
        public const string PointLoad = "Ax_PointLoad";     // repeatable
        public const string DistLoad = "Ax_DistLoad";       // repeatable
        public const string Stiffness = "Ax_Stiffness";
        public const string HingeDomains = "Ax_HingeDomains";
        public const string CoupledDof = "Ax_CoupledDOF";

        // Domain
        public const string FeType = "Ax_FEType";
        public const string Thickness = "Ax_Thickness";
        public const string SpanDirection = "Ax_SpanDirection";
        public const string EdgeSpacing = "Ax_EdgeSpacing";

        /// <summary>Every key this version understands, used to warn about typos in the Rhino attributes.</summary>
        public static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Guid, Parts,
            Support, NodalLoad,
            Type, Material, Section, Release, LocalAxis, Eccentricity, PointLoad, DistLoad, Stiffness, HingeDomains, CoupledDof,
            FeType, Thickness, SpanDirection, EdgeSpacing
        };

        /// <summary>
        /// Strips a repeat suffix so "Ax_PointLoad.2" is recognised as "Ax_PointLoad". Keys with no
        /// suffix come back unchanged.
        /// </summary>
        public static string BaseKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1) return key;

            // Only a numeric tail counts as a repeat suffix - a key really containing a dot is left alone.
            for (int i = dot + 1; i < key.Length; i++)
                if (!char.IsDigit(key[i])) return key;

            return key.Substring(0, dot);
        }

        public static bool IsKnown(string key)
        {
            string baseKey = BaseKey(key);
            return Known.Contains(baseKey);
        }
    }

    /// <summary>A 3d point in Rhino document units, straight out of the JSON.</summary>
    public struct RhinoXyz
    {
        public double X, Y, Z;

        public RhinoXyz(double x, double y, double z) { X = x; Y = y; Z = z; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", X, Y, Z);
        }
    }

    /// <summary>Anything the exporter tagged: a bag of Ax_* attributes plus the Rhino object id.</summary>
    public abstract class RhinoEntity
    {
        public string Guid = "";

        /// <summary>Raw attribute values keyed exactly as they appear in Rhino's user text.</summary>
        public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads one attribute, or null when absent. "-" or "auto" means "deliberately default / none" and reads as null.</summary>
        public string Attr(string key)
        {
            string v;
            if (!Attrs.TryGetValue(key, out v)) return null;
            if (v == null) return null;
            v = v.Trim();
            if (v.Length == 0 || v == "-" || v == "--" || string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)) return null;
            return v;
        }

        /// <summary>
        /// All values of a repeatable key, in suffix order: Ax_PointLoad, Ax_PointLoad.1, Ax_PointLoad.2...
        /// The bare key is accepted too, so a single load need not be numbered.
        /// </summary>
        public List<string> AttrList(string baseKey)
        {
            List<string> hits = new List<string>();
            List<string> keys = new List<string>(Attrs.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string k in keys)
            {
                if (!string.Equals(AxKeys.BaseKey(k), baseKey, StringComparison.OrdinalIgnoreCase)) continue;
                string v = Attr(k);
                if (v != null) hits.Add(v);
            }
            return hits;
        }

        /// <summary>Short label for the run report, e.g. "line 3f2a1c…".</summary>
        public string Label(string kind)
        {
            string shortGuid = Guid != null && Guid.Length >= 8 ? Guid.Substring(0, 8) : Guid;
            return kind + " " + shortGuid;
        }
    }

    public class RhinoPoint : RhinoEntity
    {
        public RhinoXyz Xyz;
    }

    public class RhinoLine : RhinoEntity
    {
        public RhinoXyz Start;
        public RhinoXyz End;

        /// <summary>"line" or "arc". Arcs additionally carry a third point on the arc.</summary>
        public string Geom = "line";
        public RhinoXyz ArcMid;
        public bool HasArcMid;
    }

    public class RhinoDomain : RhinoEntity
    {
        /// <summary>
        /// The closed border, already flattened Rhino-side (DupBorder then explode) into consecutive
        /// vertices. Consecutive pairs are the edges; the last vertex joins back to the first.
        /// </summary>
        public List<RhinoXyz> Contour = new List<RhinoXyz>();
    }

    /// <summary>A whole exported Rhino document.</summary>
    public class RhinoModel
    {
        public int Schema = 1;
        public string SourceFile = "";

        /// <summary>Rhino's own unit name ("Millimeters", "Meters", ...) - pre-selects the import dialog.</summary>
        public string UnitSystem = "";

        /// <summary>Rhino document absolute tolerance, in Rhino document units.</summary>
        public double Tolerance = 0.001;

        public List<RhinoPoint> Points = new List<RhinoPoint>();
        public List<RhinoLine> Lines = new List<RhinoLine>();
        public List<RhinoDomain> Domains = new List<RhinoDomain>();

        /// <summary>Warnings the exporter itself raised, carried through into the import report.</summary>
        public List<string> ExportWarnings = new List<string>();

        // ==================================================================
        // Loading
        // ==================================================================

        /// <summary>
        /// Builds a RhinoModel from a parsed JSON tree. Structural problems throw; per-entity problems
        /// are appended to <paramref name="notes"/> and that entity is skipped, so one bad object in a
        /// large export does not cost the whole import.
        /// </summary>
        public static RhinoModel FromJson(JsonValue root, List<string> notes)
        {
            if (root == null || root.Type != JsonValue.JsonType.Object)
                throw new FormatException("The file's top level is not a JSON object - this is not an AxisBridge export.");

            RhinoModel m = new RhinoModel();

            JsonValue schema = root["schema"];
            if (schema == null)
                throw new FormatException("The file has no \"schema\" field - this is not an AxisBridge export.");
            m.Schema = schema.AsInt(0);
            if (m.Schema != 1)
                notes.Add(string.Format("The file declares schema {0} but this addon understands schema 1. Reading it anyway.", m.Schema));

            JsonValue source = root["source"];
            if (source != null)
            {
                m.SourceFile = Get(source, "file", "");
                m.UnitSystem = Get(source, "unitSystem", "");
                JsonValue tol = source["tolerance"];
                if (tol != null) m.Tolerance = tol.AsDouble(0.001);
            }

            JsonValue warnings = root["warnings"];
            if (warnings != null && warnings.Type == JsonValue.JsonType.Array)
                for (int i = 0; i < warnings.Count; i++)
                    m.ExportWarnings.Add(warnings.At(i).AsString(""));

            ReadPoints(root["points"], m, notes);
            ReadLines(root["lines"], m, notes);
            ReadDomains(root["domains"], m, notes);

            return m;
        }

        private static void ReadPoints(JsonValue arr, RhinoModel m, List<string> notes)
        {
            if (arr == null || arr.Type != JsonValue.JsonType.Array) return;

            for (int i = 0; i < arr.Count; i++)
            {
                JsonValue j = arr.At(i);
                RhinoPoint p = new RhinoPoint();
                p.Guid = Get(j, "guid", "");
                ReadAttrs(j["attrs"], p);

                RhinoXyz xyz;
                if (!TryXyz(j["xyz"], out xyz))
                {
                    notes.Add(string.Format("points[{0}]: no usable \"xyz\" - skipped.", i));
                    continue;
                }
                p.Xyz = xyz;
                m.Points.Add(p);
            }
        }

        private static void ReadLines(JsonValue arr, RhinoModel m, List<string> notes)
        {
            if (arr == null || arr.Type != JsonValue.JsonType.Array) return;

            for (int i = 0; i < arr.Count; i++)
            {
                JsonValue j = arr.At(i);
                RhinoLine l = new RhinoLine();
                l.Guid = Get(j, "guid", "");
                l.Geom = Get(j, "geom", "line");
                ReadAttrs(j["attrs"], l);

                RhinoXyz a, b;
                if (!TryXyz(j["start"], out a) || !TryXyz(j["end"], out b))
                {
                    notes.Add(string.Format("lines[{0}]: no usable \"start\"/\"end\" - skipped.", i));
                    continue;
                }
                l.Start = a;
                l.End = b;

                RhinoXyz mid;
                if (TryXyz(j["arcMid"], out mid)) { l.ArcMid = mid; l.HasArcMid = true; }

                m.Lines.Add(l);
            }
        }

        private static void ReadDomains(JsonValue arr, RhinoModel m, List<string> notes)
        {
            if (arr == null || arr.Type != JsonValue.JsonType.Array) return;

            for (int i = 0; i < arr.Count; i++)
            {
                JsonValue j = arr.At(i);
                RhinoDomain d = new RhinoDomain();
                d.Guid = Get(j, "guid", "");
                ReadAttrs(j["attrs"], d);

                JsonValue contour = j["contour"];
                if (contour != null && contour.Type == JsonValue.JsonType.Array)
                {
                    for (int k = 0; k < contour.Count; k++)
                    {
                        RhinoXyz v;
                        if (TryXyz(contour.At(k), out v)) d.Contour.Add(v);
                    }
                }

                if (d.Contour.Count < 3)
                {
                    notes.Add(string.Format("domains[{0}]: its contour has {1} vertices, fewer than the 3 a domain needs - skipped.",
                        i, d.Contour.Count));
                    continue;
                }

                // The exporter may or may not repeat the first vertex to close the loop; drop the
                // duplicate so edge generation can simply wrap around.
                RhinoXyz first = d.Contour[0];
                RhinoXyz last = d.Contour[d.Contour.Count - 1];
                if (Math.Abs(first.X - last.X) < 1e-9 && Math.Abs(first.Y - last.Y) < 1e-9 && Math.Abs(first.Z - last.Z) < 1e-9)
                    d.Contour.RemoveAt(d.Contour.Count - 1);

                if (d.Contour.Count < 3)
                {
                    notes.Add(string.Format("domains[{0}]: its contour collapses to fewer than 3 distinct vertices - skipped.", i));
                    continue;
                }

                m.Domains.Add(d);
            }
        }

        private static void ReadAttrs(JsonValue attrs, RhinoEntity target)
        {
            if (attrs == null || attrs.Type != JsonValue.JsonType.Object || attrs.ObjectValue == null) return;

            foreach (KeyValuePair<string, JsonValue> kv in attrs.ObjectValue)
                target.Attrs[kv.Key] = kv.Value.AsString("");
        }

        /// <summary>Accepts a coordinate as either [x,y,z] or {"x":..,"y":..,"z":..}.</summary>
        private static bool TryXyz(JsonValue j, out RhinoXyz xyz)
        {
            xyz = new RhinoXyz();
            if (j == null) return false;

            if (j.Type == JsonValue.JsonType.Array && j.Count >= 3)
            {
                xyz = new RhinoXyz(j.At(0).AsDouble(0), j.At(1).AsDouble(0), j.At(2).AsDouble(0));
                return true;
            }

            if (j.Type == JsonValue.JsonType.Object && j["x"] != null)
            {
                xyz = new RhinoXyz(j["x"].AsDouble(0), j["y"].AsDouble(0), j["z"].AsDouble(0));
                return true;
            }

            return false;
        }

        private static string Get(JsonValue obj, string key, string fallback)
        {
            if (obj == null) return fallback;
            JsonValue v = obj[key];
            return v == null ? fallback : v.AsString(fallback);
        }
    }
}
