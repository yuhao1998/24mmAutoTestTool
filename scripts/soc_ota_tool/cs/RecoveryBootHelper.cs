using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SocOtaUpgrade
{
    /// <summary>
    /// OTA 写入成功但重启后进 Recovery 时抛出，携带原因分析报告。
    /// </summary>
    internal sealed class RecoveryBootException : InvalidOperationException
    {
        public RecoveryBootException(string summary, string fullReport)
            : base(summary)
        {
            ReasonSummary = summary;
            FullReport = fullReport;
        }

        public string ReasonSummary { get; private set; }
        public string FullReport { get; private set; }
    }

    internal sealed class RecoveryBootAnalysis
    {
        private readonly List<string> _evidence = new List<string>();
        private readonly List<string> _probableCauses = new List<string>();
        private readonly List<string> _suggestions = new List<string>();

        public bool IsRecovery { get; set; }
        public bool IsRebootLoop { get; set; }
        public List<string> Evidence { get { return _evidence; } }
        public List<string> ProbableCauses { get { return _probableCauses; } }
        public List<string> Suggestions { get { return _suggestions; } }
        public string BootSlot { get; set; }
        public string RebootCommand { get; set; }

        public string BuildSummary()
        {
            if (!IsRecovery)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            if (IsRebootLoop)
            {
                sb.Append("OTA payload 已写入，但车机进入 Recovery 反复重启");
            }
            else
            {
                sb.Append("OTA 已写入，但车机重启后进入 Recovery 模式");
            }
            if (!string.IsNullOrEmpty(BootSlot))
            {
                sb.Append("（尝试启动槽位 ").Append(BootSlot).Append("）");
            }
            sb.Append("。");
            if (ProbableCauses.Count > 0)
            {
                sb.Append(" 可能原因: ").Append(ProbableCauses[0]);
            }
            return sb.ToString();
        }

        public string BuildFullReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== Recovery 启动分析报告 ==========");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("【结论】");
            sb.AppendLine(BuildSummary());
            sb.AppendLine();
            if (!string.IsNullOrEmpty(RebootCommand))
            {
                sb.AppendLine("【重启命令】");
                sb.AppendLine("  " + RebootCommand);
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(BootSlot))
            {
                sb.AppendLine("【启动槽位】");
                sb.AppendLine("  " + BootSlot);
                sb.AppendLine();
            }
            if (Evidence.Count > 0)
            {
                sb.AppendLine("【检测依据】");
                foreach (var e in Evidence)
                {
                    sb.AppendLine("  - " + e);
                }
                sb.AppendLine();
            }
            if (ProbableCauses.Count > 0)
            {
                sb.AppendLine("【可能原因】");
                for (int i = 0; i < ProbableCauses.Count; i++)
                {
                    sb.AppendLine("  " + (i + 1) + ". " + ProbableCauses[i]);
                }
                sb.AppendLine();
            }
            if (Suggestions.Count > 0)
            {
                sb.AppendLine("【建议排查】");
                for (int i = 0; i < Suggestions.Count; i++)
                {
                    sb.AppendLine("  " + (i + 1) + ". " + Suggestions[i]);
                }
                sb.AppendLine();
            }
            sb.AppendLine("==========================================");
            return sb.ToString();
        }
    }

    internal static class RecoveryBootAnalyzer
    {
        private static readonly string[] SerialRecoveryMarkers =
        {
            "Booting Into Recovery Mode",
            "Booting into Recovery",
            "Recovery:1",
            "Fastboot=0, Recovery:1",
            "reboot: Restarting system with command 'recovery'",
            "androidboot.recover_usb=1",
            "Enter recovery mode",
            "Booting Into Recovery"
        };

        public static RecoveryBootAnalysis Analyze(
            string serialText,
            AdbHelper adb,
            string updateEngineLogPath)
        {
            var analysis = new RecoveryBootAnalysis();
            string serial = serialText ?? string.Empty;
            string engineLog = string.Empty;
            if (!string.IsNullOrEmpty(updateEngineLogPath) && File.Exists(updateEngineLogPath))
            {
                try
                {
                    engineLog = FileLogHelper.ReadAll(updateEngineLogPath);
                }
                catch
                {
                    engineLog = FileLogHelper.ReadTail(updateEngineLogPath, 500);
                }
            }

            foreach (var marker in SerialRecoveryMarkers)
            {
                if (serial.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    analysis.IsRecovery = true;
                    AddEvidenceOnce(analysis, "串口日志: " + marker);
                }
            }

            var slotMatch = Regex.Match(serial, @"Booting from slot \((_?[ab])\)", RegexOptions.IgnoreCase);
            if (slotMatch.Success)
            {
                analysis.BootSlot = slotMatch.Groups[1].Value;
                if (analysis.IsRecovery)
                {
                    AddEvidenceOnce(analysis, "Bootloader 尝试从槽位 " + analysis.BootSlot + " 启动");
                }
            }

            var rebootCmdMatch = Regex.Match(
                serial,
                @"reboot: Restarting system with command '([^']+)'",
                RegexOptions.IgnoreCase);
            if (rebootCmdMatch.Success)
            {
                analysis.RebootCommand = rebootCmdMatch.Groups[1].Value;
                if (analysis.RebootCommand.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    analysis.IsRecovery = true;
                    AddEvidenceOnce(analysis, "内核重启命令: recovery");
                }
            }

            if (adb != null)
            {
                CollectAdbRecoveryEvidence(adb, analysis);
            }

            if (!string.IsNullOrEmpty(engineLog))
            {
                CollectEngineLogEvidence(engineLog, analysis);
            }

            CollectSerialRebootLoopEvidence(serial, analysis);

            if (analysis.IsRecovery)
            {
                BuildProbableCauses(analysis, engineLog, serial);
                BuildSuggestions(analysis);
            }

            return analysis;
        }

        private static void CollectAdbRecoveryEvidence(AdbHelper adb, RecoveryBootAnalysis analysis)
        {
            string state = adb.GetPrimaryDeviceState();
            if (!string.IsNullOrEmpty(state))
            {
                if (state.Equals("recovery", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("sideload", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.IsRecovery = true;
                    AddEvidenceOnce(analysis, "adb devices 状态: " + state);
                }
            }

            string[] props =
            {
                "ro.bootmode", "ro.boot.mode", "androidboot.mode",
                "ro.boot.recovery", "ro.build.display.id"
            };
            foreach (var prop in props)
            {
                string val = adb.GetProp(prop);
                if (string.IsNullOrEmpty(val)) continue;
                if (val.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    analysis.IsRecovery = true;
                    AddEvidenceOnce(analysis, "getprop " + prop + "=" + val);
                }
            }

            if (string.IsNullOrEmpty(analysis.BootSlot))
            {
                string slot = adb.GetProp("ro.boot.slot_suffix");
                if (!string.IsNullOrEmpty(slot))
                {
                    analysis.BootSlot = slot.TrimStart('_');
                }
            }
        }

        private static void CollectSerialRebootLoopEvidence(string serial, RecoveryBootAnalysis analysis)
        {
            if (string.IsNullOrEmpty(serial))
            {
                return;
            }

            int recoveryBootCount = CountOccurrences(serial, "Booting Into Recovery Mode")
                + CountOccurrences(serial, "Booting into Recovery")
                + CountOccurrences(serial, "Fastboot=0, Recovery:1");
            int restartCount = CountOccurrences(serial, "Going down for restart")
                + CountOccurrences(serial, "reboot: Restarting system");

            if (recoveryBootCount >= 2 || (recoveryBootCount >= 1 && restartCount >= 2))
            {
                analysis.IsRecovery = true;
                analysis.IsRebootLoop = true;
                AddEvidenceOnce(analysis,
                    string.Format("串口检测到 Recovery/重启循环（Recovery 启动约 {0} 次，系统重启约 {1} 次）",
                        recoveryBootCount, restartCount));
            }
        }

        private static int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            {
                return 0;
            }
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        private static void CollectEngineLogEvidence(string engineLog, RecoveryBootAnalysis analysis)
        {
            if (Regex.IsMatch(engineLog, @"Update successfully applied", RegexOptions.IgnoreCase))
            {
                AddEvidenceOnce(analysis, "update_engine: OTA payload 已成功写入（Update successfully applied）");
            }

            if (engineLog.IndexOf("overlayfs overrides are active", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddEvidenceOnce(analysis, "update_engine: overlayfs overrides are active（overlay 仍生效）");
            }

            int verityDisabled = Regex.Matches(
                engineLog,
                @"Verity writes disabled on partition",
                RegexOptions.IgnoreCase).Count;
            if (verityDisabled > 0)
            {
                AddEvidenceOnce(analysis,
                    "update_engine: " + verityDisabled + " 个分区 Verity writes disabled（B 槽未写入 dm-verity 元数据）");
            }

            if (engineLog.IndexOf("switch_slot_on_reboot: true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                engineLog.IndexOf("Switching slot to", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddEvidenceOnce(analysis, "update_engine: 升级后已切换活动槽位（A/B slot switch）");
            }

            if (engineLog.IndexOf("verity is already enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                engineLog.IndexOf("already enabled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddEvidenceOnce(analysis, "升级前 enable-verity 显示 already enabled（overlay 可能未真正关闭）");
            }
        }

        private static void BuildProbableCauses(
            RecoveryBootAnalysis analysis,
            string engineLog,
            string serial)
        {
            bool hasOverlay = analysis.Evidence.Any(e => e.IndexOf("overlayfs", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasVerityDisabled = analysis.Evidence.Any(e => e.IndexOf("Verity writes disabled", StringComparison.OrdinalIgnoreCase) >= 0);
            bool otaSucceeded = analysis.Evidence.Any(e => e.IndexOf("Update successfully applied", StringComparison.OrdinalIgnoreCase) >= 0);
            bool slotB = !string.IsNullOrEmpty(analysis.BootSlot) &&
                         analysis.BootSlot.Equals("b", StringComparison.OrdinalIgnoreCase);

            if (analysis.IsRebootLoop)
            {
                AddCauseOnce(analysis,
                    "B 槽启动失败后 bootloader 反复进入 Recovery，形成重启循环（与手动 update_engine_client 成功写入后的现象一致）");
            }

            if (otaSucceeded && (slotB || serial.IndexOf("slot (_b)", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                AddCauseOnce(analysis,
                    "B 槽（新系统）首次启动失败：bootloader 校验或 init 启动链异常，自动 fallback 到 Recovery");
            }

            if (hasOverlay)
            {
                AddCauseOnce(analysis,
                    "升级时 overlayfs 仍处于 active 状态，可能导致分区内容与 OTA 预期不一致，B 槽启动校验失败");
            }

            if (hasVerityDisabled)
            {
                AddCauseOnce(analysis,
                    "OTA 过程中未向 B 槽写入 dm-verity 哈希树（Verity writes disabled），新槽 dm-verity 校验可能失败");
            }

            if (!string.IsNullOrEmpty(analysis.RebootCommand) &&
                analysis.RebootCommand.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddCauseOnce(analysis,
                    "update_engine / init 在 OTA 完成后以 reboot recovery 方式重启（可能因 slot 切换或 postinstall 触发）");
            }

            if (analysis.ProbableCauses.Count == 0)
            {
                AddCauseOnce(analysis,
                    "新系统镜像启动失败或 bootloader 判定当前槽位不可启动，进入 Recovery 进行修复/回退");
            }
        }

        private static void BuildSuggestions(RecoveryBootAnalysis analysis)
        {
            analysis.Suggestions.Add("确认 OTA 包与车机 ro.build.fingerprint / 硬件版本完全匹配");
            analysis.Suggestions.Add("升级前在车机执行 mount | grep overlay，确认 overlayfs 已关闭后再 OTA");
            analysis.Suggestions.Add("若 enable-verity 仅显示 already enabled 但仍有 overlay 警告，需按平台文档彻底 disable overlay");
            if (!string.IsNullOrEmpty(analysis.BootSlot) &&
                analysis.BootSlot.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                analysis.Suggestions.Add("Recovery 下可尝试: adb shell bootctl set-active 0 && adb reboot 回退 A 槽");
            }
            analysis.Suggestions.Add("Recovery 模式下 USB 为 recover_usb，正常 dev 切 peripheral 无法建立 adb，需先 boot 正常系统或 Recovery 专用 adb");
            if (analysis.IsRebootLoop)
            {
                analysis.Suggestions.Insert(0, "Recovery 反复重启时请勿继续等待 adb/dev 模式，应断电或通过 bootctl 回退 A 槽");
            }
            analysis.Suggestions.Add("手动升级若同样进入 Recovery 循环，说明 OTA 写入成功但新系统无法启动，需排查镜像/overlay/verity");
        }

        private static void AddEvidenceOnce(RecoveryBootAnalysis analysis, string text)
        {
            if (!analysis.Evidence.Contains(text))
            {
                analysis.Evidence.Add(text);
            }
        }

        private static void AddCauseOnce(RecoveryBootAnalysis analysis, string text)
        {
            if (!analysis.ProbableCauses.Contains(text))
            {
                analysis.ProbableCauses.Add(text);
            }
        }

        public static void ThrowIfRecovery(
            RecoveryBootAnalysis analysis,
            string sessionDir)
        {
            if (analysis == null || !analysis.IsRecovery)
            {
                return;
            }

            string report = analysis.BuildFullReport();
            string logPath = string.Empty;
            if (!string.IsNullOrEmpty(sessionDir))
            {
                try
                {
                    Directory.CreateDirectory(sessionDir);
                    logPath = Path.Combine(sessionDir, "recovery_boot.log");
                    File.WriteAllText(logPath, report, Encoding.UTF8);
                }
                catch
                {
                    // ignore write failure
                }
            }

            string summary = analysis.BuildSummary();
            Log.Error("========== 检测到 Recovery 模式 ==========");
            Log.Error(summary);
            foreach (var cause in analysis.ProbableCauses.Take(3))
            {
                Log.Error("  原因: " + cause);
            }
            if (!string.IsNullOrEmpty(logPath))
            {
                Log.Error("完整分析报告: " + logPath);
            }
            Log.Error("==========================================");

            throw new RecoveryBootException(summary, report);
        }
    }
}
