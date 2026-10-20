using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TokenIsland {
    public class CalendarItem {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsAllDay { get; set; }
        public string Location { get; set; }
        public string ColorHex { get; set; }
        public bool IsCustom { get; set; }

        public string GetTimeDisplay() {
            if (IsAllDay) return "全天";
            return StartTime.ToString("HH:mm") + " - " + EndTime.ToString("HH:mm");
        }

        public string GetRelativeStatus() {
            DateTime now = DateTime.Now;
            if (IsAllDay) {
                return StartTime.Date == now.Date ? "今天" : (StartTime.Date > now.Date ? "即將到來" : "已過");
            }
            if (now >= StartTime && now <= EndTime) {
                return "進行中";
            }
            if (now < StartTime) {
                TimeSpan diff = StartTime - now;
                if (diff.TotalMinutes <= 60) {
                    return string.Format("{0} 分鐘後", Math.Max(1, (int)diff.TotalMinutes));
                }
                if (diff.TotalHours <= 24 && StartTime.Date == now.Date) {
                    return string.Format("{0} 小時後", (int)diff.TotalHours);
                }
                if (StartTime.Date == now.Date.AddDays(1)) {
                    return "明天";
                }
                return StartTime.ToString("M/d");
            }
            return "已結束";
        }
    }

    public class CalendarSettingsData {
        public string GoogleIcsUrl { get; set; }
        public DateTime? LastSyncTime { get; set; }
        public string LastSyncStatus { get; set; }
    }

    public class AgendaManager {
        private static AgendaManager instance;
        public static AgendaManager Instance {
            get {
                if (instance == null) instance = new AgendaManager();
                return instance;
            }
        }

        public event Action EventsUpdated;

        private List<CalendarItem> cachedEvents = new List<CalendarItem>();
        private List<CalendarItem> customEvents = new List<CalendarItem>();
        private List<CalendarItem> googleEvents = new List<CalendarItem>();
        private CalendarSettingsData settings = new CalendarSettingsData();
        private bool isSyncing = false;
        private System.Threading.Timer autoSyncTimer;

        public bool IsConnected {
            get {
                return settings != null && !string.IsNullOrWhiteSpace(settings.GoogleIcsUrl) && settings.GoogleIcsUrl.IndexOf(".ics", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsSyncSuccess {
            get {
                if (!IsConnected) return false;
                if (!settings.LastSyncTime.HasValue) return false;
                if (string.IsNullOrEmpty(settings.LastSyncStatus)) return false;
                return settings.LastSyncStatus.StartsWith("同步成功", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string GoogleIcsUrl {
            get {
                return settings != null ? settings.GoogleIcsUrl : "";
            }
        }

        public DateTime? LastSyncTime {
            get {
                return settings != null ? settings.LastSyncTime : null;
            }
        }

        public string LastSyncStatus {
            get {
                return settings != null ? settings.LastSyncStatus : "";
            }
        }

        public class IcsDownloadResult {
            public bool Success { get; set; }
            public string Content { get; set; }
            public string ErrorMessage { get; set; }
        }

        public AgendaManager() {
            LoadSettings();
            LoadCustomEvents();
            LoadGoogleEvents();
            RebuildCachedEvents();
            EnsureDefaultEventsIfEmpty();

            // 背景每 2 分鐘自動拉取 Google 日曆最新快照
            autoSyncTimer = new System.Threading.Timer(delegate {
                if (IsConnected) {
                    SyncCalendarAsync(false);
                }
            }, null, TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(2));
        }

        public void CheckSyncOnExpand() {
            LoadCustomEvents();
            RebuildCachedEvents();
            if (EventsUpdated != null) EventsUpdated();
            if (!IsConnected || isSyncing) return;
            if (!settings.LastSyncTime.HasValue || (DateTime.Now - settings.LastSyncTime.Value).TotalSeconds >= 90) {
                Task.Factory.StartNew(delegate {
                    SyncCalendarAsync(false);
                });
            }
        }

        public bool HasEventsOnDate(DateTime date) {
            DateTime d = date.Date;
            lock (cachedEvents) {
                for (int i = 0; i < cachedEvents.Count; i++) {
                    var e = cachedEvents[i];
                    if (e.IsAllDay) {
                        if (d >= e.StartTime.Date && d < e.EndTime.Date) return true;
                        if (d == e.StartTime.Date) return true;
                    } else {
                        if (e.StartTime.Date == d || (e.StartTime.Date <= d && e.EndTime.Date >= d)) return true;
                    }
                }
            }
            return false;
        }

        public List<CalendarItem> GetEventsForDate(DateTime date) {
            DateTime d = date.Date;
            var list = new List<CalendarItem>();
            lock (cachedEvents) {
                for (int i = 0; i < cachedEvents.Count; i++) {
                    var e = cachedEvents[i];
                    if (e.IsAllDay) {
                        if ((d >= e.StartTime.Date && d < e.EndTime.Date) || d == e.StartTime.Date) {
                            list.Add(e);
                        }
                    } else {
                        if (e.StartTime.Date == d || (e.StartTime.Date <= d && e.EndTime.Date >= d)) {
                            list.Add(e);
                        }
                    }
                }
            }
            list.Sort(delegate(CalendarItem a, CalendarItem b) {
                return a.StartTime.CompareTo(b.StartTime);
            });
            return list;
        }

        public List<CalendarItem> GetUpcomingEvents(int maxCount) {
            DateTime now = DateTime.Now;
            var list = new List<CalendarItem>();
            lock (cachedEvents) {
                for (int i = 0; i < cachedEvents.Count; i++) {
                    var e = cachedEvents[i];
                    if (e.EndTime >= now.AddMinutes(-30) || (e.IsAllDay && e.StartTime.Date >= now.Date)) {
                        list.Add(e);
                    }
                }
            }
            list.Sort(delegate(CalendarItem a, CalendarItem b) {
                return a.StartTime.CompareTo(b.StartTime);
            });
            if (list.Count > maxCount) {
                return list.GetRange(0, maxCount);
            }
            return list;
        }

        public CalendarItem GetNextUpcomingEvent() {
            var list = GetUpcomingEvents(1);
            return list.Count > 0 ? list[0] : null;
        }

        public static void OpenUrlSafely(string url) {
            if (string.IsNullOrWhiteSpace(url)) return;
            try {
                Process.Start(new ProcessStartInfo {
                    FileName = url,
                    UseShellExecute = true
                });
            } catch {
                try {
                    Process.Start("cmd.exe", "/c start \"\" \"" + url + "\"");
                } catch {
                    try {
                        Process.Start("explorer.exe", "\"" + url + "\"");
                    } catch { }
                }
            }
        }

        public void OpenGoogleCalendarWeb() {
            OpenUrlSafely("https://calendar.google.com/calendar/r");
        }

        public void OpenGoogleCalendarDayWeb(DateTime date) {
            string url = string.Format(CultureInfo.InvariantCulture, "https://calendar.google.com/calendar/r/day/{0}/{1}/{2}", date.Year, date.Month, date.Day);
            OpenUrlSafely(url);
        }

        public void OpenGoogleCalendarCreateEventWeb(DateTime date, string title = "", string startTimeStr = "14:00", string endTimeStr = "15:00") {
            try {
                TimeSpan st = TimeSpan.FromHours(14), et = TimeSpan.FromHours(15);
                TimeSpan.TryParse(startTimeStr, out st);
                TimeSpan.TryParse(endTimeStr, out et);
                DateTime start = date.Date.Add(st);
                DateTime end = date.Date.Add(et);

                string startUtc = start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
                string endUtc = end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
                string url = string.Format(CultureInfo.InvariantCulture, "https://calendar.google.com/calendar/r/eventedit?dates={0}/{1}", startUtc, endUtc);
                if (!string.IsNullOrWhiteSpace(title)) {
                    url += "&text=" + Uri.EscapeDataString(title.Trim());
                }
                OpenUrlSafely(url);
            } catch {
                OpenUrlSafely("https://calendar.google.com/calendar/r/eventedit");
            }
        }

        public void OpenGoogleCalendarSettingsWeb() {
            OpenUrlSafely("https://calendar.google.com/calendar/r/settings");
        }

        public void SaveGoogleIcsUrl(string url) {
            if (settings == null) settings = new CalendarSettingsData();
            settings.GoogleIcsUrl = (url ?? "").Trim();
            settings.LastSyncStatus = "正在同步...";
            SaveSettings();
            Task.Factory.StartNew(delegate {
                SyncCalendarAsync(true);
            });
        }

        public void AddCustomEvent(string title, DateTime start, DateTime end, string colorHex) {
            if (string.IsNullOrWhiteSpace(title)) return;
            var item = new CalendarItem {
                Id = "custom-" + Guid.NewGuid().ToString("N"),
                Title = title.Trim(),
                StartTime = start,
                EndTime = end,
                IsAllDay = false,
                Location = "本地排定",
                ColorHex = string.IsNullOrEmpty(colorHex) ? "#4285F4" : colorHex,
                IsCustom = true
            };
            lock (customEvents) {
                customEvents.Add(item);
            }
            SaveCustomEvents();
            RebuildCachedEvents();
            if (EventsUpdated != null) EventsUpdated();
        }

        public void DeleteCustomEvent(string id) {
            if (string.IsNullOrEmpty(id)) return;
            bool removed = false;
            lock (customEvents) {
                int countBefore = customEvents.Count;
                customEvents.RemoveAll(x => x.Id == id);
                removed = (customEvents.Count != countBefore);
            }
            if (removed) {
                SaveCustomEvents();
                RebuildCachedEvents();
                if (EventsUpdated != null) EventsUpdated();
            }
        }

        public void RefreshAsync() {
            Task.Factory.StartNew(delegate {
                SyncCalendarAsync(true);
            });
        }

        public void SyncCalendarAsync(bool force) {
            if (isSyncing) return;
            isSyncing = true;
            try {
                if (!string.IsNullOrWhiteSpace(settings.GoogleIcsUrl)) {
                    string url = settings.GoogleIcsUrl.Trim();
                    if (url.IndexOf("calendar.google.com", StringComparison.OrdinalIgnoreCase) >= 0 && url.IndexOf(".ics", StringComparison.OrdinalIgnoreCase) < 0) {
                        settings.LastSyncStatus = "錯誤：需使用 iCal 私人網址（結尾應為 basic.ics）";
                        SaveSettings();
                        return;
                    }
                    if (url.IndexOf("/public/basic.ics", StringComparison.OrdinalIgnoreCase) >= 0) {
                        settings.LastSyncStatus = "錯誤：此為公開網址 (/public/)，非公開日曆無法存取 (404)。請改用「iCal 格式的私人網址」(/private-xxx/)";
                        SaveSettings();
                        return;
                    }

                    var res = DownloadIcs(url);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Content)) {
                        var parsed = ParseIcs(res.Content);
                        lock (googleEvents) {
                            googleEvents = parsed;
                        }
                        SaveGoogleEvents();
                        RebuildCachedEvents();
                        settings.LastSyncTime = DateTime.Now;
                        settings.LastSyncStatus = string.Format("同步成功 ({0} 個事件)", parsed.Count);
                        SaveSettings();
                    } else {
                        settings.LastSyncStatus = res.ErrorMessage ?? "下載失敗，請檢查網址或網路";
                        SaveSettings();
                    }
                } else {
                    RebuildCachedEvents();
                    EnsureDefaultEventsIfEmpty();
                }
            } catch (Exception ex) {
                settings.LastSyncStatus = "同步錯誤：" + ex.Message;
                SaveSettings();
                RebuildCachedEvents();
                EnsureDefaultEventsIfEmpty();
            } finally {
                isSyncing = false;
                if (EventsUpdated != null) {
                    EventsUpdated();
                }
            }
        }

        private IcsDownloadResult DownloadIcs(string url) {
            try {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                
                // 加入 Cache-Busting 查詢參數，穿透 HTTP 與伺服器中繼快取
                string targetUrl = url;
                long cb = DateTime.UtcNow.Ticks;
                if (targetUrl.IndexOf('?') >= 0) {
                    targetUrl += "&_cb=" + cb;
                } else {
                    targetUrl += "?_cb=" + cb;
                }

                using (var client = new WebClient()) {
                    client.Encoding = Encoding.UTF8;
                    client.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TokenIsland-CalendarSync/1.0";
                    client.Headers[HttpRequestHeader.CacheControl] = "no-cache, no-store, must-revalidate";
                    client.Headers[HttpRequestHeader.Pragma] = "no-cache";
                    string data = client.DownloadString(targetUrl);
                    return new IcsDownloadResult { Success = true, Content = data };
                }
            } catch (WebException wex) {
                string msg = "連線失敗";
                HttpWebResponse resp = wex.Response as HttpWebResponse;
                if (resp != null) {
                    int code = (int)resp.StatusCode;
                    if (code == 404) {
                        if (url.IndexOf("/public/", StringComparison.OrdinalIgnoreCase) >= 0) {
                            msg = "下載失敗 (404 找不到)：您複製的是「公開網址」，但該日曆未設為公開。請複製 Google 日曆「整合日曆」中的「私人網址」(/private-xxx/basic.ics)";
                        } else {
                            msg = "下載失敗 (404 找不到)：日曆網址無效或已被重設，請重新複製私人網址";
                        }
                    } else if (code == 401 || code == 403) {
                        msg = "下載失敗 (" + code + " 拒絕存取)：權限不足，請改用 Google 日曆整合日曆中的「私人網址」";
                    } else {
                        msg = string.Format("下載失敗 (HTTP {0} {1})", code, resp.StatusDescription);
                    }
                } else {
                    msg = "連線失敗：" + wex.Message;
                }
                return new IcsDownloadResult { Success = false, ErrorMessage = msg };
            } catch (Exception ex) {
                return new IcsDownloadResult { Success = false, ErrorMessage = "讀取失敗：" + ex.Message };
            }
        }

        public List<CalendarItem> ParseIcs(string ics) {
            var items = new List<CalendarItem>();
            if (string.IsNullOrWhiteSpace(ics)) return items;

            string[] colors = new string[] { "#4285F4", "#34A853", "#FBBC05", "#EA4335", "#A142F4" };
            int colorIdx = 0;

            string unfolded = Regex.Replace(ics, @"\r?\n[ \t]", "");
            string[] lines = unfolded.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            bool inEvent = false;
            string summary = "";
            string dtstart = "";
            string dtend = "";
            string location = "";
            string uid = "";

            for (int i = 0; i < lines.Length; i++) {
                string line = lines[i].Trim();
                if (line == "BEGIN:VEVENT") {
                    inEvent = true;
                    summary = "";
                    dtstart = "";
                    dtend = "";
                    location = "";
                    uid = Guid.NewGuid().ToString("N");
                } else if (line == "END:VEVENT" && inEvent) {
                    inEvent = false;
                    DateTime start, end;
                    bool isAllDay;
                    if (TryParseIcsDate(dtstart, out start, out isAllDay)) {
                        if (!TryParseIcsDate(dtend, out end, out isAllDay)) {
                            end = isAllDay ? start.AddDays(1) : start.AddHours(1);
                        }
                        if (string.IsNullOrWhiteSpace(summary)) summary = "未命名行程";
                        items.Add(new CalendarItem {
                            Id = uid,
                            Title = summary,
                            StartTime = start,
                            EndTime = end,
                            IsAllDay = isAllDay,
                            Location = location,
                            ColorHex = colors[colorIdx % colors.Length],
                            IsCustom = false
                        });
                        colorIdx++;
                    }
                } else if (inEvent) {
                    if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase)) {
                        summary = UnescapeIcs(line.Substring(8));
                    } else if (line.StartsWith("DTSTART", StringComparison.OrdinalIgnoreCase)) {
                        int colon = line.IndexOf(':');
                        if (colon > 0) dtstart = line.Substring(colon + 1);
                    } else if (line.StartsWith("DTEND", StringComparison.OrdinalIgnoreCase)) {
                        int colon = line.IndexOf(':');
                        if (colon > 0) dtend = line.Substring(colon + 1);
                    } else if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) {
                        location = UnescapeIcs(line.Substring(9));
                    } else if (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase)) {
                        uid = line.Substring(4).Trim();
                    }
                }
            }

            return items;
        }

        private static string UnescapeIcs(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\n", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
        }

        private static bool TryParseIcsDate(string s, out DateTime dt, out bool isAllDay) {
            dt = DateTime.MinValue;
            isAllDay = false;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            if (s.Length == 8 && Regex.IsMatch(s, @"^\d{8}$")) {
                if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                    isAllDay = true;
                    return true;
                }
            }

            if (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) {
                string clean = s.TrimEnd('Z', 'z');
                if (DateTime.TryParseExact(clean, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt)) {
                    dt = dt.ToLocalTime();
                    isAllDay = false;
                    return true;
                }
            }

            if (DateTime.TryParseExact(s, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                isAllDay = false;
                return true;
            }

            return false;
        }

        private void RebuildCachedEvents() {
            var merged = new List<CalendarItem>();
            lock (customEvents) {
                merged.AddRange(customEvents);
            }
            lock (googleEvents) {
                merged.AddRange(googleEvents);
            }
            merged.Sort(delegate(CalendarItem a, CalendarItem b) {
                return a.StartTime.CompareTo(b.StartTime);
            });
            lock (cachedEvents) {
                cachedEvents = merged;
            }
            SaveCachedEventsToFile();
        }

        private void EnsureDefaultEventsIfEmpty() {
            lock (cachedEvents) {
                if (cachedEvents.Count > 0) return;
                if (LoadCachedEventsFromFile()) return;

                DateTime today = DateTime.Today;
                cachedEvents = new List<CalendarItem> {
                    new CalendarItem {
                        Id = "demo-1",
                        Title = "TokenIsland 專案發布與代碼審定",
                        StartTime = today.AddHours(22),
                        EndTime = today.AddHours(23),
                        IsAllDay = false,
                        Location = "本地工作區",
                        ColorHex = "#4285F4",
                        IsCustom = false
                    },
                    new CalendarItem {
                        Id = "demo-2",
                        Title = "團隊架構週會 · 產品路線對齊",
                        StartTime = today.AddDays(1).AddHours(10),
                        EndTime = today.AddDays(1).AddHours(11).AddMinutes(30),
                        IsAllDay = false,
                        Location = "Google Meet",
                        ColorHex = "#34A853",
                        IsCustom = false
                    },
                    new CalendarItem {
                        Id = "demo-3",
                        Title = "全域 AI 動態島體驗覆盤",
                        StartTime = today.AddDays(1).AddHours(14),
                        EndTime = today.AddDays(1).AddHours(15),
                        IsAllDay = false,
                        Location = "線上",
                        ColorHex = "#FBBC05",
                        IsCustom = false
                    },
                    new CalendarItem {
                        Id = "demo-4",
                        Title = "週五工程技術分享會",
                        StartTime = today.AddDays(2).AddHours(16),
                        EndTime = today.AddDays(2).AddHours(17),
                        IsAllDay = false,
                        Location = "台北會議室",
                        ColorHex = "#EA4335",
                        IsCustom = false
                    }
                };
            }
        }

        private void LoadSettings() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "calendar_settings.json");
                if (File.Exists(path)) {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var ser = new JavaScriptSerializer();
                    var dict = ser.Deserialize<Dictionary<string, object>>(json);
                    if (dict != null) {
                        settings = new CalendarSettingsData();
                        if (dict.ContainsKey("GoogleIcsUrl") && dict["GoogleIcsUrl"] != null) {
                            settings.GoogleIcsUrl = dict["GoogleIcsUrl"].ToString();
                        }
                        if (dict.ContainsKey("LastSyncStatus") && dict["LastSyncStatus"] != null) {
                            settings.LastSyncStatus = dict["LastSyncStatus"].ToString();
                        }
                        if (dict.ContainsKey("LastSyncTime") && dict["LastSyncTime"] != null) {
                            DateTime dt;
                            if (DateTime.TryParse(dict["LastSyncTime"].ToString(), out dt)) {
                                settings.LastSyncTime = dt;
                            }
                        }
                    }
                }
            } catch { }
        }

        private void SaveSettings() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "calendar_settings.json");
                var ser = new JavaScriptSerializer();
                var dict = new Dictionary<string, object> {
                    { "GoogleIcsUrl", settings.GoogleIcsUrl ?? "" },
                    { "LastSyncTime", settings.LastSyncTime.HasValue ? settings.LastSyncTime.Value.ToString("o") : null },
                    { "LastSyncStatus", settings.LastSyncStatus ?? "" }
                };
                File.WriteAllText(path, ser.Serialize(dict), Encoding.UTF8);
            } catch { }
        }

        private void SaveCustomEvents() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "custom_events.json");
                var ser = new JavaScriptSerializer();
                var list = new List<Dictionary<string, object>>();
                lock (customEvents) {
                    for (int i = 0; i < customEvents.Count; i++) {
                        var e = customEvents[i];
                        list.Add(new Dictionary<string, object> {
                            { "Id", e.Id },
                            { "Title", e.Title },
                            { "StartTime", e.StartTime.ToString("o") },
                            { "EndTime", e.EndTime.ToString("o") },
                            { "IsAllDay", e.IsAllDay },
                            { "Location", e.Location },
                            { "ColorHex", e.ColorHex },
                            { "IsCustom", true }
                        });
                    }
                }
                File.WriteAllText(path, ser.Serialize(list), Encoding.UTF8);
            } catch { }
        }

        private void LoadCustomEvents() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "custom_events.json");
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var arr = ser.Deserialize<object[]>(json);
                if (arr == null) return;
                var list = new List<CalendarItem>();
                for (int i = 0; i < arr.Length; i++) {
                    var d = arr[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    DateTime st, et;
                    DateTime.TryParse(Convert.ToString(d["StartTime"]), out st);
                    DateTime.TryParse(Convert.ToString(d["EndTime"]), out et);
                    list.Add(new CalendarItem {
                        Id = Convert.ToString(d["Id"]),
                        Title = Convert.ToString(d["Title"]),
                        StartTime = st,
                        EndTime = et,
                        IsAllDay = d.ContainsKey("IsAllDay") && Convert.ToBoolean(d["IsAllDay"]),
                        Location = d.ContainsKey("Location") ? Convert.ToString(d["Location"]) : "本地排定",
                        ColorHex = d.ContainsKey("ColorHex") ? Convert.ToString(d["ColorHex"]) : "#4285F4",
                        IsCustom = true
                    });
                }
                lock (customEvents) {
                    customEvents = list;
                }
            } catch { }
        }

        private void SaveGoogleEvents() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "google_events.json");
                var ser = new JavaScriptSerializer();
                var list = new List<Dictionary<string, object>>();
                lock (googleEvents) {
                    for (int i = 0; i < googleEvents.Count; i++) {
                        var e = googleEvents[i];
                        list.Add(new Dictionary<string, object> {
                            { "Id", e.Id },
                            { "Title", e.Title },
                            { "StartTime", e.StartTime.ToString("o") },
                            { "EndTime", e.EndTime.ToString("o") },
                            { "IsAllDay", e.IsAllDay },
                            { "Location", e.Location },
                            { "ColorHex", e.ColorHex },
                            { "IsCustom", false }
                        });
                    }
                }
                File.WriteAllText(path, ser.Serialize(list), Encoding.UTF8);
            } catch { }
        }

        private void LoadGoogleEvents() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "google_events.json");
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var arr = ser.Deserialize<object[]>(json);
                if (arr == null) return;
                var list = new List<CalendarItem>();
                for (int i = 0; i < arr.Length; i++) {
                    var d = arr[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    DateTime st, et;
                    DateTime.TryParse(Convert.ToString(d["StartTime"]), out st);
                    DateTime.TryParse(Convert.ToString(d["EndTime"]), out et);
                    list.Add(new CalendarItem {
                        Id = Convert.ToString(d["Id"]),
                        Title = Convert.ToString(d["Title"]),
                        StartTime = st,
                        EndTime = et,
                        IsAllDay = d.ContainsKey("IsAllDay") && Convert.ToBoolean(d["IsAllDay"]),
                        Location = d.ContainsKey("Location") ? Convert.ToString(d["Location"]) : "",
                        ColorHex = d.ContainsKey("ColorHex") ? Convert.ToString(d["ColorHex"]) : "#4285F4",
                        IsCustom = false
                    });
                }
                lock (googleEvents) {
                    googleEvents = list;
                }
            } catch { }
        }

        private void SaveCachedEventsToFile() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "calendar_events.json");
                var ser = new JavaScriptSerializer();
                List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                lock (cachedEvents) {
                    for (int i = 0; i < cachedEvents.Count; i++) {
                        var e = cachedEvents[i];
                        var item = new Dictionary<string, object> {
                            { "Id", e.Id },
                            { "Title", e.Title },
                            { "StartTime", e.StartTime.ToString("o") },
                            { "EndTime", e.EndTime.ToString("o") },
                            { "IsAllDay", e.IsAllDay },
                            { "Location", e.Location },
                            { "ColorHex", e.ColorHex },
                            { "IsCustom", e.IsCustom }
                        };
                        list.Add(item);
                    }
                }
                File.WriteAllText(path, ser.Serialize(list), Encoding.UTF8);
            } catch { }
        }

        private bool LoadCachedEventsFromFile() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "calendar_events.json");
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var arr = ser.Deserialize<object[]>(json);
                if (arr == null || arr.Length == 0) return false;

                var list = new List<CalendarItem>();
                for (int i = 0; i < arr.Length; i++) {
                    var d = arr[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    DateTime st, et;
                    DateTime.TryParse(Convert.ToString(d["StartTime"]), out st);
                    DateTime.TryParse(Convert.ToString(d["EndTime"]), out et);
                    list.Add(new CalendarItem {
                        Id = Convert.ToString(d["Id"]),
                        Title = Convert.ToString(d["Title"]),
                        StartTime = st,
                        EndTime = et,
                        IsAllDay = d.ContainsKey("IsAllDay") && Convert.ToBoolean(d["IsAllDay"]),
                        Location = d.ContainsKey("Location") ? Convert.ToString(d["Location"]) : "",
                        ColorHex = d.ContainsKey("ColorHex") ? Convert.ToString(d["ColorHex"]) : "#4285F4",
                        IsCustom = d.ContainsKey("IsCustom") && Convert.ToBoolean(d["IsCustom"])
                    });
                }
                if (list.Count > 0) {
                    cachedEvents = list;
                    return true;
                }
            } catch { }
            return false;
        }
    }
}
