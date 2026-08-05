using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AxisVM;
using AxisVMDotNetAddonPluginInterface_v1_0;

namespace AxisVM_plugin.Addons
{
    public class SectionModifierAddon : IAxisVMDotNetAddonPluginInterface_v1_0
    {
        public int Version { get { return 1; } }
        public int SubVersion { get { return 0; } }

        public int AxisVMMainFormHandle { get; set; }
        public int AxisVMApplicationHandle { get; set; }

        public string Plugin_MenuItemText { get { return "N. Section Modifier   (WIP)"; } }

        public string Plugin_GetTranslatedMenuItemText(int PrgLang)
        {
            return "N. Section Modifier  (WIP)";
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

            return AxisVM_plugin.Functions.SectionModifierFunction.Execute((IAxisVMApplication)iAxisVMApp);
        }

        // Addon methods (toolbar)
        public string Addon_HintText { get { return "Modify Section Width/Depth Independently"; } }
        public string Addon_GetTranslatedHintText(int PrgLang) { return "N. Section Modifier  (WIP)"; }
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
                        g.Clear(Color.Magenta);
                        
                        using (var pen = new Pen(Color.Blue, 2))
                        {
                            g.DrawRectangle(pen, 3, 3, 10, 10);
                            g.DrawLine(Pens.Red, 1, 8, 15, 8); // Indicate width/depth change
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
        public int Addon_ButtonPlaceToolbarId { get { return 1; } } 
        public int Addon_FormHandle { get; set; }

        public void Addon_Init(object iAxisVMApp) { }
        public void Addon_Deinit(object iAxisVMApp) { }

        public int Addon_Execute(object iAxisVMApp)
        {
            return Plugin_Execute(iAxisVMApp);
        }
    }
}


