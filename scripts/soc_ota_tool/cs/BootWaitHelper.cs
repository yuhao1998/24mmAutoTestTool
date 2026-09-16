using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace SocOtaUpgrade
{
    internal static class BootWaitHelper
    {
        private const int PostRebootSerialWarmupSec = 20;
        private const int SerialDevRetryIntervalSec = 60;
        private const int PostOtaAdbWaitSec = 30;
        private const int RecoverySerialPollSec = 5;

        public static void WaitAfterReboot(
            AdbHelper adb,
            AppConfig cfg,
            int timeoutSec,
            UpgradeSession session,
            string sessionDir = null,
            string updateEngineLogPath = null,
            bool detectRecovery = false,
            string preOtaSlotLetter = null)
        {
            Log.Step("等待车机 reboot 并完成启动...");
            if (detectRecovery)
            {
                Log.Step("说明: 已执行 adb reboot；等待槽位切换后 Android 正常启动");
            }
            WaitAdbDisconnect(adb, 90, session);

            if (detectRecovery)
            {
                WaitPostOtaPassiveWarmup(cfg, timeoutSec, session, sessionDir, updateEngineLogPath);
                ConnectAdbAndWaitBootAfterOta(
                    adb, cfg, timeoutSec, session, sessionDir, updateEngineLogPath, preOtaSlotLetter);
                return;
            }

            WaitAdbReconnectAfterReboot(
                adb, cfg, Math.Min(180, timeoutSec), timeoutSec, session, sessionDir, updateEngineLogPath, false);

            var start = DateTime.Now;
            var deadline = start.AddSeconds(timeoutSec);
            int round = 0;
            while (DateTime.Now < deadline)
            {
                if (session != null) session.ThrowIfStopRequested();
                round++;
                if (round == 1 || round % 10 == 0)
                {
                    int elapsed = (int)(DateTime.Now - start).TotalSeconds;
                    Log.Step(string.Format("检查启动状态（已等待 {0}s / {1}s）...", elapsed, timeoutSec));
                }

                if (!adb.DeviceReady())
                {
                    Log.Step("adb 未连接，经串口切换 USB dev 模式...");
                    TrySerialDevMode(cfg, adb, false);
                    if (session != null) session.Sleep(3000);
                    continue;
                }

                string reason;
                if (IsAndroidBooted(adb, out reason))
                {
                    if (session != null) session.Sleep(5000);
                    Log.Step("系统启动完成（" + reason + "）");
                    return;
                }

                if (round % 8 == 0)
                {
                    Log.Step("启动未完成，当前: " + reason + "，继续等待...");
                }
                if (session != null) session.Sleep(3000);
            }

            throw new TimeoutException("等待启动完成超时（" + timeoutSec + "s），请确认车机已进入 Android 且 adb 可用");
        }

        /// <summary>
        /// OTA reboot 后仅被动等待串口恢复 + 固定 warmup，不做 Recovery 判定。
        /// </summary>
        private static void WaitPostOtaPassiveWarmup(
            AppConfig cfg,
            int serialResumeTimeoutSec,
            UpgradeSession session,
            string sessionDir,
            string updateEngineLogPath)
        {
            SerialHelper.ResetCapture();
            Log.Step(string.Format(
                "reboot 已开始，被动监听串口 {0} 直至通讯恢复，恢复后再等待 {1}s ...",
                cfg.SerialPort, PostRebootSerialWarmupSec));
            SerialHelper.WaitSerialResumeThenCapture(
                cfg, PostRebootSerialWarmupSec, serialResumeTimeoutSec, session,
                onCaptureChunk: chunk => CheckRecoveryDuringWarmup(chunk, sessionDir, updateEngineLogPath));
            CheckRecoveryAfterWarmup(sessionDir, updateEngineLogPath);
            Log.Step(string.Format(
                "{0}s 被动等待完成，开始经串口切换 dev 并连接 adb 进行槽位/版本校验 ...",
                PostRebootSerialWarmupSec));
        }

        private static void CheckRecoveryDuringWarmup(
            string chunk,
            string sessionDir,
            string updateEngineLogPath)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }
            var analysis = RecoveryBootAnalyzer.Analyze(
                SerialHelper.GetAccumulatedCapture(), null, updateEngineLogPath);
            if (analysis.IsRebootLoop)
            {
                RecoveryBootAnalyzer.ThrowIfRecovery(analysis, sessionDir);
            }
        }

        private static void CheckRecoveryAfterWarmup(string sessionDir, string updateEngineLogPath)
        {
            var analysis = RecoveryBootAnalyzer.Analyze(
                SerialHelper.GetAccumulatedCapture(), null, updateEngineLogPath);
            if (analysis.IsRecovery)
            {
                RecoveryBootAnalyzer.ThrowIfRecovery(analysis, sessionDir);
            }
        }

        /// <summary>
        /// 150s 等待结束后：串口切 dev → adb 连接 → 查询 ro.boot.slot_suffix → 等待 Android 启动。
        /// Recovery 仅在 adb 明确处于 recovery 状态时判定失败。
        /// </summary>
        private static void ConnectAdbAndWaitBootAfterOta(
            AdbHelper adb,
            AppConfig cfg,
            int timeoutSec,
            UpgradeSession session,
            string sessionDir,
            string updateEngineLogPath,
            string preOtaSlotLetter)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            int round = 0;
            bool slotLogged = false;

            while (DateTime.Now < deadline)
            {
                if (session != null) session.ThrowIfStopRequested();
                round++;

                if (!adb.DeviceReady() || !adb.ShellUsable())
                {
                    if (round == 1 || round % (SerialDevRetryIntervalSec / 5) == 0)
                    {
                        Log.Step("adb 未连接，经串口 su → start adbd → 切换 USB peripheral（dev）模式 ...");
                        TrySerialDevMode(cfg, adb, postOtaRecoveryWatch: false);
                    }
                    else if (round % 5 == 0)
                    {
                        Log.Step("等待 adb devices 出现 device 状态...");
                    }
                    Sleep(session, 5000);
                    continue;
                }

                if (!slotLogged)
                {
                    LogPostOtaSlot(adb, preOtaSlotLetter);
                    slotLogged = true;
                }

                if (adb.IsRecoveryViaAdb())
                {
                    CheckAndThrowRecovery(adb, cfg, sessionDir, updateEngineLogPath);
                }

                string reason;
                if (IsAndroidBooted(adb, out reason))
                {
                    if (session != null) session.Sleep(5000);
                    Log.Step("系统启动完成（" + reason + "）");
                    return;
                }

                if (round % 10 == 0)
                {
                    Log.Step("等待系统启动完成，当前: " + reason + " ...");
                }
                Sleep(session, 3000);
            }

            throw new TimeoutException(
                "OTA 已写入，但等待 Android 启动及 adb 连接超时，请检查 USB/串口及 dev 模式");
        }

        private static void LogPostOtaSlot(AdbHelper adb, string preOtaSlotLetter)
        {
            Log.Step("========== 升级后 A/B 槽位检查 ==========");
            string suffix = adb.GetProp("ro.boot.slot_suffix").Trim();
            Log.Step("  adb shell getprop ro.boot.slot_suffix = " +
                (string.IsNullOrEmpty(suffix) ? "未知" : suffix));

            string pre = AbSlotHelper.NormalizeSlot(preOtaSlotLetter);
            string actual = AbSlotHelper.NormalizeSlot(suffix);
            if (!string.IsNullOrEmpty(pre))
            {
                string expected = pre.Equals("a", StringComparison.OrdinalIgnoreCase) ? "b" : "a";
                Log.Step(string.Format(
                    "  升级前槽: _{0}，OTA 预期切换至: _{1}，当前解析槽: {2}",
                    pre,
                    expected,
                    string.IsNullOrEmpty(actual) ? "未知" : "_" + actual));
            }

            AbSlotHelper.LogSlotSnapshot("升级后 A/B 槽位", AbSlotHelper.ReadCurrent(adb));
            Log.Step("==========================================");
        }

        /// <summary>
        /// OTA 写入完成后由 PC 侧执行 adb reboot，激活 A/B 槽位切换。
        /// reboot 后至启动完成前仅被动监听，不执行 kill-server 等可能干扰升级的操作。
        /// </summary>
        public static void TriggerOtaReboot(AdbHelper adb, UpgradeSession session)
        {
            Log.Step("OTA 安装已完成，PC 侧执行 adb reboot 以激活 A/B 槽位切换 ...");
            Log.Step("说明: reboot 后 PC 侧仅被动监听 adb/串口，不 kill-server、不提前切 dev");
            adb.Run(new[] { "reboot" }, true);
        }

        private static void WaitAdbDisconnect(AdbHelper adb, int timeoutSec, UpgradeSession session)
        {
            Log.Step("以 adb 断开作为车机开始 reboot 的标识，被动等待...");
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < deadline)
            {
                if (session != null) session.ThrowIfStopRequested();
                if (!adb.DeviceReady())
                {
                    Log.Step("检测到 adb 已断开");
                    return;
                }
                Thread.Sleep(2000);
            }
            Log.Step("未检测到 adb 断开（可能 reboot 极快或 adb 仍缓存连接），继续等待重连 ...");
        }

        private static void WaitAdbReconnectAfterReboot(
            AdbHelper adb,
            AppConfig cfg,
            int adbReconnectTimeoutSec,
            int serialResumeTimeoutSec,
            UpgradeSession session,
            string sessionDir,
            string updateEngineLogPath,
            bool detectRecovery)
        {
            if (detectRecovery)
            {
                SerialHelper.ResetCapture();
            }

            Log.Step(string.Format(
                "reboot 已开始，被动监听串口 {0} 直至通讯恢复，恢复后再等待 {1}s ...",
                cfg.SerialPort, PostRebootSerialWarmupSec));

            SerialHelper.WaitSerialResumeThenCapture(
                cfg, PostRebootSerialWarmupSec, serialResumeTimeoutSec, session);

            TrySerialDevMode(cfg, adb, false);

            Log.Step("等待 adb 在 dev 模式下重新连接...");
            var deadline = DateTime.Now.AddSeconds(adbReconnectTimeoutSec);
            int round = 0;
            while (DateTime.Now < deadline)
            {
                if (session != null) session.ThrowIfStopRequested();
                round++;

                if (detectRecovery)
                {
                    if (adb.IsRecoveryViaAdb())
                    {
                        CheckAndThrowRecovery(adb, cfg, sessionDir, updateEngineLogPath);
                    }
                }

                if (adb.DeviceReady() && adb.ShellUsable())
                {
                    Log.Step("adb 设备已连接且 shell 可用");
                    return;
                }

                if (round > 1 && round % (SerialDevRetryIntervalSec / 5) == 0)
                {
                    Log.Step("adb 仍未连接，再次经串口切换 dev 模式...");
                    TrySerialDevMode(cfg, adb, false);
                }
                else if (round == 1 || round % 5 == 0)
                {
                    Log.Step("等待 adb devices 出现 device 状态...");
                }

                Sleep(session, 5000);
            }

            throw new TimeoutException("重启后 adb 长时间未连接，请检查 USB/串口及 dev 模式");
        }

        private static void PollSerialRecovery(
            AdbHelper adb,
            AppConfig cfg,
            UpgradeSession session,
            string sessionDir,
            string updateEngineLogPath)
        {
            SerialHelper.AppendCaptureQuiet(cfg, RecoverySerialPollSec, session);
            var analysis = RecoveryBootAnalyzer.Analyze(
                SerialHelper.GetAccumulatedCapture(), adb, updateEngineLogPath);
            if (analysis.IsRebootLoop)
            {
                RecoveryBootAnalyzer.ThrowIfRecovery(analysis, sessionDir);
            }
        }

        private static bool IsRecoverySuspected(
            AdbHelper adb,
            string sessionDir,
            string updateEngineLogPath)
        {
            var analysis = RecoveryBootAnalyzer.Analyze(
                SerialHelper.GetAccumulatedCapture(), adb, updateEngineLogPath);
            return analysis.IsRecovery;
        }

        private static void CheckAndThrowRecovery(
            AdbHelper adb,
            AppConfig cfg,
            string sessionDir,
            string updateEngineLogPath)
        {
            var analysis = RecoveryBootAnalyzer.Analyze(
                SerialHelper.GetAccumulatedCapture(), adb, updateEngineLogPath);
            if (analysis.IsRecovery)
            {
                RecoveryBootAnalyzer.ThrowIfRecovery(analysis, sessionDir);
            }
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

        private static void TrySerialDevMode(AppConfig cfg, AdbHelper adb, bool postOtaRecoveryWatch)
        {
            if (postOtaRecoveryWatch && IsRecoverySuspected(adb, null, null))
            {
                Log.Step("已检测到 Recovery/重启循环，跳过串口切 dev（Recovery 下无效且会长时间阻塞）");
                return;
            }

            try
            {
                int waitSec = postOtaRecoveryWatch ? PostOtaAdbWaitSec : 120;
                SerialHelper.SwitchToDevMode(cfg, adb, waitSec, restartAdbServer: false);
            }
            catch (Exception ex)
            {
                Log.Step("串口切 dev 失败: " + ex.Message);
            }
        }

        private static bool IsAndroidBooted(AdbHelper adb, out string detail)
        {
            detail = "adb 未就绪";
            if (!adb.DeviceReady())
            {
                return false;
            }

            if (!adb.ShellUsable())
            {
                detail = "adb shell 不可用";
                return false;
            }

            string bootCompleted = adb.GetProp("sys.boot_completed");
            if (bootCompleted == "1")
            {
                detail = "sys.boot_completed=1";
                return true;
            }

            string devBoot = adb.GetProp("dev.bootcomplete");
            if (devBoot == "1")
            {
                detail = "dev.bootcomplete=1";
                return true;
            }

            string bootAnim = adb.GetProp("init.svc.bootanim");
            if (bootAnim == "stopped")
            {
                string release = adb.GetProp("ro.build.version.release");
                if (!string.IsNullOrEmpty(release))
                {
                    detail = "bootanim=stopped, release=" + release;
                    return true;
                }
            }

            string slot = adb.GetProp("ro.boot.slot_suffix");
            detail = string.Format("boot_completed={0}, dev.bootcomplete={1}, bootanim={2}, slot={3}",
                string.IsNullOrEmpty(bootCompleted) ? "?" : bootCompleted,
                string.IsNullOrEmpty(devBoot) ? "?" : devBoot,
                string.IsNullOrEmpty(bootAnim) ? "?" : bootAnim,
                string.IsNullOrEmpty(slot) ? "?" : slot);
            return false;
        }
    }

    internal static class UpdateLogAnalyzer
    {
        private static readonly Regex[] FailPatterns =
        {
            new Regex(@"UPDATE_STATUS_REPORTING_ERROR"),
            new Regex(@"Update failed"),
            new Regex(@"Aborting processing due to failure"),
            new Regex(@"finished DownloadAction with code ErrorCode::k(?!Success)"),
            new Regex(@"finished .*Action with code ErrorCode::k(?!Success)"),
            new Regex(@"Couldn't open .*/payload\.bin"),
            new Regex(@"\[ERROR:.*EACCES"),
            new Regex(@"Permission denied"),
            new Regex(@"kDownloadTransferError"),
            new Regex(@"kSignature"),
            new Regex(@"payload_application_complete.*error_code=[1-9]")
        };

        private static readonly string[] SuccessPatterns =
        {
            "UPDATED_NEED_REBOOT",
            "Update successfully applied",
            "already applied, waiting for reboot",
            "applied, waiting for reboot",
            "payload_application_complete.*error_code=0",
            "onPayloadApplicationComplete.*0"
        };

        public static bool IsFailed(string logText)
        {
            if (string.IsNullOrWhiteSpace(logText)) return false;
            if (IsSucceeded(logText))
            {
                return false;
            }
            foreach (var fp in FailPatterns)
            {
                if (fp.IsMatch(logText))
                {
                    return true;
                }
            }
            return false;
        }

        public static void NotifyInstallComplete()
        {
            Log.Step("检测到安装完成信号，随后 PC 侧将执行 adb reboot 激活升级 ...");
        }

        public static bool IsSucceeded(string logText)
        {
            if (string.IsNullOrWhiteSpace(logText)) return false;
            foreach (var sp in SuccessPatterns)
            {
                if (Regex.IsMatch(logText, sp))
                {
                    return true;
                }
            }
            return false;
        }

        public static void AssertNotFailed(string logText, string logPath)
        {
            if (IsFailed(logText))
            {
                throw new InvalidOperationException("升级失败，详见日志: " + logPath);
            }
        }

        public static void AssertSucceeded(string logText, string logPath)
        {
            AssertNotFailed(logText, logPath);
            if (!IsSucceeded(logText))
            {
                throw new InvalidOperationException(
                    "未检测到升级成功信号（update_engine 可能未真正完成），详见: " + logPath);
            }
        }
    }
}
