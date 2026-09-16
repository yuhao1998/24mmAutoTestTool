using System;
using System.Threading;

namespace SocOtaUpgrade
{
    internal sealed class UpgradeSession
    {
        private static readonly object SyncRoot = new object();
        private static UpgradeSession _active;

        private volatile bool _stopRequested;
        private BackgroundProcess _logcatProc;
        private BackgroundProcess _updateProc;
        private AdbHelper _adb;

        public static UpgradeSession Active
        {
            get { lock (SyncRoot) { return _active; } }
        }

        public static UpgradeSession Begin()
        {
            lock (SyncRoot)
            {
                if (_active != null)
                {
                    throw new InvalidOperationException("已有升级任务进行中，请先结束当前任务");
                }
                _active = new UpgradeSession();
                return _active;
            }
        }

        public static bool RequestStopActive()
        {
            UpgradeSession session;
            lock (SyncRoot)
            {
                session = _active;
            }
            if (session == null)
            {
                return false;
            }
            session.RequestStop();
            return true;
        }

        public void RegisterBackground(BackgroundProcess logcat, BackgroundProcess update, AdbHelper adb)
        {
            _logcatProc = logcat;
            _updateProc = update;
            _adb = adb;
        }

        public void RequestStop()
        {
            _stopRequested = true;
            Log.Step("收到结束升级请求，正在终止相关进程...");

            if (_logcatProc != null)
            {
                _logcatProc.Kill();
            }
            if (_updateProc != null)
            {
                _updateProc.Kill();
            }
            if (_adb != null)
            {
                try
                {
                    _adb.Run(new[] { "shell", "killall update_engine_client 2>/dev/null; killall update_engine 2>/dev/null; true" }, true);
                }
                catch
                {
                    // ignore when device offline
                }
            }
            AdbProcessTracker.CleanupAll(true);
        }

        public void ThrowIfStopRequested()
        {
            if (_stopRequested)
            {
                throw new OperationCanceledException("升级已被用户终止");
            }
        }

        public void Sleep(int milliseconds)
        {
            int elapsed = 0;
            while (elapsed < milliseconds)
            {
                ThrowIfStopRequested();
                int step = Math.Min(500, milliseconds - elapsed);
                Thread.Sleep(step);
                elapsed += step;
            }
        }

        public void Finish()
        {
            lock (SyncRoot)
            {
                if (_active == this)
                {
                    _active = null;
                }
            }
        }
    }
}
