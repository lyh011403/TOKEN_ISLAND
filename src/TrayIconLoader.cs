using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TokenIsland {
static class TrayIconLoader {
    const int IconSize = 32;
    const int SourceSize = 256;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
        int width, int height, uint step, IntPtr brush, uint flags);

    public static Icon Load() {
        // Use the largest layer so removing optical padding does not enlarge a tiny bitmap.
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TokenIsland.ico");
        IntPtr handle = LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */,
            SourceSize, SourceSize, 0x10 /* LR_LOADFROMFILE */);
        if (handle != IntPtr.Zero) {
            try {
                using (Icon borrowed = Icon.FromHandle(handle))
                    return CreateTrayImage(borrowed);
            } finally {
                DestroyIcon(handle);
            }
        }

        // Keep single-executable distribution working if the adjacent ICO is absent.
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TokenIsland.TrayIcon.ico")) {
            if (stream == null) throw new InvalidOperationException("Missing embedded tray icon.");
            using (Icon embedded = new Icon(stream, SourceSize, SourceSize))
                return CreateTrayImage(embedded);
        }
    }

    static Icon CreateTrayImage(Icon source) {
        using (Bitmap original = new Bitmap(SourceSize, SourceSize, PixelFormat.Format32bppArgb))
        using (Bitmap target = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb)) {
            // Draw the HICON: .NET Framework's Icon.ToBitmap cannot decode some
            // PNG-compressed 256px ICO frames.
            using (Graphics graphics = Graphics.FromImage(original)) {
                IntPtr dc = graphics.GetHdc();
                try {
                    if (!DrawIconEx(dc, 0, 0, source.Handle, SourceSize, SourceSize, 0, IntPtr.Zero, 3))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                } finally {
                    graphics.ReleaseHdc(dc);
                }
            }
            // This logo has a white ring on a black disc. Alpha bounds include the
            // dark outer rim, which makes the actual mark look smaller in the tray.
            int left = original.Width, top = original.Height, right = -1, bottom = -1;
            for (int y = 0; y < original.Height; y++) {
                for (int x = 0; x < original.Width; x++) {
                    Color pixel = original.GetPixel(x, y);
                    if (pixel.A < 128 || Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) < 96) continue;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            }
            RectangleF crop = new RectangleF(0, 0, original.Width, original.Height);
            if (right >= left && bottom >= top) {
                // Preserve aspect ratio and a small antialiasing margin around the ring.
                float side = Math.Max(right - left + 1, bottom - top + 1) + 2;
                crop = new RectangleF((left + right + 1 - side) / 2f,
                    (top + bottom + 1 - side) / 2f, side, side);
            }
            using (Graphics graphics = Graphics.FromImage(target)) {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(original, new RectangleF(0, 0, IconSize, IconSize), crop, GraphicsUnit.Pixel);
            }
            IntPtr handle = target.GetHicon();
            try {
                using (Icon borrowed = Icon.FromHandle(handle))
                    return (Icon)borrowed.Clone();
            } finally {
                DestroyIcon(handle);
            }
        }
    }
}
}
