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
    /// </summary>
    internal static class AttrCommandRunner
    {
        public static Result Run(RhinoDoc doc, string prompt, ObjectType filter, GetObjectGeometryFilter customFilter,
                                 AxGeomKind kind, string[] scalarKeys, string[] familyKeys,
                                 Func<SelectionState, AxLibrary, RhinoDoc, AttrFormBase> makeForm, string noun)
        {
            var go = new GetObject();
            go.SetCommandPrompt(prompt);
            go.GeometryFilter = filter;
            if (customFilter != null) go.SetCustomGeometryFilter(customFilter);
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return go.CommandResult();

            var objs = new List<RhinoObject>();
            for (int i = 0; i < go.ObjectCount; i++)
            {
                RhinoObject o = go.Object(i).Object();
                if (o != null) objs.Add(o);
            }
            if (objs.Count == 0) return Result.Nothing;

            SelectionState state = SelectionState.From(objs, scalarKeys, familyKeys);
            AxLibrary library = AxLibrary.Load(doc);

            using (AttrFormBase form = makeForm(state, library, doc))
            {
                DialogResult r = ShowWithPicks(form, doc);
                if (r != DialogResult.OK || form.Result == null) return Result.Cancel;

                if (form.LibraryChanged) form.Library.Save(doc);

                if (form.Result.IsEmpty)
                {
                    RhinoApp.WriteLine("Nothing was changed.");
                    return Result.Nothing;
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
