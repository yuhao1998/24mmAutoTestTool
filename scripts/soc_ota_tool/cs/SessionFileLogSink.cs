using System;
using System.IO;
using System.Text;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 将步骤/错误日志写入 session.log，并转发到 UI/控制台。
    /// </summary>
    internal sealed class SessionFileLogSink : ILogSink, IDisposable
    {
        private readonly ILogSink _inner;
        private readonly StreamWriter _writer;
        private readonly object _sync = new object();
        private readonly string _sessionLogPath;

        public SessionFileLogSink(string sessionLogPath, ILogSink inner)
        {
            _sessionLogPath = sessionLogPath;
            _inner = inner ?? new ConsoleLogSink();
            string dir = Path.GetDirectoryName(sessionLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var stream = new FileStream(
                sessionLogPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }

        public string SessionLogPath { get { return _sessionLogPath; } }

        public void Step(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            WriteLine(line);
            _inner.Step(message);
        }

        public void Error(string message)
        {
            string line = "[ERROR] " + message;
            WriteLine(line);
            _inner.Error(message);
        }

        private void WriteLine(string line)
        {
            lock (_sync)
            {
                _writer.WriteLine(line);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try
                {
                    _writer.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
