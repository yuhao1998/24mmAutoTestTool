using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 将 .7z/.rar 周包解压并定位到 SocOtaUpgrade 可用的 update.zip / payload 目录。
    /// </summary>
    internal static class SevenZipPackageHelper
    {
        public static string ResolveToUpgradeablePackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("升级包路径不能为空");
            }
            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidOperationException("升级包不存在: " + path);
            }
            if (Directory.Exists(path))
            {
                if (IsPayloadDir(path)) return path;
                string found = FindOtaUnder(path);
                if (!string.IsNullOrEmpty(found)) return found;
                throw new InvalidOperationException("目录内未找到可用 OTA 包: " + path);
            }
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            if (!path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("升级包须为 .zip/.7z 或含 payload 的目录: " + path);
            }

            string seven = Find7zExe();
            if (string.IsNullOrEmpty(seven))
            {
                throw new InvalidOperationException(
                    "检测到 .7z 升级包，但未找到 7z.exe。请安装 7-Zip（默认 C:\\Program Files\\7-Zip\\7z.exe）。");
            }

            string extractRoot = path + "_extracted";
            if (Directory.Exists(extractRoot))
            {
                string reused = FindOtaUnder(extractRoot);
                if (!string.IsNullOrEmpty(reused))
                {
                    Log.Step("复用已解压 OTA 包: " + reused);
                    return reused;
                }
            }
            else
            {
                Directory.CreateDirectory(extractRoot);
            }

            Log.Step("解压升级包(.7z): " + path);
            Log.Step("解压目录: " + extractRoot);
            Run7z(seven, new[]
            {
                "x", path, "-o" + extractRoot, "-y",
                "-ir!update.zip", "-ir!ota.zip", "-ir!payload.bin", "-ir!payload_properties.txt"
            });
            string found2 = FindOtaUnder(extractRoot);
            if (string.IsNullOrEmpty(found2))
            {
                Log.Step("选择性解压未找到 OTA 文件，改为全量解压...");
                Run7z(seven, new[] { "x", path, "-o" + extractRoot, "-y" });
                found2 = FindOtaUnder(extractRoot);
            }
            if (string.IsNullOrEmpty(found2))
            {
                throw new InvalidOperationException("解压后未找到可用 OTA 包: " + extractRoot);
            }
            Log.Step("已解析出 SOC 升级包: " + found2);
            return found2;
        }

        private static void Run7z(string seven, string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = seven,
                Arguments = string.Join(" ", args.Select(QuoteArg)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("无法启动 7z.exe");
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && p.ExitCode != 1)
                {
                    // 7z: 0=ok, 1=warning；选择性解压找不到文件时也可能非 0，由调用方再判断
                    string msg = (stderr ?? "") + (stdout ?? "");
                    if (msg.Length > 400) msg = msg.Substring(0, 400);
                    Log.Step("7z 退出码=" + p.ExitCode + " " + msg);
                }
            }
        }

        private static string QuoteArg(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            if (a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        private static string Find7zExe()
        {
            string[] candidates =
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return "";
        }

        private static bool IsPayloadDir(string path)
        {
            return Directory.Exists(path)
                && File.Exists(Path.Combine(path, "payload.bin"))
                && File.Exists(Path.Combine(path, "payload_properties.txt"));
        }

        private static bool ZipLooksLikeOta(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    bool hasPayload = zip.Entries.Any(e =>
                        string.Equals(Path.GetFileName(e.FullName), "payload.bin", StringComparison.OrdinalIgnoreCase));
                    bool hasProps = zip.Entries.Any(e =>
                        string.Equals(Path.GetFileName(e.FullName), "payload_properties.txt", StringComparison.OrdinalIgnoreCase));
                    return hasPayload && hasProps;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FindOtaUnder(string root)
        {
            if (!Directory.Exists(root)) return "";
            if (IsPayloadDir(root)) return Path.GetFullPath(root);

            string[] preferred = { "update.zip", "ota.zip", "update_usb.zip" };
            foreach (var name in preferred)
            {
                foreach (var file in Directory.GetFiles(root, name, SearchOption.AllDirectories))
                {
                    if (ZipLooksLikeOta(file) || string.Equals(name, "update.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetFullPath(file);
                    }
                }
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (IsPayloadDir(dir)) return Path.GetFullPath(dir);
            }

            var zips = Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();
            foreach (var z in zips)
            {
                if (ZipLooksLikeOta(z)) return Path.GetFullPath(z);
            }
            if (zips.Count > 0)
            {
                Log.Step("未校验到 payload.bin，回退使用: " + zips[0]);
                return Path.GetFullPath(zips[0]);
            }
            return "";
        }
    }
}
