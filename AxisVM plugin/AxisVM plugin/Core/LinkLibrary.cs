using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// The "library" of the live link: what the AxisVM model defines besides geometry - materials, cross-sections,
    /// load cases and load combinations - in the same shape Rhino's AxLibrary stores (names; section dimensions in mm).
    ///
    /// "Send to Rhino" writes the whole current library into the axisvm section; Rhino adds what its library lacks and
    /// never removes anything. In the other direction the Rhino library (now including combinations) is created in
    /// AxisVM by the import (CatalogResolver). Names are the identity, compared case-insensitively.
    /// </summary>
    public static class LinkLibrary
    {
        /// <summary>The AxisVM model's library as JSON, and how many entries of each kind it holds.</summary>
        public static JsonValue Read(IAxisVMModel model, out int[] counts)
        {
            JsonValue lib = JsonValue.Obj();
            counts = new int[4];

            JsonValue mats = JsonValue.Arr();
            int n = Count(() => model.Materials.Count);
            for (int i = 1; i <= n; i++)
            {
                string name = LinkState.MaterialName(model, i);
                if (name.Length == 0) continue;
                mats.Add(JsonValue.Obj().Set("name", JsonValue.Str(name)).Set("family", JsonValue.Str(Family(model, i))));
            }
            lib.Set("materials", mats);
            counts[0] = mats.Count;

            JsonValue secs = JsonValue.Arr();
            n = Count(() => model.CrossSections.Count);
            for (int i = 1; i <= n; i++)
            {
                JsonValue s = Section(model, i);
                if (s != null) secs.Add(s);
            }
            lib.Set("sections", secs);
            counts[1] = secs.Count;

            JsonValue cases = JsonValue.Arr();
            n = Count(() => model.LoadCases.Count);
            for (int i = 1; i <= n; i++)
            {
                string name = CaseName(model, i);
                if (name.Length == 0 || IsUnusedDefaultCase(model, i, name)) continue;
                cases.Add(JsonValue.Obj().Set("name", JsonValue.Str(name)).Set("description", JsonValue.Str("")));
            }
            lib.Set("loadCases", cases);
            counts[2] = cases.Count;

            JsonValue combos = JsonValue.Arr();
            n = Count(() => model.LoadCombinations.Count);
            for (int i = 1; i <= n; i++)
            {
                JsonValue c = Combination(model, i);
                if (c != null) combos.Add(c);
            }
            lib.Set("combinations", combos);
            counts[3] = combos.Count;

            return lib;
        }

        /// <summary>Entries of <paramref name="axisvm"/> whose name the Rhino library (a link file's "rhino" section) does not have.</summary>
        public static int CountNew(JsonValue axisvm, JsonValue rhinoSection)
        {
            JsonValue rhinoLib = rhinoSection == null ? null : rhinoSection["library"];
            int fresh = 0;
            foreach (string kind in new[] { "materials", "sections", "loadCases", "combinations" })
            {
                HashSet<string> known = Names(rhinoLib == null ? null : rhinoLib[kind]);
                JsonValue mine = axisvm == null ? null : axisvm[kind];
                for (int i = 0; mine != null && i < mine.Count; i++)
                    if (!known.Contains(Squeeze(LinkFile.Get(mine.At(i), "name")))) fresh++;
            }
            return fresh;
        }

        private static HashSet<string> Names(JsonValue arr)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; arr != null && i < arr.Count; i++) set.Add(Squeeze(LinkFile.Get(arr.At(i), "name")));
            return set;
        }

        /// <summary>Names differing only in spaces are the same entry ("IPE 300" in AxisVM, "IPE300" in Rhino).</summary>
        private static string Squeeze(string name)
        {
            return (name ?? "").Replace(" ", "").Replace("\t", "");
        }

        // ==================================================================
        // Entries
        // ==================================================================

        private static string Family(IAxisVMModel model, int index)
        {
            try
            {
                string t = model.Materials.Item[index].MaterialType.ToString().ToLowerInvariant();
                if (t.Contains("steel")) return "Steel";
                if (t.Contains("concrete")) return "Concrete";
                if (t.Contains("timber") || t.Contains("wood")) return "Timber";
            }
            catch { }
            return "Other";
        }

        /// <summary>
        /// AxisVM's own "ST1", which every new model starts with: not part of the design when it holds no loads and no
        /// combination uses it, so it is not offered to Rhino.
        /// </summary>
        private static bool IsUnusedDefaultCase(IAxisVMModel model, int index, string name)
        {
            if (!string.Equals(name, "ST1", StringComparison.OrdinalIgnoreCase)) return false;
            try { if (model.LoadCases.get_LoadCount(index) > 0) return false; } catch { return false; }
            try
            {
                for (int c = 1; c <= model.LoadCombinations.Count; c++)
                {
                    Array factors, ids;
                    model.LoadCombinations.GetCombination(c, out factors, out ids);
                    if (ids != null) foreach (object o in ids) if (Convert.ToInt32(o) == index) return false;
                }
            }
            catch { return false; }
            return true;
        }

        private static string CaseName(IAxisVMModel model, int index)
        {
            try { return (model.LoadCases.get_Name(index) ?? "").Trim(); } catch { return ""; }
        }

        /// <summary>
        /// A section as the Rhino library defines it: rectangle / circle / pipe / hollow rectangle with their dimensions
        /// in mm, everything else (catalog profiles, built-up shapes) by name only - AxisVM finds those in its catalog.
        /// The section the import creates when a beam names none is skipped.
        /// </summary>
        private static JsonValue Section(IAxisVMModel model, int index)
        {
            IAxisVMCrossSection cs;
            string name;
            try
            {
                cs = model.CrossSections.Item[index];
                name = (cs.Name ?? "").Trim();
            }
            catch { return null; }
            if (name.Length == 0 || name.StartsWith("AxImport-", StringComparison.OrdinalIgnoreCase)) return null;

            string shape = "Catalog";
            double b = 0, h = 0, t = 0;
            try
            {
                switch (cs.CrossSectionShapeEx)
                {
                    case ECrossSectionShapeEx.csseRectangular:
                        b = cs.b * 1000.0; h = cs.h * 1000.0;
                        if (b > 0 && h > 0) shape = "Rectangle";
                        break;
                    case ECrossSectionShapeEx.csseCircle:
                        h = Math.Max(cs.h, cs.b) * 1000.0;
                        if (h > 0) shape = "Circle";
                        break;
                    case ECrossSectionShapeEx.cssePipe:
                    case ECrossSectionShapeEx.csseCircleHollow:
                        h = Math.Max(cs.h, cs.b) * 1000.0; t = Math.Max(cs.tw, cs.tf) * 1000.0;
                        if (h > 0 && t > 0) shape = "Pipe";
                        break;
                    case ECrossSectionShapeEx.csseRectangleHollow:
                        b = cs.b * 1000.0; h = cs.h * 1000.0; t = Math.Max(cs.tw, cs.tf) * 1000.0;
                        if (b > 0 && h > 0 && t > 0) shape = "RectHollow";
                        break;
                }
            }
            catch { shape = "Catalog"; }

            JsonValue e = JsonValue.Obj().Set("name", JsonValue.Str(name)).Set("shape", JsonValue.Str(shape));
            if (shape != "Catalog")
                e.Set("b", JsonValue.Num(Round(b))).Set("h", JsonValue.Num(Round(h))).Set("t", JsonValue.Num(Round(t)));
            return e;
        }

        private static double Round(double mm) { return Math.Round(mm, 3); }

        /// <summary>A combination with its type and "factor x load case" terms.</summary>
        private static JsonValue Combination(IAxisVMModel model, int index)
        {
            string name;
            try { name = (model.LoadCombinations.get_Name(index) ?? "").Trim(); } catch { return null; }
            if (name.Length == 0) return null;

            string type = "Other";
            try { type = TypeName(model.LoadCombinations.get_CombinationType(index)); } catch { }

            JsonValue terms = JsonValue.Arr();
            try
            {
                Array factors, ids;
                model.LoadCombinations.GetCombination(index, out factors, out ids);
                if (factors != null && ids != null)
                {
                    int lo = factors.GetLowerBound(0), loIds = ids.GetLowerBound(0), count = Math.Min(factors.Length, ids.Length);
                    for (int k = 0; k < count; k++)
                    {
                        int caseId = Convert.ToInt32(ids.GetValue(loIds + k));
                        double factor = Convert.ToDouble(factors.GetValue(lo + k), CultureInfo.InvariantCulture);
                        string caseName = CaseName(model, caseId);
                        if (caseName.Length > 0) terms.Add(JsonValue.Obj().Set("case", JsonValue.Str(caseName)).Set("factor", JsonValue.Num(factor)));
                    }
                }
            }
            catch { }
            return JsonValue.Obj().Set("name", JsonValue.Str(name)).Set("type", JsonValue.Str(type)).Set("terms", terms);
        }

        /// <summary>
        /// The Eurocode name of a combination type. ECombinationType has aliases with the same value (ctSLS1 = ctSLSChar,
        /// ctULS1 = ctULS, ...), and ToString() picks either, so the names are fixed here.
        /// </summary>
        private static string TypeName(ECombinationType t)
        {
            switch ((int)t)
            {
                case 0: return "Other";
                case 1: return "SLSChar";
                case 2: return "SLSFreq";
                case 3: return "SLSQuasi";
                case 4: return "ULS";
                case 5: return "ULSSeismic";
                case 6: return "ULSExceptional";
            }
            string name = t.ToString();
            return name.StartsWith("ct", StringComparison.Ordinal) ? name.Substring(2) : name;
        }

        private static int Count(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }
    }
}
