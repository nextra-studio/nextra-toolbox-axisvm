using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AxisBridge.Attributes;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    public class AxLineCommand : Command
    {
        public override string EnglishName => "AxLine";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            GetObject go = new GetObject();
            go.SetCommandPrompt("Select lines or curves to assign structural member attributes");
            go.GeometryFilter = ObjectType.Curve;
            go.SubObjectSelect = false;
            go.GetMultiple(1, 0);

            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            List<RhinoObject> objs = new List<RhinoObject>();
            for (int i = 0; i < go.ObjectCount; i++)
            {
                var o = go.Object(i).Object();
                if (o != null) objs.Add(o);
            }

            if (objs.Count == 0) return Result.Nothing;

            Dictionary<string, string> initialAttrs = AttrReader.GetAxAttributes(objs[0]);

            using (LineAttrForm form = new LineAttrForm(initialAttrs))
            {
                IntPtr rhinoHwnd = RhinoApp.MainWindowHandle();
                IWin32Window parent = (rhinoHwnd != IntPtr.Zero) ? Control.FromHandle(rhinoHwnd) : null;

                if (form.ShowDialog(parent) != DialogResult.OK)
                    return Result.Cancel;

                AttrWriter.SetAttributes(doc, objs, form.ResultAttributes);
                RhinoApp.WriteLine($"Updated structural attributes and stamped Ax_Guid on {objs.Count} curve(s).");
            }

            return Result.Success;
        }
    }
}
