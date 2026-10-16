using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Library;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.UI
{
    /// <summary>
    /// Common frame of the AxPoint / AxLine / AxDomain dialogs: header, a type strip, tab pages that
    /// are shown only when they apply, Apply / Cancel, the document library, and picking in the
    /// viewport (the dialog closes with DialogResult.Retry, the command runs <see cref="PendingPick"/>
    /// and shows the same dialog again with all edits intact).
    /// </summary>
    public abstract class AttrFormBase : Form
    {
        protected readonly SelectionState State;
        protected readonly RhinoDoc Doc;
        public AxLibrary Library { get; private set; }
        public bool LibraryChanged { get; private set; }

        /// <summary>The change set, filled when the user presses Apply.</summary>
        public AttrEdit Result { get; private set; }

        /// <summary>Set before closing with Retry; the command runs it, then re-shows the dialog.</summary>
        public Action<RhinoDoc> PendingPick { get; set; }

        /// <summary>The objects being edited (set by the command before the dialog is shown).</summary>
        public IList<Rhino.DocObjects.RhinoObject> Objects { get; set; }

        protected readonly List<Field> Fields = new List<Field>();
        protected readonly List<LoadGrid> Grids = new List<LoadGrid>();
        protected readonly TabControl Tabs;
        protected readonly Panel TypeStrip;

        private readonly List<TabPage> allPages = new List<TabPage>();
        private readonly List<ComboField> materialFields = new List<ComboField>();
        private readonly List<ComboField> sectionFields = new List<ComboField>();

        protected AttrFormBase(string title, string subtitle, SelectionState state, AxLibrary library, RhinoDoc doc, Size size)
        {
            State = state;
            Library = library;
            Doc = doc;
            Theme.Dialog(this, title, size);

            Tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            TypeStrip = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(14, 10, 14, 6) };

            Button ok, cancel;
            Panel buttons = Theme.Buttons(this, "Apply", out ok, out cancel,
                state.Count > 1 ? "Only the fields you change are written; everything shown as ‹varies› keeps each object's own value." : null);
            ok.Click += (s, e) => Apply();

            Controls.Add(Tabs);
            Controls.Add(TypeStrip);
            Controls.Add(Theme.Header(title, subtitle));
            Controls.Add(Theme.Separator());
            Controls.Add(buttons);
        }

        // ==================================================================
        // Building blocks for the derived dialogs
        // ==================================================================

        protected T Track<T>(T f) where T : Field
        {
            Fields.Add(f);
            return f;
        }

        protected TabPage AddPage(string title, Control content)
        {
            var p = new TabPage(title) { Padding = new Padding(4), UseVisualStyleBackColor = true };
            p.Controls.Add(content);
            allPages.Add(p);
            Tabs.TabPages.Add(p);
            return p;
        }

        /// <summary>Shows only the given pages, in their original order, keeping the current tab if possible.</summary>
        protected void ShowPages(ICollection<TabPage> visible)
        {
            TabPage current = Tabs.SelectedTab;
            Tabs.SuspendLayout();
            Tabs.TabPages.Clear();
            foreach (TabPage p in allPages)
                if (visible.Contains(p)) Tabs.TabPages.Add(p);
            if (current != null && Tabs.TabPages.Contains(current)) Tabs.SelectedTab = current;
            Tabs.ResumeLayout();
        }

        /// <summary>The element type dropdown in the strip under the header.</summary>
        protected ComboField TypeField(string key, string[] values, string[] labels, string label)
        {
            var f = new ComboField(key, false);
            var pairs = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < values.Length; i++) pairs.Add(new KeyValuePair<string, string>(labels[i], values[i]));
            f.SetItems(pairs);
            f.Combo.Dock = DockStyle.None;
            f.Combo.Width = 330;
            f.Combo.Font = Theme.Bold;
            f.Combo.Location = new Point(130, 10);

            TypeStrip.Controls.Add(new Label { Text = label, Font = Theme.Bold, AutoSize = true, Location = new Point(14, 14) });
            TypeStrip.Controls.Add(f.Combo);
            return Track(f);
        }

        /// <summary>Editable material dropdown fed by the library, with a "..." button to edit the library.</summary>
        protected ComboField MaterialField(out Button libButton)
        {
            var f = Track(new ComboField(AxKeys.Material, true));
            f.SetItems(Library.MaterialNames());
            materialFields.Add(f);
            libButton = LibraryButton(LibraryForm.TabMaterials);
            return f;
        }

        /// <summary>Editable section dropdown fed by the library, with a "..." button to edit the library.</summary>
        protected ComboField SectionField(out Button libButton)
        {
            var f = Track(new ComboField(AxKeys.Section, true));
            f.SetItems(Library.SectionNames());
            sectionFields.Add(f);
            libButton = LibraryButton(LibraryForm.TabSections);
            return f;
        }

        private Button LibraryButton(int tab)
        {
            var b = new Button { Text = "Library...", AutoSize = true, Height = 25 };
            b.Click += (s, e) => OpenLibrary(tab);
            return b;
        }

        /// <summary>A load grid wired to the library (load case list) and to viewport picking.</summary>
        protected LoadGrid Grid(Loads.LoadKind kind, string familyKey, string title, string help)
        {
            var g = new LoadGrid(kind, familyKey, title, help);
            g.LoadRows(State.GetFamilyState(familyKey), State.GetFamily(familyKey), CaseNames());
            g.ManageCasesRequested += _ => OpenLibrary(LibraryForm.TabLoadCases);
            g.PickRequested += (grid, row) => RequestPointPick(grid, row);
            Grids.Add(g);
            return g;
        }

        private List<string> CaseNames()
        {
            var names = Library.LoadCaseNames();
            names.AddRange(State.UsedLoadCases);
            return names;
        }

        /// <summary>Closes the dialog so a point can be picked; the command re-opens it afterwards.</summary>
        private void RequestPointPick(LoadGrid grid, int row)
        {
            PendingPick = doc =>
            {
                var gp = new GetPoint();
                gp.SetCommandPrompt("Pick the location of the point load");
                if (gp.Get() == GetResult.Point)
                {
                    Rhino.Geometry.Point3d p = gp.Point();
                    grid.SetPickedPoint(row, p.X, p.Y, p.Z);
                }
            };
            DialogResult = DialogResult.Retry;
        }

        /// <summary>Closes the dialog to pick surfaces; their ids are handed to <paramref name="onPicked"/>.</summary>
        protected void RequestSurfacePick(string prompt, Action<List<Guid>> onPicked)
        {
            RequestPick(prompt, Rhino.DocObjects.ObjectType.Surface | Rhino.DocObjects.ObjectType.Brep | Rhino.DocObjects.ObjectType.Curve, onPicked);
        }

        /// <summary>Closes the dialog to pick a direction from two points (the dialog comes back afterwards).</summary>
        protected void RequestDirectionPick(string prompt, Action<Rhino.Geometry.Vector3d> onPicked)
        {
            PendingPick = doc =>
            {
                var g1 = new GetPoint();
                g1.SetCommandPrompt(prompt + " - start point");
                if (g1.Get() != GetResult.Point) return;
                Rhino.Geometry.Point3d a = g1.Point();
                var g2 = new GetPoint();
                g2.SetCommandPrompt(prompt + " - end point");
                g2.SetBasePoint(a, true);
                g2.DrawLineFromPoint(a, true);
                if (g2.Get() != GetResult.Point) return;
                onPicked(g2.Point() - a);
            };
            DialogResult = DialogResult.Retry;
        }

        /// <summary>Closes the dialog to pick objects of the given types; their ids go to <paramref name="onPicked"/>.</summary>
        protected void RequestPick(string prompt, Rhino.DocObjects.ObjectType filter, Action<List<Guid>> onPicked)
        {
            PendingPick = doc =>
            {
                var go = new GetObject();
                go.SetCommandPrompt(prompt);
                go.GeometryFilter = filter;
                go.EnablePreSelect(false, true);
                go.DeselectAllBeforePostSelect = false;
                if (go.GetMultiple(1, 0) != GetResult.Object) return;
                var ids = new List<Guid>();
                for (int i = 0; i < go.ObjectCount; i++) ids.Add(go.Object(i).ObjectId);
                onPicked(ids);
            };
            DialogResult = DialogResult.Retry;
        }

        // ==================================================================
        // Library
        // ==================================================================

        protected void OpenLibrary(int tab)
        {
            using (var f = new LibraryForm(Library, tab))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Library = f.Result;
                LibraryChanged = true;
                RefreshLibraryLists();
            }
        }

        private void RefreshLibraryLists()
        {
            foreach (ComboField f in materialFields) f.SetItems(Library.MaterialNames());
            foreach (ComboField f in sectionFields) f.SetItems(Library.SectionNames());
            foreach (LoadGrid g in Grids) g.SetCases(CaseNames());
        }

        // ==================================================================
        // Apply
        // ==================================================================

        /// <summary>Lets a dialog veto Apply (e.g. invalid numbers). Return an error text or null.</summary>
        protected virtual string ValidateInput() { return null; }

        private void Apply()
        {
            string err = ValidateInput();
            if (err != null)
            {
                MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            var edit = new AttrEdit();
            foreach (Field f in Fields) f.Collect(edit);
            foreach (LoadGrid g in Grids)
            {
                g.Collect(edit);
                if (g.Touched)
                    foreach (string lc in g.UsedCases())
                        if (Library.EnsureLoadCase(lc)) LibraryChanged = true;
            }

            // Names typed straight into the dropdowns join the library, so they are offered next time.
            string v;
            if (edit.Scalars.TryGetValue(AxKeys.Material, out v) && Library.EnsureMaterial(v)) LibraryChanged = true;
            if (edit.Scalars.TryGetValue(AxKeys.Section, out v))
                foreach (string name in v.Split(';'))
                    if (Library.EnsureSection(name)) LibraryChanged = true;

            Result = edit;
        }

        /// <summary>Default for a length in document units given in metres (e.g. 0.2 m -> 200 in a mm model).</summary>
        protected string DocLength(double metres)
        {
            // No document (offline preview): assume a millimetre model without calling into Rhino.
            double scale = Doc != null ? RhinoMath.UnitScale(UnitSystem.Meters, Doc.ModelUnitSystem) : 1000.0;
            return (metres * scale).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        protected string DocUnitName
        {
            get { return Doc != null ? Doc.GetUnitSystemName(true, false, false, true) : "mm"; }
        }

        /// <summary>A plain note filling a tab page.</summary>
        protected static Control Note(string text)
        {
            var l = Theme.Hint(text);
            l.Font = Theme.Body;
            l.Margin = new Padding(12);
            l.MaximumSize = new Size(600, 0);
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            p.Controls.Add(l);
            return p;
        }
    }
}
