using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace TokenIsland {
    internal static class NotchUi {
        public static SolidColorBrush Brush(string hex) {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }

        // Apple 動態島同款彈性微縮按下（Spring Press）與觸碰回饋
        public static void ApplyTouchFeedback(FrameworkElement element, double pressedScale = 0.95, Action onClick = null) {
            if (element == null) return;
            ScaleTransform st = element.RenderTransform as ScaleTransform;
            if (st == null) {
                var tg = element.RenderTransform as TransformGroup;
                if (tg != null) {
                    for (int i = 0; i < tg.Children.Count; i++) {
                        if (tg.Children[i] is ScaleTransform) {
                            st = (ScaleTransform)tg.Children[i];
                            break;
                        }
                    }
                    if (st == null) {
                        st = new ScaleTransform(1.0, 1.0);
                        tg.Children.Add(st);
                    }
                } else {
                    st = new ScaleTransform(1.0, 1.0);
                    element.RenderTransform = st;
                }
            }
            element.RenderTransformOrigin = new Point(0.5, 0.5);

            element.PreviewMouseLeftButtonDown += delegate {
                var anim = new DoubleAnimation {
                    To = pressedScale,
                    Duration = TimeSpan.FromMilliseconds(70),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            };

            Action resetScale = delegate {
                var anim = new DoubleAnimation {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            };

            element.PreviewMouseLeftButtonUp += delegate {
                resetScale();
                if (onClick != null) onClick();
            };
            element.MouseLeave += delegate {
                resetScale();
            };
        }

        private static DateTime selectedDay = DateTime.Today;
        private static DateTime viewingMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        private static bool isNotebookMode = false;
        private static Action<bool> switchModeInternal;

        public static void SwitchToMode(bool notebookMode) {
            if (switchModeInternal != null) switchModeInternal(notebookMode);
        }

        // 建立左側「大字時間 · 完整日期星期 · 即時現實天氣預報卡片 · 日曆/筆記雙模卡片」面板
        public static UIElement BuildTimeCalendarView(Island owner, out Action updateTimeAction, out Action updateCalendarAction) {
            var grid = new Grid { Margin = new Thickness(10, 4, 10, 4) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 頂部時間日期與天氣預報卡片
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Google 日曆卡片

            // 1. 頂部區域：左側時間日期，右側微型天氣預報卡片（契合紅框處）
            var topHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            topHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 1.1 左側：即時時間與完整日期星期
            var clockDateBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var timeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var hourMinText = new TextBlock {
                Text = DateTime.Now.ToString("HH:mm"),
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF"),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Consolas")
            };
            var secText = new TextBlock {
                Text = DateTime.Now.ToString(":ss"),
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                Foreground = Brush("#6E758C"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 0, 3),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Consolas")
            };
            timeStack.Children.Add(hourMinText);
            timeStack.Children.Add(secText);
            clockDateBox.Children.Add(timeStack);

            var dateText = new TextBlock {
                Text = I18n.FormatDate(DateTime.Now),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#8E95AA"),
                Margin = new Thickness(0, 1, 0, 0)
            };
            clockDateBox.Children.Add(dateText);
            Grid.SetColumn(clockDateBox, 0);
            topHeaderGrid.Children.Add(clockDateBox);

            // 1.2 右側：即時現實天氣預報微型卡片（紅框處）
            var weatherCard = new Border {
                Background = Brush("#111420"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 110
            };

            // 邊框待機平滑呼吸微光（Ambient Glow Pulse）
            var borderGlowColor = new ColorAnimation(
                (Color)ColorConverter.ConvertFromString("#23283B"),
                (Color)ColorConverter.ConvertFromString("#343D5C"),
                TimeSpan.FromSeconds(2.8)
            ) {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#23283B"));
            weatherCard.BorderBrush = borderBrush;
            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderGlowColor);

            var weatherCardContent = new Grid();
            weatherCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            weatherCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 上排：圖示 + 天氣描述 + 即時氣溫
            var row0 = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var weatherIconBox = new Border {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconRotate = new RotateTransform(0, 7, 7);
            var iconTranslate = new TranslateTransform(0, 0);
            var iconTg = new TransformGroup();
            iconTg.Children.Add(iconRotate);
            iconTg.Children.Add(iconTranslate);
            weatherIconBox.RenderTransform = iconTg;

            var weatherCondText = new TextBlock {
                Text = I18n.GetWeatherCondition(WeatherManager.Instance.Current.ConditionCode),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#E2E8F0"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 66
            };

            var weatherTempText = new TextBlock {
                Text = WeatherManager.Instance.Current.TempNum,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#60A5FA"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Consolas")
            };

            Grid.SetColumn(weatherIconBox, 0);
            Grid.SetColumn(weatherCondText, 1);
            Grid.SetColumn(weatherTempText, 2);
            row0.Children.Add(weatherIconBox);
            row0.Children.Add(weatherCondText);
            row0.Children.Add(weatherTempText);
            Grid.SetRow(row0, 0);
            weatherCardContent.Children.Add(row0);

            // 下排：向量雨滴與降雨機率 + 高低溫預報範圍
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rainStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var dropIcon = new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M3.5,0.5 C3.5,0.5 1,3.8 1,5.2 C1,6.8 2.1,7.5 3.5,7.5 C4.9,7.5 6,6.8 6,5.2 C6,3.8 3.5,0.5 3.5,0.5 Z"),
                Fill = Brush("#38BDF8"),
                Width = 6.5,
                Height = 7.5,
                Margin = new Thickness(0, 0, 2.5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var weatherRainText = new TextBlock {
                Text = WeatherManager.Instance.Current.RainChance,
                FontSize = 9,
                FontWeight = FontWeights.Medium,
                Foreground = Brush("#38BDF8"),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, Consolas")
            };
            rainStack.Children.Add(dropIcon);
            rainStack.Children.Add(weatherRainText);

            var weatherRangeText = new TextBlock {
                Text = WeatherManager.Instance.Current.MaxTemp + "/" + WeatherManager.Instance.Current.MinTemp,
                FontSize = 9,
                FontWeight = FontWeights.Normal,
                Foreground = Brush("#8E95AA"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Segoe UI, Consolas")
            };

            Grid.SetColumn(rainStack, 0);
            Grid.SetColumn(weatherRangeText, 2);
            row1.Children.Add(rainStack);
            row1.Children.Add(weatherRangeText);
            Grid.SetRow(row1, 1);
            weatherCardContent.Children.Add(row1);

            weatherCard.Child = weatherCardContent;
            Grid.SetColumn(weatherCard, 1);
            topHeaderGrid.Children.Add(weatherCard);

            Grid.SetRow(topHeaderGrid, 0);
            grid.Children.Add(topHeaderGrid);

            // 根據當前真實天氣掛載專屬環境微動態
            Action applyIconMotion = delegate {
                string code = WeatherManager.Instance.Current.ConditionCode;
                iconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                iconTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                iconTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                if (code == "Sunny" || code == "Night") {
                    var swayAnim = new DoubleAnimation(-4, 4, TimeSpan.FromSeconds(2.8)) {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    iconRotate.BeginAnimation(RotateTransform.AngleProperty, swayAnim);
                } else if (code == "Rainy" || code == "Thunder") {
                    var dropAnim = new DoubleAnimation(0, 1.5, TimeSpan.FromSeconds(1.2)) {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    iconTranslate.BeginAnimation(TranslateTransform.YProperty, dropAnim);
                } else {
                    var floatAnim = new DoubleAnimation(-1.0, 1.0, TimeSpan.FromSeconds(3.2)) {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    iconTranslate.BeginAnimation(TranslateTransform.XProperty, floatAnim);
                }
            };

            // 天氣資料更新閉包
            Action updateWeatherCard = delegate {
                var w = WeatherManager.Instance.Current;
                string cond = I18n.GetWeatherCondition(w.ConditionCode);
                weatherCondText.Text = cond;
                weatherTempText.Text = w.TempNum;
                weatherRainText.Text = w.RainChance;
                weatherRangeText.Text = w.MaxTemp + "/" + w.MinTemp;
                weatherIconBox.Child = WeatherManager.CreateWeatherIcon(w.ConditionCode, 14);

                weatherCard.ToolTip = string.Format("{0} · {1}\n{2} {3} · {4} {5}\n{6} {7} · {8} {9}\n{10} {11}\n{12}",
                    w.City, cond,
                    I18n.T("Weather.FeelsLike"), w.FeelsLike,
                    I18n.T("Weather.Humidity"), w.Humidity,
                    I18n.T("Weather.MaxTemp"), w.MaxTemp,
                    I18n.T("Weather.MinTemp"), w.MinTemp,
                    I18n.T("Weather.RainChance"), w.RainChance,
                    I18n.T("Weather.ClickToSync"));

                // 淡入微動畫
                var fade = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(220)) {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                weatherCardContent.BeginAnimation(UIElement.OpacityProperty, fade);
                applyIconMotion();
            };

            updateWeatherCard();

            // 註冊天氣更新監聽
            WeatherManager.Instance.WeatherUpdated += delegate {
                grid.Dispatcher.BeginInvoke(new Action(updateWeatherCard));
            };

            // 滑鼠懸停互動
            weatherCard.MouseEnter += delegate {
                borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                weatherCard.BorderBrush = Brush("#3B82F6");
                weatherCard.Background = Brush("#151928");
            };
            weatherCard.MouseLeave += delegate {
                weatherCard.Background = Brush("#111420");
                borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderGlowColor);
            };

            // 點擊觸摸彈性回饋 + 360 度旋轉刷新
            ApplyTouchFeedback(weatherCard, 0.94, async delegate {
                var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(420)) {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                iconRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
                await WeatherManager.Instance.RefreshAsync(true);
            });

            // 3. 左下角雙模卡片：Google 日曆 ⇄ 隨手筆記本（含頂部釘選通知連動）
            var calendarBorder = new Border {
                Background = Brush("#11131A"),
                BorderBrush = Brush("#232736"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var calendarLayout = new Grid();
            calendarLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 頂部雙模切換與工具列
            calendarLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 內容視圖容器

            // 3.1 頂部欄：左側 [日曆 | 筆記] 分段切換器，右側模式專屬工具組
            var topBar = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 分段切換膠囊 [日曆 | 筆記]
            var modeSwitchBorder = new Border {
                Background = Brush("#0B0D13"),
                BorderBrush = Brush("#23283B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(1.5),
                VerticalAlignment = VerticalAlignment.Center
            };
            var modeSwitchStack = new StackPanel { Orientation = Orientation.Horizontal };

            var tabCalBorder = new Border {
                CornerRadius = new CornerRadius(4.5),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brush("#2563EB"),
                ToolTip = I18n.T("Agenda.CalendarTab")
            };
            var tabCalText = new TextBlock {
                Text = I18n.T("Agenda.CalendarTab"),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF")
            };
            tabCalBorder.Child = tabCalText;

            var tabNoteBorder = new Border {
                CornerRadius = new CornerRadius(4.5),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = I18n.T("Agenda.NoteTab")
            };
            var tabNoteText = new TextBlock {
                Text = I18n.T("Agenda.NoteTab"),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#8E95AA")
            };
            tabNoteBorder.Child = tabNoteText;

            ApplyTouchFeedback(tabCalBorder, 0.92);
            ApplyTouchFeedback(tabNoteBorder, 0.92);
            modeSwitchStack.Children.Add(tabCalBorder);
            modeSwitchStack.Children.Add(tabNoteBorder);
            modeSwitchBorder.Child = modeSwitchStack;
            Grid.SetColumn(modeSwitchBorder, 0);
            topBar.Children.Add(modeSwitchBorder);

            // 日曆模式中間：月份切換與同步狀態圓點
            var navStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            var prevWeekBtn = new TextBlock {
                Text = "◀",
                FontSize = 8.5,
                Foreground = Brush("#6E758C"),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = I18n.T("Calendar.PrevMonth")
            };
            var monthLabel = new TextBlock {
                Text = viewingMonth.ToString("yyyy/M"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#E2E8F0"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = I18n.T("Calendar.Today")
            };
            var nextWeekBtn = new TextBlock {
                Text = "▶",
                FontSize = 8.5,
                Foreground = Brush("#6E758C"),
                Cursor = Cursors.Hand,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = I18n.T("Calendar.NextMonth")
            };
            navStack.Children.Add(prevWeekBtn);
            navStack.Children.Add(monthLabel);
            navStack.Children.Add(nextWeekBtn);
            ApplyTouchFeedback(prevWeekBtn, 0.86);
            ApplyTouchFeedback(nextWeekBtn, 0.86);
            ApplyTouchFeedback(monthLabel, 0.95);

            string statusTxt = "未連結";
            Brush statusBrush = Brush("#F59E0B");
            if (AgendaManager.Instance.IsConnected) {
                if (AgendaManager.Instance.IsSyncSuccess) {
                    statusTxt = "●";
                    statusBrush = Brush("#34A853");
                } else {
                    statusTxt = "●";
                    statusBrush = Brush("#EF4444");
                }
            }

            var syncStatusTb = new TextBlock {
                Text = statusTxt,
                FontSize = 8.5,
                Foreground = statusBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = string.IsNullOrEmpty(AgendaManager.Instance.LastSyncStatus) ? "點擊配置 Google 日曆" : AgendaManager.Instance.LastSyncStatus,
                Cursor = Cursors.Hand
            };
            ApplyTouchFeedback(syncStatusTb, 0.94);
            syncStatusTb.MouseLeftButtonUp += delegate {
                ShowCalendarSetupDialog(owner);
            };
            navStack.Children.Add(syncStatusTb);
            Grid.SetColumn(navStack, 1);
            topBar.Children.Add(navStack);

            // 日曆模式右側工具按鈕組
            var btnGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var refreshBtn = new Border {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "重新整理日曆",
                Margin = new Thickness(0, 0, 2, 0)
            };
            refreshBtn.Child = CreateRefreshIcon(10.5);
            refreshBtn.MouseEnter += delegate { refreshBtn.Background = Brush("#232736"); };
            refreshBtn.MouseLeave += delegate { refreshBtn.Background = Brushes.Transparent; };
            ApplyTouchFeedback(refreshBtn, 0.86);
            btnGroup.Children.Add(refreshBtn);

            var cfgBtn = new Border {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "Google 日曆同步設定 / 匯入",
                Margin = new Thickness(0, 0, 2, 0)
            };
            cfgBtn.Child = CreateLinkIcon(11);
            cfgBtn.MouseEnter += delegate { cfgBtn.Background = Brush("#232736"); };
            cfgBtn.MouseLeave += delegate { cfgBtn.Background = Brushes.Transparent; };
            cfgBtn.MouseLeftButtonUp += delegate {
                ShowCalendarSetupDialog(owner);
            };
            ApplyTouchFeedback(cfgBtn, 0.86);
            btnGroup.Children.Add(cfgBtn);

            var extBtn = new Border {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "在瀏覽器中開啟 Google 日曆"
            };
            extBtn.Child = CreateExternalLinkIcon(10.5);
            extBtn.MouseEnter += delegate { extBtn.Background = Brush("#232736"); };
            extBtn.MouseLeave += delegate { extBtn.Background = Brushes.Transparent; };
            extBtn.MouseLeftButtonUp += delegate {
                AgendaManager.Instance.OpenGoogleCalendarDayWeb(selectedDay);
            };
            ApplyTouchFeedback(extBtn, 0.86);
            btnGroup.Children.Add(extBtn);

            Grid.SetColumn(btnGroup, 2);
            topBar.Children.Add(btnGroup);

            // 筆記模式專屬頂部工具列（釘選計數 + 新增筆記按鈕）
            var noteHeaderRight = new DockPanel {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var addNoteModalBtn = new Border {
                Background = Brush("#291D0E"),
                BorderBrush = Brush("#D97706"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1.5, 5, 1.5),
                Margin = new Thickness(4, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                ToolTip = I18n.T("Agenda.NewEvent")
            };
            addNoteModalBtn.Child = new TextBlock {
                Text = I18n.T("Agenda.AddQuickBtn"),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#FBBF24")
            };
            ApplyTouchFeedback(addNoteModalBtn, 0.90);
            addNoteModalBtn.MouseLeftButtonUp += delegate {
                ShowNoteEditDialog(owner, null);
            };
            DockPanel.SetDock(addNoteModalBtn, Dock.Right);
            noteHeaderRight.Children.Add(addNoteModalBtn);

            var pinSummaryTb = new TextBlock {
                Text = "◆ 0 釘選",
                FontSize = 8.5,
                Foreground = Brush("#FBBF24"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "釘選的筆記會直接顯示在動態島最上方的收合通知列"
            };
            noteHeaderRight.Children.Add(pinSummaryTb);

            Grid.SetColumn(noteHeaderRight, 1);
            Grid.SetColumnSpan(noteHeaderRight, 2);
            topBar.Children.Add(noteHeaderRight);

            Grid.SetRow(topBar, 0);
            calendarLayout.Children.Add(topBar);

            // 3.2 日曆滑動內容視窗（掛載 ClipToBounds 與邊界左右虛化漸層遮罩 OpacityMask）
            var slidingViewport = new Grid {
                ClipToBounds = true
            };

            // 左右兩側邊界柔和虛化漸層遮罩（Fade Gradient Mask，徹底防止內容生硬截斷或凸出卡片外側）
            var fadeMask = new LinearGradientBrush {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            fadeMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
            fadeMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.06));
            fadeMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.94));
            fadeMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
            slidingViewport.OpacityMask = fadeMask;

            var slidingContent = new Grid();
            slidingContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: 星期表頭 (一 ~ 日)
            slidingContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: 當月月曆網格 (7 欄 x 5~6 列)
            slidingContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 2: 當日日程事件清單

            var calTranslate = new TranslateTransform(0, 0);
            slidingContent.RenderTransform = calTranslate;

            // 星期表頭 (一 ~ 日)
            var weekHeaderGrid = new UniformGrid {
                Columns = 7,
                Margin = new Thickness(0, 0, 0, 2)
            };
            string[] weekHeaderNames = I18n.GetWeekHeaderNames();
            for (int wi = 0; wi < 7; wi++) {
                weekHeaderGrid.Children.Add(new TextBlock {
                    Text = weekHeaderNames[wi],
                    FontSize = 8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (wi >= 5) ? Brush("#475569") : Brush("#64748B"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            Grid.SetRow(weekHeaderGrid, 0);
            slidingContent.Children.Add(weekHeaderGrid);

            // 當月完整月曆網格（含事件小圓點標記與點擊聯動）
            var monthCalendarGrid = new UniformGrid {
                Columns = 7,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(monthCalendarGrid, 1);
            slidingContent.Children.Add(monthCalendarGrid);

            // 3.3 當日事件清單容器（4px 微型膠囊捲軸）
            var scrollViewer = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Template = CreateMicroScrollViewerTemplate(),
                Margin = new Thickness(0, 2, 0, 0)
            };
            var eventsStack = new StackPanel();
            scrollViewer.Content = eventsStack;
            Grid.SetRow(scrollViewer, 2);
            slidingContent.Children.Add(scrollViewer);

            slidingViewport.Children.Add(slidingContent);
            Grid.SetRow(slidingViewport, 1);
            calendarLayout.Children.Add(slidingViewport);

            // 3.2B 隨手筆記本視圖容器（快速輸入列 + 4px 微型膠囊捲軸筆記清單）
            var notebookViewport = new Grid {
                Visibility = Visibility.Collapsed,
                ClipToBounds = true
            };
            notebookViewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            notebookViewport.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 快速新增筆記輸入列
            var quickInputBorder = new Border {
                Background = Brush("#0C0E15"),
                BorderBrush = Brush("#262D42"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 3, 4, 3),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var quickInputGrid = new Grid();
            quickInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var quickPlaceholder = new TextBlock {
                Text = I18n.T("Agenda.QuickNotePlaceholder"),
                FontSize = 9.5,
                Foreground = Brush("#5A6278"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Margin = new Thickness(2, 0, 0, 0)
            };
            var quickTextBox = new TextBox {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush("#FFFFFF"),
                CaretBrush = Brush("#60A5FA"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0)
            };
            quickTextBox.TextChanged += delegate {
                quickPlaceholder.Visibility = string.IsNullOrEmpty(quickTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };

            var quickAddBtn = new Border {
                Background = Brush("#2563EB"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = "立即新增筆記並釘選至頂部通知列"
            };
            quickAddBtn.Child = new TextBlock {
                Text = "＋",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF")
            };
            ApplyTouchFeedback(quickAddBtn, 0.88);

            Action submitQuickNote = delegate {
                string raw = (quickTextBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(raw)) {
                    NoteManager.Instance.AddNote(raw, true, "#FBBF24");
                    quickTextBox.Text = "";
                }
            };
            quickAddBtn.MouseLeftButtonUp += delegate { submitQuickNote(); };
            quickTextBox.KeyDown += delegate(object s, KeyEventArgs eArgs) {
                if (eArgs.Key == Key.Enter) {
                    submitQuickNote();
                    eArgs.Handled = true;
                }
            };

            Grid.SetColumn(quickPlaceholder, 0);
            Grid.SetColumn(quickTextBox, 0);
            Grid.SetColumn(quickAddBtn, 1);
            quickInputGrid.Children.Add(quickPlaceholder);
            quickInputGrid.Children.Add(quickTextBox);
            quickInputGrid.Children.Add(quickAddBtn);
            quickInputBorder.Child = quickInputGrid;
            Grid.SetRow(quickInputBorder, 0);
            notebookViewport.Children.Add(quickInputBorder);

            var notesScrollViewer = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Template = CreateMicroScrollViewerTemplate(),
                Margin = new Thickness(0, 1, 0, 0)
            };
            var notesStack = new StackPanel();
            notesScrollViewer.Content = notesStack;
            Grid.SetRow(notesScrollViewer, 1);
            notebookViewport.Children.Add(notesScrollViewer);

            Grid.SetRow(notebookViewport, 1);
            calendarLayout.Children.Add(notebookViewport);

            calendarBorder.ClipToBounds = true;
            calendarBorder.Child = calendarLayout;
            Grid.SetRow(calendarBorder, 1);
            grid.Children.Add(calendarBorder);

            // 當日日程事件重繪
            Action renderSelectedDayEvents = delegate {
                eventsStack.Children.Clear();
                var items = AgendaManager.Instance.GetEventsForDate(selectedDay);

                // 當日小標題
                var daySummary = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                daySummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                daySummary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dayTb = new TextBlock {
                    Text = string.Format("{0} · {1}", selectedDay.ToString("M/d (ddd)", I18n.CurrentCulture), I18n.T("Agenda.EventCount", items.Count)),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#BAC4D8")
                };
                Grid.SetColumn(dayTb, 0);
                daySummary.Children.Add(dayTb);

                var addQuickBtn = new TextBlock {
                    Text = I18n.T("Agenda.AddQuickBtn"),
                    FontSize = 9,
                    Foreground = Brush("#4285F4"),
                    Cursor = Cursors.Hand
                };
                ApplyTouchFeedback(addQuickBtn, 0.90);
                addQuickBtn.MouseLeftButtonUp += delegate {
                    ShowAddEventDialog(owner, selectedDay);
                };
                Grid.SetColumn(addQuickBtn, 1);
                daySummary.Children.Add(addQuickBtn);
                eventsStack.Children.Add(daySummary);

                if (items.Count == 0) {
                    var emptyBox = new Border {
                        Background = Brush("#141722"),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(6, 6, 6, 6),
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    var emptyText = new TextBlock {
                        Text = I18n.T("Agenda.NoEventsNotice"),
                        FontSize = 9.5,
                        Foreground = Brush("#6E758C"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand
                    };
                    ApplyTouchFeedback(emptyText, 0.96);
                    emptyText.MouseLeftButtonUp += delegate {
                        AgendaManager.Instance.OpenGoogleCalendarDayWeb(selectedDay);
                    };
                    emptyBox.Child = emptyText;
                    eventsStack.Children.Add(emptyBox);
                } else {
                    for (int i = 0; i < items.Count; i++) {
                        var ev = items[i];
                        var itemBorder = new Border {
                            Background = Brush("#151822"),
                            BorderBrush = Brush("#1E2333"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5),
                            Margin = new Thickness(0, 0, 0, 3),
                            Padding = new Thickness(5, 3, 5, 3),
                            Cursor = Cursors.Hand,
                            ToolTip = ev.IsCustom ? string.Format("本地排定日程：{0}\n時間：{1}\n可點擊右側 ✕ 刪除", ev.Title, ev.GetTimeDisplay()) : string.Format("Google 日曆事件：{0}\n時間：{1}\n點擊在 Google 日曆中檢視", ev.Title, ev.GetTimeDisplay())
                        };

                        var itemGrid = new Grid();
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        if (ev.IsCustom) {
                            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        }

                        var colorBar = new Border {
                            Background = Brush(string.IsNullOrEmpty(ev.ColorHex) ? "#4285F4" : ev.ColorHex),
                            CornerRadius = new CornerRadius(1.5),
                            VerticalAlignment = VerticalAlignment.Stretch
                        };
                        Grid.SetColumn(colorBar, 0);
                        itemGrid.Children.Add(colorBar);

                        var detailsStack = new StackPanel { Margin = new Thickness(5, 0, 0, 0) };

                        var metaGrid = new Grid();
                        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var timeText = new TextBlock {
                            Text = ev.GetTimeDisplay(),
                            FontSize = 9,
                            Foreground = Brush("#8E95AA"),
                            FontFamily = new FontFamily("Segoe UI, Consolas")
                        };
                        Grid.SetColumn(timeText, 0);
                        metaGrid.Children.Add(timeText);

                        string relStatus = ev.GetRelativeStatus();
                        if (ev.IsCustom) {
                            relStatus = "✦ " + relStatus;
                        }
                        var relText = new TextBlock {
                            Text = relStatus,
                            FontSize = 8.5,
                            Foreground = ev.IsCustom ? Brush("#38BDF8") : Brush("#4285F4"),
                            FontWeight = FontWeights.SemiBold
                        };
                        Grid.SetColumn(relText, 1);
                        metaGrid.Children.Add(relText);
                        detailsStack.Children.Add(metaGrid);

                        var titleTb = new TextBlock {
                            Text = ev.Title,
                            FontSize = 10.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brush("#FFFFFF"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        detailsStack.Children.Add(titleTb);

                        Grid.SetColumn(detailsStack, 1);
                        itemGrid.Children.Add(detailsStack);

                        if (ev.IsCustom) {
                            var delBtn = new Border {
                                Background = Brushes.Transparent,
                                Padding = new Thickness(5, 2, 4, 2),
                                VerticalAlignment = VerticalAlignment.Center,
                                Cursor = Cursors.Hand,
                                ToolTip = "刪除此本地排定日程"
                            };
                            var delText = new TextBlock {
                                Text = "✕",
                                FontSize = 9.5,
                                Foreground = Brush("#64748B"),
                                FontWeight = FontWeights.Bold
                            };
                            delBtn.Child = delText;
                            delBtn.MouseEnter += delegate {
                                delText.Foreground = Brush("#EF4444");
                            };
                            delBtn.MouseLeave += delegate {
                                delText.Foreground = Brush("#64748B");
                            };
                            string capturedId = ev.Id;
                            delBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs eArgs) {
                                eArgs.Handled = true;
                                AgendaManager.Instance.DeleteCustomEvent(capturedId);
                            };
                            Grid.SetColumn(delBtn, 2);
                            itemGrid.Children.Add(delBtn);
                        }

                        itemBorder.Child = itemGrid;

                        itemBorder.MouseEnter += delegate {
                            itemBorder.Background = Brush("#1B2232");
                            itemBorder.BorderBrush = Brush("#3B82F6");
                        };
                        itemBorder.MouseLeave += delegate {
                            itemBorder.Background = Brush("#151822");
                            itemBorder.BorderBrush = Brush("#1E2333");
                        };
                        ApplyTouchFeedback(itemBorder, 0.98);
                        itemBorder.MouseLeftButtonUp += delegate {
                            if (!ev.IsCustom) {
                                AgendaManager.Instance.OpenGoogleCalendarDayWeb(ev.StartTime);
                            }
                        };

                        eventsStack.Children.Add(itemBorder);
                    }
                }
            };

            // 重繪整月月曆與事件小圓點標記
            Action renderMonthCalendar = null;
            renderMonthCalendar = delegate {
                monthCalendarGrid.Children.Clear();
                monthLabel.Text = viewingMonth.ToString("yyyy/M");
                if (AgendaManager.Instance.IsConnected) {
                    if (AgendaManager.Instance.IsSyncSuccess) {
                        syncStatusTb.Text = "●";
                        syncStatusTb.Foreground = Brush("#34A853");
                    } else {
                        syncStatusTb.Text = "●";
                        syncStatusTb.Foreground = Brush("#EF4444");
                    }
                } else {
                    syncStatusTb.Text = "●";
                    syncStatusTb.Foreground = Brush("#F59E0B");
                }
                syncStatusTb.ToolTip = string.IsNullOrEmpty(AgendaManager.Instance.LastSyncStatus) ? "點擊配置 Google 日曆" : AgendaManager.Instance.LastSyncStatus;

                DateTime firstDay = new DateTime(viewingMonth.Year, viewingMonth.Month, 1);
                int daysInMonth = DateTime.DaysInMonth(viewingMonth.Year, viewingMonth.Month);
                int offset = ((int)firstDay.DayOfWeek + 6) % 7; // 週一為 0
                DateTime gridStart = firstDay.AddDays(-offset);
                int totalCells = ((offset + daysInMonth + 6) / 7) * 7;

                for (int d = 0; d < totalCells; d++) {
                    DateTime date = gridStart.AddDays(d);
                    bool isCurrentMonth = (date.Month == viewingMonth.Month);
                    bool isToday = (date.Date == DateTime.Today);
                    bool isSelected = (date.Date == selectedDay.Date);
                    bool hasEvents = AgendaManager.Instance.HasEventsOnDate(date);

                    var dayCell = new Border {
                        Height = 18.5,
                        CornerRadius = new CornerRadius(3.5),
                        Background = isSelected ? Brush("#2563EB") : (isToday ? Brush("#1A2436") : Brushes.Transparent),
                        BorderBrush = isSelected ? Brush("#60A5FA") : (isToday ? Brush("#334D7A") : Brushes.Transparent),
                        BorderThickness = new Thickness(isSelected || isToday ? 1 : 0),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0.5)
                    };

                    var cellGrid = new Grid();
                    cellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    cellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var numText = new TextBlock {
                        Text = date.Day.ToString(),
                        FontSize = 9.5,
                        FontWeight = isSelected ? FontWeights.Bold : (isToday ? FontWeights.SemiBold : FontWeights.Normal),
                        Foreground = isSelected ? Brush("#FFFFFF") : (!isCurrentMonth ? Brush("#333D52") : (isToday ? Brush("#60A5FA") : Brush("#C4D2DB"))),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(numText, 0);
                    cellGrid.Children.Add(numText);

                    if (hasEvents) {
                        var dot = new Border {
                            Width = 2.5,
                            Height = 2.5,
                            CornerRadius = new CornerRadius(1.25),
                            Background = isSelected ? Brush("#FFFFFF") : Brush("#38BDF8"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, 1)
                        };
                        Grid.SetRow(dot, 1);
                        cellGrid.Children.Add(dot);
                    }

                    dayCell.Child = cellGrid;

                    dayCell.MouseEnter += delegate {
                        if (!isSelected) {
                            dayCell.Background = Brush("#1E273A");
                            dayCell.BorderBrush = Brush("#3B82F6");
                            dayCell.BorderThickness = new Thickness(1);
                        }
                    };
                    dayCell.MouseLeave += delegate {
                        if (!isSelected) {
                            dayCell.Background = isToday ? Brush("#1A2436") : Brushes.Transparent;
                            dayCell.BorderBrush = isToday ? Brush("#334D7A") : Brushes.Transparent;
                            dayCell.BorderThickness = new Thickness(isToday ? 1 : 0);
                        }
                    };
                    ApplyTouchFeedback(dayCell, 0.92);

                    DateTime capturedDate = date;
                    dayCell.MouseLeftButtonUp += delegate {
                        selectedDay = capturedDate;
                        if (capturedDate.Month != viewingMonth.Month || capturedDate.Year != viewingMonth.Year) {
                            viewingMonth = new DateTime(capturedDate.Year, capturedDate.Month, 1);
                        }
                        renderMonthCalendar();
                        renderSelectedDayEvents();
                    };

                    monthCalendarGrid.Children.Add(dayCell);
                }
            };

            // 3.34 筆記清單即時拖曳重排（Drag & Drop Reordering）狀態與事件綁定
            Border activeDragCard = null;
            NoteItem activeDragNote = null;
            Point dragStartPoint = default(Point);
            bool isDraggingActive = false;

            notesStack.PreviewMouseMove += delegate(object s, MouseEventArgs evArgs) {
                if (evArgs.LeftButton != MouseButtonState.Pressed || activeDragCard == null) {
                    return;
                }
                Point currentPos = evArgs.GetPosition(notesScrollViewer);
                if (!isDraggingActive) {
                    double diffX = Math.Abs(currentPos.X - dragStartPoint.X);
                    double diffY = Math.Abs(currentPos.Y - dragStartPoint.Y);
                    if (diffY > 4 || diffX > 4) {
                        isDraggingActive = true;
                        notesStack.CaptureMouse();
                        Mouse.OverrideCursor = Cursors.SizeAll;
                        activeDragCard.Opacity = 0.6;
                        activeDragCard.BorderBrush = Brush("#F59E0B");
                        activeDragCard.BorderThickness = new Thickness(1.5);
                    }
                }
                if (isDraggingActive) {
                    // 邊界自動平滑滾動
                    if (currentPos.Y < 25) {
                        notesScrollViewer.ScrollToVerticalOffset(notesScrollViewer.VerticalOffset - 8);
                    } else if (currentPos.Y > notesScrollViewer.ActualHeight - 25) {
                        notesScrollViewer.ScrollToVerticalOffset(notesScrollViewer.VerticalOffset + 8);
                    }

                    // 即時重排
                    Point stackPos = evArgs.GetPosition(notesStack);
                    int curIdx = notesStack.Children.IndexOf(activeDragCard);
                    int targetIdx = -1;
                    double curY = stackPos.Y;

                    for (int k = 0; k < notesStack.Children.Count; k++) {
                        var child = notesStack.Children[k] as FrameworkElement;
                        if (child == null || child == activeDragCard) continue;
                        try {
                            var transform = child.TransformToAncestor(notesStack);
                            Point childOrigin = transform.Transform(new Point(0, 0));
                            double childMidY = childOrigin.Y + (child.ActualHeight / 2.0);
                            if (curY < childMidY) {
                                targetIdx = k;
                                break;
                            }
                        } catch { }
                    }
                    if (targetIdx == -1) targetIdx = notesStack.Children.Count - 1;
                    if (targetIdx != curIdx && targetIdx >= 0 && targetIdx < notesStack.Children.Count) {
                        notesStack.Children.Remove(activeDragCard);
                        notesStack.Children.Insert(targetIdx, activeDragCard);
                    }
                }
            };

            notesStack.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                if (isDraggingActive && activeDragCard != null) {
                    evArgs.Handled = true;
                    notesStack.ReleaseMouseCapture();
                    Mouse.OverrideCursor = null;

                    var orderedIds = new List<string>();
                    for (int k = 0; k < notesStack.Children.Count; k++) {
                        var b = notesStack.Children[k] as Border;
                        if (b != null) {
                            string noteId = b.Tag as string;
                            if (!string.IsNullOrEmpty(noteId)) {
                                orderedIds.Add(noteId);
                            }
                        }
                    }

                    bool isPin = activeDragNote != null && activeDragNote.IsPinned;
                    activeDragCard.Opacity = 1.0;
                    activeDragCard.BorderBrush = isPin ? Brush("#B45309") : Brush("#1E2333");
                    activeDragCard.BorderThickness = new Thickness(1);

                    activeDragCard = null;
                    activeDragNote = null;
                    isDraggingActive = false;

                    NoteManager.Instance.ReorderNotes(orderedIds);
                } else {
                    activeDragCard = null;
                    activeDragNote = null;
                    isDraggingActive = false;
                    Mouse.OverrideCursor = null;
                }
            };

            // 3.35 渲染隨手筆記本清單與頂部釘選控制
            Action renderNotesList = null;
            renderNotesList = delegate {
                notesStack.Children.Clear();
                var allNotes = NoteManager.Instance.GetAllNotes();
                int pinnedCount = 0;
                for (int i = 0; i < allNotes.Count; i++) {
                    if (allNotes[i].IsPinned) pinnedCount++;
                }

                if (pinnedCount > 0) {
                    pinSummaryTb.Text = string.Format("已釘選 {0} 則至頂部膠囊 (共 {1} 則) · 可拖曳排序", pinnedCount, allNotes.Count);
                    pinSummaryTb.Foreground = Brush("#FBBF24");
                } else {
                    pinSummaryTb.Text = string.Format("共 {0} 則筆記 · 點擊 [釘選] 顯示於頂欄 · 可拖曳排序", allNotes.Count);
                    pinSummaryTb.Foreground = Brush("#94A3B8");
                }

                if (allNotes.Count == 0) {
                    var emptyBox = new Border {
                        Background = Brush("#141722"),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(6, 8, 6, 8),
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    var emptyText = new TextBlock {
                        Text = "尚無筆記 · 於上方輸入框輸入或點 [+ 新增筆記]",
                        FontSize = 9.5,
                        Foreground = Brush("#6E758C"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand
                    };
                    emptyText.MouseLeftButtonUp += delegate {
                        ShowNoteEditDialog(owner, null);
                    };
                    emptyBox.Child = emptyText;
                    notesStack.Children.Add(emptyBox);
                } else {
                    for (int i = 0; i < allNotes.Count; i++) {
                        NoteItem note = allNotes[i];
                        bool isPinned = note.IsPinned;

                        var cardBorder = new Border {
                            Background = isPinned ? Brush("#1E1B16") : Brush("#151822"),
                            BorderBrush = isPinned ? Brush("#B45309") : Brush("#1E2333"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5),
                            Margin = new Thickness(0, 0, 0, 3),
                            Padding = new Thickness(5, 3.5, 5, 3.5),
                            Cursor = Cursors.Hand,
                            ToolTip = "點擊文字可修改筆記內容\n點擊左側 [釘選] 可切換是否在頂欄顯示\n按住可拖曳調整順序 (排在最上方為頂欄置頂)",
                            Tag = note.Id
                        };

                        var rowGrid = new Grid();
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var dragGrip = new TextBlock {
                            Text = "⠿",
                            FontSize = 10.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brush(isPinned ? "#B45309" : "#4A5568"),
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(1, 0, 4, 0),
                            Cursor = Cursors.SizeAll,
                            ToolTip = "按住拖曳可調整順序 (排在最上方為頂欄置頂)"
                        };
                        Grid.SetColumn(dragGrip, 0);
                        rowGrid.Children.Add(dragGrip);

                        var accentBar = new Border {
                            Background = Brush(isPinned ? "#F59E0B" : (string.IsNullOrEmpty(note.ColorHex) ? "#64748B" : note.ColorHex)),
                            CornerRadius = new CornerRadius(1.5),
                            VerticalAlignment = VerticalAlignment.Stretch
                        };
                        Grid.SetColumn(accentBar, 1);
                        rowGrid.Children.Add(accentBar);

                        // 釘選切換膠囊按鈕
                        var pinBtn = new Border {
                            Background = isPinned ? Brush("#45270A") : Brush("#1A202E"),
                            BorderBrush = isPinned ? Brush("#F59E0B") : Brush("#2E364A"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(3.5),
                            Padding = new Thickness(4, 1.5, 4, 1.5),
                            Margin = new Thickness(5, 0, 5, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Cursor = Cursors.Hand,
                            ToolTip = isPinned ? "已釘選於頂部通知條 (點擊取消釘選)" : "釘選至頂部通知條顯示"
                        };
                        var pinBtnText = new TextBlock {
                            Text = isPinned ? "頂欄釘選" : "釘選",
                            FontSize = 8.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = isPinned ? Brush("#FBBF24") : Brush("#8E95AA")
                        };
                        pinBtn.Child = pinBtnText;
                        ApplyTouchFeedback(pinBtn, 0.90);
                        NoteItem capturedForPin = note;
                        pinBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                            evArgs.Handled = true;
                            NoteManager.Instance.TogglePin(capturedForPin.Id);
                        };
                        Grid.SetColumn(pinBtn, 2);
                        rowGrid.Children.Add(pinBtn);

                        // 筆記內容與更新時間（點擊可修改）
                        var contentStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        var noteTitleTb = new TextBlock {
                            Text = note.Content,
                            FontSize = 10.5,
                            FontWeight = isPinned ? FontWeights.Bold : FontWeights.SemiBold,
                            Foreground = isPinned ? Brush("#FEF3C7") : Brush("#FFFFFF"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        contentStack.Children.Add(noteTitleTb);

                        var timeMetaTb = new TextBlock {
                            Text = note.UpdatedAt.ToString("MM/dd HH:mm") + " · 點擊編輯",
                            FontSize = 8,
                            Foreground = isPinned ? Brush("#D97706") : Brush("#64748B")
                        };
                        contentStack.Children.Add(timeMetaTb);
                        Grid.SetColumn(contentStack, 3);
                        rowGrid.Children.Add(contentStack);

                        // 刪除按鈕
                        var delNoteBtn = new Border {
                            Background = Brushes.Transparent,
                            Padding = new Thickness(5, 2, 3, 2),
                            VerticalAlignment = VerticalAlignment.Center,
                            Cursor = Cursors.Hand,
                            ToolTip = "刪除此筆記"
                        };
                        var delNoteText = new TextBlock {
                            Text = "✕",
                            FontSize = 9.5,
                            Foreground = Brush("#64748B"),
                            FontWeight = FontWeights.Bold
                        };
                        delNoteBtn.Child = delNoteText;
                        delNoteBtn.MouseEnter += delegate { delNoteText.Foreground = Brush("#EF4444"); };
                        delNoteBtn.MouseLeave += delegate { delNoteText.Foreground = Brush("#64748B"); };
                        NoteItem capturedForDel = note;
                        delNoteBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                            evArgs.Handled = true;
                            NoteManager.Instance.DeleteNote(capturedForDel.Id);
                        };
                        Grid.SetColumn(delNoteBtn, 4);
                        rowGrid.Children.Add(delNoteBtn);

                        cardBorder.Child = rowGrid;

                        // 拖曳起始偵測
                        cardBorder.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs evArgs) {
                            var src = evArgs.OriginalSource as DependencyObject;
                            if (src != null) {
                                try {
                                    if (pinBtn.IsAncestorOf(src) || delNoteBtn.IsAncestorOf(src)) return;
                                } catch { }
                            }
                            activeDragCard = cardBorder;
                            activeDragNote = note;
                            dragStartPoint = evArgs.GetPosition(notesScrollViewer);
                            isDraggingActive = false;
                        };

                        cardBorder.MouseEnter += delegate {
                            cardBorder.Background = isPinned ? Brush("#292318") : Brush("#1E2436");
                            dragGrip.Foreground = Brush(isPinned ? "#F59E0B" : "#94A3B8");
                        };
                        cardBorder.MouseLeave += delegate {
                            cardBorder.Background = isPinned ? Brush("#1E1B16") : Brush("#151822");
                            dragGrip.Foreground = Brush(isPinned ? "#B45309" : "#4A5568");
                        };

                        NoteItem capturedForEdit = note;
                        cardBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                            if (isDraggingActive) return;
                            ShowNoteEditDialog(owner, capturedForEdit);
                        };

                        notesStack.Children.Add(cardBorder);
                    }
                }
            };

            // 雙模式切換核心邏輯
            switchModeInternal = delegate(bool toNotebook) {
                isNotebookMode = toNotebook;
                if (toNotebook) {
                    tabCalBorder.Background = Brushes.Transparent;
                    tabCalText.Foreground = Brush("#8E95AA");
                    tabNoteBorder.Background = Brush("#B45309");
                    tabNoteText.Foreground = Brush("#FFFFFF");

                    navStack.Visibility = Visibility.Collapsed;
                    btnGroup.Visibility = Visibility.Collapsed;
                    noteHeaderRight.Visibility = Visibility.Visible;
                    slidingViewport.Visibility = Visibility.Collapsed;
                    notebookViewport.Visibility = Visibility.Visible;
                    calendarBorder.BorderBrush = Brush("#45270A");
                    renderNotesList();
                } else {
                    tabCalBorder.Background = Brush("#2563EB");
                    tabCalText.Foreground = Brush("#FFFFFF");
                    tabNoteBorder.Background = Brushes.Transparent;
                    tabNoteText.Foreground = Brush("#8E95AA");

                    navStack.Visibility = Visibility.Visible;
                    btnGroup.Visibility = Visibility.Visible;
                    noteHeaderRight.Visibility = Visibility.Collapsed;
                    slidingViewport.Visibility = Visibility.Visible;
                    notebookViewport.Visibility = Visibility.Collapsed;
                    calendarBorder.BorderBrush = Brush("#1D2130");
                    renderMonthCalendar();
                    renderSelectedDayEvents();
                }
            };

            tabCalBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                evArgs.Handled = true;
                switchModeInternal(false);
            };
            tabNoteBorder.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs evArgs) {
                evArgs.Handled = true;
                switchModeInternal(true);
            };

            // 月份快速切換與回到今天
            prevWeekBtn.MouseLeftButtonUp += delegate {
                viewingMonth = viewingMonth.AddMonths(-1);
                renderMonthCalendar();
            };
            nextWeekBtn.MouseLeftButtonUp += delegate {
                viewingMonth = viewingMonth.AddMonths(1);
                renderMonthCalendar();
            };
            monthLabel.MouseLeftButtonUp += delegate {
                viewingMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                selectedDay = DateTime.Today;
                renderMonthCalendar();
                renderSelectedDayEvents();
            };
            refreshBtn.MouseLeftButtonUp += delegate {
                AgendaManager.Instance.RefreshAsync();
            };

            // 支援滑鼠按住左右拉滑切換日期 / 週（僅在日曆模式啟用）
            Point swipeStart = new Point();
            bool isSwipeActive = false;
            bool swipeMoved = false;

            calendarBorder.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (isNotebookMode) return;
                var el = e.OriginalSource as FrameworkElement;
                if (el != null && (el is TextBox || el is Button)) return;

                swipeStart = e.GetPosition(calendarBorder);
                isSwipeActive = true;
                swipeMoved = false;
            };

            calendarBorder.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (isNotebookMode) return;
                if (!isSwipeActive || e.LeftButton != MouseButtonState.Pressed) {
                    if (isSwipeActive) {
                        isSwipeActive = false;
                        calTranslate.X = 0;
                    }
                    return;
                }
                Point current = e.GetPosition(calendarBorder);
                double deltaX = current.X - swipeStart.X;
                double deltaY = current.Y - swipeStart.Y;

                if (Math.Abs(deltaX) > 6 && Math.Abs(deltaX) > Math.Abs(deltaY)) {
                    swipeMoved = true;
                    double maxOffset = 26.0;
                    double sign = Math.Sign(deltaX);
                    double absX = Math.Abs(deltaX);
                    calTranslate.X = sign * Math.Min(maxOffset, Math.Sqrt(absX) * 3.4);
                }
            };

            calendarBorder.MouseLeave += delegate {
                if (isSwipeActive) {
                    isSwipeActive = false;
                    var backAnim = new DoubleAnimation {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(160),
                        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                    };
                    calTranslate.BeginAnimation(TranslateTransform.XProperty, backAnim);
                }
            };

            calendarBorder.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                if (isNotebookMode) return;
                if (isSwipeActive) {
                    isSwipeActive = false;
                    Point end = e.GetPosition(calendarBorder);
                    double deltaX = end.X - swipeStart.X;
                    double deltaY = end.Y - swipeStart.Y;

                    if (swipeMoved && Math.Abs(deltaX) >= 28 && Math.Abs(deltaX) > Math.Abs(deltaY)) {
                        e.Handled = true;
                        if (deltaX < -28) {
                            viewingMonth = viewingMonth.AddMonths(1);
                            renderMonthCalendar();
                        } else if (deltaX > 28) {
                            viewingMonth = viewingMonth.AddMonths(-1);
                            renderMonthCalendar();
                        }
                    } else {
                        var backAnim = new DoubleAnimation {
                            To = 0,
                            Duration = TimeSpan.FromMilliseconds(160),
                            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                        };
                        calTranslate.BeginAnimation(TranslateTransform.XProperty, backAnim);
                    }
                }
            };

            // 滑鼠滾輪切換月份（僅在非事件捲軸區滾動時生效）
            calendarBorder.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                if (isNotebookMode) return;
                bool inScrollList = scrollViewer.IsMouseOver && scrollViewer.ScrollableHeight > 0;
                if (!inScrollList || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) {
                    e.Handled = true;
                    if (e.Delta < 0) {
                        viewingMonth = viewingMonth.AddMonths(1);
                        renderMonthCalendar();
                    } else if (e.Delta > 0) {
                        viewingMonth = viewingMonth.AddMonths(-1);
                        renderMonthCalendar();
                    }
                }
            };

            AgendaManager.Instance.EventsUpdated += delegate {
                owner.Dispatcher.BeginInvoke(new Action(delegate {
                    renderMonthCalendar();
                    renderSelectedDayEvents();
                }));
            };

            NoteManager.Instance.NotesUpdated += delegate {
                owner.Dispatcher.BeginInvoke(new Action(delegate {
                    renderNotesList();
                }));
            };

            renderMonthCalendar();
            renderSelectedDayEvents();
            renderNotesList();

            updateTimeAction = delegate {
                hourMinText.Text = DateTime.Now.ToString("HH:mm");
                secText.Text = DateTime.Now.ToString(":ss");
                dateText.Text = I18n.FormatDate(DateTime.Now);
            };

            updateCalendarAction = delegate {
                renderMonthCalendar();
                renderSelectedDayEvents();
            };

            return grid;
        }

        public static void ShowAddEventDialog(Window parent, DateTime targetDate) {
            var dialog = new Window {
                Title = "新增日程",
                Width = 350,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true
            };

            var mainBorder = new Border {
                Background = Brush("#141722"),
                BorderBrush = Brush("#2B3247"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock {
                Text = string.Format("新增日程 · {0}", targetDate.ToString("M月d日 (ddd)", CultureInfo.GetCultureInfo("zh-TW"))),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF")
            };
            Grid.SetColumn(titleTb, 0);
            header.Children.Add(titleTb);

            var closeBtn = new TextBlock {
                Text = "✕",
                FontSize = 13,
                Foreground = Brush("#8E95AA"),
                Cursor = Cursors.Hand
            };
            closeBtn.MouseLeftButtonUp += delegate { dialog.Close(); };
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
            Grid.SetRow(header, 0);
            rootGrid.Children.Add(header);

            var txtTitle = new TextBox {
                Text = "重要工作排定",
                FontSize = 11.5,
                Foreground = Brush("#FFFFFF"),
                Background = Brush("#0D0F16"),
                BorderBrush = Brush("#343A4E"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 10, 0, 8)
            };
            Grid.SetRow(txtTitle, 1);
            rootGrid.Children.Add(txtTitle);

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var txtStart = new TextBox {
                Text = "14:00",
                Width = 55,
                FontSize = 11,
                Foreground = Brush("#FFFFFF"),
                Background = Brush("#0D0F16"),
                BorderBrush = Brush("#343A4E"),
                Padding = new Thickness(4)
            };
            var txtTo = new TextBlock { Text = " 至 ", Foreground = Brush("#8E95AA"), VerticalAlignment = VerticalAlignment.Center };
            var txtEnd = new TextBox {
                Text = "15:00",
                Width = 55,
                FontSize = 11,
                Foreground = Brush("#FFFFFF"),
                Background = Brush("#0D0F16"),
                BorderBrush = Brush("#343A4E"),
                Padding = new Thickness(4)
            };
            timeRow.Children.Add(txtStart);
            timeRow.Children.Add(txtTo);
            timeRow.Children.Add(txtEnd);
            Grid.SetRow(timeRow, 2);
            rootGrid.Children.Add(timeRow);

            var tipTb = new TextBlock {
                Text = "說明：排定至動態島將立即顯示並保存；線上建立可直接在 Google 雲端儲存。",
                FontSize = 9.5,
                Foreground = Brush("#6E758C"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(tipTb, 3);
            rootGrid.Children.Add(tipTb);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };

            var googleWebBtn = new Border {
                Background = Brush("#1D2536"),
                BorderBrush = Brush("#3B82F6"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = "直接在 Google 日曆線上建立，雲端立即保存"
            };
            googleWebBtn.Child = new TextBlock { Text = "在 Google 線上建立 ↗", Foreground = Brush("#60A5FA"), FontWeight = FontWeights.SemiBold, FontSize = 10.5 };
            googleWebBtn.MouseLeftButtonUp += delegate {
                AgendaManager.Instance.OpenGoogleCalendarCreateEventWeb(targetDate, txtTitle.Text, txtStart.Text, txtEnd.Text);
                dialog.Close();
            };
            btnStack.Children.Add(googleWebBtn);

            var saveLocalBtn = new Border {
                Background = Brush("#2563EB"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                ToolTip = "在動態島立即排定顯示並永久保存"
            };
            saveLocalBtn.Child = new TextBlock { Text = "＋排定至動態島", Foreground = Brush("#FFFFFF"), FontWeight = FontWeights.Bold, FontSize = 10.5 };
            saveLocalBtn.MouseLeftButtonUp += delegate {
                TimeSpan st = TimeSpan.FromHours(14), et = TimeSpan.FromHours(15);
                TimeSpan.TryParse(txtStart.Text, out st);
                TimeSpan.TryParse(txtEnd.Text, out et);
                DateTime start = targetDate.Date.Add(st);
                DateTime end = targetDate.Date.Add(et);
                AgendaManager.Instance.AddCustomEvent(txtTitle.Text, start, end, "#4285F4");
                dialog.Close();
            };
            btnStack.Children.Add(saveLocalBtn);

            Grid.SetRow(btnStack, 4);
            rootGrid.Children.Add(btnStack);

            mainBorder.Child = rootGrid;
            dialog.Content = mainBorder;
            dialog.ShowDialog();
        }

        public static void ShowCalendarSetupDialog(Window parent) {
            var dialog = new Window {
                Title = I18n.T("CalDialog.Title"),
                Width = 510,
                Height = 490,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true
            };

            var mainBorder = new Border {
                Background = Brush("#131622"),
                BorderBrush = Brush("#2B3248"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16)
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4-step guide box
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Warning callout
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Input box & paste
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status text
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Actions

            // Header
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var title = new TextBlock {
                Text = I18n.T("CalDialog.Title"),
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF")
            };
            titleStack.Children.Add(title);

            var subtitle = new TextBlock {
                Text = I18n.T("CalDialog.Subtitle"),
                FontSize = 10.5,
                Foreground = Brush("#8E96AC"),
                Margin = new Thickness(0, 3, 0, 0)
            };
            titleStack.Children.Add(subtitle);

            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var closeBtn = new Border {
                Background = Brushes.Transparent,
                Padding = new Thickness(4),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock {
                Text = "✕",
                FontSize = 13,
                Foreground = Brush("#8E95AA")
            };
            closeBtn.MouseLeftButtonUp += delegate { dialog.Close(); };
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
            Grid.SetRow(header, 0);
            rootGrid.Children.Add(header);

            // 4 步驟清晰指引卡片
            var guideCard = new Border {
                Background = Brush("#0D0F17"),
                BorderBrush = Brush("#1F2436"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 12, 0, 8)
            };
            var guideStack = new StackPanel();

            Func<string, string, UIElement> makeStepItem = (stepNum, text) => {
                var itemGrid = new Grid { Margin = new Thickness(0, 2.5, 0, 2.5) };
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var badge = new Border {
                    Background = Brush("#1E293B"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock {
                    Text = stepNum,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush("#38BDF8")
                };
                Grid.SetColumn(badge, 0);
                itemGrid.Children.Add(badge);

                var txt = new TextBlock {
                    Text = text,
                    FontSize = 10.5,
                    Foreground = Brush("#CAD4E4"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txt, 1);
                itemGrid.Children.Add(txt);

                return itemGrid;
            };

            guideStack.Children.Add(makeStepItem("1", I18n.T("CalDialog.Step1Desc")));
            guideStack.Children.Add(makeStepItem("2", I18n.T("CalDialog.Step2Desc")));
            guideStack.Children.Add(makeStepItem("3", I18n.T("CalDialog.Step3Desc")));
            guideStack.Children.Add(makeStepItem("4", I18n.T("CalDialog.Step4Desc")));
            guideCard.Child = guideStack;
            Grid.SetRow(guideCard, 1);
            rootGrid.Children.Add(guideCard);

            // 避坑提醒卡片
            var warnBox = new Border {
                Background = Brush("#1F1816"),
                BorderBrush = Brush("#3E221D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 8)
            };
            warnBox.Child = new TextBlock {
                Text = I18n.T("CalDialog.WarningNotice"),
                FontSize = 10,
                Foreground = Brush("#F87171"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(warnBox, 2);
            rootGrid.Children.Add(warnBox);

            // 輸入框與快捷貼上
            var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var txtUrl = new TextBox {
                Text = AgendaManager.Instance.GoogleIcsUrl,
                FontSize = 11,
                Foreground = Brush("#FFFFFF"),
                Background = Brush("#0A0C13"),
                BorderBrush = Brush("#31374A"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtUrl, 0);
            inputRow.Children.Add(txtUrl);

            var pasteBtn = new Border {
                Background = Brush("#1E2536"),
                BorderBrush = Brush("#313C54"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            pasteBtn.Child = new TextBlock {
                Text = I18n.T("CalDialog.PasteBtn"),
                FontSize = 10.5,
                Foreground = Brush("#93C5FD")
            };
            pasteBtn.MouseLeftButtonUp += delegate {
                try {
                    string clip = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(clip)) {
                        txtUrl.Text = clip.Trim();
                    }
                } catch { }
            };
            ApplyTouchFeedback(pasteBtn, 0.95);
            Grid.SetColumn(pasteBtn, 1);
            inputRow.Children.Add(pasteBtn);

            Grid.SetRow(inputRow, 3);
            rootGrid.Children.Add(inputRow);

            // 狀態與同步訊息
            var warningText = new TextBlock {
                Text = AgendaManager.Instance.LastSyncStatus,
                FontSize = 10,
                Foreground = AgendaManager.Instance.IsSyncSuccess ? Brush("#34A853") : Brush("#F59E0B"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8)
            };
            Grid.SetRow(warningText, 4);
            rootGrid.Children.Add(warningText);

            // 底部操作列
            var actionRow = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var openWebLink = new TextBlock {
                Text = I18n.T("CalDialog.OpenWebBtn"),
                FontSize = 11,
                Foreground = Brush("#38BDF8"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = TextDecorations.Underline
            };
            openWebLink.MouseLeftButtonUp += delegate {
                AgendaManager.Instance.OpenGoogleCalendarSettingsWeb();
            };
            Grid.SetColumn(openWebLink, 0);
            actionRow.Children.Add(openWebLink);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cancelBtn = new Border {
                Background = Brush("#1C202E"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Child = new TextBlock { Text = I18n.T("Common.Cancel"), Foreground = Brush("#C4D2DB"), FontSize = 11.5 };
            cancelBtn.MouseLeftButtonUp += delegate { dialog.Close(); };
            ApplyTouchFeedback(cancelBtn, 0.95);
            btnStack.Children.Add(cancelBtn);

            var saveBtn = new Border {
                Background = Brush("#2563EB"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = Cursors.Hand
            };
            saveBtn.Child = new TextBlock { Text = I18n.T("CalDialog.SaveBtn"), Foreground = Brush("#FFFFFF"), FontWeight = FontWeights.Bold, FontSize = 11.5 };
            saveBtn.MouseLeftButtonUp += delegate {
                string input = (txtUrl.Text ?? "").Trim();
                if (input.IndexOf("calendar.google.com/calendar/u", StringComparison.OrdinalIgnoreCase) >= 0 && input.IndexOf(".ics", StringComparison.OrdinalIgnoreCase) < 0) {
                    warningText.Foreground = Brush("#EF4444");
                    warningText.Text = I18n.T("CalDialog.ErrHomePage");
                    return;
                }
                if (input.IndexOf("/public/basic.ics", StringComparison.OrdinalIgnoreCase) >= 0) {
                    warningText.Foreground = Brush("#EF4444");
                    warningText.Text = I18n.T("CalDialog.ErrPublic");
                    return;
                }
                AgendaManager.Instance.SaveGoogleIcsUrl(input);
                dialog.Close();
            };
            ApplyTouchFeedback(saveBtn, 0.95);
            btnStack.Children.Add(saveBtn);
            Grid.SetColumn(btnStack, 1);
            actionRow.Children.Add(btnStack);

            Grid.SetRow(actionRow, 5);
            rootGrid.Children.Add(actionRow);

            mainBorder.Child = rootGrid;
            dialog.Content = mainBorder;
            dialog.ShowDialog();
        }

        public static void ShowNoteEditDialog(Window parent, NoteItem existingNote) {
            bool isEdit = existingNote != null;
            var dialog = new Window {
                Title = isEdit ? "編輯隨手筆記" : "新增隨手筆記",
                Width = 370,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false
            };

            var mainBorder = new Border {
                Background = Brush("#10131C"),
                BorderBrush = Brush("#45270A"),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.75
                }
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock {
                Text = isEdit ? "修改隨手筆記內容" : "新增隨手筆記",
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FBBF24")
            };
            Grid.SetColumn(titleBlock, 0);
            headerGrid.Children.Add(titleBlock);

            var closeBtn = new TextBlock {
                Text = "✕",
                FontSize = 12,
                Foreground = Brush("#6E758C"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.MouseLeftButtonUp += delegate { dialog.Close(); };
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);
            Grid.SetRow(headerGrid, 0);
            rootGrid.Children.Add(headerGrid);

            var inputBoxBorder = new Border {
                Background = Brush("#181C28"),
                BorderBrush = Brush("#2E3448"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var contentInput = new TextBox {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush("#FFFFFF"),
                CaretBrush = Brush("#F59E0B"),
                FontSize = 12,
                Text = isEdit ? existingNote.Content : "",
                AcceptsReturn = false
            };
            inputBoxBorder.Child = contentInput;
            Grid.SetRow(inputBoxBorder, 1);
            rootGrid.Children.Add(inputBoxBorder);

            // 釘選至頂部通知條開關
            bool pinState = isEdit ? existingNote.IsPinned : true;
            var pinToggleBorder = new Border {
                Background = pinState ? Brush("#291D0E") : Brush("#161924"),
                BorderBrush = pinState ? Brush("#F59E0B") : Brush("#2A3042"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var pinToggleText = new TextBlock {
                Text = pinState ? "● 已啟用：釘選在收合態頂部那一條通知顯示" : "○ 未釘選：僅顯示於展開後的隨手筆記清單",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = pinState ? Brush("#FBBF24") : Brush("#94A3B8")
            };
            pinToggleBorder.Child = pinToggleText;
            pinToggleBorder.MouseLeftButtonUp += delegate {
                pinState = !pinState;
                pinToggleBorder.Background = pinState ? Brush("#291D0E") : Brush("#161924");
                pinToggleBorder.BorderBrush = pinState ? Brush("#F59E0B") : Brush("#2A3042");
                pinToggleText.Text = pinState ? "● 已啟用：釘選在收合態頂部那一條通知顯示" : "○ 未釘選：僅顯示於展開後的隨手筆記清單";
                pinToggleText.Foreground = pinState ? Brush("#FBBF24") : Brush("#94A3B8");
            };
            Grid.SetRow(pinToggleBorder, 2);
            rootGrid.Children.Add(pinToggleBorder);

            var actionRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (isEdit) {
                var delBtn = new Border {
                    Background = Brush("#2A1519"),
                    BorderBrush = Brush("#7F1D1D"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 5, 10, 5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = Cursors.Hand
                };
                var delTb = new TextBlock {
                    Text = "刪除此筆記",
                    FontSize = 11,
                    Foreground = Brush("#F87171"),
                    FontWeight = FontWeights.SemiBold
                };
                delBtn.Child = delTb;
                delBtn.MouseLeftButtonUp += delegate {
                    NoteManager.Instance.DeleteNote(existingNote.Id);
                    dialog.Close();
                };
                Grid.SetColumn(delBtn, 0);
                actionRow.Children.Add(delBtn);
            }

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
            var cancelBtn = new Border {
                Background = Brush("#1C202E"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Child = new TextBlock {
                Text = "取消",
                FontSize = 11,
                Foreground = Brush("#9CA3B8")
            };
            cancelBtn.MouseLeftButtonUp += delegate { dialog.Close(); };
            rightBtns.Children.Add(cancelBtn);

            var saveBtn = new Border {
                Background = Brush("#D97706"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 5, 14, 5),
                Cursor = Cursors.Hand
            };
            saveBtn.Child = new TextBlock {
                Text = isEdit ? "儲存修改" : "新增筆記",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#FFFFFF")
            };

            Action commitSave = delegate {
                string val = contentInput.Text != null ? contentInput.Text.Trim() : "";
                if (string.IsNullOrEmpty(val)) return;
                if (isEdit) {
                    NoteManager.Instance.UpdateNote(existingNote.Id, val);
                    if (existingNote.IsPinned != pinState) {
                        NoteManager.Instance.TogglePin(existingNote.Id);
                    }
                } else {
                    NoteManager.Instance.AddNote(val, pinState, "#F59E0B");
                }
                dialog.Close();
            };

            saveBtn.MouseLeftButtonUp += delegate { commitSave(); };
            contentInput.KeyDown += delegate(object s, KeyEventArgs ke) {
                if (ke.Key == Key.Enter) {
                    ke.Handled = true;
                    commitSave();
                } else if (ke.Key == Key.Escape) {
                    dialog.Close();
                }
            };
            rightBtns.Children.Add(saveBtn);
            Grid.SetColumn(rightBtns, 1);
            actionRow.Children.Add(rightBtns);

            Grid.SetRow(actionRow, 4);
            rootGrid.Children.Add(actionRow);

            mainBorder.Child = rootGrid;
            dialog.Content = mainBorder;
            dialog.Loaded += delegate {
                contentInput.Focus();
                contentInput.SelectAll();
            };
            dialog.ShowDialog();
        }

        public static Canvas CreateGoogleCalendarIcon(double size) {
            var canvas = new Canvas { Width = size, Height = size };
            double scale = size / 24.0;
            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(scale, scale));
            canvas.RenderTransform = tg;

            var baseBorder = new Rectangle {
                Width = 20,
                Height = 20,
                RadiusX = 4,
                RadiusY = 4,
                Fill = Brush("#FFFFFF")
            };
            Canvas.SetLeft(baseBorder, 2);
            Canvas.SetTop(baseBorder, 2);
            canvas.Children.Add(baseBorder);

            var topBar = new Rectangle {
                Width = 20,
                Height = 6,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Brush("#4285F4")
            };
            Canvas.SetLeft(topBar, 2);
            Canvas.SetTop(topBar, 2);
            canvas.Children.Add(topBar);

            var numText = new TextBlock {
                Text = DateTime.Now.Day.ToString(),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#4285F4"),
                FontFamily = new FontFamily("Segoe UI, Arial")
            };
            Canvas.SetLeft(numText, 6.5);
            Canvas.SetTop(numText, 8.5);
            canvas.Children.Add(numText);

            return canvas;
        }

        public static Path CreateExternalLinkIcon(double size) {
            var path = new Path {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = Brush("#8E95AA"),
                Data = Geometry.Parse("M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z")
            };
            return path;
        }

        public static Path CreateLinkIcon(double size) {
            var path = new Path {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = Brush("#8E95AA"),
                Data = Geometry.Parse("M3.9,12C3.9,10.29 5.29,8.9 7,8.9H11V7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H11V15.1H7C5.29,15.1 3.9,13.71 3.9,12M8,13H16V11H8V13M17,7H13V8.9H17C18.71,8.9 20.1,10.29 20.1,12C20.1,13.71 18.71,15.1 17,15.1H13V17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7Z")
            };
            return path;
        }

        public static Path CreateRefreshIcon(double size) {
            var path = new Path {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = Brush("#8E95AA"),
                Data = Geometry.Parse("M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z")
            };
            return path;
        }

        public static ControlTemplate CreateMicroScrollViewerTemplate() {
            string xaml = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollViewer'>
                <Grid Background='Transparent'>
                    <ScrollContentPresenter Margin='0,0,6,0'/>
                    <ScrollBar x:Name='PART_VerticalScrollBar' HorizontalAlignment='Right' Width='4' Background='Transparent' BorderThickness='0' Value='{TemplateBinding VerticalOffset}' Maximum='{TemplateBinding ScrollableHeight}' ViewportSize='{TemplateBinding ViewportHeight}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'>
                        <ScrollBar.Template>
                            <ControlTemplate TargetType='ScrollBar'>
                                <Track x:Name='PART_Track' IsDirectionReversed='True'>
                                    <Track.Thumb>
                                        <Thumb Focusable='False' Width='4'>
                                            <Thumb.Template>
                                                <ControlTemplate TargetType='Thumb'>
                                                    <Border Background='#35FFFFFF' CornerRadius='2' Width='4'/>
                                                </ControlTemplate>
                                            </Thumb.Template>
                                        </Thumb>
                                    </Track.Thumb>
                                </Track>
                            </ControlTemplate>
                        </ScrollBar.Template>
                    </ScrollBar>
                </Grid>
            </ControlTemplate>";
            try {
                return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
            } catch {
                return null;
            }
        }
    }
}
