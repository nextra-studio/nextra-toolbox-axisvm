using System;
using System.IO;
using AxisBridge.Export;
using AxisBridge.Link;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Live link with AxisVM through one shared file (see AxLinkSync). AxLink opens the window with
    /// "Send to AxisVM" and "Load from AxisVM"; -AxLink runs one of them from the command line on the
    /// document's link file (for scripts and toolbar buttons).
    /// </summary>
    public class AxLinkCommand : Command
    {
        public override string EnglishName => "AxLink";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            if (mode == RunMode.Interactive)
            {
                using (var form = new LinkForm(doc)) form.ShowDialog(AttrCommandRunner.RhinoOwner());
                return Result.Success;
            }

            string path = AxLinkSync.LinkPath(doc);
            var send = new OptionToggle(true, "Load", "Send");
            using (var go = new GetString())
            {
                go.SetCommandPrompt("AxLink file (Enter = " + (path.Length > 0 ? path : "none set") + ")");
                go.AddOptionToggle("Action", ref send);
                go.AcceptNothing(true);
                while (true)
                {
                    GetResult gr = go.Get();
                    if (gr == GetResult.Option) continue;
                    if (gr == GetResult.String) path = go.StringResult().Trim().Trim('"');
                    else if (gr != GetResult.Nothing) return Result.Cancel;
                    break;
                }
            }
            if (path.Length == 0) { RhinoApp.WriteLine("AxLink: no link file."); return Result.Nothing; }

            try
            {
                if (send.CurrentValue)
                {
                    ExportResult r = AxLinkSync.Send(doc, path);
                    RhinoApp.WriteLine($"AxLink: sent {r.PointCount} point(s), {r.LineCount} curve(s), {r.DomainCount} surface face(s) to {path}.");
                }
                else
                {
                    if (!File.Exists(path)) { RhinoApp.WriteLine("AxLink: " + path + " does not exist."); return Result.Failure; }
                    AxLinkSync.LoadResult r = AxLinkSync.Load(doc, path, n => false);
                    RhinoApp.WriteLine($"AxLink: loaded from AxisVM - {r.Moved} moved, {r.Updated} with new properties, {r.Created} created, {r.Unchanged} unchanged.");
                    foreach (string n in r.Notes) RhinoApp.WriteLine("  " + n);
                }
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("AxLink failed: " + ex.Message);
                return Result.Failure;
            }
        }
    }
}
