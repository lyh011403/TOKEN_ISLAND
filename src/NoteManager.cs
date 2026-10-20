using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TokenIsland {
    public class NoteItem {
        public string Id { get; set; }
        public string Content { get; set; }
        public bool IsPinned { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string ColorHex { get; set; }

        public string GetTimeDisplay() {
            DateTime now = DateTime.Now;
            if (UpdatedAt.Date == now.Date) {
                return UpdatedAt.ToString("HH:mm");
            }
            return UpdatedAt.ToString("M/d HH:mm");
        }

        public string GetSingleLineSummary() {
            if (string.IsNullOrWhiteSpace(Content)) return "空白筆記";
            string clean = Content.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
            return clean;
        }
    }

    public class NoteManager {
        private static NoteManager instance;
        public static NoteManager Instance {
            get {
                if (instance == null) instance = new NoteManager();
                return instance;
            }
        }

        public event Action NotesUpdated;

        private List<NoteItem> notes = new List<NoteItem>();

        public NoteManager() {
            LoadNotes();
            EnsureDefaultNoteIfEmpty();
        }

        public List<NoteItem> GetAllNotes() {
            var list = new List<NoteItem>();
            lock (notes) {
                list.AddRange(notes);
            }
            return list;
        }

        public List<NoteItem> GetPinnedNotes() {
            var list = new List<NoteItem>();
            lock (notes) {
                for (int i = 0; i < notes.Count; i++) {
                    if (notes[i].IsPinned) list.Add(notes[i]);
                }
            }
            return list;
        }

        public NoteItem GetPrimaryPinnedNote() {
            var pinned = GetPinnedNotes();
            return pinned.Count > 0 ? pinned[0] : null;
        }

        public void ReorderNotes(List<string> orderedIds) {
            if (orderedIds == null || orderedIds.Count == 0) return;
            bool changed = false;
            lock (notes) {
                var map = new Dictionary<string, NoteItem>();
                for (int i = 0; i < notes.Count; i++) {
                    map[notes[i].Id] = notes[i];
                }
                var newList = new List<NoteItem>();
                for (int i = 0; i < orderedIds.Count; i++) {
                    string id = orderedIds[i];
                    if (map.ContainsKey(id)) {
                        newList.Add(map[id]);
                        map.Remove(id);
                    }
                }
                foreach (var rem in map.Values) {
                    newList.Add(rem);
                }
                notes = newList;
                changed = true;
            }
            if (changed) {
                SaveNotes();
                if (NotesUpdated != null) NotesUpdated();
            }
        }

        public void AddNote(string content, bool isPinned = true, string colorHex = "#FBBF24") {
            if (string.IsNullOrWhiteSpace(content)) return;
            var item = new NoteItem {
                Id = "note-" + Guid.NewGuid().ToString("N"),
                Content = content.Trim(),
                IsPinned = isPinned,
                UpdatedAt = DateTime.Now,
                ColorHex = string.IsNullOrEmpty(colorHex) ? (isPinned ? "#FBBF24" : "#38BDF8") : colorHex
            };
            lock (notes) {
                notes.Insert(0, item);
            }
            SaveNotes();
            if (NotesUpdated != null) NotesUpdated();
        }

        public void UpdateNote(string id, string newContent, bool? isPinned = null, string colorHex = null) {
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(newContent)) return;
            bool changed = false;
            lock (notes) {
                for (int i = 0; i < notes.Count; i++) {
                    if (notes[i].Id == id) {
                        notes[i].Content = newContent.Trim();
                        if (isPinned.HasValue) notes[i].IsPinned = isPinned.Value;
                        if (!string.IsNullOrEmpty(colorHex)) notes[i].ColorHex = colorHex;
                        else notes[i].ColorHex = notes[i].IsPinned ? "#FBBF24" : "#38BDF8";
                        notes[i].UpdatedAt = DateTime.Now;
                        changed = true;
                        break;
                    }
                }
            }
            if (changed) {
                SaveNotes();
                if (NotesUpdated != null) NotesUpdated();
            }
        }

        public void TogglePin(string id) {
            if (string.IsNullOrEmpty(id)) return;
            bool changed = false;
            lock (notes) {
                for (int i = 0; i < notes.Count; i++) {
                    if (notes[i].Id == id) {
                        notes[i].IsPinned = !notes[i].IsPinned;
                        notes[i].ColorHex = notes[i].IsPinned ? "#FBBF24" : "#38BDF8";
                        notes[i].UpdatedAt = DateTime.Now;
                        changed = true;
                        break;
                    }
                }
            }
            if (changed) {
                SaveNotes();
                if (NotesUpdated != null) NotesUpdated();
            }
        }

        public void DeleteNote(string id) {
            if (string.IsNullOrEmpty(id)) return;
            bool removed = false;
            lock (notes) {
                int before = notes.Count;
                notes.RemoveAll(x => x.Id == id);
                removed = (notes.Count != before);
            }
            if (removed) {
                SaveNotes();
                if (NotesUpdated != null) NotesUpdated();
            }
        }

        private void EnsureDefaultNoteIfEmpty() {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "notes.json");
            if (File.Exists(path)) return;
            lock (notes) {
                if (notes.Count > 0) return;
                notes.Add(new NoteItem {
                    Id = "note-welcome",
                    Content = "隨手筆記：點擊右側「釘選」可固定於上方動態島通知列，點擊文字可隨時修改",
                    IsPinned = false,
                    UpdatedAt = DateTime.Now,
                    ColorHex = "#38BDF8"
                });
            }
            SaveNotes();
        }

        private void SaveNotes() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "notes.json");
                var ser = new JavaScriptSerializer();
                var list = new List<Dictionary<string, object>>();
                lock (notes) {
                    for (int i = 0; i < notes.Count; i++) {
                        var n = notes[i];
                        list.Add(new Dictionary<string, object> {
                            { "Id", n.Id },
                            { "Content", n.Content },
                            { "IsPinned", n.IsPinned },
                            { "UpdatedAt", n.UpdatedAt.ToString("o") },
                            { "ColorHex", n.ColorHex ?? "#38BDF8" }
                        });
                    }
                }
                File.WriteAllText(path, ser.Serialize(list), new UTF8Encoding(false));
            } catch { }
        }

        public void LoadNotes() {
            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "notes.json");
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var arr = ser.Deserialize<object[]>(json);
                if (arr == null) return;
                var list = new List<NoteItem>();
                for (int i = 0; i < arr.Length; i++) {
                    var d = arr[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    DateTime dt;
                    if (!DateTime.TryParse(Convert.ToString(d.ContainsKey("UpdatedAt") ? d["UpdatedAt"] : ""), out dt)) {
                        dt = DateTime.Now;
                    }
                    bool pinned = d.ContainsKey("IsPinned") && Convert.ToBoolean(d["IsPinned"]);
                    list.Add(new NoteItem {
                        Id = Convert.ToString(d.ContainsKey("Id") ? d["Id"] : Guid.NewGuid().ToString("N")),
                        Content = Convert.ToString(d.ContainsKey("Content") ? d["Content"] : ""),
                        IsPinned = pinned,
                        UpdatedAt = dt,
                        ColorHex = d.ContainsKey("ColorHex") ? Convert.ToString(d["ColorHex"]) : (pinned ? "#FBBF24" : "#38BDF8")
                    });
                }
                lock (notes) {
                    notes = list;
                }
            } catch { }
        }
    }
}
