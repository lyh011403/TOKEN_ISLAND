using System;
using System.Windows;
using System.Windows.Media;

namespace TokenIsland {
    public static class NotchShape {
        /// <summary>
        /// 生成 iOS / macOS 原廠同款 Notch 凹角幾何路徑（含雙側 Inverted Fillet 耳朵）
        /// </summary>
        public static PathGeometry BuildGeometry(double contentWidth, double height, double earRadius = 14, double bottomRadius = 18) {
            if (contentWidth < 60) contentWidth = 60;
            if (height < 24) height = 24;

            double totalWidth = contentWidth + 2 * earRadius;

            // 確保圓角半徑不超過幾何尺寸
            double actualBottomR = Math.Min(bottomRadius, Math.Min(contentWidth / 2, height - earRadius));
            if (actualBottomR < 2) actualBottomR = 2;

            var figure = new PathFigure {
                StartPoint = new Point(0, 0),
                IsClosed = true,
                IsFilled = true
            };

            // 1. 左側凹圓角 (Inverted Arc): 從頂端 (0, 0) 平滑過渡到 Notch 左壁 (earRadius, earRadius)
            figure.Segments.Add(new ArcSegment(
                new Point(earRadius, earRadius),
                new Size(earRadius, earRadius),
                0,
                false,
                SweepDirection.Clockwise,
                true
            ));

            // 2. 左側垂直壁
            figure.Segments.Add(new LineSegment(
                new Point(earRadius, height - actualBottomR),
                true
            ));

            // 3. 左下凸圓角
            figure.Segments.Add(new ArcSegment(
                new Point(earRadius + actualBottomR, height),
                new Size(actualBottomR, actualBottomR),
                0,
                false,
                SweepDirection.Counterclockwise,
                true
            ));

            // 4. 底部水平線
            figure.Segments.Add(new LineSegment(
                new Point(earRadius + contentWidth - actualBottomR, height),
                true
            ));

            // 5. 右下凸圓角
            figure.Segments.Add(new ArcSegment(
                new Point(earRadius + contentWidth, height - actualBottomR),
                new Size(actualBottomR, actualBottomR),
                0,
                false,
                SweepDirection.Counterclockwise,
                true
            ));

            // 6. 右側垂直壁
            figure.Segments.Add(new LineSegment(
                new Point(earRadius + contentWidth, earRadius),
                true
            ));

            // 7. 右側凹圓角 (Inverted Arc): 從 Notch 右壁 (earRadius + contentWidth, earRadius) 平滑過渡到頂端 (totalWidth, 0)
            figure.Segments.Add(new ArcSegment(
                new Point(totalWidth, 0),
                new Size(earRadius, earRadius),
                0,
                false,
                SweepDirection.Clockwise,
                true
            ));

            // 8. 頂部封閉回到 (0, 0)
            figure.Segments.Add(new LineSegment(
                new Point(0, 0),
                true
            ));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}
