using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 在 UI 内启动/停止 Python 邮箱监听，以及模拟邮件触发（仅拉取或完整升级）。
    /// </summary>
    internal sealed class EmailListenRunner : IDisposable
    {
        private readonly string _scriptsDir;
        private readonly Action<string> _log;
        private Process _proc;
        private Thread _stdoutThread;
        private Thread _stderrThread;
        private volatile bool _stopping;

        public bool IsRunning
        {
            get { return _proc != null && !_proc.HasExited; }
        }

        public EmailListenRunner(string socOtaBaseDir, Action<string> log)
        {
            _scriptsDir = Path.GetDirectoryName(socOtaBaseDir) ?? "";
            _log = log ?? (_ => { });
        }

        public string EntryScriptPath
        {
            get { return Path.Combine(_scriptsDir, "t2_upgrade_entry.py"); }
        }

        public string ConfigPath
        {
            get { return Path.Combine(_scriptsDir, "t2_atf_config.json"); }
        }

        public void StartListen()
        {
            if (IsRunning)
            {
                _log("[邮箱] 监听已在运行中");
                return;
            }
            if (!File.Exists(EntryScriptPath))
            {
                throw new FileNotFoundException("未找到 t2_upgrade_entry.py: " + EntryScriptPath);
            }
            if (!File.Exists(ConfigPath))
            {
                throw new FileNotFoundException("未找到 t2_atf_config.json: " + ConfigPath);
            }

            _stopping = false;
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = string.Format(
                    "-u \"{0}\" --listen-email --config \"{1}\"",
                    EntryScriptPath, ConfigPath),
                WorkingDirectory = _scriptsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Exited += (_, __) =>
            {
                try
                {
                    int code = _proc != null && _proc.HasExited ? _proc.ExitCode : -1;
                    _log("[邮箱] 监听进程已退出 (code=" + code + ")");
                }
                catch
                {
                    _log("[邮箱] 监听进程已退出");
                }
            };
            if (!_proc.Start())
            {
                throw new InvalidOperationException("无法启动 python 监听进程");
            }
            _log("[邮箱] 已启动监听 PID=" + _proc.Id);
            _log("[邮箱] 命令: python -u t2_upgrade_entry.py --listen-email");

            _stdoutThread = new Thread(() => DrainStream(_proc.StandardOutput)) { IsBackground = true };
            _stderrThread = new Thread(() => DrainStream(_proc.StandardError, "[stderr] ")) { IsBackground = true };
            _stdoutThread.Start();
            _stderrThread.Start();
        }

        public void StopListen()
        {
            if (_proc == null) return;
            _stopping = true;
            try
            {
                if (!_proc.HasExited)
                {
                    _log("[邮箱] 正在停止监听...");
                    _proc.Kill();
                    _proc.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _log("[邮箱] 停止监听异常: " + ex.Message);
            }
            finally
            {
                try { _proc.Dispose(); } catch { }
                _proc = null;
            }
        }

        /// <summary>
        /// 模拟邮件：拉取到 package_save_dir；fullUpgrade=true 时走完整升级流水线。
        /// </summary>
        public string RunSimulate(string source, bool fullUpgrade, out int exitCode)
        {
            exitCode = -1;
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("模拟源地址不能为空");
            }
            if (!File.Exists(EntryScriptPath))
            {
                throw new FileNotFoundException("未找到 t2_upgrade_entry.py: " + EntryScriptPath);
            }

            string escaped = source.Replace("\"", "\\\"");
            string modeArgs = fullUpgrade
                ? string.Format("--simulate-email --source \"{0}\"", escaped)
                : string.Format("--simulate-email --source \"{0}\" --fetch-only", escaped);
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = string.Format(
                    "-u \"{0}\" {1} --config \"{2}\"",
                    EntryScriptPath, modeArgs, ConfigPath),
                WorkingDirectory = _scriptsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _log("[邮箱] 模拟邮件" + (fullUpgrade ? "(完整升级)" : "(仅拉取)") + ": " + source);
            using (var p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("无法启动模拟进程");
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                string localPackage = "";
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        _log(line);
                        if (line.StartsWith("LOCAL_PACKAGE=", StringComparison.Ordinal))
                        {
                            localPackage = line.Substring("LOCAL_PACKAGE=".Length).Trim();
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    foreach (var line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        _log("[stderr] " + line);
                    }
                }
                return localPackage;
            }
        }

        /// <summary>兼容旧调用：仅拉取。</summary>
        public string RunSimulateFetch(string source, out int exitCode)
        {
            return RunSimulate(source, false, out exitCode);
        }

        private void DrainStream(StreamReader reader, string prefix = "")
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!_stopping) _log(prefix + line);
                }
            }
            catch
            {
                // ignore on stop
            }
        }

        public void Dispose()
        {
            StopListen();
        }
    }
}
