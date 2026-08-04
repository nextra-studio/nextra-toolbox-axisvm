using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;
using System.Windows.Forms;
using AxisVM_plugin.UI;

namespace AxisVM_plugin
{
    public class Master : IAxisVMDotNetAddonPluginInterface_v1_0
    {
        public int Version { get { return 1; } }
        public int SubVersion { get { return 0; } }

        public int AxisVMMainFormHandle { get; set; }
        public int AxisVMApplicationHandle { get; set; }

        public string Plugin_MenuItemText { get { return "Nextra"; } }

        public string Plugin_GetTranslatedMenuItemText(int PrgLang)
        {
            return "Nextra";
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

            // In the future, this can launch a main UI that lets the user choose different functions.
            // For now, it directly executes the Studs Wall function.
            return AxisVM_plugin.Functions.StudsWallFunction.Execute((IAxisVMApplication)iAxisVMApp);
        }

        // Addon methods (toolbar)
        public string Addon_HintText { get { return "Nextra"; } }
        public string Addon_GetTranslatedHintText(int PrgLang) { return "Nextra"; }
        public int Addon_IsItemEnabled(object iAxisVMApp) { return 1; }
        public int Addon_IsItemVisible(object iAxisVMApp) { return 1; }
        public int Addon_IsModalWindow { get { return 0; } }
        public byte[] Addon_IconBitmap 
        { 
            get 
            { 
                using (var bmp = new System.Drawing.Bitmap(16, 16))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.Magenta); // Transparent color
                        using (var pen = new System.Drawing.Pen(System.Drawing.Color.Brown, 2))
                        {
                            g.DrawLine(pen, 2, 2, 2, 14);
                            g.DrawLine(pen, 8, 2, 8, 14);
                            g.DrawLine(pen, 14, 2, 14, 14);
                            g.DrawLine(new System.Drawing.Pen(System.Drawing.Color.Brown, 2), 2, 14, 14, 14);
                            g.DrawLine(new System.Drawing.Pen(System.Drawing.Color.Brown, 2), 2, 2, 14, 2);
                        }
                    }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        return ms.ToArray();
                    }
                }
            } 
        }
        public int Addon_IconBitmapTransparentColor { get { return System.Drawing.ColorTranslator.ToWin32(System.Drawing.Color.Magenta); } }
        public int Addon_ButtonPlaceFormId { get { return 0; } }
        public int Addon_ButtonPlaceToolbarId { get { return 1; } } // 1 is Geometry, 2 is Elements
        public int Addon_FormHandle { get; set; }

        public void Addon_Init(object iAxisVMApp) { }
        public void Addon_Deinit(object iAxisVMApp) { }

        public int Addon_Execute(object iAxisVMApp)
        {
            return Plugin_Execute(iAxisVMApp);
        }
    }
}
