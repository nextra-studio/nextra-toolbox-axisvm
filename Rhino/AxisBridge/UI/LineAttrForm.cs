using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Library;
using AxisBridge.Loads;
using Rhino;

namespace AxisBridge.UI
{
    /// <summary>
    /// AxLine dialog: element type at the top, then only the tabs that apply to that type
    /// (member properties, spring/link data, line support, loads, parts).
    /// </summary>
    public class LineAttrForm : AttrFormBase
    {
        public static readonly string[] ScalarKeys =
        {
            AxKeys.Type, AxKeys.Material, AxKeys.Section, AxKeys.Release, AxKeys.LocalAxis, AxKeys.Eccentricity,
            AxKeys.Stiffness, AxKeys.CoupledDof, AxKeys.HingeDomains, AxKeys.Support, AxKeys.Parts
        };
        public static readonly string[] FamilyKeys = { AxKeys.PointLoad, AxKeys.DistLoad };

        private readonly ComboField type;
        private readonly ComboField material;
        private readonly ComboField section;
        private readonly ReleaseField release;
        private readonly TextField localAxis;
        private readonly TextField eccentricity;
        private readonly TextField stiffness;
        private readonly TextField coupled;
        private readonly TextField hingeDomains;
        private readonly SupportField support;
        private readonly TextField parts;

        private readonly TabPage pageMember, pageLink, pageSupport, pageLoads, pageParts;
        private readonly FieldPage.Row rowRelease, rowAxis, rowEcc, rowStiff, rowCoupled, rowHinge, rowRigidNote, rowSpringNote;

        private static readonly Dictionary<string, string> DefaultMaterial = new Dictionary<string, string>
        {
            { AxSchema.Beam, "S355" }, { AxSchema.Rib, "C30/37" }, { AxSchema.Truss, "S355" }
        };
        private static readonly Dictionary<string, string> DefaultSection = new Dictionary<string, string>
        {
            { AxSchema.Beam, "IPE300" }, { AxSchema.Rib, "300x500" }, { AxSchema.Truss, "CHS168.3x8" }
        };
        private const string DefaultStiffness = "1e6,1e6,1e6,0,0,0";
        private const string DefaultCoupled = "1,1,1,1,1,1";

        public LineAttrForm(SelectionState state, AxLibrary library, RhinoDoc doc)
            : base("AxLine - member attributes",
                   state.Count == 1 ? "1 curve selected" : state.Count + " curves selected",
                   state, library, doc, new Size(860, 640))
        {
            type = TypeField(AxKeys.Type, AxSchema.LineTypes, AxSchema.LineTypeLabels, "Element type");

            // ---- Member ---------------------------------------------------
            var member = new FieldPage();
            member.Section("Profile");
            Button matLib, secLib;
            material = MaterialField(out matLib);
            member.Add("Material", material.Control, "From the document library, or type an AxisVM catalog name (S355, C30/37, GL24h).", matLib);
            section = SectionField(out secLib);
            member.Add("Cross-section", section.Control, "Library name, catalog profile (IPE300, HEA200) or b x h in mm (300x500). Tapered: \"HEA200; HEA300\".", secLib);

            member.Section("Connections and orientation");
            release = Track(new ReleaseField());
            rowRelease = member.Add("End releases", release.Control);
            localAxis = Track(new TextField(AxKeys.LocalAxis, "Auto"));
            rowAxis = member.Add("Local z reference", localAxis.Control, "Blank = AxisVM automatic orientation, or a direction vector, e.g. 0,0,1.");
            eccentricity = Track(new TextField(AxKeys.Eccentricity, "none"));
            rowEcc = member.Add("Eccentricity", eccentricity.Control, "Offset ex,ey,ez in " + DocUnitName + " (one value for both ends, or start=..; end=..).");
            pageMember = AddPage("Member", member);

            // ---- Spring / link / hinge ---------------------------------------
            var link = new FieldPage();
            link.Section("Spring, link and edge hinge");
            rowSpringNote = link.AddWide(Theme.Hint("Spring element between the two curve end points (global directions)."));
            stiffness = Track(new TextField(AxKeys.Stiffness, "kx, ky, kz, kxx, kyy, kzz"));
            rowStiff = link.Add("Stiffness", stiffness.Control, "6 values: kN/m for translations, kNm/rad for rotations (edge hinge: per metre). 0 = free.");
            coupled = Track(new TextField(AxKeys.CoupledDof, "x, y, z, xx, yy, zz"));
            rowCoupled = link.Add("Coupled DOFs", coupled.Control, "6 flags, 1 = coupled (rigid), 0 = free. E.g. 1,1,1,0,0,0 couples translations only.");

            hingeDomains = Track(new TextField(AxKeys.HingeDomains, "Auto - every domain sharing this edge"));
            var pick = new Button { Text = "Pick...", AutoSize = true, Height = 25 };
            pick.Click += (s, e) => RequestSurfacePick("Select the domains this edge hinge releases (Enter when done)", ids =>
            {
                var parts = new List<string>();
                foreach (Guid g in ids) parts.Add(g.ToString());
                hingeDomains.Assign(string.Join("; ", parts.ToArray()));
            });
            rowHinge = link.Add("Hinged domains", hingeDomains.Control, "Leave blank to release every domain whose edge runs along this line, or Pick specific surfaces.", pick);
            rowRigidNote = link.AddWide(Theme.Hint("A rigid link is created as an AxisVM rigid body: both end nodes move together as one rigid element. It needs no further input."));
            pageLink = AddPage("Spring / link", link);

            // ---- Support ------------------------------------------------------
            var sup = new FieldPage();
            sup.Section("Line support");
            support = Track(new SupportField(true));
            sup.Add("Support", support.Control, "Creates an AxisVM line (edge) support along the whole curve, e.g. the bottom edge of a wall.");
            pageSupport = AddPage("Support", sup);

            // ---- Loads --------------------------------------------------------
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            var gridDist = Grid(LoadKind.MemberDist, AxKeys.DistLoad, "Distributed loads",
                "One row per load. Positions: ratio 0..1 along the curve, or length in " + DocUnitName + " from the curve start. Leave the 'end' cells blank for a uniform load.");
            var gridPoint = Grid(LoadKind.MemberPoint, AxKeys.PointLoad, "Point loads",
                "One row per load, e.g. the same load in Dead and Live = two rows (use Duplicate).");
            split.Panel1.Controls.Add(gridDist);
            split.Panel2.Controls.Add(gridPoint);
            pageLoads = AddPage("Loads", split);
            Shown += (s, e) => { try { split.SplitterDistance = split.Height / 2; } catch { } };

            // ---- Parts --------------------------------------------------------
            var partPage = new FieldPage();
            partPage.Section("AxisVM parts");
            parts = Track(new TextField(AxKeys.Parts, "e.g. Level 2; Steel framing"));
            partPage.Add("Parts", parts.Control, "Semicolon-separated part names. The element is added to every named part.");
            pageParts = AddPage("Parts", partPage);

            LoadFields();
            type.Changed += (s, e) => { ApplyTypeDefaults(); UpdateVisibility(); };
            UpdateVisibility();
        }

        private string CurrentType { get { return type.ShowsVaries ? null : type.Value; } }

        private void LoadFields()
        {
            // Legacy spellings ("rigid link", "nodenodelink") are shown as their canonical type.
            FieldState t = State.Get(AxKeys.Type);
            string typeNow = t.Kind == FieldStateKind.Same ? (AxSchema.NormalizeLineType(t.Value) ?? AxSchema.Beam) : AxSchema.Beam;
            // Curves that have no type yet are being tagged for the first time: write the shown defaults.
            // Already-tagged curves only get what the user actually changes.
            bool fresh = t.Kind == FieldStateKind.AbsentAll;
            type.Load(t.Kind == FieldStateKind.Same ? FieldState.Of(typeNow) : t, AxSchema.Beam, fresh);

            material.Load(State.Get(AxKeys.Material), Lookup(DefaultMaterial, typeNow), fresh);
            section.Load(State.Get(AxKeys.Section), Lookup(DefaultSection, typeNow), fresh);
            release.Load(State.Get(AxKeys.Release), typeNow == AxSchema.Truss ? "" : "fix", fresh);
            localAxis.Load(State.Get(AxKeys.LocalAxis), "", fresh);
            eccentricity.Load(State.Get(AxKeys.Eccentricity), "", fresh);
            stiffness.Load(State.Get(AxKeys.Stiffness), typeNow == AxSchema.Spring || typeNow == AxSchema.EdgeHinge ? DefaultStiffness : "", fresh);
            coupled.Load(State.Get(AxKeys.CoupledDof), typeNow == AxSchema.NodeLink ? DefaultCoupled : "", fresh);
            hingeDomains.Load(State.Get(AxKeys.HingeDomains), "", fresh);
            support.Load(State.Get(AxKeys.Support), "", fresh);
            parts.Load(State.Get(AxKeys.Parts), "", fresh);
        }

        private static string Lookup(Dictionary<string, string> d, string key)
        {
            string v;
            return key != null && d.TryGetValue(key, out v) ? v : "";
        }

        /// <summary>
        /// When the user switches type, swap in that type's defaults - but only where the field still
        /// holds another type's default (or nothing), so a deliberately typed value is never lost.
        /// </summary>
        private void ApplyTypeDefaults()
        {
            string t = CurrentType;
            if (t == null) return;

            SwapDefault(material, DefaultMaterial, t);
            SwapDefault(section, DefaultSection, t);

            if ((t == AxSchema.Spring || t == AxSchema.EdgeHinge) && !stiffness.ShowsVaries && (stiffness.Value.Length == 0 || !stiffness.Touched))
                stiffness.Assign(stiffness.Value.Length == 0 ? DefaultStiffness : stiffness.Value);
            if (t == AxSchema.NodeLink && !coupled.ShowsVaries && (coupled.Value.Length == 0 || !coupled.Touched))
                coupled.Assign(coupled.Value.Length == 0 ? DefaultCoupled : coupled.Value);
            // Profile fields become relevant after a type change: make sure their shown values get written.
            if (t == AxSchema.Beam || t == AxSchema.Rib || t == AxSchema.Truss)
            {
                if (!material.ShowsVaries && !material.Touched && material.Value.Length > 0) material.Assign(material.Value);
                if (!section.ShowsVaries && !section.Touched && section.Value.Length > 0) section.Assign(section.Value);
            }
            if (t == AxSchema.Truss && !release.ShowsVaries) release.Assign("");
        }

        private static void SwapDefault(ComboField f, Dictionary<string, string> defaults, string type)
        {
            if (f.ShowsVaries) return;
            string want;
            if (!defaults.TryGetValue(type, out want)) return;
            string have = f.Value;
            if (have.Length == 0 || defaults.ContainsValue(have)) f.Assign(want);
        }

        private void UpdateVisibility()
        {
            string t = CurrentType;     // null = mixed selection: show everything
            bool all = t == null;
            bool frame = all || t == AxSchema.Beam || t == AxSchema.Rib || t == AxSchema.Truss;
            bool bending = all || t == AxSchema.Beam || t == AxSchema.Rib;
            bool spring = all || t == AxSchema.Spring;
            bool hinge = all || t == AxSchema.EdgeHinge;
            bool nodeLink = all || t == AxSchema.NodeLink;
            bool rigid = all || t == AxSchema.RigidLink;

            rowRelease.Visible = bending;
            rowAxis.Visible = bending;
            rowEcc.Visible = bending;
            rowSpringNote.Visible = spring;
            rowStiff.Visible = spring || hinge;
            rowCoupled.Visible = nodeLink;
            rowHinge.Visible = hinge;
            rowRigidNote.Visible = rigid;

            var pages = new List<TabPage>();
            if (frame) pages.Add(pageMember);
            if (spring || hinge || nodeLink || rigid) pages.Add(pageLink);
            if (all || t == AxSchema.Rib || t == AxSchema.Simple) pages.Add(pageSupport);
            if (bending) pages.Add(pageLoads);
            pages.Add(pageParts);
            ShowPages(pages);
        }

        protected override string ValidateInput()
        {
            string t = CurrentType;
            if ((t == AxSchema.Spring || t == AxSchema.EdgeHinge) && stiffness.Touched && stiffness.Value != "-" && CountNumbers(stiffness.Value) != 6)
                return "Stiffness needs 6 comma-separated numbers (kx, ky, kz, kxx, kyy, kzz).";
            if (t == AxSchema.NodeLink && coupled.Touched && coupled.Value != "-" && CountNumbers(coupled.Value) != 6)
                return "Coupled DOFs needs 6 comma-separated flags (1 or 0).";
            return null;
        }

        private static int CountNumbers(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? 0 : s.Split(',').Length;
        }
    }
}
