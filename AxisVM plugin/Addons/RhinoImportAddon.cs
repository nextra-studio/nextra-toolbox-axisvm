using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;

namespace AxisVM_plugin.Addons
{
    /// <summary>
    /// Toolbar and menu entry for the Rhino import. Same shell as the other six addons - all the work
    /// is in RhinoImportLogic.
    /// </summary>
    public class RhinoImportAddon : IAxisVMDotNetAddonPluginInterface_v1_0
    {
        public int Version { get { return 1; } }
        public int SubVersion { get { return 0; } }

        public int AxisVMMainFormHandle { get; set; }
        public int AxisVMApplicationHandle { get; set; }

        public string Plugin_MenuItemText { get { return "N. Rhino Import"; } }

        public string Plugin_GetTranslatedMenuItemText(int PrgLang)
        {
            return "N. Rhino Import";
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

            return AxisVM_plugin.Functions.RhinoImportLogic.Execute((IAxisVMApplication)iAxisVMApp);
        }

        // Addon methods (toolbar)
        public string Addon_HintText { get { return "Build the model from a Rhino AxisBridge export"; } }
        public string Addon_GetTranslatedHintText(int PrgLang) { return "N. Rhino Import"; }
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

                        // A wireframe box on the left with an arrow pointing into a frame on the right -
                        // geometry coming across from another program.
                        using (var pen = new Pen(Color.DimGray, 1))
                        {
                            g.DrawRectangle(pen, 1, 3, 4, 9);
                        }
                        using (var arrow = new Pen(Color.SeaGreen, 2))
                        {
                            g.DrawLine(arrow, 6, 8, 10, 8);
                            g.DrawLine(arrow, 8, 6, 10, 8);
                            g.DrawLine(arrow, 8, 10, 10, 8);
                        }
                        using (var target = new Pen(Color.Navy, 1))
                        {
                            g.DrawRectangle(target, 11, 3, 3, 9);
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
        public int Addon_ButtonPlaceToolbarId { get { return 1; } } // 1 = Elements toolbar, 2 = Loads
        public int Addon_FormHandle { get; set; }

        public void Addon_Init(object iAxisVMApp) { }
        public void Addon_Deinit(object iAxisVMApp) { }

        public int Addon_Execute(object iAxisVMApp)
        {
            return Plugin_Execute(iAxisVMApp);
        }
    }
}
