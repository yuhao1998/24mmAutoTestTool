using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    internal sealed class HalModuleDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Binary { get; set; }
        public string ProcessPattern { get; set; }
        public string InitService { get; set; }
        public bool Required { get; set; }
    }

    internal sealed class HalModulesManifest
    {
        public string Version { get; set; }
        public string Description { get; set; }
        public List<HalModuleDefinition> Modules { get; set; }

        public HalModulesManifest()
        {
            Modules = new List<HalModuleDefinition>();
        }
    }

    internal sealed class HalStatusCheckItem
    {
        public string ModuleId { get; set; }
        public string ModuleName { get; set; }
        public string Result { get; set; }
        public bool Required { get; set; }
        public bool InImage { get; set; }
        public bool ProcessAlive { get; set; }
        public string InitState { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class HalStatusReport
    {
        public bool Passed { get; set; }
        public string Timestamp { get; set; }
        public string Tier { get; set; }
        public bool BootCompleted { get; set; }
        public int RecentTombstones { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public int SkipCount { get; set; }
        public List<HalStatusCheckItem> Modules { get; set; }
        public List<string> GlobalChecks { get; set; }

        public HalStatusReport()
        {
            Modules = new List<HalStatusCheckItem>();
            GlobalChecks = new List<string>();
        }
    }

    internal sealed class HalStatusException : InvalidOperationException
    {
        public HalStatusException(string summary, string fullReport)
            : base(summary)
        {
            Summary = summary;
            FullReport = fullReport;
        }

        public string Summary { get; private set; }
        public string FullReport { get; private set; }
    }

    internal static class HalStatusChecker
    {
        public static void VerifyOrThrow(AdbHelper adb, AppConfig cfg, string baseDir, string sessionDir)
        {
            if (cfg != null && cfg.SkipHalStatusCheck)
            {
                Log.Step("已跳过 HAL 基本状态检查（SkipHalStatusCheck=true）");
                return;
            }

            Log.Step("========== 步骤 5（L0）：HAL 基本状态检查 ==========");
            AdbRootHelper.EnsureRoot(adb, null, "HAL 检查前执行 adb root ...");
            HalModulesManifest manifest = HalModulesManifestHelper.Load(baseDir, cfg);
            HalStatusReport report = RunChecks(adb, cfg, manifest);
            SaveReport(report, sessionDir);
            LogReport(report);

            int tombstoneMinutes = cfg != null && cfg.HalStatusTombstoneMinutes > 0
                ? cfg.HalStatusTombstoneMinutes
                : 60;
            if (report.RecentTombstones > 0)
            {
                DeviceLogCollector.CollectRecentTombstones(adb, sessionDir, tombstoneMinutes);
            }

            int requiredFails = CountRequiredFails(report);
            int optionalFails = CountOptionalFails(report);
            if (requiredFails == 0 && optionalFails == 0)
            {
                LogGlobalWarningsIfAny(report);
                Log.Step("HAL 基本状态检查 Pass");
                Log.Step("====================================================");
                return;
            }

            string fullReport = BuildFailureText(report);
            string summary = string.Format(
                "HAL 基本状态检查 Fail：{0} 个必检模块异常，{1} 个可选模块异常",
                requiredFails,
                optionalFails);
            Log.Error(summary);
            throw new HalStatusException(summary, fullReport);
        }

        private static void LogGlobalWarningsIfAny(HalStatusReport report)
        {
            if (report == null || report.GlobalChecks == null)
            {
                return;
            }

            bool hasWarn = false;
            foreach (string line in report.GlobalChecks)
            {
                if (line != null && line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    hasWarn = true;
                    Log.Step("全局告警（不阻断）: " + line);
                }
            }

            if (hasWarn)
            {
                Log.Step("说明: HAL 模块均正常，上述全局项仅作记录，不计入失败");
            }
        }

        public static HalStatusReport RunStandalone(AdbHelper adb, AppConfig cfg, string baseDir)
        {
            HalModulesManifest manifest = HalModulesManifestHelper.Load(baseDir, cfg);
            HalStatusReport report = RunChecks(adb, cfg, manifest);
            LogReport(report);
            return report;
        }

        private static HalStatusReport RunChecks(AdbHelper adb, AppConfig cfg, HalModulesManifest manifest)
        {
            var report = new HalStatusReport
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Tier = "L0",
                Passed = true
            };

            string bootCompleted = adb.GetProp("sys.boot_completed");
            report.BootCompleted = bootCompleted == "1";
            if (!report.BootCompleted)
            {
                report.Passed = false;
                report.GlobalChecks.Add("FAIL sys.boot_completed != 1 (actual=" + bootCompleted + ")");
            }
            else
            {
                report.GlobalChecks.Add("PASS sys.boot_completed=1");
            }

            int tombstoneMinutes = cfg != null && cfg.HalStatusTombstoneMinutes > 0
                ? cfg.HalStatusTombstoneMinutes
                : 60;
            report.RecentTombstones = CountRecentTombstones(adb, tombstoneMinutes);
            if (report.RecentTombstones > 0)
            {
                report.Passed = false;
                report.GlobalChecks.Add(string.Format(
                    "FAIL 近 {0} 分钟内 tombstone 数量={1}",
                    tombstoneMinutes, report.RecentTombstones));
            }
            else
            {
                report.GlobalChecks.Add(string.Format(
                    "PASS 近 {0} 分钟内无 tombstone", tombstoneMinutes));
            }

            List<HalModuleDefinition> modulesToCheck = ResolveModulesToCheck(manifest, cfg);
            Dictionary<string, string> initStates = ReadInitServiceStates(adb);
            HashSet<string> aliveBinaries = ReadExistingBinaries(adb, modulesToCheck);
            string psOutput = adb.Run(new[] { "shell", "ps -A" }, true);

            foreach (HalModuleDefinition module in modulesToCheck)
            {
                HalStatusCheckItem item = CheckModule(module, psOutput, initStates, aliveBinaries);
                report.Modules.Add(item);
                if (item.Result == "PASS")
                {
                    report.PassCount++;
                }
                else if (item.Result == "SKIP")
                {
                    report.SkipCount++;
                }
                else if (item.Result == "FAIL")
                {
                    report.FailCount++;
                    if (item.Required)
                    {
                        report.Passed = false;
                    }
                }
            }

            if (CountRequiredFails(report) > 0)
            {
                report.Passed = false;
            }

            return report;
        }

        private static List<HalModuleDefinition> ResolveModulesToCheck(
            HalModulesManifest manifest,
            AppConfig cfg)
        {
            var all = manifest.Modules ?? new List<HalModuleDefinition>();
            var filtered = HalModulesManifestHelper.FilterModules(manifest, cfg != null ? cfg.HalModuleFilter : null);
            if (cfg != null && cfg.HalModuleFilter != null && cfg.HalModuleFilter.Count > 0)
            {
                if (filtered.Count == 0)
                {
                    Log.Step("警告: 包名列表未匹配任何 HAL 模块，将检查全部 " + all.Count + " 个模块");
                    return all;
                }
                Log.Step(string.Format(
                    "按包名列表检查 {0}/{1} 个 HAL 模块",
                    filtered.Count,
                    all.Count));
                return filtered;
            }
            return all;
        }

        private static HalStatusCheckItem CheckModule(
            HalModuleDefinition module,
            string psOutput,
            Dictionary<string, string> initStates,
            HashSet<string> aliveBinaries)
        {
            var item = new HalStatusCheckItem
            {
                ModuleId = module.Id,
                ModuleName = module.Name,
                Required = module.Required
            };

            if (string.IsNullOrWhiteSpace(module.Binary) || !aliveBinaries.Contains(module.Binary))
            {
                item.Result = "SKIP";
                item.InImage = false;
                item.Detail = "镜像内未包含该 HAL 二进制，跳过";
                return item;
            }

            item.InImage = true;
            item.ProcessAlive = IsProcessAlive(psOutput, module.ProcessPattern);
            string initState;
            if (!string.IsNullOrWhiteSpace(module.InitService) &&
                initStates.TryGetValue(module.InitService, out initState))
            {
                item.InitState = initState;
            }

            bool initRunning = string.Equals(item.InitState, "running", StringComparison.OrdinalIgnoreCase);
            if (item.ProcessAlive || initRunning)
            {
                item.Result = "PASS";
                item.Detail = item.ProcessAlive
                    ? "进程存活"
                    : "init.svc=" + item.InitState;
                return item;
            }

            item.Result = "FAIL";
            item.Detail = "进程未运行";
            if (!string.IsNullOrWhiteSpace(item.InitState))
            {
                item.Detail += "，init.svc=" + item.InitState;
            }
            return item;
        }

        private static bool IsProcessAlive(string psOutput, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(psOutput))
            {
                return false;
            }
            return psOutput.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HashSet<string> ReadExistingBinaries(AdbHelper adb, List<HalModuleDefinition> modules)
        {
            var paths = modules
                .Where(m => !string.IsNullOrWhiteSpace(m.Binary))
                .Select(m => m.Binary)
                .Distinct()
                .ToList();
            if (paths.Count == 0)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var sb = new StringBuilder();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append(" && ");
                sb.Append("[ -f ").Append(ShellQuote(paths[i])).Append(" ] && echo EXISTS:").Append(i);
            }

            string output = adb.Run(new[] { "shell", sb.ToString() }, true);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("EXISTS:", StringComparison.Ordinal)) continue;
                string indexText = trimmed.Substring("EXISTS:".Length);
                int index;
                if (int.TryParse(indexText, out index) && index >= 0 && index < paths.Count)
                {
                    existing.Add(paths[index]);
                }
            }
            return existing;
        }

        private static Dictionary<string, string> ReadInitServiceStates(AdbHelper adb)
        {
            string output = adb.Run(new[] { "shell", "getprop" }, true);
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "[init.svc.";
                int start = line.IndexOf(prefix, StringComparison.Ordinal);
                if (start < 0) continue;
                int nameStart = start + prefix.Length;
                int endBracket = line.IndexOf(']', nameStart);
                if (endBracket <= nameStart) continue;
                string name = line.Substring(nameStart, endBracket - nameStart);
                int colon = line.IndexOf("]: [", endBracket, StringComparison.Ordinal);
                if (colon < 0) continue;
                int valueStart = colon + 4;
                int valueEnd = line.LastIndexOf(']');
                if (valueEnd <= valueStart) continue;
                string value = line.Substring(valueStart, valueEnd - valueStart);
                states[name] = value;
            }
            return states;
        }

        private static int CountRecentTombstones(AdbHelper adb, int minutes)
        {
            string output = adb.Run(new[] { "shell", string.Format(
                "find /data/tombstones -type f -mmin -{0} 2>/dev/null | wc -l", minutes) }, true);
            string digits = new string(output.Where(char.IsDigit).ToArray());
            int count;
            return int.TryParse(digits, out count) ? count : 0;
        }

        private static void SaveReport(HalStatusReport report, string sessionDir)
        {
            if (string.IsNullOrEmpty(sessionDir))
            {
                return;
            }
            Directory.CreateDirectory(sessionDir);
            var ser = new JavaScriptSerializer();
            File.WriteAllText(
                Path.Combine(sessionDir, "hal_status.json"),
                ser.Serialize(report),
                Encoding.UTF8);
        }

        private static void LogReport(HalStatusReport report)
        {
            Log.Step("--- 全局检查 ---");
            foreach (string line in report.GlobalChecks)
            {
                Log.Step("  " + line);
            }
            Log.Step("--- HAL 模块 ---");
            foreach (HalStatusCheckItem item in report.Modules)
            {
                Log.Step(string.Format("  [{0}] {1} ({2}) - {3}",
                    item.Result, item.ModuleName, item.ModuleId, item.Detail));
            }
            Log.Step(string.Format("汇总: Pass={0} Fail={1} Skip={2}",
                report.PassCount, report.FailCount, report.SkipCount));
        }

        private static string BuildFailureText(HalStatusReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== HAL 基本状态检查失败报告 ==========");
            sb.AppendLine("时间: " + report.Timestamp);
            sb.AppendLine();
            sb.AppendLine("【全局】");
            foreach (string line in report.GlobalChecks)
            {
                sb.AppendLine("  " + line);
            }
            sb.AppendLine();
            sb.AppendLine("【模块明细】");
            foreach (HalStatusCheckItem item in report.Modules.Where(i => i.Result == "FAIL"))
            {
                sb.AppendLine(string.Format("  [FAIL] {0} ({1}) required={2}",
                    item.ModuleName, item.ModuleId, item.Required));
                sb.AppendLine("    " + item.Detail);
            }
            sb.AppendLine("============================================");
            return sb.ToString();
        }

        private static int CountRequiredFails(HalStatusReport report)
        {
            return report.Modules.Count(i => i.Result == "FAIL" && i.Required);
        }

        private static int CountOptionalFails(HalStatusReport report)
        {
            return report.Modules.Count(i => i.Result == "FAIL" && !i.Required);
        }

        private static string ShellQuote(string value)
        {
            return "'" + value.Replace("'", "'\\''") + "'";
        }
    }
}
