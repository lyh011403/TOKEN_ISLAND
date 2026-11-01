using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TokenIsland {
    public static class FullscreenDetector {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint GW_HWNDNEXT = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly HashSet<string> BrowserProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "waterfox",
            "chromium", "whale", "thorium", "zen", "floorp", "maxthon", "centbrowser", "sidekick",
            "tor", "librewolf"
        };

        private static readonly HashSet<string> AiToolProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "codex", "chatgpt", "antigravity", "claude", "cursor", "code", "windowsterminal", "cmd", "powershell", "pwsh",
            "devenv", "rider64", "pycharm64", "idea64", "grok bot", "grok", "grokbot", "sand", "sand-desktop"
        };

        public static string GetWindowTitle(IntPtr hWnd) {
            if (hWnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsAiToolWindow(IntPtr hWnd, string procName) {
            if (string.IsNullOrEmpty(procName)) return false;
            // 關鍵解耦：若當前前景為網頁瀏覽器，一律歸屬瀏覽器規則管理，不享有 AI 豁免！
            // 防止瀏覽器分頁開啟 ChatGPT / Claude / Grok 時，造成靈動島無法自動避讓而擋住分頁列
            if (BrowserProcessNames.Contains(procName)) return false;

            // 1. 獨立桌面 AI 工具進程白名單（如 Grok Bot 桌面版、Cursor、VS Code、Terminal 等）
            if (AiToolProcessNames.Contains(procName)) return true;

            // 2. 非瀏覽器之獨立桌面軟體視窗標題判定
            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return false;
            return title.IndexOf("Grok Bot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("Grok", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsAiToolOrGrokWindow(IntPtr hWnd, string procName) {
            return IsAiToolWindow(hWnd, procName);
        }

        private static readonly Dictionary<uint, string> pidNameCache = new Dictionary<uint, string>();
        private static DateTime lastCacheClean = DateTime.UtcNow;

        private static string GetProcessNameFast(uint pid) {
            if (pid == 0) return "";
            if ((DateTime.UtcNow - lastCacheClean).TotalSeconds > 30) {
                pidNameCache.Clear();
                lastCacheClean = DateTime.UtcNow;
            }
            string name;
            if (pidNameCache.TryGetValue(pid, out name)) return name;
            try {
                using (var proc = Process.GetProcessById((int)pid)) {
                    name = proc != null ? (proc.ProcessName ?? "") : "";
                }
            } catch {
                name = "";
            }
            pidNameCache[pid] = name;
            return name;
        }

        private static bool IsIgnoredSystemClass(string cls) {
            if (string.IsNullOrEmpty(cls)) return true;
            return cls == "Progman" ||
                   cls == "WorkerW" ||
                   cls == "#32769" ||
                   cls == "Shell_TrayWnd" ||
                   cls == "Shell_SecondaryTrayWnd" ||
                   cls == "NotifyIconOverflowWindow" ||
                   cls == "Windows.UI.Core.CoreWindow" ||
                   cls == "XamlExplorerHostIslandWindow" ||
                   cls == "ForegroundStaging" ||
                   cls == "MSCTFIME UI" ||
                   cls == "IME" ||
                   cls == "SHELLDLL_DefView" ||
                   cls == "SysListView32";
        }

        public static IntPtr GetForegroundWindowHandle() {
            return GetForegroundWindow();
        }

        public static bool IsDesktopOrShellWindow(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return true;
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (IsIgnoredSystemClass(cls)) return true;
            if (cls == "SHELLDLL_DefView" || cls == "SysListView32") return true;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != 0) {
                string pName = GetProcessNameFast(pid);
                if (string.Equals(pName, "explorer", StringComparison.OrdinalIgnoreCase)) {
                    // 若是一般檔案總管視窗 (CabinetWClass) 則視為一般應用程式視窗，其餘視為桌面/系統外殼
                    if (cls != "CabinetWClass") return true;
                }
            }
            return false;
        }

        public static void SendToBottom(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return;
            // 先移除 TOPMOST 標記 (-2 = HWND_NOTOPMOST)，再壓入最底層 (1 = HWND_BOTTOM)
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        public static void BringToTopmost(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        public static bool IsForegroundFullscreen(IntPtr selfHandle) {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == selfHandle || IsIconic(fg)) return false;
            if (IsDesktopOrShellWindow(fg)) return false; // 桌面永遠不是全螢幕遮蔽應用，桌面時常駐！

            var className = new StringBuilder(256);
            GetClassName(fg, className, className.Capacity);
            if (IsIgnoredSystemClass(className.ToString())) return false;

            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == (uint)Process.GetCurrentProcess().Id) return false;

            RECT r;
            if (GetWindowRect(fg, out r)) {
                Screen islandScreen = Screen.FromHandle(selfHandle) ?? Screen.PrimaryScreen;
                if (islandScreen != null) {
                    var bounds = islandScreen.Bounds;
                    // 必須前景視窗完全覆蓋動態島所在的螢幕，才判定為全螢幕遮蔽
                    if (r.Left <= bounds.Left + 2 && r.Top <= bounds.Top + 2 &&
                        r.Right >= bounds.Right - 2 && r.Bottom >= bounds.Bottom - 2) {
                        return true;
                    }
                }
            }

            return false;
        }

        // 穩定判斷動態島所在螢幕是否開著瀏覽器（含前景瀏覽器、選單/輸入法焦點防抖、以及 Z-Order 頂層主視窗巡檢）
        public static bool IsForegroundBrowserCoveringTop(IntPtr selfHandle) {
            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            Screen islandScreen = Screen.FromHandle(selfHandle) ?? Screen.PrimaryScreen;
            var islandBounds = islandScreen.Bounds;
            var workArea = islandScreen.WorkingArea;
            int centerX = workArea.Left + workArea.Width / 2;

            // 1. 直接檢查當前 Foreground Window 是否屬於同螢幕上的瀏覽器進程
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != selfHandle && !IsIconic(fg)) {
                if (IsDesktopOrShellWindow(fg)) return false;
                uint fgPid;
                GetWindowThreadProcessId(fg, out fgPid);
                if (fgPid != 0 && fgPid != ownPid) {
                    RECT fgRect;
                    if (GetWindowRect(fg, out fgRect)) {
                        // 檢查前景是否落在動態島所在的螢幕
                        int interL = Math.Max(fgRect.Left, islandBounds.Left);
                        int interT = Math.Max(fgRect.Top, islandBounds.Top);
                        int interR = Math.Min(fgRect.Right, islandBounds.Right);
                        int interB = Math.Min(fgRect.Bottom, islandBounds.Bottom);
                        if (interR > interL && interB > interT) {
                            string pName = GetProcessNameFast(fgPid);
                            if (BrowserProcessNames.Contains(pName)) {
                                // 判斷是否覆蓋動態島頂部區域
                                if (fgRect.Top <= workArea.Top + 75 && fgRect.Bottom >= workArea.Top + 50 &&
                                    fgRect.Left <= centerX + 220 && fgRect.Right >= centerX - 220) {
                                    return true;
                                }
                            }
                            if (IsAiToolWindow(fg, pName)) return false;
                        }
                    }
                }
            }

            // 2. 順著 Z-Order 由上而下巡檢動態島所在螢幕的第一個可見主應用程式視窗
            IntPtr curr = GetTopWindow(IntPtr.Zero);
            int inspected = 0;
            while (curr != IntPtr.Zero && inspected < 60) {
                inspected++;
                if (curr != selfHandle && IsWindowVisible(curr) && !IsIconic(curr)) {
                    int exStyle = GetWindowLong(curr, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TRANSPARENT) == 0) {
                        var sb = new StringBuilder(128);
                        GetClassName(curr, sb, sb.Capacity);
                        string cls = sb.ToString();
                        if (!IsIgnoredSystemClass(cls)) {
                            uint pid;
                            GetWindowThreadProcessId(curr, out pid);
                            if (pid != 0 && pid != ownPid) {
                                RECT r;
                                if (GetWindowRect(curr, out r)) {
                                    // 判斷此視窗是否在動態島所在螢幕
                                    int interL = Math.Max(r.Left, islandBounds.Left);
                                    int interT = Math.Max(r.Top, islandBounds.Top);
                                    int interR = Math.Min(r.Right, islandBounds.Right);
                                    int interB = Math.Min(r.Bottom, islandBounds.Bottom);
                                    if (interR > interL && interB > interT) {
                                        int w = r.Right - r.Left;
                                        int h = r.Bottom - r.Top;
                                        if (w >= 420 && h >= 300) {
                                            string procName = GetProcessNameFast(pid);
                                            if (BrowserProcessNames.Contains(procName)) {
                                                if (r.Top <= workArea.Top + 75 && r.Bottom >= workArea.Top + 50 &&
                                                    r.Left <= centerX + 220 && r.Right >= centerX - 220) {
                                                    return true;
                                                }
                                            }
                                            // 若遇到一個非瀏覽器大型主視窗已經完全蓋住動態島所在螢幕上半部中央，代表後層已遮蔽
                                            if ((exStyle & WS_EX_TOOLWINDOW) == 0 &&
                                                r.Top <= workArea.Top + 100 && r.Bottom >= workArea.Top + 400 &&
                                                r.Left <= centerX - 150 && r.Right >= centerX + 150) {
                                                return false;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                curr = GetWindow(curr, GW_HWNDNEXT);
            }

            return false;
        }

        public static bool IsForegroundAiTool(IntPtr selfHandle) {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == selfHandle || IsIconic(fg)) return false;
            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == 0 || fgPid == (uint)Process.GetCurrentProcess().Id) return false;
            string pName = GetProcessNameFast(fgPid);
            return AiToolProcessNames.Contains(pName);
        }

        public static bool IsForegroundBrowser(IntPtr selfHandle) {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == selfHandle || IsIconic(fg)) return false;
            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == 0 || fgPid == (uint)Process.GetCurrentProcess().Id) return false;
            string pName = GetProcessNameFast(fgPid);
            return BrowserProcessNames.Contains(pName);
        }

        // 智慧避讓判定（具備多螢幕隔離感知）：
        // 1. 若前景視窗完全位於其他螢幕（例如副螢幕操作瀏覽器），絕不影響主螢幕動態島，直接回傳 false！
        // 2. 若同螢幕前景視窗屬於 AI 工具（Antigravity、Claude、Codex/VS Code 等），永遠保持置頂！
        // 3. 若同螢幕前景為全螢幕應用程式，自動避讓隱藏
        // 4. 若同螢幕前景為網頁瀏覽器且覆蓋頂部中央分頁列區域，自動避讓不擋分頁
        // 5. 若同螢幕前景為一般視窗且遮蔽螢幕頂部中央動態島區域，自動避讓
        public static bool ShouldYieldTopmost(IntPtr selfHandle) {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == selfHandle || IsIconic(fg)) return false;
            if (IsDesktopOrShellWindow(fg)) return false; // 桌面時常駐，絕對不避讓！

            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            int fgW = r.Right - r.Left;
            int fgH = r.Bottom - r.Top;
            if (fgW <= 0 || fgH <= 0) return false;

            Screen islandScreen = Screen.FromHandle(selfHandle) ?? Screen.PrimaryScreen;
            var islandBounds = islandScreen.Bounds;
            var workArea = islandScreen.WorkingArea;

            // 核心邊界隔離：前景視窗與動態島所屬螢幕是否重疊？
            int intersectLeft = Math.Max(r.Left, islandBounds.Left);
            int intersectTop = Math.Max(r.Top, islandBounds.Top);
            int intersectRight = Math.Min(r.Right, islandBounds.Right);
            int intersectBottom = Math.Min(r.Bottom, islandBounds.Bottom);
            bool overlapsIslandScreen = (intersectRight > intersectLeft) && (intersectBottom > intersectTop);

            if (!overlapsIslandScreen) {
                // 前景視窗完全位於其他螢幕（例如在副螢幕操作網頁/應用程式），零相交，絕不避讓！
                return false;
            }

            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            string pName = (fgPid != 0 && fgPid != ownPid) ? GetProcessNameFast(fgPid) : "";

            int centerX = workArea.Left + workArea.Width / 2;

            // 1. 若同螢幕前景為網頁瀏覽器，嚴格優先執行避讓判定（即使當前分頁為 ChatGPT / Claude / Grok）
            if (!string.IsNullOrEmpty(pName) && BrowserProcessNames.Contains(pName)) {
                // 檢查是否為全螢幕（覆蓋了動態島所在螢幕）
                if (r.Left <= islandBounds.Left + 2 && r.Top <= islandBounds.Top + 2 &&
                    r.Right >= islandBounds.Right - 2 && r.Bottom >= islandBounds.Bottom - 2) {
                    return true;
                }
                // 同螢幕瀏覽器分頁列通常在頂部，若頂端在頂部範圍且橫向覆蓋動態島，或瀏覽器最大化，則避讓
                if ((r.Top <= workArea.Top + 75 && r.Bottom >= workArea.Top + 50 &&
                     r.Left <= centerX + 220 && r.Right >= centerX - 220) ||
                    (r.Left <= workArea.Left + 15 && r.Top <= workArea.Top + 15 && r.Right >= workArea.Right - 15)) {
                    return true;
                }
                return false;
            }

            // 2. 若為非瀏覽器的獨立 AI 工具視窗（例如 Grok Bot 桌面端、Cursor、VS Code、Terminal 等），永遠維持置頂不避讓！
            if (IsAiToolWindow(fg, pName)) {
                return false;
            }

            // 3. 檢查一般視窗是否為全螢幕（覆蓋了動態島所在螢幕）
            if (r.Left <= islandBounds.Left + 2 && r.Top <= islandBounds.Top + 2 &&
                r.Right >= islandBounds.Right - 2 && r.Bottom >= islandBounds.Bottom - 2) {
                return true;
            }

            // 檢查同螢幕上的非 AI 視窗是否遮蔽螢幕頂部中央
            var className = new StringBuilder(256);
            GetClassName(fg, className, className.Capacity);
            string cls = className.ToString();
            if (!IsIgnoredSystemClass(cls)) {
                if (fgW >= 280 && fgH >= 160 &&
                    r.Top <= workArea.Top + 85 && r.Bottom >= workArea.Top + 50 &&
                    r.Left <= centerX + 300 && r.Right >= centerX - 300) {
                    return true;
                }
            }

            return false;
        }
    }
}
