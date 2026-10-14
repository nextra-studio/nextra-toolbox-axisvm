using System.Windows.Forms;
using AxisBridge.Library;
using AxisBridge.UI;
using Rhino;
using Rhino.Commands;

namespace AxisBridge.Commands
{
    /// <summary>Edits the document's materials, cross-sections and load cases (used by every attribute dialog).</summary>
    public class AxLibraryCommand : Command
    {
        public override string EnglishName => "AxLibrary";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            AxLibrary lib = AxLibrary.Load(doc);
            using (var f = new LibraryForm(lib, LibraryForm.TabMaterials))
            {
                if (f.ShowDialog(AttrCommandRunner.RhinoOwner()) != DialogResult.OK || f.Result == null) return Result.Cancel;
                f.Result.Save(doc);
                RhinoApp.WriteLine("AxisBridge library saved: {0} material(s), {1} section(s), {2} load case(s).",
                    f.Result.Materials.Count, f.Result.Sections.Count, f.Result.LoadCases.Count);
            }
            return Result.Success;
        }
    }
}
