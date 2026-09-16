using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SocOtaUpgrade
{
    internal static class FileLogHelper
    {
        public static string ReadTail(string path, int lines)
        {
            if (!File.Exists(path)) return "";
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        var queue = new Queue<string>();
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            queue.Enqueue(line);
                            if (queue.Count > lines) queue.Dequeue();
                        }
                        return string.Join("\n", queue.ToArray());
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
            return "";
        }

        public static string ReadAll(string path)
        {
            if (!File.Exists(path)) return "";
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
            return "";
        }
    }
}
