using System;
using System.Collections.Generic;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// Constants for the structural user text attributes.
    /// Prefixed with Ax_ so they never collide with rendering or BIM user strings in Rhino.
    ///
    /// The AxisVM addon (RhinoImportAddon, Core/RhinoModelDto.cs) keeps an identical copy of these
    /// names - change both sides together.
    /// </summary>
    public static class AxKeys
    {
        public const string Prefix = "Ax_";

        // Identification
        public const string Guid = "Ax_Guid";

        // Point attributes (Support is also used on curves = line support)
        public const string Support = "Ax_Support";
        public const string NodalLoad = "Ax_NodalLoad";     // repeatable: Ax_NodalLoad, Ax_NodalLoad.1, ...

        // Line attributes
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

        // Surface attributes (domains and load panels)
        public const string FeType = "Ax_FEType";
        public const string Thickness = "Ax_Thickness";
        public const string SpanDirection = "Ax_SpanDirection";
        public const string EdgeSpacing = "Ax_EdgeSpacing";
        public const string SpanTolerance = "Ax_SpanTolerance"; // one-way panel: degrees off 90 deg a receiving member may be
        public const string LoadTargets = "Ax_LoadTargets";   // two-way load panel: ids of the only curves/points receiving its load

        // Common
        public const string Parts = "Ax_Parts";
        public const string Diaphragm = "Ax_Diaphragm";       // rigid diaphragm name on curves / surfaces; equal names = one diaphragm

        /// <summary>Keys that may repeat with a numeric suffix (one load per key).</summary>
        public static readonly string[] Families = { NodalLoad, PointLoad, DistLoad };

        private static readonly HashSet<string> ExactKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Guid, Support, Type, Material, Section, Release, LocalAxis, Eccentricity,
            Stiffness, HingeDomains, CoupledDof, FeType, Thickness, SpanDirection, EdgeSpacing, SpanTolerance, LoadTargets, Parts, Diaphragm
        };

        /// <summary>
        /// Strips a repeat suffix so "Ax_PointLoad.2" is recognised as "Ax_PointLoad". Only a purely
        /// numeric tail counts as a suffix; any other key comes back unchanged.
        /// </summary>
        public static string BaseKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1) return key;
            for (int i = dot + 1; i < key.Length; i++)
                if (!char.IsDigit(key[i])) return key;
            return key.Substring(0, dot);
        }

        /// <summary>Numeric suffix of a repeatable key: "Ax_DistLoad" = 0, "Ax_DistLoad.3" = 3.</summary>
        public static int SuffixOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            int dot = key.LastIndexOf('.');
            if (dot <= 0) return 0;
            int n;
            return int.TryParse(key.Substring(dot + 1), out n) ? n : 0;
        }

        /// <summary>The key used for the i-th load of a family: 0 = bare key, then ".1", ".2", ...</summary>
        public static string FamilyKey(string baseKey, int index)
        {
            return index == 0 ? baseKey : baseKey + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool IsFamily(string baseKey)
        {
            foreach (string f in Families)
                if (string.Equals(f, baseKey, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsKnown(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string b = BaseKey(key);
            return ExactKeys.Contains(b) || IsFamily(b);
        }
    }
}
