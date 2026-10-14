using System.Collections.Generic;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Gives every Ax-tagged object a fresh Ax_Guid equal to its own Rhino id, so copy/pasted members
    /// (which inherit the source's Ax_Guid) stop sharing an identifier. Runs on the whole document.
    /// </summary>
    public class AxUpdateGuidCommand : Command
    {
        public override string EnglishName => "AxUpdateGuid";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            List<RhinoObject> objs = new List<RhinoObject>();
            foreach (RhinoObject o in doc.Objects.FindByFilter(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true
            }))
            {
                if (o == null) continue;
                if (AxLayers.IsOnTagTree(doc, o)) continue;   // visualization tags are throwaway
                objs.Add(o);
            }

            AttrWriter.RestampGuids(doc, objs, out int stamped, out int stale);

            RhinoApp.WriteLine($"AxUpdateGuid: re-stamped Ax_Guid on {stamped} object(s) ({stale} had a stale/duplicated id).");
            return Result.Success;
        }
    }
}
