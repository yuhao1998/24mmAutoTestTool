using System;
using System.IO;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 结构化进度上报：在每个阶段写 progress.json，并在 session.log 输出 [STAGE] 标记行，
    /// 便于脚本（t2_upgrade_entry.py）轮询/解析当前阶段，决定下一步动作。
    /// </summary>
    internal static class ProgressReporter
    {
        private static string _progressPath;
        private static DateTime _startTime;
        private static readonly object _sync = new object();
        private static readonly System.Text.Encoding _utf8NoBom = new System.Text.UTF8Encoding(false);

        public static void Init(string sessionDir)
        {
            lock (_sync)
            {
                _progressPath = Path.Combine(sessionDir, "progress.json");
                _startTime = DateTime.Now;
            }
            Update("init", "running", "会话初始化");
        }

        /// <summary>更新当前阶段状态。stage 为阶段 ID，status 为 running/success/failed/cancelled。</summary>
        public static void Update(string stage, string status, string message)
        {
            if (string.IsNullOrEmpty(_progressPath)) return;
            try
            {
                string json;
                lock (_sync)
                {
                    var payload = new
                    {
                        stage = stage ?? "",
                        status = status ?? "",
                        message = message ?? "",
                        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        elapsed_sec = (int)(DateTime.Now - _startTime).TotalSeconds
                    };
                    json = new JavaScriptSerializer().Serialize(payload);
                    File.WriteAllText(_progressPath, json, _utf8NoBom);
                }
                // 同时在 session.log 留一行机器可解析的标记，便于脚本 tail 解析
                Log.Step(string.Format("[STAGE] stage={0} status={1} msg={2}", stage, status, message ?? ""));
            }
            catch
            {
                // 进度上报不得影响升级主流程
            }
        }
    }
}
