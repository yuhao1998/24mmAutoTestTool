using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 测试结束后固定生成 BMC 风格 detect_result.html 到会话日志目录。
    /// </summary>
    internal static class SessionDetectReportHelper
    {
        private static readonly string[] McuPropCandidates = new[]
        {
            "vendor.mcu.version",
            "ro.vendor.mcu.version",
            "persist.vendor.mcu.version",
            "ro.boot.mcu.version",
            "sys.mcu.version",
            "ro.mcu.version"
        };

        /// <summary>
        /// 采集车机 SOC/MCU、PC 信息，写入 device_info.json，并调用 Python 生成 HTML。
        /// packagePath 为空时报告写「非ota升级」。
        /// </summary>
        public static string Generate(
            AdbHelper adb,
            string socOtaBaseDir,
            string sessionDir,
            string packagePath)
        {
            if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
            {
                return null;
            }

            string scriptsDir = Path.GetDirectoryName(socOtaBaseDir);
            string pcId, pcName;
            ResolvePcInfo(scriptsDir, out pcId, out pcName);

            string soc = "";
            string mcu = "";
            try
            {
                if (adb != null)
                {
                    soc = (adb.GetProp("ro.build.version.incremental") ?? "").Trim();
                    if (string.IsNullOrEmpty(soc))
                    {
                        soc = (adb.GetProp("ro.build.display.id") ?? "").Trim();
                    }
                    foreach (string prop in McuPropCandidates)
                    {
                        string v = (adb.GetProp(prop) ?? "").Trim();
                        if (!string.IsNullOrEmpty(v))
                        {
                            mcu = v;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Step("采集 SOC/MCU 版本失败: " + ex.Message);
            }

            // version_verify 兜底 SOC
            if (string.IsNullOrEmpty(soc))
            {
                soc = TryReadSocFromVersionVerify(sessionDir);
            }

            var info = new Dictionary<string, object>();
            info["generated_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            info["pc_id"] = pcId ?? "";
            info["pc_name"] = pcName ?? "";
            info["package_path"] = string.IsNullOrWhiteSpace(packagePath) ? "" : packagePath.Trim();
            info["soc"] = soc ?? "";
            info["mcu"] = string.IsNullOrEmpty(mcu) ? "—" : mcu;
            info["ota_path_display"] = string.IsNullOrWhiteSpace(packagePath) ? "非ota升级" : packagePath.Trim();

            string infoPath = Path.Combine(sessionDir, "device_info.json");
            var ser = new JavaScriptSerializer();
            File.WriteAllText(infoPath, ser.Serialize(info), new UTF8Encoding(false));
            Log.Step("已写入 device_info.json（SOC/MCU/PC/OTA 路径）");

            string py = Path.Combine(scriptsDir ?? "", "t2_detect_report.py");
            if (!File.Exists(py))
            {
                Log.Step("未找到 t2_detect_report.py，跳过 HTML 报告");
                return null;
            }

            string pkg = string.IsNullOrWhiteSpace(packagePath) ? "" : packagePath.Trim();
            var argList = new System.Collections.Generic.List<string>();
            argList.Add("-u");
            argList.Add("\"" + py + "\"");
            argList.Add("--session-dir");
            argList.Add("\"" + sessionDir + "\"");
            argList.Add("--scripts-dir");
            argList.Add("\"" + scriptsDir + "\"");
            if (!string.IsNullOrEmpty(pkg))
            {
                argList.Add("--package-path");
                argList.Add("\"" + EscapeArg(pkg) + "\"");
            }
            if (!string.IsNullOrEmpty(pcId))
            {
                argList.Add("--pc-id");
                argList.Add("\"" + EscapeArg(pcId) + "\"");
            }
            if (!string.IsNullOrEmpty(pcName))
            {
                argList.Add("--pc-name");
                argList.Add("\"" + EscapeArg(pcName) + "\"");
            }
            if (!string.IsNullOrEmpty(soc))
            {
                argList.Add("--soc-version");
                argList.Add("\"" + EscapeArg(soc) + "\"");
            }
            if (!string.IsNullOrEmpty(mcu) && mcu != "—")
            {
                argList.Add("--mcu-version");
                argList.Add("\"" + EscapeArg(mcu) + "\"");
            }
            string args = string.Join(" ", argList);

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = args,
                WorkingDirectory = scriptsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";

            Log.Step("生成 BMC 风格报告 detect_result.html ...");
            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    throw new InvalidOperationException("无法启动 python 生成报告");
                }
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120000);
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Log.Step(line);
                    }
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Log.Step("[detect_report stderr] " + stderr.Trim());
                }
                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException("生成 detect_result.html 失败, exit=" + proc.ExitCode);
                }
            }

            string rootHtml = Path.Combine(sessionDir, "detect_result.html");
            if (File.Exists(rootHtml))
            {
                Log.Step("报告已固定生成: " + rootHtml);
                return rootHtml;
            }
            string nested = Path.Combine(sessionDir, "detect", "detect_result.html");
            if (File.Exists(nested))
            {
                File.Copy(nested, rootHtml, true);
                Log.Step("报告已固定生成: " + rootHtml);
                return rootHtml;
            }
            Log.Step("警告: 未找到生成的 detect_result.html");
            return null;
        }

        private static string EscapeArg(string s)
        {
            return (s ?? "").Replace("\"", "\\\"");
        }

        private static string TryReadSocFromVersionVerify(string sessionDir)
        {
            try
            {
                string path = Path.Combine(sessionDir, "version_verify.json");
                if (!File.Exists(path)) return "";
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return "";
                object itemsObj;
                if (!root.TryGetValue("Items", out itemsObj) || itemsObj == null) return "";
                var list = itemsObj as System.Collections.ArrayList;
                if (list == null)
                {
                    var arr = itemsObj as object[];
                    if (arr != null) list = new System.Collections.ArrayList(arr);
                }
                if (list == null) return "";
                foreach (object o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = Convert.ToString(d.ContainsKey("Name") ? d["Name"] : "");
                    if (name != null && name.IndexOf("incremental", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return Convert.ToString(d.ContainsKey("Actual") ? d["Actual"] : "") ?? "";
                    }
                }
            }
            catch
            {
                // ignore
            }
            return "";
        }

        private static void ResolvePcInfo(string scriptsDir, out string pcId, out string pcName)
        {
            pcId = "";
            pcName = Environment.MachineName;
            if (string.IsNullOrEmpty(scriptsDir)) return;

            string[] candidates = new[]
            {
                Path.Combine(scriptsDir, "t2_atf_config.json"),
                Path.GetFullPath(Path.Combine(scriptsDir, "..", "BMC_ATF_auto-exec-controller", "config.json"))
            };

            foreach (string path in candidates)
            {
                TryReadPcFromJson(path, scriptsDir, ref pcId, ref pcName);
                if (!string.IsNullOrEmpty(pcId)) return;
            }
        }

        private static void TryReadPcFromJson(string path, string scriptsDir, ref string pcId, ref string pcName)
        {
            if (!File.Exists(path)) return;
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return;

                object pcObj;
                if (root.TryGetValue("pc", out pcObj))
                {
                    var pc = pcObj as Dictionary<string, object>;
                    if (pc != null)
                    {
                        if (pc.ContainsKey("pc_id") && pc["pc_id"] != null)
                            pcId = Convert.ToString(pc["pc_id"]) ?? pcId;
                        if (pc.ContainsKey("name") && pc["name"] != null)
                            pcName = Convert.ToString(pc["name"]) ?? pcName;
                        else if (pc.ContainsKey("alias") && pc["alias"] != null)
                            pcName = Convert.ToString(pc["alias"]) ?? pcName;
                    }
                }

                if (string.IsNullOrEmpty(pcId) && root.ContainsKey("bmc_config_path"))
                {
                    string rel = Convert.ToString(root["bmc_config_path"]);
                    if (!string.IsNullOrEmpty(rel))
                    {
                        string bmc = Path.IsPathRooted(rel)
                            ? rel
                            : Path.GetFullPath(Path.Combine(scriptsDir, rel));
                        TryReadPcFromJson(bmc, scriptsDir, ref pcId, ref pcName);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
