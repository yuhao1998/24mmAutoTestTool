using System;

namespace SocOtaUpgrade
{
    internal static class DeviceMaintenanceHelper
    {
        public static void RebootViaSerial(AppConfig cfg)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.SerialPort))
            {
                throw new InvalidOperationException("请先在配置中填写串口号（COM）");
            }
            SerialHelper.RebootViaSerial(cfg);
        }

        public static void SwitchToDevMode(AppConfig cfg, AdbHelper adb)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.SerialPort))
            {
                throw new InvalidOperationException("请先在配置中填写串口号（COM）");
            }
            if (string.IsNullOrWhiteSpace(cfg.UsbModeCmd))
            {
                throw new InvalidOperationException("请先在配置中填写 USB 切 dev 命令");
            }
            SerialHelper.SwitchToDevMode(cfg, adb);
            Log.Step("串口切 dev 完成，adb 已可用");
        }

        public static void AdbRoot(AdbHelper adb)
        {
            AdbRootHelper.EnsureRoot(adb);
        }

        public static void AdbRemount(AdbHelper adb)
        {
            AdbRootHelper.EnsureRemount(adb);
        }
    }
}
