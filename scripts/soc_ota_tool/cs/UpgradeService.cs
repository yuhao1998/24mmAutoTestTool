using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    internal sealed class UpgradeService
    {
        private readonly string _baseDir;
        private readonly ILogSink _sink;
        private UpgradeSession _session;

        public UpgradeService(string baseDir, ILogSink sink)
        {
            _baseDir = baseDir;
            _sink = sink ?? new ConsoleLogSink();
        }

        public string LastSessionDir { get; private set; }

        public void RequestStop()
        {
            if (_session != null)
            {
                _session.RequestStop();
            }
        }

        public static bool StopActiveUpgrade()
        {
            return UpgradeSession.RequestStopActive();
        }

        public void Run(AppConfig cfg, string packagePath)
        {
            _session = UpgradeSession.Begin();
            SessionFileLogSink sessionLog = null;
            try
            {
                ExecuteUpgrade(cfg, packagePath, out sessionLog);
            }
            finally
            {
                if (sessionLog != null)
                {
                    sessionLog.Dispose();
                }
                _session.Finish();
                _session = null;
                Log.SetSink(null);
            }
        }

        private void ExecuteUpgrade(AppConfig cfg, string packagePath, out SessionFileLogSink sessionLog)
        {
            sessionLog = null;
            string logRoot = string.IsNullOrWhiteSpace(cfg.LogDir)
                ? Path.Combine(_baseDir, "logs")
                : cfg.LogDir;
            string sessionDir = Path.Combine(logRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string workDir = Path.Combine(sessionDir, "work");
            Directory.CreateDirectory(workDir);
            LastSessionDir = sessionDir;

            sessionLog = new SessionFileLogSink(Path.Combine(sessionDir, "session.log"), _sink);
            Log.SetSink(sessionLog);
            ProgressReporter.Init(sessionDir);

            string engineLog = Path.Combine(sessionDir, "update_engine.log");
            string clientLog = Path.Combine(sessionDir, "update_engine_client.log");

            var adb = new AdbHelper(_baseDir);
            BackgroundProcess logcatProc = null;
            BackgroundProcess updateProc = null;

            try
            {
                _session.ThrowIfStopRequested();
                Log.Step(AppBranding.SessionStart);
                Log.Step("配置: 串口=" + cfg.SerialPort + " 波特率=" + cfg.BaudRate);
                Log.Step("日志目录: " + sessionDir);
                Log.Step("会话日志文件: " + Path.Combine(sessionDir, "session.log"));
                Log.Step("adb 路径: " + adb.AdbPath);

                string resolved = ResolvePackagePath(packagePath);
                resolved = SevenZipPackageHelper.ResolveToUpgradeablePackage(resolved);
                ValidatePackage(resolved);
                Log.Step("升级包校验通过: " + resolved);
                ProgressReporter.Update("package_check", "success", "升级包校验通过: " + resolved);

                if (adb.DeviceReady())
                {
                    Log.Step("检测到 adb 已连接（dev 模式）");
                    ProgressReporter.Update("dev_mode", "success", "adb 已连接（dev 模式）");
                }
                else
                {
                    ProgressReporter.Update("dev_mode", "running", "经串口 su → start adbd → 切换 USB peripheral");
                    Log.Step("adb 未连接，经串口 su → start adbd → 切换 USB peripheral（dev）模式 ...");
                    SerialHelper.SwitchToDevMode(cfg, adb);
                    ProgressReporter.Update("dev_mode", "success", "已切换 dev 模式并连接 adb");
                }

                _session.ThrowIfStopRequested();
                PrepareOtaEnvironment(adb, cfg);
                AbSlotSnapshot preOtaSlot = AbSlotHelper.ReadCurrent(adb);
                AbSlotHelper.LogSlotSnapshot("升级前 A/B 槽位", preOtaSlot);
                File.WriteAllText(
                    Path.Combine(sessionDir, "pre_ota_slot.json"),
                    new JavaScriptSerializer().Serialize(new
                    {
                        SlotSuffix = preOtaSlot.SlotSuffix,
                        SlotLetter = preOtaSlot.SlotLetter,
                        IsAbDevice = preOtaSlot.IsAbDevice
                    }),
                    System.Text.Encoding.UTF8);
                ProgressReporter.Update("pre_slot", "success", "升级前槽位: " + preOtaSlot.SlotLetter);

                string localZip = PrepareLocalZip(resolved, workDir);
                OtaPackageExpectation packageExpectation = OtaPackageReader.Load(localZip, cfg);
                OtaPackageReader.SaveManifest(
                    packageExpectation, Path.Combine(sessionDir, "ota_package_manifest.json"));
                OtaPackageReader.LogExpectation(packageExpectation);
                ProgressReporter.Update("push", "running", "推送 update.zip 到 /data/ota 并解压");
                PushAndExtractManualLike(adb, cfg, localZip);
                ProgressReporter.Update("push", "success", "OTA 包已就绪于 " + cfg.RemoteOtaDir);

                _session.ThrowIfStopRequested();
                if (!adb.WaitDeviceReady(60))
                {
                    throw new InvalidOperationException("启动 logcat 前 adb 不可用，请确认 dev 模式已连接");
                }
                Log.Step("开始采集 update_engine 日志 -> " + engineLog);
                logcatProc = adb.StartBackground(
                    new[] { "logcat", "-v", "time", "-s", "update_engine:*", "UpdateEngine:*", "update_engine_client:*" },
                    engineLog, engineLog + ".err");
                _session.Sleep(2000);

                string updateShellCmd = OtaUpdateScript.BuildAdbShellUpdateCommand(cfg.RemoteOtaDir);
                Log.Step("启动 update_engine_client（adb shell 内执行，Linux 换行/字符）...");
                updateProc = adb.StartBackground(new[] { "shell", updateShellCmd }, clientLog, clientLog + ".err");
                _session.RegisterBackground(logcatProc, updateProc, adb);

                ProgressReporter.Update("update_engine", "running", "update_engine 应用 OTA 中");
                WaitUpdateComplete(updateProc, engineLog, cfg.UpdateTimeoutSec);
                ProgressReporter.Update("update_engine", "success", "OTA 安装完成（update_engine 已成功应用）");

                _session.ThrowIfStopRequested();
                Log.Step("=== OTA 安装完成（update_engine 已成功应用）===");
                ProgressReporter.Update("reboot", "running", "adb reboot 触发槽位切换");
                BootWaitHelper.TriggerOtaReboot(adb, _session);
                ProgressReporter.Update("boot_wait", "running", "等待 reboot 完成 + 串口恢复 + dev 切换");
                BootWaitHelper.WaitAfterReboot(
                    adb, cfg, cfg.BootTimeoutSec, _session, sessionDir, engineLog,
                    detectRecovery: true, preOtaSlotLetter: preOtaSlot.SlotLetter);
                ProgressReporter.Update("boot_wait", "success", "设备重启完成，adb 重新连接");

                ProgressReporter.Update("post_logs", "running", "采集重启后设备日志");
                DeviceLogCollector.CollectPostRebootLogs(adb, sessionDir);
                ProgressReporter.Update("post_logs", "success", "重启后日志采集完成");

                ProgressReporter.Update("version_verify", "running", "版本/A/B 槽位校验");
                OtaVersionVerifier.VerifyOrThrow(
                    adb, packageExpectation, cfg, sessionDir, engineLog, preOtaSlot.SlotLetter);
                ProgressReporter.Update("version_verify", "success", "版本校验通过");

                _session.ThrowIfStopRequested();
                AdbRootHelper.EnsureRoot(
                    adb, _session, "版本校验通过，执行 adb root 以进行 HAL 状态检查 ...");
                ProgressReporter.Update("hal_check", "running", "HAL 模块/系统状态检查");
                HalStatusChecker.VerifyOrThrow(adb, cfg, _baseDir, sessionDir);
                ProgressReporter.Update("hal_check", "success", "HAL 状态检查通过");

                if (cfg == null || !cfg.SkipHalStatusCheck)
                {
                    ProgressReporter.Update("script_pool", "running", "脚本池 push + 执行 + 收集");
                    // 与 t2_atf_config.script_pool.fail_task_on_script_fail 对齐：默认不因脚本失败中断升级成功态
                    bool failTaskOnScriptFail = ReadFailTaskOnScriptFail();
                    HalScriptPoolResult poolResult = HalScriptPoolRunner.Run(
                        adb, _baseDir, sessionDir, throwOnFail: failTaskOnScriptFail);
                    if (poolResult.Ran)
                    {
                        ProgressReporter.Update(
                            "script_pool",
                            poolResult.Passed ? "success" : "failed",
                            poolResult.Summary);
                    }
                    else
                    {
                        ProgressReporter.Update("script_pool", "success", poolResult.Summary);
                    }
                }

                try
                {
                    ProgressReporter.Update("detect_report", "running", "生成 detect_result.html");
                    string html = SessionDetectReportHelper.Generate(adb, _baseDir, sessionDir, packagePath);
                    ProgressReporter.Update(
                        "detect_report",
                        "success",
                        string.IsNullOrEmpty(html) ? "报告生成完成" : html);
                }
                catch (Exception rex)
                {
                    Log.Step("生成 detect_result.html 失败: " + rex.Message);
                    ProgressReporter.Update("detect_report", "failed", rex.Message);
                }

                Log.Step(AppBranding.SessionSuccess);
                ProgressReporter.Update("done", "success", "升级流程完成");
            }
            catch (OperationCanceledException)
            {
                ProgressReporter.Update("cancelled", "cancelled", "用户终止升级");
                Log.Step(AppBranding.SessionCancelled);
                throw;
            }
            catch (Exception ex)
            {
                ProgressReporter.Update("failed", "failed", ex.Message);
                throw;
            }
            finally
            {
                if (logcatProc != null) logcatProc.Kill();
                if (updateProc != null) updateProc.Kill();
                AdbProcessTracker.CleanupAll(true);
            }
        }

        private void CheckStop()
        {
            if (_session != null) _session.ThrowIfStopRequested();
        }

        private static string ResolvePackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("升级包路径不能为空");
            }
            return Path.GetFullPath(path.Trim().Trim('"'));
        }

        private static void ValidatePackage(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidOperationException("升级包不存在: " + path);
            }
            if (Directory.Exists(path))
            {
                if (!File.Exists(Path.Combine(path, "payload.bin")))
                    throw new InvalidOperationException("目录内缺少 payload.bin: " + path);
                if (!File.Exists(Path.Combine(path, "payload_properties.txt")))
                    throw new InvalidOperationException("目录内缺少 payload_properties.txt: " + path);
                return;
            }
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("升级包须为 .zip 或含 payload 的目录: " + path);
            }
            using (var zip = ZipFile.OpenRead(path))
            {
                bool hasPayload = zip.Entries.Any(e => Path.GetFileName(e.FullName) == "payload.bin");
                bool hasProps = zip.Entries.Any(e => Path.GetFileName(e.FullName) == "payload_properties.txt");
                if (!hasPayload) throw new InvalidOperationException("zip 内缺少 payload.bin: " + path);
                if (!hasProps) throw new InvalidOperationException("zip 内缺少 payload_properties.txt: " + path);
            }
        }

        /// <summary>
        /// 读 t2_atf_config.json → script_pool.fail_task_on_script_fail；缺省 false。
        /// </summary>
        private bool ReadFailTaskOnScriptFail()
        {
            try
            {
                string scriptsDir = Path.GetDirectoryName(_baseDir);
                if (string.IsNullOrEmpty(scriptsDir)) return false;
                string path = Path.Combine(scriptsDir, "t2_atf_config.json");
                if (!File.Exists(path)) return false;
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return false;
                object spObj;
                if (!root.TryGetValue("script_pool", out spObj)) return false;
                var sp = spObj as System.Collections.Generic.Dictionary<string, object>;
                if (sp == null) return false;
                object v;
                if (sp.TryGetValue("fail_task_on_script_fail", out v) && v is bool)
                {
                    return (bool)v;
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        private static string PrepareLocalZip(string path, string workDir)
        {
            string updateZip = Path.Combine(workDir, "update.zip");
            if (File.Exists(updateZip)) File.Delete(updateZip);
            if (Directory.Exists(path))
            {
                ZipFile.CreateFromDirectory(path, updateZip);
            }
            else
            {
                File.Copy(path, updateZip, true);
            }
            Log.Step("本地 update.zip 已准备: " + updateZip);
            return updateZip;
        }

        /// <summary>
        /// 与手动流程一致：adb root → push → setenforce 0 → unzip → update_engine_client → adb reboot。
        /// </summary>
        private static void PushAndExtractManualLike(AdbHelper adb, AppConfig cfg, string localZip)
        {
            string dir = cfg.RemoteOtaDir.TrimEnd('/');

            OtaPushHelper.PushLikeReference(adb, localZip, dir);

            Log.Step("关闭 SELinux 强制模式 (setenforce 0) ...");
            adb.Run(new[] { "shell", "setenforce 0" }, true);
            string enforce = adb.Run(new[] { "shell", "getenforce" }, true).Trim();
            Log.Step("当前 SELinux 状态: " + (string.IsNullOrEmpty(enforce) ? "未知" : enforce));

            Log.Step("解压 update.zip ...");
            adb.Run(new[] { "shell", string.Format(
                "cd {0} && (unzip -o update.zip || busybox unzip -o update.zip)", dir) }, true);

            string check = adb.Run(new[] { "shell",
                string.Format("ls {0}/payload.bin {0}/payload_properties.txt 2>/dev/null", dir) }, true);
            if (!check.Contains("payload.bin") || !check.Contains("payload_properties.txt"))
            {
                throw new InvalidOperationException(
                    "解压后未在 " + dir + " 找到 payload.bin / payload_properties.txt");
            }
            Log.Step("OTA 包已就绪于 " + dir);
        }

        private void PrepareOtaEnvironment(AdbHelper adb, AppConfig cfg)
        {
            AdbRootHelper.EnsureRoot(adb, _session);
        }

        private void WaitUpdateComplete(BackgroundProcess updateProc, string engineLog, int timeoutSec)
        {
            Log.Step(string.Format("监听升级进度（超时 {0}s）...", timeoutSec));
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            var start = DateTime.Now;
            bool clientEnded = false;
            int round = 0;

            while (DateTime.Now < deadline)
            {
                CheckStop();
                round++;
                string tail = FileLogHelper.ReadTail(engineLog, 200);
                if (!string.IsNullOrEmpty(tail))
                {
                    if (UpdateLogAnalyzer.IsFailed(tail))
                    {
                        UpdateLogAnalyzer.AssertNotFailed(tail, engineLog);
                    }
                    if (UpdateLogAnalyzer.IsSucceeded(tail))
                    {
                        UpdateLogAnalyzer.NotifyInstallComplete();
                        return;
                    }
                }

                if (!clientEnded && updateProc.WaitForExit(3000))
                {
                    CheckStop();
                    clientEnded = true;
                    if (updateProc.ExitCode != 0 && !UpdateLogAnalyzer.IsSucceeded(FileLogHelper.ReadAll(engineLog)))
                    {
                        throw new InvalidOperationException("update_engine_client 异常退出 (exit=" + updateProc.ExitCode + ")");
                    }
                    Log.Step("update_engine_client 已退出，继续监听 update_engine 日志直至完成或超时...");
                }

                if (round == 1 || round % 12 == 0)
                {
                    int elapsed = (int)(DateTime.Now - start).TotalSeconds;
                    Log.Step(string.Format("升级监听中（已等待 {0}s）...", elapsed));
                }

                _session.Sleep(2000);
            }

            string finalLog = FileLogHelper.ReadAll(engineLog);
            if (UpdateLogAnalyzer.IsSucceeded(finalLog))
            {
                UpdateLogAnalyzer.NotifyInstallComplete();
                return;
            }
            throw new TimeoutException("升级超时（" + timeoutSec + "s），详见: " + engineLog);
        }
    }

    internal static class AdbExtensions
    {
        public static bool WaitDevice(this AdbHelper adb, int timeoutSec)
        {
            return WaitDeviceReady(adb, timeoutSec);
        }

        public static bool WaitDeviceReady(this AdbHelper adb, int timeoutSec)
        {
            Log.Step(string.Format("等待 adb 设备就绪（最多 {0}s，需 device + shell 可用）...", timeoutSec));
            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            int round = 0;
            while (DateTime.Now < deadline)
            {
                var active = UpgradeSession.Active;
                if (active != null) active.ThrowIfStopRequested();
                round++;

                if (adb.DeviceReady())
                {
                    if (adb.ShellUsable())
                    {
                        Log.Step("adb 设备已连接且 shell 可用");
                        return true;
                    }
                    if (round == 1 || round % 5 == 0)
                    {
                        Log.Step("adb 已列出 device，但 shell 尚未可用，继续等待...");
                    }
                }
                else if (round == 1 || round % 5 == 0)
                {
                    Log.Step("等待 adb devices 出现 device 状态...");
                }

                Thread.Sleep(1000);
            }
            return false;
        }
    }
}
