using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SocOtaUpgrade
{
    internal static class AppIconHelper
    {
        public static void TryApply(Form form, string baseDir)
        {
            if (form == null || string.IsNullOrEmpty(baseDir))
            {
                return;
            }

            Icon icon = TryLoad(baseDir);
            if (icon == null)
            {
                return;
            }

            form.Icon = icon;
        }

        public static Icon TryLoad(string baseDir)
        {
            string icoPath = Path.Combine(baseDir, "app.ico");
            if (!File.Exists(icoPath))
            {
                return null;
            }

            try
            {
                return new Icon(icoPath);
            }
            catch
            {
                return null;
            }
        }
    }
}
