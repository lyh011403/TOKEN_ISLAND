using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TokenIsland {
// Repair native Z order without activating, moving, or revealing the window.
sealed class TopmostGuard : IDisposable {
    readonly Window window;
    readonly Func<bool> suspended;
    readonly DispatcherTimer timer;
    bool disposed;
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public TopmostGuard(Window window, Func<bool> suspended = null) {
        this.window = window; this.suspended = suspended;
        timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += OnTick;
        window.SourceInitialized += OnRefresh;
        window.Deactivated += OnRefresh;
        window.IsVisibleChanged += OnVisibleChanged;
        window.Closed += OnClosed;
        if (window.IsVisible) timer.Start();
    }
    void OnTick(object sender, EventArgs e) { Ensure(); }
    void OnRefresh(object sender, EventArgs e) { Ensure(); }
    void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
        if (window.IsVisible) { timer.Start(); Ensure(); } else timer.Stop();
    }
    void OnClosed(object sender, EventArgs e) { Dispose(); }
    public void Ensure() {
        if (disposed || !window.IsVisible || !window.IsEnabled || window.WindowState == WindowState.Minimized || (suspended != null && suspended())) return;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Rect bounds;
        if (!GetWindowRect(hwnd, out bounds)) return;
        uint ownProcess; GetWindowThreadProcessId(hwnd, out ownProcess);
        bool covered = false;
        // Leave this application's menus, dialogs and animation overlays above it.
        for (IntPtr above = GetWindow(hwnd, 3); above != IntPtr.Zero; above = GetWindow(above, 3)) {
            if (!IsWindowVisible(above)) continue;
            uint process; GetWindowThreadProcessId(above, out process);
            if (process == ownProcess) return;
            Rect other;
            if (GetWindowRect(above, out other) && other.Left < bounds.Right && other.Right > bounds.Left && other.Top < bounds.Bottom && other.Bottom > bounds.Top) covered = true;
        }
        if (covered || (GetWindowLong(hwnd, -20) & 0x8) == 0)
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
    }
    public void Dispose() {
        if (disposed) return;
        disposed = true; timer.Stop(); timer.Tick -= OnTick;
        window.SourceInitialized -= OnRefresh; window.Deactivated -= OnRefresh;
        window.IsVisibleChanged -= OnVisibleChanged; window.Closed -= OnClosed;
    }
}
}
