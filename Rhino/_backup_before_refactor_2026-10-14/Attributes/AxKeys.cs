using System;
using System.Collections.Generic;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// Constants and validation for structural user text attributes.
    /// Prefixed with Ax_ so they never collide with rendering or BIM user strings in Rhino.
    /// </summary>
    public static class AxKeys
    {
        public const string Prefix = "Ax_";

        // Identification
        public const string Guid = "Ax_Guid";

        // Point attributes
        public const string Support = "Ax_Support";
        public const string NodalLoad = "Ax_NodalLoad";

        // Line attributes
        public const string Type = "Ax_Type";
        public const string Material = "Ax_Material";
        public const string Section = "Ax_Section";
        public const string Release = "Ax_Release";
        public const string LocalAxis = "Ax_LocalAxis";
        public const string Eccentricity = "Ax_Eccentricity";
        public const string PointLoad = "Ax_PointLoad";
        public const string DistLoad = "Ax_DistLoad";
        public const string Stiffness = "Ax_Stiffness";
        public const string HingeDomains = "Ax_HingeDomains";
        public const string CoupledDof = "Ax_CoupledDOF";

        // Domain attributes
        public const string FeType = "Ax_FEType";
        public const string Thickness = "Ax_Thickness";
        public const string SpanDirection = "Ax_SpanDirection";
        public const string EdgeSpacing = "Ax_EdgeSpacing";

        // Common
        public const string Parts = "Ax_Parts";

        private static readonly HashSet<string> ExactKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Guid, Support, Type, Material, Section, Release, LocalAxis, Eccentricity,
            Stiffness, HingeDomains, CoupledDof, FeType, Thickness, SpanDirection, EdgeSpacing, Parts
        };

        private static readonly string[] SuffixKeys =
        {
            NodalLoad, PointLoad, DistLoad
        };

        public static bool IsKnown(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (ExactKeys.Contains(key)) return true;

            foreach (string prefix in SuffixKeys)
            {
                if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                if (key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
    }
}
