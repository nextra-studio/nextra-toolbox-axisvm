using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;

namespace AxisVM_plugin.Addons
{
    /// <summary>
    /// Second entry point for the one-way load panel tools: takes the load panels the user has
    /// selected and rebuilds each of them as its one-way equivalent (one panel, or the three-panel
    /// edge-strip set), re-applying the uniform area load that was on the original.
    ///
    /// All of the real work lives in <see cref="AxisVM_plugin.Functions.ConvertLoadPanelLogic.ExecuteConvert"/>
    /// (panel geometry via the shared <see cref="AxisVM_plugin.Functions.OneWayPanelBuilder"/>);
    /// this class is only the AxisVM addon/plugin registration shell.
    /// </summary>
    public class ConvertLoadPanelToOneWayAddon : IAxisVMDotNetAddonPluginInterface_v1_0
    {
        public int Version { get { return 1; } }
        public int SubVersion { get { return 0; } }

        public int AxisVMMainFormHandle { get; set; }
        public int AxisVMApplicationHandle { get; set; }

        public string Plugin_MenuItemText { get { return "N. Convert Load Panel -> One-Way"; } }

        public string Plugin_GetTranslatedMenuItemText(int PrgLang)
        {
            return "N. Convert Load Panel -> One-Way";
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

            return AxisVM_plugin.Functions.ConvertLoadPanelLogic.ExecuteConvert((IAxisVMApplication)iAxisVMApp);
        }

        // Addon methods (toolbar)
        public string Addon_HintText { get { return "Convert selected load panels to one-way load panels"; } }
        public string Addon_GetTranslatedHintText(int PrgLang) { return "N. Convert Load Panel -> One-Way"; }
        public int Addon_IsItemEnabled(object iAxisVMApp) { return 1; }
        public int Addon_IsItemVisible(object iAxisVMApp) { return 1; }
        public int Addon_IsModalWindow { get { return 0; } }

        public byte[] Addon_IconBitmap
        {
            get
            {
                using (var bmp = new Bitmap(16, 16))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Magenta); // Transparent color
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;

                        // Panel outline + two support edges + a "1" for one-way.
                        using (var pen = new Pen(Color.DarkOrange, 1))
                        {
                            g.DrawRectangle(pen, 2, 2, 11, 11);
                        }
                        using (var pen = new Pen(Color.Blue, 1))
                        {
                            g.DrawLine(pen, 5, 3, 5, 12);
                            g.DrawLine(pen, 10, 3, 10, 12);
                        }
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        return ms.ToArray();
                    }
                }
            }
        }

        public int Addon_IconBitmapTransparentColor { get { return ColorTranslator.ToWin32(Color.Magenta); } }
        public int Addon_ButtonPlaceFormId { get { return 0; } }
        public int Addon_ButtonPlaceToolbarId { get { return 2; } } // Elements toolbar
        public int Addon_FormHandle { get; set; }

        public void Addon_Init(object iAxisVMApp) { }
        public void Addon_Deinit(object iAxisVMApp) { }

        public int Addon_Execute(object iAxisVMApp)
        {
            return Plugin_Execute(iAxisVMApp);
        }
    }
}
