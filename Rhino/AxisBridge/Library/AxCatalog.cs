using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AxisBridge.Library
{
    /// <summary>A material as listed in the AxisVM catalog, e.g. Eurocode / Timber / Glulam / "GL 24h".</summary>
    public class CatalogMaterial
    {
        /// <summary>Design code the material belongs to ("Eurocode").</summary>
        public string Code = "";
        /// <summary>AxisVM material type: Steel, Concrete, Timber, Aluminium, Brick, Other.</summary>
        public string Type = "";
        /// <summary>Sub-kind where AxisVM has one (timber: Softwood, Hardwood, Glulam, LVL, CLT), else blank.</summary>
        public string Kind = "";
        /// <summary>The catalog name, spelled exactly as AxisVM does ("S 355", "C30/37", "GL 24h").</summary>
        public string Name = "";
        /// <summary>Product standard as AxisVM reports it ("EN 338:2009"), informative only.</summary>
        public string Standard = "";
    }

    /// <summary>A cross-section as listed in an AxisVM profile table, e.g. I shapes / "IPE (EN 10365) I-beams" / "IPE 300".</summary>
    public class CatalogSection
    {
        /// <summary>Readable shape group ("I shapes", "Pipes"...), the top level of the AxisVM library.</summary>
        public string Shape = "";
        /// <summary>AxisVM ECrossSectionShapeEx value of the table.</summary>
        public int ShapeId;
        /// <summary>Table name as AxisVM shows it ("HEA (EN 10365) Wide flange beams").</summary>
        public string Table = "";
        /// <summary>Catalog file of the table without folder or extension ("HEA_PL"); identifies the table on import.</summary>
        public string File = "";
        /// <summary>Region of the table (EU, UK, US, HU...).</summary>
        public string Region = "";
        /// <summary>The profile name, spelled exactly as AxisVM does ("HEA 300", "CHS 168,3x8").</summary>
        public string Name = "";
    }

    /// <summary>
    /// The AxisVM material and cross-section catalog, as written by the AxisVM Rhino Import add-on
    /// ("Export catalog for Rhino") into two CSV files in <see cref="DefaultFolder"/>. Rows keep AxisVM's own order (code / type / grade, shape / table / profile), so the picker
    /// shows them the way the AxisVM library dialog does.
    /// </summary>
    public class AxCatalog
    {
        public const string MaterialsFile = "AxisVM-materials.csv";
        public const string SectionsFile = "AxisVM-sections.csv";

        private static readonly string[] MaterialColumns = { "Code", "Type", "Kind", "Name", "Standard" };
        private static readonly string[] SectionColumns = { "Shape", "ShapeId", "Table", "File", "Region", "Name" };

        public List<CatalogMaterial> Materials = new List<CatalogMaterial>();
        public List<CatalogSection> Sections = new List<CatalogSection>();

        /// <summary>Folder the files were read from or written to.</summary>
        public string Folder = "";

        /// <summary>When the CSV files were written (local time).</summary>
        public DateTime Written;

        // ==================================================================
        // Where the files live
        // ==================================================================

        /// <summary>
        /// %APPDATA%\AxisBridge of the logged-in Windows user (C:\Users\&lt;user&gt;\AppData\Roaming\AxisBridge).
        /// The AxisVM Rhino Import add-on writes the catalog here and this plug-in reads it from here. Both
        /// resolve the folder at run time, so it is the same folder on any laptop whatever the user name, drive
        /// or install location of AxisVM and Rhino, and it is always writable.
        /// </summary>
        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxisBridge");
        }

        /// <summary>The catalog the AxisVM add-on exported, or null when it has not been exported yet.</summary>
        public static AxCatalog FindDefault() { return Exists(DefaultFolder()) ? Load(DefaultFolder()) : null; }

        public static bool Exists(string folder)
        {
            return !string.IsNullOrEmpty(folder)
                && File.Exists(Path.Combine(folder, MaterialsFile))
                && File.Exists(Path.Combine(folder, SectionsFile));
        }

        /// <summary>Writes both files in the add-on's format (used by the checks; the real files come from AxisVM).</summary>
        public void Save(string folder)
        {
            Directory.CreateDirectory(folder);

            var mats = new List<string[]>();
            foreach (CatalogMaterial m in Materials) mats.Add(new[] { m.Code, m.Type, m.Kind, m.Name, m.Standard });
            var secs = new List<string[]>();
            foreach (CatalogSection s in Sections)
                secs.Add(new[] { s.Shape, s.ShapeId.ToString(CultureInfo.InvariantCulture), s.Table, s.File, s.Region, s.Name });

            WriteCsv(Path.Combine(folder, MaterialsFile), MaterialColumns, mats);
            WriteCsv(Path.Combine(folder, SectionsFile), SectionColumns, secs);
            Folder = folder;
            Written = File.GetLastWriteTime(Path.Combine(folder, SectionsFile));
        }

        public static AxCatalog Load(string folder)
        {
            var c = new AxCatalog { Folder = folder };

            foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(folder, MaterialsFile)))
            {
                var m = new CatalogMaterial { Code = Get(r, "Code"), Type = Get(r, "Type"), Kind = Get(r, "Kind"), Name = Get(r, "Name"), Standard = Get(r, "Standard") };
                if (m.Name.Length > 0) c.Materials.Add(m);
            }

            foreach (Dictionary<string, string> r in ReadCsv(Path.Combine(folder, SectionsFile)))
            {
                int id;
                int.TryParse(Get(r, "ShapeId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                var s = new CatalogSection { Shape = Get(r, "Shape"), ShapeId = id, Table = Get(r, "Table"), File = Get(r, "File"), Region = Get(r, "Region"), Name = Get(r, "Name") };
                if (s.Name.Length > 0) c.Sections.Add(s);
            }

            string stamp = Path.Combine(folder, SectionsFile);
            c.Written = File.Exists(stamp) ? File.GetLastWriteTime(stamp) : DateTime.MinValue;
            return c;
        }

        private static string Get(Dictionary<string, string> row, string key)
        {
            string v;
            return row.TryGetValue(key, out v) && v != null ? v : "";
        }

        // ==================================================================
        // CSV (RFC 4180: comma separated, quoted when needed, UTF-8 with BOM so Excel shows ń and ü)
        // ==================================================================

        public static void WriteCsv(string path, string[] header, List<string[]> rows)
        {
            var sb = new StringBuilder();
            AppendRow(sb, header);
            foreach (string[] r in rows) AppendRow(sb, r);

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

        /// <summary>Reads a CSV with a header row into one dictionary per row (keys are the header names).</summary>
        public static List<Dictionary<string, string>> ReadCsv(string path)
        {
            var result = new List<Dictionary<string, string>>();
            if (!File.Exists(path)) return result;

            List<List<string>> rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8));
            if (rows.Count == 0) return result;

            List<string> header = rows[0];
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> r = rows[i];
                if (r.Count == 1 && r[0].Length == 0) continue;   // blank line
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < header.Count; k++) d[header[k].Trim()] = k < r.Count ? r[k] : "";
                result.Add(d);
            }
            return result;
        }

        public static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            int i = 0;
            if (text.Length > 0 && text[0] == '﻿') i = 1;

            for (; i < text.Length; i++)
            {
                char ch = text[i];
                any = true;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row); row = new List<string>();
                    any = false;
                }
                else cell.Append(ch);
            }
            if (any) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        // ==================================================================
        // Mapping to the document library
        // ==================================================================

        /// <summary>Steel / Concrete / Timber / Other - the families the library grid knows.</summary>
        public static string FamilyOf(CatalogMaterial m)
        {
            switch ((m.Type ?? "").ToLowerInvariant())
            {
                case "steel": return "Steel";
                case "concrete": return "Concrete";
                case "timber": return "Timber";
                default: return "Other";
            }
        }

        public static LibMaterial ToLibrary(CatalogMaterial m)
        {
            return new LibMaterial { Name = m.Name, Family = FamilyOf(m) };
        }

        public static LibSection ToLibrary(CatalogSection s)
        {
            string name = s.Name.Trim();
            return new LibSection
            {
                Name = name,
                Shape = LibSection.Catalog,
                CatalogShape = s.ShapeId,
                CatalogTable = s.Table,
                CatalogFile = s.File,
                CatalogName = name == s.Name ? null : s.Name
            };
        }
    }
}
