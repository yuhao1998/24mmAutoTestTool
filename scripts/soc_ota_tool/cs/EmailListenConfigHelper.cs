using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    /// <summary>
    /// 将 UI 上的邮箱监听配置同步到 scripts/t2_atf_config.json 的 email 段，
    /// 供 t2_upgrade_entry.py 邮箱监听使用。
    /// </summary>
    internal static class EmailListenConfigHelper
    {
        public static string ResolveT2AtfConfigPath(string socOtaBaseDir)
        {
            // SocOtaUpgrade/ 的上一级是 scripts/
            string scriptsDir = Path.GetDirectoryName(socOtaBaseDir);
            if (string.IsNullOrEmpty(scriptsDir))
            {
                return "";
            }
            return Path.Combine(scriptsDir, "t2_atf_config.json");
        }

        public static void ApplyFromT2Config(string socOtaBaseDir, AppConfig cfg)
        {
            if (cfg == null) return;
            string path = ResolveT2AtfConfigPath(socOtaBaseDir);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                var root = LoadJsonObject(path);
                if (root == null || !root.ContainsKey("email")) return;
                var email = root["email"] as Dictionary<string, object>;
                if (email == null) return;

                object host;
                if (email.TryGetValue("imap_host", out host) && host != null)
                {
                    string h = Convert.ToString(host);
                    if (!string.IsNullOrWhiteSpace(h)) cfg.EmailImapHost = h.Trim();
                }
                else if (email.TryGetValue("mail_host", out host) && host != null)
                {
                    string h = Convert.ToString(host);
                    if (!string.IsNullOrWhiteSpace(h)) cfg.EmailImapHost = h.Trim();
                }
                object user;
                if (email.TryGetValue("username", out user) && user != null)
                {
                    cfg.EmailUsername = Convert.ToString(user) ?? "";
                }
                object pwd;
                if (email.TryGetValue("password", out pwd) && pwd != null)
                {
                    cfg.EmailPassword = Convert.ToString(pwd) ?? "";
                }
                object mailbox;
                if (email.TryGetValue("mailbox", out mailbox) && mailbox != null)
                {
                    string mb = Convert.ToString(mailbox);
                    if (!string.IsNullOrWhiteSpace(mb)) cfg.EmailMailbox = mb.Trim();
                }
                object fromKw;
                if (email.TryGetValue("from_keyword", out fromKw) && fromKw != null)
                {
                    cfg.EmailFromKeyword = Convert.ToString(fromKw) ?? "";
                }
                object subjectKw;
                if (email.TryGetValue("subject_keyword", out subjectKw) && subjectKw != null)
                {
                    string sk = Convert.ToString(subjectKw) ?? "";
                    if (!string.IsNullOrWhiteSpace(sk)) cfg.EmailSubjectKeyword = sk.Trim();
                }
                object saveDir;
                if (email.TryGetValue("package_save_dir", out saveDir) && saveDir != null)
                {
                    cfg.EmailPackageSaveDir = Convert.ToString(saveDir) ?? "";
                }
                else if (root.ContainsKey("download_dir") && root["download_dir"] != null
                         && string.IsNullOrWhiteSpace(cfg.EmailPackageSaveDir))
                {
                    cfg.EmailPackageSaveDir = Convert.ToString(root["download_dir"]) ?? "";
                }
            }
            catch
            {
                // 读取失败不影响主流程
            }
        }

        public static bool SyncToT2Config(string socOtaBaseDir, AppConfig cfg, out string message)
        {
            message = "";
            if (cfg == null) return false;
            string path = ResolveT2AtfConfigPath(socOtaBaseDir);
            if (string.IsNullOrEmpty(path))
            {
                message = "未找到 scripts 目录，跳过同步 t2_atf_config.json";
                return false;
            }
            if (!File.Exists(path))
            {
                message = "未找到 t2_atf_config.json: " + path;
                return false;
            }

            try
            {
                var root = LoadJsonObject(path);
                if (root == null) root = new Dictionary<string, object>();

                Dictionary<string, object> email;
                object emailObj;
                if (root.TryGetValue("email", out emailObj) && emailObj is Dictionary<string, object>)
                {
                    email = (Dictionary<string, object>)emailObj;
                }
                else
                {
                    email = new Dictionary<string, object>();
                    root["email"] = email;
                }

                if (!string.IsNullOrWhiteSpace(cfg.EmailImapHost))
                {
                    email["imap_host"] = cfg.EmailImapHost.Trim();
                }
                // 与 OWA 一致：默认 EWS
                email["protocol"] = "ews";
                if (!email.ContainsKey("ews_url") || email["ews_url"] == null
                    || string.IsNullOrWhiteSpace(Convert.ToString(email["ews_url"])))
                {
                    string host = string.IsNullOrWhiteSpace(cfg.EmailImapHost)
                        ? "webmail.hangsheng.com.cn" : cfg.EmailImapHost.Trim();
                    email["ews_url"] = "https://" + host + "/EWS/Exchange.asmx";
                }
                if (!email.ContainsKey("pop3_port") || email["pop3_port"] == null)
                {
                    email["pop3_port"] = 995;
                }
                email["username"] = cfg.EmailUsername ?? "";
                // 仅当 UI 填写了密码时才覆盖，避免清空已有密码
                if (!string.IsNullOrEmpty(cfg.EmailPassword))
                {
                    email["password"] = cfg.EmailPassword;
                }
                email["mailbox"] = string.IsNullOrWhiteSpace(cfg.EmailMailbox) ? "INBOX" : cfg.EmailMailbox.Trim();
                email["from_keyword"] = cfg.EmailFromKeyword ?? "";
                email["subject_keyword"] = string.IsNullOrWhiteSpace(cfg.EmailSubjectKeyword)
                    ? "T2_OTA" : cfg.EmailSubjectKeyword.Trim();
                email["package_save_dir"] = cfg.EmailPackageSaveDir ?? "";

                var ser = new JavaScriptSerializer();
                string json = PrettyPrint(ser.Serialize(root));
                File.WriteAllText(path, json, new UTF8Encoding(false));
                string poolMsg;
                SyncHalScriptPool(socOtaBaseDir, cfg, out poolMsg);
                message = "已同步邮箱监听配置到: " + path;
                if (!string.IsNullOrEmpty(poolMsg)) message = message + "; " + poolMsg;
                return true;
            }
            catch (Exception ex)
            {
                message = "同步 t2_atf_config.json 失败: " + ex.Message;
                return false;
            }
        }


        /// <summary>
        /// 按 HAL「包名 | 脚本」同步 t2_script_pool.json（脚本池格式）。
        /// </summary>
        public static bool SyncHalScriptPool(string socOtaBaseDir, AppConfig cfg, out string message)
        {
            message = "";
            if (cfg == null) return false;
            string scriptsDir = Path.GetDirectoryName(socOtaBaseDir);
            if (string.IsNullOrEmpty(scriptsDir))
            {
                message = "未找到 scripts 目录";
                return false;
            }
            string poolPath = Path.Combine(scriptsDir, "t2_script_pool.json");
            try
            {
                var root = File.Exists(poolPath) ? LoadJsonObject(poolPath) : new Dictionary<string, object>();
                if (root == null) root = new Dictionary<string, object>();
                root["enabled"] = true;
                root["build_detect_report"] = true;
                if (!root.ContainsKey("fail_fast")) root["fail_fast"] = false;
                if (!root.ContainsKey("default_timeout_sec")) root["default_timeout_sec"] = 120;
                if (!root.ContainsKey("device_script_dir")) root["device_script_dir"] = "/data/local/tmp/t2_script_pool";
                if (!root.ContainsKey("device_hal_selfcheck_dir")) root["device_hal_selfcheck_dir"] = "/data/local/tmp/t2_selfcheck";

                var bindings = HalModulesManifestHelper.ParseScriptBindings(
                    HalModulesManifestHelper.JoinFilterLines(cfg.HalModuleFilter));
                var scripts = new System.Collections.ArrayList();
                int n = 0;
                foreach (var b in bindings)
                {
                    if (b == null || string.IsNullOrWhiteSpace(b.ScriptPath)) continue;
                    string scriptPath = HalModulesManifestHelper.ToScriptsDirRelativePath(b.ScriptPath, socOtaBaseDir);
                    if (string.IsNullOrWhiteSpace(scriptPath)) continue;
                    string moduleId = GuessModuleId(b.Package, scriptPath);
                    var item = new Dictionary<string, object>();
                    item["kind"] = "hal_module";
                    item["id"] = moduleId;
                    item["module_id"] = moduleId;
                    item["name"] = moduleId + " 自检";
                    item["path"] = scriptPath;
                    item["package"] = b.Package;
                    item["timeout_sec"] = moduleId == "vehicleconfig" ? 300 : 120;
                    item["enabled"] = true;
                    item["tags"] = new object[] { "hal", moduleId };
                    scripts.Add(item);
                    n++;
                }
                                if (n == 0)
                {
                    message = "未配置自检脚本路径（包名后无 | 脚本），保留原脚本池";
                    return true;
                }
                root["scripts"] = scripts;
                var ser = new JavaScriptSerializer();
                File.WriteAllText(poolPath, PrettyPrint(ser.Serialize(root)), new UTF8Encoding(false));
                message = "已同步脚本池 " + n + " 项到 t2_script_pool.json";
                return true;
            }
            catch (Exception ex)
            {
                message = "同步脚本池失败: " + ex.Message;
                return false;
            }
        }

        private static string GuessModuleId(string package, string scriptPath)
        {
            string p = (scriptPath ?? "").Replace("\\", "/").ToLowerInvariant();
            Match m = Regex.Match(p, @"hal_selfcheck/([^/]+)/");
            if (m.Success) return m.Groups[1].Value;
            string pkg = (package ?? "").ToLowerInvariant();
            if (pkg.Contains("vehicleconfig")) return "vehicleconfig";
            if (pkg.Contains("diag")) return "diag";
            if (pkg.Contains("light")) return "light";
            if (pkg.Contains("audioctrl")) return "audioctrl";
            if (pkg.Contains("input")) return "input";
            string name = Path.GetFileNameWithoutExtension(scriptPath ?? "");
            return string.IsNullOrWhiteSpace(name) ? "hal" : name;
        }


        private static Dictionary<string, object> LoadJsonObject(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var ser = new JavaScriptSerializer();
            return ser.Deserialize<Dictionary<string, object>>(text);
        }

        private static string PrettyPrint(string compactJson)
        {
            try
            {
                var sb = new StringBuilder();
                int indent = 0;
                bool inString = false;
                for (int i = 0; i < compactJson.Length; i++)
                {
                    char c = compactJson[i];
                    if (c == '"' && (i == 0 || compactJson[i - 1] != '\\'))
                    {
                        inString = !inString;
                        sb.Append(c);
                        continue;
                    }
                    if (inString)
                    {
                        sb.Append(c);
                        continue;
                    }
                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case '}':
                        case ']':
                            sb.AppendLine();
                            indent = Math.Max(0, indent - 1);
                            sb.Append(new string(' ', indent * 2));
                            sb.Append(c);
                            break;
                        case ',':
                            sb.Append(c);
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case ':':
                            sb.Append(": ");
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return compactJson;
            }
        }
    }
}
