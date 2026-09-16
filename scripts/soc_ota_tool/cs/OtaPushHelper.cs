using System;
using System.IO;
using System.Linq;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 与 E:\BMCSJB\SocOtaUpgrade 参考版一致：mkdir -p 后 adb push 到 /data/ota/ 目录。
    /// 参考版不做 rm -rf 清空；本地工具若部署在含中文的路径下，adb push 可能把远端文件名截断为 upda，需先复制到 ASCII 临时目录。
    /// </summary>
    internal static class OtaPushHelper
    {
        private static readonly string PushTempDir = Path.Combine(Path.GetTempPath(), "soc_ota_push");
        private const string RemoteZipName = "update.zip";

        /// <summary>
        /// 参考版在 E:\BMCSJB 等纯 ASCII 路径下可直接 push；含中文/非 ASCII 时复制到 %TEMP%\soc_ota_push\update.zip。
        /// </summary>
        public static string ResolvePushSource(string localZip, string adbToolsDir)
        {
            string tempZip = Path.Combine(PushTempDir, RemoteZipName);
            if (Path.GetFullPath(localZip).Equals(Path.GetFullPath(tempZip), StringComparison.OrdinalIgnoreCase))
            {
                return localZip;
            }

            if (!NeedsAsciiPushStaging(localZip, adbToolsDir))
            {
                return localZip;
            }

            Directory.CreateDirectory(PushTempDir);
            File.Copy(localZip, tempZip, true);
            Log.Step("adb push 使用 ASCII 临时路径（避免中文/空格路径导致推送异常）: " + tempZip);
            return tempZip;
        }

        /// <summary>
        /// 与参考版一致：mkdir -p → push 到 remoteDir/（不清空目录、不 chmod 777、不做远端大小校验）。
        /// </summary>
        public static void PushLikeReference(AdbHelper adb, string localZip, string remoteDir)
        {
            remoteDir = remoteDir.TrimEnd('/');
            string pushSource = ResolvePushSource(localZip, adb.ToolsDir);

            Log.Step("推送 update.zip 到 " + remoteDir + "/ ...");
            adb.Run(new[] { "shell", string.Format("mkdir -p {0}", remoteDir) }, true);
            adb.Run(new[] { "push", pushSource, remoteDir + "/" }, false, PushTempDir);

            string listing = adb.Run(new[] { "shell", "ls -1 " + remoteDir + " 2>/dev/null" }, true).Trim();
            if (listing.Contains("upda") && !listing.Contains(RemoteZipName))
            {
                throw new InvalidOperationException(
                    "车机端推送文件名异常（发现 upda 而非 update.zip），请将工具部署到纯 ASCII 路径或确认已使用最新版 SocOtaUpgrade.exe");
            }
        }

        private static bool NeedsAsciiPushStaging(string localZip, string adbToolsDir)
        {
            return !IsAsciiOnlyPath(localZip) || !IsAsciiOnlyPath(adbToolsDir);
        }

        private static bool IsAsciiOnlyPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }
            foreach (char c in path)
            {
                if (c > 127)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
