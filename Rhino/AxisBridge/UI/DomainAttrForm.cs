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
    /// AxDomain dialog. A surface becomes either an AxisVM domain (shell / plate / membrane, with
    /// material and thickness) or a load panel (which in AxisVM is NOT a domain - it only distributes
    /// loads onto the members around it, so it has no material, thickness, axis or parts).
    /// The tabs follow that choice.
    /// </summary>
    public class DomainAttrForm : AttrFormBase
    {
        public static readonly string[] ScalarKeys =
        {
            AxKeys.FeType, AxKeys.Material, AxKeys.Thickness, AxKeys.LocalAxis, AxKeys.Eccentricity,
            AxKeys.SpanDirection, AxKeys.EdgeSpacing, AxKeys.Parts
        };
        public static readonly string[] FamilyKeys = { AxKeys.DistLoad, AxKeys.PointLoad };

        private readonly ComboField feType;
        private readonly ComboField material;
        private readonly TextField thickness;
        private readonly TextField localAxis;
        private readonly TextField eccentricity;
        private readonly ComboField spanDir;
        private readonly TextField edgeSpacing;
        private readonly TextField parts;

        private readonly TabPage pageDomain, pagePanel, pageLoads, pageParts;
        private readonly FieldPage.Row rowSpan, rowSpacing, rowTwoWayNote;

        public DomainAttrForm(SelectionState state, AxLibrary library, RhinoDoc doc)
            : base("AxDomain - surface attributes",
                   state.Count == 1 ? "1 surface selected" : state.Count + " surfaces selected",
                   state, library, doc, new Size(820, 600))
        {
            feType = TypeField(AxKeys.FeType, AxSchema.SurfaceTypes, AxSchema.SurfaceTypeLabels, "Surface type");

            // ---- Domain -------------------------------------------------------
            var dom = new FieldPage();
            dom.Section("Domain (finite elements)");
            Button matLib;
            material = MaterialField(out matLib);
            dom.Add("Material", material.Control, "From the document library, or an AxisVM catalog name (C30/37, S355, GL24h).", matLib);
            thickness = Track(new TextField(AxKeys.Thickness, "e.g. " + DocLength(0.2)));
            dom.Add("Thickness", thickness.Control, "In " + DocUnitName + ".");
            localAxis = Track(new TextField(AxKeys.LocalAxis, "Auto"));
            dom.Add("Local x reference", localAxis.Control, "Blank = AxisVM automatic, or a direction vector such as 1,0,0.");
            eccentricity = Track(new TextField(AxKeys.Eccentricity, "none"));
            dom.Add("Eccentricity", eccentricity.Control, "Offset of the mid-plane along the surface normal, in " + DocUnitName + " (for a flat slab the z value is used, e.g. 0,0,-100).");
            pageDomain = AddPage("Domain", dom);

            // ---- Load panel ---------------------------------------------------
            var pan = new FieldPage();
            pan.Section("Load panel");
            rowTwoWayNote = pan.AddWide(Theme.Hint("A two-way load panel distributes its loads to all surrounding members/edges. It needs no further input - add loads on the Loads tab."));
            spanDir = Track(new ComboField(AxKeys.SpanDirection, true));
            spanDir.SetItems(new[]
            {
                new KeyValuePair<string, string>("Auto - along the shorter span", ""),
                new KeyValuePair<string, string>("Along global X", "x"),
                new KeyValuePair<string, string>("Along global Y", "y")
            });
            rowSpan = pan.Add("Span direction", spanDir.Control, "Direction the slab spans (load goes to the edges perpendicular to it). You can also type a vector, e.g. 1,1,0.");
            edgeSpacing = Track(new TextField(AxKeys.EdgeSpacing, "0 = one panel over the whole bay"));
            rowSpacing = pan.Add("Edge strip width", edgeSpacing.Control, "Optional tributary edge-strip width in " + DocUnitName + "; creates two edge strips plus a centre panel.");
            pagePanel = AddPage("Load panel", pan);

            // ---- Loads --------------------------------------------------------
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            var gridArea = Grid(LoadKind.SurfaceArea, AxKeys.DistLoad, "Area loads",
                "One row per load - several load cases on the same surface are simply several rows (Duplicate moves to the next load case). Negative qz = downwards.");
            var gridPoint = Grid(LoadKind.SurfacePoint, AxKeys.PointLoad, "Point loads",
                "Location in " + DocUnitName + " (use Pick to click it in the viewport). The point should lie on the surface.");
            split.Panel1.Controls.Add(gridArea);
            split.Panel2.Controls.Add(gridPoint);
            pageLoads = AddPage("Loads", split);
            Shown += (s, e) => { try { split.SplitterDistance = (int)(split.Height * 0.55); } catch { } };

            // ---- Parts --------------------------------------------------------
            var partPage = new FieldPage();
            partPage.Section("AxisVM parts");
            parts = Track(new TextField(AxKeys.Parts, "e.g. Floor slabs; Level 1"));
            partPage.Add("Parts", parts.Control, "Semicolon-separated part names (domains only - AxisVM parts cannot hold load panels).");
            pageParts = AddPage("Parts", partPage);

            LoadFields();
            feType.Changed += (s, e) => UpdateVisibility();
            UpdateVisibility();
        }

        private string CurrentType { get { return feType.ShowsVaries ? null : feType.Value; } }

        private void LoadFields()
        {
            FieldState t = State.Get(AxKeys.FeType);
            string typeNow = t.Kind == FieldStateKind.Same ? (AxSchema.NormalizeSurfaceType(t.Value) ?? AxSchema.Shell) : AxSchema.Shell;
            feType.Load(t.Kind == FieldStateKind.Same ? FieldState.Of(typeNow) : t, AxSchema.Shell);

            material.Load(State.Get(AxKeys.Material), "C30/37");
            thickness.Load(State.Get(AxKeys.Thickness), DocLength(0.2));
            localAxis.Load(State.Get(AxKeys.LocalAxis), "");
            eccentricity.Load(State.Get(AxKeys.Eccentricity), "");
            spanDir.Load(State.Get(AxKeys.SpanDirection), "");
            edgeSpacing.Load(State.Get(AxKeys.EdgeSpacing), "");
            parts.Load(State.Get(AxKeys.Parts), "");
        }

        private void UpdateVisibility()
        {
            string t = CurrentType;
            bool all = t == null;
            bool domain = all || !AxSchema.IsLoadPanel(t);
            bool panel = all || AxSchema.IsLoadPanel(t);
            bool oneWay = all || t == AxSchema.OneWayLoadPanel;

            rowTwoWayNote.Visible = all || t == AxSchema.LoadPanel;
            rowSpan.Visible = oneWay;
            rowSpacing.Visible = oneWay;

            var pages = new List<TabPage>();
            if (domain) pages.Add(pageDomain);
            if (panel) pages.Add(pagePanel);
            pages.Add(pageLoads);
            if (domain) pages.Add(pageParts);
            ShowPages(pages);
        }

        protected override string ValidateInput()
        {
            if (CurrentType != null && !AxSchema.IsLoadPanel(CurrentType) && thickness.Touched)
            {
                double d;
                if (!double.TryParse(thickness.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out d) || d <= 0)
                    return "Thickness must be a positive number (in " + DocUnitName + ").";
            }
            return null;
        }
    }
}
