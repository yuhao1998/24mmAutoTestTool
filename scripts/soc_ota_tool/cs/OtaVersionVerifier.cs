using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SocOtaUpgrade
{
    internal sealed class OtaPackageExpectation
    {
        public string PackagePath { get; set; }
        public string Sha256 { get; set; }
        public string PostBuild { get; set; }
        public string PostBuildIncremental { get; set; }
        public string PreDevice { get; set; }
        public string PayloadFileHash { get; set; }
        public string PayloadFileSize { get; set; }
    }

    internal sealed class DeviceVersionSnapshot
    {
        public string DisplayId { get; set; }
        public string Incremental { get; set; }
        public string Fingerprint { get; set; }
        public string VendorVersion { get; set; }
        public string ProductVersion { get; set; }
        public string Description { get; set; }
    }

    internal sealed class VersionCheckItem
    {
        public string Name { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public bool Passed { get; set; }
    }

    internal sealed class VersionVerifyReport
    {
        public bool Passed { get; set; }
        public string Timestamp { get; set; }
        public string PackagePath { get; set; }
        public string PackageSha256 { get; set; }
        public List<VersionCheckItem> Items { get; set; }

        public VersionVerifyReport()
        {
            Items = new List<VersionCheckItem>();
        }
    }

    internal sealed class VersionVerifyException : InvalidOperationException
    {
        public VersionVerifyException(string summary, string fullReport)
            : base(summary)
        {
            Summary = summary;
            FullReport = fullReport;
        }

        public string Summary { get; private set; }
        public string FullReport { get; private set; }
    }

    internal static class OtaPackageReader
    {
        private const string MetadataEntry = "META-INF/com/android/metadata";
        private const string PayloadPropsEntry = "payload_properties.txt";

        public static OtaPackageExpectation Load(string packagePath, AppConfig cfg)
        {
            string fullPath = Path.GetFullPath(packagePath);
            var expectation = new OtaPackageExpectation
            {
                PackagePath = fullPath,
                Sha256 = ComputeSha256(fullPath)
            };

            if (Directory.Exists(fullPath))
            {
                LoadFromDirectory(fullPath, expectation);
            }
            else if (File.Exists(fullPath))
            {
                LoadFromZip(fullPath, expectation);
            }

            ApplyConfigOverrides(expectation, cfg);
            return expectation;
        }

        public static void SaveManifest(OtaPackageExpectation expectation, string path)
        {
            var ser = new JavaScriptSerializer();
            File.WriteAllText(path, ser.Serialize(expectation), Encoding.UTF8);
        }

        public static void LogExpectation(OtaPackageExpectation expectation)
        {
            Log.Step("========== OTA 包版本预期（步骤 4 基准） ==========");
            Log.Step("  包路径: " + expectation.PackagePath);
            Log.Step("  SHA256: " + expectation.Sha256);
            if (!string.IsNullOrEmpty(expectation.PostBuild))
            {
                Log.Step("  post-build: " + expectation.PostBuild);
            }
            if (!string.IsNullOrEmpty(expectation.PostBuildIncremental))
            {
                Log.Step("  post-build-incremental: " + expectation.PostBuildIncremental);
            }
            if (!string.IsNullOrEmpty(expectation.PayloadFileHash))
            {
                Log.Step("  payload FILE_HASH: " + expectation.PayloadFileHash);
            }
            Log.Step("==================================================");
        }

        private static void ApplyConfigOverrides(OtaPackageExpectation expectation, AppConfig cfg)
        {
            if (cfg == null) return;
            if (!string.IsNullOrWhiteSpace(cfg.ExpectedBuildFingerprint))
            {
                expectation.PostBuild = cfg.ExpectedBuildFingerprint.Trim();
            }
            if (!string.IsNullOrWhiteSpace(cfg.ExpectedBuildIncremental))
            {
                expectation.PostBuildIncremental = cfg.ExpectedBuildIncremental.Trim();
            }
        }

        private static void LoadFromDirectory(string dir, OtaPackageExpectation expectation)
        {
            string propsPath = Path.Combine(dir, PayloadPropsEntry);
            if (File.Exists(propsPath))
            {
                ParsePayloadProperties(File.ReadAllText(propsPath, Encoding.UTF8), expectation);
            }
        }

        private static void LoadFromZip(string zipPath, OtaPackageExpectation expectation)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry meta = FindEntry(zip, MetadataEntry);
                if (meta != null)
                {
                    using (var reader = new StreamReader(meta.Open(), Encoding.UTF8))
                    {
                        ParseMetadata(reader.ReadToEnd(), expectation);
                    }
                }

                ZipArchiveEntry props = FindEntry(zip, PayloadPropsEntry);
                if (props != null)
                {
                    using (var reader = new StreamReader(props.Open(), Encoding.UTF8))
                    {
                        ParsePayloadProperties(reader.ReadToEnd(), expectation);
                    }
                }
            }
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, string name)
        {
            string normalized = name.Replace('\\', '/');
            return zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static void ParseMetadata(string text, OtaPackageExpectation expectation)
        {
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Equals("post-build", StringComparison.OrdinalIgnoreCase))
                {
                    expectation.PostBuild = value;
                }
                else if (key.Equals("post-build-incremental", StringComparison.OrdinalIgnoreCase))
                {
                    expectation.PostBuildIncremental = value;
                }
                else if (key.Equals("pre-device", StringComparison.OrdinalIgnoreCase))
                {
                    expectation.PreDevice = value;
                }
            }
        }

        private static void ParsePayloadProperties(string text, OtaPackageExpectation expectation)
        {
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Equals("FILE_HASH", StringComparison.OrdinalIgnoreCase))
                {
                    expectation.PayloadFileHash = value;
                }
                else if (key.Equals("FILE_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    expectation.PayloadFileSize = value;
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    internal static class OtaVersionVerifier
    {
        public static void VerifyOrThrow(
            AdbHelper adb,
            OtaPackageExpectation expectation,
            AppConfig cfg,
            string sessionDir,
            string engineLog,
            string preOtaSlotLetter)
        {
            if (cfg != null && cfg.SkipVersionVerify)
            {
                Log.Step("已跳过版本校验（SkipVersionVerify=true）");
                LogDeviceVersion(adb);
                AbSlotHelper.LogSlotSnapshot("当前 A/B 槽位", AbSlotHelper.ReadCurrent(adb));
                return;
            }

            Log.Step("========== 步骤 4：车机版本校验 ==========");
            DeviceVersionSnapshot device = ReadDeviceVersion(adb);
            AbSlotSnapshot postSlot = AbSlotHelper.ReadCurrent(adb);
            AbSlotExpectation slotExpectation = AbSlotHelper.ParseEngineLogExpectation(engineLog, preOtaSlotLetter);
            LogDeviceVersion(device);
            AbSlotHelper.LogSlotSnapshot("升级后 A/B 槽位", postSlot);
            if (!string.IsNullOrEmpty(slotExpectation.TargetSlotLetter))
            {
                Log.Step("  OTA 目标槽位（日志解析）: " + slotExpectation.TargetSlotLetter.ToUpperInvariant());
            }

            var report = BuildReport(expectation, device, cfg, postSlot, slotExpectation, preOtaSlotLetter);
            SaveReport(report, sessionDir);

            if (report.Passed)
            {
                Log.Step("版本校验 Pass");
                Log.Step("==========================================");
                return;
            }

            string fullReport = BuildFailureText(report, expectation, device);
            string summary = "版本校验 Fail：车机版本与 OTA 包预期不一致";
            Log.Error(summary);
            foreach (var item in report.Items.Where(i => !i.Passed))
            {
                Log.Error(string.Format("  {0}: 预期=[{1}] 实际=[{2}]", item.Name, item.Expected, item.Actual));
            }
            Log.Error("详见: " + Path.Combine(sessionDir, "version_verify.json"));
            throw new VersionVerifyException(summary, fullReport);
        }

        private static DeviceVersionSnapshot ReadDeviceVersion(AdbHelper adb)
        {
            return new DeviceVersionSnapshot
            {
                DisplayId = adb.GetProp("ro.build.display.id"),
                Incremental = adb.GetProp("ro.build.version.incremental"),
                Fingerprint = adb.GetProp("ro.build.fingerprint"),
                VendorVersion = adb.GetProp("ro.vendor.build.version"),
                ProductVersion = adb.GetProp("ro.product.build.version"),
                Description = adb.GetProp("ro.build.description")
            };
        }

        private static void LogDeviceVersion(AdbHelper adb)
        {
            LogDeviceVersion(ReadDeviceVersion(adb));
        }

        private static void LogDeviceVersion(DeviceVersionSnapshot device)
        {
            Log.Step("========== 车机版本信息（getprop） ==========");
            LogProp("ro.build.display.id", device.DisplayId);
            LogProp("ro.build.version.incremental", device.Incremental);
            LogProp("ro.build.fingerprint", device.Fingerprint);
            LogProp("ro.vendor.build.version", device.VendorVersion);
            LogProp("ro.product.build.version", device.ProductVersion);
            LogProp("ro.build.description", device.Description);
            Log.Step("============================================");
        }

        private static void LogProp(string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Log.Step("  " + name + " = " + value);
            }
        }

        private static VersionVerifyReport BuildReport(
            OtaPackageExpectation expectation,
            DeviceVersionSnapshot device,
            AppConfig cfg,
            AbSlotSnapshot postSlot,
            AbSlotExpectation slotExpectation,
            string preOtaSlotLetter)
        {
            var report = new VersionVerifyReport
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                PackagePath = expectation.PackagePath,
                PackageSha256 = expectation.Sha256,
                Passed = true
            };

            AddAbSlotChecks(report, cfg, postSlot, slotExpectation, preOtaSlotLetter);

            if (!string.IsNullOrWhiteSpace(cfg != null ? cfg.ExpectedPackageSha256 : null))
            {
                AddCheck(report, "OTA包SHA256",
                    cfg.ExpectedPackageSha256.Trim().ToLowerInvariant(),
                    expectation.Sha256,
                    StringEqualsNormalized(expectation.Sha256, cfg.ExpectedPackageSha256));
            }
            else
            {
                report.Items.Add(new VersionCheckItem
                {
                    Name = "OTA包SHA256",
                    Expected = "(记录存档)",
                    Actual = expectation.Sha256,
                    Passed = true
                });
            }

            bool hasVersionExpectation =
                !string.IsNullOrWhiteSpace(expectation.PostBuildIncremental) ||
                !string.IsNullOrWhiteSpace(expectation.PostBuild);

            if (!hasVersionExpectation)
            {
                AddCheck(report, "SOC版本预期",
                    "OTA 包 metadata 或配置中的 expected_build_*",
                    "未解析到预期版本",
                    false);
                return report;
            }

            if (!string.IsNullOrWhiteSpace(expectation.PostBuildIncremental))
            {
                AddCheck(report, "SOC版本号(incremental)",
                    expectation.PostBuildIncremental,
                    device.Incremental,
                    StringEqualsNormalized(device.Incremental, expectation.PostBuildIncremental));
            }

            if (!string.IsNullOrWhiteSpace(expectation.PostBuild))
            {
                AddCheck(report, "SOC指纹(fingerprint)",
                    expectation.PostBuild,
                    device.Fingerprint,
                    StringEqualsNormalized(device.Fingerprint, expectation.PostBuild));
            }

            return report;
        }

        private static void AddAbSlotChecks(
            VersionVerifyReport report,
            AppConfig cfg,
            AbSlotSnapshot postSlot,
            AbSlotExpectation slotExpectation,
            string preOtaSlotLetter)
        {
            if (postSlot == null || !postSlot.IsAbDevice)
            {
                report.Items.Add(new VersionCheckItem
                {
                    Name = "A/B槽位",
                    Expected = "A/B 设备",
                    Actual = "非 A/B 或未识别槽位",
                    Passed = true
                });
                return;
            }

            string currentSlot = AbSlotHelper.FormatSlot(
                postSlot.SlotLetter, postSlot.SlotSuffix, postSlot.CurrentSlotIndex);
            report.Items.Add(new VersionCheckItem
            {
                Name = "A/B当前槽位",
                Expected = "(记录)",
                Actual = currentSlot,
                Passed = true
            });

            string expectedTarget = null;
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.ExpectedBootSlotSuffix))
            {
                expectedTarget = AbSlotHelper.NormalizeSlot(cfg.ExpectedBootSlotSuffix);
            }
            else if (slotExpectation != null && !string.IsNullOrEmpty(slotExpectation.TargetSlotLetter))
            {
                expectedTarget = slotExpectation.TargetSlotLetter;
            }

            if (!string.IsNullOrEmpty(expectedTarget))
            {
                AddCheck(report, "A/B目标槽匹配",
                    expectedTarget.ToUpperInvariant(),
                    (postSlot.SlotLetter ?? "").ToUpperInvariant(),
                    string.Equals(postSlot.SlotLetter, expectedTarget, StringComparison.OrdinalIgnoreCase));
            }
            else if (slotExpectation != null && slotExpectation.SlotSwitchRequested)
            {
                string preLetter = AbSlotHelper.NormalizeSlot(preOtaSlotLetter);
                if (!string.IsNullOrEmpty(preLetter) && !string.IsNullOrEmpty(postSlot.SlotLetter))
                {
                    bool switched = !string.Equals(preLetter, postSlot.SlotLetter, StringComparison.OrdinalIgnoreCase);
                    AddCheck(report, "A/B槽位切换",
                        preLetter.Equals("a", StringComparison.OrdinalIgnoreCase) ? "A→B" : "B→A",
                        preLetter + "→" + postSlot.SlotLetter,
                        switched);
                }
                else
                {
                    report.Items.Add(new VersionCheckItem
                    {
                        Name = "A/B槽位切换",
                        Expected = "升级前后槽位不同",
                        Actual = "缺少升级前槽位记录",
                        Passed = true
                    });
                }
            }
            else
            {
                report.Items.Add(new VersionCheckItem
                {
                    Name = "A/B槽位切换",
                    Expected = "OTA 日志含目标槽位或 switch_slot",
                    Actual = "未解析到目标槽（仅记录当前槽）",
                    Passed = true
                });
            }

            if (!string.IsNullOrEmpty(postSlot.SlotLetter))
            {
                AddCheck(report, "A/B当前槽可启动",
                    "bootable",
                    postSlot.CurrentSlotBootable ? "bootable" : "not bootable",
                    postSlot.CurrentSlotBootable);
            }
        }

        private static void AddCheck(
            VersionVerifyReport report,
            string name,
            string expected,
            string actual,
            bool passed)
        {
            report.Items.Add(new VersionCheckItem
            {
                Name = name,
                Expected = expected ?? string.Empty,
                Actual = actual ?? string.Empty,
                Passed = passed
            });
            if (!passed)
            {
                report.Passed = false;
            }
        }

        private static bool StringEqualsNormalized(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveReport(VersionVerifyReport report, string sessionDir)
        {
            if (string.IsNullOrEmpty(sessionDir))
            {
                return;
            }
            Directory.CreateDirectory(sessionDir);
            string jsonPath = Path.Combine(sessionDir, "version_verify.json");
            var ser = new JavaScriptSerializer();
            File.WriteAllText(jsonPath, ser.Serialize(report), Encoding.UTF8);
        }

        private static string BuildFailureText(
            VersionVerifyReport report,
            OtaPackageExpectation expectation,
            DeviceVersionSnapshot device)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== 版本校验失败报告 ==========");
            sb.AppendLine("时间: " + report.Timestamp);
            sb.AppendLine();
            sb.AppendLine("【OTA 包】");
            sb.AppendLine("  路径: " + expectation.PackagePath);
            sb.AppendLine("  SHA256: " + expectation.Sha256);
            if (!string.IsNullOrEmpty(expectation.PostBuildIncremental))
            {
                sb.AppendLine("  预期 incremental: " + expectation.PostBuildIncremental);
            }
            if (!string.IsNullOrEmpty(expectation.PostBuild))
            {
                sb.AppendLine("  预期 fingerprint: " + expectation.PostBuild);
            }
            sb.AppendLine();
            sb.AppendLine("【车机 getprop】");
            sb.AppendLine("  ro.build.version.incremental = " + (device.Incremental ?? ""));
            sb.AppendLine("  ro.build.fingerprint = " + (device.Fingerprint ?? ""));
            sb.AppendLine("  ro.build.display.id = " + (device.DisplayId ?? ""));
            sb.AppendLine();
            foreach (var item in report.Items.Where(i => i.Name != null && i.Name.StartsWith("A/B")))
            {
                sb.AppendLine(string.Format("  [{0}] {1}: 预期={2} 实际={3}",
                    item.Passed ? "Pass" : "Fail", item.Name, item.Expected, item.Actual));
            }
            sb.AppendLine("【校验明细】");
            foreach (var item in report.Items)
            {
                sb.AppendLine(string.Format("  [{0}] {1}",
                    item.Passed ? "Pass" : "Fail", item.Name));
                sb.AppendLine("    预期: " + item.Expected);
                sb.AppendLine("    实际: " + item.Actual);
            }
            sb.AppendLine("======================================");
            return sb.ToString();
        }
    }
}
