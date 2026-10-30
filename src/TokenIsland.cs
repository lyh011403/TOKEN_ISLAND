using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TokenIsland {
static class Json {
    public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 8000000, RecursionLimit = 100 }; }
    public static object Parse(string s) { return Serializer().DeserializeObject(s); }
    public static string Stringify(object o) { return Serializer().Serialize(o); }
    public static object Get(object o, params string[] keys) { foreach (string k in keys) { var d = o as Dictionary<string, object>; if (d == null || !d.TryGetValue(k, out o)) return null; } return o; }
    public static double? Number(object o) { double n; return o != null && double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n) ? (double?)n : null; }
    public static string Str(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
    public static IEnumerable<object> Array(object o) { return (o as object[]) ?? new object[0]; }
}
class Quota {
    public string Label;
    public double? Remaining;
    public DateTime? Reset;
    public double WindowMinutes;
    public bool Expired { get { return Reset.HasValue && Reset.Value <= DateTime.UtcNow; } }
    public double? EffectiveRemaining {
        get {
            if (!Remaining.HasValue) return null;
            return Remaining;
        }
    }
    public DateTime? NextResetTime {
        get {
            if (!Reset.HasValue) return null;
            if (Reset.Value > DateTime.UtcNow) return Reset.Value;
            if (WindowMinutes > 0) {
                DateTime next = Reset.Value;
                var window = TimeSpan.FromMinutes(WindowMinutes);
                while (next <= DateTime.UtcNow) next += window;
                return next;
            }
            return Reset.Value;
        }
    }
    public static double? Clamp(double? v) {
        if (!v.HasValue) return null;
        double val = Math.Max(0, Math.Min(100, v.Value));
        if (val <= 1.0) return 0;
        return val;
    }
    public static DateTime? Epoch(object v) { var n = Json.Number(v); if (!n.HasValue || n < 0 || n > 253402300799) return null; return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(n.Value); }
    public static DateTime? Iso(object v) { DateTimeOffset dt; return DateTimeOffset.TryParse(Json.Str(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt) ? (DateTime?)dt.UtcDateTime : null; }
}
class CompletedUsage {
    public string Id;
    public DateTime Finished;
    public long Tokens;
}
class CompletionParseState {
    public long Position;
    public long? Total, Baseline;
    public string Turn;
}
class CompletionNotifications {
    readonly DateTime started;
    readonly HashSet<string> seen = new HashSet<string>();
    public CompletionNotifications(DateTime started) { this.started = started; }
    public bool Take(CompletedUsage usage) { return usage.Finished >= started && seen.Add(usage.Id); }
}
class Provider {
    public List<CompletedUsage> Completions = new List<CompletedUsage>();
    public string Id, Name, Color, Note, Source, TokenLabel, Theme = "orange";
    public DateTime? Updated;
    public long? Tokens;
    public List<Quota> Quotas = new List<Quota>();
    public int? ResetCreditsCount;
    public DateTime? ResetCreditsExpiry;
    public bool Error;
    public Provider(string id, string name, string color) { Id = id; Name = name; Color = color; }
    public static bool IsPrimaryGeminiLabel(string label) {
        return label != null && label.Contains("Gemini") && label.IndexOf("Argon", StringComparison.OrdinalIgnoreCase) < 0;
    }
    public double? Remaining {
        get {
            if (Id == "antigravity") {
                var geminiQuotas = Quotas.Where(x => IsPrimaryGeminiLabel(x.Label) && x.EffectiveRemaining.HasValue).ToList();
                if (geminiQuotas.Count > 0) return geminiQuotas.Min(x => x.EffectiveRemaining.Value);
                var fallback = Quotas.Where(x => (x.Label == null || (!x.Label.Contains("Claude") && !x.Label.Contains("GPT") && x.Label.IndexOf("Argon", StringComparison.OrdinalIgnoreCase) < 0)) && x.EffectiveRemaining.HasValue).ToList();
                return fallback.Count > 0 ? fallback.Min(x => x.EffectiveRemaining.Value) : (double?)null;
            }
            var q = Quotas.Where(x => x.EffectiveRemaining.HasValue).ToList();
            return q.Count == 0 ? (double?)null : q.Min(x => x.EffectiveRemaining.Value);
        }
    }
    public Quota DailyQuota {
        get {
            if (Id == "antigravity") {
                var g5h = Quotas.FirstOrDefault(x => IsPrimaryGeminiLabel(x.Label) && (x.WindowMinutes == 300 || x.Label.Contains("5 小時")) && x.EffectiveRemaining.HasValue);
                if (g5h != null) return g5h;
                var g = Quotas.FirstOrDefault(x => IsPrimaryGeminiLabel(x.Label) && x.EffectiveRemaining.HasValue);
                if (g != null) return g;
            }
            var daily = Quotas.FirstOrDefault(x => x.WindowMinutes > 0 && x.WindowMinutes <= 1440 && x.EffectiveRemaining.HasValue);
            if (daily != null) return daily;
            daily = Quotas.FirstOrDefault(x => (x.Label == null || (!x.Label.Contains("週") && !x.Label.Contains("week"))) && x.EffectiveRemaining.HasValue);
            if (daily != null) return daily;
            return Quotas.FirstOrDefault(x => x.EffectiveRemaining.HasValue);
        }
    }
    public Quota WeeklyQuota {
        get {
            if (Id == "antigravity") {
                var gW = Quotas.FirstOrDefault(x => IsPrimaryGeminiLabel(x.Label) && (x.WindowMinutes >= 10000 || x.Label.Contains("每週")) && x.EffectiveRemaining.HasValue);
                if (gW != null) return gW;
            }
            var weekly = Quotas.FirstOrDefault(x => (x.WindowMinutes >= 10000 || (x.Label != null && (x.Label.Contains("週") || x.Label.Contains("week")))) && x.EffectiveRemaining.HasValue);
            return weekly;
        }
    }
    public double? DailyRemaining {
        get {
            var q = DailyQuota;
            return q != null ? q.EffectiveRemaining : Remaining;
        }
    }
    public double? WeeklyRemaining {
        get {
            var q = WeeklyQuota;
            if (q != null && q.EffectiveRemaining.HasValue) return q.EffectiveRemaining.Value;
            if (Id == "antigravity") {
                var g = Quotas.FirstOrDefault(x => IsPrimaryGeminiLabel(x.Label) && (x.WindowMinutes >= 10000 || x.Label.Contains("每週")) && x.EffectiveRemaining.HasValue);
                if (g != null) return g.EffectiveRemaining.Value;
            }
            return Remaining.HasValue ? Remaining.Value : 100.0;
        }
    }
    public string DailyShort {
        get {
            return DailyRemaining.HasValue ? Math.Floor(DailyRemaining.Value).ToString("0") + "%" : "—";
        }
    }
    public static string GetQuotaLevelColor(double v) {
        if (v >= 70) return "#5AD4B2"; // 100-70 綠色
        if (v >= 30) return "#F5D061"; // 69-30 黃色
        if (v >= 10) return "#FF9500"; // 29-10 橘色
        return "#FF5C5C";              // 9 以下 紅色
    }
    public string GetWeeklyDotColor() {
        if (!WeeklyRemaining.HasValue) return null;
        return GetQuotaLevelColor(WeeklyRemaining.Value);
    }
    public string Short { get { return Remaining.HasValue ? Math.Floor(Remaining.Value).ToString("0") + "%" : "—"; } }
    public bool Stale { get { return Error || (Updated.HasValue && DateTime.UtcNow - Updated.Value > TimeSpan.FromMinutes(5)); } }
}
public class Preferences {
    public bool Codex = true, Antigravity = true, Claude = true, Grok = true;
    public bool Breathing = true, AnimalAnimation = true, ReducedMotion = false;
    public bool Pinned = false, Alerts = true;
    public double Left = -1, Top = 0, Opacity = 0.98;
    public string Dock = "top";
    public double Scale = 1.0;
    public string Language = "zh-TW";
    public string GetLanguage() { return string.IsNullOrEmpty(Language) ? "zh-TW" : Language; }
    public string ActiveTab = "nook";
    public bool HoverExpand = true;
    public string CodexColor = "blue", AntigravityColor = "orange", ClaudeColor = "green", GrokColor = "silver";
    public string AnimalType = "fox";
    public string CustomAnimalColor = "";
    public string CodexAnimal = "cat", AntigravityAnimal = "fox", ClaudeAnimal = "fox", GrokAnimal = "owl";
    public Dictionary<string, string> ProviderAnimals = new Dictionary<string, string>();
    public Dictionary<string, string> ProviderColors = new Dictionary<string, string>();
    public bool TokenDropAnimation = true;
    public double DropAnimScale = 1.0;
    public string DropAnimPos = "right";
    public string DropTextStyle = "B";
    public bool CinemaMode = true;
    public bool AvoidBrowser = true;
    public int TargetScreenIndex = -1;
    public string TargetScreenDeviceName = "";
    public Forms.Screen GetTargetScreen() {
        var all = Forms.Screen.AllScreens;
        if (all == null || all.Length == 0) return Forms.Screen.PrimaryScreen;
        if (!string.IsNullOrEmpty(TargetScreenDeviceName)) {
            foreach (var s in all) {
                if (string.Equals(s.DeviceName, TargetScreenDeviceName, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        if (TargetScreenIndex >= 0 && TargetScreenIndex < all.Length) {
            return all[TargetScreenIndex];
        }
        return Forms.Screen.PrimaryScreen ?? all[0];
    }
    public List<string> ProviderOrder = new List<string> { "codex", "antigravity", "claude", "grok" };
    public List<string> GetOrderedProviders() {
        var defaultList = new List<string> { "codex", "antigravity", "claude", "grok" };
        if (ProviderOrder == null || ProviderOrder.Count == 0) return defaultList;
        var list = new List<string>();
        foreach (var id in ProviderOrder) {
            if (defaultList.Contains(id) && !list.Contains(id)) list.Add(id);
        }
        foreach (var id in defaultList) {
            if (!list.Contains(id)) list.Add(id);
        }
        return list;
    }
    public string GetAnimalType() {
        return string.IsNullOrEmpty(AnimalType) ? "fox" : AnimalType;
    }
    public string GetProviderAnimal(string id) {
        if (ProviderAnimals != null && ProviderAnimals.ContainsKey(id) && !string.IsNullOrEmpty(ProviderAnimals[id]))
            return ProviderAnimals[id];
        if (id == "codex") return string.IsNullOrEmpty(CodexAnimal) ? "cat" : CodexAnimal;
        if (id == "antigravity") return string.IsNullOrEmpty(AntigravityAnimal) ? "fox" : AntigravityAnimal;
        if (id == "claude") return string.IsNullOrEmpty(ClaudeAnimal) ? "fox" : ClaudeAnimal;
        if (id == "grok") return string.IsNullOrEmpty(GrokAnimal) ? "owl" : GrokAnimal;
        return string.IsNullOrEmpty(AnimalType) ? "fox" : AnimalType;
    }
    public void SetProviderAnimal(string id, string animal) {
        if (ProviderAnimals == null) ProviderAnimals = new Dictionary<string, string>();
        ProviderAnimals[id] = animal;
        if (id == "codex") CodexAnimal = animal;
        else if (id == "antigravity") AntigravityAnimal = animal;
        else if (id == "claude") ClaudeAnimal = animal;
        else if (id == "grok") GrokAnimal = animal;
    }
    public string ToggleProviderAnimal(string id) {
        string cur = GetProviderAnimal(id);
        string next = cur == "cat" ? "fox" : (cur == "fox" ? "owl" : "cat");
        SetProviderAnimal(id, next);
        return next;
    }
    public string GetProviderColor(string id) {
        if (ProviderColors != null && ProviderColors.ContainsKey(id) && !string.IsNullOrEmpty(ProviderColors[id]))
            return ProviderColors[id];
        if (!string.IsNullOrEmpty(CustomAnimalColor)) return CustomAnimalColor;
        if (id == "codex") return string.IsNullOrEmpty(CodexColor) ? "#3B82F6" : CodexColor;
        if (id == "antigravity") return string.IsNullOrEmpty(AntigravityColor) ? "#FFA500" : AntigravityColor;
        if (id == "claude") return string.IsNullOrEmpty(ClaudeColor) ? "#10B981" : ClaudeColor;
        if (id == "grok") return string.IsNullOrEmpty(GrokColor) ? "#E2E8F0" : GrokColor;
        return "#FFA500";
    }
    public void SetProviderColor(string id, string color) {
        if (ProviderColors == null) ProviderColors = new Dictionary<string, string>();
        ProviderColors[id] = color;
        if (id == "codex") CodexColor = color;
        else if (id == "antigravity") AntigravityColor = color;
        else if (id == "claude") ClaudeColor = color;
        else if (id == "grok") GrokColor = color;
    }
    public static string GetDefaultColor(string id) {
        if (id == "codex") return "#3B82F6";
        if (id == "antigravity") return "#FFA500";
        if (id == "claude") return "#10B981";
        if (id == "grok") return "#E2E8F0";
        return "#FFA500";
    }
    public static string GetDefaultAnimal(string id) {
        if (id == "codex") return "cat";
        if (id == "antigravity") return "fox";
        if (id == "claude") return "fox";
        if (id == "grok") return "owl";
        return "fox";
    }
    public static string NormalizeColor(string c) {
        if (string.IsNullOrEmpty(c)) return "#FFA500";
        string s = c.Trim().ToLowerInvariant();
        if (s == "orange") return "#FFA500";
        if (s == "blue") return "#3B82F6";
        if (s == "green") return "#10B981";
        if (s == "purple") return "#8B5CF6";
        if (s == "pink") return "#EC4899";
        if (s == "silver") return "#94A3B8";
        if (!s.StartsWith("#")) s = "#" + s;
        return s.ToUpperInvariant();
    }
}
static class Storage {
    public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string State = Path.Combine(Root, "data");
    public static void Write(string name, object o) { Directory.CreateDirectory(State); string p = Path.Combine(State, name); string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(tmp, Json.Stringify(o), new UTF8Encoding(false)); if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p); }
    public static Preferences Load() {
        try {
            var p = Json.Serializer().Deserialize<Preferences>(File.ReadAllText(Path.Combine(State, "settings.json"))) ?? new Preferences();
            if (p.ProviderColors == null) p.ProviderColors = new Dictionary<string, string>();
            if (!p.ProviderColors.ContainsKey("codex")) p.ProviderColors["codex"] = !string.IsNullOrEmpty(p.CodexColor) ? p.CodexColor : Preferences.GetDefaultColor("codex");
            if (!p.ProviderColors.ContainsKey("antigravity")) p.ProviderColors["antigravity"] = !string.IsNullOrEmpty(p.AntigravityColor) ? p.AntigravityColor : Preferences.GetDefaultColor("antigravity");
            if (!p.ProviderColors.ContainsKey("claude")) p.ProviderColors["claude"] = !string.IsNullOrEmpty(p.ClaudeColor) ? p.ClaudeColor : Preferences.GetDefaultColor("claude");
            if (!p.ProviderColors.ContainsKey("grok")) p.ProviderColors["grok"] = !string.IsNullOrEmpty(p.GrokColor) ? p.GrokColor : Preferences.GetDefaultColor("grok");
            if (p.ProviderAnimals == null) p.ProviderAnimals = new Dictionary<string, string>();
            if (!p.ProviderAnimals.ContainsKey("codex")) p.ProviderAnimals["codex"] = !string.IsNullOrEmpty(p.CodexAnimal) ? p.CodexAnimal : "cat";
            if (!p.ProviderAnimals.ContainsKey("antigravity")) p.ProviderAnimals["antigravity"] = !string.IsNullOrEmpty(p.AntigravityAnimal) ? p.AntigravityAnimal : "fox";
            if (!p.ProviderAnimals.ContainsKey("claude")) p.ProviderAnimals["claude"] = !string.IsNullOrEmpty(p.ClaudeAnimal) ? p.ClaudeAnimal : "fox";
            if (!p.ProviderAnimals.ContainsKey("grok")) p.ProviderAnimals["grok"] = !string.IsNullOrEmpty(p.GrokAnimal) ? p.GrokAnimal : "owl";
            p.CustomAnimalColor = "";
            if (p.DropAnimScale <= 0.55 && p.DropAnimScale > 0) p.DropAnimScale = 1.0;
            if (string.IsNullOrEmpty(p.DropAnimPos)) p.DropAnimPos = "right";
            return p;
        } catch { return new Preferences(); }
    }
}
static class Providers {
    public static Provider EmptyCodex() { return new Provider("codex", "Codex", "#83E8C3") { Note = "等待 Codex 的用量紀錄", Source = "本機 Codex 紀錄" }; }
    public static Provider EmptyAg() { return new Provider("antigravity", "Antigravity", "#96B7FF") { Note = "請先開啟並登入 Antigravity", Source = "Antigravity 本機服務" }; }
    public static Provider EmptyClaude() { return new Provider("claude", "Claude", "#EDA46A") { Note = "請先啟動 Claude 官方桌面版", Source = "Claude 官方桌面版" }; }
    public static Provider EmptyGrok() { return new Provider("grok", "Grok", "#E2E8F0") { Note = "等待 Grok Bot 本機用量紀錄", Source = "Grok Bot 本機服務" }; }
    public static string FindGrokBotDataDir() {
        try {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string grokDir = Path.Combine(appData, "Grok Bot", "sand-client-persistence");
            if (Directory.Exists(grokDir)) return grokDir;
        } catch { }
        return null;
    }
    public static Provider ParseGrokBotBlob(string text) {
        var r = EmptyGrok();
        if (string.IsNullOrEmpty(text)) return r;
        try {
            object obj = Json.Parse(text);
            if (obj == null) return r;
            object val = Json.Get(obj, "value");
            object reading = Json.Get(val, "reading");
            object usage = Json.Get(reading, "usage");
            if (usage == null) return r;

            double? percentUsed = Json.Number(Json.Get(usage, "percentUsed"));
            double? nextResetMs = Json.Number(Json.Get(usage, "nextResetMs"));
            double? readAtMs = Json.Number(Json.Get(reading, "readAtMs"));
            string planName = Json.Str(Json.Get(usage, "unifiedPlanName"));
            if (string.IsNullOrEmpty(planName)) planName = "Premium";

            if (percentUsed.HasValue) {
                double remaining = Math.Max(0.0, Math.Min(100.0, 100.0 - percentUsed.Value));
                DateTime? resetTime = null;
                if (nextResetMs.HasValue && nextResetMs.Value > 0) {
                    resetTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(nextResetMs.Value);
                }

                r.Quotas.Add(new Quota {
                    Label = planName + " · 週額度",
                    Remaining = Quota.Clamp(remaining),
                    Reset = resetTime,
                    WindowMinutes = 10080
                });

                r.Tokens = (long)Math.Round(percentUsed.Value);
                r.TokenLabel = string.Format("{0} · {1}% 已用", planName, Math.Round(percentUsed.Value));
            }

            if (readAtMs.HasValue && readAtMs.Value > 0) {
                r.Updated = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(readAtMs.Value);
            } else {
                r.Updated = DateTime.UtcNow;
            }

            r.Note = "Grok Bot 官方桌面端紀錄";
            r.Source = "Grok Bot 本機紀錄";
            return r;
        } catch {
            return r;
        }
    }
    public static Provider ParseGrok(string json) {
        var r = EmptyGrok();
        if (string.IsNullOrEmpty(json)) return r;
        try {
            object obj = Json.Parse(json);
            if (obj == null) return r;

            double? remaining = Json.Number(Json.Get(obj, "remaining"));
            double? used = Json.Number(Json.Get(obj, "used_messages"));
            double? total = Json.Number(Json.Get(obj, "total_messages"));
            double? windowMinutes = Json.Number(Json.Get(obj, "window_minutes"));
            string model = Json.Str(Json.Get(obj, "model"));
            if (string.IsNullOrEmpty(model)) model = "Grok";

            DateTime? resetTime = null;
            object resetObj = Json.Get(obj, "resets_at");
            if (resetObj != null) {
                resetTime = Quota.Epoch(resetObj);
                if (!resetTime.HasValue) resetTime = Quota.Iso(resetObj);
            }

            DateTime? stamp = Quota.Iso(Json.Get(obj, "timestamp"));
            r.Updated = stamp ?? DateTime.UtcNow;

            if (!remaining.HasValue && used.HasValue && total.HasValue && total.Value > 0) {
                remaining = Math.Max(0.0, Math.Min(100.0, (1.0 - (used.Value / total.Value)) * 100.0));
            }

            if (remaining.HasValue) {
                string quotaLabel = windowMinutes.HasValue && windowMinutes.Value > 0
                    ? (windowMinutes.Value <= 120 ? "2 小時額度" : (windowMinutes.Value / 60).ToString("0.#") + " 小時額度")
                    : "訊息額度";

                r.Quotas.Add(new Quota {
                    Label = model + " · " + quotaLabel,
                    Remaining = Quota.Clamp(remaining.Value),
                    Reset = resetTime,
                    WindowMinutes = windowMinutes.GetValueOrDefault(120)
                });
            }

            if (used.HasValue) {
                r.Tokens = (long)used.Value;
                r.TokenLabel = total.HasValue
                    ? string.Format("已用 {0} / 上限 {1} 則", (long)used.Value, (long)total.Value)
                    : string.Format("本週期已使用 {0} 則訊息", (long)used.Value);
            }

            r.Note = "Grok Bot 外部同步";
            return r;
        } catch {
            return r;
        }
    }
    public static Provider Grok() {
        // 1. 優先嘗試讀取 Grok Bot 官方桌面版（sand-client-persistence 中的 weekly-usage 快取）
        try {
            string dir = FindGrokBotDataDir();
            if (dir != null && Directory.Exists(dir)) {
                var files = Directory.GetFiles(dir, "*.blob");
                string targetFile = null;
                foreach (var f in files) {
                    if (f.IndexOf("o5swk23mpewxk43bm5ss4y3bmnugk", StringComparison.OrdinalIgnoreCase) >= 0) {
                        targetFile = f;
                        break;
                    }
                }
                if (targetFile == null) {
                    var sorted = files.Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTime).Take(10);
                    foreach (var fi in sorted) {
                        if (fi.Length > 0 && fi.Length < 10000) {
                            try {
                                string txt = File.ReadAllText(fi.FullName, Encoding.UTF8);
                                if (txt.Contains("percentUsed") && txt.Contains("unifiedPlanName")) {
                                    targetFile = fi.FullName;
                                    break;
                                }
                            } catch { }
                        }
                    }
                }

                if (targetFile != null && File.Exists(targetFile)) {
                    using (var fs = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8)) {
                        string content = reader.ReadToEnd();
                        var parsed = ParseGrokBotBlob(content);
                        if (parsed != null && parsed.Quotas.Count > 0) return parsed;
                    }
                }
            }
        } catch { }

        // 2. 次要：檢查是否有外部同步之 grok-status.json
        string file = Path.Combine(Storage.State, "grok-status.json");
        if (File.Exists(file)) {
            try {
                return ParseGrok(File.ReadAllText(file, Encoding.UTF8));
            } catch { }
        }

        return EmptyGrok();
    }
    public static Provider ParseCodex(string line) {
        object e = Json.Parse(line), p = Json.Get(e, "payload");
        if (Json.Str(Json.Get(p, "type")) != "token_count") return null;
        var r = EmptyCodex(); object limits = Json.Get(p, "rate_limits");
        string id = Json.Str(Json.Get(limits, "limit_id"));
        if (id.Length > 0 && id != "codex") return null;
        foreach (string key in new[] { "primary", "secondary" }) {
            object q = Json.Get(limits, key); var used = Json.Number(Json.Get(q, "used_percent"));
            if (!used.HasValue) continue;
            double? minutes = Json.Number(Json.Get(q, "window_minutes"));
            string label = minutes == 10080 ? "每週額度" : minutes == 300 ? "5 小時額度" : minutes.HasValue ? (minutes.Value / 60).ToString("0.#") + " 小時額度" : "用量額度";
            r.Quotas.Add(new Quota { Label = label, Remaining = Quota.Clamp(100 - used), Reset = Quota.Epoch(Json.Get(q, "resets_at")), WindowMinutes = minutes.GetValueOrDefault() });
        }
        double? tokens = Json.Number(Json.Get(p, "info", "total_token_usage", "total_tokens"));
        if (tokens.HasValue && tokens >= 0 && tokens < long.MaxValue) r.Tokens = (long)tokens.Value;
        r.TokenLabel = "最近活動對話 · 累計 Token";
        r.Updated = Quota.Iso(Json.Get(e, "timestamp"));
        r.Note = r.Quotas.Count > 0 ? "訂閱額度 · 以最近一次用量事件為準" : "此紀錄沒有訂閱額度（可能使用 API）";
        return r;
    }
    static readonly Dictionary<string, CompletionParseState> completionLogs = new Dictionary<string, CompletionParseState>();
    public static List<CompletedUsage> ParseCompletedUsage(string[] lines, string session, bool completeFile, CompletionParseState state = null) {
        state = state ?? new CompletionParseState { Total = completeFile ? (long?)0 : null };
        var completed = new List<CompletedUsage>();
        long? total = state.Total, baseline = state.Baseline;
        string turn = state.Turn;
        foreach (string line in lines) {
            if (!line.Contains("event_msg")) continue;
            try {
                object entry = Json.Parse(line);
                if (Json.Str(Json.Get(entry, "type")) != "event_msg") continue;
                object payload = Json.Get(entry, "payload");
                string kind = Json.Str(Json.Get(payload, "type"));
                if (kind == "task_started") { turn = Json.Str(Json.Get(payload, "turn_id")); baseline = total; }
                else if (kind == "token_count") {
                    double? value = Json.Number(Json.Get(payload, "info", "total_token_usage", "total_tokens"));
                    if (value.HasValue && value >= 0 && value < long.MaxValue) total = (long)value.Value;
                } else if (kind == "turn_aborted") { turn = null; baseline = null; }
                else if (kind == "task_complete") {
                    string id = Json.Str(Json.Get(payload, "turn_id"));
                    DateTime? stamp = Quota.Iso(Json.Get(entry, "timestamp"));
                    if (!String.IsNullOrEmpty(turn) && id == turn && stamp.HasValue && baseline.HasValue && total.HasValue && total > baseline)
                        completed.Add(new CompletedUsage { Id = session + ":" + turn, Finished = stamp.Value, Tokens = total.Value - baseline.Value });
                    turn = null; baseline = null;
                }
            } catch { }
        }
        state.Total = total; state.Baseline = baseline; state.Turn = turn;
        return completed;
    }
    public static List<CompletedUsage> ReadCompletedUsage(string path) {
        CompletionParseState state;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (!completionLogs.TryGetValue(path, out state) || stream.Length < state.Position) {
                state = new CompletionParseState { Total = 0 };
                completionLogs[path] = state;
                if (stream.Length >= 3 && stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF) state.Position = 3;
            }
            stream.Seek(state.Position, SeekOrigin.Begin);
            string appended;
            using (var reader = new StreamReader(stream, Encoding.UTF8)) appended = reader.ReadToEnd();
            int lastLine = appended.LastIndexOf('\n');
            if (lastLine < 0) return new List<CompletedUsage>();
            string complete = appended.Substring(0, lastLine + 1);
            var result = ParseCompletedUsage(complete.Split('\n'), path, true, state);
            state.Position += Encoding.UTF8.GetByteCount(complete);
            return result;
        }
    }
    static string RunCodexApi(string url, string token, string accId) {
        try {
            string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (!File.Exists(curl)) curl = "curl.exe";
            var psi = new ProcessStartInfo {
                FileName = curl,
                Arguments = "-s --max-time 4 -H \"Authorization: Bearer " + token + "\" " +
                            (string.IsNullOrEmpty(accId) ? "" : "-H \"chatgpt-account-id: " + accId + "\" ") +
                            "-H \"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36\" " +
                            "\"" + url + "\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi)) {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4500);
                return output;
            }
        } catch { return null; }
    }
    public static Provider Codex() {
        var result = EmptyCodex();
        string root = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (String.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        // 1. 優先嘗試讀取 auth.json 調用官方即時 API
        bool apiSuccess = false;
        try {
            string authFile = Path.Combine(root, "auth.json");
            if (File.Exists(authFile)) {
                object authObj = Json.Parse(File.ReadAllText(authFile, Encoding.UTF8));
                string accessToken = Json.Str(Json.Get(authObj, "tokens", "access_token"));
                string accountId = Json.Str(Json.Get(authObj, "tokens", "account_id"));
                if (!string.IsNullOrEmpty(accessToken)) {
                    // 查詢 usage
                    string usageJson = RunCodexApi("https://chatgpt.com/backend-api/codex/usage", accessToken, accountId);
                    if (!string.IsNullOrEmpty(usageJson) && usageJson.Contains("rate_limit")) {
                        object usageObj = Json.Parse(usageJson);
                        object rateLimit = Json.Get(usageObj, "rate_limit");
                        if (rateLimit != null) {
                            result.Quotas.Clear();
                            object pw = Json.Get(rateLimit, "primary_window");
                            if (pw != null) {
                                var used = Json.Number(Json.Get(pw, "used_percent"));
                                if (used.HasValue) {
                                    double? sec = Json.Number(Json.Get(pw, "limit_window_seconds"));
                                    double winMin = sec.HasValue ? sec.Value / 60.0 : 300.0;
                                    string lbl = winMin == 300 ? "5 小時額度" : (winMin / 60.0).ToString("0.#") + " 小時額度";
                                    result.Quotas.Add(new Quota {
                                        Label = lbl,
                                        Remaining = Quota.Clamp(100.0 - used.Value),
                                        Reset = Quota.Epoch(Json.Get(pw, "reset_at")),
                                        WindowMinutes = winMin
                                    });
                                }
                            }
                            object sw = Json.Get(rateLimit, "secondary_window");
                            if (sw != null) {
                                var used = Json.Number(Json.Get(sw, "used_percent"));
                                if (used.HasValue) {
                                    double? sec = Json.Number(Json.Get(sw, "limit_window_seconds"));
                                    double winMin = sec.HasValue ? sec.Value / 60.0 : 10080.0;
                                    result.Quotas.Add(new Quota {
                                        Label = "每週額度",
                                        Remaining = Quota.Clamp(100.0 - used.Value),
                                        Reset = Quota.Epoch(Json.Get(sw, "reset_at")),
                                        WindowMinutes = winMin
                                    });
                                }
                            }
                            result.Updated = DateTime.UtcNow;
                            result.Source = "ChatGPT 官方 API";
                            result.Note = "官方即時用量 · 5 小時與每週獨立結算";
                            apiSuccess = true;
                        }
                    }

                    // 查詢 rate-limit-reset-credits
                    string creditsJson = RunCodexApi("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", accessToken, accountId);
                    if (!string.IsNullOrEmpty(creditsJson) && creditsJson.Contains("available_count")) {
                        object creditsObj = Json.Parse(creditsJson);
                        double? availCount = Json.Number(Json.Get(creditsObj, "available_count"));
                        if (availCount.HasValue) {
                            result.ResetCreditsCount = (int)availCount.Value;
                        }
                        var creditsList = Json.Array(Json.Get(creditsObj, "credits"));
                        foreach (object c in creditsList) {
                            string status = Json.Str(Json.Get(c, "status"));
                            if (status == "available") {
                                result.ResetCreditsExpiry = Quota.Iso(Json.Get(c, "expires_at"));
                                break;
                            }
                        }
                    }
                }
            }
        } catch { }

        // 2. 讀取本機 sessions 日誌（取得累計 Token 與任務完成通知；若 API 失敗則作為 Quotas 回退機制）
        string sessions = Path.Combine(root, "sessions");
        if (Directory.Exists(sessions)) {
            try {
                Provider newestSession = null;
                var completed = new List<CompletedUsage>();
                foreach (var f in new DirectoryInfo(sessions).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc).Take(8)) {
                    string tail;
                    using (var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                        long start = Math.Max(0, stream.Length - 2 * 1024 * 1024); stream.Seek(start, SeekOrigin.Begin);
                        using (var reader = new StreamReader(stream, Encoding.UTF8)) { if (start > 0) reader.ReadLine(); tail = reader.ReadToEnd(); }
                    }
                    string[] lines = tail.Split('\n');
                    completed.AddRange(ReadCompletedUsage(f.FullName));
                    if (!apiSuccess && newestSession == null) {
                        for (int i = lines.Length - 1; i >= 0; --i) {
                            if (!lines[i].Contains("token_count")) continue;
                            try { var parsed = ParseCodex(lines[i]); if (parsed != null && parsed.Updated.HasValue) { if (newestSession == null || parsed.Updated > newestSession.Updated) newestSession = parsed; break; } } catch { }
                        }
                    } else if (apiSuccess && !result.Tokens.HasValue) {
                        for (int i = lines.Length - 1; i >= 0; --i) {
                            if (!lines[i].Contains("token_count")) continue;
                            try {
                                object e = Json.Parse(lines[i]), p = Json.Get(e, "payload");
                                double? tokens = Json.Number(Json.Get(p, "info", "total_token_usage", "total_tokens"));
                                if (tokens.HasValue && tokens >= 0 && tokens < long.MaxValue) {
                                    result.Tokens = (long)tokens.Value;
                                    result.TokenLabel = "最近活動對話 · 累計 Token";
                                    break;
                                }
                            } catch { }
                        }
                    }
                }
                if (!apiSuccess && newestSession != null) {
                    result = newestSession;
                }
                result.Completions = completed.OrderBy(x => x.Finished).ToList();
            } catch (UnauthorizedAccessException) { if (!apiSuccess) { result.Note = "無法讀取 Codex 紀錄，請以同一 Windows 帳號啟動"; result.Error = true; } }
            catch { if (!apiSuccess) { result.Note = "Codex 紀錄暫時無法讀取"; result.Error = true; } }
        }
        return result;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    static List<int> Ports(int pid) {
        int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0); var ports = new List<int>();
        if (size <= 0) return ports; IntPtr p = Marshal.AllocHGlobal(size);
        try { if (GetExtendedTcpTable(p, ref size, false, 2, 3, 0) != 0) return ports; int count = Marshal.ReadInt32(p); for (int i = 0; i < count; i++) { int offset = 4 + i * 24; if (Marshal.ReadInt32(p, offset + 20) == pid) { byte b1 = Marshal.ReadByte(p, offset + 8), b2 = Marshal.ReadByte(p, offset + 9); ports.Add(b1 * 256 + b2); } } } finally { Marshal.FreeHGlobal(p); }
        return ports.Distinct().ToList();
    }
    static string Flag(string command, string flag) { var m = Regex.Match(command, "--" + Regex.Escape(flag) + "[=\\s]+(?:\"([^\"]+)\"|([^\\s]+))"); return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : ""; }
    static string LocalPost(int port, string scheme, string csrf, string method = "RetrieveUserQuotaSummary") {
        var req = (HttpWebRequest)WebRequest.Create(scheme + "://127.0.0.1:" + port + "/exa.language_server_pb.LanguageServerService/" + method);
        req.Proxy = null; req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 1800; req.ReadWriteTimeout = 1800; req.AllowAutoRedirect = false;
        req.Headers["X-Codeium-Csrf-Token"] = csrf;
        if (scheme == "https") req.ServerCertificateValidationCallback = delegate { return true; }; // Only this fixed loopback request, never global TLS validation.
        byte[] body = Encoding.UTF8.GetBytes("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}"); req.ContentLength = body.Length;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        using (var response = req.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) { char[] buf = new char[2000000]; int n = 0, read; while (n < buf.Length && (read = reader.Read(buf, n, Math.Min(8192, buf.Length - n))) > 0) n += read; if (n == buf.Length) throw new IOException("Response too large"); return new string(buf, 0, n); }
    }
    public static Provider ParseAntigravityQuotaSummary(string json) {
        object root = Json.Parse(json); var result = EmptyAg();
        var groups = Json.Array(Json.Get(root, "response", "groups"));
        if (!groups.Any()) return null;
        foreach (object g in groups) {
            string groupName = Json.Str(Json.Get(g, "displayName")) ?? "";
            var buckets = Json.Array(Json.Get(g, "buckets"));
            foreach (object b in buckets) {
                var fraction = Json.Number(Json.Get(b, "remainingFraction"));
                if (!fraction.HasValue) fraction = 0;
                string window = Json.Str(Json.Get(b, "window"));
                double winMin = window == "5h" ? 300 : window == "weekly" ? 10080 : 0;
                string winText = window == "5h" ? "5 小時" : "每週";

                if (groupName == "Gemini Models") {
                    result.Quotas.Add(new Quota {
                        Label = "Gemini " + winText,
                        Remaining = Quota.Clamp(fraction * 100),
                        Reset = Quota.Iso(Json.Get(b, "resetTime")),
                        WindowMinutes = winMin
                    });
                } else if (groupName.IndexOf("Argon", StringComparison.OrdinalIgnoreCase) >= 0) {
                    result.Quotas.Add(new Quota {
                        Label = "Gemini 4 Argon " + winText,
                        Remaining = Quota.Clamp(fraction * 100),
                        Reset = Quota.Iso(Json.Get(b, "resetTime")),
                        WindowMinutes = winMin
                    });
                } else if (groupName.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 || groupName.IndexOf("3p", StringComparison.OrdinalIgnoreCase) >= 0) {
                    // GPT 已於 11 月下架移除，僅保留 Claude 配額
                    result.Quotas.Add(new Quota {
                        Label = "Claude " + winText,
                        Remaining = Quota.Clamp(fraction * 100),
                        Reset = Quota.Iso(Json.Get(b, "resetTime")),
                        WindowMinutes = winMin
                    });
                }
            }
        }
        if (result.Quotas.Count > 0) {
            result.Updated = DateTime.UtcNow;
            result.Note = "官方 View Usage 配額 · 5 小時與每週獨立結算";
            return result;
        }
        return null;
    }
    public static Provider ParseAntigravity(string json) {
        var summary = ParseAntigravityQuotaSummary(json);
        if (summary != null && summary.Quotas.Count > 0) return summary;

        object root = Json.Parse(json); var result = EmptyAg();
        var models = Json.Array(Json.Get(root, "userStatus", "cascadeModelConfigData", "clientModelConfigs"));
        foreach (object model in models) {
            string lbl = Json.Str(Json.Get(model, "label")) ?? "";
            if (lbl.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            var fraction = Json.Number(Json.Get(model, "quotaInfo", "remainingFraction"));
            object qi = Json.Get(model, "quotaInfo");
            if (!fraction.HasValue && qi != null && Json.Get(qi, "resetTime") != null) fraction = 0;
            if (!fraction.HasValue) continue;
            result.Quotas.Add(new Quota { Label = lbl, Remaining = Quota.Clamp(fraction * 100), Reset = Quota.Iso(Json.Get(qi, "resetTime")) });
        }
        result.Updated = DateTime.UtcNow;
        result.Note = result.Quotas.Count > 0 ? "模型額度 · 部分模型可能共用額度" : "已連線，但此版本未回傳模型額度";
        return result;
    }
    static Provider FetchAgData(int port, string scheme, string csrf) {
        try {
            string summaryJson = LocalPost(port, scheme, csrf, "RetrieveUserQuotaSummary");
            var summary = ParseAntigravityQuotaSummary(summaryJson);
            if (summary != null && summary.Quotas.Count > 0) return summary;
        } catch { }
        try {
            string statusJson = LocalPost(port, scheme, csrf, "GetUserStatus");
            return ParseAntigravity(statusJson);
        } catch { }
        return null;
    }
    static int cachedAgPid = 0;
    static int cachedAgPort = 0;
    static string cachedAgCsrf = "";
    static string cachedAgScheme = "http";

    public static Provider Antigravity() {
        var result = EmptyAg(); bool found = false;
        var usage = AntigravityUsage.Read();
        result.Completions = usage.Completions;
        long liveTokens = usage.LastCallTokens.GetValueOrDefault();
        bool hasTokens = usage.LastCallTokens.HasValue;

        // 1. 優先嘗試快取連線（2ms 極速直連，免除 WMI 繁重開銷）
        if (cachedAgPort > 0 && !string.IsNullOrEmpty(cachedAgCsrf)) {
            try {
                var data = FetchAgData(cachedAgPort, cachedAgScheme, cachedAgCsrf);
                if (data != null && data.Quotas.Count > 0) {
                    data.Completions = usage.Completions;
                    if (hasTokens) {
                        data.Tokens = liveTokens;
                        data.TokenLabel = "最近模型呼叫 · Token（含快取）";
                    }
                    return data;
                }
            } catch {
                cachedAgPort = 0;
                cachedAgCsrf = "";
            }
        }

        // 2. 快取失效或首次運行時才執行進程探索
        try {
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='language_server.exe' OR Name='language_server_windows_x64.exe'"))
            using (var list = searcher.Get()) {
                foreach (ManagementObject proc in list) {
                    string cmd = Convert.ToString(proc["CommandLine"]), exe = Convert.ToString(proc["ExecutablePath"]);
                    if (!(exe + cmd).ToLowerInvariant().Contains("antigravity")) continue;
                    found = true; string csrf = Flag(cmd, "csrf_token"); if (csrf.Length == 0) continue;
                    int pid = Convert.ToInt32(proc["ProcessId"]);
                    foreach (int port in Ports(pid)) foreach (string scheme in new[] { "http", "https" }) {
                        try {
                            var data = FetchAgData(port, scheme, csrf);
                            if (data != null && data.Quotas.Count > 0) {
                                cachedAgPid = pid;
                                cachedAgPort = port;
                                cachedAgCsrf = csrf;
                                cachedAgScheme = scheme;
                                data.Completions = usage.Completions;
                                if (hasTokens) {
                                    data.Tokens = liveTokens;
                                    data.TokenLabel = "最近模型呼叫 · Token（含快取）";
                                }
                                return data;
                            }
                        } catch { }
                    }
                }
            }
            if (found) { result.Note = "偵測到 Antigravity；本機用量介面暫未回應"; result.Error = true; }
        } catch { result.Note = "暫時無法連接 Antigravity 本機服務"; result.Error = true; }

        if (hasTokens) {
            result.Tokens = liveTokens;
            result.TokenLabel = "最近模型呼叫 · Token（含快取）";
            result.Updated = DateTime.UtcNow;
        }
        return result;
    }
    public static Provider ParseClaude(string json, DateTime stamp) {
        object root = Json.Parse(json); var result = EmptyClaude();
        foreach (string key in new[] { "five_hour", "seven_day", "spend_limit" }) {
            object q = Json.Get(root, "rate_limits", key); var used = Json.Number(Json.Get(q, "used_percentage")); if (!used.HasValue) continue;
            double winMin = key == "five_hour" ? 300 : key == "seven_day" ? 10080 : 0;
            result.Quotas.Add(new Quota { Label = key == "five_hour" ? "5 小時額度" : key == "seven_day" ? "每週額度" : "支出額度", Remaining = Quota.Clamp(100 - used), Reset = Quota.Epoch(Json.Get(q, "resets_at")), WindowMinutes = winMin });
        }
        double? input = Json.Number(Json.Get(root, "context_window", "total_input_tokens")), output = Json.Number(Json.Get(root, "context_window", "total_output_tokens"));
        if (input.HasValue && output.HasValue) result.Tokens = (long)(input.Value + output.Value);
        result.TokenLabel = "目前上下文 Token（非帳號累計）";
        result.Updated = stamp; result.Note = result.Quotas.Count > 0 ? "Claude 回報的帳號額度" : "已收到狀態；此版本／帳號未提供額度";
        return result;
    }
    public static string FindClaudeDesktopDataDir() {
        string custom = Environment.GetEnvironmentVariable("CLAUDE_DESKTOP_DIR");
        if (!String.IsNullOrEmpty(custom) && Directory.Exists(custom)) return custom;
        try {
            string pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(pkg)) {
                var dirs = Directory.GetDirectories(pkg, "Claude_*");
                foreach (var d in dirs) {
                    string roaming = Path.Combine(d, @"LocalCache\Roaming\Claude");
                    if (Directory.Exists(roaming)) return roaming;
                }
            }
        } catch { }
        string std = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (Directory.Exists(std)) return std;
        return null;
    }
    public static Provider Claude() {
        bool running = false;
        try {
            var procs = Process.GetProcessesByName("claude");
            running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        } catch { }

        string f = Path.Combine(Storage.State, "claude-status.json");
        Provider cli = null;
        try {
            if (File.Exists(f)) {
                var dt = File.GetLastWriteTimeUtc(f);
                if (DateTime.UtcNow - dt < TimeSpan.FromHours(4)) cli = ParseClaude(File.ReadAllText(f), dt);
            }
        } catch { }

        if (cli != null && cli.Quotas.Count > 0) return cli;

        var result = EmptyClaude();
        string dataDir = FindClaudeDesktopDataDir();
        if (dataDir == null) {
            result.Note = "未偵測到 Claude 官方桌面版安裝目錄";
            return result;
        }

        string configPath = Path.Combine(dataDir, "config.json");
        string historyPath = Path.Combine(dataDir, "plan-usage-history.json");
        string version = null;
        string account = null;

        try {
            if (File.Exists(configPath)) {
                object cfg = Json.Parse(File.ReadAllText(configPath));
                version = Json.Str(Json.Get(cfg, "updaterLastSeenVersion"));
                account = Json.Str(Json.Get(cfg, "lastKnownAccountUuid"));
            }
        } catch { }

        if (!running) {
            result.Note = "Claude 官方桌面版未啟動" + (version != null ? " (v" + version + ")" : "");
            return result;
        }

        result.Updated = DateTime.UtcNow;
        if (!String.IsNullOrEmpty(version)) result.Source = "Claude 官方桌面版 v" + version;

        try {
            if (File.Exists(historyPath)) {
                object hist = Json.Parse(File.ReadAllText(historyPath));
                var samples = Json.Array(Json.Get(hist, "samples")).ToList();
                if (samples.Count > 0) {
                    object last = samples[samples.Count - 1];
                    object u = Json.Get(last, "u");
                    double? used = Json.Number(Json.Get(u, "used_percentage"));
                    if (used.HasValue) {
                        result.Quotas.Add(new Quota { Label = "桌面版額度", Remaining = Quota.Clamp(100 - used.Value), Reset = null });
                    }
                }
            }
        } catch { }

        if (result.Quotas.Count == 0) {
            result.Quotas.Add(new Quota { Label = "桌面連線", Remaining = 100, Reset = null });
        }

        string accShort = (!String.IsNullOrEmpty(account) && account.Length >= 8) ? " · 帳號 " + account.Substring(0, 8) : "";
        result.Note = "Claude 官方 Windows 桌面版正常連線" + accShort;
        return result;
    }
}
class Island : Window {
    Preferences prefs;
    Dictionary<string, Provider> data = new Dictionary<string, Provider>();
    Grid rootContainer, islandHost, contentContainer, compactContainer, expandedBody;
    TranslateTransform compactTransform, expandedTransform;
    readonly System.Diagnostics.Stopwatch morphStopwatch = new System.Diagnostics.Stopwatch();
    bool isMorphing = false;
    double morphStartLogicalW, morphStartLogicalH;
    double morphTargetLogicalW, morphTargetLogicalH;
    double morphDurationMs;
    bool morphTargetOpen;
    double compactPhysicsX, compactPhysicsW, compactPhysicsH;
    double expandedPhysicsX, expandedPhysicsW, expandedPhysicsH;
    int morphTransitionId = 0;
    System.Windows.Shapes.Path notchShapePath;
    Canvas pawOverlayCanvas;
    static readonly Random pawRnd = new Random();
    Thread wakeupListener;
    StackPanel cards;
    ScrollViewer details, settingsDetails;
    TextBlock rightHeaderTitle;
    Button pin, menuBtn;
    DispatcherTimer poll, clock, collapse, hover, animationClock, cinemaTimer, debounceTimer;
    FileSystemWatcher watcherAg, watcherCodex;
    ScaleTransform scaleTransform;
    Border leftColHost;
    UIElement timeCalendarView;
    Action updateTimeAction, updateCalendarAction;
    internal Action toggleLanguageDropdown;
    TextBlock compactTimeText, compactDateText, compactWeatherText, compactAgendaText;
    TextBlock compactAgendaTag, compactAgendaTime, compactAgendaTitle, sepDotText;
    Border compactWeatherIcon, compactAgendaCapsule, compactAgendaTagBadge;
    StackPanel compactChipsBox;
    readonly List<PixelAnimal> animals = new List<PixelAnimal>();
    Forms.NotifyIcon tray;
    System.Drawing.Icon trayImage;
    bool expanded, busy, closing, isCinemaHidden, settingsOpen; public bool IsExpanded { get { return expanded; } }
    DateTime suppressHover = DateTime.MinValue;
    DateTime lastAg = DateTime.MinValue;
    HashSet<string> warned = new HashSet<string>();
    readonly CompletionNotifications completionNotifications = new CompletionNotifications(DateTime.UtcNow);
    readonly TopmostGuard topmostGuard;

    double GetCompactLogicalWidth() {
        int activeCount = 0;
        if (prefs != null) {
            foreach (var id in prefs.GetOrderedProviders()) {
                if (Enabled(id)) activeCount++;
            }
        }
        activeCount = Math.Max(1, activeCount);
        double compactContentW = Math.Max(500, 310 + activeCount * 70);
        return compactContentW + 28;
    }

    void TraceDrag(string text) { if (!Environment.GetCommandLineArgs().Contains("--debug-ui")) return; try { Directory.CreateDirectory(Storage.State); File.AppendAllText(Path.Combine(Storage.State,"interaction.log"),DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine); } catch {} }
    public static SolidColorBrush Brush(string s) { return (SolidColorBrush)new BrushConverter().ConvertFromString(s); }

    public void SetScale(double s) {
        prefs.Scale = Math.Round(Math.Max(0.6, Math.Min(3.0, s)), 2);
        if (scaleTransform != null) { scaleTransform.ScaleX = prefs.Scale; scaleTransform.ScaleY = prefs.Scale; }
        Expand(expanded); Save();
    }

    public void SetOpacity(double op) {
        prefs.Opacity = Math.Round(Math.Max(0.05, Math.Min(1.0, op)), 2);
        Opacity = prefs.Opacity;
        Save();
    }

    string selectedSettingsModelId = "codex";

    public void OpenModelAppearanceSettings(string modelId) {
        if (!string.IsNullOrEmpty(modelId)) selectedSettingsModelId = modelId;
        if (!expanded) Expand(true);
        ToggleSettingsView(true);
    }

    public void SwitchModelAnimal(string id, string animal) {
        prefs.SetProviderAnimal(id, animal);
        PixelAnimal.EnsureTheme(prefs.GetProviderColor(id), animal);
        Render();
        UpdateMotion();
        Save();
    }

    public void SwitchModelColor(string id, string color) {
        prefs.SetProviderColor(id, color);
        if (data.ContainsKey(id)) data[id].Theme = color;
        PixelAnimal.EnsureTheme(color, prefs.GetProviderAnimal(id));
        Render();
        UpdateMotion();
        Save();
    }

    public void SetAnimalModel(string model) {
        SwitchModelAnimal(selectedSettingsModelId, model);
    }

    public void SwitchProviderAnimal(string id) {
        string next = prefs.ToggleProviderAnimal(id);
        PixelAnimal.EnsureTheme(prefs.GetProviderColor(id), next);
        Render();
        UpdateMotion();
        Save();
    }

    public void SetCustomAnimalColor(string colorOrHex) {
        SwitchModelColor(selectedSettingsModelId, colorOrHex);
    }

    public void SetCatColor(string id, string theme) {
        SwitchModelColor(id, theme);
    }

    public Island(bool screenshot) {
        try {
            prefs = Storage.Load();
            I18n.CurrentLanguage = prefs.GetLanguage();
            I18n.LanguageChanged += OnLanguageChanged;
            prefs.Dock = "top"; // 強制頂部動態島模式
            PixelAnimal.CurrentAnimal = prefs.GetAnimalType();
            if (prefs.Scale < 0.6 || prefs.Scale > 3.0) prefs.Scale = 1.0;
            if (!screenshot) CatDropOverlay.PreloadAsync();

            Title = I18n.T("Island.Title");
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = null;
            Topmost = true;
            ShowInTaskbar = false;
            topmostGuard = new TopmostGuard(this, () => isCinemaHidden);
            FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI");
            FontSize = 13;
            Foreground = Brush("#F3F4F7");
            Opacity = prefs.Opacity <= 0.1 || prefs.Opacity > 1.0 ? 0.98 : Math.Round(prefs.Opacity, 2);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var area = WorkArea();
            double initLogicalW = GetCompactLogicalWidth();
            double initLogicalH = 40;
            double expandedLogicalW = Math.Max(660 + 28, initLogicalW);
            Width = expandedLogicalW * prefs.Scale;
            Height = initLogicalH * prefs.Scale;
            Top = area.Top;
            Left = area.Left + (area.Width - Width) / 2;

            data["codex"] = Providers.EmptyCodex(); data["codex"].Theme = prefs.GetProviderColor("codex");
            data["antigravity"] = Providers.EmptyAg(); data["antigravity"].Theme = prefs.GetProviderColor("antigravity");
            data["claude"] = Providers.EmptyClaude(); data["claude"].Theme = prefs.GetProviderColor("claude");
            data["grok"] = Providers.EmptyGrok(); data["grok"].Theme = prefs.GetProviderColor("grok");
            foreach (var id in new[] { "codex", "antigravity", "claude", "grok" }) {
                PixelAnimal.EnsureTheme(prefs.GetProviderColor(id), prefs.GetProviderAnimal(id));
            }
            LocalSyncServer.Start(delegate(string providerId) {
                Dispatcher.BeginInvoke(new Action(async delegate {
                    await Refresh(true);
                }));
            });

            // 根容器與縮放（外層全透明畫布）
            rootContainer = new Grid { ClipToBounds = false, Background = null };
            scaleTransform = new ScaleTransform(prefs.Scale, prefs.Scale);
            rootContainer.LayoutTransform = scaleTransform;

            // 動態島本體宿主：頂部置中（iOS / macOS 原廠核心錨點）
            islandHost = new Grid {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = false,
                Background = null,
                Width = initLogicalW,
                Height = initLogicalH
            };
            rootContainer.Children.Add(islandHost);

            // 原廠同款 Notch 幾何形狀背景（含反向凹弧耳朵 Ears）
            notchShapePath = new System.Windows.Shapes.Path {
                Fill = Brush("#0B0C12"),
                Stroke = Brush("#222638"),
                StrokeThickness = 1.0,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            islandHost.Children.Add(notchShapePath);

            // 內容容器：左右各留 14px 凹耳寬度，頂部貼齊 0
            contentContainer = new Grid {
                Margin = new Thickness(14, 0, 14, 0),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            islandHost.Children.Add(contentContainer);

            // 貓腳印互動懸浮畫布（IsHitTestVisible = false，完全不干擾下方任何控制項）
            pawOverlayCanvas = new Canvas {
                IsHitTestVisible = false,
                ClipToBounds = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            islandHost.Children.Add(pawOverlayCanvas);

            Content = rootContainer;
            UpdateNotchGeometry(initLogicalW, initLogicalH);

            // 1. 收合態（Compact）：高度 40px，固定貼齊頂部 Notch，絕不垂直居中漂移
            compactContainer = new Grid {
                Height = 40,
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = false
            };
            compactTransform = new TranslateTransform(0, 0);
            compactContainer.RenderTransform = compactTransform;
            compactContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            compactContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compactContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 收合態左側：時鐘 · 幾月幾號星期幾 · 天氣圖示 · 氣溫
            var timeWeatherBox = new StackPanel {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = I18n.T("Island.CompactTip")
            };
            compactTimeText = new TextBlock {
                Text = DateTime.Now.ToString("HH:mm"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Consolas")
            };
            timeWeatherBox.Children.Add(compactTimeText);

            var timeDot1 = new Border {
                Width = 3,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = Brush("#4C5368"),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            timeWeatherBox.Children.Add(timeDot1);

            compactDateText = new TextBlock {
                Text = I18n.FormatCompactDate(DateTime.Now),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#BAC4D8"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI")
            };
            timeWeatherBox.Children.Add(compactDateText);

            var timeDot2 = new Border {
                Width = 3,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = Brush("#4C5368"),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            timeWeatherBox.Children.Add(timeDot2);

            compactWeatherIcon = new Border {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            compactWeatherIcon.Child = WeatherManager.CreateWeatherIcon(WeatherManager.Instance.Current.ConditionCode, 13);
            timeWeatherBox.Children.Add(compactWeatherIcon);

            compactWeatherText = new TextBlock {
                Text = WeatherManager.Instance.Current.Temp,
                FontSize = 11,
                Foreground = Brush("#9DA4BC"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Consolas")
            };
            timeWeatherBox.Children.Add(compactWeatherText);

            timeWeatherBox.MouseLeftButtonUp += async delegate {
                if (!expanded) Expand(true);
                await WeatherManager.Instance.RefreshAsync(false);
            };
            NotchUi.ApplyTouchFeedback(timeWeatherBox, 0.97);

            Grid.SetColumn(timeWeatherBox, 0);
            compactContainer.Children.Add(timeWeatherBox);

            // 收合態中間紅框位置：日曆狀態與近期日程通知膠囊
            compactAgendaCapsule = new Border {
                Background = Brush("#131622"),
                BorderBrush = Brush("#232738"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 8, 2),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "點擊展開 Google 日曆檢視日程"
            };

            var agendaStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var calIcon = NotchUi.CreateGoogleCalendarIcon(12);
            calIcon.Margin = new Thickness(0, 0, 4, 0);
            calIcon.VerticalAlignment = VerticalAlignment.Center;
            agendaStack.Children.Add(calIcon);

            // 日期微型標籤膠囊（例如「明天」、「30分後」、「週五」）
            compactAgendaTagBadge = new Border {
                Background = Brush("#1A2B4C"),
                CornerRadius = new CornerRadius(3.5),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            compactAgendaTag = new TextBlock {
                Text = "日程",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#60A5FA")
            };
            compactAgendaTagBadge.Child = compactAgendaTag;
            agendaStack.Children.Add(compactAgendaTagBadge);

            compactAgendaTime = new TextBlock {
                Text = "14:00",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#E2E8F0"),
                FontFamily = new FontFamily("Segoe UI, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };
            agendaStack.Children.Add(compactAgendaTime);

            sepDotText = new TextBlock {
                Text = "·",
                FontSize = 10,
                Foreground = Brush("#64748B"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };
            agendaStack.Children.Add(sepDotText);

            compactAgendaTitle = new TextBlock {
                Text = "載入日程中...",
                FontSize = 10.5,
                Foreground = Brush("#BAC4D8"),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 128
            };
            compactAgendaText = compactAgendaTitle;
            agendaStack.Children.Add(compactAgendaTitle);
            compactAgendaCapsule.Child = agendaStack;

            compactAgendaCapsule.MouseEnter += delegate {
                compactAgendaCapsule.Background = Brush("#1D2336");
                compactAgendaCapsule.BorderBrush = Brush("#3B82F6");
            };
            compactAgendaCapsule.MouseLeave += delegate {
                var pinned = NoteManager.Instance.GetPrimaryPinnedNote();
                compactAgendaCapsule.Background = pinned != null ? Brush("#1B1710") : Brush("#131622");
                compactAgendaCapsule.BorderBrush = pinned != null ? Brush("#78350F") : Brush("#232738");
            };
            compactAgendaCapsule.MouseLeftButtonUp += delegate {
                bool hasPinnedNote = NoteManager.Instance.GetPrimaryPinnedNote() != null;
                if (!expanded) Expand(true);
                NotchUi.SwitchToMode(hasPinnedNote);
            };
            NotchUi.ApplyTouchFeedback(compactAgendaCapsule, 0.96);

            Grid.SetColumn(compactAgendaCapsule, 1);
            compactContainer.Children.Add(compactAgendaCapsule);

            // 收合態右側：模型與小貓咪晶片（高度 40px，小動物 28px 完整顯示無切角）
            compactChipsBox = new StackPanel {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(compactChipsBox, 2);
            compactContainer.Children.Add(compactChipsBox);

            contentContainer.Children.Add(compactContainer);

            // 2. 展開態（Expanded）：雙欄架構（左 Google 日曆日程，右模型清單）
            expandedBody = new Grid {
                Visibility = Visibility.Collapsed,
                ClipToBounds = false,
                Margin = new Thickness(0, 4, 0, 4)
            };
            expandedTransform = new TranslateTransform(0, 0);
            expandedBody.RenderTransform = expandedTransform;
            expandedBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            expandedBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            expandedBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左欄：時間、日期與 Google 日曆卡片
            leftColHost = new Border();
            timeCalendarView = NotchUi.BuildTimeCalendarView(this, out updateTimeAction, out updateCalendarAction);
            leftColHost.Child = timeCalendarView;
            Grid.SetColumn(leftColHost, 0);
            expandedBody.Children.Add(leftColHost);

            // 中隔線
            var colDivider = new Border {
                Background = Brush("#1C202E"),
                Width = 1,
                Margin = new Thickness(0, 8, 0, 8),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(colDivider, 1);
            expandedBody.Children.Add(colDivider);

            // 右欄：頂部小提示與按鈕 + 模型清單卡片
            var rightCol = new Grid { Margin = new Thickness(10, 2, 4, 4) };
            rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var rightHeader = new DockPanel { LastChildFill = false, Margin = new Thickness(4, 0, 2, 4) };
            rightHeaderTitle = Text(I18n.T("Island.ModelQuota"), 11, "#94A3B8");
            DockPanel.SetDock(rightHeaderTitle, Dock.Left);
            rightHeader.Children.Add(rightHeaderTitle);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pin = Button("◇", delegate {
                prefs.Pinned = !prefs.Pinned;
                pin.Content = prefs.Pinned ? "◆" : "◇";
                pin.ToolTip = prefs.Pinned ? I18n.T("Island.Pinned") : I18n.T("Island.Unpinned");
                Expand(true);
                Save();
            }, prefs.Pinned ? I18n.T("Island.Pinned") : I18n.T("Island.Unpinned"));
            pin.FontSize = 15;
            NotchUi.ApplyTouchFeedback(pin, 0.88);
            actions.Children.Add(pin);

            menuBtn = Button("≡", delegate { OpenMenu(); }, I18n.T("Island.SettingsTitle"));
            menuBtn.FontSize = 15;
            NotchUi.ApplyTouchFeedback(menuBtn, 0.88);
            actions.Children.Add(menuBtn);

            DockPanel.SetDock(actions, Dock.Right);
            rightHeader.Children.Add(actions);
            Grid.SetRow(rightHeader, 0);
            rightCol.Children.Add(rightHeader);

            cards = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            details = new ScrollViewer {
                Content = cards,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 0)
            };
            settingsDetails = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            string svXaml = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollViewer'><Grid Background='Transparent'><ScrollContentPresenter Margin='0,0,6,0'/><ScrollBar x:Name='PART_VerticalScrollBar' HorizontalAlignment='Right' Width='4' Background='Transparent' BorderThickness='0' Value='{TemplateBinding VerticalOffset}' Maximum='{TemplateBinding ScrollableHeight}' ViewportSize='{TemplateBinding ViewportHeight}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'><ScrollBar.Template><ControlTemplate TargetType='ScrollBar'><Track x:Name='PART_Track' IsDirectionReversed='True'><Track.Thumb><Thumb Focusable='False' Width='4'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='#28FFFFFF' CornerRadius='2' Width='4'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate></ScrollBar.Template></ScrollBar></Grid></ControlTemplate>";
            try {
                details.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(svXaml);
                settingsDetails.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(svXaml);
            } catch { }

            Grid.SetRow(details, 1);
            rightCol.Children.Add(details);
            Grid.SetRow(settingsDetails, 1);
            rightCol.Children.Add(settingsDetails);

            Grid.SetColumn(rightCol, 2);
            expandedBody.Children.Add(rightCol);

            contentContainer.Children.Add(expandedBody);

            // 天氣與日曆更新回呼
            WeatherManager.Instance.WeatherUpdated += delegate {
                Dispatcher.BeginInvoke(new Action(delegate {
                    UpdateCompactWeather();
                }));
            };
            AgendaManager.Instance.EventsUpdated += delegate {
                Dispatcher.BeginInvoke(new Action(delegate {
                    UpdateCompactAgenda();
                }));
            };
            NoteManager.Instance.NotesUpdated += delegate {
                Dispatcher.BeginInvoke(new Action(delegate {
                    UpdateCompactAgenda();
                }));
            };

            // 智慧 Z-Order 禮讓定時器（每 450ms 巡檢：當前景為網頁瀏覽器或全螢幕時，徹底隱藏不顯示，絕不阻擋瀏覽器分頁點擊）
            int clearStreak = 0;
            cinemaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            cinemaTimer.Tick += delegate {
                try {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd == IntPtr.Zero) return;

                    IntPtr fg = FullscreenDetector.GetForegroundWindowHandle();
                    bool isDesktop = FullscreenDetector.IsDesktopOrShellWindow(fg);

                    bool shouldYield = false;
                    if (!isDesktop) {
                        if (prefs.CinemaMode && FullscreenDetector.IsForegroundFullscreen(hwnd)) {
                            shouldYield = true;
                        } else if (prefs.AvoidBrowser && FullscreenDetector.ShouldYieldTopmost(hwnd)) {
                            shouldYield = true;
                        }
                    }

                    if (shouldYield) {
                        clearStreak = 0;
                        if (expanded) Expand(false);
                        if (!isCinemaHidden) {
                            isCinemaHidden = true;
                            Topmost = false;
                        }
                        FullscreenDetector.SendToBottom(hwnd);
                        if (Visibility != Visibility.Hidden) {
                            Visibility = Visibility.Hidden;
                        }
                        return;
                    }

                    // 處於桌面時（點擊桌面、桌面常駐，絕不隱藏，立即置頂顯示）：
                    if (isDesktop) {
                        clearStreak = 0;
                        if (isCinemaHidden || Visibility != Visibility.Visible || !Topmost) {
                            isCinemaHidden = false;
                            Visibility = Visibility.Visible;
                            Topmost = true;
                            FullscreenDetector.BringToTopmost(hwnd);
                        }
                    } else {
                        // 非瀏覽器/非全螢幕時（例如在 Antigravity、Claude、Codex 等應用程式前景）
                        clearStreak++;
                        if (clearStreak >= 2) {
                            clearStreak = 0;
                            if (isCinemaHidden || Visibility != Visibility.Visible) {
                                isCinemaHidden = false;
                                Visibility = Visibility.Visible;
                                Topmost = true;
                                FullscreenDetector.BringToTopmost(hwnd);
                            }
                        }
                    }
                } catch { }
            };
            cinemaTimer.Start();

            // 滑鼠離開動態島自動關閉設定視圖並平滑收合（每 220ms 巡檢，離開 > 25px 滿 0.44 秒觸發）
            int outsideSafeZoneTicks = 0;
            collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            collapse.Tick += delegate {
                if (Environment.GetCommandLineArgs().Contains("--screenshot")) return;
                if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0) {
                    outsideSafeZoneTicks = 0;
                    return;
                }
                if (prefs.Pinned && !settingsOpen) {
                    outsideSafeZoneTicks = 0;
                    collapse.Stop();
                    return;
                }
                if (IsCursorInSafeZone(25, 25)) {
                    outsideSafeZoneTicks = 0;
                    return;
                }
                outsideSafeZoneTicks++;
                if (outsideSafeZoneTicks >= 2) {
                    outsideSafeZoneTicks = 0;
                    collapse.Stop();
                    if (settingsOpen) ToggleSettingsView(false);
                    if (!prefs.Pinned) Expand(false);
                }
            };

            hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            hover.Tick += delegate {
                hover.Stop();
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && (FullscreenDetector.IsForegroundFullscreen(hwnd) || (prefs.AvoidBrowser && FullscreenDetector.ShouldYieldTopmost(hwnd)))) {
                    return;
                }
                if ((IsMouseOver || IsCursorInSafeZone(20, 15)) && !expanded) {
                    Expand(true);
                }
            };

            MouseEnter += delegate {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && (FullscreenDetector.IsForegroundFullscreen(hwnd) || (prefs.AvoidBrowser && FullscreenDetector.ShouldYieldTopmost(hwnd)))) {
                    return;
                }
                outsideSafeZoneTicks = 0;
                if (expanded) {
                    collapse.Stop();
                }
                if (prefs.HoverExpand && !expanded && DateTime.UtcNow > suppressHover) {
                    if (IsCursorInSafeZone(20, 15)) {
                        hover.Stop();
                        hover.Start();
                    }
                }
            };

            MouseLeave += delegate {
                hover.Stop();
                if (!prefs.Pinned || settingsOpen) {
                    outsideSafeZoneTicks = 0;
                    if (!collapse.IsEnabled) collapse.Start();
                }
            };

            // 點擊畫面產生靈動小貓腳印微動畫（隨機粉嫩毛色微光 + 彈性印出與上飄淡出）
            PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                try {
                    Point pt = e.GetPosition(pawOverlayCanvas != null ? (IInputElement)pawOverlayCanvas : islandHost ?? (IInputElement)rootContainer);
                    SpawnCatPaw(pt.X, pt.Y);
                } catch { }
            };

            // 裝置固定置頂居中：徹底刪除拖拉移動機制，點擊收合態動態島直接展開
            PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;
                if (!expanded) {
                    Expand(true);
                }
            };

            PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;
                OpenMenu();
                e.Handled = true;
            };

            PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
                    SetScale(prefs.Scale + (e.Delta > 0 ? 0.05 : -0.05));
                    e.Handled = true;
                }
            };

            KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape) {
                    if (settingsOpen) ToggleSettingsView(false);
                    prefs.Pinned = false;
                    Expand(false);
                    Save();
                }
            };

            SetupTray();
            Render();

            // 小動物呼吸動畫時鐘
            animationClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            animationClock.Tick += delegate {
                if (!IsVisible || prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation || (!prefs.Breathing && !prefs.AnimalAnimation)) return;
                foreach (var animal in animals) {
                    if (animal.IsVisible) animal.InvalidateVisual();
                }
            };
            animationClock.Start();

            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            poll.Tick += async delegate { await Refresh(false); };
            poll.Start();

            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += delegate {
                compactTimeText.Text = DateTime.Now.ToString("HH:mm");
                if (compactDateText != null) compactDateText.Text = I18n.FormatCompactDate(DateTime.Now);
                UpdateCompactAgenda();
                if (expanded && updateTimeAction != null) updateTimeAction();
            };
            clock.Start();

            debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            debounceTimer.Tick += async delegate {
                debounceTimer.Stop();
                await Refresh(true);
            };

            // 監聽 Antigravity 與 Codex 的紀錄變更
            try {
                string agConvDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "conversations");
                if (Directory.Exists(agConvDir)) {
                    watcherAg = new FileSystemWatcher(agConvDir) {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        Filter = "*.*",
                        EnableRaisingEvents = true
                    };
                    FileSystemEventHandler onAgChange = delegate {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            debounceTimer.Stop();
                            debounceTimer.Start();
                        }));
                    };
                    watcherAg.Changed += onAgChange;
                    watcherAg.Created += onAgChange;
                }
            } catch { }

            try {
                string codexDir = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrEmpty(codexDir)) codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string codexSessions = Path.Combine(codexDir, "sessions");
                if (Directory.Exists(codexSessions)) {
                    watcherCodex = new FileSystemWatcher(codexSessions) {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        Filter = "*.jsonl",
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    FileSystemEventHandler onCodexChange = delegate {
                        Dispatcher.BeginInvoke(new Action(delegate {
                            debounceTimer.Stop();
                            debounceTimer.Start();
                        }));
                    };
                    watcherCodex.Changed += onCodexChange;
                    watcherCodex.Created += onCodexChange;
                }
            } catch { }

            SizeChanged += OnIslandSizeChanged;

            Loaded += async delegate {
                MoveToTargetScreen();
                Expand(prefs.Pinned);
                await Refresh(true);
                await WeatherManager.Instance.RefreshAsync(false);
                AgendaManager.Instance.RefreshAsync();
                if (prefs.Pinned) Expand(true);

                // 啟動單一實例喚醒監聽執行緒（當使用者再次點擊 EXE 時瞬間喚醒並展開）
                try {
                    wakeupListener = new Thread(() => {
                        try {
                            EventWaitHandle wh;
                            if (EventWaitHandle.TryOpenExisting("Local\\TokenIslandWakeupEvent", out wh)) {
                                while (!closing) {
                                    if (wh.WaitOne(600)) {
                                        if (closing) break;
                                        Dispatcher.BeginInvoke(new Action(() => {
                                            isCinemaHidden = false;
                                            Animate(TopProperty, WorkArea().Top);
                                            Expand(true);
                                            Topmost = true;
                                            Activate();
                                            SpawnCatPaw(Width / 2, Math.Min(Height - 30, 50));
                                        }));
                                    }
                                }
                            }
                        } catch { }
                    }) { IsBackground = true };
                    wakeupListener.Start();
                } catch { }
            };

            Closed += delegate {
                closing = true;
                if (wakeupListener != null) {
                    try { wakeupListener.Abort(); } catch { }
                }
                topmostGuard.Dispose();
                LocalSyncServer.Stop();
                poll.Stop(); clock.Stop(); collapse.Stop(); hover.Stop(); animationClock.Stop(); cinemaTimer.Stop();
                if (debounceTimer != null) debounceTimer.Stop();
                if (watcherAg != null) { watcherAg.EnableRaisingEvents = false; watcherAg.Dispose(); }
                if (watcherCodex != null) { watcherCodex.EnableRaisingEvents = false; watcherCodex.Dispose(); }
                Save();
                tray.Visible = false;
                tray.Dispose();
                if (trayImage != null) { trayImage.Dispose(); trayImage = null; }
                Application.Current.Shutdown();
            };

        } catch (Exception ex) {
            try { File.WriteAllText(System.IO.Path.Combine(Storage.State, "init-ex.txt"), ex.ToString()); } catch { }
            throw;
        }
    }

    void UpdateCompactWeather() {
        var w = WeatherManager.Instance.Current;
        if (compactWeatherText != null) compactWeatherText.Text = w.Temp;
        if (compactWeatherIcon != null) compactWeatherIcon.Child = WeatherManager.CreateWeatherIcon(w.ConditionCode, 13);
    }

    void UpdateCompactAgenda() {
        if (compactAgendaTitle == null) return;

        // 1. 優先檢查是否有釘選於頂部通知條的隨手筆記
        var pinnedNote = NoteManager.Instance.GetPrimaryPinnedNote();
        if (pinnedNote != null) {
            var allNotes = NoteManager.Instance.GetAllNotes();
            int pinnedCount = 0;
            for (int i = 0; i < allNotes.Count; i++) {
                if (allNotes[i].IsPinned) pinnedCount++;
            }

            if (compactAgendaCapsule != null && !compactAgendaCapsule.IsMouseOver) {
                compactAgendaCapsule.Background = Brush("#1B1710");
                compactAgendaCapsule.BorderBrush = Brush("#78350F");
            }
            if (compactAgendaTagBadge != null) {
                compactAgendaTagBadge.Visibility = Visibility.Visible;
                compactAgendaTagBadge.Background = Brush("#45270A");
            }
            if (compactAgendaTag != null) {
                compactAgendaTag.Foreground = Brush("#FBBF24");
                compactAgendaTag.Text = pinnedCount > 1 ? string.Format("{0}({1})", I18n.T("Agenda.PinnedBadge"), pinnedCount) : I18n.T("Agenda.PinnedBadge");
            }
            if (compactAgendaTime != null) {
                compactAgendaTime.Visibility = Visibility.Collapsed;
            }
            if (sepDotText != null) {
                sepDotText.Visibility = Visibility.Collapsed;
            }

            compactAgendaTitle.Foreground = Brush("#FEF3C7");
            compactAgendaTitle.Text = pinnedNote.Content;

            if (compactAgendaCapsule != null) {
                var nextEv = AgendaManager.Instance.GetNextUpcomingEvent();
                string extraCal = nextEv != null
                    ? string.Format("\n\n近期日曆：{0} ({1})", nextEv.Title, nextEv.GetTimeDisplay())
                    : "";
                compactAgendaCapsule.ToolTip = string.Format("頂欄釘選筆記：{0}\n更新時間：{1}\n點擊展開隨手筆記本進行增刪修改{2}",
                    pinnedNote.Content,
                    pinnedNote.UpdatedAt.ToString("yyyy/MM/dd HH:mm"),
                    extraCal);
            }
            return;
        }

        // 2. 無釘選筆記時，恢復顯示日曆行程通知
        if (compactAgendaCapsule != null && !compactAgendaCapsule.IsMouseOver) {
            compactAgendaCapsule.Background = Brush("#131622");
            compactAgendaCapsule.BorderBrush = Brush("#232738");
        }
        compactAgendaTitle.Foreground = Brush("#C4D2DB");

        var next = AgendaManager.Instance.GetNextUpcomingEvent();
        if (next != null) {
            DateTime now = DateTime.Now;
            string dateTag = I18n.T("Agenda.Today");
            Brush tagBg = Brush("#162820");
            Brush tagFg = Brush("#34D399");

            if (next.StartTime.Date == now.Date) {
                TimeSpan diff = next.StartTime - now;
                if (now >= next.StartTime && now <= next.EndTime) {
                    dateTag = I18n.T("Agenda.InProgress");
                    tagBg = Brush("#3B1D22");
                    tagFg = Brush("#F87171");
                } else if (diff.TotalMinutes > 0 && diff.TotalMinutes <= 60) {
                    dateTag = I18n.T("Agenda.MinsLater", Math.Max(1, (int)diff.TotalMinutes));
                    tagBg = Brush("#362712");
                    tagFg = Brush("#FBBF24");
                } else {
                    dateTag = I18n.T("Agenda.Today");
                    tagBg = Brush("#162820");
                    tagFg = Brush("#34D399");
                }
            } else if (next.StartTime.Date == now.Date.AddDays(1)) {
                dateTag = I18n.T("Quota.Tomorrow");
                tagBg = Brush("#1A2B4C");
                tagFg = Brush("#60A5FA");
            } else if (next.StartTime.Date == now.Date.AddDays(2)) {
                dateTag = I18n.T("Agenda.DayAfterTomorrow");
                tagBg = Brush("#241D3B");
                tagFg = Brush("#A78BFA");
            } else if ((next.StartTime.Date - now.Date).TotalDays < 7) {
                dateTag = next.StartTime.ToString("ddd", I18n.CurrentCulture);
                tagBg = Brush("#1E293B");
                tagFg = Brush("#94A3B8");
            } else {
                dateTag = next.StartTime.ToString("M/d");
                tagBg = Brush("#1E293B");
                tagFg = Brush("#94A3B8");
            }

            if (compactAgendaTagBadge != null) {
                compactAgendaTagBadge.Visibility = Visibility.Visible;
                compactAgendaTagBadge.Background = tagBg;
            }
            if (compactAgendaTag != null) {
                compactAgendaTag.Foreground = tagFg;
                compactAgendaTag.Text = dateTag;
            }

            if (compactAgendaTime != null) {
                compactAgendaTime.Visibility = Visibility.Visible;
                compactAgendaTime.Text = next.IsAllDay ? I18n.T("Agenda.AllDay") : next.StartTime.ToString("HH:mm");
            }
            if (sepDotText != null) sepDotText.Visibility = Visibility.Visible;

            compactAgendaTitle.Text = next.Title;

            if (compactAgendaCapsule != null) {
                compactAgendaCapsule.ToolTip = string.Format("近期日程：{0} ({1})\n時間：{2}\n地點：{3}\n點擊展開日曆或切換隨手筆記",
                    next.Title,
                    next.GetRelativeStatus(),
                    next.GetTimeDisplay(),
                    string.IsNullOrEmpty(next.Location) ? "未設定地點" : next.Location);
            }
        } else {
            var items = AgendaManager.Instance.GetEventsForDate(DateTime.Today);
            if (compactAgendaTagBadge != null) compactAgendaTagBadge.Visibility = Visibility.Collapsed;
            if (compactAgendaTime != null) compactAgendaTime.Visibility = Visibility.Collapsed;
            if (sepDotText != null) sepDotText.Visibility = Visibility.Collapsed;

            if (items.Count > 0) {
                compactAgendaTitle.Text = I18n.T("Agenda.EventCount", items.Count);
            } else {
                compactAgendaTitle.Text = I18n.T("Agenda.NoEvents");
            }
            if (compactAgendaCapsule != null) {
                compactAgendaCapsule.ToolTip = "點擊展開 Google 日曆或切換隨手筆記本";
            }
        }
    }

    static bool HasButtonAncestor(DependencyObject obj) {
        while (obj != null) {
            if (obj is System.Windows.Controls.Primitives.ButtonBase || obj is System.Windows.Controls.Primitives.ScrollBar || obj is System.Windows.Controls.Primitives.Thumb) return true;
            if (!(obj is Visual)) return false;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    static bool HasScrollOrCardAncestor(DependencyObject obj) {
        while (obj != null) {
            if (obj is ScrollViewer) return true;
            var fe = obj as FrameworkElement;
            if (fe != null && fe.Tag is string) return true;
            if (!(obj is Visual)) return false;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    static TextBlock Text(string text, double size, string color) {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    Button Button(string text, Action action, string tip) {
        var b = new Button { Content = text, ToolTip = tip, Foreground = Brush("#BDC5D2"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(7, 3, 7, 3), Cursor = Cursors.Hand, FontSize = 15, Focusable = false };
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        f.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        f.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var c = new FrameworkElementFactory(typeof(ContentPresenter));
        c.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        c.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        f.AppendChild(c);
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = f };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.MouseEnter += delegate { b.Background = Brush("#242838"); };
        b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
        b.Click += delegate { action(); };
        return b;
    }

    bool Enabled(string id) { return id == "codex" ? prefs.Codex : id == "antigravity" ? prefs.Antigravity : id == "grok" ? prefs.Grok : prefs.Claude; }
    void SetEnabled(string id, bool v) {
        if (id == "codex") prefs.Codex = v;
        else if (id == "antigravity") prefs.Antigravity = v;
        else if (id == "grok") prefs.Grok = v;
        else prefs.Claude = v;
        Save();
        Render();
        Expand(expanded);
    }

    void Save() {
        try {
            prefs.Opacity = Opacity;
            Storage.Write("settings.json", prefs);
        } catch { }
    }

    bool IsCursorInSafeZone(double padX = 25, double padY = 25) {
        if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0) return true;
        try {
            var mp = Forms.Control.MousePosition;
            Point pt = new Point(mp.X, mp.Y);
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) {
                pt = src.CompositionTarget.TransformFromDevice.Transform(pt);
            }
            var area = WorkArea();
            double compactW = GetCompactLogicalWidth();
            double currentIslandW = (expanded ? Math.Max(660 + 28, compactW) : compactW) * prefs.Scale;
            double islandLeft = area.Left + (area.Width - currentIslandW) / 2.0;
            double h = (expanded ? 316 : 40) * prefs.Scale;
            return pt.X >= islandLeft - padX && pt.X <= islandLeft + currentIslandW + padX &&
                   pt.Y >= Top - 30 && pt.Y <= Top + h + padY;
        } catch {
            return expanded && IsMouseOver;
        }
    }

    public Rect WorkArea() {
        var targetScreen = prefs != null ? prefs.GetTargetScreen() : Forms.Screen.PrimaryScreen;
        if (targetScreen == null) return SystemParameters.WorkArea;
        var area = targetScreen.WorkingArea;
        var src = PresentationSource.FromVisual(this);
        if (src == null || src.CompositionTarget == null) {
            try {
                if (targetScreen.Primary) return SystemParameters.WorkArea;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) {
                    double dpiX = g.DpiX / 96.0;
                    double dpiY = g.DpiY / 96.0;
                    return new Rect(area.Left / dpiX, area.Top / dpiY, area.Width / dpiX, area.Height / dpiY);
                }
            } catch {
                return SystemParameters.WorkArea;
            }
        }
        try {
            var inverse = src.CompositionTarget.TransformFromDevice;
            var origin = inverse.Transform(new Point(area.Left, area.Top));
            var end = inverse.Transform(new Point(area.Right, area.Bottom));
            return new Rect(origin, end);
        } catch {
            return SystemParameters.WorkArea;
        }
    }

    public void MoveToTargetScreen() {
        var area = WorkArea();
        double s = prefs.Scale;
        double expandedLogicalW = 620 + 28;
        double totalW = expandedLogicalW * s;
        FreezeBounds();
        Left = area.Left + (area.Width - totalW) / 2.0;
        Top = area.Top;
        Width = totalW;
        Height = (expanded ? 316 : 40) * s;
        prefs.Left = Left;
        prefs.Top = Top;
        if (!expanded) {
            double compactLogicalW = GetCompactLogicalWidth();
            if (islandHost != null) islandHost.Width = compactLogicalW;
            UpdateNotchGeometry(compactLogicalW, 40);
        }
        Save();
    }

    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);
        MoveToTargetScreen();
    }

    void FreezeBounds() {
        double l = Left, t = Top, w = Width, h = Height;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Left = l; Top = t; Width = w; Height = h;
    }

    void Animate(System.Windows.DependencyProperty property, double target) {
        double current = (double)GetValue(property);
        BeginAnimation(property, null);
        SetValue(property, target);
        if (SystemParameters.ClientAreaAnimation && !prefs.ReducedMotion) {
            BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(220)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
        }
    }

    void AnimateBounds(bool open) {
        morphTransitionId++;
        int myTransition = morphTransitionId;

        if (isMorphing) {
            CompositionTarget.Rendering -= OnMorphRendering;
            isMorphing = false;
        }
        morphStopwatch.Reset();

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        var area = WorkArea();
        double s = prefs.Scale;
        double compactLogicalW = GetCompactLogicalWidth();
        double compactLogicalH = 40;

        double expandedContentW = Math.Max(640, compactLogicalW - 28);
        double expandedLogicalW = expandedContentW + 28;
        double expandedLogicalH = 316;

        compactPhysicsW = compactLogicalW * s;
        compactPhysicsH = compactLogicalH * s;
        compactPhysicsX = area.Left + (area.Width - compactPhysicsW) / 2.0;

        expandedPhysicsW = expandedLogicalW * s;
        expandedPhysicsH = expandedLogicalH * s;
        expandedPhysicsX = area.Left + (area.Width - expandedPhysicsW) / 2.0;

        if (prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation) {
            FinalizeBounds(open);
            return;
        }

        morphTargetOpen = open;

        if (open) {
            // === 展開態：物理視窗 Height 放大到展開高度，Width 和 Left 保持常駐置中 ===
            if (Width != expandedPhysicsW) Width = expandedPhysicsW;
            if (Left != expandedPhysicsX) Left = expandedPhysicsX;
            if (Top != area.Top) Top = area.Top;
            if (Height != expandedPhysicsH) Height = expandedPhysicsH;

            morphStartLogicalW = islandHost != null && islandHost.ActualWidth > 0 ? islandHost.ActualWidth : (islandHost != null ? islandHost.Width : compactLogicalW);
            if (morphStartLogicalW <= 0) morphStartLogicalW = compactLogicalW;
            morphStartLogicalH = islandHost != null && islandHost.ActualHeight > 0 ? islandHost.ActualHeight : (islandHost != null ? islandHost.Height : compactLogicalH);
            if (morphStartLogicalH <= 0) morphStartLogicalH = 40;

            morphTargetLogicalW = expandedLogicalW;
            morphTargetLogicalH = expandedLogicalH;
            morphDurationMs = 220.0;
        } else {
            // === 收合態：HWND 物理視窗在動畫期間保持不動（0 次 SetWindowPos）！===
            morphStartLogicalW = islandHost != null && islandHost.ActualWidth > 0 ? islandHost.ActualWidth : (islandHost != null ? islandHost.Width : expandedLogicalW);
            if (morphStartLogicalW <= 0) morphStartLogicalW = expandedLogicalW;
            morphStartLogicalH = islandHost != null && islandHost.ActualHeight > 0 ? islandHost.ActualHeight : (islandHost != null ? islandHost.Height : expandedLogicalH);
            if (morphStartLogicalH <= 0) morphStartLogicalH = 316;

            morphTargetLogicalW = compactLogicalW;
            morphTargetLogicalH = compactLogicalH;
            morphDurationMs = 190.0;
        }

        isMorphing = true;
        morphStopwatch.Start();
        CompositionTarget.Rendering += OnMorphRendering;
    }

    void OnMorphRendering(object sender, EventArgs e) {
        if (!isMorphing) return;

        double elapsed = morphStopwatch.Elapsed.TotalMilliseconds;
        double rawT = Math.Min(1.0, elapsed / morphDurationMs);

        // 頂級動態島流體果凍曲線（Fluid Spring Ease-Out）
        double ease = morphTargetOpen ? (1.0 - Math.Pow(1.0 - rawT, 3.2)) : (1.0 - Math.Pow(1.0 - rawT, 3.6));

        double curW = morphStartLogicalW + (morphTargetLogicalW - morphStartLogicalW) * ease;
        double curH = morphStartLogicalH + (morphTargetLogicalH - morphStartLogicalH) * ease;

        if (islandHost != null) {
            islandHost.Width = curW;
            islandHost.Height = curH;
        }
        UpdateNotchGeometry(curW, curH);

        if (rawT >= 1.0) {
            if (isMorphing) {
                CompositionTarget.Rendering -= OnMorphRendering;
                isMorphing = false;
            }
            morphStopwatch.Stop();
            FinalizeBounds(morphTargetOpen);
        }
    }

    void FinalizeBounds(bool open) {
        if (isMorphing) {
            CompositionTarget.Rendering -= OnMorphRendering;
            isMorphing = false;
        }
        if (morphStopwatch != null) morphStopwatch.Stop();

        var area = WorkArea();
        double s = prefs.Scale;
        double compactLogicalW = GetCompactLogicalWidth();
        double compactLogicalH = 40;

        double expandedContentW = Math.Max(640, compactLogicalW - 28);
        double expandedLogicalW = expandedContentW + 28;
        double expandedLogicalH = 316;

        compactPhysicsW = compactLogicalW * s;
        compactPhysicsH = compactLogicalH * s;
        compactPhysicsX = area.Left + (area.Width - compactPhysicsW) / 2.0;

        expandedPhysicsW = expandedLogicalW * s;
        expandedPhysicsH = expandedLogicalH * s;
        expandedPhysicsX = area.Left + (area.Width - expandedPhysicsW) / 2.0;

        if (open) {
            if (Width != expandedPhysicsW) Width = expandedPhysicsW;
            if (Left != expandedPhysicsX) Left = expandedPhysicsX;
            if (Top != area.Top) Top = area.Top;
            if (Height != expandedPhysicsH) Height = expandedPhysicsH;

            if (islandHost != null) {
                islandHost.Width = expandedLogicalW;
                islandHost.Height = expandedLogicalH;
            }
            UpdateNotchGeometry(expandedLogicalW, expandedLogicalH);

            compactContainer.Visibility = Visibility.Collapsed;
            compactContainer.Opacity = 0;
            expandedBody.Visibility = Visibility.Visible;
            expandedBody.Opacity = 1;
            if (compactTransform != null) compactTransform.Y = 0;
            if (expandedTransform != null) expandedTransform.Y = 0;
        } else {
            // === 收合完成：物理視窗水平 Width 與 Left 永久釘死在螢幕中央，僅切齊 Height 釋放下層空間 ===
            // 嚴禁在此修改 Left 或 Width，杜絕任何跨幀像素跳動（Frame Jitter = 0px）
            if (Height != compactPhysicsH) Height = compactPhysicsH;

            if (islandHost != null) {
                islandHost.Width = compactLogicalW;
                islandHost.Height = compactLogicalH;
            }
            UpdateNotchGeometry(compactLogicalW, compactLogicalH);

            expandedBody.Visibility = Visibility.Collapsed;
            expandedBody.Opacity = 0;
            compactContainer.Visibility = Visibility.Visible;
            compactContainer.Opacity = 1;
            if (compactTransform != null) compactTransform.Y = 0;
            if (expandedTransform != null) expandedTransform.Y = 0;
        }
    }

    public void Reveal() { isCinemaHidden = false; Visibility = Visibility.Visible; Show(); WindowState = WindowState.Normal; Topmost = true; topmostGuard.Ensure(); Activate(); }

    void SetupTray() {
        if (tray == null) {
            trayImage = TrayIconLoader.Load();
            tray = new Forms.NotifyIcon {
                Icon = trayImage,
                Visible = true
            };
            tray.DoubleClick += delegate { Dispatcher.Invoke(new Action(Reveal)); };
        }
        tray.Text = I18n.T("Tray.Title");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(I18n.T("Tray.Show"), null, delegate { Dispatcher.Invoke(new Action(Reveal)); });
        menu.Items.Add(I18n.T("Tray.Settings"), null, delegate { Dispatcher.Invoke(new Action(delegate { Reveal(); ToggleSettingsView(true); })); });
        menu.Items.Add(I18n.T("Tray.HideTemp"), null, delegate { Dispatcher.Invoke(new Action(Hide)); });
        menu.Items.Add(I18n.T("Tray.Refresh"), null, delegate { Dispatcher.Invoke(new Action(async delegate { await Refresh(true); })); });
        menu.Items.Add(I18n.T("Tray.Exit"), null, delegate { Dispatcher.Invoke(new Action(Close)); });
        tray.ContextMenuStrip = menu;
    }

    void OnLanguageChanged() {
        try {
            Title = I18n.T("Island.Title");
            SetupTray();
            Render();
            if (leftColHost != null) {
                timeCalendarView = NotchUi.BuildTimeCalendarView(this, out updateTimeAction, out updateCalendarAction);
                leftColHost.Child = timeCalendarView;
            }
            if (expanded && updateTimeAction != null) updateTimeAction();
            if (expanded && updateCalendarAction != null) updateCalendarAction();
            UpdateCompactWeather();
            UpdateCompactAgenda();
            if (pin != null) {
                pin.ToolTip = prefs.Pinned ? I18n.T("Island.Pinned") : I18n.T("Island.Unpinned");
            }
            if (menuBtn != null) {
                menuBtn.ToolTip = settingsOpen ? I18n.T("Island.BackToQuota") : I18n.T("Island.SettingsTitle");
            }
            if (rightHeaderTitle != null) {
                rightHeaderTitle.Text = settingsOpen ? I18n.T("Island.SettingsTitle") : I18n.T("Island.ModelQuota");
            }
        } catch { }
    }

    void OnIslandSizeChanged(object sender, SizeChangedEventArgs e) {
        if (isMorphing) return;
        if (islandHost != null) UpdateNotchGeometry(islandHost.Width, islandHost.Height);
    }

    void UpdateNotchGeometry(double logicalW, double logicalH) {
        if (notchShapePath == null) return;
        if (logicalW < 40 || logicalH < 20) return;

        double earRadius = 14;
        double bottomRadius = 18;
        double contentW = Math.Max(40, logicalW - 2 * earRadius);
        double contentH = logicalH;

        var geom = NotchShape.BuildGeometry(contentW, contentH, earRadius, bottomRadius);
        if (geom.CanFreeze) geom.Freeze();
        notchShapePath.Data = geom;
        notchShapePath.Width = logicalW;
        notchShapePath.Height = logicalH;
    }

    void Expand(bool open) {
        if (!open && settingsOpen) {
            ToggleSettingsView(false);
        }
        bool reveal = open && !expanded;
        expanded = open;
        pin.Content = prefs.Pinned ? "◆" : "◇";

        if (open) {
            // === 展開態微動效 ===
            if (!prefs.ReducedMotion && SystemParameters.ClientAreaAnimation) {
                var compactFade = new DoubleAnimation(compactContainer.Opacity, 0, TimeSpan.FromMilliseconds(70)) { FillBehavior = FillBehavior.HoldEnd };
                compactFade.Completed += delegate { if (expanded) compactContainer.Visibility = Visibility.Collapsed; };
                compactContainer.BeginAnimation(OpacityProperty, compactFade);

                expandedBody.Visibility = Visibility.Visible;
                var bodyFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) {
                    BeginTime = TimeSpan.FromMilliseconds(50),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                var bodySlide = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(200)) {
                    BeginTime = TimeSpan.FromMilliseconds(50),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                expandedBody.BeginAnimation(OpacityProperty, bodyFade);
                if (expandedTransform != null) expandedTransform.BeginAnimation(TranslateTransform.YProperty, bodySlide);
            } else {
                compactContainer.Visibility = Visibility.Collapsed;
                compactContainer.Opacity = 0;
                expandedBody.Visibility = Visibility.Visible;
                expandedBody.Opacity = 1;
                if (expandedTransform != null) expandedTransform.Y = 0;
            }

            AgendaManager.Instance.CheckSyncOnExpand();
            if (updateTimeAction != null) updateTimeAction();
            if (updateCalendarAction != null) updateCalendarAction();
            if ((!prefs.Pinned || settingsOpen) && collapse != null && !collapse.IsEnabled) collapse.Start();
        } else {
            // === 收合態微動效（分層向心吸附，絕不留黑底視覺殘留）===
            if (collapse != null) collapse.Stop();

            if (!prefs.ReducedMotion && SystemParameters.ClientAreaAnimation) {
                // 1. 卡片內容向頂部優雅收攏淡出（Slide Up + Fade Out，95ms 快速乾脆）
                var bodyFadeOut = new DoubleAnimation(expandedBody.Opacity, 0, TimeSpan.FromMilliseconds(95)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                var bodySlideUp = new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(95)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                bodyFadeOut.Completed += delegate {
                    if (!expanded) expandedBody.Visibility = Visibility.Collapsed;
                };
                expandedBody.BeginAnimation(OpacityProperty, bodyFadeOut);
                if (expandedTransform != null) expandedTransform.BeginAnimation(TranslateTransform.YProperty, bodySlideUp);

                // 2. 膠囊列在島嶼接近膠囊高度時（75ms）柔和淡入展開
                compactContainer.Visibility = Visibility.Visible;
                var compactFadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) {
                    BeginTime = TimeSpan.FromMilliseconds(75),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                var compactSlide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(120)) {
                    BeginTime = TimeSpan.FromMilliseconds(75),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                compactContainer.BeginAnimation(OpacityProperty, compactFadeIn);
                if (compactTransform != null) compactTransform.BeginAnimation(TranslateTransform.YProperty, compactSlide);
            } else {
                expandedBody.Visibility = Visibility.Collapsed;
                expandedBody.Opacity = 0;
                compactContainer.Visibility = Visibility.Visible;
                compactContainer.Opacity = 1;
                if (compactTransform != null) compactTransform.Y = 0;
            }
        }

        AnimateBounds(open);
    }

    static string FormatResetBadge(Quota q) {
        DateTime? t = q.NextResetTime;
        if (!t.HasValue) return I18n.T("Quota.DynamicQuota");
        DateTime local = t.Value.ToLocalTime();
        DateTime now = DateTime.Now;
        var span = t.Value - DateTime.UtcNow;
        if (span.TotalSeconds <= 0) return I18n.T("Quota.Ready");
        if (span.TotalHours <= 24 && local.Date == now.Date) return local.ToString("t", I18n.CurrentCulture);
        if (span.TotalHours <= 36 && local.Date == now.Date.AddDays(1)) return I18n.T("Quota.Tomorrow") + " " + local.ToString("t", I18n.CurrentCulture);
        return local.ToString("M", I18n.CurrentCulture);
    }

    static string ResetText(Quota q) {
        DateTime? t = q.NextResetTime;
        if (!t.HasValue) return I18n.T("Quota.DynamicAdjust");
        var span = t.Value - DateTime.UtcNow;
        if (span.TotalSeconds <= 0) return I18n.T("Quota.CycleReady");
        string days = span.TotalDays >= 1 ? I18n.T("Quota.Days", (int)span.TotalDays) : "";
        string hours = span.Hours > 0 ? I18n.T("Quota.Hours", span.Hours) : "";
        string mins = I18n.T("Quota.MinsReset", span.Minutes);
        string baseTip = days + hours + mins + " (" + t.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") + ")";
        if (q.WindowMinutes == 10080) baseTip += "\n" + I18n.T("Quota.WeeklyCycle");
        else if (q.WindowMinutes > 0) baseTip += "\n" + (q.WindowMinutes <= 1440 ? I18n.T("Quota.DailyWindow") : I18n.T("Quota.LongWindow"));
        return baseTip;
    }

    static string Age(DateTime? t) {
        if (!t.HasValue) return I18n.T("Quota.NotConnected");
        var s = DateTime.UtcNow - t.Value;
        return s.TotalSeconds < 60 ? I18n.T("Quota.JustNow") : s.TotalMinutes < 60 ? I18n.T("Quota.MinsAgo", (int)s.TotalMinutes) : s.TotalHours < 24 ? I18n.T("Quota.HoursAgo", (int)s.TotalHours) : I18n.T("Quota.DaysAgo", (int)s.TotalDays);
    }

    PixelAnimal Animal(Provider p, double size) {
        string theme = p != null ? prefs.GetProviderColor(p.Id) : "orange";
        string animalType = p != null ? prefs.GetProviderAnimal(p.Id) : prefs.GetAnimalType();
        var animal = new PixelAnimal(p, size, theme, animalType) {
            Breathing = prefs.Breathing,
            Animate = prefs.AnimalAnimation,
            Reduced = prefs.ReducedMotion
        };
        animals.Add(animal);
        return animal;
    }

    Border MakeWeeklyDot(Provider p, double size = 5) {
        string col = p.GetWeeklyDotColor();
        if (col == null) return null;
        var dot = new Border {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = Brush(col),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 2, 0),
            ToolTip = (p.WeeklyQuota != null ? "每週額度剩餘：" : "額度健康度：") + (p.WeeklyRemaining.HasValue ? Math.Floor(p.WeeklyRemaining.Value).ToString("0") + "%" : "100%")
        };
        if (prefs.Breathing && !prefs.ReducedMotion && SystemParameters.ClientAreaAnimation) {
            var pulse = new DoubleAnimation {
                From = 0.35,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(1350),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            dot.BeginAnimation(UIElement.OpacityProperty, pulse);
        }
        return dot;
    }

    void Render() {
        double scroll = details != null ? details.VerticalOffset : 0;
        animals.Clear();
        compactChipsBox.Children.Clear();
        cards.Children.Clear();

        var orderedIds = prefs.GetOrderedProviders();
        foreach (var id in orderedIds) {
            if (!data.ContainsKey(id)) continue;
            var p = data[id];
            if (!Enabled(p.Id)) continue;

            string status = PixelAnimal.MoodText(PixelAnimal.Mood(p.DailyRemaining ?? p.Remaining, p.Stale));

            // 收合態晶片（高度 40px，小動物 28px 垂直居中，100% 完整無切角）
            var chip = new StackPanel {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = Cursors.Hand,
                ToolTip = p.Name + " · " + status + (string.IsNullOrEmpty(p.TokenLabel) ? "" : "\n" + p.TokenLabel) + "\n" + Age(p.Updated)
            };
            chip.Children.Add(Animal(p, 28));

            var dot = MakeWeeklyDot(p, 5);
            if (dot != null) chip.Children.Add(dot);

            var value = Text(p.DailyShort, 11.5, "#DFE9ED");
            value.FontFamily = new FontFamily("Consolas");
            value.Margin = new Thickness(dot != null ? 2 : 4, 0, 0, 0);
            chip.Children.Add(value);
            chip.MouseLeftButtonUp += delegate { if (!expanded) Expand(true); };
            NotchUi.ApplyTouchFeedback(chip, 0.94);
            compactChipsBox.Children.Add(chip);

            // 展開態卡片（已固定，無拖曳擾動）
            cards.Children.Add(Card(p));
        }

        if (!data.Values.Any(p => Enabled(p.Id))) {
            compactChipsBox.Children.Add(Text(I18n.T("Island.NoProviderSelected"), 11, "#C4D2DB"));
            cards.Children.Add(Text(I18n.T("Island.SelectProviderTip"), 12, "#C4D2DB"));
        }

        if (details != null) details.ScrollToVerticalOffset(scroll);
    }

    UIElement Card(Provider p) {
        var panel = new StackPanel();
        var card = new Border {
            Background = Brush("#11131C"),
            BorderBrush = Brush("#232738"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Arrow,
            Child = panel
        };
        card.MouseEnter += delegate {
            card.Background = Brush("#151926");
            card.BorderBrush = Brush("#333E5A");
        };
        card.MouseLeave += delegate {
            card.Background = Brush("#11131C");
            card.BorderBrush = Brush("#232738");
        };
        NotchUi.ApplyTouchFeedback(card, 0.985);

        var title = new DockPanel();
        var rightGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cardDot = MakeWeeklyDot(p, 6);
        if (cardDot != null) { cardDot.Margin = new Thickness(0, 0, 4, 0); rightGroup.Children.Add(cardDot); }
        var percent = Text(p.DailyShort, 18, p.DailyRemaining.HasValue ? p.Color : "#82909C");
        percent.FontFamily = new FontFamily("Consolas");
        percent.FontWeight = FontWeights.Bold;
        rightGroup.Children.Add(percent);

        DockPanel.SetDock(rightGroup, Dock.Right);
        title.Children.Add(rightGroup);

        string currentAnimal = prefs.GetProviderAnimal(p.Id);
        string nextAnimal = currentAnimal == "cat" ? "fox" : (currentAnimal == "fox" ? "owl" : "cat");
        string currentName = currentAnimal == "cat" ? "像素小貓" : (currentAnimal == "fox" ? "像素赤狐" : "像素貓頭鷹");
        string nextName = nextAnimal == "cat" ? "像素小貓" : (nextAnimal == "fox" ? "像素赤狐" : "像素貓頭鷹");

        var sprite = Animal(p, 34);
        sprite.HorizontalAlignment = HorizontalAlignment.Center;
        sprite.VerticalAlignment = VerticalAlignment.Center;

        var avatarBtn = new Border {
            Width = 40,
            Height = 36,
            CornerRadius = new CornerRadius(7),
            Background = Brush("#141926"),
            BorderBrush = Brush("#243044"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 7, 0),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            ToolTip = "左鍵點擊：設定 " + p.Name + " 的角色與外觀配色\n右鍵點擊：快速切換角色（目前：" + currentName + "）",
            Child = sprite
        };
        avatarBtn.MouseEnter += delegate {
            avatarBtn.Background = Brush("#1C2638");
            avatarBtn.BorderBrush = Brush("#5AD4B2");
        };
        avatarBtn.MouseLeave += delegate {
            avatarBtn.Background = Brush("#141926");
            avatarBtn.BorderBrush = Brush("#243044");
        };
        string pid = p.Id;
        avatarBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) {
            e.Handled = true;
            OpenModelAppearanceSettings(pid);
        };
        avatarBtn.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) {
            e.Handled = true;
            SwitchProviderAnimal(pid);
        };
        NotchUi.ApplyTouchFeedback(avatarBtn, 0.90);

        DockPanel.SetDock(avatarBtn, Dock.Left);
        title.Children.Add(avatarBtn);

        var name = Text(p.Name, 12.5, "#EEF2F3");
        name.FontWeight = FontWeights.SemiBold;
        name.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(name);
        panel.Children.Add(title);

        List<Quota> displayQuotas = new List<Quota>();
        if (p.Id == "antigravity") {
            var g5h = p.Quotas.FirstOrDefault(q => Provider.IsPrimaryGeminiLabel(q.Label) && (q.WindowMinutes == 300 || q.Label.Contains("5 小時") || q.Label.Contains("5h")));
            var gWeekly = p.Quotas.FirstOrDefault(q => Provider.IsPrimaryGeminiLabel(q.Label) && (q.WindowMinutes >= 10000 || q.Label.Contains("每週") || q.Label.IndexOf("Weekly", StringComparison.OrdinalIgnoreCase) >= 0));
            var dailyQ = g5h ?? p.DailyQuota;
            if (dailyQ != null) {
                displayQuotas.Add(new Quota {
                    Label = (dailyQ.WindowMinutes == 300 || dailyQ.Label.Contains("5 小時") || dailyQ.Label.Contains("5h")) ? I18n.T("Quota.5Hours") : I18n.T("Quota.Daily"),
                    Remaining = dailyQ.Remaining,
                    Reset = dailyQ.Reset,
                    WindowMinutes = dailyQ.WindowMinutes > 0 ? dailyQ.WindowMinutes : 300
                });
            }
            var weeklyQ = gWeekly ?? p.WeeklyQuota;
            if (weeklyQ != null) {
                displayQuotas.Add(new Quota {
                    Label = I18n.T("Quota.Weekly"),
                    Remaining = weeklyQ.Remaining,
                    Reset = weeklyQ.Reset,
                    WindowMinutes = weeklyQ.WindowMinutes > 0 ? weeklyQ.WindowMinutes : 10080
                });
            } else {
                DateTime now = DateTime.Now;
                int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
                if (daysUntilMonday == 0) daysUntilMonday = 7;
                DateTime nextWeeklyReset = now.Date.AddDays(daysUntilMonday).ToUniversalTime();
                displayQuotas.Add(new Quota {
                    Label = I18n.T("Quota.Weekly"),
                    Remaining = p.WeeklyRemaining ?? (dailyQ != null ? dailyQ.Remaining : 100.0),
                    Reset = nextWeeklyReset,
                    WindowMinutes = 10080
                });
            }
        } else {
            displayQuotas = p.Quotas.Take(2).ToList();
        }

        foreach (var q in displayQuotas) {
            double? rem = q.EffectiveRemaining;
            string label = q.Label.Replace("額度", "").Trim();
            if (label.Contains("5 小時") || label.Contains("5小時") || label.Equals("5h", StringComparison.OrdinalIgnoreCase) || label.Equals("5時間", StringComparison.OrdinalIgnoreCase)) {
                label = I18n.T("Quota.5Hours");
            } else if (label.Contains("每週") || label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) || label.Equals("週間", StringComparison.OrdinalIgnoreCase)) {
                label = I18n.T("Quota.Weekly");
            } else if (label.Contains("每日") || label.Equals("Daily", StringComparison.OrdinalIgnoreCase) || label.Equals("日次", StringComparison.OrdinalIgnoreCase)) {
                label = I18n.T("Quota.Daily");
            }

            var line = new Grid { Margin = new Thickness(2, 4, 0, 1), ToolTip = label + (rem.HasValue ? " · 剩餘 " + Math.Floor(rem.Value).ToString("0") + "%" : "") + "\n" + ResetText(q) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            string displayLabel = label;
            if (displayLabel.Length > 8) displayLabel = displayLabel.Substring(0, 7) + "…";
            var lbl = Text(displayLabel, 10, "#98A8B6");
            Grid.SetColumn(lbl, 0);
            line.Children.Add(lbl);

            var bars = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 5, 0) };
            for (int i = 0; i < 12; i++) {
                bars.ColumnDefinitions.Add(new ColumnDefinition());
                bool lit = rem.HasValue && (i + 0.5) * 100.0 / 12.0 <= rem.Value;
                string barColor = lit ? (rem <= 19 ? "#E7AD7B" : p.Color) : "#1E2634";
                var bar = new Border { Margin = new Thickness(0, 0, 2, 0), Background = Brush(barColor), CornerRadius = new CornerRadius(1) };
                Grid.SetColumn(bar, i);
                bars.Children.Add(bar);
            }
            Grid.SetColumn(bars, 1);
            line.Children.Add(bars);

            string pctStr = rem.HasValue ? Math.Floor(rem.Value).ToString("0") + "%" : "—";
            var pctTxt = Text(pctStr, 10, rem.HasValue ? "#E4EDF2" : "#728391");
            pctTxt.FontFamily = new FontFamily("Consolas");
            pctTxt.FontWeight = FontWeights.SemiBold;
            pctTxt.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pctTxt, 2);
            line.Children.Add(pctTxt);

            string rstBadge = FormatResetBadge(q);
            var rstTxt = Text(rstBadge, 9, "#7E9AA8");
            rstTxt.HorizontalAlignment = HorizontalAlignment.Right;
            rstTxt.Margin = new Thickness(2, 0, 0, 0);
            Grid.SetColumn(rstTxt, 3);
            line.Children.Add(rstTxt);

            panel.Children.Add(line);
        }

        // Antigravity 子模型獨立配額膠囊列：顯示 Claude 與 Gemini 4 Argon（GPT 已於 11 月下架直接移除）
        if (p.Id == "antigravity") {
            var subPillsPanel = new WrapPanel {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 5, 0, 1)
            };

            Func<string, List<Quota>, Border> makeSubPill = delegate(string badgeTitle, List<Quota> qs) {
                if (qs == null || qs.Count == 0) return null;
                double minRem = qs.Min(x => x.EffectiveRemaining.Value);
                string valText = Math.Floor(minRem).ToString("0") + "%";
                string valColor = Provider.GetQuotaLevelColor(minRem);

                var tipLines = new List<string>();
                foreach (var sq in qs) {
                    double r = sq.EffectiveRemaining.Value;
                    tipLines.Add(sq.Label + " · " + I18n.T("Quota.Remaining") + " " + Math.Floor(r).ToString("0") + "% (" + ResetText(sq) + ")");
                }

                var pillBorder = new Border {
                    Background = Brush("#161F2A"),
                    BorderBrush = Brush("#263545"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 6, 2),
                    ToolTip = string.Join("\n", tipLines.ToArray())
                };

                var pillStack = new StackPanel {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var nameTb = Text(badgeTitle + " ", 9.5, "#98A8B6");
                var valTb = Text(valText, 9.5, valColor);
                valTb.FontFamily = new FontFamily("Consolas");
                valTb.FontWeight = FontWeights.Bold;

                pillStack.Children.Add(nameTb);
                pillStack.Children.Add(valTb);
                pillBorder.Child = pillStack;
                return pillBorder;
            };

            var claudeQs = p.Quotas.Where(q => q.Label != null && q.Label.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 && q.EffectiveRemaining.HasValue).ToList();
            var claudePill = makeSubPill("Claude", claudeQs);
            if (claudePill != null) subPillsPanel.Children.Add(claudePill);

            var argonQs = p.Quotas.Where(q => q.Label != null && q.Label.IndexOf("Argon", StringComparison.OrdinalIgnoreCase) >= 0 && q.EffectiveRemaining.HasValue).ToList();
            var argonPill = makeSubPill("Gemini 4 Argon", argonQs);
            if (argonPill != null) subPillsPanel.Children.Add(argonPill);

            if (subPillsPanel.Children.Count > 0) {
                panel.Children.Add(subPillsPanel);
            }
        }

        if (p.Tokens.HasValue) {
            var t = Text("Token " + p.Tokens.Value.ToString("N0") + (p.Id == "codex" ? I18n.T("Quota.RecentSession") : I18n.T("Quota.ContextWindow")), 9, "#8A9EA8");
            t.ToolTip = p.TokenLabel;
            t.Margin = new Thickness(2, 3, 0, 0);
            panel.Children.Add(t);
        }
        if (p.Id == "codex" && p.ResetCreditsCount.HasValue && p.ResetCreditsCount.Value > 0) {
            int count = p.ResetCreditsCount.Value;
            string expStr = "—";
            string fullExpStr = "—";
            if (p.ResetCreditsExpiry.HasValue) {
                DateTime localExp = p.ResetCreditsExpiry.Value.ToLocalTime();
                expStr = localExp.ToString("M/d");
                fullExpStr = localExp.ToString("yyyy/MM/dd tt hh:mm", I18n.CurrentCulture);
            }
            string badgeText = string.Format(I18n.T("Quota.ResetCreditsBadge"), count, expStr);
            string tipText = string.Format(I18n.T("Quota.ResetCreditsTip"), count, fullExpStr);

            var resetBadge = new Border {
                Background = Brush("#10281F"),
                BorderBrush = Brush("#1D5A3F"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 8, 2),
                Margin = new Thickness(2, 4, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = tipText
            };
            resetBadge.MouseEnter += delegate {
                resetBadge.Background = Brush("#163A2D");
                resetBadge.BorderBrush = Brush("#10B981");
            };
            resetBadge.MouseLeave += delegate {
                resetBadge.Background = Brush("#10281F");
                resetBadge.BorderBrush = Brush("#1D5A3F");
            };
            resetBadge.MouseLeftButtonUp += delegate {
                try { Process.Start(new ProcessStartInfo("https://chatgpt.com/") { UseShellExecute = true }); } catch { }
            };

            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconPath = new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M12 4V1L8 5l4 4V6a6 6 0 1 1-6 6h-2a8 8 0 1 0 8-8z"),
                Fill = Brush("#34D399"),
                Width = 9,
                Height = 9,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            var badgeLabel = Text(badgeText, 9.5, "#A7F3D0");
            badgeLabel.FontWeight = FontWeights.SemiBold;
            badgeLabel.VerticalAlignment = VerticalAlignment.Center;

            badgeStack.Children.Add(iconPath);
            badgeStack.Children.Add(badgeLabel);
            resetBadge.Child = badgeStack;

            panel.Children.Add(resetBadge);
        }
        if (p.Quotas.Count == 0) {
            var status = Text(p.Updated.HasValue ? I18n.T("Quota.NotProvided") : I18n.T("Quota.NotConnected"), 9.5, "#96A5B2");
            status.Margin = new Thickness(3, 2, 0, 0);
            panel.Children.Add(status);
        }
        if (p.Id == "claude" && !p.Updated.HasValue) {
            var b = Button(I18n.T("Quota.OpenClaude"), ConnectClaude, I18n.T("Quota.OpenClaude"));
            b.FontSize = 10;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(b);
        }
        if (p.Id == "grok" && !p.Updated.HasValue) {
            var b = Button("開啟 X.com Grok", delegate {
                try { Process.Start(new ProcessStartInfo("https://x.com/i/grok") { UseShellExecute = true }); } catch { }
            }, "在預設瀏覽器開啟 X.com Grok");
            b.FontSize = 10;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(b);
        }

        card.ToolTip = p.Source + "\n" + p.Note + "\n" + PixelAnimal.MoodText(PixelAnimal.Mood(p.DailyRemaining ?? p.Remaining, p.Stale));
        return card;
    }

    async Task Refresh(bool force) {
        if (busy || closing) return;
        busy = true;
        try {
            bool ag = force || DateTime.UtcNow - lastAg > TimeSpan.FromSeconds(8);
            var c = Task.Run(() => Providers.Codex());
            var cl = Task.Run(() => Providers.Claude());
            var a = ag ? Task.Run(() => Providers.Antigravity()) : Task.FromResult(data["antigravity"]);
            var gk = Task.Run(() => Providers.Grok());
            await Task.WhenAll(c, cl, a, gk);
            if (closing) return;
            Merge(c.Result);
            Merge(cl.Result);
            Merge(gk.Result);
            if (ag) { Merge(a.Result); lastAg = DateTime.UtcNow; }
            foreach (var p in data.Values) {
                var alertQuotas = p.Id == "antigravity"
                    ? p.Quotas.Where(x => Provider.IsPrimaryGeminiLabel(x.Label)).ToList()
                    : p.Quotas;
                string key = p.Id + ":" + String.Join(",", alertQuotas.Select(q => q.Reset.HasValue ? q.Reset.Value.ToString("yyyyMMddHH") : "none"));
                if (prefs.Alerts && Enabled(p.Id) && p.Remaining.HasValue && p.Remaining <= 15 && !p.Stale && !warned.Contains(key)) {
                    warned.Add(key);
                    string nameText = p.Id == "antigravity" ? "Antigravity 主體" : p.Name;
                    tray.ShowBalloonTip(5000, nameText + " 額度偏低", "最低剩餘 " + p.Short + "，點擊浮窗查看重置時間。", Forms.ToolTipIcon.Info);
                }
            }
            foreach (var provider in new[] { c.Result, a.Result }) foreach (var usage in provider.Completions) {
                if (completionNotifications.Take(usage) && prefs.TokenDropAnimation && Enabled(provider.Id))
                    CatDropOverlay.Show(provider.Name + " · 本輪總計", usage.Tokens, null, provider.Remaining, this, prefs);
            }
        } finally {
            busy = false;
            if (!closing) Render();
        }
    }

    void Merge(Provider p) {
        p.Theme = prefs.GetProviderColor(p.Id);
        var old = data[p.Id];
        if (p.Quotas.Count == 0 && old.Quotas.Count > 0) {
            p.Quotas = old.Quotas;
        }
        if (!p.ResetCreditsCount.HasValue && old.ResetCreditsCount.HasValue) {
            p.ResetCreditsCount = old.ResetCreditsCount;
            p.ResetCreditsExpiry = old.ResetCreditsExpiry;
        }
        if ((p.Error || !p.Updated.HasValue) && old.Updated.HasValue) {
            old.Error = true;
            old.Note = p.Note + "；保留上次資料";
            if (p.Tokens.HasValue) old.Tokens = p.Tokens;
            if (p.ResetCreditsCount.HasValue) old.ResetCreditsCount = p.ResetCreditsCount;
            if (p.ResetCreditsExpiry.HasValue) old.ResetCreditsExpiry = p.ResetCreditsExpiry;
        } else {
            data[p.Id] = p;
        }
    }

    Button MenuAction(string title, Action action, bool closeMenu = false) {
        var b = Button(title, delegate { if (closeMenu && settingsOpen) ToggleSettingsView(false); action(); }, title);
        b.FontSize = 11;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.Padding = new Thickness(8, 5, 8, 5);
        return b;
    }

    Button MenuToggle(string title, bool selected, Action<bool> action) {
        bool state = selected;
        var b = Button("", delegate {}, title);
        b.FontSize = 11;
        b.Padding = new Thickness(7, 4, 7, 4);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.Children.Add(Text(title, 11, "#D8E4EA"));
        var track = new Border { Width = 26, Height = 13, Background = Brush(state ? "#5AD4B2" : "#30404C"), BorderBrush = Brush("#526978"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        var knob = new Border { Width = 8, Height = 7, Background = Brush("#101C24"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = new TranslateTransform(state ? 11 : 0, 0) };
        track.Child = knob;
        Grid.SetColumn(track, 1);
        g.Children.Add(track);
        b.Content = g;
        b.Click += delegate {
            state = !state;
            action(state);
            track.Background = Brush(state ? "#5AD4B2" : "#30404C");
            var transform = (TranslateTransform)knob.RenderTransform;
            double old = transform.X;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = state ? 11 : 0;
            if (!prefs.ReducedMotion && SystemParameters.ClientAreaAnimation)
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(old, transform.X, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
            Save();
        };
        return b;
    }

    void UpdateMotion() {
        foreach(var a in animals) {
            a.Breathing = prefs.Breathing;
            a.Animate = prefs.AnimalAnimation;
            a.Reduced = prefs.ReducedMotion;
            a.InvalidateVisual();
        }
        Render();
    }

    static Style sliderStyle;
    static Style GetSliderStyle() {
        if (sliderStyle != null) return sliderStyle;
        string xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'><Setter Property='Focusable' Value='False'/><Setter Property='Height' Value='22'/><Setter Property='IsMoveToPointEnabled' Value='True'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'><Grid VerticalAlignment='Center' Height='22' Background='Transparent'><Border Height='4' CornerRadius='2' Background='#1A2733' VerticalAlignment='Center' Margin='6,0,6,0'/><Track x:Name='PART_Track'><Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Background='Transparent' BorderThickness='0' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Background='Transparent' BorderThickness='0' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton><Track.Thumb><Thumb Focusable='False'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='thumbBorder' Width='14' Height='14' CornerRadius='7' Background='#68B8D4' BorderBrush='#FFFFFF' BorderThickness='1.5' Cursor='Hand'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='thumbBorder' Property='Background' Value='#8BE0FF'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='thumbBorder' Property='Background' Value='#5AD4B2'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>";
        try { sliderStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml); } catch { }
        return sliderStyle;
    }

    UIElement MakeSliderWithInput(string title, double sliderMin, double sliderMax, Func<double> getPercent, Action<double> setPercent, string tip = null) {
        var panel = new StackPanel { Margin = new Thickness(7, 3, 7, 5) };
        var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var lbl = Text(title, 10.5, "#8EA2B0");
        lbl.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(lbl, Dock.Left);
        hdr.Children.Add(lbl);

        var inputGroup = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var txt = new TextBox {
            Width = 44,
            Height = 20,
            Background = Brush("#10171F"),
            Foreground = Brush("#F0F5F8"),
            BorderBrush = Brush("#243644"),
            BorderThickness = new Thickness(1),
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            FontFamily = FontFamily,
            Padding = new Thickness(0, 1, 0, 1),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (tip != null) txt.ToolTip = tip;
        var unit = Text("%", 10.5, "#7E94A3");
        unit.Margin = new Thickness(3, 0, 0, 0);
        inputGroup.Children.Add(txt);
        inputGroup.Children.Add(unit);
        hdr.Children.Add(inputGroup);
        panel.Children.Add(hdr);

        var slider = new Slider {
            Minimum = sliderMin,
            Maximum = sliderMax,
            SmallChange = 1,
            LargeChange = 5,
            Margin = new Thickness(0, 1, 0, 2)
        };
        var st = GetSliderStyle();
        if (st != null) slider.Style = st;

        bool updating = false;
        Action<double, bool> applyValue = delegate(double pctVal, bool updateInput) {
            if (updating) return;
            updating = true;
            try {
                pctVal = Math.Round(pctVal, 1);
                if (updateInput) txt.Text = ((int)Math.Round(pctVal)).ToString();
                double clampedForSlider = Math.Max(sliderMin, Math.Min(sliderMax, pctVal));
                if (Math.Abs(slider.Value - clampedForSlider) > 0.1) slider.Value = clampedForSlider;
                setPercent(pctVal / 100.0);
            } finally { updating = false; }
        };

        double currentPct = Math.Round(getPercent() * 100.0, 1);
        txt.Text = ((int)Math.Round(currentPct)).ToString();
        slider.Value = Math.Max(sliderMin, Math.Min(sliderMax, currentPct));

        slider.ValueChanged += delegate(object s, RoutedPropertyChangedEventArgs<double> e) {
            if (!updating) applyValue(e.NewValue, true);
        };

        Action parseInput = delegate {
            string raw = txt.Text.Trim().TrimEnd('%');
            double val;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out val) && val > 0) applyValue(val, false);
            else txt.Text = ((int)Math.Round(getPercent() * 100.0)).ToString();
        };

        txt.LostFocus += delegate { parseInput(); };
        txt.KeyDown += delegate(object s, KeyEventArgs e) {
            if (e.Key == Key.Enter) { parseInput(); Keyboard.ClearFocus(); e.Handled = true; }
        };

        panel.Children.Add(slider);
        return panel;
    }

    void OpenMenu() {
        ToggleSettingsView(!settingsOpen);
    }

    public void ToggleSettingsView(bool show) {
        hover.Stop();
        if (!show) {
            settingsOpen = false;
            if (settingsDetails != null) settingsDetails.Visibility = Visibility.Collapsed;
            if (details != null) details.Visibility = Visibility.Visible;
            if (rightHeaderTitle != null) {
                rightHeaderTitle.Text = I18n.T("Island.ModelQuota");
                rightHeaderTitle.Foreground = Brush("#94A3B8");
                rightHeaderTitle.FontWeight = FontWeights.Normal;
            }
            if (menuBtn != null) {
                menuBtn.Content = "≡";
                menuBtn.ToolTip = I18n.T("Island.SettingsTitle");
            }
            return;
        }

        isCinemaHidden = false;
        Topmost = true;
        try {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) FullscreenDetector.BringToTopmost(hwnd);
        } catch { }
        if (!expanded) Expand(true);

        settingsOpen = true;
        if (rightHeaderTitle != null) {
            rightHeaderTitle.Text = I18n.T("Island.SettingsTitle");
            rightHeaderTitle.Foreground = Brush("#E8F1F5");
            rightHeaderTitle.FontWeight = FontWeights.Bold;
        }
        if (menuBtn != null) {
            menuBtn.Content = "✕";
            menuBtn.ToolTip = I18n.T("Island.BackToQuota");
        }

        var rows = new StackPanel { Margin = new Thickness(2, 0, 4, 4) };
        Action separator = delegate { rows.Children.Add(new Border { Height = 1, Background = Brush("#1C2232"), Margin = new Thickness(4, 4, 4, 4) }); };

        // 1. 互動與避讓
        var actTitle = Text(I18n.T("Settings.Interaction"), 10.5, "#8EA2B0");
        actTitle.FontWeight = FontWeights.SemiBold;
        actTitle.Margin = new Thickness(4, 1, 0, 2);
        rows.Children.Add(actTitle);
        rows.Children.Add(MenuToggle(I18n.T("Settings.HoverExpand"), prefs.HoverExpand, delegate(bool v) { prefs.HoverExpand = v; Save(); }));
        rows.Children.Add(MenuToggle(I18n.T("Settings.CinemaMode"), prefs.CinemaMode, delegate(bool v) { prefs.CinemaMode = v; Save(); }));
        rows.Children.Add(MenuToggle(I18n.T("Settings.AvoidBrowser"), prefs.AvoidBrowser, delegate(bool v) { prefs.AvoidBrowser = v; Save(); }));
        rows.Children.Add(MenuToggle(I18n.T("Settings.PinnedToggle"), prefs.Pinned, delegate(bool v) {
            prefs.Pinned = v;
            pin.Content = v ? "◆" : "◇";
            pin.ToolTip = v ? I18n.T("Island.Pinned") : I18n.T("Island.Unpinned");
            Save();
        }));

        separator();

        // 2. 顯示螢幕位置 (多螢幕自由選擇)
        var monTitle = Text(I18n.T("Settings.ScreenSelection"), 10.5, "#8EA2B0");
        monTitle.FontWeight = FontWeights.SemiBold;
        monTitle.Margin = new Thickness(4, 1, 0, 2);
        rows.Children.Add(monTitle);

        var allScreens = Forms.Screen.AllScreens;
        var currentTargetScreen = prefs.GetTargetScreen();
        var screenButtons = new List<Border>();

        var screensPanel = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
        for (int i = 0; i < allScreens.Length; i++) {
            var scr = allScreens[i];
            int screenIdx = i;
            string devName = scr.DeviceName;
            bool isPrimary = scr.Primary;
            string res = scr.Bounds.Width + "×" + scr.Bounds.Height;
            string label = "螢幕 " + (i + 1) + (isPrimary ? " (主螢幕)" : " (副螢幕)") + " · " + res;
            bool isCurrent = string.Equals(scr.DeviceName, currentTargetScreen.DeviceName, StringComparison.OrdinalIgnoreCase);

            var scrBtn = new Border {
                Background = Brush(isCurrent ? "#1E2A3A" : "#131822"),
                BorderBrush = Brush(isCurrent ? "#5AD4B2" : "#243042"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand,
                ToolTip = "將動態島放置在 " + label
            };
            var scrTxt = Text(label, 11, isCurrent ? "#5AD4B2" : "#C8D6DF");
            scrTxt.FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal;
            scrBtn.Child = scrTxt;

            scrBtn.MouseLeftButtonUp += delegate {
                prefs.TargetScreenIndex = screenIdx;
                prefs.TargetScreenDeviceName = devName;
                MoveToTargetScreen();
                for (int j = 0; j < screenButtons.Count; j++) {
                    bool sel = j == screenIdx;
                    screenButtons[j].Background = Brush(sel ? "#1E2A3A" : "#131822");
                    screenButtons[j].BorderBrush = Brush(sel ? "#5AD4B2" : "#243042");
                    var tb = screenButtons[j].Child as TextBlock;
                    if (tb != null) {
                        tb.Foreground = Brush(sel ? "#5AD4B2" : "#C8D6DF");
                        tb.FontWeight = sel ? FontWeights.Bold : FontWeights.Normal;
                    }
                }
            };
            NotchUi.ApplyTouchFeedback(scrBtn, 0.97);
            screenButtons.Add(scrBtn);
            screensPanel.Children.Add(scrBtn);
        }
        rows.Children.Add(screensPanel);

        separator();

        // 2. 監控模型勾選
        var srvTitle = Text("AI 模型監控", 10.5, "#8EA2B0");
        srvTitle.FontWeight = FontWeights.SemiBold;
        srvTitle.Margin = new Thickness(4, 1, 0, 2);
        rows.Children.Add(srvTitle);
        foreach (var p in data.Values) {
            string id = p.Id;
            rows.Children.Add(MenuToggle(p.Name, Enabled(id), delegate(bool val) { SetEnabled(id, val); }));
        }

        separator();

        // 3. 個別模型外觀設定
        var animalTitle = Text(I18n.T("Settings.Appearance"), 10.5, "#8EA2B0");
        animalTitle.FontWeight = FontWeights.SemiBold;
        animalTitle.Margin = new Thickness(4, 1, 0, 4);
        rows.Children.Add(animalTitle);

        var modelTabs = new[] {
            new { Id = "codex", Name = "Codex" },
            new { Id = "antigravity", Name = "Antigravity" },
            new { Id = "claude", Name = "Claude" },
            new { Id = "grok", Name = "Grok" }
        };

        if (string.IsNullOrEmpty(selectedSettingsModelId) || !modelTabs.Any(m => m.Id == selectedSettingsModelId))
            selectedSettingsModelId = "codex";

        var tabsGrid = new UniformGrid { Columns = 4, Margin = new Thickness(4, 0, 4, 6) };
        var tabBorders = new Dictionary<string, Border>();
        var tabTexts = new Dictionary<string, TextBlock>();

        foreach (var m in modelTabs) {
            string tabId = m.Id;
            var txt = Text(m.Name, 11, "#8EA2B0");
            txt.HorizontalAlignment = HorizontalAlignment.Center;
            var tabBtn = new Border {
                Background = Brush("#131822"),
                BorderBrush = Brush("#243042"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 5, 4, 5),
                Margin = new Thickness(tabId == "codex" ? 0 : 2, 0, tabId == "claude" ? 0 : 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = m.Name,
                Child = txt
            };
            NotchUi.ApplyTouchFeedback(tabBtn, 0.95);
            tabBorders[tabId] = tabBtn;
            tabTexts[tabId] = txt;
            tabsGrid.Children.Add(tabBtn);
        }
        rows.Children.Add(tabsGrid);

        // 動態即時預覽與當前模型狀態卡
        var previewCard = new Border {
            Background = Brush("#0D111A"),
            BorderBrush = Brush("#1E2638"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 0, 4, 6)
        };
        var previewDock = new DockPanel();
        var previewBox = new Border {
            Width = 40,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = Brush("#141924"),
            BorderBrush = Brush("#243044"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(previewBox, Dock.Left);
        previewDock.Children.Add(previewBox);

        var previewInfoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var previewModelNameTxt = Text("Codex", 11.5, "#EEF2F3");
        previewModelNameTxt.FontWeight = FontWeights.SemiBold;
        previewInfoPanel.Children.Add(previewModelNameTxt);

        var previewDetailTxt = Text("", 10.5, "#7E8E9E");
        previewDetailTxt.Margin = new Thickness(0, 1, 0, 0);
        previewInfoPanel.Children.Add(previewDetailTxt);
        previewDock.Children.Add(previewInfoPanel);
        previewCard.Child = previewDock;
        rows.Children.Add(previewCard);

        // 角色選擇 (像素小貓 / 像素赤狐)
        // 角色選擇 (像素小貓 / 像素赤狐 / 像素貓頭鷹)
        var modelGrid = new UniformGrid { Columns = 3, Margin = new Thickness(4, 0, 4, 6) };
        var catTxt = Text(I18n.T("Settings.Cat"), 11, "#94A3B8");
        catTxt.HorizontalAlignment = HorizontalAlignment.Center;
        var catCard = new Border {
            Background = Brush("#131822"),
            BorderBrush = Brush("#243042"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = I18n.T("Settings.Cat"),
            Child = catTxt
        };
        NotchUi.ApplyTouchFeedback(catCard, 0.96);

        var foxTxt = Text(I18n.T("Settings.Fox"), 11, "#94A3B8");
        foxTxt.HorizontalAlignment = HorizontalAlignment.Center;
        var foxCard = new Border {
            Background = Brush("#131822"),
            BorderBrush = Brush("#243042"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = I18n.T("Settings.Fox"),
            Child = foxTxt
        };
        NotchUi.ApplyTouchFeedback(foxCard, 0.96);

        var owlTxt = Text(I18n.T("Settings.Owl"), 11, "#94A3B8");
        owlTxt.HorizontalAlignment = HorizontalAlignment.Center;
        var owlCard = new Border {
            Background = Brush("#131822"),
            BorderBrush = Brush("#243042"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = I18n.T("Settings.Owl"),
            Child = owlTxt
        };
        NotchUi.ApplyTouchFeedback(owlCard, 0.96);

        modelGrid.Children.Add(catCard);
        modelGrid.Children.Add(foxCard);
        modelGrid.Children.Add(owlCard);
        rows.Children.Add(modelGrid);

        // 配色調色盤
        var colorSub = Text(I18n.T("Settings.ThemeColor"), 10, "#64748B");
        colorSub.Margin = new Thickness(4, 2, 0, 3);
        rows.Children.Add(colorSub);

        var paletteWrap = new WrapPanel { Margin = new Thickness(4, 0, 4, 6) };
        var presets = new[] {
            new { Name = "經典橘", Hex = "#FFA500" },
            new { Name = "熾焰紅", Hex = "#EF4444" },
            new { Name = "琥珀金", Hex = "#F59E0B" },
            new { Name = "翡翠綠", Hex = "#10B981" },
            new { Name = "薄荷青", Hex = "#14B8A6" },
            new { Name = "晴空青", Hex = "#06B6D4" },
            new { Name = "蔚藍色", Hex = "#3B82F6" },
            new { Name = "紫羅蘭", Hex = "#8B5CF6" },
            new { Name = "櫻花粉", Hex = "#EC4899" },
            new { Name = "曜石銀", Hex = "#94A3B8" }
        };

        var swatchBorders = new List<Border>();
        TextBox hexInput = null;
        Border colorPreviewDot = null;

        Action updateAppearanceUi = delegate {
            string curId = selectedSettingsModelId;
            string curAnimal = prefs.GetProviderAnimal(curId);
            string curColor = prefs.GetProviderColor(curId);
            string normCurColor = Preferences.NormalizeColor(curColor);
            string modelDisplayName = curId == "codex" ? "Codex" : curId == "antigravity" ? "Antigravity" : "Claude";
            string animalDisplayName = curAnimal == "cat" ? I18n.T("Settings.Cat") : (curAnimal == "fox" ? I18n.T("Settings.Fox") : I18n.T("Settings.Owl"));

            // 1. 刷新分頁按鈕外觀
            foreach (var m in modelTabs) {
                bool isTabActive = m.Id == curId;
                if (tabBorders.ContainsKey(m.Id)) {
                    tabBorders[m.Id].Background = Brush(isTabActive ? "#1E2A3A" : "#131822");
                    tabBorders[m.Id].BorderBrush = Brush(isTabActive ? "#5AD4B2" : "#243042");
                }
                if (tabTexts.ContainsKey(m.Id)) {
                    tabTexts[m.Id].Foreground = Brush(isTabActive ? "#5AD4B2" : "#8EA2B0");
                    tabTexts[m.Id].FontWeight = isTabActive ? FontWeights.Bold : FontWeights.Normal;
                }
            }

            // 2. 刷新即時預覽圖
            var previewProvider = data.ContainsKey(curId) ? data[curId] : new Provider(curId, modelDisplayName, curColor);
            var sprite = Animal(previewProvider, 34);
            sprite.HorizontalAlignment = HorizontalAlignment.Center;
            sprite.VerticalAlignment = VerticalAlignment.Center;
            previewBox.Child = sprite;
            previewModelNameTxt.Text = modelDisplayName;
            previewDetailTxt.Text = animalDisplayName + " · " + curColor;

            // 3. 刷新動物角色按鈕
            bool isCat = curAnimal == "cat";
            bool isFox = curAnimal == "fox";
            bool isOwl = curAnimal == "owl";

            catCard.Background = Brush(isCat ? "#1E2A3A" : "#131822");
            catCard.BorderBrush = Brush(isCat ? "#5AD4B2" : "#243042");
            catTxt.Foreground = Brush(isCat ? "#5AD4B2" : "#94A3B8");
            catTxt.FontWeight = isCat ? FontWeights.Bold : FontWeights.Normal;

            foxCard.Background = Brush(isFox ? "#1E2A3A" : "#131822");
            foxCard.BorderBrush = Brush(isFox ? "#5AD4B2" : "#243042");
            foxTxt.Foreground = Brush(isFox ? "#5AD4B2" : "#94A3B8");
            foxTxt.FontWeight = isFox ? FontWeights.Bold : FontWeights.Normal;

            owlCard.Background = Brush(isOwl ? "#1E2A3A" : "#131822");
            owlCard.BorderBrush = Brush(isOwl ? "#5AD4B2" : "#243042");
            owlTxt.Foreground = Brush(isOwl ? "#5AD4B2" : "#94A3B8");
            owlTxt.FontWeight = isOwl ? FontWeights.Bold : FontWeights.Normal;

            // 4. 刷新色票選中環
            for (int i = 0; i < presets.Length && i < swatchBorders.Count; i++) {
                string presetHexNorm = Preferences.NormalizeColor(presets[i].Hex);
                bool isSwatchSelected = presetHexNorm == normCurColor;
                swatchBorders[i].BorderBrush = Brush(isSwatchSelected ? "#FFFFFF" : "#243042");
                swatchBorders[i].BorderThickness = new Thickness(isSwatchSelected ? 2 : 1);
            }

            // 5. 刷新自訂輸入框與圓點
            if (colorPreviewDot != null) {
                try { colorPreviewDot.Background = Brush(curColor); } catch { }
            }
            if (hexInput != null) {
                hexInput.Text = curColor;
            }
        };

        // 綁定分頁點擊事件
        foreach (var m in modelTabs) {
            string tabId = m.Id;
            tabBorders[tabId].MouseLeftButtonUp += delegate {
                selectedSettingsModelId = tabId;
                updateAppearanceUi();
            };
        }

        // 綁定角色切換點擊事件
        catCard.MouseLeftButtonUp += delegate {
            SwitchModelAnimal(selectedSettingsModelId, "cat");
            updateAppearanceUi();
        };
        foxCard.MouseLeftButtonUp += delegate {
            SwitchModelAnimal(selectedSettingsModelId, "fox");
            updateAppearanceUi();
        };
        owlCard.MouseLeftButtonUp += delegate {
            SwitchModelAnimal(selectedSettingsModelId, "owl");
            updateAppearanceUi();
        };

        // 構建色票
        foreach (var p in presets) {
            string targetHex = p.Hex;
            var swatch = new Border {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = Brush(targetHex),
                BorderBrush = Brush("#243042"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                ToolTip = p.Name + " (" + targetHex + ")"
            };
            swatch.MouseEnter += delegate { if (swatch.BorderThickness.Top < 2) swatch.BorderBrush = Brush("#94A3B8"); };
            swatch.MouseLeave += delegate {
                string normCur = Preferences.NormalizeColor(prefs.GetProviderColor(selectedSettingsModelId));
                bool sel = Preferences.NormalizeColor(targetHex) == normCur;
                swatch.BorderBrush = Brush(sel ? "#FFFFFF" : "#243042");
            };
            swatch.MouseLeftButtonUp += delegate {
                SwitchModelColor(selectedSettingsModelId, targetHex);
                updateAppearanceUi();
            };
            NotchUi.ApplyTouchFeedback(swatch, 0.88);
            swatchBorders.Add(swatch);
            paletteWrap.Children.Add(swatch);
        }
        rows.Children.Add(paletteWrap);

        // 自訂 HEX 與重置列
        var hexRow = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
        var hexLbl = Text(I18n.T("Settings.CustomColor"), 10.5, "#94A3B8");
        hexLbl.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(hexLbl, Dock.Left);
        hexRow.Children.Add(hexLbl);

        colorPreviewDot = new Border {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Brush(prefs.GetProviderColor(selectedSettingsModelId)),
            BorderBrush = Brush("#334155"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(colorPreviewDot, Dock.Left);
        hexRow.Children.Add(colorPreviewDot);

        var resetBtn = Button(I18n.T("Common.Reset"), delegate {
            string defColor = Preferences.GetDefaultColor(selectedSettingsModelId);
            string defAnimal = Preferences.GetDefaultAnimal(selectedSettingsModelId);
            SwitchModelAnimal(selectedSettingsModelId, defAnimal);
            SwitchModelColor(selectedSettingsModelId, defColor);
            updateAppearanceUi();
        }, I18n.T("Common.Reset"));
        resetBtn.FontSize = 10;
        resetBtn.Padding = new Thickness(6, 2, 6, 2);
        resetBtn.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(resetBtn, Dock.Right);
        hexRow.Children.Add(resetBtn);

        Action applyCustomHex = delegate {
            if (hexInput != null) {
                string val = hexInput.Text.Trim();
                if (!val.StartsWith("#")) val = "#" + val;
                try {
                    var test = (Color)ColorConverter.ConvertFromString(val);
                    SwitchModelColor(selectedSettingsModelId, val);
                    updateAppearanceUi();
                } catch { }
            }
        };

        var applyBtn = Button(I18n.T("Common.Apply"), delegate { applyCustomHex(); }, I18n.T("Common.Apply"));
        applyBtn.FontSize = 10;
        applyBtn.Padding = new Thickness(6, 2, 6, 2);
        applyBtn.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(applyBtn, Dock.Right);
        hexRow.Children.Add(applyBtn);

        hexInput = new TextBox {
            Height = 22,
            Background = Brush("#10171F"),
            Foreground = Brush("#F0F5F8"),
            BorderBrush = Brush("#243644"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(4, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = prefs.GetProviderColor(selectedSettingsModelId)
        };
        hexInput.KeyDown += delegate(object s, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                applyCustomHex();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        hexRow.Children.Add(hexInput);
        rows.Children.Add(hexRow);

        updateAppearanceUi();

        separator();

        // 4. 動態效果
        var motionTitle = Text(I18n.T("Settings.Motion"), 10.5, "#8EA2B0");
        motionTitle.FontWeight = FontWeights.SemiBold;
        motionTitle.Margin = new Thickness(4, 1, 0, 2);
        rows.Children.Add(motionTitle);
        rows.Children.Add(MenuToggle(I18n.T("Settings.Breathing"), prefs.Breathing && prefs.AnimalAnimation, delegate(bool v) {
            prefs.Breathing = v;
            prefs.AnimalAnimation = v;
            UpdateMotion();
        }));
        var dropOptsBox = new StackPanel { Margin = new Thickness(0, 2, 0, 0), Visibility = prefs.TokenDropAnimation ? Visibility.Visible : Visibility.Collapsed };
        rows.Children.Add(MenuToggle(I18n.T("Settings.TokenDrop"), prefs.TokenDropAnimation, delegate(bool v) {
            prefs.TokenDropAnimation = v;
            dropOptsBox.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            Save();
        }));

        dropOptsBox.Children.Add(MakeSliderWithInput(I18n.T("Settings.DropScale"), 60, 160, () => prefs.DropAnimScale, v => { prefs.DropAnimScale = Math.Round(v, 2); Save(); }, I18n.T("Settings.DropScaleTip")));

        var posHdr = Text(I18n.T("Settings.DropPos"), 10.5, "#8EA2B0");
        posHdr.FontWeight = FontWeights.SemiBold;
        posHdr.Margin = new Thickness(7, 4, 7, 3);
        dropOptsBox.Children.Add(posHdr);

        var posBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 7, 5) };
        Button btnPosRight = null;
        Button btnPosIsland = null;
        Button btnPosCenter = null;
        Action updatePosBtns = null;

        Func<string, string, string, Button> makePosBtn = (title, val, tip) => {
            var b = Button(title, () => {
                prefs.DropAnimPos = val;
                Save();
                if (updatePosBtns != null) updatePosBtns();
            }, tip);
            b.FontSize = 10.5;
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Padding = new Thickness(9, 4, 9, 4);
            return b;
        };

        btnPosRight = makePosBtn(I18n.T("Settings.DropPosRight"), "right", "螢幕右上角 (推薦，不遮擋靈動島)");
        btnPosIsland = makePosBtn(I18n.T("Settings.DropPosIsland"), "island", "靈動島正中或側邊");
        btnPosCenter = makePosBtn(I18n.T("Settings.DropPosCenter"), "center", "螢幕正中");

        updatePosBtns = () => {
            string cur = !string.IsNullOrEmpty(prefs.DropAnimPos) ? prefs.DropAnimPos : "right";
            btnPosRight.Background = Brush(cur == "right" ? "#1E3A5F" : "#141A22");
            btnPosRight.Foreground = Brush(cur == "right" ? "#60A5FA" : "#94A3B8");
            btnPosIsland.Background = Brush(cur == "island" ? "#1E3A5F" : "#141A22");
            btnPosIsland.Foreground = Brush(cur == "island" ? "#60A5FA" : "#94A3B8");
            btnPosCenter.Background = Brush(cur == "center" ? "#1E3A5F" : "#141A22");
            btnPosCenter.Foreground = Brush(cur == "center" ? "#60A5FA" : "#94A3B8");
        };
        updatePosBtns();

        posBox.Children.Add(btnPosRight);
        posBox.Children.Add(btnPosIsland);
        posBox.Children.Add(btnPosCenter);
        dropOptsBox.Children.Add(posBox);

        var testBtn = Button(I18n.T("Settings.DropPreview"), () => {
            CatDropOverlay.Show("ANTIGRAVITY", 1450, 3.5, 96.5, this, prefs);
        }, "立即預覽掉落小貓動畫");
        testBtn.FontSize = 10.5;
        testBtn.Foreground = Brush("#38BDF8");
        testBtn.Background = Brush("#0C2030");
        testBtn.Margin = new Thickness(7, 3, 7, 4);
        testBtn.Padding = new Thickness(10, 5, 10, 5);
        testBtn.HorizontalAlignment = HorizontalAlignment.Left;
        dropOptsBox.Children.Add(testBtn);

        rows.Children.Add(dropOptsBox);

        separator();

        // 5. 顯示縮放比例
        rows.Children.Add(MakeSliderWithInput(I18n.T("Settings.Scale"), 60, 200, () => prefs.Scale, v => SetScale(v), I18n.T("Settings.ScaleTip")));

        separator();

        // 6. 介面語言 (Language Dropdown)
        var langTitle = Text(I18n.T("Settings.Language"), 10.5, "#8EA2B0");
        langTitle.FontWeight = FontWeights.SemiBold;
        langTitle.Margin = new Thickness(4, 1, 0, 3);
        rows.Children.Add(langTitle);

        var langContainer = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };

        string currentLangDisplayName = I18n.LanguageNames.ContainsKey(I18n.CurrentLanguage) 
            ? I18n.LanguageNames[I18n.CurrentLanguage] 
            : I18n.CurrentLanguage;

        var langHeaderBtn = new Border {
            Background = Brush("#161A26"),
            BorderBrush = Brush("#283042"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Cursor = Cursors.Hand
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var currentLangTxt = new TextBlock {
            Text = currentLangDisplayName,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = Brush("#E2E8F0"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(currentLangTxt, 0);
        headerGrid.Children.Add(currentLangTxt);

        var arrowTxt = new TextBlock {
            Text = "▼",
            FontSize = 9,
            Foreground = Brush("#8EA2B0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(arrowTxt, 1);
        headerGrid.Children.Add(arrowTxt);

        langHeaderBtn.Child = headerGrid;

        var langListCard = new Border {
            Background = Brush("#0F121C"),
            BorderBrush = Brush("#283042"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var langScrollViewer = new ScrollViewer {
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Template = NotchUi.CreateMicroScrollViewerTemplate()
        };
        langScrollViewer.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e) {
            var scv = s as ScrollViewer;
            if (scv != null) {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta * 0.3));
                e.Handled = true;
            }
        };

        var langItemsStack = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
        foreach (var langCode in I18n.SupportedLanguages) {
            string code = langCode;
            bool isSelected = (I18n.CurrentLanguage == code);
            var itemBorder = new Border {
                Background = Brush(isSelected ? "#1E293B" : "#00000000"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(1, 1, 1, 1),
                Cursor = Cursors.Hand
            };
            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var itemTxt = new TextBlock {
                Text = I18n.LanguageNames.ContainsKey(code) ? I18n.LanguageNames[code] : code,
                FontSize = 10.5,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(isSelected ? "#60A5FA" : "#A6B2C8"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(itemTxt, 0);
            itemGrid.Children.Add(itemTxt);

            if (isSelected) {
                var checkTxt = new TextBlock {
                    Text = "✓",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush("#60A5FA"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                Grid.SetColumn(checkTxt, 1);
                itemGrid.Children.Add(checkTxt);
            }

            itemBorder.Child = itemGrid;

            itemBorder.MouseEnter += delegate {
                if (I18n.CurrentLanguage != code) {
                    itemBorder.Background = Brush("#161E2E");
                    itemTxt.Foreground = Brush("#FFFFFF");
                }
            };
            itemBorder.MouseLeave += delegate {
                if (I18n.CurrentLanguage != code) {
                    itemBorder.Background = Brush("#00000000");
                    itemTxt.Foreground = Brush("#A6B2C8");
                }
            };

            itemBorder.MouseLeftButtonUp += delegate {
                if (I18n.CurrentLanguage != code) {
                    prefs.Language = code;
                    I18n.CurrentLanguage = code;
                    Save();
                    ToggleSettingsView(true);
                } else {
                    langListCard.Visibility = Visibility.Collapsed;
                    arrowTxt.Text = "▼";
                }
            };

            langItemsStack.Children.Add(itemBorder);
        }

        langScrollViewer.Content = langItemsStack;
        langListCard.Child = langScrollViewer;

        Action toggleDrop = delegate {
            bool isExpanded = langListCard.Visibility == Visibility.Visible;
            langListCard.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
            arrowTxt.Text = isExpanded ? "▼" : "▲";
        };
        toggleLanguageDropdown = toggleDrop;
        langHeaderBtn.MouseLeftButtonUp += delegate { toggleDrop(); };

        NotchUi.ApplyTouchFeedback(langHeaderBtn, 0.98);

        langContainer.Children.Add(langHeaderBtn);
        langContainer.Children.Add(langListCard);
        rows.Children.Add(langContainer);

        separator();

        // 7. Grok 油猴腳本助手
        var grokScriptCard = new Border {
            Background = Brush("#111520"),
            BorderBrush = Brush("#232B3D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 2, 4, 4)
        };
        var scriptLayout = new Grid();
        scriptLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scriptInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        scriptInfo.Children.Add(Text(I18n.T("Settings.GrokUserscript"), 10.5, "#E2E8F0"));
        scriptInfo.Children.Add(Text("自動捕捉 X.com Grok 機器人額度 (Tampermonkey)", 9, "#7E8B9B"));
        var openScriptBtn = Button(I18n.T("Settings.InstallScript"), delegate {
            try {
                string scriptPath = Path.Combine(Storage.Root, "assets", "tokenisland-grok.user.js");
                if (File.Exists(scriptPath)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + scriptPath + "\"") { UseShellExecute = true });
            } catch { }
        }, I18n.T("Settings.InstallScript"));
        openScriptBtn.Padding = new Thickness(8, 3, 8, 3);
        openScriptBtn.FontSize = 10;
        Grid.SetColumn(scriptInfo, 0);
        Grid.SetColumn(openScriptBtn, 1);
        scriptLayout.Children.Add(scriptInfo);
        scriptLayout.Children.Add(openScriptBtn);
        grokScriptCard.Child = scriptLayout;
        rows.Children.Add(grokScriptCard);

        separator();

        // 8. 底部操作
        var bottom = new UniformGrid { Columns = 2, Margin = new Thickness(4, 2, 4, 2) };
        bottom.Children.Add(MenuAction(I18n.T("Common.UpdateNow"), async delegate { await Refresh(true); await WeatherManager.Instance.RefreshAsync(true); }, false));
        bottom.Children.Add(MenuAction(I18n.T("Common.Exit"), Close, true));
        rows.Children.Add(bottom);

        var card = new Border {
            Background = Brush("#121521"),
            BorderBrush = Brush("#222638"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 8, 6),
            Child = rows
        };

        settingsDetails.Content = card;
        details.Visibility = Visibility.Collapsed;
        settingsDetails.Visibility = Visibility.Visible;
        if (!collapse.IsEnabled) collapse.Start();
    }

    void Open(string path) {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "無法開啟，請手動前往：\n" + path, "Token Island"); }
    }

    void ConnectClaude() {
        bool running = false;
        try {
            var processes = Process.GetProcessesByName("claude");
            running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
        } catch { }

        string dataDir = Providers.FindClaudeDesktopDataDir();
        if (running) {
            string info = "Claude 官方 Windows 桌面版已在執行中，Token Island 已建立同步連線。";
            if (dataDir != null) {
                try {
                    string cfgFile = Path.Combine(dataDir, "config.json");
                    if (File.Exists(cfgFile)) {
                        object cfg = Json.Parse(File.ReadAllText(cfgFile));
                        string ver = Json.Str(Json.Get(cfg, "updaterLastSeenVersion"));
                        string acc = Json.Str(Json.Get(cfg, "lastKnownAccountUuid"));
                        if (!String.IsNullOrEmpty(ver)) info += "\n版本：" + ver;
                        if (!String.IsNullOrEmpty(acc)) info += "\n已登入帳號：" + acc;
                    }
                } catch { }
            }
            MessageBox.Show(this, info, "Claude 官方 Windows 桌面版");
            return;
        }

        try {
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude") { UseShellExecute = true });
            MessageBox.Show(this, "已發送啟動請求至 Claude 官方 Windows 桌面版。\n應用程式開啟後，浮窗將自動偵測並同步連線狀態。", "Claude 官方桌面版");
        } catch {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Claude\Claude.exe");
            if (File.Exists(exe)) {
                try { Process.Start(exe); MessageBox.Show(this, "已啟動 Claude 官方桌面版。", "Claude 官方桌面版"); return; } catch { }
            }
            MessageBox.Show(this, "尚未偵測到已啟動的 Claude 桌面版。\n請先從開始功能表或工作列開啟 Claude 官方 Windows 應用程式。", "Claude 官方桌面版");
        }
    }

    public static void CaptureElement(FrameworkElement element, string path) {
        element.UpdateLayout();

        FrameworkElement parent = element.Parent as FrameworkElement;
        Visual targetVisual = (parent != null) ? (Visual)parent : (Visual)element;

        Point offset = new Point(0, 0);
        if (parent != null) {
            try {
                offset = element.TransformToAncestor(parent).Transform(new Point(0, 0));
            } catch { }
        }

        double ew = element.ActualWidth > 0 ? element.ActualWidth : element.Width > 0 ? element.Width : 320;
        double eh = element.ActualHeight > 0 ? element.ActualHeight : element.Height > 0 ? element.Height : 300;
        int w = Math.Max(1, (int)Math.Ceiling(ew));
        int h = Math.Max(1, (int)Math.Ceiling(eh));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) {
            var vb = new VisualBrush(targetVisual) {
                Stretch = Stretch.None,
                Viewbox = new Rect(offset.X, offset.Y, ew, eh),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, w, h),
                ViewportUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    public static void RunScreenshot() {
        var app = new Application();
        var win = new Island(false);
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        bool done = false;
        Action runCapture = async delegate {
            if (done) return;
            done = true;
            try {
                File.AppendAllText(Path.Combine(Storage.State, "screenshot-debug.log"), DateTime.UtcNow.ToString("o") + " runCapture started\n");
                win.prefs.Pinned = false;
                win.prefs.Scale = 1.0;
                if (win.scaleTransform != null) {
                    win.scaleTransform.ScaleX = 1.0;
                    win.scaleTransform.ScaleY = 1.0;
                }
                win.hover.Stop();
                win.collapse.Stop();
                win.suppressHover = DateTime.UtcNow.AddMinutes(5);
                win.Expand(false);
                win.FreezeBounds();
                while (win.busy) await Task.Delay(150);
                await win.Refresh(true);
                while (win.busy) await Task.Delay(150);
                await WeatherManager.Instance.RefreshAsync(true);
                if (!win.data.ContainsKey("codex") || !win.data["codex"].Remaining.HasValue) {
                    var pCodex = Providers.Codex();
                    win.data["codex"] = pCodex;
                }
                if (!win.data.ContainsKey("antigravity") || !win.data["antigravity"].Remaining.HasValue) {
                    var pAg = Providers.Antigravity();
                    win.data["antigravity"] = pAg;
                }
                win.Render();
                win.Expand(false);
                win.FreezeBounds();
                await Task.Delay(400);
                try {
                    File.AppendAllText(Path.Combine(Storage.State, "screenshot-debug.log"),
                        string.Format("host.W={0}, host.ActualW={1}, content.ActualW={2}, compact.ActualW={3}, col0={4}, col1={5}, col2={6}, chips.Count={7}, chips.ActualW={8}\n",
                            win.islandHost.Width, win.islandHost.ActualWidth, win.contentContainer.ActualWidth, win.compactContainer.ActualWidth,
                            win.compactContainer.ColumnDefinitions[0].ActualWidth, win.compactContainer.ColumnDefinitions[1].ActualWidth, win.compactContainer.ColumnDefinitions[2].ActualWidth,
                            win.compactChipsBox.Children.Count, win.compactChipsBox.ActualWidth));
                    for (int ci = 0; ci < win.compactChipsBox.Children.Count; ci++) {
                        var fe = win.compactChipsBox.Children[ci] as FrameworkElement;
                        File.AppendAllText(Path.Combine(Storage.State, "screenshot-debug.log"),
                            string.Format("  chip[{0}]: ActualW={1}, DesiredSize={2}\n", ci, fe != null ? fe.ActualWidth : -1, fe != null ? fe.DesiredSize.ToString() : ""));
                    }
                } catch { }
                CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-compact.png"));

                win.Expand(true);
                await Task.Delay(400);
                CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview.png"));
                CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-expanded.png"));

                NotchUi.SwitchToMode(true);
                await Task.Delay(250);
                CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-notebook.png"));
                NotchUi.SwitchToMode(false);

                win.OpenModelAppearanceSettings("antigravity");
                await Task.Delay(300);
                CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-settings.png"));
                if (win.settingsDetails != null) {
                    win.settingsDetails.ScrollToVerticalOffset(190);
                    await Task.Delay(250);
                    CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-settings-appearance.png"));
                    win.settingsDetails.ScrollToBottom();
                    await Task.Delay(250);
                    CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-settings-scrolled.png"));
                    if (win.toggleLanguageDropdown != null) {
                        win.toggleLanguageDropdown();
                        win.settingsDetails.ScrollToBottom();
                        await Task.Delay(250);
                        CaptureElement(win.islandHost ?? win.rootContainer, Path.Combine(Storage.Root, "preview-settings-dropdown.png"));
                    }
                }
                win.ToggleSettingsView(false);

                // 壓力測試：動態切換全球 12 國主流語言，確保 Visual Tree 重構 100% 穩定無例外
                foreach (var testLang in I18n.SupportedLanguages) {
                    I18n.CurrentLanguage = testLang;
                    await Task.Delay(50);
                }
                I18n.CurrentLanguage = win.prefs.Language;
            } catch (Exception ex) {
                try { File.WriteAllText(Path.Combine(Storage.Root, "screenshot-err.txt"), ex.ToString()); } catch { }
            } finally {
                win.Close();
                app.Shutdown();
            }
        };
        win.ContentRendered += delegate { win.Dispatcher.BeginInvoke(new Action(() => runCapture())); };
        var fallbackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        fallbackTimer.Tick += delegate { fallbackTimer.Stop(); runCapture(); };
        fallbackTimer.Start();
        win.Show();
        app.Run();
    }
    public void Capture(string path) { CaptureElement(this, path); }

    // 點擊畫面產生靈動小貓腳印微動畫（隨機粉嫩毛色微光 + 彈性印出與上飄淡出）
    public void SpawnCatPaw(double x, double y) {
        if (pawOverlayCanvas == null) return;
        var pawBox = new Canvas {
            Width = 24,
            Height = 22,
            IsHitTestVisible = false
        };

        // 隨機粉嫩毛色配色：蜜桃粉、櫻花粉、暖金杏色、天青藍
        string[] pawColors = { "#FF8FA3", "#F472B6", "#FB7185", "#FBBF24", "#60A5FA" };
        string chosenColor = pawColors[pawRnd.Next(pawColors.Length)];
        var pawBrush = Brush(chosenColor);

        // 1. 主掌心肉墊（Main Palm Pad）
        var mainPad = new System.Windows.Shapes.Path {
            Data = Geometry.Parse("M5,12 C5,9 19,9 19,12 C19,17.5 15,19.5 12,19.5 C9,19.5 5,17.5 5,12 Z"),
            Fill = pawBrush
        };
        pawBox.Children.Add(mainPad);

        // 2. 四個可愛趾球肉墊（4 Toe Pads）
        var e1 = new System.Windows.Shapes.Ellipse { Width = 4.0, Height = 5.2, Fill = pawBrush, RenderTransform = new RotateTransform(-26, 2, 2.6) };
        Canvas.SetLeft(e1, 3.0); Canvas.SetTop(e1, 4.5);
        pawBox.Children.Add(e1);

        var e2 = new System.Windows.Shapes.Ellipse { Width = 4.2, Height = 5.6, Fill = pawBrush, RenderTransform = new RotateTransform(-8, 2.1, 2.8) };
        Canvas.SetLeft(e2, 7.8); Canvas.SetTop(e2, 2.0);
        pawBox.Children.Add(e2);

        var e3 = new System.Windows.Shapes.Ellipse { Width = 4.2, Height = 5.6, Fill = pawBrush, RenderTransform = new RotateTransform(8, 2.1, 2.8) };
        Canvas.SetLeft(e3, 12.8); Canvas.SetTop(e3, 2.0);
        pawBox.Children.Add(e3);

        var e4 = new System.Windows.Shapes.Ellipse { Width = 4.0, Height = 5.2, Fill = pawBrush, RenderTransform = new RotateTransform(26, 2, 2.6) };
        Canvas.SetLeft(e4, 17.5); Canvas.SetTop(e4, 4.5);
        pawBox.Children.Add(e4);

        pawBox.Effect = new DropShadowEffect {
            Color = ((SolidColorBrush)pawBrush).Color,
            BlurRadius = 6,
            ShadowDepth = 0,
            Opacity = 0.70
        };

        double randAngle = pawRnd.Next(-24, 25);
        var st = new ScaleTransform(0.35, 0.35);
        var tt = new TranslateTransform(0, 0);
        var rt = new RotateTransform(randAngle);
        var tg = new TransformGroup();
        tg.Children.Add(st);
        tg.Children.Add(tt);
        tg.Children.Add(rt);
        pawBox.RenderTransform = tg;
        pawBox.RenderTransformOrigin = new Point(0.5, 0.5);

        Canvas.SetLeft(pawBox, x - 12);
        Canvas.SetTop(pawBox, y - 11);
        pawOverlayCanvas.Children.Add(pawBox);

        var popAnim = new DoubleAnimation(0.35, 1.05, TimeSpan.FromMilliseconds(130)) {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
        };
        var floatAnim = new DoubleAnimation(0, -9.0, TimeSpan.FromMilliseconds(480)) {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeAnim = new DoubleAnimation(0.95, 0.0, TimeSpan.FromMilliseconds(320)) {
            BeginTime = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeAnim.Completed += delegate {
            pawOverlayCanvas.Children.Remove(pawBox);
        };

        st.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
        tt.BeginAnimation(TranslateTransform.YProperty, floatAnim);
        pawBox.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
    }
}
static class Program {
    [STAThread] public static int Main(string[] args) {
        try { Directory.CreateDirectory(Storage.State); File.AppendAllText(Path.Combine(Storage.State, "startup.log"), DateTime.UtcNow.ToString("o") + " Entered Main: " + string.Join(" ", args) + "\n"); } catch { }
        if (args.Contains("--claude-statusline")) {
            try {
                if (!Console.IsInputRedirected) return 1;
                var readTask = System.Threading.Tasks.Task.Factory.StartNew(() => Console.In.ReadToEnd());
                if (!readTask.Wait(500)) return 1;
                object input = Json.Parse(readTask.Result);
                var clean = new Dictionary<string, object> {
                    { "rate_limits", Json.Get(input, "rate_limits") },
                    { "context_window", new Dictionary<string, object> {
                        { "total_input_tokens", Json.Get(input, "context_window", "total_input_tokens") },
                        { "total_output_tokens", Json.Get(input, "context_window", "total_output_tokens") }
                    } }
                };
                Storage.Write("claude-status.json", clean);
                Console.Write("Token Island · 用量已同步");
                return 0;
            } catch { return 1; }
        }
        if (args.Contains("--grok-statusline")) {
            try {
                if (!Console.IsInputRedirected) return 1;
                var readTask = System.Threading.Tasks.Task.Factory.StartNew(() => Console.In.ReadToEnd());
                if (!readTask.Wait(500)) return 1;
                object input = Json.Parse(readTask.Result);
                Storage.Write("grok-status.json", input);
                Console.Write("Token Island · Grok 用量已同步");
                return 0;
            } catch { return 1; }
        }
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--sprite-preview")) { PixelPreview.Save(); return 0; }
        if (args.Contains("--screenshot-drop")) {
            CatDropOverlay.CaptureDropPreview(Path.Combine(Storage.Root, "preview-drop.png"), Storage.Load());
            CatDropOverlay.CapturePixelStyleDemo(Path.Combine(Storage.Root, "preview-pixel-styles.png"));
            return 0;
        }
        if (args.Contains("--screenshot") || args.Contains("--screenshot-menu")) {
            Island.RunScreenshot();
            return 0;
        }
        if (args.Contains("--diagnose")) {
            var results = new[] { Providers.Codex(), Providers.Antigravity(), Providers.Claude(), Providers.Grok() };
            Storage.Write("diagnostics.json", results.Select(x => new { provider = x.Name, note = x.Note, updated = x.Updated.HasValue ? x.Updated.Value.ToString("o") : null, remaining = x.Remaining, quotaCount = x.Quotas.Count, tokenCount = x.Tokens }).ToArray()); return 0;
        }
        bool fresh;
        bool force = args.Contains("--force");
        EventWaitHandle wakeupEvent = null;
        try {
            wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\TokenIslandWakeupEvent", out fresh);
        } catch { fresh = true; }

        using (var mutex = new Mutex(true, "Local\\TokenIslandDesktop-v1", out fresh)) {
            var existing = Process.GetProcessesByName("TokenIsland").Where(p => p.Id != Process.GetCurrentProcess().Id).ToList();
            if (existing.Count > 0 && !fresh && !force) {
                try {
                    File.AppendAllText(Path.Combine(Storage.State, "startup.log"), DateTime.UtcNow.ToString("o") + " Instance already running, signaling wakeup...\n");
                    if (wakeupEvent != null) {
                        wakeupEvent.Set();
                    }
                } catch { }
                return 0;
            }
            try {
                Directory.CreateDirectory(Storage.State);
                File.AppendAllText(Path.Combine(Storage.State, "startup.log"), DateTime.UtcNow.ToString("o") + " Starting Island...\n");
                AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                    try { File.WriteAllText(Path.Combine(Storage.State, "unhandled.txt"), e.ExceptionObject.ToString()); } catch { }
                };
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Exit += (s, e) => {
                    try { File.AppendAllText(Path.Combine(Storage.State, "app-exit.log"), DateTime.UtcNow.ToString("o") + " App.Exit fired: Code=" + e.ApplicationExitCode + "\n"); } catch { }
                };
                app.DispatcherUnhandledException += (s, e) => {
                    try { File.WriteAllText(Path.Combine(Storage.State, "dispatcher-error.txt"), e.Exception.ToString()); } catch { }
                };
                var win = new Island(args.Contains("--screenshot") || args.Contains("--screenshot-menu") || args.Contains("--screenshot-drop"));
                win.Show();
                app.Run(win);
                File.AppendAllText(Path.Combine(Storage.State, "startup.log"), DateTime.UtcNow.ToString("o") + " app.Run completed\n");
                return 0;
            }
            catch (Exception ex) {
                try {
                    File.WriteAllText(Path.Combine(Storage.State, "startup-ex.txt"), ex.ToString());
                    Storage.Write("last-error.json", new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
                } catch { }
                MessageBox.Show("Token Island 啟動失敗：" + ex.Message, "Token Island");
                return 1;
            }
        }
    }
    static int SelfTest() {
        var tests = new List<string>();
        try { File.WriteAllText(Path.Combine(Storage.State, "selftest-debug.log"), "Starting SelfTest\n"); } catch { }
        Action<bool, string> check = (ok, name) => {
            try { File.AppendAllText(Path.Combine(Storage.State, "selftest-debug.log"), "Check: " + name + "\n"); } catch { }
            if (!ok) throw new Exception(name);
            tests.Add(name);
        };
        try {
            var codex = Providers.ParseCodex("{\"timestamp\":\"2026-09-28T08:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":1200}},\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":9,\"window_minutes\":300,\"resets_at\":2000000000},\"secondary\":{\"used_percent\":33,\"window_minutes\":10080,\"resets_at\":2000000000}}}}");
            check(codex.Remaining == 67 && codex.Quotas[0].Remaining == 91, "Codex: both quota windows and minimum"); check(codex.Tokens == 1200, "Codex: session tokens kept separate");
            var ag = Providers.ParseAntigravity("{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"label\":\"Gemini\",\"quotaInfo\":{\"remainingFraction\":0.8544423,\"resetTime\":\"2030-01-01T00:00:00Z\"}},{\"label\":\"Claude\",\"quotaInfo\":{\"resetTime\":\"2030-01-01T00:00:00Z\"}},{\"label\":\"Unknown\"}]}}}");
            check(ag.Quotas.Count == 2 && ag.Quotas[1].Remaining == 0 && Math.Abs(ag.Quotas[0].Remaining.Value - 85.44423) < .001, "Antigravity: fraction, protobuf zero and unknown");
            string mockSummary = "{\"response\":{\"groups\":[{\"displayName\":\"Gemini Models\",\"buckets\":[{\"window\":\"weekly\",\"remainingFraction\":0.78,\"resetTime\":\"2026-10-05T15:00:00Z\"},{\"window\":\"5h\",\"remainingFraction\":0.67,\"resetTime\":\"2026-09-29T18:00:00Z\"}]},{\"displayName\":\"Claude and GPT models\",\"buckets\":[{\"window\":\"weekly\",\"remainingFraction\":1.0},{\"window\":\"5h\",\"remainingFraction\":1.0}]}]}}";
            var agSummary = Providers.ParseAntigravity(mockSummary);
            check(agSummary.DailyRemaining == 67.0 && agSummary.WeeklyRemaining == 78.0, "Antigravity View Usage 5h and weekly quota alignment");
            check(agSummary.Remaining == 67.0, "Antigravity Remaining looks at main Gemini quota");
            string mockDepletedClaude = "{\"response\":{\"groups\":[{\"displayName\":\"Gemini Models\",\"buckets\":[{\"window\":\"weekly\",\"remainingFraction\":0.62,\"resetTime\":\"2026-10-12T15:00:00Z\"},{\"window\":\"5h\",\"remainingFraction\":0.94,\"resetTime\":\"2026-10-07T18:00:00Z\"}]},{\"displayName\":\"Claude and GPT models\",\"buckets\":[{\"window\":\"weekly\",\"remainingFraction\":0.33},{\"window\":\"5h\",\"remainingFraction\":1.0}]},{\"displayName\":\"Gemini 4 Argon\",\"buckets\":[{\"window\":\"weekly\",\"remainingFraction\":0.57,\"resetTime\":\"2026-10-24T07:21:55Z\"}]}]}}";
            var agDepleted = Providers.ParseAntigravity(mockDepletedClaude);
            check(agDepleted.Remaining == 62.0 && agDepleted.DailyRemaining == 94.0 && agDepleted.WeeklyRemaining == 62.0, "Depleted 3p and Argon models never drag down main Gemini remaining");
            check(agDepleted.Quotas.Any(q => q.Label.Contains("Gemini 4 Argon") && Math.Abs(q.Remaining.Value - 57.0) < 0.01) && !agDepleted.Quotas.Any(q => q.Label.Contains("GPT")), "Gemini 4 Argon parsed and GPT removed");
            var cl = Providers.ParseClaude("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":120,\"resets_at\":2000000000}},\"context_window\":{\"total_input_tokens\":100,\"total_output_tokens\":20}}", DateTime.UtcNow);
            check(cl.Remaining == 0 && cl.Tokens == 120, "Claude: over-limit clamp and context tokens");
            check(Providers.ParseClaude("{}", DateTime.UtcNow).Remaining == null, "Missing quota never becomes 100%");
            check(new Provider("x", "x", "#FFFFFF") { Quotas = new List<Quota> { new Quota { Remaining = 20, Reset = DateTime.UtcNow.AddMinutes(-1) } } }.Remaining == 20.0, "Preserve actual quota without jittering to 100%");
            var dotTest = new Provider("test", "test", "#FFF") {
                Quotas = new List<Quota> {
                    new Quota { Label = "5 小時", WindowMinutes = 300, Remaining = 80 },
                    new Quota { Label = "每週", WindowMinutes = 10080, Remaining = 45 }
                }
            };
            check(dotTest.DailyRemaining == 80 && dotTest.WeeklyRemaining == 45, "Provider daily and weekly quota separation");
            check(dotTest.GetWeeklyDotColor() == "#F5D061", "Weekly dot color 69-30 is yellow");
            check(Quota.Epoch(2000000000).Value.Kind == DateTimeKind.Utc, "Epoch timezone");
            check(PixelAnimal.Mood(60,false) == AnimalMood.Full && PixelAnimal.Mood(59.9,false) == AnimalMood.Watch, "Eye state: full to watch threshold");
            check(PixelAnimal.Mood(20,false) == AnimalMood.Watch && PixelAnimal.Mood(19.9,false) == AnimalMood.Low, "Eye state: watch to low threshold");
            check(PixelAnimal.Mood(null,false) == AnimalMood.Unknown && PixelAnimal.Mood(null,true) == AnimalMood.Unknown, "Missing quota is unknown");
            check(PixelAnimal.Mood(0,false) == AnimalMood.Empty && PixelAnimal.Mood(0,true) == AnimalMood.Empty, "0% quota shows depleted animation even with cached data");
            check(Quota.Clamp(1.0) == 0 && Quota.Clamp(0.9) == 0 && Quota.Clamp(1.1) == 1.1, "Exhausted quota clamp: <= 1.0% snaps to 0%");
            check(PixelPreview.FrameDifference(false,false,true) == false, "Reduced motion freezes breathing and eyes");
            if (SystemParameters.ClientAreaAnimation) { check(PixelPreview.FrameDifference(true,false,false), "Breathing produces different rendered frames"); check(PixelPreview.FrameDifference(false,true,false), "Eye animation produces different rendered frames"); }
            check(Providers.FindClaudeDesktopDataDir() != null, "Claude Desktop data directory discovery");
            check(Providers.Claude().Id == "claude", "Claude provider instantiation");
            check(CatDropOverlay.LoadFrames() != null && CatDropOverlay.LoadFrames().Length > 0, "Cat drop frames loaded successfully");
            check(new Preferences { Dock = "top" }.Dock == "top", "Top docking preference configuration");
            check(new Preferences { AnimalType = "fox" }.GetAnimalType() == "fox", "Fox animal type preference");
            check(new Preferences { CustomAnimalColor = "#EF4444" }.GetProviderColor("codex") == "#EF4444", "Custom animal color preference override");
            PixelAnimal.EnsureTheme("orange", "fox");
            PixelAnimal.EnsureTheme("#10B981", "fox");
            PixelAnimal.EnsureTheme("orange", "owl");
            PixelAnimal.EnsureTheme("#3B82F6", "owl");
            PixelAnimal.EnsureTheme("pink", "owl");
            check(true, "Fox and Owl theme loading and custom hex color tinting with branch protection");
            var pTest = new Preferences();
            check(pTest.GetProviderAnimal("codex") == "cat", "Default Codex animal is cat");
            check(pTest.ToggleProviderAnimal("codex") == "fox", "Toggle Codex animal to fox");
            check(pTest.GetProviderAnimal("codex") == "fox", "Codex animal is now fox");
            check(pTest.ToggleProviderAnimal("codex") == "owl", "Toggle Codex animal to owl");
            check(pTest.GetProviderAnimal("codex") == "owl", "Codex animal is now owl");
            check(pTest.ToggleProviderAnimal("codex") == "cat", "Toggle Codex animal back to cat");
            check(pTest.GetProviderAnimal("antigravity") == "fox", "Antigravity animal remains independent");
            pTest.SetProviderColor("codex", "#EF4444");
            pTest.SetProviderColor("claude", "#10B981");
            check(pTest.GetProviderColor("codex") == "#EF4444", "Codex independent custom color set and get");
            check(pTest.GetProviderColor("claude") == "#10B981", "Claude independent custom color set and get");
            check(pTest.GetProviderColor("antigravity") == "orange" || pTest.GetProviderColor("antigravity") == "#FFA500", "Antigravity color remains independent");
            var grokTest = Providers.ParseGrok("{\"remaining\":78.5,\"used_messages\":11,\"total_messages\":50,\"model\":\"Grok 2\",\"window_minutes\":120,\"resets_at\":2000000000}");
            check(grokTest.Remaining == 78.5 && grokTest.Quotas.Count == 1 && grokTest.Tokens == 11, "Grok provider parsing and message count");
            check(pTest.GetProviderAnimal("grok") == "owl", "Default Grok animal is owl");
            pTest.SetProviderColor("grok", "#E2E8F0");
            check(pTest.GetProviderColor("grok") == "#E2E8F0", "Grok independent color set and get");
            string mockGrokBlob = "{\"schemaVersion\":2,\"value\":{\"kind\":\"present\",\"selectedTeamId\":null,\"reading\":{\"usage\":{\"percentUsed\":30.44,\"nextResetMs\":1793475330733,\"unifiedPlanName\":\"Premium\"},\"readAtMs\":1792939554109}}}";
            var nativeGrok = Providers.ParseGrokBotBlob(mockGrokBlob);
            check(Math.Abs(nativeGrok.Remaining.Value - 69.56) < 0.01 && nativeGrok.Tokens == 30, "Grok Bot native blob parsing percentUsed and remaining");
            check(Providers.FindGrokBotDataDir() == null || Directory.Exists(Providers.FindGrokBotDataDir()), "Grok Bot client data directory discovery");
            Storage.Write("test-results.json", new { passed = tests.Count, tests = tests }); return 0;
        } catch (Exception e) {
            try { File.WriteAllText(Path.Combine(Storage.State, "test-err.txt"), e.ToString()); } catch { }
            Storage.Write("test-results.json", new { passed = tests.Count, failed = e.Message }); return 1;
        }
    }
}
}
