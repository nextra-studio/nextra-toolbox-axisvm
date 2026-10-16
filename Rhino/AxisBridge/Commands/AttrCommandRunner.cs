using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.Library;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// The shared flow of AxPoint / AxLine / AxDomain:
    ///   select (pre-selection works) -> summarise the selection -> dialog (re-shown after viewport
    ///   picks) -> write only what changed -> save the library if it grew.
    /// With <c>acceptEdges</c> (AxLine) surface and mesh edges may be picked too: each becomes a new
    /// edge hinge curve before the dialog opens, and is removed again when the dialog is cancelled.
    /// </summary>
    internal static class AttrCommandRunner
    {
        public static Result Run(RhinoDoc doc, string prompt, ObjectType filter, GetObjectGeometryFilter customFilter,
                                 AxGeomKind kind, string[] scalarKeys, string[] familyKeys,
                                 Func<SelectionState, AxLibrary, RhinoDoc, AttrFormBase> makeForm, string noun,
                                 bool acceptEdges = false)
        {
            var go = new GetObject();
            go.SetCommandPrompt(prompt);
            go.GeometryFilter = filter;
            if (customFilter != null) go.SetCustomGeometryFilter(customFilter);
            if (acceptEdges) go.GeometryFilter |= ObjectType.EdgeFilter | ObjectType.MeshEdge;
            go.SubObjectSelect = acceptEdges;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return go.CommandResult();

            var objs = new List<RhinoObject>();
            var created = new List<RhinoObject>();
            for (int i = 0; i < go.ObjectCount; i++)
            {
                ObjRef r = go.Object(i);
                Rhino.Geometry.Curve edge = acceptEdges ? EdgeHinges.EdgeCurve(r) : null;
                RhinoObject o = edge != null ? EdgeHinges.AddHingeCurve(doc, edge) : r.Object();
                if (o == null) continue;
                objs.Add(o);
                if (edge != null) created.Add(o);
            }
            if (objs.Count == 0) return Result.Nothing;
            if (created.Count > 0)
                RhinoApp.WriteLine(string.Format("AxisBridge: {0} edge(s) copied to new edge hinge curve(s) on layer {1}.", created.Count, EdgeHinges.LayerPath));

            SelectionState state = SelectionState.From(objs, scalarKeys, familyKeys);
            AxLibrary library = AxLibrary.Load(doc);

            using (AttrFormBase form = makeForm(state, library, doc))
            {
                form.Objects = objs;
                DialogResult r = ShowWithPicks(form, doc);
                if (r != DialogResult.OK || form.Result == null)
                {
                    foreach (RhinoObject o in created) doc.Objects.Delete(o.Id, true);
                    if (created.Count > 0) doc.Views.Redraw();
                    return Result.Cancel;
                }

                if (form.LibraryChanged) form.Library.Save(doc);

                if (form.Result.IsEmpty)
                {
                    RhinoApp.WriteLine("Nothing was changed.");
                    return created.Count > 0 ? Result.Success : Result.Nothing;
                }

                // Objects may have been replaced while picking; re-fetch them by id.
                var live = new List<RhinoObject>();
                foreach (RhinoObject o in objs)
                {
                    RhinoObject cur = doc.Objects.FindId(o.Id);
                    if (cur != null) live.Add(cur);
                }

                int n = AttrWriter.ApplyEdit(doc, live, form.Result, kind);
                RhinoApp.WriteLine(string.Format("AxisBridge: updated {0} {1} ({2} attribute(s), {3} load list(s) changed).",
                    n, noun, form.Result.Scalars.Count, form.Result.Families.Count));
            }

            return Result.Success;
        }

        /// <summary>Shows the dialog; a Retry result means "let me pick in the viewport, then come back".</summary>
        private static DialogResult ShowWithPicks(AttrFormBase form, RhinoDoc doc)
        {
            while (true)
            {
                DialogResult r = form.ShowDialog(RhinoOwner());
                if (r == DialogResult.Retry && form.PendingPick != null)
                {
                    Action<RhinoDoc> pick = form.PendingPick;
                    form.PendingPick = null;
                    try { pick(doc); }
                    catch (Exception ex) { RhinoApp.WriteLine("AxisBridge: picking failed - " + ex.Message); }
                    continue;
                }
                return r;
            }
        }

        public static IWin32Window RhinoOwner()
        {
            IntPtr h = RhinoApp.MainWindowHandle();
            return h != IntPtr.Zero ? new Owner(h) : null;
        }

        private class Owner : IWin32Window
        {
            public Owner(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; private set; }
        }
    }
}
