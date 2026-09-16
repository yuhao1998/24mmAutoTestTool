using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SocOtaUpgrade
{
    internal sealed class VerificationResultRow
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string Result { get; set; }
        public string Detail { get; set; }
    }

    internal static class VerificationResultsUiHelper
    {
        public static List<VerificationResultRow> LoadFromSession(string sessionDir)
        {
            var rows = new List<VerificationResultRow>();
            if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
            {
                return rows;
            }

            string versionPath = Path.Combine(sessionDir, "version_verify.json");
            if (File.Exists(versionPath))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var report = ser.Deserialize<VersionVerifyReport>(File.ReadAllText(versionPath, Encoding.UTF8));
                    if (report != null && report.Items != null)
                    {
                        foreach (VersionCheckItem item in report.Items)
                        {
                            rows.Add(new VerificationResultRow
                            {
                                Category = "版本校验",
                                Name = item.Name,
                                Result = item.Passed ? "PASS" : "FAIL",
                                Detail = string.Format("预期={0} 实际={1}", item.Expected, item.Actual)
                            });
                        }
                    }
                }
                catch
                {
                    rows.Add(new VerificationResultRow
                    {
                        Category = "版本校验",
                        Name = "报告解析",
                        Result = "WARN",
                        Detail = versionPath
                    });
                }
            }

            string halPath = Path.Combine(sessionDir, "hal_status.json");
            if (File.Exists(halPath))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var report = ser.Deserialize<HalStatusReport>(File.ReadAllText(halPath, Encoding.UTF8));
                    if (report != null)
                    {
                        if (report.GlobalChecks != null)
                        {
                            foreach (string line in report.GlobalChecks)
                            {
                                rows.Add(ParseGlobalCheckLine(line));
                            }
                        }
                        if (report.Modules != null)
                        {
                            foreach (HalStatusCheckItem item in report.Modules)
                            {
                                rows.Add(new VerificationResultRow
                                {
                                    Category = "HAL模块",
                                    Name = item.ModuleName,
                                    Result = item.Result,
                                    Detail = item.Detail
                                });
                            }
                        }
                    }
                }
                catch
                {
                    rows.Add(new VerificationResultRow
                    {
                        Category = "HAL模块",
                        Name = "报告解析",
                        Result = "WARN",
                        Detail = halPath
                    });
                }
            }

            AppendScriptPoolRows(rows, sessionDir);
            return rows;
        }

        private static void AppendScriptPoolRows(List<VerificationResultRow> rows, string sessionDir)
        {
            string scriptPath = Path.Combine(sessionDir, "script_results.json");
            if (File.Exists(scriptPath))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var report = ser.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(scriptPath, Encoding.UTF8));
                    if (report != null)
                    {
                        object itemsObj;
                        if (report.TryGetValue("Items", out itemsObj) && itemsObj != null)
                        {
                            var list = itemsObj as System.Collections.ArrayList;
                            if (list == null)
                            {
                                var arr = itemsObj as object[];
                                if (arr != null) list = new System.Collections.ArrayList(arr);
                            }
                            if (list != null)
                            {
                                foreach (object o in list)
                                {
                                    var d = o as Dictionary<string, object>;
                                    if (d == null) continue;
                                    string name = GetStr(d, "name");
                                    if (string.IsNullOrEmpty(name)) name = GetStr(d, "id");
                                    bool skipped = GetBool(d, "skipped");
                                    bool passed = GetBool(d, "passed");
                                    string result = skipped ? "SKIP" : (passed ? "PASS" : "FAIL");
                                    string detail = GetStr(d, "error");
                                    if (string.IsNullOrEmpty(detail))
                                    {
                                        detail = string.Format(
                                            "kind={0} exit={1} {2}s",
                                            GetStr(d, "kind"),
                                            GetStr(d, "exit_code"),
                                            GetStr(d, "duration_sec"));
                                    }
                                    rows.Add(new VerificationResultRow
                                    {
                                        Category = "脚本池",
                                        Name = name,
                                        Result = result,
                                        Detail = detail
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                    rows.Add(new VerificationResultRow
                    {
                        Category = "脚本池",
                        Name = "报告解析",
                        Result = "WARN",
                        Detail = scriptPath
                    });
                }
            }

            string modulesDir = Path.Combine(sessionDir, "detect", "modules");
            if (!Directory.Exists(modulesDir))
            {
                return;
            }
            foreach (string modDir in Directory.GetDirectories(modulesDir))
            {
                string mj = Path.Combine(modDir, "module_result.json");
                if (!File.Exists(mj)) continue;
                try
                {
                    var ser = new JavaScriptSerializer();
                    var mod = ser.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(mj, Encoding.UTF8));
                    if (mod == null) continue;
                    string mid = GetStr(mod, "module_id");
                    if (string.IsNullOrEmpty(mid)) mid = Path.GetFileName(modDir);
                    object casesObj;
                    if (!mod.TryGetValue("cases", out casesObj) || casesObj == null) continue;
                    var cases = casesObj as System.Collections.ArrayList;
                    if (cases == null)
                    {
                        var arr = casesObj as object[];
                        if (arr != null) cases = new System.Collections.ArrayList(arr);
                    }
                    if (cases == null) continue;
                    foreach (object co in cases)
                    {
                        var c = co as Dictionary<string, object>;
                        if (c == null) continue;
                        string cid = GetStr(c, "id");
                        if (string.IsNullOrEmpty(cid)) cid = GetStr(c, "name");
                        string status = (GetStr(c, "status") ?? GetStr(c, "result") ?? "").ToLowerInvariant();
                        string result = "INFO";
                        if (status == "pass" || status == "passed" || status == "ok") result = "PASS";
                        else if (status == "fail" || status == "failed" || status == "error") result = "FAIL";
                        else if (status == "skip" || status == "skipped" || status == "warn") result = "SKIP";
                        rows.Add(new VerificationResultRow
                        {
                            Category = "模块用例",
                            Name = mid + "/" + cid,
                            Result = result,
                            Detail = GetStr(c, "summary")
                        });
                    }
                }
                catch
                {
                    // ignore single module parse errors
                }
            }
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v);
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v), out b) && b;
        }

        public static void BindListView(ListView listView, IList<VerificationResultRow> rows)
        {
            listView.BeginUpdate();
            listView.Items.Clear();
            foreach (VerificationResultRow row in rows)
            {
                var item = new ListViewItem(row.Category);
                item.SubItems.Add(row.Name);
                item.SubItems.Add(row.Result);
                item.SubItems.Add(row.Detail);
                item.ForeColor = GetResultColor(row.Result);
                listView.Items.Add(item);
            }
            listView.EndUpdate();
        }

        private static VerificationResultRow ParseGlobalCheckLine(string line)
        {
            string result = "INFO";
            if (line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
            {
                result = "PASS";
            }
            else if (line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                result = "FAIL";
            }
            return new VerificationResultRow
            {
                Category = "系统状态",
                Name = line.Length > 5 ? line.Substring(5).Trim() : line,
                Result = result,
                Detail = line
            };
        }

        private static Color GetResultColor(string result)
        {
            if (string.Equals(result, "PASS", StringComparison.OrdinalIgnoreCase))
            {
                return Color.DarkGreen;
            }
            if (string.Equals(result, "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                return Color.DarkRed;
            }
            if (string.Equals(result, "SKIP", StringComparison.OrdinalIgnoreCase))
            {
                return Color.Gray;
            }
            if (string.Equals(result, "BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                return Color.DarkOrange;
            }
            return Color.Black;
        }
    }
}
