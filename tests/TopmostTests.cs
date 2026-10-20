using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TokenIsland;

static class TopmostTests {
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    static Window CreateWindow() { return new Window { Title = "Token Island placement check", Left = 80, Top = 80, Width = 160, Height = 70, Topmost = true, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None }; }
    static void Pump() { var frame = new DispatcherFrame(); var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) }; t.Tick += delegate { t.Stop(); frame.Continue = false; }; t.Start(); Dispatcher.PushFrame(frame); }
    static bool Above(IntPtr a, IntPtr b) { for (var h = GetWindow(b, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h == a) return true; return false; }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    [STAThread] public static int Main(string[] args) {
        if (args.Length > 0) {
            var app = new Application(); var cover = CreateWindow(); cover.Show();
            Console.WriteLine(new WindowInteropHelper(cover).Handle.ToInt64());
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; timeout.Tick += delegate { cover.Close(); }; timeout.Start();
            app.Run(); return 0;
        }
        Window window = null; Process child = null;
        try {
            window = CreateWindow(); window.Show();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            using (var guard = new TopmostGuard(window)) {
                SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x13);
                Check((GetWindowLong(hwnd, -20) & 8) == 0, "Simulated native topmost loss");
                var focus = GetForegroundWindow(); Pump();
                Check((GetWindowLong(hwnd, -20) & 8) != 0, "Timer restores topmost");
                Check(GetForegroundWindow() == focus, "Repair preserves keyboard focus");
                child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--cover") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                var coverHandle = new IntPtr(long.Parse(child.StandardOutput.ReadLine()));
                Check(Above(coverHandle, hwnd), "External topmost window initially covers float");
                focus = GetForegroundWindow(); Pump();
                Check(Above(hwnd, coverHandle), "Timer raises float over external cover");
                Check(GetForegroundWindow() == focus, "Z-order repair does not activate float");
                child.Kill(); child.WaitForExit(); child.Dispose(); child = null;
                var dialog = CreateWindow(); dialog.Owner = window; dialog.Show();
                guard.Ensure(); Check(Above(new WindowInteropHelper(dialog).Handle, hwnd), "Own dialog remains above float"); dialog.Close();
                window.Hide(); Pump(); Check(!window.IsVisible, "Hidden float stays hidden");
                window.Show(); Pump(); Check((GetWindowLong(hwnd, -20) & 8) != 0, "Showing resumes topmost protection");
                guard.Dispose(); SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x13); Pump();
                Check((GetWindowLong(hwnd, -20) & 8) == 0, "Dispose stops protection");
            }
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e.Message); return 1; }
        finally { if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); } if (window != null) window.Close(); }
    }
}
