using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    internal sealed class AppConfig
    {
        public string SerialPort { get; set; }
        public int BaudRate { get; set; }
        public string UsbModeCmd { get; set; }
        public string RemoteOtaDir { get; set; }
        public int BootTimeoutSec { get; set; }
        public int UpdateTimeoutSec { get; set; }
        public string LogDir { get; set; }
        public bool SkipEnableVerity { get; set; }
        public bool SkipVersionVerify { get; set; }
        public string ExpectedBuildIncremental { get; set; }
        public string ExpectedBuildFingerprint { get; set; }
        public string ExpectedPackageSha256 { get; set; }
        public bool SkipHalStatusCheck { get; set; }
        public string HalModulesManifest { get; set; }
        public int HalStatusTombstoneMinutes { get; set; }
        public string ExpectedBootSlotSuffix { get; set; }
        /// <summary>HAL 检查包名/模块 ID 过滤列表；空则检查 hal_modules.json 中全部模块。</summary>
        public List<string> HalModuleFilter { get; set; }

        /// <summary>邮箱监听：IMAP 主机。</summary>
        public string EmailImapHost { get; set; }
        /// <summary>邮箱监听：登录账号。</summary>
        public string EmailUsername { get; set; }
        /// <summary>邮箱监听：登录密码/授权码（仅写入 t2_atf_config.json，不落 soc_ota_config）。</summary>
        [ScriptIgnore]
        public string EmailPassword { get; set; }
        /// <summary>邮箱监听：收件箱文件夹名（如 INBOX）。</summary>
        public string EmailMailbox { get; set; }
        /// <summary>邮箱监听：发件人过滤关键字（空=不过滤）。</summary>
        public string EmailFromKeyword { get; set; }
        /// <summary>邮箱监听：邮件提示词（匹配主题或正文，对应 t2_atf_config email.subject_keyword）。</summary>
        public string EmailSubjectKeyword { get; set; }
        /// <summary>邮箱监听：升级包保存到本地的目录。</summary>
        public string EmailPackageSaveDir { get; set; }

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                SerialPort = "COM4",
                BaudRate = 115200,
                UsbModeCmd = "echo peripheral > /sys/bus/platform/devices/a600000.ssusb/mode",
                RemoteOtaDir = "/data/ota",
                BootTimeoutSec = 600,
                UpdateTimeoutSec = 3600,
                LogDir = "",
                EmailImapHost = "webmail.hangsheng.com.cn",
                EmailUsername = "",
                EmailPassword = "",
                EmailMailbox = "INBOX",
                EmailFromKeyword = "",
                EmailSubjectKeyword = "T2_OTA",
                EmailPackageSaveDir = ""
            };
        }

        public void ApplyDefaults()
        {
            var d = CreateDefault();
            if (string.IsNullOrWhiteSpace(SerialPort)) SerialPort = d.SerialPort;
            if (BaudRate <= 0) BaudRate = d.BaudRate;
            if (string.IsNullOrWhiteSpace(UsbModeCmd)) UsbModeCmd = d.UsbModeCmd;
            if (string.IsNullOrWhiteSpace(RemoteOtaDir)) RemoteOtaDir = d.RemoteOtaDir;
            if (BootTimeoutSec <= 0) BootTimeoutSec = d.BootTimeoutSec;
            if (UpdateTimeoutSec <= 0) UpdateTimeoutSec = d.UpdateTimeoutSec;
            if (string.IsNullOrWhiteSpace(EmailImapHost)) EmailImapHost = d.EmailImapHost;
            if (EmailUsername == null) EmailUsername = "";
            if (EmailPassword == null) EmailPassword = "";
            if (string.IsNullOrWhiteSpace(EmailMailbox)) EmailMailbox = d.EmailMailbox;
            if (EmailFromKeyword == null) EmailFromKeyword = "";
            if (EmailSubjectKeyword == null) EmailSubjectKeyword = d.EmailSubjectKeyword;
            if (EmailPackageSaveDir == null) EmailPackageSaveDir = "";
        }

        public static string ConfigPath(string baseDir)
        {
            return Path.Combine(baseDir, "soc_ota_config.json");
        }

        public static AppConfig Load(string baseDir, AppConfig overrides)
        {
            var cfg = CreateDefault();
            string path = ConfigPath(baseDir);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var loaded = ser.Deserialize<AppConfig>(json);
                if (loaded != null)
                {
                    cfg = loaded;
                }
            }
            else
            {
                Save(baseDir, cfg);
                Console.WriteLine("已生成默认配置: " + path);
            }
            cfg.ApplyDefaults();
            if (overrides != null)
            {
                if (!string.IsNullOrWhiteSpace(overrides.SerialPort)) cfg.SerialPort = overrides.SerialPort;
                if (overrides.BaudRate > 0) cfg.BaudRate = overrides.BaudRate;
                if (!string.IsNullOrWhiteSpace(overrides.UsbModeCmd)) cfg.UsbModeCmd = overrides.UsbModeCmd;
                if (!string.IsNullOrWhiteSpace(overrides.RemoteOtaDir)) cfg.RemoteOtaDir = overrides.RemoteOtaDir;
                if (overrides.BootTimeoutSec > 0) cfg.BootTimeoutSec = overrides.BootTimeoutSec;
                if (overrides.UpdateTimeoutSec > 0) cfg.UpdateTimeoutSec = overrides.UpdateTimeoutSec;
                if (!string.IsNullOrWhiteSpace(overrides.LogDir)) cfg.LogDir = overrides.LogDir;
            }
            return cfg;
        }

        public static void Save(string baseDir, AppConfig cfg)
        {
            cfg.ApplyDefaults();
            var ser = new JavaScriptSerializer();
            string json = ser.Serialize(cfg);
            File.WriteAllText(ConfigPath(baseDir), json, Encoding.UTF8);
        }
    }
}
