using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace TokenIsland {
enum AnimalMood { Unknown, Full, Watch, Low, Empty }
class PixelAnimal : FrameworkElement {
    public readonly Provider Provider;
    public string Theme;
    public string Animal = "cat";
    public static string CurrentAnimal = "cat";
    public bool Breathing = true, Animate = true, Reduced;
    public double? PreviewTime;
    public static readonly Stopwatch Time = Stopwatch.StartNew();
    static readonly Dictionary<string, Brush> palette = new Dictionary<string, Brush>();
    static readonly Dictionary<string, Dictionary<AnimalMood, BitmapSource[]>> themedFrames = new Dictionary<string, Dictionary<AnimalMood, BitmapSource[]>>();
    static readonly Dictionary<string, BitmapSource> unknownFrames = new Dictionary<string, BitmapSource>();

    public PixelAnimal(Provider provider, double size, string theme = null, string animal = null) {
        Provider = provider;
        Animal = animal ?? CurrentAnimal ?? "cat";
        Theme = theme ?? (provider != null ? provider.Theme : "orange");
        Width = size; Height = size; SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        IsHitTestVisible = false;
        EnsureTheme(Theme, Animal);
    }

    public static void ClearThemeCache() {
        themedFrames.Clear();
    }

    public static void EnsureTheme(string theme, string animal = null) {
        if (string.IsNullOrEmpty(theme)) theme = "orange";
        if (string.IsNullOrEmpty(animal)) animal = CurrentAnimal ?? "cat";
        string cacheKey = animal + ":" + theme;
        if (themedFrames.ContainsKey(cacheKey)) return;
        try {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(baseDir, "assets", animal);
            if (!Directory.Exists(dir)) dir = Path.Combine(Storage.Root, "assets", animal);
            if (!Directory.Exists(dir)) {
                dir = Path.Combine(baseDir, "assets", "cat");
                if (!Directory.Exists(dir)) dir = Path.Combine(Storage.Root, "assets", "cat");
            }

            var dict = new Dictionary<AnimalMood, BitmapSource[]>();
            dict[AnimalMood.Full] = LoadSheet(Path.Combine(dir, "01.png"), theme, animal);
            dict[AnimalMood.Watch] = LoadSheet(Path.Combine(dir, "02.png"), theme, animal);
            dict[AnimalMood.Low] = LoadSheet(Path.Combine(dir, "03.png"), theme, animal);
            dict[AnimalMood.Empty] = LoadSheet(Path.Combine(dir, "04.png"), theme, animal);
            themedFrames[cacheKey] = dict;

            if (!unknownFrames.ContainsKey(animal)) {
                string u = Path.Combine(dir, "unknown.png");
                if (File.Exists(u)) {
                    var dec = new PngBitmapDecoder(new Uri(u, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var f = dec.Frames[0];
                    f.Freeze();
                    unknownFrames[animal] = f;
                }
            }
        } catch { }
    }

    static BitmapSource[] LoadSheet(string path, string theme, string animal = null) {
        if (!File.Exists(path)) return null;
        var dec = new PngBitmapDecoder(new Uri(path, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        if (src.Format != PixelFormats.Bgra32) {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }
        src = ApplyColorTheme(src, theme, animal);
        src.Freeze();
        int frameSize = Math.Max(1, src.PixelHeight);
        int count = Math.Max(1, src.PixelWidth / frameSize);
        var arr = new BitmapSource[count];
        for (int i = 0; i < count; i++) {
            var crop = new CroppedBitmap(src, new Int32Rect(i * frameSize, 0, frameSize, frameSize));
            crop.Freeze();
            arr[i] = crop;
        }
        return arr;
    }

    static BitmapSource ApplyColorTheme(BitmapSource src, string theme, string animal = null) {
        if (string.IsNullOrEmpty(theme)) return src;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        byte[] pixels = new byte[h * stride];
        src.CopyPixels(pixels, stride, 0);

        double targetHue = -1;
        double targetSat = -1;
        double targetLum = -1;
        bool desaturate = false;
        if (theme == "blue") targetHue = 0.58;       // 蔚藍
        else if (theme == "purple") targetHue = 0.77; // 紫羅蘭
        else if (theme == "green") targetHue = 0.38;  // 翡翠綠
        else if (theme == "pink") targetHue = 0.94;   // 櫻花粉
        else if (theme == "orange") targetHue = 0.08; // 暖橘
        else if (theme == "silver") desaturate = true;// 曜石銀灰
        else if (theme.StartsWith("#") && (theme.Length == 4 || theme.Length == 7 || theme.Length == 9)) {
            try {
                var col = (Color)ColorConverter.ConvertFromString(theme);
                RgbToHsl(col.R, col.G, col.B, out targetHue, out targetSat, out targetLum);
                if (targetSat < 0.15) desaturate = true;
            } catch { }
        }

        bool isOwl = (animal == "owl");
        int frameSize = Math.Max(1, h);

        for (int y = 0; y < h; y++) {
            int rowOffset = y * stride;
            double relY = (double)y / h;
            for (int x = 0; x < w; x++) {
                int i = rowOffset + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a < 15) continue;

                if (isOwl) {
                    // 1. 樹枝硬切割保護：高度佔比 66.7% 以下 (y >= 64 in 96x96) 全數保留原木與陰影質感
                    if (relY >= (64.0 / 96.0)) continue;

                    // 2. 側邊萌發之嫩葉保護 (局部 x >= 66 且 y >= 58) 完整保留自然綠
                    int localX = x % frameSize;
                    if (localX >= 66 && y >= 58) continue;

                    // 3. 金色爪子色彩保護 (R > 180 && G > 120 && B < 60)
                    if (r > 180 && g > 120 && b < 60) continue;

                    double hVal, sVal, lVal;
                    RgbToHsl(r, g, b, out hVal, out sVal, out lVal);

                    // 4. 金黃色大圓眼保護 (色相 0.07~0.18 且高飽和度 s > 0.35) 保持炯炯有神
                    if (hVal >= 0.07 && hVal <= 0.18 && sVal > 0.35) continue;

                    // 5. 灰色羽毛智慧層次染色 (亮度 0.12 ~ 0.94)
                    if (lVal > 0.12 && lVal < 0.94) {
                        if (desaturate) {
                            sVal = Math.Min(sVal, 0.04);
                            if (targetLum >= 0) lVal = Math.Max(0.20, Math.Min(0.85, lVal * (targetLum / 0.55)));
                        } else if (targetHue >= 0) {
                            // 依據原始羽毛亮暗層次注入立體感飽和度
                            double factor = 1.0 - Math.Pow((lVal - 0.52) / 0.40, 2.0);
                            if (factor < 0) factor = 0;
                            double newS = 0.55 * factor + 0.12;
                            if (targetSat >= 0) newS = Math.Max(sVal, newS * (targetSat / 0.70));
                            else newS = Math.Max(sVal, newS);

                            hVal = targetHue;
                            sVal = Math.Max(0.10, Math.Min(1.0, newS));
                            if (targetLum >= 0) lVal = Math.Max(0.18, Math.Min(0.88, lVal * (targetLum / 0.55)));
                        } else {
                            continue;
                        }

                        byte nr, ng, nb;
                        HslToRgb(hVal, sVal, lVal, out nr, out ng, out nb);
                        pixels[i] = nb; pixels[i + 1] = ng; pixels[i + 2] = nr;
                    }
                } else {
                    double hVal, sVal, lVal;
                    RgbToHsl(r, g, b, out hVal, out sVal, out lVal);
                    if (sVal > 0.20 && lVal < 0.88) {
                        // 貓咪專屬生動特徵保護：保留粉嫩小鼻子與琥珀金大圓眼
                        if (animal == "cat") {
                            // 1. 粉紅小鼻子與肉色保護 (粉紅肉色 R > G+35 && R > B+40 && H 在 0.01~0.05 && L > 0.55)
                            if (r > g + 35 && r > b + 40 && hVal >= 0.01 && hVal <= 0.05 && lVal > 0.55) {
                                continue;
                            }
                            // 2. 琥珀金/橄欖金眼珠虹膜保護 (高飽和金黃綠 H 在 0.098~0.155，S > 0.45 && L 在 0.30~0.85)
                            if (hVal >= 0.098 && hVal <= 0.155 && sVal > 0.45 && lVal > 0.30 && lVal < 0.85) {
                                continue;
                            }
                        }

                        if (desaturate) {
                            sVal = 0.04;
                            if (targetLum >= 0) lVal = Math.Max(0.20, Math.Min(0.85, lVal * (targetLum / 0.55)));
                        } else if (targetHue >= 0) {
                            hVal = targetHue;
                            if (targetSat >= 0) sVal = Math.Max(0.20, Math.Min(1.0, sVal * (targetSat / 0.70)));
                            if (targetLum >= 0) lVal = Math.Max(0.18, Math.Min(0.86, lVal * (targetLum / 0.55)));
                        }
                        byte nr, ng, nb;
                        HslToRgb(hVal, sVal, lVal, out nr, out ng, out nb);
                        pixels[i] = nb; pixels[i + 1] = ng; pixels[i + 2] = nr;
                    }
                }
            }
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        wb.Freeze();
        return wb;
    }

    static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l) {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
        l = (max + min) / 2.0;
        if (max == min) { h = 0; s = 0; }
        else {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == rd) h = (gd - bd) / d + (gd < bd ? 6.0 : 0.0);
            else if (max == gd) h = (bd - rd) / d + 2.0;
            else h = (rd - gd) / d + 4.0;
            h /= 6.0;
        }
    }

    static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b) {
        if (s == 0) { r = g = b = (byte)(l * 255); }
        else {
            Func<double, double, double, double> hue2rgb = (p, q, t) => {
                if (t < 0) t += 1.0;
                if (t > 1) t -= 1.0;
                if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
                return p;
            };
            double qVal = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double pVal = 2.0 * l - qVal;
            r = (byte)Math.Round(hue2rgb(pVal, qVal, h + 1.0 / 3.0) * 255);
            g = (byte)Math.Round(hue2rgb(pVal, qVal, h) * 255);
            b = (byte)Math.Round(hue2rgb(pVal, qVal, h - 1.0 / 3.0) * 255);
        }
    }

    public static AnimalMood Mood(double? remaining, bool stale) {
        if (!remaining.HasValue) return AnimalMood.Unknown;
        if (remaining <= 0) return AnimalMood.Empty;
        if (remaining < 20) return AnimalMood.Low;
        if (remaining < 60) return AnimalMood.Watch;
        return AnimalMood.Full;
    }

    public static string MoodText(AnimalMood mood) {
        return mood == AnimalMood.Full ? "充足 · 精神飽滿" : mood == AnimalMood.Watch ? "留意 · 專注戒備" : mood == AnimalMood.Low ? "偏低 · 半瞇眼打盹" : mood == AnimalMood.Empty ? "用盡 · 軟爛休眠" : "未連接或資料待更新";
    }

    static Brush B(string color) {
        Brush b;
        if (!palette.TryGetValue(color, out b)) {
            b = (Brush)new BrushConverter().ConvertFromString(color);
            b.Freeze();
            palette[color] = b;
        }
        return b;
    }

    protected override void OnRender(DrawingContext dc) {
        var mood = Mood(Provider != null ? (Provider.DailyRemaining ?? Provider.Remaining) : null, Provider != null && Provider.Stale);
        bool unknown = mood == AnimalMood.Unknown;
        double t = PreviewTime ?? Time.Elapsed.TotalSeconds;
        bool motion = Animate && !Reduced && SystemParameters.ClientAreaAnimation;

        string animal = Animal ?? CurrentAnimal ?? "cat";
        string theme = Theme ?? (Provider != null ? Provider.Theme : "orange");
        EnsureTheme(theme, animal);

        BitmapSource frame = null;
        int idx = 0;
        string cacheKey = animal + ":" + theme;
        if (unknown) {
            if (unknownFrames.ContainsKey(animal)) frame = unknownFrames[animal];
            else if (unknownFrames.ContainsKey("cat")) frame = unknownFrames["cat"];
        } else if (themedFrames.ContainsKey(cacheKey)) {
            var dict = themedFrames[cacheKey];
            if (dict.ContainsKey(mood) && dict[mood] != null && dict[mood].Length > 0) {
                var frames = dict[mood];
                idx = motion ? ((int)(t * 10.0) % frames.Length) : 0;
                frame = frames[idx];
            }
        }

        if (frame != null) {
            bool applyBreathing = Breathing && !Reduced && SystemParameters.ClientAreaAnimation && !unknown && mood != AnimalMood.Empty;
            if (unknown) {
                dc.PushOpacity(0.65);
            } else if (applyBreathing) {
                double light = .84 + .16 * (1 + Math.Sin(t * 2 * Math.PI / 3)) / 2;
                dc.PushOpacity(light);
            }
            Rect drawRect = new Rect(0, 0, ActualWidth, ActualHeight);
            if (animal == "fox") {
                double scale = 1.15;
                double drawW = ActualWidth * scale;
                double drawH = ActualHeight * scale;
                double drawX = (ActualWidth - drawW) / 2.0;
                double drawY = (ActualHeight - drawH) + (ActualHeight * 0.05);
                drawRect = new Rect(drawX, drawY, drawW, drawH);
            } else if (animal == "owl") {
                double scale = 1.12;
                double drawW = ActualWidth * scale;
                double drawH = ActualHeight * scale;
                double drawX = (ActualWidth - drawW) / 2.0;
                double drawY = (ActualHeight - drawH) / 2.0;
                drawRect = new Rect(drawX, drawY, drawW, drawH);
            }
            dc.DrawImage(frame, drawRect);
            if (unknown || applyBreathing) dc.Pop();

            // 01 充足（≥60%）：方案 C 大顆璀璨四角菱芒（7.5*s，向上升騰淡化、邊界防超框）
            if (mood == AnimalMood.Full) {
                // 防超框限制：絕對裁剪於按鈕矩形內，確保任何光芒頂點絕不溢出框外
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

                double s = Math.Min(ActualWidth, ActualHeight) / 32.0;
                bool isFox = animal == "fox";
                double s1x = ActualWidth * (isFox ? 0.70 : 0.68);
                double s1y = ActualHeight * (isFox ? 0.32 : 0.34);
                double s2x = ActualWidth * (isFox ? 0.28 : 0.26);
                double s2y = ActualHeight * (isFox ? 0.36 : 0.38);

                if (!motion) {
                    DrawPixelSparkle(dc, s1x, ActualHeight * (isFox ? 0.28 : 0.30), 7.5 * s, 0.95, true);
                    DrawPixelSparkle(dc, s2x, ActualHeight * (isFox ? 0.32 : 0.34), 5.2 * s, 0.85, false);
                } else {
                    double cycle = 2.0;

                    // 粒子 1（大顆主芒 7.5*s）：向上升騰並平滑淡化消散
                    double p1 = (t * 0.85) % cycle;
                    double life1 = 1.6;
                    if (p1 < life1) {
                        double prog1 = p1 / life1;
                        double dy1 = -Math.Pow(prog1, 0.85) * (4.2 * s);
                        double op1 = prog1 < 0.25 ? (prog1 / 0.25) * 0.95 :
                                     prog1 < 0.50 ? 0.95 :
                                     0.95 * Math.Max(0.0, 1.0 - (prog1 - 0.50) / 0.50);
                        DrawPixelSparkle(dc, s1x, s1y + dy1, 7.5 * s, op1, true);
                    }

                    // 粒子 2（大顆副芒 5.2*s，相位交錯 1.0s）：交錯升騰淡化
                    double p2 = ((t * 0.85) + 1.0) % cycle;
                    double life2 = 1.5;
                    if (p2 < life2) {
                        double prog2 = p2 / life2;
                        double dy2 = -Math.Pow(prog2, 0.85) * (3.6 * s);
                        double op2 = prog2 < 0.25 ? (prog2 / 0.25) * 0.85 :
                                     prog2 < 0.50 ? 0.85 :
                                     0.85 * Math.Max(0.0, 1.0 - (prog2 - 0.50) / 0.50);
                        DrawPixelSparkle(dc, s2x, s2y + dy2, 5.2 * s, op2, false);
                    }
                }

                dc.Pop(); // 移除 Clip
            }

            // 02 留意警戒（20–59%）：第一幀閃現經典像素「！」驚嘆號特效
            if (mood == AnimalMood.Watch && (!motion || idx <= 3)) {
                double s = Math.Min(ActualWidth, ActualHeight) / 32.0;
                double op = !motion ? 1.0 : idx == 0 ? 1.0 : idx == 1 ? 0.95 : idx == 2 ? 0.85 : 0.45;
                double bounceY = (!motion || idx != 1) ? 0 : -1.0 * s;
                dc.PushOpacity(op);

                double cx = ActualWidth * (animal == "owl" ? 0.35 : (animal == "fox" ? 0.38 : 0.36));
                double cy = (animal == "fox" ? ActualHeight * 0.06 : (animal == "owl" ? ActualHeight * 0.08 : ActualHeight * 0.15)) + bounceY;

                // 第一幀爆閃十字芒刺
                if (!motion || idx == 0) {
                    dc.DrawRectangle(B("#FFFFFF"), null, new Rect(cx - 5 * s, cy + 2 * s, 1.5 * s, 1.5 * s));
                    dc.DrawRectangle(B("#FFFFFF"), null, new Rect(cx + 6 * s, cy + 2 * s, 1.5 * s, 1.5 * s));
                    dc.DrawRectangle(B("#FFFFFF"), null, new Rect(cx, cy - 4 * s, 1.5 * s, 1.5 * s));
                    dc.DrawRectangle(B("#FFE600"), null, new Rect(cx - 3 * s, cy - 2 * s, 1.5 * s, 1.5 * s));
                    dc.DrawRectangle(B("#FFE600"), null, new Rect(cx + 4 * s, cy - 2 * s, 1.5 * s, 1.5 * s));
                }

                // 像素驚嘆號主體（經典警戒黃配純黑像素邊框）
                dc.DrawRectangle(B("#111111"), null, new Rect(cx - 1.5 * s, cy - 3 * s, 4 * s, 8 * s));
                dc.DrawRectangle(B("#FFD300"), null, new Rect(cx - 0.5 * s, cy - 2 * s, 2 * s, 6 * s));
                dc.DrawRectangle(B("#FFFFFF"), null, new Rect(cx - 0.5 * s, cy - 2 * s, 2 * s, 2 * s));

                dc.DrawRectangle(B("#111111"), null, new Rect(cx - 1.5 * s, cy + 6.5 * s, 4 * s, 4 * s));
                dc.DrawRectangle(B("#FFD300"), null, new Rect(cx - 0.5 * s, cy + 7.5 * s, 2 * s, 2 * s));

                dc.Pop();
            }

            // 03 偏低（1–19%）：疲憊滑落冷汗珠特效
            if (mood == AnimalMood.Low) {
                double s = Math.Min(ActualWidth, ActualHeight) / 32.0;
                double hx = ActualWidth * (animal == "owl" ? 0.33 : (animal == "fox" ? 0.38 : 0.42));
                double hy = ActualHeight * (animal == "owl" ? 0.46 : (animal == "fox" ? 0.52 : 0.50));

                if (!motion) {
                    DrawPixelSweat(dc, hx, hy, 4.2 * s, 0.9);
                } else {
                    double p = (t * 0.9) % 1.8;
                    if (p < 1.3) {
                        double prog = p / 1.3;
                        double op = prog < 0.2 ? (prog / 0.2) : prog <= 0.8 ? 1.0 : ((1.0 - prog) / 0.2);
                        double dy = Math.Pow(prog, 1.2) * 5.0 * s;
                        double szScale = Math.Min(1.0, 0.6 + prog * 0.4);
                        DrawPixelSweat(dc, hx, hy + dy, 4.2 * s * szScale, op);
                    }
                }
            }

            // 04 用盡（0%）：頭頂動態升騰 ZZZ 特效
            if (mood == AnimalMood.Empty) {
                double s = Math.Min(ActualWidth, ActualHeight) / 32.0;
                double zPhase = (t * 2.0) % 3.0;
                double zx = ActualWidth * (animal == "fox" ? 0.30 : (animal == "owl" ? 0.36 : 0.38));
                double zy = ActualHeight * (animal == "fox" ? 0.30 : (animal == "owl" ? 0.28 : 0.22));
                DrawPixelZ(dc, zx + Math.Sin(zPhase * 2) * 1.5 * s, zy - (zPhase * 3.5 * s), 5 * s, Math.Min(1.0, Math.Max(0.0, 1.2 - zPhase * 0.35)));
                double z2 = (zPhase + 1.0) % 3.0;
                DrawPixelZ(dc, zx + ActualWidth * 0.10 + Math.Cos(z2 * 2) * 1.5 * s, zy + ActualHeight * 0.04 - (z2 * 3.5 * s), 4 * s, Math.Min(1.0, Math.Max(0.0, 1.2 - z2 * 0.35)));
            }
        } else {
            double scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
            dc.PushTransform(new ScaleTransform(scale, scale));
            string fur = unknown ? "#737F89" : (Provider.Color ?? "#EDA46A");
            Action<string, double, double, double, double> box = (color, x, y, w, h) => dc.DrawRectangle(B(color), null, new Rect(x, y, w, h));
            box(fur, 5, 7, 14, 12);
            box("#F2EAD5", 6, 13, 12, 4);
            dc.Pop();
        }
    }

    // 方案 C 璀璨四角菱芒 (Radiant Diamond Glint)
    static void DrawPixelSparkle(DrawingContext dc, double cx, double cy, double size, double opacity, bool isLarge) {
        if (opacity <= 0.03) return;
        dc.PushOpacity(opacity);
        var gold = B("#F59E0B");
        var brightGold = B("#FEF08A");
        var white = Brushes.White;

        double vertH = size * 1.5;
        double vertW = size * 0.42;
        double horizW = size * 1.1;
        double horizH = size * 0.38;

        // 垂直細長菱形
        var geoV = new StreamGeometry();
        using (var ctx = geoV.Open()) {
            ctx.BeginFigure(new Point(cx, cy - vertH * 0.5), true, true);
            ctx.LineTo(new Point(cx + vertW * 0.5, cy), true, false);
            ctx.LineTo(new Point(cx, cy + vertH * 0.5), true, false);
            ctx.LineTo(new Point(cx - vertW * 0.5, cy), true, false);
        }
        geoV.Freeze();
        dc.DrawGeometry(gold, null, geoV);

        // 水平稍短菱形
        var geoH = new StreamGeometry();
        using (var ctx = geoH.Open()) {
            ctx.BeginFigure(new Point(cx, cy - horizH * 0.5), true, true);
            ctx.LineTo(new Point(cx + horizW * 0.5, cy), true, false);
            ctx.LineTo(new Point(cx, cy + horizH * 0.5), true, false);
            ctx.LineTo(new Point(cx - horizW * 0.5, cy), true, false);
        }
        geoH.Freeze();
        dc.DrawGeometry(gold, null, geoH);

        // 中心晶體核心菱形 (高亮淡黃金)
        double cSize = size * 0.55;
        var geoC = new StreamGeometry();
        using (var ctx = geoC.Open()) {
            ctx.BeginFigure(new Point(cx, cy - cSize * 0.5), true, true);
            ctx.LineTo(new Point(cx + cSize * 0.5, cy), true, false);
            ctx.LineTo(new Point(cx, cy + cSize * 0.5), true, false);
            ctx.LineTo(new Point(cx - cSize * 0.5, cy), true, false);
        }
        geoC.Freeze();
        dc.DrawGeometry(brightGold, null, geoC);

        // 核心純白微芒
        double wSize = size * 0.28;
        var geoW = new StreamGeometry();
        using (var ctx = geoW.Open()) {
            ctx.BeginFigure(new Point(cx, cy - wSize * 0.5), true, true);
            ctx.LineTo(new Point(cx + wSize * 0.5, cy), true, false);
            ctx.LineTo(new Point(cx, cy + wSize * 0.5), true, false);
            ctx.LineTo(new Point(cx - wSize * 0.5, cy), true, false);
        }
        geoW.Freeze();
        dc.DrawGeometry(white, null, geoW);

        // 凹角晶塵光點
        if (isLarge) {
            double dot = Math.Max(1.2, size * 0.16);
            double d = size * 0.55;
            dc.DrawRectangle(brightGold, null, new Rect(cx - d, cy - d, dot, dot));
            dc.DrawRectangle(brightGold, null, new Rect(cx + d - dot, cy - d, dot, dot));
            dc.DrawRectangle(brightGold, null, new Rect(cx - d, cy + d - dot, dot, dot));
            dc.DrawRectangle(brightGold, null, new Rect(cx + d - dot, cy + d - dot, dot, dot));
        }
        dc.Pop();
    }

    static void DrawPixelSweat(DrawingContext dc, double cx, double cy, double size, double opacity) {
        if (opacity <= 0.05) return;
        dc.PushOpacity(opacity);

        var darkBlue = B("#0F172A");
        var midBlue  = B("#3B82F6");
        var lightBlue= B("#93C5FD");
        var white    = Brushes.White;

        double u = Math.Max(1.0, size / 5.0);

        // 像素水滴造型 (上尖下圓)
        dc.DrawRectangle(darkBlue, null, new Rect(cx + u * 0.8, cy - u * 0.2, u * 1.4, u * 1.2));
        dc.DrawRectangle(darkBlue, null, new Rect(cx - u * 0.2, cy + u * 0.8, u * 3.4, u * 3.4));
        dc.DrawRectangle(darkBlue, null, new Rect(cx + u * 0.3, cy + u * 4.0, u * 2.4, u * 1.2));

        dc.DrawRectangle(midBlue, null, new Rect(cx, cy + u, u * 3.0, u * 3.0));
        dc.DrawRectangle(midBlue, null, new Rect(cx + u * 0.5, cy + u * 4.0, u * 2.0, u * 0.8));

        dc.DrawRectangle(lightBlue, null, new Rect(cx + u, cy, u, u));
        dc.DrawRectangle(lightBlue, null, new Rect(cx + u * 0.3, cy + u * 1.2, u * 1.0, u * 2.0));

        dc.DrawRectangle(white, null, new Rect(cx + u * 0.5, cy + u * 1.5, u * 0.9, u * 0.9));

        dc.Pop();
    }

    static void DrawPixelZ(DrawingContext dc, double x, double y, double size, double opacity) {
        if (opacity <= 0.05) return;
        dc.PushOpacity(opacity);
        var b = B("#8AC4DF");
        double th = Math.Max(1.0, size / 4.0);
        dc.DrawRectangle(b, null, new Rect(x, y, size, th));
        dc.DrawRectangle(b, null, new Rect(x + size * 0.6, y + size * 0.3, th, th));
        dc.DrawRectangle(b, null, new Rect(x + size * 0.3, y + size * 0.6, th, th));
        dc.DrawRectangle(b, null, new Rect(x, y + size - th, size, th));
        dc.Pop();
    }
}
class PixelBorder : Border {
    protected override void OnRender(DrawingContext dc) {
        double w = ActualWidth, h = ActualHeight; if (w < 16 || h < 16) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open()) { c.BeginFigure(new Point(8,1),true,true); c.PolyLineTo(new[] {new Point(w-8,1),new Point(w-8,3),new Point(w-4,3),new Point(w-4,7),new Point(w-1,7),new Point(w-1,h-7),new Point(w-4,h-7),new Point(w-4,h-3),new Point(w-8,h-3),new Point(w-8,h-1),new Point(8,h-1),new Point(8,h-3),new Point(4,h-3),new Point(4,h-7),new Point(1,h-7),new Point(1,7),new Point(4,7),new Point(4,3),new Point(8,3)},true,false); }
        geo.Freeze(); dc.DrawGeometry(Island.Brush("#111D26"),new Pen(Island.Brush("#6B8393"),1),geo);
        dc.DrawLine(new Pen(Island.Brush("#2B3D49"),1),new Point(10,4),new Point(w-10,4));
        dc.DrawLine(new Pen(Island.Brush("#2B3D49"),1),new Point(10,h-4),new Point(w-10,h-4));
    }
}
static class PixelPreview {
    static Provider Example(string id, double? remaining, string theme) { var p = new Provider(id,id,"#C7DEE4") { Theme = theme }; if (remaining.HasValue) p.Quotas.Add(new Quota { Remaining = remaining, Reset = DateTime.UtcNow.AddDays(1) }); return p; }
    static byte[] Frame(bool breath, bool animate, bool reduced, double t) {
        try {
            var a = new PixelAnimal(Example("codex",80,"green"),72) { Breathing = breath, Animate = animate, Reduced = reduced, PreviewTime = t };
            a.Measure(new Size(72,72)); a.Arrange(new Rect(0,0,72,72)); a.UpdateLayout(); var b = new RenderTargetBitmap(72,72,96,96,PixelFormats.Pbgra32); b.Render(a); byte[] pixels = new byte[72*72*4]; b.CopyPixels(pixels,72*4,0); return pixels;
        } catch (Exception ex) {
            try { File.WriteAllText(Path.Combine(Storage.State, "frame-err.txt"), ex.ToString()); } catch { }
            throw;
        }
    }
    public static bool FrameDifference(bool breath, bool animate, bool reduced) { return !Frame(breath,animate,reduced,0).SequenceEqual(Frame(breath,animate,reduced,1)); }
    public static void Save() {
        var panel = new StackPanel { Background = Island.Brush("#0C121E"), Width = 640, Margin = new Thickness(0) };
        var header = new StackPanel { Margin = new Thickness(22, 18, 22, 14) };
        header.Children.Add(new TextBlock { Text = "靈動伴侶狀態圖鑑 · 三大角色與用量情緒", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Island.Brush("#F8FAFC") });
        header.Children.Add(new TextBlock { Text = "依據各模型即時剩餘配額，動態演繹五階像素情緒動態與專屬主題染色", FontSize = 11, Foreground = Island.Brush("#94A3B8"), Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(header);

        string[] labels = {"充足 ≥60%","留意 20–59%","偏低 1–19%","用盡 0%","未知／過期"};
        double?[] values = {80, 40, 10, 0, null};
        string[] animals = { "cat", "fox", "owl" };
        string[] ids = { "claude", "antigravity", "codex" };
        string[] themes = { "orange", "blue", "silver" };
        string[] rowTitles = { "貓咪 (Cat)\n經典暖橘", "赤狐 (Fox)\nAntigravity 蔚藍", "貓頭鷹 (Owl)\n曜石灰" };

        for(int r = 0; r < 3; r++) {
            string animal = animals[r];
            string id = ids[r], theme = themes[r];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 2, 18, 12) };
            row.Children.Add(new TextBlock {
                Text = rowTitles[r], Width = 104, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Island.Brush("#E2E8F0"), VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 16
            });
            for(int i = 0; i < 5; i++) {
                var cell = new StackPanel { Width = 96 };
                cell.Children.Add(new PixelAnimal(Example(id, values[i], theme), 64, theme, animal) {
                    PreviewTime = 0, Reduced = true, HorizontalAlignment = HorizontalAlignment.Center
                });
                cell.Children.Add(new TextBlock {
                    Text = labels[i], FontSize = 10, Foreground = Island.Brush("#94A3B8"),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
                });
                row.Children.Add(cell);
            }
            panel.Children.Add(row);
        }
        panel.Measure(new Size(640, double.PositiveInfinity));
        panel.Arrange(new Rect(new Point(), panel.DesiredSize));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var f = File.Create(Path.Combine(Storage.Root, "animal-states.png"))) encoder.Save(f);
    }
}

static class CatDropOverlay {
    static BitmapSource[] frames;
    static bool loaded = false;

    public static BitmapSource[] LoadFrames() {
        if (loaded) return frames;
        loaded = true;
        try {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "cat_play");
            if (!Directory.Exists(dir)) dir = Path.Combine(Storage.Root, "assets", "cat_play");
            if (Directory.Exists(dir)) {
                var list = new List<BitmapSource>();
                for (int i = 0; i <= 93; i++) {
                    string fn = Path.Combine(dir, string.Format("CAT_PLAY_{0:D3}.png", i));
                    if (!File.Exists(fn)) break;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(fn, UriKind.Absolute);
                    bmp.DecodePixelWidth = 280;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    list.Add(bmp);
                }
                if (list.Count > 0) frames = list.ToArray();
            }
        } catch { }
        return frames;
    }

    public static void Show(string modelName, long? tokenDelta, double? percentDelta, double? currentRemaining, Window owner, Preferences prefs) {
        if (prefs != null && !prefs.TokenDropAnimation) return;
        var f = LoadFrames();
        if (f == null || f.Length == 0) return;

        if (owner != null && owner.Dispatcher != null) {
            owner.Dispatcher.BeginInvoke(new Action(delegate {
                try {
                    new CatDropWindow(modelName, tokenDelta, percentDelta, currentRemaining, owner, prefs, f).Show();
                } catch { }
            }));
        }
    }

    static FontFamily pixelFont;
    public static FontFamily PixelFont {
        get {
            if (pixelFont == null) {
                try {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string fontPath = Path.Combine(baseDir, "assets", "PressStart2P.ttf");
                    if (!File.Exists(fontPath)) fontPath = Path.Combine(Storage.Root, "assets", "PressStart2P.ttf");
                    if (File.Exists(fontPath)) {
                        string folder = Path.GetDirectoryName(Path.GetFullPath(fontPath)).Replace('\\', '/');
                        if (!folder.EndsWith("/")) folder += "/";
                        pixelFont = new FontFamily(new Uri("file:///" + folder), "./#Press Start 2P");
                    }
                } catch { }
                if (pixelFont == null) pixelFont = new FontFamily("VT323, Consolas, monospace");
            }
            return pixelFont;
        }
    }

    public static FrameworkElement MakeTightPixelText(string text, double fontSize, Brush brush, bool stroke, string glowCol = null) {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        if (stroke) {
            int[] dx = { -2, 2, 0, 0, -2, 2, -2, 2 };
            int[] dy = { 0, 0, -2, 2, -2, -2, 2, 2 };
            for (int i = 0; i < 8; i++) {
                var shadow = new TextBlock {
                    Text = text,
                    FontSize = fontSize,
                    FontFamily = PixelFont,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(dx[i], dy[i], -dx[i], -dy[i])
                };
                TextOptions.SetTextRenderingMode(shadow, TextRenderingMode.Aliased);
                TextOptions.SetTextFormattingMode(shadow, TextFormattingMode.Display);
                grid.Children.Add(shadow);
            }
        }

        var tb = new TextBlock {
            Text = text,
            FontSize = fontSize,
            FontFamily = PixelFont,
            Foreground = brush
        };
        TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
        grid.Children.Add(tb);

        if (!string.IsNullOrEmpty(glowCol)) {
            var col = (Color)ColorConverter.ConvertFromString(glowCol);
            grid.Effect = new System.Windows.Media.Effects.DropShadowEffect {
                Color = col,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.85
            };
        }
        return grid;
    }

    static Border MakeDemoCard(string optTitle, string optDesc, string mainText, string subText, string mainCol, string subCol, string glowCol, bool stroke = true) {
        var card = new Border {
            Background = Island.Brush("#0C0F14"),
            BorderBrush = Island.Brush("#1E252D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(22, 0, 22, 14),
            Padding = new Thickness(18, 14, 18, 16)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = optTitle, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Island.Brush("#F3F7FA"), Margin = new Thickness(0, 0, 0, 2) });
        stack.Children.Add(new TextBlock { Text = optDesc, FontSize = 11, Foreground = Island.Brush("#8EA1AE"), Margin = new Thickness(0, 0, 0, 14) });

        var dispStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 4) };

        var big = MakeTightPixelText(mainText, 30, Island.Brush(mainCol), stroke, glowCol);
        big.Margin = new Thickness(0, 0, 14, 0);
        dispStack.Children.Add(big);

        var sub = MakeTightPixelText(subText, 13, Island.Brush(subCol), stroke, null);
        sub.VerticalAlignment = VerticalAlignment.Center;
        dispStack.Children.Add(sub);

        stack.Children.Add(dispStack);
        card.Child = stack;
        return card;
    }

    public static void CapturePixelStyleDemo(string outputPath) {
        var panel = new StackPanel { Background = Island.Brush("#07090C"), Width = 660 };
        panel.Children.Add(new TextBlock { Text = "Press Start 2P 緊湊比例化跳字展示（字距與比例優化版）", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Island.Brush("#F2F5F8"), Margin = new Thickness(24, 18, 24, 6) });
        panel.Children.Add(new TextBlock { Text = "已消除等寬像素字在「1、減號、逗號」之間的過度空隙，字形緊湊飽滿，1:1 還原您上傳的參考圖風格。", FontSize = 11, Foreground = Island.Brush("#8EA1AE"), Margin = new Thickness(24, 0, 24, 16), TextWrapping = TextWrapping.Wrap });

        panel.Children.Add(MakeDemoCard("樣式 A：街機黃金厚像素（緊湊比例 · 強烈推薦）", "Press Start 2P 智慧緊湊比例 + 亮金黃主字（#FFD700）+ 硬核像素黑輪廓，比例勻稱不開散", "-1,450", "TOKENS (-3.2%)", "#FFD700", "#FFAA00", "#FF9500", true));
        panel.Children.Add(MakeDemoCard("樣式 B：Nothing 琥珀霓虹方塊（科技純光暈款）", "100% 延續 RF_AIBOX_DM 琥珀橙光（#FF9500）+ 飽和橙金光暈，純粹方塊像素感，質感奢華", "-1,450", "TOKENS (-3.2%)", "#FF9500", "#FFB340", "#FF9500", false));
        panel.Children.Add(MakeDemoCard("樣式 C：高反差冰白芯 + 琥珀雙層（穿透力最強）", "立體冰白字芯（#FFFFFF）+ 琥珀橙金副標，即使桌面背景極其複雜或全白，依然清晰銳利", "-1,450", "TOKENS (-3.2%)", "#FFFFFF", "#FF9500", "#FF9500", true));

        panel.Measure(new Size(660, double.PositiveInfinity));
        panel.Arrange(new Rect(new Point(0, 0), panel.DesiredSize));
        panel.UpdateLayout();

        int w = 660, h = (int)Math.Ceiling(panel.ActualHeight) + 12;
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(panel);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var stream = File.Create(outputPath)) enc.Save(stream);
    }

    public static void CaptureDropPreview(string outputPath, Preferences prefs) {
        var f = LoadFrames();
        if (f == null || f.Length == 0) return;
        double s = prefs != null ? Math.Max(0.2, Math.Min(5.0, prefs.DropAnimScale)) : 1.0;
        int frameIdx = Math.Min(35, f.Length - 1);
        double catW = 240 * s;
        double catH = 426 * s;
        double textRequiredH = Math.Max(84.0, 115.0 * s);
        double totalW = Math.Max(420 * s, 380);
        double totalH = catH + 10 * s + textRequiredH;

        var grid = new Grid { Width = totalW, Height = totalH, Background = Brushes.Transparent };
        var cat = new Image {
            Source = f[frameIdx],
            Width = catW,
            Height = catH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(cat, BitmapScalingMode.HighQuality);
        grid.Children.Add(cat);

        // 讀取使用者選定的樣式（預設 B：Nothing 琥珀霓虹方塊純懸浮）
        string style = prefs != null && !string.IsNullOrEmpty(prefs.DropTextStyle) ? prefs.DropTextStyle : "B";
        string mainCol = style == "A" ? "#FFD700" : style == "C" ? "#FFFFFF" : "#FF9500";
        string subCol  = style == "A" ? "#FFAA00" : style == "C" ? "#FF9500" : "#FFB340";
        string glowCol = "#FF9500";
        bool stroke    = style != "B";

        // 防文字縮小時看不清的最小保護字號，與縮小貓咪比例完美協調
        double mainFontSize = Math.Max(18.0, 32.0 * s);
        double subFontSize  = Math.Max(9.5, 13.5 * s);

        var dispStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        
        double aiFontSize = Math.Max(10.0, 13.0 * s);
        string aiCol = style == "A" ? "#FFE57F" : style == "C" ? "#C2E2FF" : "#FFC266";
        var aiHeader = MakeTightPixelText("ANTIGRAVITY", aiFontSize, Island.Brush(aiCol), stroke, null);
        aiHeader.HorizontalAlignment = HorizontalAlignment.Center;
        aiHeader.Margin = new Thickness(0, 0, 0, 2 * s);
        dispStack.Children.Add(aiHeader);

        var big = MakeTightPixelText("-1,420", mainFontSize, Island.Brush(mainCol), stroke, glowCol);
        big.HorizontalAlignment = HorizontalAlignment.Center;
        big.Margin = new Thickness(0, 0, 0, 3 * s);
        dispStack.Children.Add(big);

        var sub = MakeTightPixelText("TOKENS (-3.5%)", subFontSize, Island.Brush(subCol), stroke, null);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        dispStack.Children.Add(sub);

        // 嚴格放置於貓咪正下方！零重疊！純文字懸浮！高度完全保底不截斷！
        var floatText = new Border {
            Background = Brushes.Transparent,
            Child = dispStack,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, catH + 8 * s, 0, 0),
            Opacity = 1.0
        };
        grid.Children.Add(floatText);

        grid.Measure(new Size(totalW, totalH));
        grid.Arrange(new Rect(0, 0, totalW, totalH));
        grid.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)totalW, (int)totalH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(grid);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var stream = File.Create(outputPath)) encoder.Save(stream);
    }
}

class CatDropWindow : Window {
    System.Windows.Threading.DispatcherTimer timer;
    int currentFrame = 0;
    Image catImg;
    Border floatText;
    ScaleTransform scaleTr;
    TranslateTransform transTr;
    BitmapSource[] frames;
    bool popped = false;

    public CatDropWindow(string modelName, long? tokenDelta, double? percentDelta, double? currentRemaining, Window owner, Preferences prefs, BitmapSource[] framesList) {
        frames = framesList;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        double s = prefs != null ? Math.Max(0.2, Math.Min(5.0, prefs.DropAnimScale)) : 1.0;
        double catW = 240 * s;
        double catH = 426 * s;
        double textRequiredH = Math.Max(84.0, 115.0 * s);
        Width = Math.Max(420 * s, 380);
        Height = catH + 10 * s + textRequiredH;

        Rect screenArea = SystemParameters.WorkArea;
        if (owner != null) {
            PresentationSource src = PresentationSource.FromVisual(owner);
            if (src != null && src.CompositionTarget != null) {
                var matrix = src.CompositionTarget.TransformToDevice;
                Point point = matrix.Transform(new Point(owner.Left + owner.Width / 2, owner.Top));
                var area = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
                var inverse = src.CompositionTarget.TransformFromDevice;
                screenArea = new Rect(inverse.Transform(new Point(area.Left, area.Top)), inverse.Transform(new Point(area.Right, area.Bottom)));
            }
        }

        Top = screenArea.Top;
        string pos = prefs != null ? prefs.DropAnimPos : "island";
        if (pos == "center") {
            Left = screenArea.Left + (screenArea.Width - Width) / 2;
        } else if (pos == "right") {
            Left = screenArea.Right - Width - 30;
        } else {
            if (owner != null) {
                Left = Math.Max(screenArea.Left + 10, Math.Min(owner.Left + (owner.Width - Width) / 2, screenArea.Right - Width - 10));
            } else {
                Left = screenArea.Left + (screenArea.Width - Width) / 2;
            }
        }

        var grid = new Grid();
        catImg = new Image {
            Source = frames[0],
            Width = catW,
            Height = catH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(catImg, BitmapScalingMode.HighQuality);
        grid.Children.Add(catImg);

        // 讀取樣式（預設 B：Nothing 琥珀霓虹方塊純懸浮）
        string style = prefs != null && !string.IsNullOrEmpty(prefs.DropTextStyle) ? prefs.DropTextStyle : "B";
        string mainCol = style == "A" ? "#FFD700" : style == "C" ? "#FFFFFF" : "#FF9500";
        string subCol  = style == "A" ? "#FFAA00" : style == "C" ? "#FF9500" : "#FFB340";
        string glowCol = "#FF9500";
        bool stroke    = style != "B";

        // 防縮小看不清保護，小尺寸下與縮小貓咪比例完美協調
        double mainFontSize = Math.Max(18.0, 32.0 * s);
        double subFontSize  = Math.Max(9.5, 13.5 * s);

        // 頂部明確標註消耗來源之 AI 模型名稱（例如 ANTIGRAVITY、CODEX、CLAUDE）
        var dispStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        string aiTitle = !string.IsNullOrEmpty(modelName) ? modelName : "AI";
        if (aiTitle.Contains("·")) aiTitle = aiTitle.Substring(0, aiTitle.IndexOf("·")).Trim();
        aiTitle = aiTitle.ToUpperInvariant();

        double aiFontSize = Math.Max(10.0, 13.0 * s);
        string aiCol = style == "A" ? "#FFE57F" : style == "C" ? "#C2E2FF" : "#FFC266";
        var aiHeader = CatDropOverlay.MakeTightPixelText(aiTitle, aiFontSize, Island.Brush(aiCol), stroke, null);
        aiHeader.HorizontalAlignment = HorizontalAlignment.Center;
        aiHeader.Margin = new Thickness(0, 0, 0, 2 * s);
        dispStack.Children.Add(aiHeader);

        string mainStr;
        string subStr;
        if (tokenDelta.HasValue && tokenDelta.Value > 0) {
            bool turnSummary = modelName != null && modelName.Contains("本輪總計");
            mainStr = string.Format(turnSummary ? "{0:N0}" : "-{0:N0}", tokenDelta.Value);
            subStr = turnSummary ? "TURN TOTAL TOKENS" : percentDelta.HasValue && percentDelta.Value > 0 ? string.Format("TOKENS (-{0:0.#}%)", percentDelta.Value) : "TOKENS CONSUMED";
        } else if (percentDelta.HasValue && percentDelta.Value > 0) {
            mainStr = string.Format("-{0:0.#}%", percentDelta.Value);
            subStr = currentRemaining.HasValue ? string.Format("QUOTA (剩{0:0.#}%)", currentRemaining.Value) : "QUOTA CONSUMED";
        } else {
            mainStr = "ACTIVE";
            subStr = "TOKENS APPLIED";
        }

        var big = CatDropOverlay.MakeTightPixelText(mainStr, mainFontSize, Island.Brush(mainCol), stroke, glowCol);
        big.HorizontalAlignment = HorizontalAlignment.Center;
        big.Margin = new Thickness(0, 0, 0, 3 * s);
        dispStack.Children.Add(big);

        var sub = CatDropOverlay.MakeTightPixelText(subStr, subFontSize, Island.Brush(subCol), stroke, null);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        dispStack.Children.Add(sub);

        // 像素遊戲跳出彈跳動態群組（位於貓咪下方）
        scaleTr = new ScaleTransform(0.3, 0.3);
        transTr = new TranslateTransform(0, 16 * s);
        var tg = new TransformGroup();
        tg.Children.Add(scaleTr);
        tg.Children.Add(transTr);

        floatText = new Border {
            Background = Brushes.Transparent,
            Child = dispStack,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, catH + 8 * s, 0, 0),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = tg
        };
        grid.Children.Add(floatText);

        Content = grid;

        timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += OnTick;
        timer.Start();
    }

    void OnTick(object sender, EventArgs e) {
        currentFrame++;
        if (currentFrame >= frames.Length) {
            timer.Stop();
            Close();
            return;
        }

        catImg.Source = frames[currentFrame];

        // 貓咪墜下到位（第 10 幀）瞬間觸發像素電玩動態跳字
        if (currentFrame >= 10 && !popped) {
            popped = true;
            if (SystemParameters.ClientAreaAnimation) {
                var backEase = new System.Windows.Media.Animation.BackEase { Amplitude = 0.55, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                var cubicEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

                scaleTr.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = backEase });
                scaleTr.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(300)) { EasingFunction = backEase });
                transTr.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(transTr.Y, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = cubicEase });
                floatText.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(180)));
            } else {
                scaleTr.ScaleX = 1.0;
                scaleTr.ScaleY = 1.0;
                transTr.Y = 0;
                floatText.Opacity = 1.0;
            }
        } else if (currentFrame > 80) {
            floatText.Opacity = Math.Max(0.0, floatText.Opacity - 0.1);
            Opacity = Math.Max(0.0, Opacity - 0.08);
        }
    }
}
}
