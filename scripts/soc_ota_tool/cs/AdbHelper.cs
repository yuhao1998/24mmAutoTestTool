using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SocOtaUpgrade
{
    internal sealed class AdbHelper
    {
        private readonly string _adbPath;
        private readonly string _toolsDir;

        public AdbHelper(string baseDir)
        {
            _toolsDir = Path.Combine(baseDir, "tools");
            _adbPath = Path.Combine(_toolsDir, "adb.exe");
            if (!File.Exists(_adbPath))
            {
                throw new InvalidOperationException(
                    "未找到 tools\\adb.exe，请确保 SocOtaUpgrade.exe 同目录下存在 tools 文件夹（含 adb.exe 及 DLL）");
            }
            AdbProcessTracker.SetAdbPath(_adbPath);
        }

        public string AdbPath { get { return _adbPath; } }

        public string ToolsDir { get { return _toolsDir; } }

        public string Run(string[] args, bool allowFailure)
        {
            return Run(args, allowFailure, _toolsDir);
        }

        public string Run(string[] args, bool allowFailure, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = _toolsDir;
            }
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = QuoteArgs(args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                AdbProcessTracker.Register(p);
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                AdbProcessTracker.Unregister(p.Id);
                if (!allowFailure && p.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "adb " + string.Join(" ", args) + " failed (exit=" + p.ExitCode + "): " + output.Trim());
                }
                return output.Trim();
            }
        }

        public BackgroundProcess StartBackground(string[] args, string stdoutFile, string stderrFile)
        {
            var stdoutStream = new FileStream(stdoutFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var stderrStream = new FileStream(stderrFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var stdout = new StreamWriter(stdoutStream, Encoding.UTF8) { AutoFlush = true };
            var stderr = new StreamWriter(stderrStream, Encoding.UTF8) { AutoFlush = true };

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = QuoteArgs(args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _toolsDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.WriteLine(e.Data); };
            p.Exited += (_, __) =>
            {
                AdbProcessTracker.Unregister(p.Id);
                try { stdout.Dispose(); } catch { }
                try { stderr.Dispose(); } catch { }
            };
            p.Start();
            AdbProcessTracker.Register(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return new BackgroundProcess(p);
        }

        public bool DeviceReady()
        {
            return string.Equals(GetPrimaryDeviceState(), "device", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 返回首个已连接设备的 adb 状态：device / recovery / sideload / unauthorized / offline 等。
        /// </summary>
        public string GetPrimaryDeviceState()
        {
            string outText = Run(new[] { "devices" }, true);
            foreach (var line in outText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[0]))
                {
                    return parts[1];
                }
            }
            return string.Empty;
        }

        public bool IsRecoveryViaAdb()
        {
            string state = GetPrimaryDeviceState();
            if (state.Equals("recovery", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("sideload", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] props = { "ro.bootmode", "ro.boot.mode", "androidboot.mode" };
            foreach (var prop in props)
            {
                string val = GetProp(prop);
                if (!string.IsNullOrEmpty(val) &&
                    val.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public bool ShellUsable()
        {
            if (!DeviceReady()) return false;
            string outText = Run(new[] { "shell", "echo", "OK" }, true);
            return outText.Contains("OK");
        }

        public string GetProp(string name)
        {
            return Run(new[] { "shell", "getprop", name }, true).Trim();
        }

        public void RestartServer()
        {
            Log.Step("重置 adb server（kill-server / start-server）...");
            Run(new[] { "kill-server" }, true);
            Thread.Sleep(800);
            Run(new[] { "start-server" }, true);
            Thread.Sleep(500);
        }

        private static string QuoteArgs(string[] args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append('"').Append(args[i].Replace("\"", "\\\"")).Append('"');
            }
            return sb.ToString();
        }
    }

    internal sealed class BackgroundProcess
    {
        private readonly Process _process;

        public BackgroundProcess(Process process)
        {
            _process = process;
        }

        public bool HasExited { get { return _process.HasExited; } }

        public int ExitCode { get { return _process.ExitCode; } }

        public bool WaitForExit(int milliseconds)
        {
            return _process.WaitForExit(milliseconds);
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    AdbProcessTracker.KillProcess(_process.Id);
                }
            }
            catch { }
        }
    }
}
