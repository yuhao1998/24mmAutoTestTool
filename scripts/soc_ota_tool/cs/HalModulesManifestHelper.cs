using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    internal static class HalModulesManifestHelper
    {
        private const string DefaultManifestName = "hal_modules.json";

        public static string ResolveManifestPath(string baseDir, AppConfig cfg)
        {
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.HalModulesManifest))
            {
                string custom = cfg.HalModulesManifest.Trim();
                if (Path.IsPathRooted(custom))
                {
                    return custom;
                }
                return Path.Combine(baseDir, custom);
            }
            return Path.Combine(baseDir, DefaultManifestName);
        }

        public static HalModulesManifest Load(string baseDir, AppConfig cfg)
        {
            string path = ResolveManifestPath(baseDir, cfg);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("未找到 HAL 模块清单: " + path);
            }

            var ser = new JavaScriptSerializer();
            var manifest = ser.Deserialize<HalModulesManifest>(File.ReadAllText(path, Encoding.UTF8));
            if (manifest == null || manifest.Modules == null || manifest.Modules.Count == 0)
            {
                throw new InvalidOperationException("HAL 模块清单无效: " + path);
            }
            return manifest;
        }

        public static List<string> GetDefaultPackageNames(string baseDir, AppConfig cfg)
        {
            var manifest = Load(baseDir, cfg);
            return manifest.Modules
                .Where(m => !string.IsNullOrWhiteSpace(m.Binary))
                .Select(m => Path.GetFileName(m.Binary.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> ParseFilterLines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return list;
            }
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string raw = line.Trim();
                if (raw.Length == 0 || raw.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                // 保留整行（可含「包名 | 脚本」），匹配时再取包名部分
                list.Add(raw);
            }
            return list;
        }

        /// <summary>
        /// 解析「包名 | 自检脚本」行。脚本为 .sh 文件路径：相对 exe（../hal_selfcheck/...）、相对 scripts/（hal_selfcheck/...）或绝对路径；无 | 则仅 L0 包名。
        /// </summary>
        public static List<HalScriptBinding> ParseScriptBindings(string text)
        {
            var list = new List<HalScriptBinding>();
            foreach (string raw in ParseFilterLines(text))
            {
                string package;
                string script;
                SplitPackageAndScript(raw, out package, out script);
                if (string.IsNullOrWhiteSpace(package))
                {
                    continue;
                }
                list.Add(new HalScriptBinding
                {
                    Package = package,
                    ScriptPath = script ?? ""
                });
            }
            return list;
        }

        public static void SplitPackageAndScript(string line, out string package, out string script)
        {
            package = "";
            script = "";
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            string raw = line.Trim();
            int idx = raw.IndexOf('|');
            if (idx < 0)
            {
                package = raw;
                return;
            }
            package = raw.Substring(0, idx).Trim();
            script = raw.Substring(idx + 1).Trim();
        }

        public static string JoinFilterLines(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return string.Empty;
            }
            return string.Join(Environment.NewLine, lines);
        }

        public static string GuessDefaultSelfCheckScript(string packageOrId)
        {
            // 相对 SocOtaUpgrade.exe 所在目录（上一级为 scripts/）
            string t = (packageOrId ?? "").ToLowerInvariant();
            if (t.Contains("vehicleconfig")) return "../hal_selfcheck/vehicleconfig/run.sh";
            if (t.Contains("diag@") || t.EndsWith("diag") || t.Contains(".diag@")) return "../hal_selfcheck/diag/run.sh";
            if (t.Contains("light@") || t.Contains(".light@") || t.EndsWith("light")) return "../hal_selfcheck/light/run.sh";
            return "";
        }

        /// <summary>
        /// 将 UI/配置中的脚本路径规范为相对 scripts/ 的路径（供 t2_script_pool.json 使用）。
        /// 支持：绝对路径、相对 exe（../hal_selfcheck/...）、已是 scripts 相对（hal_selfcheck/...）。
        /// </summary>
        public static string ToScriptsDirRelativePath(string scriptPath, string socOtaBaseDir)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) return "";
            string scriptsDir = Path.GetDirectoryName(socOtaBaseDir);
            if (string.IsNullOrEmpty(scriptsDir)) return scriptPath.Replace("\\", "/").Trim();

            string full;
            string raw = scriptPath.Trim().Replace("/", "\\");
            if (Path.IsPathRooted(raw))
            {
                full = Path.GetFullPath(raw);
            }
            else if (raw.StartsWith(".." + Path.DirectorySeparatorChar) || raw.StartsWith("../") || raw.StartsWith("..\\"))
            {
                full = Path.GetFullPath(Path.Combine(socOtaBaseDir, raw));
            }
            else
            {
                // 默认视为已相对 scripts/
                full = Path.GetFullPath(Path.Combine(scriptsDir, raw));
            }

            string root = Path.GetFullPath(scriptsDir);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).TrimStart('\\', '/').Replace("\\", "/");
            }
            return scriptPath.Replace("\\", "/").Trim();
        }

        public static List<HalModuleDefinition> FilterModules(
            HalModulesManifest manifest,
            IList<string> filter)
        {
            if (manifest == null || manifest.Modules == null)
            {
                return new List<HalModuleDefinition>();
            }
            if (filter == null || filter.Count == 0)
            {
                return manifest.Modules.ToList();
            }

            var tokens = filter
                .Select(NormalizeToken)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tokens.Count == 0)
            {
                return manifest.Modules.ToList();
            }

            return manifest.Modules.Where(m => ModuleMatchesAny(m, tokens)).ToList();
        }

        private static bool ModuleMatchesAny(HalModuleDefinition module, List<string> tokens)
        {
            string binaryName = Path.GetFileName((module.Binary ?? string.Empty).Trim());
            foreach (string token in tokens)
            {
                if (string.Equals(module.Id, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (string.Equals(module.Name, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(binaryName) &&
                    (string.Equals(binaryName, token, StringComparison.OrdinalIgnoreCase) ||
                     binaryName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
                string process = module.ProcessPattern ?? string.Empty;
                if (process.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeToken(string value)
        {
            string t = (value ?? string.Empty).Trim();
            int idx = t.IndexOf('|');
            if (idx >= 0)
            {
                t = t.Substring(0, idx).Trim();
            }
            return t;
        }
    }

    internal sealed class HalScriptBinding
    {
        public string Package { get; set; }
        public string ScriptPath { get; set; }
    }
}
