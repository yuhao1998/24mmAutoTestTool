using System;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace SocOtaUpgrade
{
    internal static class SerialHelper
    {
        public static string LastSerialCapture { get; private set; }

        public static void SwitchToDevMode(AppConfig cfg, AdbHelper adb)
        {
            SwitchToDevMode(cfg, adb, 120, true);
        }

        public static void SwitchToDevMode(AppConfig cfg, AdbHelper adb, int adbWaitSec)
        {
            SwitchToDevMode(cfg, adb, adbWaitSec, true);
        }

        /// <param name="restartAdbServer">OTA reboot 后切 dev 时应为 false，避免 kill-server 干扰启动。</param>
        public static void SwitchToDevMode(AppConfig cfg, AdbHelper adb, int adbWaitSec, bool restartAdbServer)
        {
            // 串口切 adb：su → 校验 root → start adbd → 写 USB mode → 读回校验
            // 等待刻意缩短；是否成功以 sysfs mode / adb device 为准，不以固定 sleep 代替。
            Log.Step("串口切 adb：su → start adbd → 切换 USB peripheral（dev）...");
            using (var port = OpenPort(cfg))
            {
                WakePort(port);
                EnsureRootShell(port);

                string modeBefore = QueryUsbMode(port);
                Log.Step("切前 USB mode=" + (string.IsNullOrEmpty(modeBefore) ? "未知" : modeBefore));

                Log.Step("串口发送命令: start adbd");
                SendLine(port, "start adbd");
                Thread.Sleep(800);
                DrainPort(port, "start adbd");

                string adbdState = QueryProp(port, "init.svc.adbd");
                Log.Step("init.svc.adbd=" + (string.IsNullOrEmpty(adbdState) ? "未知" : adbdState.Trim()));

                string usbCmd = string.IsNullOrWhiteSpace(cfg.UsbModeCmd)
                    ? "echo peripheral > /sys/bus/platform/devices/a600000.ssusb/mode"
                    : cfg.UsbModeCmd.Trim();
                bool switched = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    Log.Step(string.Format("串口发送 USB 切模式命令 (尝试 {0}/3): {1}", attempt, usbCmd));
                    SendLine(port, usbCmd);
                    Thread.Sleep(1000);
                    DrainPort(port, "usb_mode_cmd");

                    string modeAfter = QueryUsbMode(port);
                    Log.Step("切后 USB mode=" + (string.IsNullOrEmpty(modeAfter) ? "未知" : modeAfter));
                    if (!string.IsNullOrEmpty(modeAfter) &&
                        modeAfter.IndexOf("peripheral", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        switched = true;
                        break;
                    }
                    Log.Step("USB mode 仍非 peripheral，重试...");
                    Thread.Sleep(500);
                }

                if (!switched)
                {
                    throw new InvalidOperationException(
                        "串口已发送切模式命令，但 /sys/.../ssusb/mode 未变为 peripheral。" +
                        "请确认：① su 已拿到 root；② USB 数据线已连接 PC；③ sysfs 路径与车机一致。");
                }
            }

            if (restartAdbServer)
            {
                adb.RestartServer();
            }
            else
            {
                Log.Step("串口切 dev 完成，校验 adb 状态（不重置 adb server）...");
            }
            if (!adb.WaitDeviceReady(adbWaitSec))
            {
                throw new InvalidOperationException(
                    "USB mode 已是 peripheral，但 adb 仍不可用。请检查 USB 数据线/口、驱动，以及 getprop sys.usb.config 是否含 adb。");
            }
        }

        /// <summary>
        /// 静默追加串口输出（用于 Recovery 循环监测，不刷屏）。
        /// </summary>
        public static void AppendCaptureQuiet(AppConfig cfg, int durationSec, UpgradeSession session)
        {
            var sb = new StringBuilder();
            try
            {
                using (var port = OpenPort(cfg))
                {
                    var deadline = DateTime.Now.AddSeconds(durationSec);
                    while (DateTime.Now < deadline)
                    {
                        if (session != null) session.ThrowIfStopRequested();
                        try
                        {
                            if (port.BytesToRead > 0)
                            {
                                sb.Append(port.ReadExisting());
                            }
                        }
                        catch (TimeoutException)
                        {
                            // ignore
                        }
                        Thread.Sleep(200);
                    }
                    if (port.BytesToRead > 0)
                    {
                        sb.Append(port.ReadExisting());
                    }
                }
            }
            catch
            {
                return;
            }

            if (sb.Length > 0)
            {
                AppendCapture(sb.ToString());
            }
        }

        /// <summary>
        /// reboot 后被动监听：先等到串口有数据（通讯恢复），再固定等待 warmupSec 并持续采集。
        /// 期间不向车机发送任何命令。
        /// </summary>
        public static string WaitSerialResumeThenCapture(
            AppConfig cfg,
            int warmupSec,
            int resumeTimeoutSec,
            UpgradeSession session,
            Action<string> onCaptureChunk = null)
        {
            Log.Step(string.Format(
                "被动监听串口 {0}，等待通讯恢复（最多 {1}s）...",
                cfg.SerialPort, resumeTimeoutSec));
            var sb = new StringBuilder();
            DateTime? resumeAt = null;
            try
            {
                using (var port = OpenPort(cfg))
                {
                    var resumeDeadline = DateTime.Now.AddSeconds(resumeTimeoutSec);
                    while (!resumeAt.HasValue && DateTime.Now < resumeDeadline)
                    {
                        if (session != null) session.ThrowIfStopRequested();
                        try
                        {
                            if (port.BytesToRead > 0)
                            {
                                string chunk = port.ReadExisting();
                                sb.Append(chunk);
                                AppendCapture(chunk);
                                if (onCaptureChunk != null) onCaptureChunk(chunk);
                                resumeAt = DateTime.Now;
                                Log.Step(string.Format("串口 {0} 通讯已恢复", cfg.SerialPort));
                                Log.Step(string.Format("通讯恢复后等待 {0}s 再执行后续操作...", warmupSec));
                            }
                        }
                        catch (TimeoutException)
                        {
                            // ignore
                        }
                        Thread.Sleep(200);
                    }

                    if (!resumeAt.HasValue)
                    {
                        Log.Step(string.Format(
                            "未在 {0}s 内检测到串口数据，仍等待 {1}s 后继续...",
                            resumeTimeoutSec, warmupSec));
                        resumeAt = DateTime.Now;
                    }

                    var warmupEnd = resumeAt.Value.AddSeconds(warmupSec);
                    while (DateTime.Now < warmupEnd)
                    {
                        if (session != null) session.ThrowIfStopRequested();
                        try
                        {
                            if (port.BytesToRead > 0)
                            {
                                string chunk = port.ReadExisting();
                                sb.Append(chunk);
                                AppendCapture(chunk);
                                if (onCaptureChunk != null) onCaptureChunk(chunk);
                            }
                        }
                        catch (TimeoutException)
                        {
                            // ignore
                        }
                        Thread.Sleep(200);
                    }
                    if (port.BytesToRead > 0)
                    {
                        string chunk = port.ReadExisting();
                        sb.Append(chunk);
                        AppendCapture(chunk);
                        if (onCaptureChunk != null) onCaptureChunk(chunk);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Step("串口被动监听失败（可忽略）: " + ex.Message);
            }

            string text = sb.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                LogSerialSnippet(text, "串口监听");
            }
            return text;
        }

        /// <summary>
        /// 重启后被动监听串口输出（不切 dev），用于捕获 bootloader/Recovery 启动日志。
        /// </summary>
        public static string CaptureOutput(AppConfig cfg, int durationSec, UpgradeSession session)
        {
            Log.Step(string.Format("被动监听串口 {0}（{1}s），检测 Recovery 启动信息 ...", cfg.SerialPort, durationSec));
            var sb = new StringBuilder();
            try
            {
                using (var port = OpenPort(cfg))
                {
                    var deadline = DateTime.Now.AddSeconds(durationSec);
                    while (DateTime.Now < deadline)
                    {
                        if (session != null) session.ThrowIfStopRequested();
                        try
                        {
                            if (port.BytesToRead > 0)
                            {
                                sb.Append(port.ReadExisting());
                            }
                        }
                        catch (TimeoutException)
                        {
                            // ignore
                        }
                        Thread.Sleep(200);
                    }
                    if (port.BytesToRead > 0)
                    {
                        sb.Append(port.ReadExisting());
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Step("串口被动监听失败（可忽略）: " + ex.Message);
            }

            string text = sb.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppendCapture(text);
                LogSerialSnippet(text, "串口监听");
            }
            return text;
        }

        public static string EnterRootAndSendCommand(AppConfig cfg, string command, int waitAfterSec)
        {
            return EnterRootAndSendCommands(cfg, new[] { command }, waitAfterSec);
        }

        /// <summary>
        /// 串口：先 su 进 root，再依次发送多条命令。
        /// waitAfterEachSec 为每条命令默认等待秒数；若提供 waitAfterSecs 则按索引覆盖。
        /// </summary>
        public static string EnterRootAndSendCommands(
            AppConfig cfg,
            string[] commands,
            int waitAfterEachSec,
            params int[] waitAfterSecs)
        {
            if (commands == null || commands.Length == 0)
            {
                throw new ArgumentException("commands 不能为空", "commands");
            }

            Log.Step(string.Format("打开串口 {0}@{1}，先 su 进入 root ...", cfg.SerialPort, cfg.BaudRate));
            var sb = new StringBuilder();
            using (var port = OpenPort(cfg))
            {
                WakePort(port);
                EnsureRootShell(port);
                sb.Append(LastSerialCapture ?? string.Empty);

                for (int i = 0; i < commands.Length; i++)
                {
                    string command = commands[i];
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        continue;
                    }
                    int waitSec = (waitAfterSecs != null && i < waitAfterSecs.Length)
                        ? waitAfterSecs[i]
                        : waitAfterEachSec;
                    // 默认等待缩短：未单独指定时最多 2s，避免「命令已生效仍傻等」
                    if (waitAfterSecs == null || i >= waitAfterSecs.Length)
                    {
                        if (waitSec > 2) waitSec = 2;
                    }
                    if (waitSec < 1) waitSec = 1;

                    Log.Step("串口发送命令: " + command);
                    SendLine(port, command);
                    Thread.Sleep(waitSec * 1000);
                    sb.Append(ReadResponse(port, "命令响应"));
                }
            }
            string text = sb.ToString();
            AppendCapture(text);
            return text;
        }

        public static void SendCommand(AppConfig cfg, string command, int waitAfterSec)
        {
            EnterRootAndSendCommand(cfg, command, waitAfterSec);
        }

        /// <summary>
        /// 经串口 su 后执行 reboot（适用于 adb 不可用时的车机一键重启）。
        /// </summary>
        public static void RebootViaSerial(AppConfig cfg)
        {
            Log.Step(string.Format("经串口 {0} 执行车机重启（su + sync; reboot）...", cfg.SerialPort));
            EnterRootAndSendCommand(cfg, "sync; reboot", 3);
            Log.Step("重启命令已发送，车机将重新启动（adb/串口会短暂断开）");
        }

        public static string GetAccumulatedCapture()
        {
            return LastSerialCapture ?? string.Empty;
        }

        public static void ResetCapture()
        {
            LastSerialCapture = string.Empty;
        }

        private static void AppendCapture(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (string.IsNullOrEmpty(LastSerialCapture))
            {
                LastSerialCapture = text;
            }
            else
            {
                LastSerialCapture += text;
            }
        }

        private static void LogSerialSnippet(string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string snippet = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (snippet.Length > 300)
            {
                snippet = snippet.Substring(0, 300) + "...";
            }
            Log.Step(label + " 摘要: " + snippet);
        }

        private static SerialPort OpenPort(AppConfig cfg)
        {
            var port = new SerialPort(cfg.SerialPort, cfg.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 3000,
                WriteTimeout = 3000,
                // 车机 console 对 UTF-8 多字节偶发乱码；命令用 ASCII 字节发送更稳
                Encoding = Encoding.ASCII,
                NewLine = "\n",
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            return port;
        }

        private static void WakePort(SerialPort port)
        {
            Thread.Sleep(200);
            SendLine(port, "");
            Thread.Sleep(300);
            DrainPort(port, null);
        }

        /// <summary>
        /// 显式写 ASCII + LF。避免 WriteLine 在部分运行时附带 CR，导致 shell 回显/执行异常。
        /// </summary>
        private static void SendLine(SerialPort port, string line)
        {
            if (line == null) line = string.Empty;
            byte[] payload = Encoding.ASCII.GetBytes(line + "\n");
            port.Write(payload, 0, payload.Length);
        }

        private static void EnsureRootShell(SerialPort port)
        {
            Log.Step("串口发送命令: su");
            SendLine(port, "su");
            Thread.Sleep(1000);
            DrainPort(port, "su");

            // 最多再试一次 su，并用 id 确认 uid=0
            for (int i = 0; i < 2; i++)
            {
                SendLine(port, "id");
                Thread.Sleep(600);
                string idOut = DrainPort(port, "id");
                if (idOut.IndexOf("uid=0(", StringComparison.Ordinal) >= 0 ||
                    idOut.IndexOf("uid=0 ", StringComparison.Ordinal) >= 0)
                {
                    Log.Step("串口已进入 root（uid=0）");
                    return;
                }
                Log.Step("尚未确认 root，再次 su ...");
                SendLine(port, "su");
                Thread.Sleep(1000);
                DrainPort(port, "su");
            }
            throw new InvalidOperationException(
                "串口 su 后仍非 root（id 未显示 uid=0）。无 root 时无法写 USB mode，切 dev 会失败。");
        }

        private static string QueryUsbMode(SerialPort port)
        {
            // 短命令，避免长行在 console 上回卷干扰解析
            SendLine(port, "cat /sys/bus/platform/devices/a600000.ssusb/mode");
            Thread.Sleep(700);
            string raw = DrainPort(port, null);
            return ExtractUsbMode(raw);
        }

        private static string QueryProp(SerialPort port, string prop)
        {
            SendLine(port, "getprop " + prop);
            Thread.Sleep(600);
            string raw = DrainPort(port, null);
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            // 取非空、非提示行的最后一行近似值
            string[] lines = raw.Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("getprop", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Contains("console:/") || t.EndsWith("#") || t.EndsWith("$")) continue;
                if (t.StartsWith("[")) continue;
                return t;
            }
            return raw.Trim();
        }

        private static string ExtractUsbMode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string[] lines = raw.Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Equals("peripheral", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("otg", StringComparison.OrdinalIgnoreCase))
                {
                    return t.ToLowerInvariant();
                }
            }
            // 回显里夹带关键字时兜底
            if (raw.IndexOf("peripheral", StringComparison.OrdinalIgnoreCase) >= 0) return "peripheral";
            if (raw.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0) return "host";
            return string.Empty;
        }

        private static string DrainPort(SerialPort port, string label)
        {
            var sb = new StringBuilder();
            try
            {
                Thread.Sleep(150);
                DateTime deadline = DateTime.Now.AddMilliseconds(400);
                while (DateTime.Now < deadline)
                {
                    if (port.BytesToRead > 0)
                    {
                        sb.Append(port.ReadExisting());
                        deadline = DateTime.Now.AddMilliseconds(200);
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }
                }
            }
            catch (TimeoutException)
            {
                // ignore
            }

            string resp = sb.ToString();
            if (!string.IsNullOrEmpty(resp))
            {
                AppendCapture(resp);
                if (!string.IsNullOrEmpty(label))
                {
                    string display = resp.Trim().Replace('\r', ' ').Replace('\n', ' ');
                    if (display.Length > 800)
                    {
                        display = display.Substring(0, 800) + "...";
                    }
                    Log.Step(label + ": " + display);
                }
            }
            return resp;
        }

        private static string ReadResponse(SerialPort port, string label)
        {
            return DrainPort(port, label);
        }
    }
}
