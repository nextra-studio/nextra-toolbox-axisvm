using System;
using System.Collections.Generic;
using AxisBridge.Attributes;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    public class AxClearCommand : Command
    {
        public override string EnglishName => "AxClear";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            GetObject go = new GetObject();
            go.SetCommandPrompt("Select objects to clear structural Ax_* attributes from");
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

            AttrWriter.ClearAxAttributes(doc, objs);
            RhinoApp.WriteLine($"Cleared all Ax_* structural attributes from {objs.Count} object(s).");

            return Result.Success;
        }
    }
}
