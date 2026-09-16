using System;

namespace SocOtaUpgrade
{
    internal sealed class UpgradeApp
    {
        private readonly string _baseDir;

        public UpgradeApp(string baseDir)
        {
            _baseDir = baseDir;
        }

        public int Run(string[] args)
        {
            string package = "";
            string com = "";
            int baud = 0;
            bool showConfig = false;
            bool editConfig = false;
            bool stopUpgrade = false;
            bool halStatusOnly = false;
            bool serialReboot = false;
            bool switchDev = false;
            bool adbRoot = false;
            bool adbRemount = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-package":
                        if (i + 1 < args.Length) package = args[++i];
                        break;
                    case "-com":
                        if (i + 1 < args.Length) com = args[++i];
                        break;
                    case "-baud":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out baud);
                        break;
                    case "-show-config":
                        showConfig = true;
                        break;
                    case "-edit-config":
                        editConfig = true;
                        break;
                    case "-stop":
                    case "-stop-upgrade":
                        stopUpgrade = true;
                        break;
                    case "-hal-status":
                        halStatusOnly = true;
                        break;
                    case "-serial-reboot":
                        serialReboot = true;
                        break;
                    case "-switch-dev":
                        switchDev = true;
                        break;
                    case "-adb-root":
                        adbRoot = true;
                        break;
                    case "-adb-remount":
                        adbRemount = true;
                        break;
                    case "-h":
                    case "-help":
                        PrintHelp();
                        return 0;
                }
            }

            var overrides = new AppConfig();
            if (!string.IsNullOrWhiteSpace(com)) overrides.SerialPort = com;
            if (baud > 0) overrides.BaudRate = baud;
            var cfg = AppConfig.Load(_baseDir, overrides);

            if (showConfig)
            {
                PrintConfig(cfg);
                return 0;
            }
            if (editConfig)
            {
                InteractiveEditConfig(cfg);
                AppConfig.Save(_baseDir, cfg);
                Console.WriteLine("配置已保存: " + AppConfig.ConfigPath(_baseDir));
                return 0;
            }
            if (stopUpgrade)
            {
                if (UpgradeService.StopActiveUpgrade())
                {
                    Console.WriteLine("已发送结束升级请求。");
                    return 0;
                }
                Console.WriteLine("当前没有进行中的升级任务。");
                return 1;
            }
            if (halStatusOnly)
            {
                return RunHalStatusOnly(cfg);
            }
            if (serialReboot || switchDev || adbRoot || adbRemount)
            {
                return RunMaintenanceOnly(cfg, serialReboot, switchDev, adbRoot, adbRemount);
            }

            Console.WriteLine(AppBranding.ProductName);
            Console.WriteLine("配置文件: " + AppConfig.ConfigPath(_baseDir));
            Console.WriteLine("图形界面: 直接双击 SocOtaUpgrade.exe");
            Console.WriteLine();

            try
            {
                if (string.IsNullOrWhiteSpace(package))
                {
                    Console.Write("请输入 OTA 升级包路径（.zip 或目录）: ");
                    package = Console.ReadLine();
                }
                var service = new UpgradeService(_baseDir, new ConsoleLogSink());
                service.Run(cfg, package);
                return 0;
            }
            catch (OperationCanceledException ex)
            {
                Log.Error(ex.Message);
                return 2;
            }
            catch (RecoveryBootException ex)
            {
                Log.Error(ex.ReasonSummary);
                if (!string.IsNullOrEmpty(ex.FullReport))
                {
                    Console.WriteLine(ex.FullReport);
                }
                return 3;
            }
            catch (VersionVerifyException ex)
            {
                Log.Error(ex.Summary);
                if (!string.IsNullOrEmpty(ex.FullReport))
                {
                    Console.WriteLine(ex.FullReport);
                }
                return 4;
            }
            catch (HalStatusException ex)
            {
                Log.Error(ex.Summary);
                if (!string.IsNullOrEmpty(ex.FullReport))
                {
                    Console.WriteLine(ex.FullReport);
                }
                return 5;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                Console.WriteLine();
                Console.WriteLine("按 Enter 退出...");
                Console.ReadLine();
                return 1;
            }
        }

        private int RunHalStatusOnly(AppConfig cfg)
        {
            Log.SetSink(new ConsoleLogSink());
            Console.WriteLine(AppBranding.ProductName + " — 升级后检查（L0 + 已配置脚本池）");
            Console.WriteLine("配置文件: " + AppConfig.ConfigPath(_baseDir));
            Console.WriteLine();

            string logRoot = string.IsNullOrWhiteSpace(cfg.LogDir)
                ? System.IO.Path.Combine(_baseDir, "logs")
                : cfg.LogDir;
            string sessionDir = System.IO.Path.Combine(logRoot, "hal_status_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            System.IO.Directory.CreateDirectory(sessionDir);

            try
            {
                var adb = new AdbHelper(_baseDir);
                if (!adb.DeviceReady())
                {
                    throw new InvalidOperationException("adb 未连接或设备未就绪，请先切 dev 模式");
                }
                HalStatusChecker.VerifyOrThrow(adb, cfg, _baseDir, sessionDir);
                Console.WriteLine();
                Console.WriteLine("HAL 基本状态检查 Pass");
                Console.WriteLine("报告: " + System.IO.Path.Combine(sessionDir, "hal_status.json"));

                HalScriptPoolResult pool = HalScriptPoolRunner.Run(adb, _baseDir, sessionDir, throwOnFail: false);
                if (pool.Ran)
                {
                    Console.WriteLine("脚本池: " + pool.Summary);
                    if (!string.IsNullOrEmpty(pool.ResultFile))
                        Console.WriteLine("脚本池结果: " + pool.ResultFile);
                }
                else
                {
                    Console.WriteLine(pool.Summary);
                }

                string html = SessionDetectReportHelper.Generate(adb, _baseDir, sessionDir, "");
                if (!string.IsNullOrEmpty(html))
                {
                    Console.WriteLine("detect 报告: " + html);
                }

                if (pool.Ran && !pool.Passed)
                {
                    throw new HalScriptPoolException("脚本池执行未通过: " + pool.Summary, pool);
                }
                return 0;
            }
            catch (HalStatusException ex)
            {
                Log.Error(ex.Summary);
                if (!string.IsNullOrEmpty(ex.FullReport))
                {
                    Console.WriteLine(ex.FullReport);
                }
                Console.WriteLine("报告: " + System.IO.Path.Combine(sessionDir, "hal_status.json"));
                return 5;
            }
            catch (HalScriptPoolException ex)
            {
                Log.Error(ex.Message);
                Console.WriteLine(ex.Message);
                return 6;
            }
        }

        private static void PrintConfig(AppConfig cfg)
        {
            Console.WriteLine("配置文件: " + AppConfig.ConfigPath(System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)));
            Console.WriteLine("  serial_port        = " + cfg.SerialPort);
            Console.WriteLine("  baud_rate          = " + cfg.BaudRate);
            Console.WriteLine("  usb_mode_cmd       = " + cfg.UsbModeCmd);
            Console.WriteLine("  remote_ota_dir     = " + cfg.RemoteOtaDir);
            Console.WriteLine("  boot_timeout_sec   = " + cfg.BootTimeoutSec);
            Console.WriteLine("  update_timeout_sec = " + cfg.UpdateTimeoutSec);
            Console.WriteLine("  log_dir            = " + cfg.LogDir);
        }

        private static void InteractiveEditConfig(AppConfig cfg)
        {
            Console.WriteLine("=== 编辑配置（直接回车保留当前值）===");
            cfg.SerialPort = Prompt("串口号 COM", cfg.SerialPort);
            cfg.BaudRate = PromptInt("波特率", cfg.BaudRate);
            cfg.UsbModeCmd = Prompt("USB 切 dev 命令", cfg.UsbModeCmd);
            cfg.RemoteOtaDir = Prompt("车机 OTA 目录", cfg.RemoteOtaDir);
            cfg.BootTimeoutSec = PromptInt("启动超时(秒)", cfg.BootTimeoutSec);
            cfg.UpdateTimeoutSec = PromptInt("升级超时(秒)", cfg.UpdateTimeoutSec);
            cfg.LogDir = Prompt("本地日志目录(空=exe/logs)", cfg.LogDir ?? "");
        }

        private static string Prompt(string label, string current)
        {
            Console.Write(label + " [" + current + "]: ");
            string line = Console.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? current : line.Trim();
        }

        private static int PromptInt(string label, int current)
        {
            Console.Write(label + " [" + current + "]: ");
            string line = Console.ReadLine();
            int v;
            return int.TryParse(line, out v) ? v : current;
        }

        private int RunMaintenanceOnly(AppConfig cfg, bool serialReboot, bool switchDev, bool adbRoot, bool adbRemount)
        {
            Log.SetSink(new ConsoleLogSink());
            var adb = new AdbHelper(_baseDir);
            try
            {
                if (serialReboot)
                {
                    Console.WriteLine("=== 串口一键重启 ===");
                    DeviceMaintenanceHelper.RebootViaSerial(cfg);
                }
                if (switchDev)
                {
                    Console.WriteLine("=== 串口切 dev ===");
                    DeviceMaintenanceHelper.SwitchToDevMode(cfg, adb);
                }
                if (adbRoot)
                {
                    Console.WriteLine("=== adb root ===");
                    if (!adb.DeviceReady())
                    {
                        throw new InvalidOperationException("adb 未连接，请先执行 -switch-dev");
                    }
                    DeviceMaintenanceHelper.AdbRoot(adb);
                }
                if (adbRemount)
                {
                    Console.WriteLine("=== adb remount ===");
                    if (!adb.DeviceReady())
                    {
                        throw new InvalidOperationException("adb 未连接，请先执行 -switch-dev");
                    }
                    DeviceMaintenanceHelper.AdbRemount(adb);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("SocOtaUpgrade.exe [-package PATH] [-com COM4] [-baud 115200]");
            Console.WriteLine("                  [-gui] [-report] [-hal-status] [-serial-reboot] [-switch-dev]");
            Console.WriteLine("                  [-adb-root] [-adb-remount]");
            Console.WriteLine("                  [-show-config] [-edit-config] [-stop] [-help]");
            Console.WriteLine("无参数时启动图形界面。升级进行中可用 -stop 或界面「结束升级」终止。");
            Console.WriteLine("-gui            强制图形界面；与 -package 连用可由脚本触发并自动开始升级。");
            Console.WriteLine("                成功后自动关闭并返回退出码 0；失败时弹窗留观，关闭后返回对应退出码。");
            Console.WriteLine("-report         监听触发模式：升级+验证完成后弹窗说明再退出（不直接关），配合 t2_upgrade_entry.py 上报 ATF。");
            Console.WriteLine("-hal-status     仅执行 HAL 基本状态检查（需 adb 已连接）。");
            Console.WriteLine("-serial-reboot  经串口 su 后执行 sync; reboot。");
            Console.WriteLine("-switch-dev     经串口 su → start adbd → 切换 USB peripheral（dev）并连接 adb。");
            Console.WriteLine("-adb-root        执行 adb root。");
            Console.WriteLine("-adb-remount     执行 adb root + adb remount。");
        }
    }
}
