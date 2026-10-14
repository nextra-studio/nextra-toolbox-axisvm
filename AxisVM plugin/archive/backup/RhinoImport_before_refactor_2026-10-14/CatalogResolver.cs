using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Turns the names written in the Rhino attributes - a material, a cross-section, a load case - into
    /// AxisVM indices, creating what is missing and falling back to something sensible when even the
    /// catalog cannot supply it.
    ///
    /// Every lookup is cached by name, because a thousand-beam import would otherwise scan the load
    /// case list a thousand times (AxisVM's load case collection has no IndexOf, only a name scan).
    /// Every fallback appends a note, so the run summary always says what was substituted for what.
    /// </summary>
    public class CatalogResolver
    {
        private readonly IAxisVMModel model;
        private readonly List<string> notes;

        private readonly Dictionary<string, int> materials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> sections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> loadCases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> references = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Catalog shapes tried, in order, when a section name is not already in the model.</summary>
        private static readonly ECrossSectionShapeEx[] CatalogShapes =
        {
            ECrossSectionShapeEx.csseI,
            ECrossSectionShapeEx.csseU,
            ECrossSectionShapeEx.csseL,
            ECrossSectionShapeEx.csseT,
            ECrossSectionShapeEx.cssePipe,
            ECrossSectionShapeEx.csseBox,
            ECrossSectionShapeEx.csseRectangleHollow,
            ECrossSectionShapeEx.csseCircleHollow,
            ECrossSectionShapeEx.csseC,
            ECrossSectionShapeEx.csseZ
        };

        /// <summary>Design code used when pulling a material out of the AxisVM catalog.</summary>
        private const ENationalDesignCode Code = ENationalDesignCode.ndcEuroCode;

        /// <summary>Cross-section created as a last resort, so a beam is never left without one [m].</summary>
        private const string FallbackSectionName = "AxImport-default 200x400";
        private const double FallbackSectionB = 0.200;
        private const double FallbackSectionH = 0.400;

        /// <summary>Material created as a last resort when the model is completely empty.</summary>
        private const string FallbackMaterialName = "C25/30";

        public CatalogResolver(IAxisVMModel model, List<string> notes)
        {
            this.model = model;
            this.notes = notes;
        }

        // ==================================================================
        // Materials
        // ==================================================================

        /// <summary>
        /// Resolves a material name: already in the model, else pulled from the EuroCode catalog, else
        /// the model's first material, else a freshly created C25/30. Returns 0 only if all of that
        /// fails, which the caller treats as "leave the element's material alone".
        /// </summary>
        public int Material(string name, string context)
        {
            string key = string.IsNullOrEmpty(name) ? "" : name.Trim();

            int cached;
            if (materials.TryGetValue(key, out cached)) return cached;

            int id = 0;

            if (key.Length > 0)
            {
                try { id = model.Materials.IndexOf(key); } catch { id = 0; }

                if (id <= 0)
                {
                    try { id = model.Materials.AddFromCatalog(Code, key); } catch { id = 0; }
                    if (id > 0) notes.Add(string.Format("Material '{0}' was added from the AxisVM catalog.", key));
                }
            }

            if (id <= 0)
            {
                int fallback = FallbackMaterial();
                if (fallback > 0 && key.Length > 0)
                {
                    string fallbackName = "?";
                    try { fallbackName = model.Materials.Item[fallback].Name; } catch { }
                    notes.Add(string.Format("{0}: material '{1}' is not in the model or the catalog - '{2}' used instead.",
                        context, key, fallbackName));
                }
                id = fallback;
            }

            materials[key] = id;
            return id;
        }

        private int fallbackMaterialId = -1;

        private int FallbackMaterial()
        {
            if (fallbackMaterialId >= 0) return fallbackMaterialId;

            int count = 0;
            try { count = model.Materials.Count; } catch { }

            if (count > 0) { fallbackMaterialId = 1; return fallbackMaterialId; }

            // Nothing at all in the model - put one concrete in so beams and domains are still valid.
            int id = 0;
            try { id = model.Materials.AddFromCatalog(Code, FallbackMaterialName); } catch { id = 0; }
            if (id > 0)
                notes.Add(string.Format("The model had no materials; '{0}' was added from the catalog as the default.", FallbackMaterialName));
            else
                notes.Add("The model has no materials and none could be added from the catalog. Elements were left without one.");

            fallbackMaterialId = id;
            return id;
        }

        // ==================================================================
        // Cross-sections
        // ==================================================================

        /// <summary>
        /// Resolves a cross-section name: already in the model, else parsed as a dimension string
        /// (e.g. "500x500", "300x400", "100 x 300", "500 X 500") to create a rectangular section, else searched
        /// through catalog shapes (e.g. "IPE400", "HEA200"), else fallback.
        /// </summary>
        public int Section(string name, string context)
        {
            string key = string.IsNullOrEmpty(name) ? "" : name.Trim();

            int cached;
            if (sections.TryGetValue(key, out cached)) return cached;

            int id = 0;

            if (key.Length > 0)
            {
                // 1. Check if already present in the active model
                try { id = model.CrossSections.IndexOf(key); } catch { id = 0; }

                // 2. Check if name is a rectangular dimension expression (e.g. "500x500", "300x400", "100 x 300")
                if (id <= 0)
                {
                    double b_m, h_m;
                    string normName;
                    if (TryParseRectangularDimensions(key, out b_m, out h_m, out normName))
                    {
                        try { id = model.CrossSections.IndexOf(normName); } catch { id = 0; }
                        if (id <= 0)
                        {
                            try
                            {
                                id = model.CrossSections.AddRectangular(normName, b_m, h_m, ECrossSectionProcess.cspOther);
                            }
                            catch { id = 0; }

                            if (id <= 0)
                            {
                                try
                                {
                                    id = model.CrossSections.AddRectangular(normName, b_m, h_m, ECrossSectionProcess.cspRolled);
                                }
                                catch { id = 0; }
                            }

                            if (id <= 0)
                            {
                                try { id = model.CrossSections.IndexOf(normName); } catch { id = 0; }
                            }

                            if (id > 0)
                            {
                                notes.Add(string.Format("Rectangular cross-section '{0}' (width={1:0.###} m, depth={2:0.###} m) was created.", normName, b_m, h_m));
                            }
                        }
                    }
                }

                // 3. Search AxisVM standard catalogs across shapes (e.g. IPE400, HEA200, UPN200, etc.)
                if (id <= 0)
                {
                    string[] nameVariants = GetCatalogNameVariants(key);
                    foreach (string candidate in nameVariants)
                    {
                        foreach (ECrossSectionShapeEx shape in CatalogShapes)
                        {
                            try { id = model.CrossSections.AddFromCatalog_V161(shape, candidate); } catch { id = 0; }
                            if (id > 0)
                            {
                                notes.Add(string.Format("Cross-section '{0}' was added from the AxisVM catalog (as '{1}').", key, candidate));
                                break;
                            }
                        }
                        if (id > 0) break;
                    }
                }
            }

            if (id <= 0)
            {
                int fallback = FallbackSection();
                if (fallback > 0 && key.Length > 0)
                    notes.Add(string.Format("{0}: cross-section '{1}' is not in the model or the catalog - '{2}' used instead.",
                        context, key, FallbackSectionName));
                id = fallback;
            }

            sections[key] = id;
            return id;
        }

        /// <summary>
        /// Detects expressions like "500x500", "300x400", "100 x 300", "500 X 500", "600X300", "REC 300x500", "300*500", "300/500"
        /// and converts them into width (b) and height (h) in metres.
        /// </summary>
        public static bool TryParseRectangularDimensions(string text, out double b_m, out double h_m, out string normalizedName)
        {
            b_m = 0;
            h_m = 0;
            normalizedName = "";
            if (string.IsNullOrEmpty(text)) return false;

            string s = text.Trim();
            string[] prefixes = { "RECTANGULAR ", "RECTANGLE ", "RECT ", "REC ", "R ", "B ", "COL ", "COLUMN ", "BEAM " };
            foreach (string p in prefixes)
            {
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(p.Length).Trim();
                    break;
                }
            }

            char[] delims = { 'x', 'X', '*', '/', '×' };
            int delimIdx = s.IndexOfAny(delims);
            if (delimIdx <= 0 || delimIdx >= s.Length - 1) return false;

            string part1 = s.Substring(0, delimIdx).Trim();
            string part2 = s.Substring(delimIdx + 1).Trim();

            double dim1, dim2;
            if (!double.TryParse(part1, NumberStyles.Float, CultureInfo.InvariantCulture, out dim1) ||
                !double.TryParse(part2, NumberStyles.Float, CultureInfo.InvariantCulture, out dim2))
            {
                if (!double.TryParse(part1.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out dim1) ||
                    !double.TryParse(part2.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out dim2))
                {
                    return false;
                }
            }

            if (dim1 <= 0 || dim2 <= 0) return false;

            // Numbers >= 5 are treated as millimeters (e.g. 500x500 -> 0.5m x 0.5m)
            if (dim1 >= 5.0 || dim2 >= 5.0)
            {
                b_m = dim1 / 1000.0;
                h_m = dim2 / 1000.0;
                normalizedName = string.Format(CultureInfo.InvariantCulture, "{0:0.#}x{1:0.#}", dim1, dim2);
            }
            else
            {
                b_m = dim1;
                h_m = dim2;
                normalizedName = string.Format(CultureInfo.InvariantCulture, "{0:0.###}x{1:0.###}m", dim1, dim2);
            }

            return true;
        }

        private static string[] GetCatalogNameVariants(string key)
        {
            List<string> variants = new List<string>();
            variants.Add(key);

            string noSpaces = key.Replace(" ", "");
            if (!variants.Contains(noSpaces)) variants.Add(noSpaces);

            // e.g. "IPE400" -> "IPE 400"
            for (int i = 1; i < key.Length; i++)
            {
                if (char.IsLetter(key[i - 1]) && char.IsDigit(key[i]))
                {
                    string spaced = key.Substring(0, i) + " " + key.Substring(i);
                    if (!variants.Contains(spaced)) variants.Add(spaced);
                    break;
                }
            }

            // UPN vs UNP
            if (key.StartsWith("UNP", StringComparison.OrdinalIgnoreCase))
                variants.Add("UPN" + key.Substring(3));
            else if (key.StartsWith("UPN", StringComparison.OrdinalIgnoreCase))
                variants.Add("UNP" + key.Substring(3));

            return variants.ToArray();
        }

        private int fallbackSectionId = -1;

        private int FallbackSection()
        {
            if (fallbackSectionId >= 0) return fallbackSectionId;

            int id = 0;
            try { id = model.CrossSections.IndexOf(FallbackSectionName); } catch { id = 0; }

            if (id <= 0)
            {
                try
                {
                    id = model.CrossSections.AddRectangular(FallbackSectionName, FallbackSectionB, FallbackSectionH,
                                                            ECrossSectionProcess.cspOther);
                }
                catch { id = 0; }
            }

            if (id <= 0)
            {
                // Last resort: whatever the model already had.
                int count = 0;
                try { count = model.CrossSections.Count; } catch { }
                if (count > 0) id = 1;
            }

            fallbackSectionId = id;
            return id;
        }

        // ==================================================================
        // Load cases
        // ==================================================================

        /// <summary>
        /// Resolves a load case by name, creating a standard case when it does not exist yet.
        /// AxisVM's load case collection has no IndexOf, so the lookup is a name scan.
        /// </summary>
        public int LoadCase(string name)
        {
            string key = string.IsNullOrEmpty(name) ? "" : name.Trim();

            int cached;
            if (key.Length > 0 && loadCases.TryGetValue(key, out cached)) return cached;

            int id = 0;

            if (key.Length > 0)
            {
                id = FindLoadCaseByName(key);

                if (id <= 0)
                {
                    try { id = model.LoadCases.Add(key, ELoadCaseType.lctStandard); } catch { id = 0; }
                    if (id > 0) notes.Add(string.Format("Load case '{0}' did not exist and was created.", key));
                }
            }

            if (id <= 0)
            {
                // Fallback: if model has any load case, use item 1; otherwise create "Dead"
                int count = 0;
                try { count = model.LoadCases.Count; } catch { }

                if (count > 0)
                {
                    id = 1;
                    if (key.Length > 0)
                    {
                        string fallbackName = "?";
                        try { fallbackName = model.LoadCases.get_Name(1); } catch { }
                        notes.Add(string.Format("Load case '{0}' could not be created; assigned to existing case '{1}' instead.", key, fallbackName));
                    }
                }
                else
                {
                    string fallbackLc = key.Length > 0 ? key : "Dead";
                    try { id = model.LoadCases.Add(fallbackLc, ELoadCaseType.lctStandard); } catch { id = 0; }
                    if (id > 0) notes.Add(string.Format("Created default load case '{0}'.", fallbackLc));
                }
            }

            if (key.Length > 0 && id > 0) loadCases[key] = id;
            return id;
        }

        private int FindLoadCaseByName(string name)
        {
            try
            {
                int count = model.LoadCases.Count;
                for (int i = 1; i <= count; i++)
                {
                    if (string.Equals(model.LoadCases.get_Name(i), name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return 0;
        }

        // ==================================================================
        // Reference vectors
        // ==================================================================

        /// <summary>
        /// Resolves an Ax_LocalAxis direction to a reference vector index, reusing one reference per
        /// distinct direction so a thousand identically-oriented beams share a single entry.
        /// </summary>
        public int ReferenceVector(RhinoXyz direction)
        {
            string key = string.Format(CultureInfo.InvariantCulture, "{0:0.######}|{1:0.######}|{2:0.######}",
                                       direction.X, direction.Y, direction.Z);

            int cached;
            if (references.TryGetValue(key, out cached)) return cached;

            int id = 0;
            try { id = AxisVMHelper.CreateReferenceVector(model, direction.X, direction.Y, direction.Z); }
            catch { id = 0; }

            if (id <= 0) notes.Add(string.Format("A reference vector for direction {0} could not be created.", direction));

            references[key] = id;
            return id;
        }
    }
}
