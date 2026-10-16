using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;

namespace AxisVM_plugin.Addons
{
    /// <summary>
    /// AxisVM entry point of the Connection Load Extractor: menu text, toolbar icon, and the hand-off to
    /// Functions.ConnectionLoadExtractorFunction.
    /// </summary>
    public class ConnectionLoadExtractorAddon : IAxisVMDotNetAddonPluginInterface_v1_0
    {
        public int Version { get { return 1; } }
        public int SubVersion { get { return 0; } }

        public int AxisVMMainFormHandle { get; set; }
        public int AxisVMApplicationHandle { get; set; }

        public string Plugin_MenuItemText { get { return "N. Connection Load Extractor"; } }

        public string Plugin_GetTranslatedMenuItemText(int PrgLang)
        {
            return "N. Connection Load Extractor";
        }

        public int Plugin_IsMenuItemEnabled(object iAxisVMApp) { return 1; }
        public int Plugin_IsMenuItemVisible(object iAxisVMApp) { return 1; }
        public int Plugin_IsModalWindow { get { return 0; } }

        public void Plugin_Init(object iAxisVMApp) { }
        public void Plugin_Deinit(object iAxisVMApp) { }

        public int Plugin_Execute(object iAxisVMApp)
        {
            if (iAxisVMApp == null)
            {
                MessageBox.Show("AxisVM application object is null.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }

            // The main form handle makes the (modeless) result window stay on top of AxisVM
            return AxisVM_plugin.Functions.ConnectionLoadExtractorFunction.Execute((IAxisVMApplication)iAxisVMApp, AxisVMMainFormHandle);
        }

        // Addon methods (toolbar)
        public string Addon_HintText { get { return "Extract connection loads at selected beam/rib ends and find the critical combination"; } }
        public string Addon_GetTranslatedHintText(int PrgLang) { return "N. Connection Load Extractor"; }
        public int Addon_IsItemEnabled(object iAxisVMApp) { return 1; }
        public int Addon_IsItemVisible(object iAxisVMApp) { return 1; }
        public int Addon_IsModalWindow { get { return 0; } }

        public byte[] Addon_IconBitmap { get { return AxisVM_plugin.Core.AddonIcons.ConnectionLoadExtractor(); } }


        public int Addon_IconBitmapTransparentColor { get { return ColorTranslator.ToWin32(Color.Magenta); } }
        public int Addon_ButtonPlaceFormId { get { return 0; } }
        // AxisVM main form tabs: 0 geometry, 1 elements, 2 loads, 3 mesh, 4 static, 5 buckling, ...
        public int Addon_ButtonPlaceToolbarId { get { return 4; } }
        public int Addon_FormHandle { get; set; }

        public void Addon_Init(object iAxisVMApp) { }
        public void Addon_Deinit(object iAxisVMApp) { }

        public int Addon_Execute(object iAxisVMApp)
        {
            return Plugin_Execute(iAxisVMApp);
        }
    }
}
