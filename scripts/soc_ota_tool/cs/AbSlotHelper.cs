using System;
using System.Text.RegularExpressions;

namespace SocOtaUpgrade
{
    internal sealed class AbSlotSnapshot
    {
        public bool IsAbDevice { get; set; }
        public string SlotSuffix { get; set; }
        public string SlotLetter { get; set; }
        public int CurrentSlotIndex { get; set; }
        public bool CurrentSlotBootable { get; set; }
        public bool SlotA_Bootable { get; set; }
        public bool SlotB_Bootable { get; set; }
    }

    internal sealed class AbSlotExpectation
    {
        public string PreOtaSlotLetter { get; set; }
        public string TargetSlotLetter { get; set; }
        public bool SlotSwitchRequested { get; set; }
    }

    internal static class AbSlotHelper
    {
        public static AbSlotSnapshot ReadCurrent(AdbHelper adb)
        {
            var snapshot = new AbSlotSnapshot
            {
                CurrentSlotIndex = -1
            };

            string suffix = adb.GetProp("ro.boot.slot_suffix").Trim();
            if (string.IsNullOrEmpty(suffix))
            {
                suffix = adb.GetProp("ro.boot.slot").Trim();
            }
            snapshot.SlotSuffix = suffix;
            snapshot.SlotLetter = NormalizeSlotLetter(suffix);

            string abUpdate = adb.GetProp("ro.build.ab_update").Trim();
            string virtualAb = adb.GetProp("ro.virtual_ab.enabled").Trim();
            snapshot.IsAbDevice =
                !string.IsNullOrEmpty(snapshot.SlotLetter) ||
                abUpdate == "1" ||
                abUpdate.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                virtualAb == "1" ||
                virtualAb.Equals("true", StringComparison.OrdinalIgnoreCase);

            string currentSlotText = adb.Run(new[] { "shell", "bootctl get-current-slot 2>/dev/null" }, true).Trim();
            int currentSlot;
            if (int.TryParse(ExtractFirstInteger(currentSlotText), out currentSlot))
            {
                snapshot.CurrentSlotIndex = currentSlot;
                if (string.IsNullOrEmpty(snapshot.SlotLetter))
                {
                    snapshot.SlotLetter = currentSlot == 1 ? "b" : "a";
                    snapshot.SlotSuffix = "_" + snapshot.SlotLetter;
                }
                snapshot.IsAbDevice = true;
            }

            string bootable0 = adb.Run(new[] { "shell", "bootctl is-slot-bootable 0 2>/dev/null" }, true).Trim();
            string bootable1 = adb.Run(new[] { "shell", "bootctl is-slot-bootable 1 2>/dev/null" }, true).Trim();
            bool bootctlAvailable =
                bootable0 == "0" || bootable0 == "1" ||
                bootable1 == "0" || bootable1 == "1" ||
                IsTruthy(bootable0) || IsTruthy(bootable1);
            snapshot.SlotA_Bootable = IsTruthy(bootable0);
            snapshot.SlotB_Bootable = IsTruthy(bootable1);

            if (!string.IsNullOrEmpty(snapshot.SlotLetter))
            {
                snapshot.CurrentSlotBootable = snapshot.SlotLetter.Equals("b", StringComparison.OrdinalIgnoreCase)
                    ? snapshot.SlotB_Bootable
                    : snapshot.SlotA_Bootable;
            }
            else if (snapshot.CurrentSlotIndex >= 0)
            {
                snapshot.CurrentSlotBootable = snapshot.CurrentSlotIndex == 1
                    ? snapshot.SlotB_Bootable
                    : snapshot.SlotA_Bootable;
            }

            if (!bootctlAvailable)
            {
                snapshot.CurrentSlotBootable = true;
            }

            if (!snapshot.IsAbDevice && !string.IsNullOrEmpty(snapshot.SlotLetter))
            {
                snapshot.IsAbDevice = true;
            }

            return snapshot;
        }

        public static AbSlotExpectation ParseEngineLogExpectation(string engineLog, string preOtaSlotLetter)
        {
            var expectation = new AbSlotExpectation
            {
                PreOtaSlotLetter = NormalizeSlotLetter(preOtaSlotLetter)
            };

            if (string.IsNullOrEmpty(engineLog))
            {
                return expectation;
            }

            expectation.SlotSwitchRequested =
                engineLog.IndexOf("switch_slot_on_reboot: true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                engineLog.IndexOf("Switching slot to", StringComparison.OrdinalIgnoreCase) >= 0;

            Match targetMatch = Regex.Match(
                engineLog,
                @"Switching slot to _?([ab])",
                RegexOptions.IgnoreCase);
            if (targetMatch.Success)
            {
                expectation.TargetSlotLetter = NormalizeSlotLetter(targetMatch.Groups[1].Value);
                return expectation;
            }

            Match slotPair = Regex.Match(
                engineLog,
                @"source slot\s+(\d+).*target slot\s+(\d+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (slotPair.Success)
            {
                expectation.TargetSlotLetter = SlotIndexToLetter(int.Parse(slotPair.Groups[2].Value));
                return expectation;
            }

            Match installing = Regex.Match(
                engineLog,
                @"Installing on (?:target )?slot _?([ab])",
                RegexOptions.IgnoreCase);
            if (installing.Success)
            {
                expectation.TargetSlotLetter = NormalizeSlotLetter(installing.Groups[1].Value);
                return expectation;
            }

            if (expectation.SlotSwitchRequested && !string.IsNullOrEmpty(expectation.PreOtaSlotLetter))
            {
                expectation.TargetSlotLetter = expectation.PreOtaSlotLetter.Equals("a", StringComparison.OrdinalIgnoreCase)
                    ? "b"
                    : "a";
            }

            return expectation;
        }

        public static void LogSlotSnapshot(string title, AbSlotSnapshot snapshot)
        {
            Log.Step("--- " + title + " ---");
            if (snapshot == null)
            {
                Log.Step("  (无数据)");
                return;
            }
            Log.Step("  A/B 设备: " + (snapshot.IsAbDevice ? "是" : "否"));
            if (!snapshot.IsAbDevice)
            {
                return;
            }
            Log.Step("  当前槽位: " + FormatSlot(snapshot.SlotLetter, snapshot.SlotSuffix, snapshot.CurrentSlotIndex));
            Log.Step("  当前槽可启动: " + (snapshot.CurrentSlotBootable ? "是" : "否/未知"));
            Log.Step(string.Format("  槽位可启动: A={0} B={1}",
                FormatBootable(snapshot.SlotA_Bootable),
                FormatBootable(snapshot.SlotB_Bootable)));
        }

        public static string FormatSlot(string letter, string suffix, int index)
        {
            if (!string.IsNullOrEmpty(suffix))
            {
                return suffix;
            }
            if (!string.IsNullOrEmpty(letter))
            {
                return "_" + letter;
            }
            if (index >= 0)
            {
                return index == 1 ? "_b (slot 1)" : "_a (slot 0)";
            }
            return "未知";
        }

        public static string NormalizeSlot(string value)
        {
            return NormalizeSlotLetter(value);
        }

        private static string NormalizeSlotLetter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            string trimmed = value.Trim().TrimStart('_').ToLowerInvariant();
            if (trimmed == "a" || trimmed == "0")
            {
                return "a";
            }
            if (trimmed == "b" || trimmed == "1")
            {
                return "b";
            }
            return trimmed;
        }

        private static string SlotIndexToLetter(int index)
        {
            return index == 1 ? "b" : "a";
        }

        private static string ExtractFirstInteger(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var digits = new System.Text.StringBuilder();
            bool started = false;
            foreach (char c in text)
            {
                if (char.IsDigit(c))
                {
                    digits.Append(c);
                    started = true;
                }
                else if (started)
                {
                    break;
                }
            }
            return digits.ToString();
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string trimmed = value.Trim();
            return trimmed == "1" ||
                   trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBootable(bool bootable)
        {
            return bootable ? "可启动" : "否/未知";
        }
    }
}
