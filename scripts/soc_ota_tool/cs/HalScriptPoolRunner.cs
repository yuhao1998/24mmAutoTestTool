using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 脚本池调度：按 UI 同步的 t2_script_pool.json，
    /// 建设本机路径 → adb push 到车机 → 执行 run.sh → 拉取结果到会话目录。
    /// 实际 push/执行逻辑复用 scripts/t2_script_pool.py。
    /// </summary>
    internal sealed class HalScriptPoolResult
    {
        public bool Ran { get; set; }
        public bool Passed { get; set; }
        public bool Enabled { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public int SkipCount { get; set; }
        public string ResultFile { get; set; }
        public string DetectHtml { get; set; }
        public string Summary { get; set; }
    }

    internal sealed class HalScriptPoolException : Exception
    {
        public HalScriptPoolResult Result { get; private set; }

        public HalScriptPoolException(string message, HalScriptPoolResult result)
            : base(message)
        {
            Result = result;
        }
    }

    internal static class HalScriptPoolRunner
    {
        public static string ScriptsDir(string socOtaBaseDir)
        {
            return Path.GetDirectoryName(socOtaBaseDir);
        }

        public static string PoolPath(string socOtaBaseDir)
        {
            string scriptsDir = ScriptsDir(socOtaBaseDir);
            return string.IsNullOrEmpty(scriptsDir)
                ? null
                : Path.Combine(scriptsDir, "t2_script_pool.json");
        }

        /// <summary>
        /// 统计 pool 中 enabled=true 的条目数。pool 不存在或未启用则返回 0。
        /// </summary>
        public static int CountEnabledScripts(string socOtaBaseDir)
        {
            string poolPath = PoolPath(socOtaBaseDir);
            if (string.IsNullOrEmpty(poolPath) || !File.Exists(poolPath))
            {
                return 0;
            }
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(poolPath, Encoding.UTF8));
                if (root == null)
                {
                    return 0;
                }
                object en;
                if (root.TryGetValue("enabled", out en) && en is bool && !(bool)en)
                {
                    return 0;
                }
                object scriptsObj;
                if (!root.TryGetValue("scripts", out scriptsObj) || scriptsObj == null)
                {
                    return 0;
                }
                var list = scriptsObj as System.Collections.ArrayList;
                if (list == null)
                {
                    var typed = scriptsObj as object[];
                    if (typed == null) return 0;
                    list = new System.Collections.ArrayList(typed);
                }
                int n = 0;
                foreach (object o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    object se;
                    if (d.TryGetValue("enabled", out se) && se is bool && !(bool)se)
                    {
                        continue;
                    }
                    n++;
                }
                return n;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 若无启用脚本则跳过；否则 root/remount（尽力）后调用 Python 执行脚本池。
        /// throwOnFail=true 时 FailCount&gt;0 抛 HalScriptPoolException。
        /// </summary>
        public static HalScriptPoolResult Run(
            AdbHelper adb,
            string socOtaBaseDir,
            string sessionDir,
            bool throwOnFail)
        {
            var result = new HalScriptPoolResult
            {
                Ran = false,
                Passed = true,
                Enabled = false,
                Summary = "未执行脚本池"
            };

            string scriptsDir = ScriptsDir(socOtaBaseDir);
            string poolPath = PoolPath(socOtaBaseDir);
            if (string.IsNullOrEmpty(scriptsDir) || !Directory.Exists(scriptsDir))
            {
                result.Summary = "未找到 scripts 目录，跳过脚本池";
                Log.Step(result.Summary);
                return result;
            }

            int enabled = CountEnabledScripts(socOtaBaseDir);
            if (enabled <= 0)
            {
                result.Summary = "未配置启用的自检脚本路径（或脚本池 enabled=false），跳过 push/执行";
                Log.Step(result.Summary);
                return result;
            }

            string py = Path.Combine(scriptsDir, "t2_script_pool.py");
            if (!File.Exists(py))
            {
                throw new FileNotFoundException("未找到脚本池执行器: " + py);
            }

            result.Enabled = true;
            result.Ran = true;
            Log.Step("========== 脚本池：建设路径 → push → 执行 → 收集 ==========");
            Log.Step("启用项数=" + enabled + "；pool=" + poolPath);
            Log.Step("车机 HAL 目录约定：/data/local/tmp/t2_selfcheck/<module_id>/");

            TryPrepareDevice(adb);

            string args = string.Format(
                "-u \"{0}\" --scripts-dir \"{1}\" --pool \"{2}\" --session-dir \"{3}\"",
                py, scriptsDir, poolPath, sessionDir);

            var psi = new ProcessStartInfo
            {
                FileName = ResolvePython(),
                Arguments = args,
                WorkingDirectory = scriptsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // 强制 Python UTF-8 输出，避免中文乱码（.NET 4.x 无 StandardOutputEncoding）
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";

            Log.Step("启动: " + psi.FileName + " " + args);
            int exitCode;
            using (var proc = new Process { StartInfo = psi })
            {
                var sbErr = new StringBuilder();
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Step(e.Data);
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        sbErr.AppendLine(e.Data);
                        Log.Step("[script_pool stderr] " + e.Data);
                    }
                };
                if (!proc.Start())
                {
                    throw new InvalidOperationException("无法启动 python 执行脚本池");
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // 单模块最长 300s，预留多模块与 push；上限 30 分钟
                if (!proc.WaitForExit(30 * 60 * 1000))
                {
                    try { proc.Kill(); } catch { }
                    throw new TimeoutException("脚本池执行超时（>30min）");
                }
                exitCode = proc.ExitCode;
            }

            FillFromResultFile(result, sessionDir);
            if (string.IsNullOrEmpty(result.Summary))
            {
                result.Summary = string.Format(
                    "脚本池完成 exit={0} Pass={1} Fail={2} Skip={3}",
                    exitCode, result.PassCount, result.FailCount, result.SkipCount);
            }
            Log.Step(result.Summary);
            if (!string.IsNullOrEmpty(result.DetectHtml) && File.Exists(result.DetectHtml))
            {
                Log.Step("检测报告: " + result.DetectHtml);
            }

            result.Passed = result.FailCount == 0 && exitCode == 0;
            if (!result.Passed && throwOnFail)
            {
                throw new HalScriptPoolException(
                    "脚本池执行未通过: " + result.Summary +
                    (string.IsNullOrEmpty(result.DetectHtml)
                        ? ""
                        : ("\n报告: " + result.DetectHtml)),
                    result);
            }
            return result;
        }

        public static HalScriptPoolResult RunOrThrow(
            AdbHelper adb, string socOtaBaseDir, string sessionDir)
        {
            return Run(adb, socOtaBaseDir, sessionDir, throwOnFail: true);
        }

        private static void TryPrepareDevice(AdbHelper adb)
        {
            try
            {
                AdbRootHelper.EnsureRoot(adb, null, "脚本池执行前 adb root ...");
            }
            catch (Exception ex)
            {
                Log.Step("警告: adb root 失败（仍尝试执行）: " + ex.Message);
                return;
            }
            try
            {
                // L2 写 /vendor 需要 remount；失败不阻断（仅 L1 的模块仍可跑）
                AdbRootHelper.EnsureRemount(adb);
            }
            catch (Exception ex)
            {
                Log.Step("警告: adb remount 失败（写 vendor 的 L2 可能失败）: " + ex.Message);
            }
        }

        private static string ResolvePython()
        {
            // 与邮箱监听一致：优先 PATH 中的 python
            return "python";
        }

        private static void FillFromResultFile(HalScriptPoolResult result, string sessionDir)
        {
            string resultFile = Path.Combine(sessionDir, "script_results.json");
            result.ResultFile = File.Exists(resultFile) ? resultFile : null;
            string detectHtml = Path.Combine(sessionDir, "detect", "detect_result.html");
            if (File.Exists(detectHtml))
            {
                result.DetectHtml = detectHtml;
            }

            if (!File.Exists(resultFile))
            {
                return;
            }
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(resultFile, Encoding.UTF8));
                if (root == null) return;
                object v;
                if (root.TryGetValue("PassCount", out v) && v != null)
                    result.PassCount = Convert.ToInt32(v);
                if (root.TryGetValue("FailCount", out v) && v != null)
                    result.FailCount = Convert.ToInt32(v);
                if (root.TryGetValue("SkipCount", out v) && v != null)
                    result.SkipCount = Convert.ToInt32(v);
                if (root.TryGetValue("Passed", out v) && v is bool)
                    result.Passed = (bool)v;
                if (root.TryGetValue("detect_dir", out v) && v != null)
                {
                    string dd = Convert.ToString(v);
                    string html = Path.Combine(dd, "detect_result.html");
                    if (File.Exists(html)) result.DetectHtml = html;
                }
                result.Summary = string.Format(
                    "脚本池 Pass={0} Fail={1} Skip={2}",
                    result.PassCount, result.FailCount, result.SkipCount);
            }
            catch (Exception ex)
            {
                Log.Step("解析 script_results.json 失败: " + ex.Message);
            }
        }
    }
}
