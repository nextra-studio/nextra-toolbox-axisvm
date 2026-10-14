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
    /// <summary>AxPoint dialog: nodal support, nodal loads (one row per load / load case) and parts.</summary>
    public class PointAttrForm : AttrFormBase
    {
        public static readonly string[] ScalarKeys = { AxKeys.Support, AxKeys.Parts };
        public static readonly string[] FamilyKeys = { AxKeys.NodalLoad };

        private readonly SupportField support;
        private readonly TextField parts;

        public PointAttrForm(SelectionState state, AxLibrary library, RhinoDoc doc)
            : base("AxPoint - node attributes",
                   state.Count == 1 ? "1 point selected" : state.Count + " points selected",
                   state, library, doc, new Size(780, 520))
        {
            TypeStrip.Visible = false;

            var sup = new FieldPage();
            sup.Section("Nodal support");
            support = Track(new SupportField(false));
            sup.Add("Support", support.Control, "A point becomes an AxisVM node. A point lying on a member is connected to it automatically on import.");
            sup.Section("AxisVM parts");
            parts = Track(new TextField(AxKeys.Parts, "e.g. Column bases"));
            sup.Add("Parts", parts.Control, "Semicolon-separated part names.");
            AddPage("Support", sup);

            var grid = Grid(LoadKind.Nodal, AxKeys.NodalLoad, "Nodal loads",
                "One row per load in global directions - add a row per load case (Duplicate moves to the next case).");
            AddPage("Loads", grid);

            support.Load(State.Get(AxKeys.Support), "");
            parts.Load(State.Get(AxKeys.Parts), "");
        }
    }
}
