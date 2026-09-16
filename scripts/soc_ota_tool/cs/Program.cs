using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SocOtaUpgrade
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            AdbProcessTracker.SetAdbPath(Path.Combine(baseDir, "tools", "adb.exe"));

            // -gui 强制 GUI 模式（即使带 -package 也走图形界面，便于脚本触发时展示进度条/结果表）
            bool forceGui = HasGuiFlag(args);
            string autoPackage = ExtractPackageArg(args);
            bool reportMode = HasReportFlag(args);

            bool cliMode = !forceGui && args.Length > 0 && HasCliArgs(args);

            if (cliMode)
            {
                AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                Application.ApplicationExit += (_, __) => AdbProcessTracker.CleanupAll(true);
                try
                {
                    return new UpgradeApp(baseDir).Run(args);
                }
                finally
                {
                    AdbProcessTracker.CleanupAll(true);
                }
            }

            Application.ApplicationExit += (_, __) => AdbProcessTracker.CleanupAll(true);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(baseDir, autoPackage, reportMode);
            Application.Run(form);
            return form.ExitCode;
        }

        private static bool HasGuiFlag(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "-gui", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-report", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool HasReportFlag(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "-report", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ExtractPackageArg(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-package", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length)
                {
                    return args[++i];
                }
            }
            return null;
        }

        private static bool HasCliArgs(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "-cli", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-package", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-show-config", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-edit-config", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-stop", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-stop-upgrade", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-hal-status", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-serial-reboot", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-switch-dev", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-adb-root", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-adb-remount", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-help", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
