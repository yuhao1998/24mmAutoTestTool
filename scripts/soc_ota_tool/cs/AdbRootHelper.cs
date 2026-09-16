using System;
using System.Threading;

namespace SocOtaUpgrade
{
    internal static class AdbRootHelper
    {
        public static void EnsureRoot(AdbHelper adb, UpgradeSession session = null, string logMessage = null)
        {
            if (session != null)
            {
                session.ThrowIfStopRequested();
            }

            string id = adb.Run(new[] { "shell", "id" }, true);
            if (id.Contains("uid=0"))
            {
                Log.Step("adb 已为 root");
                return;
            }

            Log.Step(string.IsNullOrEmpty(logMessage) ? "执行 adb root ..." : logMessage);
            adb.Run(new[] { "root" }, true);
            Sleep(session, 3000);
            if (!adb.WaitDeviceReady(60))
            {
                Log.Step("adb root 后设备尚未就绪，继续检查 root 状态...");
            }

            id = adb.Run(new[] { "shell", "id" }, true);
            if (id.Contains("uid=0"))
            {
                Log.Step("adb root 成功");
                return;
            }

            throw new InvalidOperationException("adb root 失败，请确认车机处于 dev 模式且允许 root");
        }

        public static void EnsureRemount(AdbHelper adb, UpgradeSession session = null)
        {
            EnsureRoot(adb, session, "执行 adb remount 前先 root ...");
            if (session != null)
            {
                session.ThrowIfStopRequested();
            }

            Log.Step("执行 adb remount ...");
            string output = adb.Run(new[] { "remount" }, true);
            if (!string.IsNullOrWhiteSpace(output))
            {
                Log.Step("adb remount 输出: " + output.Trim());
            }

            if (IsRemountSucceeded(output))
            {
                Log.Step("adb remount 成功");
                return;
            }

            string mountInfo = adb.Run(new[] { "shell", "mount | grep -E ' /system | /vendor '" }, true);
            if (IsPartitionWritable(mountInfo))
            {
                Log.Step("adb remount 成功（分区已 rw）");
                return;
            }

            throw new InvalidOperationException(
                "adb remount 失败，请确认 userdebug/eng 镜像且 adb 已 root: " +
                (string.IsNullOrWhiteSpace(output) ? mountInfo.Trim() : output.Trim()));
        }

        private static bool IsRemountSucceeded(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }
            return output.IndexOf("remount succeeded", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("Remounted", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPartitionWritable(string mountInfo)
        {
            if (string.IsNullOrWhiteSpace(mountInfo))
            {
                return false;
            }
            foreach (var line in mountInfo.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains(" rw,") || line.Contains(",rw ") || line.Contains(",rw,") || line.EndsWith(" rw"))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Sleep(UpgradeSession session, int milliseconds)
        {
            if (session != null)
            {
                session.Sleep(milliseconds);
            }
            else
            {
                Thread.Sleep(milliseconds);
            }
        }
    }
}
