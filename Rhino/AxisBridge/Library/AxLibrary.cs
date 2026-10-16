using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Rhino;

namespace AxisBridge.Library
{
    /// <summary>A material in the document library. Name is what elements reference (Ax_Material).</summary>
    public class LibMaterial
    {
        [JsonProperty("name")] public string Name { get; set; } = "";

        /// <summary>Steel / Concrete / Timber / Other - only used to group the list.</summary>
        [JsonProperty("family")] public string Family { get; set; } = "";
    }

    /// <summary>
    /// A cross-section in the document library. Name is what elements reference (Ax_Section).
    /// Shape "Catalog" means "look the name up in the AxisVM profile catalog" (IPE300, HEA200...);
    /// the parametric shapes are created in AxisVM from their dimensions, which are always in mm.
    /// </summary>
    public class LibSection
    {
        public const string Catalog = "Catalog";
        public const string Rectangle = "Rectangle";
        public const string Circle = "Circle";
        public const string Pipe = "Pipe";
        public const string RectHollow = "RectHollow";

        public static readonly string[] Shapes = { Catalog, Rectangle, Circle, Pipe, RectHollow };

        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("shape")] public string Shape { get; set; } = Catalog;

        /// <summary>Width (Rectangle, RectHollow) [mm].</summary>
        [JsonProperty("b")] public double B { get; set; }
        /// <summary>Height (Rectangle, RectHollow) or diameter (Circle, Pipe) [mm].</summary>
        [JsonProperty("h")] public double H { get; set; }
        /// <summary>Wall thickness (Pipe, RectHollow) [mm].</summary>
        [JsonProperty("t")] public double T { get; set; }

        /// <summary>
        /// Set when the profile was picked from the AxisVM catalog (AxLibrary > From AxisVM catalog): the table's shape
        /// (ECrossSectionShapeEx), its name and its catalog file, so AxisVM adds exactly this profile even
        /// when another table has one with the same name. Blank / 0 for typed names.
        /// </summary>
        [JsonProperty("catalogShape", DefaultValueHandling = DefaultValueHandling.Ignore)] public int CatalogShape { get; set; }
        [JsonProperty("catalogTable", NullValueHandling = NullValueHandling.Ignore)] public string CatalogTable { get; set; }
        [JsonProperty("catalogFile", NullValueHandling = NullValueHandling.Ignore)] public string CatalogFile { get; set; }
        /// <summary>The name exactly as the catalog spells it, only when that differs from Name (some tables pad names with spaces).</summary>
        [JsonProperty("catalogName", NullValueHandling = NullValueHandling.Ignore)] public string CatalogName { get; set; }

        /// <summary>A readable definition for the grid / tooltips.</summary>
        public string Describe()
        {
            switch (Shape)
            {
                case Rectangle: return string.Format(CultureInfo.InvariantCulture, "Rectangle b={0} h={1} mm", B, H);
                case Circle: return string.Format(CultureInfo.InvariantCulture, "Circle d={0} mm", H);
                case Pipe: return string.Format(CultureInfo.InvariantCulture, "Pipe d={0} t={1} mm", H, T);
                case RectHollow: return string.Format(CultureInfo.InvariantCulture, "Hollow rect b={0} h={1} t={2} mm", B, H, T);
                default: return string.IsNullOrEmpty(CatalogTable) ? "AxisVM catalog profile" : "AxisVM catalog: " + CatalogTable;
            }
        }

        /// <summary>Suggested name for a parametric section, e.g. "300x500", "D300", "CHS168.3x8".</summary>
        public string AutoName()
        {
            switch (Shape)
            {
                case Rectangle: return string.Format(CultureInfo.InvariantCulture, "{0}x{1}", B, H);
                case Circle: return string.Format(CultureInfo.InvariantCulture, "D{0}", H);
                case Pipe: return string.Format(CultureInfo.InvariantCulture, "CHS{0}x{1}", H, T);
                case RectHollow: return string.Format(CultureInfo.InvariantCulture, "RHS{0}x{1}x{2}", H, B, T);
                default: return Name;
            }
        }
    }

    /// <summary>A load case in the document library, offered in every load grid.</summary>
    public class LibLoadCase
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
    }

    /// <summary>One "factor x load case" term of a load combination.</summary>
    public class LibCombinationTerm
    {
        [JsonProperty("case")] public string Case { get; set; } = "";
        [JsonProperty("factor")] public double Factor { get; set; } = 1.0;
    }

    /// <summary>
    /// A load combination of the library. Type is AxisVM's combination type without its "ct" prefix
    /// (ULS, SLSChar, SLSQuasi, Other, ...); AxisVM creates the combination from these terms when the model is imported.
    /// </summary>
    public class LibCombination
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "Other";
        [JsonProperty("terms")] public List<LibCombinationTerm> Terms { get; set; } = new List<LibCombinationTerm>();
    }

    /// <summary>
    /// The per-document library of materials, cross-sections, load cases and load combinations.
    ///
    /// It lives in the .3dm (document user strings, section "AxisBridge"), so it travels with the
    /// model, and it is written into the AxExport JSON so AxisVM creates the load cases in this order
    /// and builds the parametric sections exactly as defined here.
    /// </summary>
    public class AxLibrary
    {
        private const string DocSection = "AxisBridge";
        private const string DocEntry = "Library";

        [JsonProperty("materials")] public List<LibMaterial> Materials { get; set; } = new List<LibMaterial>();
        [JsonProperty("sections")] public List<LibSection> Sections { get; set; } = new List<LibSection>();
        [JsonProperty("loadCases")] public List<LibLoadCase> LoadCases { get; set; } = new List<LibLoadCase>();
        [JsonProperty("combinations")] public List<LibCombination> Combinations { get; set; } = new List<LibCombination>();

        /// <summary>Common Eurocode grades offered by "Add preset" in the library dialog.</summary>
        public static readonly string[][] MaterialPresets =
        {
            new[] { "Steel", "S235" }, new[] { "Steel", "S275" }, new[] { "Steel", "S355" }, new[] { "Steel", "S460" },
            new[] { "Concrete", "C20/25" }, new[] { "Concrete", "C25/30" }, new[] { "Concrete", "C30/37" },
            new[] { "Concrete", "C35/45" }, new[] { "Concrete", "C40/50" }, new[] { "Concrete", "C45/55" },
            new[] { "Timber", "C24" }, new[] { "Timber", "GL24h" }, new[] { "Timber", "GL28h" }, new[] { "Timber", "GL32h" },
        };

        /// <summary>Common catalog profiles offered by "Add preset".</summary>
        public static readonly string[] SectionPresets =
        {
            "IPE200", "IPE240", "IPE300", "IPE360", "IPE400",
            "HEA160", "HEA200", "HEA240", "HEA300", "HEB200", "HEB300",
            "UPN200", "SHS100x100x6", "RHS200x100x8", "CHS168.3x8"
        };

        // ==================================================================
        // Persistence
        // ==================================================================

        /// <summary>The document's library, or a small starter library when the document has none yet.</summary>
        public static AxLibrary Load(RhinoDoc doc)
        {
            string json = doc?.Strings.GetValue(DocSection, DocEntry);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    AxLibrary lib = JsonConvert.DeserializeObject<AxLibrary>(json);
                    if (lib != null)
                    {
                        lib.Normalize();
                        return lib;
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("AxisBridge: the stored library could not be read (" + ex.Message + "); using defaults.");
                }
            }
            return Defaults();
        }

        public void Save(RhinoDoc doc)
        {
            if (doc == null) return;
            Normalize();
            doc.Strings.SetString(DocSection, DocEntry, JsonConvert.SerializeObject(this, Formatting.None));
        }

        public static AxLibrary Defaults()
        {
            var lib = new AxLibrary();
            lib.Materials.Add(new LibMaterial { Name = "S355", Family = "Steel" });
            lib.Materials.Add(new LibMaterial { Name = "C30/37", Family = "Concrete" });
            lib.Materials.Add(new LibMaterial { Name = "GL24h", Family = "Timber" });
            lib.Sections.Add(new LibSection { Name = "IPE300", Shape = LibSection.Catalog });
            lib.Sections.Add(new LibSection { Name = "HEA200", Shape = LibSection.Catalog });
            lib.Sections.Add(new LibSection { Name = "300x500", Shape = LibSection.Rectangle, B = 300, H = 500 });
            lib.LoadCases.Add(new LibLoadCase { Name = "Dead", Description = "Permanent - self weight / finishes" });
            lib.LoadCases.Add(new LibLoadCase { Name = "Live", Description = "Imposed / variable" });
            return lib;
        }

        /// <summary>Drops blank and duplicate (case-insensitive) names, keeping the first.</summary>
        public void Normalize()
        {
            if (Materials == null) Materials = new List<LibMaterial>();
            if (Sections == null) Sections = new List<LibSection>();
            if (LoadCases == null) LoadCases = new List<LibLoadCase>();
            if (Combinations == null) Combinations = new List<LibCombination>();
            Combinations = Distinct(Combinations, c => c.Name);
            Materials = Distinct(Materials, m => m.Name);
            Sections = Distinct(Sections, s => s.Name);
            LoadCases = Distinct(LoadCases, c => c.Name);
        }

        private static List<T> Distinct<T>(List<T> items, Func<T, string> name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<T>();
            foreach (T item in items)
            {
                if (item == null) continue;
                string n = (name(item) ?? "").Trim();
                if (n.Length == 0 || !seen.Add(n)) continue;
                result.Add(item);
            }
            return result;
        }

        // ==================================================================
        // Lookups used by the attribute dialogs
        // ==================================================================

        public List<string> MaterialNames() { return Names(Materials, m => m.Name); }
        public List<string> SectionNames() { return Names(Sections, s => s.Name); }
        public List<string> LoadCaseNames() { return Names(LoadCases, c => c.Name); }

        /// <summary>How many entries a merge from AxisVM added.</summary>
        public class MergeResult
        {
            public int Materials, Sections, LoadCases, Combinations;
            public int Total { get { return Materials + Sections + LoadCases + Combinations; } }
        }

        /// <summary>
        /// Adds what the AxisVM model defines and this library lacks (by name, case-insensitive): the "library" of
        /// the link file's axisvm section. Entries already here are kept as they are and nothing is ever removed.
        /// </summary>
        public MergeResult MergeFrom(Newtonsoft.Json.Linq.JToken axisvm)
        {
            var res = new MergeResult();
            if (axisvm == null || axisvm.Type != Newtonsoft.Json.Linq.JTokenType.Object) return res;

            foreach (var t in (axisvm["materials"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
            {
                var m = t.ToObject<LibMaterial>();
                if (m == null || string.IsNullOrWhiteSpace(m.Name) || MaterialNames().Exists(x => string.Equals(x, m.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (string.IsNullOrEmpty(m.Family)) m.Family = GuessFamily(m.Name);
                Materials.Add(m);
                res.Materials++;
            }
            foreach (var t in (axisvm["sections"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
            {
                var s = t.ToObject<LibSection>();
                if (s == null || string.IsNullOrWhiteSpace(s.Name) || FindSection(s.Name) != null) continue;
                Sections.Add(s);
                res.Sections++;
            }
            foreach (var t in (axisvm["loadCases"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
            {
                var c = t.ToObject<LibLoadCase>();
                if (c == null || string.IsNullOrWhiteSpace(c.Name) || LoadCaseNames().Exists(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                LoadCases.Add(c);
                res.LoadCases++;
            }
            foreach (var t in (axisvm["combinations"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
            {
                var c = t.ToObject<LibCombination>();
                if (c == null || string.IsNullOrWhiteSpace(c.Name) || Combinations.Exists(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                Combinations.Add(c);
                res.Combinations++;
            }
            return res;
        }

        private static List<string> Names<T>(List<T> items, Func<T, string> name)
        {
            var list = new List<string>();
            foreach (T i in items) list.Add(name(i));
            return list;
        }

        public LibSection FindSection(string name)
        {
            foreach (LibSection s in Sections)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Adds the name if it is not there yet. Returns true when something was added.</summary>
        public bool EnsureMaterial(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (LibMaterial m in Materials)
                if (string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            Materials.Add(new LibMaterial { Name = name.Trim(), Family = GuessFamily(name) });
            return true;
        }

        /// <summary>
        /// Adds a section name typed directly in a dialog. "300x500" style names become a Rectangle,
        /// everything else a catalog profile.
        /// </summary>
        public bool EnsureSection(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (FindSection(name.Trim()) != null) return false;

            var s = new LibSection { Name = name.Trim(), Shape = LibSection.Catalog };
            double b, h;
            if (TryParseRect(name, out b, out h)) { s.Shape = LibSection.Rectangle; s.B = b; s.H = h; }
            Sections.Add(s);
            return true;
        }

        public bool EnsureLoadCase(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (LibLoadCase c in LoadCases)
                if (string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            LoadCases.Add(new LibLoadCase { Name = name.Trim() });
            return true;
        }

        public static string GuessFamily(string name)
        {
            string n = (name ?? "").Trim().ToUpperInvariant().Replace(" ", "");
            if (n.StartsWith("GL") || (n.StartsWith("C") && !n.Contains("/") && n.Length <= 3) || n.StartsWith("D") && n.Length <= 3) return "Timber";
            if (n.StartsWith("C") && n.Contains("/")) return "Concrete";
            if (n.StartsWith("S")) return "Steel";
            return "Other";
        }

        /// <summary>"300x500" (mm) -> b=300, h=500. Only plain two-number names qualify.</summary>
        public static bool TryParseRect(string text, out double b, out double h)
        {
            b = h = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Trim().ToLowerInvariant().Replace('*', 'x').Split('x');
            if (parts.Length != 2) return false;
            return double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                && b > 0 && h > 0;
        }
    }
}
