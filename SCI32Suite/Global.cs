using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SCI32Suite
{
    public static class Global
    {
        public static void EnsureFileAssociations()
        {
            string exePath = Application.ExecutablePath;
            string baseDir = Path.GetDirectoryName(exePath);
            string sci32Ico = Path.Combine(baseDir, "sci32.ico");
            string scpalIco = Path.Combine(baseDir, "scpal.ico");

            RegisterExt(".sci32", "SCI32.Project", exePath, "SCI32 Project", sci32Ico);
            RegisterExt(".scpal", "SCI32.Palette", exePath, "SCI32 Palette", scpalIco);
        }

        private static void RegisterExt(string ext, string progId, string exePath, string description, string iconPath)
        {
            // already registered?
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext))
                if (k != null) return;

            if (MessageBox.Show($"Associate {ext} files with this program (and set its icon)?",
                                "File Association",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question) != DialogResult.Yes) return;

            // ProgID
            using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                prog.SetValue("", description ?? progId);
                // Icon (prefer .ico file; Explorer expects .ico or EXE/DLL with icon resource)
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", File.Exists(iconPath) ? $"\"{iconPath}\"" : $"\"{exePath}\",0");

                // Shell\Open\Command
                using (var cmd = prog.CreateSubKey(@"Shell\Open\Command"))
                    cmd.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            // .ext → ProgID
            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
                extKey.SetValue("", progId);

            // Tell Explorer associations changed
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
