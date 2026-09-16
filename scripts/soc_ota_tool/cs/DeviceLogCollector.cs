using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SocOtaUpgrade
{
    internal static class DeviceLogCollector
    {
        public static void CollectPostRebootLogs(AdbHelper adb, string sessionDir)
        {
            if (adb == null || string.IsNullOrEmpty(sessionDir))
            {
                return;
            }

            string dir = Path.Combine(sessionDir, "post_reboot");
            Directory.CreateDirectory(dir);
            Log.Step("保存重启后设备日志 -> " + dir);

            SaveSerialCapture(dir);
            SaveAdbCommandOutput(adb, new[] { "logcat", "-d", "-v", "time" }, Path.Combine(dir, "logcat_dump.log"));
            SaveAdbCommandOutput(adb, new[] { "shell", "dmesg" }, Path.Combine(dir, "dmesg.log"));
            SaveAdbCommandOutput(adb, new[] { "shell", "getprop" }, Path.Combine(dir, "getprop.txt"));
            SaveAdbCommandOutput(
                adb,
                new[] { "shell", "logcat", "-d", "-b", "crash", "-v", "time" },
                Path.Combine(dir, "logcat_crash.log"));
        }

        public static string CollectRecentTombstones(AdbHelper adb, string sessionDir, int withinMinutes)
        {
            if (adb == null || string.IsNullOrEmpty(sessionDir) || withinMinutes <= 0)
            {
                return null;
            }

            List<string> remoteFiles = ListRecentTombstoneFiles(adb, withinMinutes);
            if (remoteFiles.Count == 0)
            {
                Log.Step("未找到近 " + withinMinutes + " 分钟内的 tombstone 文件，跳过拉取");
                return null;
            }

            string dir = Path.Combine(sessionDir, "tombstones");
            Directory.CreateDirectory(dir);
            Log.Step(string.Format(
                "检测到 {0} 个 tombstone，拉取到本地目录 -> {1}",
                remoteFiles.Count,
                dir));

            var index = new StringBuilder();
            index.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            index.AppendLine("窗口: 近 " + withinMinutes + " 分钟");
            index.AppendLine();

            int pulled = 0;
            foreach (string remote in remoteFiles)
            {
                string fileName = Path.GetFileName(remote.Replace('/', '\\'));
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = "tombstone_" + pulled;
                }
                string localPath = Path.Combine(dir, fileName);
                bool ok = TryPullFile(adb, remote, localPath);
                index.AppendLine((ok ? "[OK] " : "[FAIL] ") + remote + " -> " + localPath);
                if (ok)
                {
                    pulled++;
                }
            }

            index.AppendLine();
            index.AppendLine("成功拉取: " + pulled + "/" + remoteFiles.Count);
            string indexPath = Path.Combine(dir, "tombstones_index.txt");
            File.WriteAllText(indexPath, index.ToString(), Encoding.UTF8);
            Log.Step("tombstone 索引已保存: " + indexPath);
            return dir;
        }

        private static void SaveSerialCapture(string dir)
        {
            string serial = SerialHelper.GetAccumulatedCapture();
            if (string.IsNullOrWhiteSpace(serial))
            {
                return;
            }
            string path = Path.Combine(dir, "serial_capture.log");
            File.WriteAllText(path, serial, Encoding.UTF8);
            Log.Step("串口累积日志已保存: " + path);
        }

        private static void SaveAdbCommandOutput(AdbHelper adb, string[] adbArgs, string localPath)
        {
            try
            {
                string output = adb.Run(adbArgs, true);
                File.WriteAllText(localPath, output ?? string.Empty, Encoding.UTF8);
                Log.Step("已保存: " + Path.GetFileName(localPath));
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    localPath,
                    "采集失败: " + ex.Message,
                    Encoding.UTF8);
                Log.Step("保存 " + Path.GetFileName(localPath) + " 失败: " + ex.Message);
            }
        }

        private static List<string> ListRecentTombstoneFiles(AdbHelper adb, int withinMinutes)
        {
            string output = adb.Run(new[] { "shell", string.Format(
                "find /data/tombstones -type f -mmin -{0} 2>/dev/null", withinMinutes) }, true);
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && line.StartsWith("/", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static bool TryPullFile(AdbHelper adb, string remotePath, string localPath)
        {
            try
            {
                string parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
                adb.Run(new[] { "pull", remotePath, localPath }, false);
                return File.Exists(localPath) && new FileInfo(localPath).Length > 0;
            }
            catch (Exception ex)
            {
                Log.Step("拉取 tombstone 失败 " + remotePath + ": " + ex.Message);
                return false;
            }
        }
    }
}
