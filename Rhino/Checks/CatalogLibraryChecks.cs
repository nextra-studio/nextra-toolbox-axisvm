// Rhino side of the AxisVM catalog checks. Run through scripts/Check-CatalogLibrary.ps1.
//
//   offline : CSV round trip with awkward names, LibSection JSON (old documents unchanged).
//   catalog : reads the CSVs the AxisVM add-on exported (step 1 of the script) the way AxLibrary does,
//             drives the picker and the library form, and writes an AxExport-style JSON - one beam per picked
//             catalog section, one per catalog material - for the AxisVM import checks.
//
// Args: <out folder> offline | <out folder> <catalog folder>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AxisBridge.Export;
using AxisBridge.Library;
using AxisBridge.UI;
using Newtonsoft.Json;

internal static class CatalogLibraryChecks
{
    private static int failures;
    private static string output;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        try
        {
            CsvRoundTrip();
            LibraryJson();
            if (args.Length > 1 && args[1] != "offline")
            {
                AxCatalog c = ReadExported(Path.GetFullPath(args[1]));
                PickerAndLibrary(c);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  exception: " + ex);
            failures++;
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ==================================================================
    // Offline
    // ==================================================================

    private static void CsvRoundTrip()
    {
        string dir = Path.Combine(output, "csv-roundtrip");
        var c = new AxCatalog();
        c.Materials.Add(new CatalogMaterial { Code = "Eurocode", Type = "Steel", Name = "S 275 NH/NLH", Standard = "EN 10025-3" });
        c.Materials.Add(new CatalogMaterial { Code = "Eurocode", Type = "Brick", Name = "Solid Clay Brick M2.5 G" });
        c.Materials.Add(new CatalogMaterial { Code = "Eurocode", Type = "Timber", Kind = "Glulam", Name = "GL 24h", Standard = "EN 14080:2013" });
        string[] awkward =
        {
            "CFCHS 508,0 x 12,5", "CFRHS 500  x  300 x 12,0", "Lindab C 350/2.5", "L 8X6X1-3/4", "L 9X4X 5/8",
            "TPB (100*50)", "Name \"quoted\"", " leading and trailing ", "ROR 1016,00*  20,0", "Z CF 400x2,5x85/75"
        };
        foreach (string n in awkward)
            c.Sections.Add(new CatalogSection { Shape = "I shapes", ShapeId = 3, Table = "C CF (Pruszyński) Polish, cold formed", File = "X_1", Region = "PL", Name = n });
        c.Save(dir);

        AxCatalog back = AxCatalog.Load(dir);
        Check(back.Materials.Count == 3 && back.Sections.Count == awkward.Length, "csv: row counts survive the round trip");
        Check(back.Sections.Select(s => s.Name).SequenceEqual(awkward), "csv: names with commas, quotes, double and edge spaces come back byte-identical");
        Check(back.Sections.All(s => s.Table == "C CF (Pruszyński) Polish, cold formed" && s.ShapeId == 3 && s.File == "X_1"), "csv: table (unicode + comma), shape id and file survive");
        Check(back.Materials[2].Kind == "Glulam" && back.Materials[2].Standard == "EN 14080:2013", "csv: material kind and standard survive");
        Check(AxCatalog.Exists(dir) && !File.Exists(Path.Combine(dir, AxCatalog.SectionsFile + ".tmp")), "csv: both files written, no temp file left");
        Check(!AxCatalog.Exists(Path.Combine(output, "does-not-exist")), "csv: a folder without a catalog is reported as not exported");
        string first = File.ReadAllLines(Path.Combine(dir, AxCatalog.SectionsFile))[0];
        Check(first.TrimStart('﻿') == "Shape,ShapeId,Table,File,Region,Name", "csv: sections header");

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Check(AxCatalog.DefaultFolder() == Path.Combine(appData, "AxisBridge") && Path.IsPathRooted(AxCatalog.DefaultFolder()),
            "folder: catalog folder is the user's %APPDATA%\\AxisBridge, resolved at run time (" + AxCatalog.DefaultFolder() + ")");
    }

    private static void LibraryJson()
    {
        // A document saved before the catalog existed must load and save unchanged.
        const string old = "{\"materials\":[{\"name\":\"S355\",\"family\":\"Steel\"}],\"sections\":[{\"name\":\"IPE300\",\"shape\":\"Catalog\",\"b\":0.0,\"h\":0.0,\"t\":0.0}],\"loadCases\":[]}";
        AxLibrary lib = JsonConvert.DeserializeObject<AxLibrary>(old);
        Check(JsonConvert.SerializeObject(lib, Formatting.None) == old, "json: a library without catalog fields serializes exactly as before");

        lib.Sections.Add(AxCatalog.ToLibrary(new CatalogSection { Name = "L 250x28", ShapeId = 20, Table = "LE Iyz=0 (EN 10056-1) Equal angles", File = "L_EN_IYZ0" }));
        lib.Sections.Add(AxCatalog.ToLibrary(new CatalogSection { Name = " 50X30X  4", ShapeId = 9, Table = "[] Dunaújvárosi Hungarian rectangular shapes", File = "DU_TEGLA" }));
        AxLibrary back = JsonConvert.DeserializeObject<AxLibrary>(JsonConvert.SerializeObject(lib));
        LibSection s = back.FindSection("L 250x28");
        Check(s != null && s.Shape == LibSection.Catalog && s.CatalogShape == 20 && s.CatalogFile == "L_EN_IYZ0" && s.CatalogTable.StartsWith("LE Iyz=0") && s.CatalogName == null,
            "json: catalog shape, table and file round-trip");
        LibSection p = back.FindSection("50X30X  4");
        Check(p != null && p.Name == "50X30X  4" && p.CatalogName == " 50X30X  4", "json: a padded catalog name is listed trimmed and keeps AxisVM's spelling");
        Check(s != null && s.Describe() == "AxisVM catalog: LE Iyz=0 (EN 10056-1) Equal angles", "json: grid shows the source table");
    }

    // ==================================================================
    // The CSVs exported by the AxisVM add-on
    // ==================================================================

    private static AxCatalog ReadExported(string folder)
    {
        Check(AxCatalog.Exists(folder), "catalog: the add-on's two CSV files are found in " + folder);
        AxCatalog c = AxCatalog.Load(folder);
        Console.WriteLine("      " + c.Materials.Count + " materials, " + c.Sections.Count + " sections, written " + c.Written.ToString("yyyy-MM-dd HH:mm:ss"));

        Check(c.Materials.Count == 84 && c.Materials.All(m => m.Code == "Eurocode"), "catalog: 84 Eurocode materials read");
        foreach (string n in new[] { "S 235", "S 355", "S 460 Q/QL/QL1", "C30/37", "C90/105", "C24", "GL 24h", "KERTO-S", "CLT", "Solid Clay Brick M2.5 G" })
            Check(c.Materials.Any(m => m.Name == n), "catalog: material '" + n + "' with AxisVM's exact spelling");
        Check(Kind(c, "GL 24h") == "Timber/Glulam" && Kind(c, "C24") == "Timber/Softwood" && Kind(c, "D30") == "Timber/Hardwood" && Kind(c, "KERTO-S") == "Timber/LVL" && Kind(c, "CLT") == "Timber/CLT"
              && Kind(c, "S 355") == "Steel/" && Kind(c, "C30/37") == "Concrete/", "catalog: grouped by type, timber by kind");
        Check(c.Materials.Select(m => m.Type).Distinct().SequenceEqual(new[] { "Steel", "Concrete", "Timber", "Brick" }), "catalog: materials in AxisVM order");

        Check(c.Sections.GroupBy(s => s.File + "|" + s.Table).Count() == 165 && c.Sections.Count == 17049, "catalog: 165 tables, 17049 profiles read");
        Check(Names(c, "HEA_PL").First() == "HEA 100" && Names(c, "HEA_PL").Last() == "HEA 1000", "catalog: table order kept (HEA 100 ... HEA 1000)");
        Check(c.Sections.Any(s => s.Name == "CFCHS 508,0 x 12,5") && c.Sections.Any(s => s.Name == " 50X30X  4") && c.Sections.Any(s => s.Name == "IPE  80"),
            "catalog: names with commas, leading spaces and double spaces read exactly");
        Check(c.Sections.All(s => s.ShapeId > 0 && s.Shape.Length > 0 && s.Table.Length > 0 && s.File.Length > 0), "catalog: every row has shape, table and file");
        return c;
    }

    private static string Kind(AxCatalog c, string name)
    {
        CatalogMaterial m = c.Materials.FirstOrDefault(x => x.Name == name);
        return m == null ? "?" : m.Type + "/" + m.Kind;
    }

    private static List<string> Names(AxCatalog c, string file)
    {
        return c.Sections.Where(s => s.File == file).Select(s => s.Name).ToList();
    }

    // ==================================================================
    // Picker -> library form -> export JSON
    // ==================================================================

    private static void PickerAndLibrary(AxCatalog c)
    {
        AxLibrary start = AxLibrary.Defaults();

        // Materials picker
        using (CatalogPickerForm f = CatalogPickerForm.ForMaterials(c, start.MaterialNames()))
        {
            Show(f);
            List<string> tree = f.TreeForTest();
            Check(tree.Count > 0 && tree[0].StartsWith("Eurocode") && tree.Any(l => l.Trim().StartsWith("Timber")) && tree.Any(l => l.Trim().StartsWith("Glulam")),
                "picker: material tree is Code > Type > Kind (" + string.Join(" | ", tree.Select(l => l.Trim()).Take(9).ToArray()) + ")");
            f.SearchForTest("gl24");
            Check(f.ListedForTest == 2, "picker: search 'gl24' finds GL 24c and GL 24h (" + f.StatusForTest + ")");
            f.SearchForTest("");
            Save(f, "picker-materials.png");
            f.TickForTest("S 355", "C 30/37", "C30/37", "GL 24h", "S 235");
            List<string> picked = f.CollectForTest().Cast<CatalogMaterial>().Select(m => m.Name).ToList();
            // C30/37 is already in the default library: shown grey and cannot be ticked.
            Check(picked.SequenceEqual(new[] { "S 235", "S 355", "GL 24h" }), "picker: ticked materials returned in catalog order, library entries skipped (" + string.Join(", ", picked.ToArray()) + ")");
        }

        // Sections picker
        using (CatalogPickerForm f = CatalogPickerForm.ForSections(c, start.SectionNames()))
        {
            Show(f);
            List<string> tree = f.TreeForTest();
            Check(tree.Count > 0 && tree.Any(l => l.StartsWith("I shapes")) && tree.Any(l => l.Trim().StartsWith("IPE (EN 10365) I-beams")),
                "picker: section tree is Shape > Table");
            f.SearchForTest("hea300");
            Check(f.ListedForTest >= 1 && f.StatusForTest.Contains("match"), "picker: search ignores spaces/case ('hea300' -> " + f.StatusForTest + ")");
            f.SearchForTest("");
            Save(f, "picker-sections.png");
        }

        // Library form: add a spread of materials and sections exactly as the picker hands them over.
        List<CatalogSection> sample = Sample(c);
        AxLibrary result;
        using (var lib = new LibraryForm(start, LibraryForm.TabSections))
        {
            Show(lib);
            lib.AddFromCatalogForTest(c.Materials, sample);
            Save(lib, "library-sections.png");
            result = lib.CollectForTest();
        }
        int union = start.MaterialNames().Union(c.Materials.Select(m => m.Name), StringComparer.OrdinalIgnoreCase).Count();
        Check(result.Materials.Count == union && result.Materials.Count(m => m.Name == "C30/37") == 1,
            "library: catalog materials added next to the defaults, C30/37 not duplicated (" + result.Materials.Count + ")");
        Check(result.Materials.First(m => m.Name == "GL 24h").Family == "Timber" && result.Materials.First(m => m.Name == "S 460 Q/QL/QL1").Family == "Steel"
              && result.Materials.First(m => m.Name == "Solid Clay Brick M2.5 G").Family == "Other", "library: family follows the AxisVM material type");
        Check(sample.All(s => { LibSection l = result.FindSection(s.Name.Trim()); return l != null && l.Shape == LibSection.Catalog && l.CatalogFile == s.File && l.CatalogShape == s.ShapeId && l.CatalogTable == s.Table; }),
            "library: every picked section keeps its table, file and shape through the grid (" + sample.Count + " sections)");
        List<CatalogSection> padded = sample.Where(s => s.Name != s.Name.Trim()).ToList();
        Check(padded.Count > 0 && padded.All(s => { LibSection l = result.FindSection(s.Name.Trim()); return l.Name == s.Name.Trim() && l.CatalogName == s.Name; })
              && sample.Where(s => s.Name == s.Name.Trim()).All(s => result.FindSection(s.Name).CatalogName == null),
            "library: catalog names padded with spaces (" + padded.Count + ", e.g. '" + (padded.Count > 0 ? padded[0].Name : "") + "') are listed trimmed and keep the exact spelling for AxisVM");
        Check(result.FindSection("300x500").Shape == LibSection.Rectangle && result.FindSection("IPE300").CatalogFile == null, "library: existing rows untouched");

        // AxExport-style JSON: one beam per catalog section and one per catalog material.
        var model = new ExportModelDto { Source = new SourceDto { File = "CatalogLibraryChecks.3dm", UnitSystem = "Meters", Tolerance = 0.001 }, Library = result };
        int i = 0;
        foreach (CatalogSection s in sample)
            model.Lines.Add(Beam("sec-" + i, i++, 0, "S 235", s.Name.Trim()));
        int j = 0;
        foreach (CatalogMaterial m in c.Materials)
            model.Lines.Add(Beam("mat-" + j, j++, 20, m.Name, "HEA 300"));
        string json = Path.Combine(output, "catalog-export.json");
        File.WriteAllText(json, JsonConvert.SerializeObject(model, Formatting.Indented));
        Console.WriteLine("      export for the AxisVM import checks: " + json + " (" + model.Lines.Count + " beams)");
    }

    /// <summary>
    /// First and last profile of every table, names unique (the library is keyed by name). The Iyz=0 angle
    /// tables go first, so their names win over the same names in the ordinary tables - that is the case
    /// that proves the import takes a profile from the table that was picked.
    /// </summary>
    private static List<CatalogSection> Sample(AxCatalog c)
    {
        var picked = new List<CatalogSection>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HEA 300" };  // used by the material beams
        Action<CatalogSection> add = s => { if (s != null && names.Add(s.Name.Trim())) picked.Add(s); };

        add(c.Sections.FirstOrDefault(s => s.File == "L_EN_IYZ0" && s.Name == "L 250x28"));
        foreach (var t in c.Sections.Where(s => s.File.EndsWith("_IYZ0")).GroupBy(s => s.File)) { add(t.First()); add(t.Last()); }
        add(c.Sections.FirstOrDefault(s => s.File == "B_NEGY" && s.Name == "78x80"));      // looks like a "b x h" rectangle
        foreach (var t in c.Sections.GroupBy(s => s.File)) { add(t.First()); add(t.Last()); }
        return picked;
    }

    private static LineDto Beam(string guid, int i, double y, string material, string section)
    {
        var l = new LineDto { Guid = guid, Start = new[] { i * 2.0, y, 0 }, End = new[] { i * 2.0, y, 3.0 } };
        l.Attrs["Ax_Type"] = "Beam";
        l.Attrs["Ax_Material"] = material;
        l.Attrs["Ax_Section"] = section;
        return l;
    }

    private static void Show(Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-20000, -20000);
        f.ShowInTaskbar = false;
        f.Show();
        Application.DoEvents();
    }

    private static void Save(Form f, string file)
    {
        f.PerformLayout();
        Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(Path.Combine(output, file), ImageFormat.Png);
        }
    }
}
