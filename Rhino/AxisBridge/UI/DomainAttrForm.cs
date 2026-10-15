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
            AxKeys.SpanDirection, AxKeys.EdgeSpacing, AxKeys.SpanTolerance, AxKeys.LoadTargets, AxKeys.Parts
        };
        public static readonly string[] FamilyKeys = { AxKeys.DistLoad, AxKeys.PointLoad };

        private readonly ComboField feType;
        private readonly ComboField material;
        private readonly TextField thickness;
        private readonly TextField localAxis;
        private readonly TextField eccentricity;
        private readonly SpanField spanDir;
        private readonly TargetsField targets;
        private PanelPreview preview;
        private readonly TextField edgeSpacing;
        private readonly TextField spanTolerance;
        private readonly TextField parts;

        private readonly TabPage pageDomain, pagePanel, pageLoads, pageParts;
        private readonly FieldPage.Row rowSpan, rowSpacing, rowTolerance, rowTwoWayNote, rowTargets, rowOneWayNote, rowTwoWaySection, rowOneWaySection;

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

            // Two-way: where the load goes.
            rowTwoWaySection = pan.Section("Two-way load panel - load transfer");
            rowTwoWayNote = pan.AddWide(Theme.Hint("The load is shared by the members it rests on. Leave it automatic, or pick exactly which beams / walls / columns (curves or points) carry it - e.g. a slab spanning between two beams only."));
            targets = Track(new TargetsField());
            targets.PickRequested += () => RequestPick("Pick the members (curves) and nodes (points) that receive this panel's load - Enter when done",
                Rhino.DocObjects.ObjectType.Curve | Rhino.DocObjects.ObjectType.Point, ids => targets.AddPicked(ids));
            rowTargets = pan.Add("Load goes to", targets.Control);

            // One-way: span direction.
            rowOneWaySection = pan.Section("One-way load panel - span");
            rowOneWayNote = pan.AddWide(Theme.Hint("The load spans in one direction to the edges perpendicular to it. The blue arrow in the viewport shows the span of every selected panel while this dialog is open."));
            spanDir = Track(new SpanField());
            spanDir.PickRequested += () => RequestDirectionPick("Span direction", v =>
            {
                string text = SpanTools.VectorText(v);
                if (text.Length > 0) spanDir.SetPicked(text);
            });
            rowSpan = pan.Add("Span direction", spanDir.Control,
                "Auto = the shorter span of the panel's smallest enclosing rectangle - also for L-shaped or skewed panels. Directions are in the global XY plane.");
            edgeSpacing = Track(new TextField(AxKeys.EdgeSpacing, "blank = one panel over the whole bay"));
            rowSpacing = pan.Add("Edge strip width", edgeSpacing.Control, "Optional tributary edge-strip width in " + DocUnitName + "; creates two edge strips plus a centre panel.");
            spanTolerance = Track(new TextField(AxKeys.SpanTolerance, "blank = 5"));
            rowTolerance = pan.Add("Angle tolerance [deg]", spanTolerance.Control,
                "Edges and beams inside the panel at 90 deg +/- this angle to the span receive its load (e.g. 10 = 80 to 100 deg). Also how much a side may bend and still count as straight.");
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
            spanDir.Changed += (s, e) => UpdatePreview();
            targets.Changed += (s, e) => UpdatePreview();
            Shown += (s, e) =>
            {
                // Live arrows / highlights in the viewport while the dialog is open.
                if (preview == null && Objects != null)
                {
                    preview = new PanelPreview(Doc, Objects);
                    preview.Enabled = true;
                }
                UpdatePreview();
            };
            feType.Changed += (s, e) =>
            {
                // Turning a load panel into a domain: its material / thickness now matter - write what is shown.
                string t2 = CurrentType;
                if (t2 != null && !AxSchema.IsLoadPanel(t2))
                {
                    if (!material.ShowsVaries && !material.Touched && material.Value.Length > 0) material.Assign(material.Value);
                    if (!thickness.ShowsVaries && !thickness.Touched && thickness.Value.Length > 0) thickness.Assign(thickness.Value);
                }
                UpdateVisibility();
                UpdatePreview();
            };
            UpdateVisibility();
        }

        private void UpdatePreview()
        {
            if (preview == null) return;
            string t = CurrentType;
            preview.SpanValue = t == AxSchema.OneWayLoadPanel ? spanDir.PreviewValue : null;
            preview.SetTargets(t == AxSchema.LoadPanel ? targets.Ids : new List<Guid>());
            preview.Refresh();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && preview != null)
            {
                preview.Enabled = false;
                preview.Refresh();
                preview = null;
            }
            base.Dispose(disposing);
        }

        private string CurrentType { get { return feType.ShowsVaries ? null : feType.Value; } }

        private void LoadFields()
        {
            FieldState t = State.Get(AxKeys.FeType);
            string typeNow = t.Kind == FieldStateKind.Same ? (AxSchema.NormalizeSurfaceType(t.Value) ?? AxSchema.Shell) : AxSchema.Shell;
            // Surfaces without a type are being tagged for the first time: write the shown defaults.
            bool fresh = t.Kind == FieldStateKind.AbsentAll;
            feType.Load(t.Kind == FieldStateKind.Same ? FieldState.Of(typeNow) : t, AxSchema.Shell, fresh);

            material.Load(State.Get(AxKeys.Material), "C30/37", fresh);
            thickness.Load(State.Get(AxKeys.Thickness), DocLength(0.2), fresh);
            localAxis.Load(State.Get(AxKeys.LocalAxis), "", fresh);
            eccentricity.Load(State.Get(AxKeys.Eccentricity), "", fresh);
            spanDir.Load(State.Get(AxKeys.SpanDirection), "", fresh);
            targets.Load(State.Get(AxKeys.LoadTargets), "", fresh);
            edgeSpacing.Load(State.Get(AxKeys.EdgeSpacing), "", fresh);
            spanTolerance.Load(State.Get(AxKeys.SpanTolerance), "", fresh);
            parts.Load(State.Get(AxKeys.Parts), "", fresh);
        }

        private void UpdateVisibility()
        {
            string t = CurrentType;
            bool all = t == null;
            bool domain = all || !AxSchema.IsLoadPanel(t);
            bool panel = all || AxSchema.IsLoadPanel(t);
            bool oneWay = all || t == AxSchema.OneWayLoadPanel;

            bool twoWay = all || t == AxSchema.LoadPanel;
            rowTwoWaySection.Visible = twoWay;
            rowTwoWayNote.Visible = twoWay;
            rowTargets.Visible = twoWay;
            rowOneWaySection.Visible = oneWay;
            rowOneWayNote.Visible = oneWay;
            rowSpan.Visible = oneWay;
            rowSpacing.Visible = oneWay;
            rowTolerance.Visible = oneWay;

            var pages = new List<TabPage>();
            if (domain) pages.Add(pageDomain);
            if (panel) pages.Add(pagePanel);
            pages.Add(pageLoads);
            if (domain) pages.Add(pageParts);
            ShowPages(pages);
        }

        protected override string ValidateInput()
        {
            if (CurrentType == AxSchema.LoadPanel && targets.IsIncomplete)
                return "'Only the members / nodes I pick' is selected, but nothing was picked. Pick the members, or switch back to Automatic.";
            if (CurrentType == AxSchema.OneWayLoadPanel && spanDir.Touched)
            {
                Rhino.Geometry.Vector3d v;
                if (!SpanTools.IsAutoLong(spanDir.Value) && !SpanTools.Resolve(spanDir.Value, null, out v) && spanDir.Value.Length > 0)
                    return "The custom span direction must be a vector like 1,0,0 (and not vertical).";
            }
            if (CurrentType == AxSchema.OneWayLoadPanel && spanTolerance.Touched && spanTolerance.Value.Trim().Length > 0)
            {
                double tol;
                if (!double.TryParse(spanTolerance.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out tol) || tol < 0 || tol >= 45)
                    return "The angle tolerance must be from 0 up to, but below, 45 degrees (blank = 5).";
            }
            if (CurrentType != null && !AxSchema.IsLoadPanel(CurrentType) && thickness.Touched && thickness.Value != "-")
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
