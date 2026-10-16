using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Writes the AxisVM catalog - every Eurocode material and every cross-section table - as two CSV files
    /// for the Rhino side (AxLibrary > "From AxisVM catalog"). Runs from the Rhino Import dialog, inside the
    /// AxisVM that is open, and only reads the catalog: the model is not touched.
    ///
    /// Rows keep AxisVM's order (code / type / kind / grade; shape / table / profile) and names are written
    /// exactly as AxisVM spells them, padding included. Catalog arrays and indexed properties are 1-based
    /// (index 0 is an empty placeholder), so every loop runs 1..Count.
    /// </summary>
    public static class CatalogExport
    {
        public const string MaterialsFile = "AxisVM-materials.csv";
        public const string SectionsFile = "AxisVM-sections.csv";

        /// <summary>The design code whose materials are exported; the importer adds materials with the same code.</summary>
        private const ENationalDesignCode Code = ENationalDesignCode.ndcEuroCode;
        private const string CodeName = "Eurocode";

        /// <summary>Material types in the order AxisVM lists them.</summary>
        private static readonly EMaterialType[] MaterialTypes =
        {
            EMaterialType.mtSteel, EMaterialType.mtConcrete, EMaterialType.mtTimber,
            EMaterialType.mtAluminium, EMaterialType.mtBrick, EMaterialType.mtOther
        };

        public class Result
        {
            public string Folder = "";
            public int Materials;
            public int Sections;
            public int Tables;
        }

        /// <summary>%APPDATA%\AxisBridge - the folder the Rhino plug-in reads the catalog from.</summary>
        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxisBridge");
        }

        /// <summary>Reads the catalog of <paramref name="app"/> and writes both CSV files into <paramref name="folder"/>.</summary>
        public static Result Export(IAxisVMApplication app, string folder)
        {
            var materials = new List<string[]>();
            var sections = new List<string[]>();
            var tableIds = new HashSet<string>();

            IAxisVMCatalog cat = app.Catalog;
            try
            {
                ReadMaterials(cat, materials);
                ReadSections(cat, sections, tableIds);
            }
            finally { Release(cat); }

            if (materials.Count == 0 && sections.Count == 0)
                throw new InvalidOperationException("AxisVM returned an empty catalog; nothing was written.");

            Directory.CreateDirectory(folder);
            WriteCsv(Path.Combine(folder, MaterialsFile), new[] { "Code", "Type", "Kind", "Name", "Standard" }, materials);
            WriteCsv(Path.Combine(folder, SectionsFile), new[] { "Shape", "ShapeId", "Table", "File", "Region", "Name" }, sections);

            return new Result { Folder = folder, Materials = materials.Count, Sections = sections.Count, Tables = tableIds.Count };
        }

        private static void ReadMaterials(IAxisVMCatalog cat, List<string[]> rows)
        {
            foreach (EMaterialType mt in MaterialTypes)
            {
                Array names = null;
                int n;
                try { n = cat.GetMaterialNamesByType(Code, mt, out names); } catch { n = 0; }
                if (n <= 0 || names == null) continue;

                foreach (object o in names)
                {
                    string name = o as string;
                    if (string.IsNullOrEmpty(name)) continue;

                    string standard = "", kind = "";
                    AxisVMMaterial m = null;
                    try
                    {
                        if (cat.GetMaterial(Code, name, out m) > 0 && m != null)
                        {
                            standard = Safe(() => m.MaterialDesignName);
                            if (mt == EMaterialType.mtTimber) kind = TimberKind(Safe(() => m.TimberType.ToString()));
                        }
                    }
                    catch { }
                    finally { Release(m); }

                    rows.Add(new[] { CodeName, MaterialTypeName(mt), kind, name, standard });
                }
            }
        }

        private static void ReadSections(IAxisVMCatalog cat, List<string[]> rows, HashSet<string> tableIds)
        {
            AxisVMCrossSectionTables tables;
            cat.GetAllTables(out tables);
            var found = new List<KeyValuePair<int, string[]>>();
            try
            {
                int count = tables.Count;
                for (int i = 1; i <= count; i++)
                {
                    RCrossSectionTable rec = tables.Item[i];
                    if (rec.Id <= 0) continue;

                    string table = Safe(() => tables.PrgTableName[i]);
                    if (table.Length == 0) table = Safe(() => tables.DocTableName[i]);
                    string file = Safe(() => tables.FileName[i]);
                    string shape = ShapeName(rec.CrossSectionShape);
                    string shapeId = ((int)rec.CrossSectionShape).ToString(CultureInfo.InvariantCulture);
                    string region = RegionName(rec.CrossSectionRegion);

                    // Name by name: GetCrossSectionNamesEx would be one call per table, but it brought
                    // AxisVM 18 r1o down on the SHS (EN 10210-2) table.
                    AxisVMCrossSectionTable tab = null;
                    try { cat.GetTableCrossSections(rec.Id, out tab); } catch { tab = null; }
                    if (tab == null) continue;
                    try
                    {
                        int n = tab.Count;
                        for (int k = 1; k <= n; k++)
                        {
                            string name = Safe(() => tab.CrossSectionName[k]);
                            if (name.Length == 0) continue;
                            found.Add(new KeyValuePair<int, string[]>((int)rec.CrossSectionShape, new[] { shape, shapeId, table, file, region, name }));
                            tableIds.Add(file + "|" + table);
                        }
                    }
                    finally { Release(tab); }
                }
            }
            finally { Release(tables); }

            // Shape groups in AxisVM's own order; tables (already alphabetical) and the profiles inside a table
            // keep the catalog order (HEA 100, 120 ... 1000 rather than alphabetical). The index keeps it stable.
            var order = new List<int>();
            for (int i = 0; i < found.Count; i++) order.Add(i);
            order.Sort((a, b) => found[a].Key != found[b].Key ? found[a].Key.CompareTo(found[b].Key) : a.CompareTo(b));
            foreach (int i in order) rows.Add(found[i].Value);
        }

        // ==================================================================
        // Readable names
        // ==================================================================

        public static string MaterialTypeName(EMaterialType t)
        {
            switch (t)
            {
                case EMaterialType.mtSteel: return "Steel";
                case EMaterialType.mtConcrete: return "Concrete";
                case EMaterialType.mtTimber: return "Timber";
                case EMaterialType.mtAluminium: return "Aluminium";
                case EMaterialType.mtBrick: return "Brick";
                default: return "Other";
            }
        }

        public static string TimberKind(string enumName)
        {
            switch ((enumName ?? "").ToLowerInvariant())
            {
                case "ttsoftwood": return "Softwood";
                case "tthardwood": return "Hardwood";
                case "ttglulam": return "Glulam";
                case "ttlvl": return "LVL";
                case "ttclt": return "CLT";
                case "": return "";
                default: return "Other";
            }
        }

        /// <summary>The shape group shown at the top of the Rhino picker, as in the AxisVM library.</summary>
        public static string ShapeName(ECrossSectionShapeEx s)
        {
            switch (s.ToString())
            {
                case "csseRectangular": return "Rectangular";
                case "csseI": return "I shapes";
                case "csseDoubleI": return "Double I shapes";
                case "csseWedgedI": return "Wedged I shapes";
                case "csseAsymmetricI": return "Asymmetric I shapes";
                case "cssePipe": return "Pipes";
                case "csseBox": return "Box shapes";
                case "csseU": return "U shapes";
                case "csseL": return "L shapes";
                case "csseDoubleL": return "Double L shapes";
                case "csseT": return "T shapes";
                case "csseZ": return "Z shapes";
                case "csseC": return "C shapes";
                case "csseS": return "S shapes";
                case "csseJ": return "J shapes";
                case "csseCircle": return "Round shapes";
                case "csseRectangleHollow": return "Hollow rectangles";
                case "csseCircleHollow": return "Hollow circles";
                case "csseHSQ": return "HSQ shapes";
                case "csseIFB": return "IFB shapes";
                case "csseSFB": return "SFB shapes";
            }
            string n = s.ToString();
            return n.StartsWith("csse") ? n.Substring(4) + " shapes" : n;
        }

        public static string RegionName(ECrossSectionRegion r)
        {
            string n = r.ToString();
            if (n.StartsWith("cssr_")) n = n.Substring(5);
            return n == "Unknown" ? "" : n;
        }

        // ==================================================================
        // CSV (RFC 4180, UTF-8 with BOM so Excel shows ń and ü) - read back by AxCatalog on the Rhino side
        // ==================================================================

        private static void WriteCsv(string path, string[] header, List<string[]> rows)
        {
            var sb = new StringBuilder();
            AppendRow(sb, header);
            foreach (string[] r in rows) AppendRow(sb, r);

            // Temporary file first, so a failed write never leaves half a catalog behind.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void AppendRow(StringBuilder sb, string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(cells[i]));
            }
            sb.Append("\r\n");
        }

        /// <summary>Quotes a cell when it holds a comma, quote, line break or leading/trailing space.</summary>
        public static string Quote(string cell)
        {
            string c = cell ?? "";
            bool needs = c.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || (c.Length > 0 && (c[0] == ' ' || c[c.Length - 1] == ' '));
            return needs ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }
    }
}
