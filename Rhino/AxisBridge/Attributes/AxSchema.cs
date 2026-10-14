using System;
using System.Collections.Generic;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// Which attributes make sense on which kind of element.
    ///
    /// This one table drives three things so they can never disagree:
    ///   * the dialogs only show the fields that apply to the chosen element type,
    ///   * AttrWriter removes attributes that no longer apply after a type change
    ///     (e.g. a Beam turned into a Spring loses Ax_Material / Ax_Section),
    ///   * AxVisualize / AxLayerTidyUp use the same canonical type names.
    /// </summary>
    public static class AxSchema
    {
        // ------------------------------------------------------------------
        // Canonical type names
        // ------------------------------------------------------------------

        public const string Beam = "Beam";
        public const string Rib = "Rib";
        public const string Truss = "Truss";
        public const string Spring = "Spring";
        public const string RigidLink = "RigidLink";
        public const string NodeLink = "NodeLink";
        public const string EdgeHinge = "EdgeHinge";
        public const string Simple = "Simple";

        public const string Shell = "Shell";
        public const string Plate = "Plate";
        public const string Membrane = "Membrane";
        public const string LoadPanel = "LoadPanel";
        public const string OneWayLoadPanel = "OneWayLoadPanel";

        public static readonly string[] LineTypes = { Beam, Rib, Truss, Spring, RigidLink, NodeLink, EdgeHinge, Simple };
        public static readonly string[] SurfaceTypes = { Shell, Plate, Membrane, LoadPanel, OneWayLoadPanel };

        /// <summary>Human readable labels for the type dropdowns (same order as the arrays above).</summary>
        public static readonly string[] LineTypeLabels =
        {
            "Beam", "Rib (beam on a surface edge)", "Truss (axial only)", "Spring element",
            "Rigid link (rigid body)", "Node-node link (coupled DOFs)", "Edge hinge (surface edge release)",
            "Simple line (geometry / line support only)"
        };

        public static readonly string[] SurfaceTypeLabels =
        {
            "Shell domain", "Plate domain", "Membrane domain",
            "Load panel - two-way", "Load panel - one-way"
        };

        // ------------------------------------------------------------------
        // Applicability
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, HashSet<string>> LineKeys =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { Beam,      Set(AxKeys.Type, AxKeys.Material, AxKeys.Section, AxKeys.Release, AxKeys.LocalAxis, AxKeys.Eccentricity, AxKeys.PointLoad, AxKeys.DistLoad, AxKeys.Parts) },
                { Rib,       Set(AxKeys.Type, AxKeys.Material, AxKeys.Section, AxKeys.Release, AxKeys.LocalAxis, AxKeys.Eccentricity, AxKeys.PointLoad, AxKeys.DistLoad, AxKeys.Support, AxKeys.Parts) },
                // A truss is axial only: no releases, no orientation and no member loads in AxisVM.
                { Truss,     Set(AxKeys.Type, AxKeys.Material, AxKeys.Section, AxKeys.Parts) },
                { Spring,    Set(AxKeys.Type, AxKeys.Stiffness, AxKeys.Parts) },
                { RigidLink, Set(AxKeys.Type, AxKeys.Parts) },
                { NodeLink,  Set(AxKeys.Type, AxKeys.CoupledDof, AxKeys.Parts) },
                { EdgeHinge, Set(AxKeys.Type, AxKeys.Stiffness, AxKeys.HingeDomains, AxKeys.Parts) },
                { Simple,    Set(AxKeys.Type, AxKeys.Support, AxKeys.Parts) },
            };

        private static readonly Dictionary<string, HashSet<string>> SurfaceKeys =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { Shell,           Set(AxKeys.FeType, AxKeys.Material, AxKeys.Thickness, AxKeys.LocalAxis, AxKeys.Eccentricity, AxKeys.PointLoad, AxKeys.DistLoad, AxKeys.Parts) },
                { Plate,           Set(AxKeys.FeType, AxKeys.Material, AxKeys.Thickness, AxKeys.LocalAxis, AxKeys.Eccentricity, AxKeys.PointLoad, AxKeys.DistLoad, AxKeys.Parts) },
                { Membrane,        Set(AxKeys.FeType, AxKeys.Material, AxKeys.Thickness, AxKeys.LocalAxis, AxKeys.Eccentricity, AxKeys.PointLoad, AxKeys.DistLoad, AxKeys.Parts) },
                // A load panel is NOT a domain in AxisVM: no material, thickness, axis, eccentricity or parts.
                { LoadPanel,       Set(AxKeys.FeType, AxKeys.PointLoad, AxKeys.DistLoad) },
                { OneWayLoadPanel, Set(AxKeys.FeType, AxKeys.SpanDirection, AxKeys.EdgeSpacing, AxKeys.PointLoad, AxKeys.DistLoad) },
            };

        private static readonly HashSet<string> PointKeys = Set(AxKeys.Support, AxKeys.NodalLoad, AxKeys.Parts);

        private static HashSet<string> Set(params string[] keys)
        {
            return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Maps any accepted spelling ("rigid link", "nodenodelink", ...) to the canonical line type, or null.</summary>
        public static string NormalizeLineType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string k = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            switch (k)
            {
                case "beam": return Beam;
                case "rib": return Rib;
                case "truss": return Truss;
                case "spring": return Spring;
                case "rigidlink": case "rigid": case "rigidbody": return RigidLink;
                case "nodelink": case "nodenodelink": case "nnlink": case "link": return NodeLink;
                case "edgehinge": case "hinge": return EdgeHinge;
                case "simple": case "simpleline": case "line": return Simple;
                default: return null;
            }
        }

        /// <summary>Maps any accepted spelling to the canonical surface type, or null.</summary>
        public static string NormalizeSurfaceType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string k = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            switch (k)
            {
                case "shell": return Shell;
                case "plate": return Plate;
                case "membrane": return Membrane;
                case "loadpanel": case "panel": case "twowayloadpanel": case "loadpaneltwoway": return LoadPanel;
                case "onewayloadpanel": case "onewaypanel": case "loadpaneloneway": case "oneway": return OneWayLoadPanel;
                default: return null;
            }
        }

        public static bool IsLoadPanel(string canonicalSurfaceType)
        {
            return canonicalSurfaceType == LoadPanel || canonicalSurfaceType == OneWayLoadPanel;
        }

        /// <summary>Whether a (base) key belongs on a line of this type. Unknown/absent type keeps everything.</summary>
        public static bool LineKeyApplies(string type, string baseKey)
        {
            if (IsAlwaysKept(baseKey)) return true;
            HashSet<string> keys;
            string t = NormalizeLineType(type);
            if (t == null || !LineKeys.TryGetValue(t, out keys)) return true;
            return keys.Contains(baseKey);
        }

        /// <summary>Whether a (base) key belongs on a surface of this type. Unknown/absent type keeps everything.</summary>
        public static bool SurfaceKeyApplies(string feType, string baseKey)
        {
            if (IsAlwaysKept(baseKey)) return true;
            HashSet<string> keys;
            string t = NormalizeSurfaceType(feType);
            if (t == null || !SurfaceKeys.TryGetValue(t, out keys)) return true;
            return keys.Contains(baseKey);
        }

        public static bool PointKeyApplies(string baseKey)
        {
            return IsAlwaysKept(baseKey) || PointKeys.Contains(baseKey);
        }

        /// <summary>Ax_Guid and anything this version does not know are never removed by the cleanup.</summary>
        private static bool IsAlwaysKept(string baseKey)
        {
            return string.Equals(baseKey, AxKeys.Guid, StringComparison.OrdinalIgnoreCase) || !AxKeys.IsKnown(baseKey);
        }
    }
}
