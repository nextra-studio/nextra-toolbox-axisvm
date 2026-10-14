using System;
using Rhino.PlugIns;

namespace AxisBridge
{
    /// <summary>
    /// PlugIn entry point for AxisBridge in Rhino 8.
    /// </summary>
    public class AxisBridgePlugin : PlugIn
    {
        public static AxisBridgePlugin Instance { get; private set; }

        public AxisBridgePlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime
        {
            get { return PlugInLoadTime.AtStartup; }
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Rhino.RhinoApp.WriteLine("AxisBridge plugin loaded successfully. Commands: AxPoint, AxLine, AxDomain, AxClear, AxExport, AxUpdateGuid, AxVisualize, AxLayerTidyUp");
            return LoadReturnCode.Success;
        }
    }
}
