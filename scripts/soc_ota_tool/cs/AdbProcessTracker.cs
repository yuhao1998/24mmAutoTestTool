using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SocOtaUpgrade
{
    internal static class AdbProcessTracker
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<int> TrackedPids = new List<int>();
        private static string _adbPath;

        public static void SetAdbPath(string adbPath)
        {
            _adbPath = adbPath;
        }

        public static void Register(Process process)
        {
            if (process == null) return;
            lock (SyncRoot)
            {
                if (!TrackedPids.Contains(process.Id))
                {
                    TrackedPids.Add(process.Id);
                }
            }
        }

        public static void Unregister(int pid)
        {
            lock (SyncRoot)
            {
                TrackedPids.Remove(pid);
            }
        }

        public static void KillProcess(int pid)
        {
            KillProcessTree(pid);
            Unregister(pid);
        }

        public static void CleanupAll(bool killAdbServer)
        {
            int[] pids;
            lock (SyncRoot)
            {
                pids = TrackedPids.ToArray();
                TrackedPids.Clear();
            }

            foreach (int pid in pids)
            {
                KillProcessTree(pid);
            }

            if (killAdbServer && !string.IsNullOrEmpty(_adbPath))
            {
                KillAdbServer();
            }
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                using (var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /T /PID " + pid,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(3000);
                    }
                }
            }
            catch
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited) proc.Kill();
                }
                catch
                {
                    // already exited
                }
            }
        }

        private static void KillAdbServer()
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = "kill-server",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_adbPath)
                }))
                {
                    if (p != null)
                    {
                        p.WaitForExit(5000);
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
