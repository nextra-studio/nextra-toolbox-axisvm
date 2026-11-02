using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using AxisBridge.Loads;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Visualize
{
    /// <summary>What the AxVisualize overlay shows. One instance is shared by the window and the conduit.</summary>
    public class VisualSettings
    {
        public bool Info = true;
        public bool Supports = true;
        public bool Releases = true;
        public bool Loads = true;
        public bool LoadValues = true;
        public bool Span = true;
        public bool LocalAxes;
        public bool Other;

        /// <summary>The one load case drawn, or null for none.</summary>
        public string LoadCase;

        public double SymbolScale = 1.0;
        public double ArrowScale = 1.0;
        public int LabelSize = 12;

        /// <summary>Element types (Beam, Plate, Node, ...) the user unticked; nothing is drawn for them.</summary>
        public readonly HashSet<string> HiddenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The AxVisualize overlay: support symbols, release markers, load arrows of one load case, span
    /// arrows, local axes and one info label per element. Draw only - nothing is added to the document,
    /// so nothing can be selected or exported. The scene is read once and re-read when objects change.
    /// </summary>
    public class AxVisualConduit : DisplayConduit
    {
        public readonly VisualSettings Settings;

        /// <summary>Raised (on the UI thread, during a redraw) after the model was re-read.</summary>
        public event EventHandler Rebuilt;

        private readonly List<Item> items = new List<Item>();
        private readonly List<string> caseNames = new List<string>();
        private readonly List<string> typeNames = new List<string>();
        private BoundingBox sceneBox = BoundingBox.Empty;
        private double baseSize = 1.0;
        private uint docSerial;
        private bool dirty = true;
        private bool hooked;

        private static readonly Color InfoColor = Color.FromArgb(25, 35, 50);
        private static readonly Color OtherColor = Color.FromArgb(90, 90, 110);
        private static readonly Color SupportColor = Color.FromArgb(30, 90, 170);
        private static readonly Color CustomSupportColor = Color.FromArgb(230, 150, 20);
        private static readonly Color ReleasedColor = Color.FromArgb(215, 40, 40);
        private static readonly Color SemiColor = Color.FromArgb(235, 130, 0);
        private static readonly Color ForceColor = Color.FromArgb(205, 30, 60);
        private static readonly Color LineLoadColor = Color.FromArgb(0, 140, 70);
        private static readonly Color AreaLoadColor = Color.FromArgb(0, 140, 160);
        private static readonly Color MomentColor = Color.FromArgb(140, 60, 200);
        private static readonly Color SpanColor = Color.FromArgb(0, 110, 220);

        private static Mesh unitSphere, unitBox, unitDisc;

        /// <summary>Viewport of the frame being drawn.</summary>
        private RhinoViewport vp;

        // Sizes in drawing units (1 unit = 1/1000 of the model diagonal, times the window sliders). They are
        // fixed in the model like real geometry: small when zoomed out, larger when zoomed in. Text too.
        private const double SymbolPx = 9.0;
        private const double ArrowPx = 60.0;
        private const double HeadPx = 11.0;
        private const double ReleaseInsetPx = 18.0;
        private const double TriadPx = 35.0;
        private const double StationPx = 26.0;

        public AxVisualConduit(VisualSettings settings)
        {
            Settings = settings ?? new VisualSettings();
        }

        /// <summary>Load case names used by the objects (after the last read), in first-seen order.</summary>
        public IList<string> CaseNames => caseNames.AsReadOnly();

        /// <summary>Element types present in the model (after the last read), in first-seen order.</summary>
        public IList<string> TypeNames => typeNames.AsReadOnly();

        public void Start()
        {
            if (!hooked)
            {
                RhinoDoc.AddRhinoObject += OnObject;
                RhinoDoc.DeleteRhinoObject += OnObject;
                RhinoDoc.UndeleteRhinoObject += OnObject;
                RhinoDoc.ReplaceRhinoObject += OnReplace;
                RhinoDoc.ModifyObjectAttributes += OnAttributes;
                RhinoDoc.EndOpenDocument += OnDocument;
                RhinoDoc.NewDocument += OnDocument;
                RhinoDoc.CloseDocument += OnDocument;
                hooked = true;
            }
            dirty = true;
            Enabled = true;
            Redraw();
        }

        public void Stop()
        {
            if (hooked)
            {
                RhinoDoc.AddRhinoObject -= OnObject;
                RhinoDoc.DeleteRhinoObject -= OnObject;
                RhinoDoc.UndeleteRhinoObject -= OnObject;
                RhinoDoc.ReplaceRhinoObject -= OnReplace;
                RhinoDoc.ModifyObjectAttributes -= OnAttributes;
                RhinoDoc.EndOpenDocument -= OnDocument;
                RhinoDoc.NewDocument -= OnDocument;
                RhinoDoc.CloseDocument -= OnDocument;
                hooked = false;
            }
            Enabled = false;
            items.Clear();
            Redraw();
        }

        /// <summary>Re-reads the model on the next redraw.</summary>
        public void Invalidate()
        {
            dirty = true;
            Redraw();
        }

        public static void Redraw()
        {
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void OnObject(object sender, RhinoObjectEventArgs e) { dirty = true; }
        private void OnReplace(object sender, RhinoReplaceObjectEventArgs e) { dirty = true; }
        private void OnAttributes(object sender, RhinoModifyObjectAttributesEventArgs e) { dirty = true; }
        private void OnDocument(object sender, DocumentOpenEventArgs e) { dirty = true; }
        private void OnDocument(object sender, DocumentEventArgs e) { dirty = true; }

        // ==================================================================
        // Reading the model
        // ==================================================================

        private class LoadItem
        {
            public LoadKind Kind;
            public LoadRow Row;
        }

        private class Item
        {
            public RhinoObject Obj;
            public string TypeName;
            public Point3d Anchor;
            public Curve Crv;
            public double Length;
            public bool Surface;
            public List<string> Info = new List<string>();
            public List<string> Other = new List<string>();
            public string Support;
            public string Release;
            public List<LoadItem> Loads = new List<LoadItem>();
            public List<Line> Spans = new List<Line>();
            public List<List<Point3d>> Outlines;
            public List<Point3d> AreaPts;
            public List<Vector3d> AreaNormals;
        }

        private void EnsureBuilt(RhinoDoc doc)
        {
            if (doc == null) return;
            if (!dirty && doc.RuntimeSerialNumber == docSerial) return;
            dirty = false;
            docSerial = doc.RuntimeSerialNumber;
            Build(doc);
            Rebuilt?.Invoke(this, EventArgs.Empty);
        }

        private void Build(RhinoDoc doc)
        {
            items.Clear();
            caseNames.Clear();
            typeNames.Clear();
            sceneBox = BoundingBox.Empty;
            double tol = doc.ModelAbsoluteTolerance;

            foreach (RhinoObject obj in doc.Objects.FindByFilter(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true }))
            {
                if (obj?.Geometry == null || obj.Geometry is TextDot) continue;
                if (AxLayers.IsOnTagTree(doc, obj)) continue;

                Dictionary<string, string> attrs = AttrReader.GetAxAttributes(obj);
                if (!AxClassify.IsTagged(attrs)) continue;

                sceneBox.Union(obj.Geometry.GetBoundingBox(false));

                // Closed curves with an Ax_FEType are domains, not members (same rule as the exporter).
                AxGeomKind geomKind = AxClassify.KindOf(obj, attrs);
                var it = new Item { Obj = obj, Anchor = AxClassify.AnchorOf(obj) };
                if (geomKind == AxGeomKind.Line) it.Crv = obj.Geometry as Curve;
                if (it.Crv != null) it.Length = it.Crv.GetLength();
                it.Surface = geomKind == AxGeomKind.Domain;
                it.TypeName = AxClassify.ElementTypeName(obj, attrs);
                if (!typeNames.Contains(it.TypeName)) typeNames.Add(it.TypeName);

                string type = Get(attrs, AxKeys.Type), fe = Get(attrs, AxKeys.FeType);
                if (it.Crv != null)
                {
                    AddLine(it.Info, type);
                    AddLine(it.Info, Get(attrs, AxKeys.Section));
                    AddLine(it.Info, Get(attrs, AxKeys.Material));
                }
                else if (it.Surface)
                {
                    AddLine(it.Info, fe);
                    string t = Get(attrs, AxKeys.Thickness);
                    if (t != null) AddLine(it.Info, "t=" + t);
                    AddLine(it.Info, Get(attrs, AxKeys.Material));
                }

                AddLine(it.Other, Prefixed("k=", Get(attrs, AxKeys.Stiffness)));
                AddLine(it.Other, Prefixed("axis=", Get(attrs, AxKeys.LocalAxis)));
                AddLine(it.Other, Prefixed("ecc=", Get(attrs, AxKeys.Eccentricity)));
                AddLine(it.Other, Prefixed("dof=", Get(attrs, AxKeys.CoupledDof)));
                AddLine(it.Other, Prefixed("domains=", Get(attrs, AxKeys.HingeDomains)));
                AddLine(it.Other, Prefixed("spacing=", Get(attrs, AxKeys.EdgeSpacing)));
                AddLine(it.Other, Prefixed("parts=", Get(attrs, AxKeys.Parts)));

                it.Support = Get(attrs, AxKeys.Support);
                if (it.Crv != null) it.Release = Get(attrs, AxKeys.Release);

                ReadLoads(obj, attrs, it);

                if (AxSchema.NormalizeSurfaceType(fe) == AxSchema.OneWayLoadPanel)
                {
                    string span = Get(attrs, AxKeys.SpanDirection);
                    foreach (List<Point3d> outline in SpanTools.Outlines(obj, tol))
                    {
                        Vector3d dir;
                        if (!SpanTools.Resolve(span, outline, out dir)) continue;
                        BoundingBox bb = new BoundingBox(outline);
                        double half = 0.4 * Math.Max(bb.Diagonal.X, bb.Diagonal.Y);
                        it.Spans.Add(new Line(bb.Center - dir * half, bb.Center + dir * half));
                    }
                }

                if (it.Surface && it.Loads.Exists(l => l.Kind == LoadKind.SurfaceArea))
                    it.Outlines = SpanTools.Outlines(obj, tol);

                items.Add(it);
            }

            double diag = sceneBox.IsValid ? sceneBox.Diagonal.Length : 0.0;
            baseSize = diag * 0.01;
            if (baseSize <= RhinoMath.ZeroTolerance) baseSize = Math.Max(tol * 100.0, 0.1);

            // Arrow stations on loaded surfaces need the scene size, so they come last.
            foreach (Item it in items)
                if (it.Outlines != null) SampleSurface(it, tol);
        }

        private void ReadLoads(RhinoObject obj, Dictionary<string, string> attrs, Item it)
        {
            bool surface = !(obj.Geometry is Curve) || attrs.ContainsKey(AxKeys.FeType);
            foreach (string key in new[] { AxKeys.NodalLoad, AxKeys.PointLoad, AxKeys.DistLoad })
            {
                LoadKind kind;
                if (key == AxKeys.NodalLoad) kind = LoadKind.Nodal;
                else if (key == AxKeys.PointLoad) kind = surface ? LoadKind.SurfacePoint : LoadKind.MemberPoint;
                else kind = surface ? LoadKind.SurfaceArea : LoadKind.MemberDist;

                foreach (string v in AttrReader.GetFamily(attrs, key))
                {
                    LoadRow row;
                    if (!LoadRow.TryParse(kind, v, out row)) continue;
                    it.Loads.Add(new LoadItem { Kind = kind, Row = row });
                    if (!caseNames.Exists(c => string.Equals(c, row.Case, StringComparison.OrdinalIgnoreCase)))
                        caseNames.Add(row.Case);
                }
            }
        }

        /// <summary>Arrow stations spread over each face of a loaded surface, inside its trims.</summary>
        private void SampleSurface(Item it, double tol)
        {
            it.AreaPts = new List<Point3d>();
            it.AreaNormals = new List<Vector3d>();
            Brep brep = ToBrep(it.Obj.Geometry, tol);
            if (brep == null)
            {
                it.AreaPts.Add(it.Anchor);
                it.AreaNormals.Add(Vector3d.ZAxis);
                return;
            }

            foreach (BrepFace face in brep.Faces)
            {
                Vector3d ext = face.GetBoundingBox(false).Diagonal;
                double size = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
                // About two line-load arrow spacings between surface arrows, in each direction.
                int n = Math.Max(2, Math.Min(20, (int)Math.Round(size / (DrawUnit * StationPx * 2.0))));
                Interval du = face.Domain(0), dv = face.Domain(1);
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double u = du.ParameterAt((i + 0.5) / n), v = dv.ParameterAt((j + 0.5) / n);
                        if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
                        Vector3d nrm = face.NormalAt(u, v);
                        if (face.OrientationIsReversed) nrm.Reverse();
                        it.AreaPts.Add(face.PointAt(u, v));
                        it.AreaNormals.Add(nrm);
                    }
            }
        }

        private static Brep ToBrep(GeometryBase g, double tol)
        {
            switch (g)
            {
                case Brep b: return b;
                case Extrusion e: return e.ToBrep();
                case Surface s: return s.ToBrep();
                case Curve c:
                {
                    Brep[] planar = c.IsClosed ? Brep.CreatePlanarBreps(c, tol) : null;
                    return planar != null && planar.Length > 0 ? planar[0] : null;
                }
                default: return null;
            }
        }

        // ==================================================================
        // Drawing
        // ==================================================================

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            EnsureBuilt(e.RhinoDoc);
            if (!sceneBox.IsValid) return;
            vp = e.Viewport;
            BoundingBox bb = sceneBox;
            // Symbols are pixel sized, so how far they reach in world units depends on the zoom.
            double grow = Px(sceneBox.Center, ArrowPx * Settings.ArrowScale + SymbolPx * 3.0 * Settings.SymbolScale + 20);
            bb.Inflate(grow);
            e.IncludeBoundingBox(bb);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            EnsureBuilt(e.RhinoDoc);
            vp = e.Viewport;
            Scales scales = Settings.Loads ? MaxValues() : null;

            foreach (Item it in items)
            {
                if (!IsShown(e.RhinoDoc, it)) continue;
                if (Settings.Supports && it.Support != null) DrawSupport(e.Display, it);
                if (Settings.Releases && it.Release != null) DrawReleaseMarkers(e.Display, it);
                if (Settings.Span) foreach (Line l in it.Spans) DrawDoubleArrow(e.Display, l, SpanColor);
                if (Settings.LocalAxes && it.Crv != null) DrawTriad(e.Display, it);
                if (scales != null) foreach (LoadItem l in it.Loads) if (InCase(l)) DrawLoad(e.Display, it, l, scales, null);
            }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            EnsureBuilt(e.RhinoDoc);
            vp = e.Viewport;
            Scales scales = Settings.Loads && Settings.LoadValues ? MaxValues() : null;
            var labels = new List<ValueLabel>();

            foreach (Item it in items)
            {
                if (!IsShown(e.RhinoDoc, it)) continue;

                var lines = new List<string>();
                if (Settings.Info) lines.AddRange(it.Info);
                if (Settings.Other) lines.AddRange(it.Other);
                if (lines.Count > 0)
                {
                    double off = it.Crv != null ? lines.Count * Math.Max(6, Settings.LabelSize) * 0.6 + 10 : 0;
                    bool loaded = Settings.Loads && it.Loads.Exists(InCase);
                    DrawLabel(e, it.Anchor, lines, lines.Count == it.Other.Count && !Settings.Info ? OtherColor : InfoColor, loaded ? off : -off);
                }

                if (Settings.Supports && it.Support != null && SupportKind(it.Support) == "custom")
                    DrawLabel(e, it.Anchor, new List<string> { "k=" + Short(it.Support) }, CustomSupportColor, SymbolPx * Settings.SymbolScale * 2 + 12);

                // Release text only when the member is long enough on screen for both labels to fit.
                if (Settings.Releases && it.Release != null && ScreenLength(e, it) >= 140)
                {
                    foreach (bool start in new[] { true, false })
                    {
                        string text = ReleaseText(it.Release, start);
                        Color c;
                        if (!ReleaseColor(text, out c)) continue;
                        // Below the marker and pushed toward the member centre, so end labels do not collide.
                        Point2d a = e.Viewport.WorldToClient(ReleasePoint(it, start)), m = e.Viewport.WorldToClient(it.Anchor);
                        Vector2d toward = new Vector2d(m.X - a.X, m.Y - a.Y);
                        if (!toward.Unitize()) toward = new Vector2d(start ? 1 : -1, 0);
                        DrawLabel(e, ReleasePoint(it, start), new List<string> { text }, c, 14, toward.X * 30);
                    }
                }

                if (scales != null)
                    foreach (LoadItem l in it.Loads)
                        if (InCase(l)) DrawLoad(null, it, l, scales, labels);
            }

            foreach (ValueLabel v in labels)
            {
                Point2d tail = e.Viewport.WorldToClient(v.At), foot = e.Viewport.WorldToClient(v.Foot);
                Vector2d away = new Vector2d(tail.X - foot.X, tail.Y - foot.Y);
                if (!away.Unitize()) away = new Vector2d(0, -1);
                double push = Math.Max(6, Settings.LabelSize) * 0.9 + 4;
                DrawLabel(e, v.At, new List<string> { v.Text }, v.Color, away.Y * push, away.X * push);
            }
        }

        private double ScreenLength(DrawEventArgs e, Item it)
        {
            return it.Length / DrawUnit;
        }

        /// <summary>One drawing unit in model units.</summary>
        private double DrawUnit => baseSize / 10.0;

        /// <summary>Model length of the given number of drawing units (the point is unused: sizes are fixed in the model).</summary>
        private double Px(Point3d at, double units)
        {
            return units * DrawUnit;
        }

        /// <summary>Distance between two points in drawing units.</summary>
        private double ScreenDistance(Point3d a, Point3d b)
        {
            return a.DistanceTo(b) / DrawUnit;
        }

        /// <summary>Screen pixels per drawing unit at this point, for things Rhino only draws in pixels (text, dots).</summary>
        private double Zoom(RhinoViewport view, Point3d at)
        {
            double ppu;
            if (view != null && view.GetWorldToScreenScale(at, out ppu) && ppu > 1e-12) return ppu * DrawUnit;
            return 1.0;
        }

        private static Vector3d Unit(Vector3d v)
        {
            v.Unitize();
            return v;
        }

        private bool IsShown(RhinoDoc doc, Item it)
        {
            if (Settings.HiddenTypes.Contains(it.TypeName)) return false;
            RhinoObject obj = it.Obj;
            if (obj == null || obj.IsDeleted || !obj.Visible) return false;
            int li = obj.Attributes.LayerIndex;
            return li < 0 || li >= doc.Layers.Count || doc.Layers[li].IsVisible;
        }

        private bool InCase(LoadItem l)
        {
            return Settings.LoadCase != null && string.Equals(l.Row.Case, Settings.LoadCase, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Multi-line label, centred on the world point (lines stacked in screen space).</summary>
        private void DrawLabel(DrawEventArgs e, Point3d world, List<string> lines, Color color, double dyPixels = 0, double dxPixels = 0)
        {
            double k = Zoom(e.Viewport, world);
            double h = Math.Max(6, Settings.LabelSize) * k;
            if (h < 4) return;   // too small to read at this zoom
            int hp = (int)Math.Round(Math.Min(h, 400));
            Point2d s = e.Viewport.WorldToClient(world);
            double top = s.Y + dyPixels * k - (lines.Count - 1) * h * 0.6;
            for (int i = 0; i < lines.Count; i++)
                e.Display.Draw2dText(lines[i], color, new Point2d(s.X + dxPixels * k, top + i * h * 1.2), true, hp);
        }

        // ---------------------------------------------------------- supports

        private double SymbolSize(Point3d at) { return Px(at, SymbolPx * Settings.SymbolScale); }

        private static string SupportKind(string raw)
        {
            string k = AxClassify.SupportKindName(raw);
            return k == null ? "custom" : k.ToLowerInvariant();
        }

        private void DrawSupport(DisplayPipeline d, Item it)
        {
            string kind = SupportKind(it.Support);
            if (it.Crv == null)
            {
                DrawSupportSymbol(d, it.Anchor, kind);
                return;
            }

            d.DrawCurve(it.Crv, kind == "custom" ? CustomSupportColor : SupportColor, 5);
            // About one symbol per 60 px of member on screen, at least both ends.
            int n = Math.Max(1, Math.Min(20, (int)(ScreenDistance(it.Crv.PointAtStart, it.Crv.PointAtEnd) / 60.0)));
            for (int i = 0; i <= n; i++)
                DrawSupportSymbol(d, AtLength(it, it.Length * i / n), kind);
        }

        private void DrawSupportSymbol(DisplayPipeline d, Point3d p, string kind)
        {
            EnsureMeshes();
            double s = SymbolSize(p);
            Transform xf = Transform.Translation(new Vector3d(p)) * Transform.Scale(Point3d.Origin, s);
            d.PushModelTransform(xf);
            switch (kind)
            {
                case "fixed":
                    d.DrawMeshShaded(unitBox, Material(SupportColor));
                    break;
                case "roller":
                    d.DrawMeshShaded(unitSphere, Material(SupportColor));
                    d.DrawMeshShaded(unitDisc, Material(SupportColor));
                    break;
                case "custom":
                    d.DrawMeshShaded(unitSphere, Material(CustomSupportColor));
                    break;
                default:
                    d.DrawMeshShaded(unitSphere, Material(SupportColor));
                    break;
            }
            d.PopModelTransform();
        }

        private static void EnsureMeshes()
        {
            if (unitSphere != null) return;
            unitSphere = Mesh.CreateFromSphere(new Sphere(Point3d.Origin, 1.0), 16, 10);
            // Fixed: a block under the node, its top face at the node.
            unitBox = Mesh.CreateFromBox(new BoundingBox(-1.2, -1.2, -1.6, 1.2, 1.2, 0.0), 1, 1, 1);
            // Roller: a flat plate under the ball.
            unitDisc = Mesh.CreateFromCylinder(new Cylinder(new Circle(new Plane(new Point3d(0, 0, -1.45), Vector3d.ZAxis), 1.6), 0.3), 1, 24);
            unitSphere.Normals.ComputeNormals();
            unitBox.Normals.ComputeNormals();
            unitDisc.Normals.ComputeNormals();
        }

        private static readonly Dictionary<int, DisplayMaterial> materials = new Dictionary<int, DisplayMaterial>();

        private static DisplayMaterial Material(Color c)
        {
            DisplayMaterial m;
            if (!materials.TryGetValue(c.ToArgb(), out m))
            {
                m = new DisplayMaterial(c);
                materials[c.ToArgb()] = m;
            }
            return m;
        }

        // ---------------------------------------------------------- releases

        private Point3d ReleasePoint(Item it, bool start)
        {
            Point3d end = start ? it.Crv.PointAtStart : it.Crv.PointAtEnd;
            double inset = Math.Min(0.3 * it.Length, Px(end, ReleaseInsetPx * Settings.SymbolScale));
            return AtLength(it, start ? inset : it.Length - inset);
        }

        private void DrawReleaseMarkers(DisplayPipeline d, Item it)
        {
            foreach (bool start in new[] { true, false })
            {
                Color c;
                if (!ReleaseColor(ReleaseText(it.Release, start), out c)) continue;
                Point3d rp = ReleasePoint(it, start);
                int size = (int)Math.Round(Math.Max(2, Math.Min(80, 7 * Math.Sqrt(Settings.SymbolScale) * Zoom(d.Viewport, rp))));
                d.DrawPoint(rp, PointStyle.RoundSimple, size, c);
            }
        }

        /// <summary>Red = something released, orange = semi-rigid only, false = fully fixed (nothing drawn).</summary>
        private static bool ReleaseColor(string text, out Color c)
        {
            c = ReleasedColor;
            if (text == "fix" || text == "rigid" || text.Replace("|", "").Trim('x').Length == 0) return false;
            if (text == "pin" || text.IndexOf('o') >= 0) return true;
            c = SemiColor;
            return true;
        }

        /// <summary>Compact release glyph: translations then rotations, o = released, x = fixed, s = spring.</summary>
        private static string ReleaseText(string raw, bool wantStart)
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t == "fix" || t == "fixed") return "fix";
            if (t == "pin" || t == "pinned") return "pin";
            if (t == "semi-pin" || t == "semipin") return "semi-pin";

            Dictionary<string, string> f = AttrReader.ParseFields(raw);
            string list = null;
            string side;
            if (f.TryGetValue(wantStart ? "start" : "end", out side) || f.TryGetValue(wantStart ? "s" : "e", out side)) list = side;
            else if (f.Count == 0) list = raw;   // bare 6-number list applies to both ends
            if (string.IsNullOrEmpty(list)) return "rigid";

            string[] parts = list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length && i < 6; i++)
            {
                if (i == 3) sb.Append('|');
                double val;
                double.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out val);
                sb.Append(val <= 0 ? 'o' : (val >= 1e9 ? 'x' : 's'));
            }
            return sb.Length > 0 ? sb.ToString() : "rigid";
        }

        // ---------------------------------------------------------- loads

        /// <summary>Largest value of each load family in the shown case, so arrows scale within a family.</summary>
        private class Scales
        {
            public double Force, Moment, Line, Area;
        }

        private Scales MaxValues()
        {
            var s = new Scales();
            foreach (Item it in items)
                foreach (LoadItem l in it.Loads)
                {
                    if (!InCase(l)) continue;
                    double f = Vec(l.Row.V, 0).Length, m = Vec(l.Row.V, 3).Length;
                    switch (l.Kind)
                    {
                        case LoadKind.MemberDist:
                            s.Line = Math.Max(s.Line, Math.Max(f, l.Row.V2 != null ? Vec(l.Row.V2, 0).Length : 0));
                            break;
                        case LoadKind.SurfaceArea:
                            s.Area = Math.Max(s.Area, f);
                            break;
                        default:
                            s.Force = Math.Max(s.Force, f);
                            s.Moment = Math.Max(s.Moment, m);
                            break;
                    }
                }
            return s;
        }

        /// <summary>Arrow length for a value at this ratio of the largest one, measured at the given point.</summary>
        private double ArrowLength(double ratio, Point3d at)
        {
            if (ratio <= 0 || double.IsNaN(ratio)) return 0;
            return Px(at, ArrowPx * Settings.ArrowScale * Math.Max(0.15, Math.Min(1.0, ratio)));
        }

        /// <summary>
        /// Draws one load (display != null) or collects its value label (labels != null). The label sits at
        /// the tail of the arrow, so the two passes share the geometry code.
        /// </summary>
        private void DrawLoad(DisplayPipeline d, Item it, LoadItem l, Scales s, List<ValueLabel> labels)
        {
            LoadRow r = l.Row;
            string text = labels != null ? LoadText(l) : null;

            switch (l.Kind)
            {
                case LoadKind.Nodal:
                case LoadKind.MemberPoint:
                case LoadKind.SurfacePoint:
                {
                    Point3d p;
                    Vector3d f = Vec(r.V, 0), m = Vec(r.V, 3);
                    if (l.Kind == LoadKind.Nodal) p = it.Anchor;
                    else if (l.Kind == LoadKind.SurfacePoint) p = new Point3d(r.X, r.Y, r.Z);
                    else
                    {
                        double at = r.ByLength ? r.Pos : r.Pos * it.Length;
                        p = AtLength(it, at);
                        if (r.System == "Local") { f = ToGlobal(it, at, f); m = ToGlobal(it, at, m); }
                    }

                    Point3d tail = p;
                    if (f.Length > 1e-12)
                        tail = Arrow(d, p, f, ArrowLength(f.Length / s.Force, p), ForceColor);
                    if (m.Length > 1e-12)
                    {
                        Point3d mTip = MomentArrow(d, p, m, ArrowLength(m.Length / s.Moment, p));
                        if (f.Length <= 1e-12) tail = mTip;
                    }
                    labels?.Add(new ValueLabel(tail, p, text, f.Length > 1e-12 ? ForceColor : MomentColor));
                    break;
                }

                case LoadKind.MemberDist:
                {
                    if (it.Crv == null || it.Length <= 0) break;
                    double a = r.ByLength ? r.From : r.From * it.Length;
                    double b = double.IsNaN(r.To) ? it.Length : (r.ByLength ? r.To : r.To * it.Length);
                    a = Clamp(a, 0, it.Length);
                    b = Clamp(b, 0, it.Length);
                    if (b - a <= 1e-9) break;

                    double[] v2 = r.V2 ?? r.V;
                    Point3d pa = AtLength(it, a), pb = AtLength(it, b), mid = AtLength(it, (a + b) / 2);
                    // One world scale for the whole member, so the diagram stays a straight (or trapezoid) line.
                    double full = ArrowLength(1.0, mid);
                    int n = Math.Max(2, Math.Min(40, (int)(ScreenDistance(pa, pb) / StationPx) + 1));
                    var tails = new List<Point3d>();
                    var feet = new List<Point3d>();
                    for (int i = 0; i < n; i++)
                    {
                        double k = (double)i / (n - 1), at = a + (b - a) * k;
                        Vector3d q = Vec(r.V, 0) * (1 - k) + Vec(v2, 0) * k;
                        if (r.System == "Local") q = ToGlobal(it, at, q);
                        Point3d p = AtLength(it, at);
                        double ratio = s.Line > 0 ? q.Length / s.Line : 0;
                        double len = ratio <= 0 ? 0 : full * Math.Max(0.15, Math.Min(1.0, ratio));
                        feet.Add(p);
                        tails.Add(q.Length > 1e-12 ? Arrow(d, p, q, len, LineLoadColor) : p);
                    }
                    if (d != null)
                    {
                        d.DrawPolyline(tails, LineLoadColor, 2);
                        // Close the diagram at both ends of the loaded length.
                        d.DrawLine(feet[0], tails[0], LineLoadColor, 2);
                        d.DrawLine(feet[feet.Count - 1], tails[tails.Count - 1], LineLoadColor, 2);
                    }
                    // At 30 % of the loaded length, clear of a mid-span point load.
                    int li = (int)Math.Round((tails.Count - 1) * 0.3);
                    labels?.Add(new ValueLabel(tails[li], feet[li], text, LineLoadColor));
                    break;
                }

                case LoadKind.SurfaceArea:
                {
                    Vector3d q = Vec(r.V, 0);
                    if (q.Length <= 1e-12 || it.AreaPts == null) break;
                    bool local = r.System == "Local";
                    double len = ArrowLength(s.Area > 0 ? q.Length / s.Area : 0, it.Anchor);
                    Point3d labelAt = local ? it.Anchor : it.Anchor - Unit(q) * len;
                    for (int i = 0; i < it.AreaPts.Count; i++)
                    {
                        // Local: the z part follows the surface normal, the in-plane parts stay global.
                        Vector3d qi = local ? new Vector3d(q.X, q.Y, 0) + it.AreaNormals[i] * q.Z : q;
                        Arrow(d, it.AreaPts[i], qi, len, AreaLoadColor);
                    }
                    if (!local && it.Outlines != null)
                    {
                        Vector3d lift = q;
                        lift.Unitize();
                        lift *= -len;
                        foreach (List<Point3d> o in it.Outlines)
                        {
                            var moved = new List<Point3d>(o.Count + 1);
                            foreach (Point3d p in o) moved.Add(p + lift);
                            if (moved.Count > 0) moved.Add(moved[0]);
                            d?.DrawPolyline(moved, AreaLoadColor, 1);
                        }
                    }
                    labels?.Add(new ValueLabel(labelAt, it.Anchor, text, AreaLoadColor));
                    break;
                }
            }
        }

        /// <summary>Arrow with its tip at p, pointing along dir. Returns the tail.</summary>
        private Point3d Arrow(DisplayPipeline d, Point3d p, Vector3d dir, double length, Color c)
        {
            dir.Unitize();
            Point3d tail = p - dir * length;
            if (d != null)
            {
                d.DrawLine(tail, p, c, 2);
                d.DrawArrowHead(p, dir, c, 0, Px(p, HeadPx * Settings.SymbolScale));
            }
            return tail;
        }

        /// <summary>Moment vector as a double-headed arrow along its axis (right-hand rule). Returns the tip.</summary>
        private Point3d MomentArrow(DisplayPipeline d, Point3d p, Vector3d m, double length)
        {
            m.Unitize();
            Point3d tip = p + m * length;
            if (d != null)
            {
                double head = Px(tip, HeadPx * Settings.SymbolScale);
                d.DrawLine(p, tip, MomentColor, 2);
                d.DrawArrowHead(tip, m, MomentColor, 0, head);
                d.DrawArrowHead(tip - m * head * 0.9, m, MomentColor, 0, head);
            }
            return tip;
        }

        private void DrawDoubleArrow(DisplayPipeline d, Line l, Color c)
        {
            double rel = l.Length > 0 ? Px(l.From, HeadPx * 1.4 * Settings.SymbolScale) / l.Length : 0.1;
            d.DrawArrow(l, c, 0, rel);
            d.DrawArrow(new Line(l.To, l.From), c, 0, rel);
        }

        /// <summary>"Fz=-12", "qz=-5→-8", "qz=-2.5" - the case is in the window, so it is left out.</summary>
        private static string LoadText(LoadItem l)
        {
            bool dist = l.Kind == LoadKind.MemberDist || l.Kind == LoadKind.SurfaceArea;
            string[] names = dist ? new[] { "qx", "qy", "qz", "mx", "my", "mz" } : new[] { "Fx", "Fy", "Fz", "Mx", "My", "Mz" };
            int count = l.Kind == LoadKind.SurfaceArea ? 3 : 6;
            double[] v2 = l.Row.V2 != null && !LoadRow.Same(l.Row.V, l.Row.V2) ? l.Row.V2 : null;
            var parts = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(l.Row.V[i]) <= 1e-12 && (v2 == null || Math.Abs(v2[i]) <= 1e-12)) continue;
                string s = names[i] + "=" + LoadRow.N(l.Row.V[i]);
                if (v2 != null) s += "→" + LoadRow.N(v2[i]);
                parts.Add(s);
            }
            return parts.Count == 0 ? "0" : string.Join(" ", parts.ToArray());
        }

        private class ValueLabel
        {
            public readonly Point3d At;
            public readonly string Text;
            public readonly Color Color;
            /// <summary>Where the arrow touches the element; the label is pushed away from it.</summary>
            public readonly Point3d Foot;
            public ValueLabel(Point3d at, Point3d foot, string text, Color color) { At = at; Foot = foot; Text = text; Color = color; }
        }

        // ---------------------------------------------------------- member frames

        /// <summary>Simplified member frame: x along the member, y horizontal, z up (x global for vertical members).</summary>
        private static void Frame(Item it, double at, out Vector3d x, out Vector3d y, out Vector3d z)
        {
            double t;
            if (!it.Crv.LengthParameter(Clamp(at, 0, it.Length), out t)) t = it.Crv.Domain.Mid;
            x = it.Crv.TangentAt(t);
            Vector3d refv = Math.Abs(x * Vector3d.ZAxis) > 0.99 ? Vector3d.XAxis : Vector3d.ZAxis;
            y = Vector3d.CrossProduct(refv, x);
            y.Unitize();
            z = Vector3d.CrossProduct(x, y);
            z.Unitize();
        }

        private static Vector3d ToGlobal(Item it, double at, Vector3d local)
        {
            if (it.Crv == null) return local;
            Vector3d x, y, z;
            Frame(it, at, out x, out y, out z);
            return x * local.X + y * local.Y + z * local.Z;
        }

        private void DrawTriad(DisplayPipeline d, Item it)
        {
            Vector3d x, y, z;
            Frame(it, it.Length / 2, out x, out y, out z);
            Point3d o = AtLength(it, it.Length / 2);
            double len = Px(o, TriadPx * Settings.SymbolScale), rel = 0.25;
            d.DrawArrow(new Line(o, o + x * len), Color.Red, 0, rel);
            d.DrawArrow(new Line(o, o + y * len), Color.FromArgb(0, 170, 0), 0, rel);
            d.DrawArrow(new Line(o, o + z * len), Color.Blue, 0, rel);
        }

        // ---------------------------------------------------------- helpers

        private static Point3d AtLength(Item it, double s)
        {
            if (it.Crv == null) return it.Anchor;
            double t;
            if (!it.Crv.LengthParameter(Clamp(s, 0, it.Length), out t))
                t = it.Crv.Domain.ParameterAt(it.Length > 0 ? s / it.Length : 0.5);
            return it.Crv.PointAt(t);
        }

        private static Vector3d Vec(double[] v, int i)
        {
            return new Vector3d(v[i], v[i + 1], v[i + 2]);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static string Get(Dictionary<string, string> attrs, string key)
        {
            string v;
            if (!attrs.TryGetValue(key, out v) || v == null) return null;
            v = v.Trim();
            return v.Length == 0 || v == "-" ? null : v;
        }

        private static string Prefixed(string prefix, string v)
        {
            return v == null ? null : prefix + v;
        }

        private static void AddLine(List<string> lines, string v)
        {
            if (!string.IsNullOrEmpty(v)) lines.Add(Short(v));
        }

        private static string Short(string v)
        {
            return v.Length > 32 ? v.Substring(0, 31) + "…" : v;
        }
    }
}
