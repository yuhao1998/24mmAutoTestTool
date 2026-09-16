using System;
using System.IO;
using System.Text;

namespace SocOtaUpgrade
{
    internal static class OtaUpdateScript
    {
        public const string ScriptFileName = "run_soc_update.sh";

        public static string RemoteScriptPath(string remoteOtaDir)
        {
            return remoteOtaDir.TrimEnd('/') + "/" + ScriptFileName;
        }

        public static void Deploy(AdbHelper adb, string workDir, string remoteOtaDir)
        {
            string dir = remoteOtaDir.TrimEnd('/');
            string propsPath = dir + "/payload_properties.txt";
            string remotePath = RemoteScriptPath(dir);

            Log.Step("修复 payload_properties.txt 换行符 (CRLF -> LF) ...");
            adb.Run(new[] { "shell", string.Format(
                "sed -i 's/\\r$//' {0} 2>/dev/null; true", propsPath) }, true);

            string props = adb.Run(new[] { "shell",
                string.Format("cd {0} && cat payload_properties.txt", dir) }, true).Trim();
            if (string.IsNullOrEmpty(props) || !props.Contains("FILE_HASH=") || !props.Contains("FILE_SIZE="))
            {
                throw new InvalidOperationException("车机端 payload_properties.txt 内容异常");
            }
            Log.Step("车机 payload_properties.txt 校验通过");

            string localPath = WriteLinuxScript(workDir, dir);
            Log.Step("推送备用手动脚本（Linux LF）-> " + remotePath);
            adb.Run(new[] { "push", localPath, remotePath }, false);
            adb.Run(new[] { "shell", string.Format(
                "sed -i 's/\\r$//' {0} 2>/dev/null; chmod 644 {0}", remotePath) }, true);
        }

        /// <summary>
        /// 在 adb shell 内串联执行两条命令（等价于已进入 adb shell 后手动输入）。
        /// PROP 与 $PROP 由车机 shell 展开，不经 PC 侧处理。
        /// </summary>
        public static string BuildAdbShellUpdateCommand(string remoteOtaDir)
        {
            string dir = remoteOtaDir.TrimEnd('/');
            return string.Format(
                "cd {0} && PROP=$(cat payload_properties.txt) && update_engine_client --update --payload=file://{0}/payload.bin --headers=\"$PROP\"",
                dir);
        }

        private static string WriteLinuxScript(string workDir, string remoteOtaDir)
        {
            string localPath = Path.Combine(workDir, ScriptFileName);
            string content = ToLinuxText(BuildManualScriptContent(remoteOtaDir));
            File.WriteAllText(localPath, content, new UTF8Encoding(false));
            return localPath;
        }

        private static string BuildManualScriptContent(string remoteOtaDir)
        {
            string dir = remoteOtaDir.TrimEnd('/');
            return "# 在 adb shell 中先执行: cd " + dir + "\n" +
                "PROP=$(cat payload_properties.txt)\n" +
                "update_engine_client \\\n" +
                "--update \\\n" +
                "--payload=file://" + dir + "/payload.bin \\\n" +
                "--headers=\"$PROP\"\n";
        }

        private static string ToLinuxText(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (!normalized.EndsWith("\n", StringComparison.Ordinal))
            {
                normalized += "\n";
            }
            return normalized;
        }
    }
}
