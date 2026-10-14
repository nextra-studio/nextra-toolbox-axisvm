using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Turns the names written in the Rhino attributes - a material, a cross-section, a load case - into
    /// AxisVM indices, creating what is missing and falling back to something sensible when even the
    /// catalog cannot supply it.
    ///
    /// Names are matched forgivingly: "S355", "s355" and "S 355" are the same material, because the
    /// AxisVM catalog spells grades with a space ("S 355", "C 30/37") while people rarely type one.
    /// Every lookup is cached by the name as written, and every substitution adds a note so the run
    /// summary says what was used for what.
    /// </summary>
    public class CatalogResolver
    {
        private readonly IAxisVMModel model;
        private readonly List<string> notes;
        private readonly RhinoLibrary library;

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
        private const string FallbackMaterialName = "C30/37";

        public CatalogResolver(IAxisVMModel model, List<string> notes, RhinoLibrary library)
        {
            this.model = model;
            this.notes = notes;
            this.library = library ?? new RhinoLibrary();
        }

        /// <summary>"S 355", "s355", "S-355" -> "S355": the key used to compare names forgivingly.</summary>
        public static string Compact(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (!char.IsWhiteSpace(c) && c != '-' && c != '_') sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// Spellings to try against the catalog: as typed, without spaces, and with a space between
        /// the leading letters and the first digit ("S355" -> "S 355", "C30/37" -> "C 30/37",
        /// "GL24h" -> "GL 24h", "IPE300" -> "IPE 300").
        /// </summary>
        public static List<string> NameVariants(string name)
        {
            List<string> v = new List<string>();
            Action<string> add = s => { if (!string.IsNullOrEmpty(s) && !v.Contains(s)) v.Add(s); };

            string t = (name ?? "").Trim();
            add(t);
            string noSpace = t.Replace(" ", "");
            add(noSpace);

            Match m = Regex.Match(noSpace, @"^([A-Za-z]+)(\d.*)$");
            if (m.Success)
            {
                add(m.Groups[1].Value + " " + m.Groups[2].Value);
                add(m.Groups[1].Value.ToUpperInvariant() + " " + m.Groups[2].Value);
                add(m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value);
            }
            add(t.ToUpperInvariant());
            return v;
        }

        // ==================================================================
        // Materials
        // ==================================================================

        /// <summary>
        /// Resolves a material name: already in the model (ignoring spaces/case), else pulled from the
        /// EuroCode catalog under any spelling variant, else the model's first material, else a fresh
        /// C30/37. Returns 0 only if all of that fails.
        /// </summary>
        public int Material(string name, string context)
        {
            string key = string.IsNullOrEmpty(name) ? "" : name.Trim();

            int cached;
            if (materials.TryGetValue(key, out cached)) return cached;

            int id = 0;

            if (key.Length > 0)
            {
                id = FindMaterialInModel(key);

                if (id <= 0)
                {
                    foreach (string candidate in NameVariants(key))
                    {
                        try { id = model.Materials.AddFromCatalog(Code, candidate); } catch { id = 0; }
                        if (id > 0)
                        {
                            notes.Add(string.Format("Material '{0}' was added from the AxisVM catalog{1}.", key,
                                candidate == key ? "" : " (as '" + candidate + "')"));
                            break;
                        }
                    }
                }
            }

            if (id <= 0)
            {
                int fallback = FallbackMaterial();
                if (fallback > 0 && key.Length > 0)
                {
                    notes.Add(string.Format("{0}: material '{1}' is not in the model or the EuroCode catalog - '{2}' used instead. " +
                                            "Check the spelling, or add it to the model before importing.",
                        context, key, MaterialName(fallback)));
                }
                id = fallback;
            }

            materials[key] = id;
            return id;
        }

        private int FindMaterialInModel(string name)
        {
            try
            {
                int id = model.Materials.IndexOf(name);
                if (id > 0) return id;
            }
            catch { }

            string want = Compact(name);
            int count = 0;
            try { count = model.Materials.Count; } catch { }
            for (int i = 1; i <= count; i++)
                if (Compact(MaterialName(i)) == want) return i;
            return 0;
        }

        private string MaterialName(int id)
        {
            try { return model.Materials.Item[id].Name; } catch { return "?"; }
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
            foreach (string candidate in NameVariants(FallbackMaterialName))
            {
                try { id = model.Materials.AddFromCatalog(Code, candidate); } catch { id = 0; }
                if (id > 0) break;
            }
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
        /// Resolves a cross-section name, in this order:
        ///   1. already in the model (ignoring spaces/case),
        ///   2. defined in the Rhino library (catalog or parametric shape),
        ///   3. a "300x500" style rectangle,
        ///   4. the AxisVM profile catalogs under any spelling variant (IPE300, HEA 200...),
        ///   5. a parametric CHS / SHS / RHS / D (circle) built from the numbers in the name,
        ///   6. the fallback 200x400 rectangle (with a note).
        /// </summary>
        public int Section(string name, string context)
        {
            string key = string.IsNullOrEmpty(name) ? "" : name.Trim();

            int cached;
            if (sections.TryGetValue(key, out cached)) return cached;

            int id = 0;

            if (key.Length > 0)
            {
                id = FindSectionInModel(key);

                RhinoLibSection lib = library.FindSection(key);
                if (id <= 0 && lib != null && !string.Equals(lib.Shape, "Catalog", StringComparison.OrdinalIgnoreCase))
                    id = CreateLibrarySection(lib);

                if (id <= 0)
                {
                    double b_m, h_m;
                    string normName;
                    if (TryParseRectangularDimensions(key, out b_m, out h_m, out normName))
                    {
                        id = FindSectionInModel(normName);
                        if (id <= 0) id = AddRectangular(normName, b_m, h_m);
                        if (id > 0) notes.Add(string.Format("Rectangular cross-section '{0}' (b={1:0.###} m, h={2:0.###} m) is used.", normName, b_m, h_m));
                    }
                }

                if (id <= 0) id = FromCatalog(key);
                if (id <= 0) id = ParametricFromName(key);
            }

            if (id <= 0)
            {
                int fallback = FallbackSection();
                if (fallback > 0 && key.Length > 0)
                    notes.Add(string.Format("{0}: cross-section '{1}' is not in the model, the library or the catalog - '{2}' used instead.",
                        context, key, FallbackSectionName));
                id = fallback;
            }

            sections[key] = id;
            return id;
        }

        private int FindSectionInModel(string name)
        {
            try
            {
                int id = model.CrossSections.IndexOf(name);
                if (id > 0) return id;
            }
            catch { }

            string want = Compact(name);
            int count = 0;
            try { count = model.CrossSections.Count; } catch { }
            for (int i = 1; i <= count; i++)
            {
                string n;
                try { n = model.CrossSections.Item[i].Name; } catch { continue; }
                if (Compact(n) == want) return i;
            }
            return 0;
        }

        private int FromCatalog(string key)
        {
            List<string> variants = NameVariants(key);
            if (key.StartsWith("UNP", StringComparison.OrdinalIgnoreCase)) variants.AddRange(NameVariants("UPN" + key.Substring(3)));
            else if (key.StartsWith("UPN", StringComparison.OrdinalIgnoreCase)) variants.AddRange(NameVariants("UNP" + key.Substring(3)));

            foreach (string candidate in variants)
            {
                foreach (ECrossSectionShapeEx shape in CatalogShapes)
                {
                    int id;
                    try { id = model.CrossSections.AddFromCatalog_V161(shape, candidate); } catch { id = 0; }
                    if (id > 0)
                    {
                        notes.Add(string.Format("Cross-section '{0}' was added from the AxisVM catalog{1}.", key,
                            candidate == key ? "" : " (as '" + candidate + "')"));
                        return id;
                    }
                }
            }
            return 0;
        }

        /// <summary>Creates a parametric section exactly as defined in the Rhino library (dimensions in mm).</summary>
        private int CreateLibrarySection(RhinoLibSection s)
        {
            double b = s.B / 1000.0, h = s.H / 1000.0, t = s.T / 1000.0;
            int id = 0;
            string shape = (s.Shape ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (shape)
                {
                    case "rectangle":
                        if (b > 0 && h > 0) id = AddRectangular(s.Name, b, h);
                        break;
                    case "circle":
                        if (h > 0) id = model.CrossSections.AddCircle(s.Name, h);
                        break;
                    case "pipe":
                        if (h > 0 && t > 0 && 2 * t < h) id = model.CrossSections.AddPipe(s.Name, h, t, ECrossSectionProcess.cspRolled);
                        break;
                    case "recthollow":
                        if (b > 0 && h > 0 && t > 0 && 2 * t < Math.Min(b, h)) id = model.CrossSections.AddRectangularHollow(s.Name, b, h, t, t);
                        break;
                }
            }
            catch (Exception ex)
            {
                notes.Add(string.Format("Library section '{0}' could not be created ({1}).", s.Name, ex.Message));
                return 0;
            }

            if (id > 0) notes.Add(string.Format("Cross-section '{0}' was created from the Rhino library ({1}).", s.Name, s.Shape));
            else notes.Add(string.Format("Library section '{0}' ({1}) has missing or invalid dimensions.", s.Name, s.Shape));
            return id;
        }

        /// <summary>CHS168.3x8, SHS100x100x6, RHS200x100x8, D300 -> a parametric section from the numbers (mm).</summary>
        private int ParametricFromName(string key)
        {
            Match m = Regex.Match(key.Replace(" ", ""), @"^(CHS|RO|PIPE|SHS|RHS|D)(\d+(?:\.\d+)?)(?:[xX*](\d+(?:\.\d+)?))?(?:[xX*](\d+(?:\.\d+)?))?$",
                                  RegexOptions.IgnoreCase);
            if (!m.Success) return 0;

            string kind = m.Groups[1].Value.ToUpperInvariant();
            double a = Num(m.Groups[2].Value), b = Num(m.Groups[3].Value), c = Num(m.Groups[4].Value);
            int id = 0;
            try
            {
                if ((kind == "CHS" || kind == "RO" || kind == "PIPE") && a > 0 && b > 0 && c == 0)
                    id = model.CrossSections.AddPipe(key, a / 1000.0, b / 1000.0, ECrossSectionProcess.cspRolled);
                else if ((kind == "SHS" || kind == "RHS") && a > 0 && b > 0 && c > 0)
                    id = model.CrossSections.AddRectangularHollow(key, b / 1000.0, a / 1000.0, c / 1000.0, c / 1000.0);
                else if (kind == "SHS" && a > 0 && b > 0 && c == 0)
                    id = model.CrossSections.AddRectangularHollow(key, a / 1000.0, a / 1000.0, b / 1000.0, b / 1000.0);
                else if (kind == "D" && a > 0 && b == 0)
                    id = model.CrossSections.AddCircle(key, a / 1000.0);
            }
            catch { id = 0; }

            if (id > 0) notes.Add(string.Format("Cross-section '{0}' was not in the catalog; it was built from its dimensions (mm).", key));
            return id;
        }

        private static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private int AddRectangular(string name, double b, double h)
        {
            int id = 0;
            try { id = model.CrossSections.AddRectangular(name, b, h, ECrossSectionProcess.cspOther); } catch { id = 0; }
            if (id <= 0)
            {
                try { id = model.CrossSections.AddRectangular(name, b, h, ECrossSectionProcess.cspRolled); } catch { id = 0; }
            }
            if (id <= 0) id = FindSectionInModel(name);
            return id;
        }

        /// <summary>
        /// Detects expressions like "500x500", "300 x 400", "REC 300x500", "300*500" and converts
        /// them into width (b) and height (h) in metres. Numbers of 5 or more are millimetres.
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

            char[] delims = { 'x', 'X', '*', '×' };
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

        private int fallbackSectionId = -1;

        private int FallbackSection()
        {
            if (fallbackSectionId >= 0) return fallbackSectionId;

            int id = FindSectionInModel(FallbackSectionName);
            if (id <= 0) id = AddRectangular(FallbackSectionName, FallbackSectionB, FallbackSectionH);

            if (id <= 0)
            {
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

        /// <summary>Creates the Rhino library's load cases up front, in the order they were defined.</summary>
        public void CreateLibraryLoadCases()
        {
            foreach (string name in library.LoadCases) LoadCase(name);
        }

        /// <summary>
        /// Resolves a load case by name (case-insensitive), creating a standard case when it does not
        /// exist yet. AxisVM's load case collection has no IndexOf, so the lookup is a name scan.
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
                    if (id > 0) notes.Add(string.Format("Load case '{0}' was created.", key));
                }
            }

            if (id <= 0)
            {
                int count = 0;
                try { count = model.LoadCases.Count; } catch { }

                if (count > 0)
                {
                    id = 1;
                    if (key.Length > 0)
                    {
                        string fallbackName = "?";
                        try { fallbackName = model.LoadCases.get_Name(1); } catch { }
                        notes.Add(string.Format("Load case '{0}' could not be created; its loads went to '{1}' instead.", key, fallbackName));
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
                string want = name.Trim();
                for (int i = 1; i <= count; i++)
                {
                    if (string.Equals((model.LoadCases.get_Name(i) ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase))
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
