using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TokenIsland {
    public class ShelfItem {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Extension { get; set; }
        public string FileSizeStr { get; set; }
        public bool IsDirectory { get; set; }
        public BitmapSource IconSource { get; set; }
    }

    public class TrayShelfManager {
        private static TrayShelfManager instance;
        public static TrayShelfManager Instance {
            get {
                if (instance == null) instance = new TrayShelfManager();
                return instance;
            }
        }

        public List<ShelfItem> Items { get; private set; }
        public event Action ShelfChanged;

        public TrayShelfManager() {
            Items = new List<ShelfItem>();
            LoadState();
        }

        public void AddFiles(string[] paths) {
            if (paths == null) return;
            bool changed = false;
            foreach (var path in paths) {
                if (string.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (Items.Any(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;

                var item = CreateItem(path);
                if (item != null) {
                    Items.Insert(0, item);
                    changed = true;
                }
            }
            if (changed) {
                SaveState();
                RaiseShelfChanged();
            }
        }

        public void RemoveItem(ShelfItem item) {
            if (item != null && Items.Remove(item)) {
                SaveState();
                RaiseShelfChanged();
            }
        }

        public void Clear() {
            if (Items.Count > 0) {
                Items.Clear();
                SaveState();
                RaiseShelfChanged();
            }
        }

        private ShelfItem CreateItem(string path) {
            try {
                bool isDir = Directory.Exists(path);
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) name = path;
                string ext = isDir ? "DIR" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                string sizeStr = "—";

                if (!isDir && File.Exists(path)) {
                    long bytes = new FileInfo(path).Length;
                    sizeStr = FormatBytes(bytes);
                } else if (isDir) {
                    sizeStr = "資料夾";
                }

                BitmapSource iconBmp = GetFileIcon(path, isDir);

                return new ShelfItem {
                    FilePath = path,
                    FileName = name,
                    Extension = ext,
                    FileSizeStr = sizeStr,
                    IsDirectory = isDir,
                    IconSource = iconBmp
                };
            } catch {
                return null;
            }
        }

        private BitmapSource GetFileIcon(string path, bool isDir) {
            try {
                using (Icon sysIcon = Icon.ExtractAssociatedIcon(path)) {
                    if (sysIcon != null) {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions()
                        );
                        bmp.Freeze();
                        return bmp;
                    }
                }
            } catch { }
            return null;
        }

        private string FormatBytes(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }

        private void SaveState() {
            try {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "shelf_history.json");
                var list = Items.Select(x => x.FilePath).ToList();
                File.WriteAllText(file, Json.Stringify(list));
            } catch { }
        }

        private void LoadState() {
            try {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "shelf_history.json");
                if (!File.Exists(file)) return;
                var raw = File.ReadAllText(file);
                var arr = Json.Array(Json.Parse(raw));
                foreach (var o in arr) {
                    string path = Json.Str(o);
                    if (File.Exists(path) || Directory.Exists(path)) {
                        var item = CreateItem(path);
                        if (item != null) Items.Add(item);
                    }
                }
            } catch { }
        }

        private void RaiseShelfChanged() {
            try {
                if (ShelfChanged != null) ShelfChanged();
            } catch { }
        }
    }
}
