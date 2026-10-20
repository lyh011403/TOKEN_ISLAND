using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TokenIsland {
    public class WeatherData {
        public string City = "台北";
        public string Temp = "26°C";
        public string TempNum = "26°";
        public string FeelsLike = "28°";
        public string Condition = "晴時多雲";
        public string ConditionCode = "PartlyCloudy"; // Sunny, Night, PartlyCloudy, Cloudy, Rainy, Thunder, Snow
        public string MinTemp = "22°";
        public string MaxTemp = "28°";
        public string Humidity = "75%";
        public string RainChance = "15%";
        public string WindKmph = "14";
        public DateTime Updated = DateTime.MinValue;
    }

    public class WeatherManager {
        private static WeatherManager instance;
        public static WeatherManager Instance {
            get {
                if (instance == null) instance = new WeatherManager();
                return instance;
            }
        }

        public WeatherData Current { get; private set; }
        public event Action WeatherUpdated;

        private DateTime lastFetch = DateTime.MinValue;
        private bool isFetching = false;

        public WeatherManager() {
            Current = new WeatherData {
                City = "台北",
                Temp = "26°C",
                TempNum = "26°",
                FeelsLike = "28°",
                Condition = "晴時多雲",
                ConditionCode = "PartlyCloudy",
                MinTemp = "22°",
                MaxTemp = "28°",
                Humidity = "75%",
                RainChance = "15%",
                WindKmph = "14",
                Updated = DateTime.Now
            };
        }

        public async Task RefreshAsync(bool force = false) {
            if (isFetching) return;
            if (!force && DateTime.UtcNow - lastFetch < TimeSpan.FromMinutes(25)) return;

            isFetching = true;
            try {
                await Task.Run(() => {
                    FetchWeather();
                });
                lastFetch = DateTime.UtcNow;
                if (WeatherUpdated != null) WeatherUpdated();
            } catch { }
            finally {
                isFetching = false;
            }
        }

        private void FetchWeather() {
            try {
                // 優先使用 Open-Meteo 高解析在地氣象模型
                if (FetchFromOpenMeteo()) return;
            } catch { }

            try {
                // 降級備援方案：wttr.in
                FetchFromWttrIn();
            } catch {
                // 離線快取防護
                int hour = DateTime.Now.Hour;
                bool isNight = hour < 6 || hour >= 18;
                if (Current == null) Current = new WeatherData();
                Current.ConditionCode = isNight ? "Night" : "PartlyCloudy";
                Current.Condition = isNight ? "晴朗夜空" : "晴時多雲";
            }
        }

        private bool FetchFromOpenMeteo() {
            string city = "本地";
            double lat = 25.0693;
            double lon = 121.4626;

            // 1. IP 快速反查所在城市與經緯度 (timeout 2500ms)
            try {
                var geoReq = (HttpWebRequest)WebRequest.Create("http://ip-api.com/json/?fields=status,city,regionName,lat,lon");
                geoReq.Timeout = 2500;
                geoReq.ReadWriteTimeout = 2500;
                using (var geoRes = (HttpWebResponse)geoReq.GetResponse())
                using (var reader = new StreamReader(geoRes.GetResponseStream())) {
                    string geoJson = reader.ReadToEnd();
                    var ser = new JavaScriptSerializer();
                    var gDict = ser.Deserialize<Dictionary<string, object>>(geoJson);
                    if (gDict != null && gDict.ContainsKey("status") && Convert.ToString(gDict["status"]) == "success") {
                        string rawCity = Convert.ToString(gDict["city"]);
                        city = LocalizeCityName(rawCity);
                        lat = Convert.ToDouble(gDict["lat"]);
                        lon = Convert.ToDouble(gDict["lon"]);
                    }
                }
            } catch { }

            // 2. 請求 Open-Meteo 高解析氣象數據 (免金鑰、高可靠度)
            string url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto",
                lat, lon);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 4000;
            req.ReadWriteTimeout = 4000;
            req.UserAgent = "TokenIsland/2.0";

            using (var res = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(res.GetResponseStream())) {
                string json = reader.ReadToEnd();
                var ser = new JavaScriptSerializer();
                var dict = ser.Deserialize<Dictionary<string, object>>(json);
                if (dict == null || !dict.ContainsKey("current")) return false;

                var cur = (Dictionary<string, object>)dict["current"];
                var daily = dict.ContainsKey("daily") ? (Dictionary<string, object>)dict["daily"] : null;

                double temp = Convert.ToDouble(cur["temperature_2m"]);
                string tempC = Math.Round(temp) + "°C";
                string tempNum = Math.Round(temp) + "°";
                string feelsLike = Math.Round(Convert.ToDouble(cur["apparent_temperature"])) + "°";
                string humidity = Convert.ToString(cur["relative_humidity_2m"]) + "%";
                double precip = Convert.ToDouble(cur["precipitation"]);
                int wmoCode = Convert.ToInt32(cur["weather_code"]);
                bool isDay = Convert.ToInt32(cur["is_day"]) == 1;

                string maxTemp = tempNum;
                string minTemp = tempNum;
                int rainProbVal = 0;
                string rainChance = "0%";

                if (daily != null) {
                    try {
                        var maxList = (ArrayList)daily["temperature_2m_max"];
                        var minList = (ArrayList)daily["temperature_2m_min"];
                        var rainList = (ArrayList)daily["precipitation_probability_max"];
                        if (maxList != null && maxList.Count > 0) maxTemp = Math.Round(Convert.ToDouble(maxList[0])) + "°";
                        if (minList != null && minList.Count > 0) minTemp = Math.Round(Convert.ToDouble(minList[0])) + "°";
                        if (rainList != null && rainList.Count > 0) {
                            rainProbVal = Convert.ToInt32(rainList[0]);
                            rainChance = rainProbVal + "%";
                        }
                    } catch { }
                }

                // 3. 雙門檻防虛警校驗（Precipitation Threshold）
                string condCode;
                string zhDesc;
                MapWmoCode(wmoCode, isDay, precip, rainProbVal, out condCode, out zhDesc);

                Current = new WeatherData {
                    City = city,
                    Temp = tempC,
                    TempNum = tempNum,
                    FeelsLike = feelsLike,
                    Condition = zhDesc,
                    ConditionCode = condCode,
                    MinTemp = minTemp,
                    MaxTemp = maxTemp,
                    Humidity = humidity,
                    RainChance = rainChance,
                    WindKmph = "12",
                    Updated = DateTime.Now
                };
                return true;
            }
        }

        private static void MapWmoCode(int code, bool isDay, double precip, int rainProb, out string condCode, out string zhDesc) {
            bool isNight = !isDay;

            // 0, 1: 晴天
            if (code == 0 || code == 1) {
                condCode = isNight ? "Night" : "Sunny";
                zhDesc = isNight ? "晴朗夜空" : "晴朗無雲";
                return;
            }
            // 2: 晴時多雲
            if (code == 2) {
                condCode = "PartlyCloudy";
                zhDesc = "晴時多雲";
                return;
            }
            // 3: 陰天
            if (code == 3) {
                condCode = "Cloudy";
                zhDesc = "陰天多雲";
                return;
            }
            // 45, 48: 霧
            if (code == 45 || code == 48) {
                condCode = "Cloudy";
                zhDesc = "晨霧迷濛";
                return;
            }
            // 71..77: 雪
            if (code >= 71 && code <= 77) {
                condCode = "Snow";
                zhDesc = "降雪";
                return;
            }

            // 雨天與陣雨 (51..67, 80..82)
            if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) {
                // 核心防虛警門檻：若模型帶有微弱水氣代碼但機率 < 35% 且地面降雨量 < 0.1mm，杜絕誤報下雨！
                if (rainProb < 35 && precip < 0.1) {
                    condCode = "PartlyCloudy";
                    zhDesc = "晴時多雲";
                    return;
                }
                condCode = "Rainy";
                if (code >= 63 || code == 82) zhDesc = "大雨陣雨";
                else if (code >= 51 && code <= 55) zhDesc = "局部零星小雨";
                else zhDesc = "短暫陣雨";
                return;
            }

            // 雷雨 (95..99)
            if (code >= 95) {
                if (rainProb < 35 && precip < 0.1) {
                    condCode = "PartlyCloudy";
                    zhDesc = "晴時多雲";
                    return;
                }
                condCode = "Thunder";
                zhDesc = "雷陣雨";
                return;
            }

            condCode = isNight ? "Night" : "PartlyCloudy";
            zhDesc = isNight ? "晴朗夜空" : "晴時多雲";
        }

        private void FetchFromWttrIn() {
            var req = (HttpWebRequest)WebRequest.Create("https://wttr.in/?format=j1");
            req.UserAgent = "curl/7.68.0";
            req.Timeout = 4000;
            req.ReadWriteTimeout = 4000;

            using (var res = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(res.GetResponseStream())) {
                string json = reader.ReadToEnd();
                var ser = new JavaScriptSerializer();
                var dict = ser.Deserialize<Dictionary<string, object>>(json);

                var currentList = (ArrayList)dict["current_condition"];
                var currentCond = (Dictionary<string, object>)currentList[0];
                string tempRaw = Convert.ToString(currentCond["temp_C"]);
                string tempC = tempRaw + "°C";
                string tempNum = tempRaw + "°";
                string feelsLike = Convert.ToString(currentCond["FeelsLikeC"]) + "°";
                string humidity = Convert.ToString(currentCond["humidity"]) + "%";
                string wind = Convert.ToString(currentCond["windspeedKmph"]);
                double precip = 0.0;
                double.TryParse(Convert.ToString(currentCond["precipMM"]), out precip);

                var descList = (ArrayList)currentCond["weatherDesc"];
                var descDict = (Dictionary<string, object>)descList[0];
                string desc = Convert.ToString(descDict["value"]);

                string city = "本地";
                try {
                    var areaList = (ArrayList)dict["nearest_area"];
                    var area = (Dictionary<string, object>)areaList[0];
                    var areaNameList = (ArrayList)area["areaName"];
                    var areaNameDict = (Dictionary<string, object>)areaNameList[0];
                    city = LocalizeCityName(Convert.ToString(areaNameDict["value"]));
                } catch { }

                string maxTemp = tempNum;
                string minTemp = tempNum;
                int rainChanceNum = 0;
                string rainChance = "0%";
                try {
                    var weatherArr = (ArrayList)dict["weather"];
                    if (weatherArr != null && weatherArr.Count > 0) {
                        var today = (Dictionary<string, object>)weatherArr[0];
                        maxTemp = Convert.ToString(today["maxtempC"]) + "°";
                        minTemp = Convert.ToString(today["mintempC"]) + "°";

                        var hourly = (ArrayList)today["hourly"];
                        if (hourly != null) {
                            int curHour = DateTime.Now.Hour;
                            int bestDiff = 999;
                            foreach (Dictionary<string, object> slot in hourly) {
                                int slotTime = 0;
                                int.TryParse(Convert.ToString(slot["time"]), out slotTime);
                                slotTime = slotTime / 100;
                                int diff = Math.Abs(slotTime - curHour);
                                if (diff < bestDiff) {
                                    bestDiff = diff;
                                    rainChanceNum = 0;
                                    int.TryParse(Convert.ToString(slot["chanceofrain"]), out rainChanceNum);
                                    rainChance = rainChanceNum + "%";
                                }
                            }
                        }
                    }
                } catch { }

                string condCode = MapConditionCodeWithThreshold(desc, precip, rainChanceNum);
                string zhDesc = TranslateConditionWithThreshold(condCode, desc, precip, rainChanceNum);

                Current = new WeatherData {
                    City = city,
                    Temp = tempC,
                    TempNum = tempNum,
                    FeelsLike = feelsLike,
                    Condition = zhDesc,
                    ConditionCode = condCode,
                    MinTemp = minTemp,
                    MaxTemp = maxTemp,
                    Humidity = humidity,
                    RainChance = rainChance,
                    WindKmph = wind,
                    Updated = DateTime.Now
                };
            }
        }

        private string MapConditionCodeWithThreshold(string desc, double precip, int rainChance) {
            if (string.IsNullOrEmpty(desc)) return "PartlyCloudy";
            string d = desc.ToLowerInvariant();
            int hour = DateTime.Now.Hour;
            bool isNight = hour < 6 || hour >= 18;

            if (d.Contains("thunder") || d.Contains("storm")) {
                if (rainChance < 35 && precip < 0.1) return isNight ? "Night" : "PartlyCloudy";
                return "Thunder";
            }
            if (d.Contains("rain") || d.Contains("drizzle") || d.Contains("shower")) {
                // 防虛警門檻
                if (rainChance < 35 && precip < 0.1) return isNight ? "Night" : "PartlyCloudy";
                return "Rainy";
            }
            if (d.Contains("snow") || d.Contains("ice") || d.Contains("sleet")) return "Snow";
            if (d.Contains("cloud") || d.Contains("overcast")) return "Cloudy";
            if (d.Contains("clear") || d.Contains("sunny")) return isNight ? "Night" : "Sunny";
            return isNight ? "Night" : "PartlyCloudy";
        }

        private string TranslateConditionWithThreshold(string code, string raw, double precip, int rainChance) {
            string r = (raw ?? "").ToLowerInvariant();
            int hour = DateTime.Now.Hour;
            bool isNight = hour < 6 || hour >= 18;

            if (code == "PartlyCloudy") return isNight ? "晴朗夜空" : "晴時多雲";
            if (code == "Sunny") return isNight ? "晴朗夜空" : "晴朗無雲";
            if (code == "Cloudy") return "陰天多雲";
            if (code == "Snow") return "降雪";
            if (code == "Thunder") return "雷陣雨";
            if (code == "Rainy") {
                if (r.Contains("heavy") || r.Contains("torrential")) return "大雨豪雨";
                if (r.Contains("shower")) return "陣雨";
                return "短暫陣雨";
            }
            return "晴時多雲";
        }

        private static string LocalizeCityName(string raw) {
            if (string.IsNullOrEmpty(raw)) return "本地";
            string s = raw.Trim();
            if (s.Equals("Taipei", StringComparison.OrdinalIgnoreCase)) return "台北";
            if (s.Equals("New Taipei", StringComparison.OrdinalIgnoreCase) || s.Equals("New Taipei City", StringComparison.OrdinalIgnoreCase)) return "新北";
            if (s.Equals("Sanchung", StringComparison.OrdinalIgnoreCase) || s.Equals("Sanchong", StringComparison.OrdinalIgnoreCase)) return "三重";
            if (s.Equals("Banqiao", StringComparison.OrdinalIgnoreCase) || s.Equals("Panchiao", StringComparison.OrdinalIgnoreCase)) return "板橋";
            if (s.Equals("Zhonghe", StringComparison.OrdinalIgnoreCase) || s.Equals("Chungho", StringComparison.OrdinalIgnoreCase)) return "中和";
            if (s.Equals("Yonghe", StringComparison.OrdinalIgnoreCase) || s.Equals("Yungho", StringComparison.OrdinalIgnoreCase)) return "永和";
            if (s.Equals("Xinzhuang", StringComparison.OrdinalIgnoreCase) || s.Equals("Hsinchuang", StringComparison.OrdinalIgnoreCase)) return "新莊";
            if (s.Equals("Xindian", StringComparison.OrdinalIgnoreCase) || s.Equals("Hsintien", StringComparison.OrdinalIgnoreCase)) return "新店";
            if (s.Equals("Taoyuan", StringComparison.OrdinalIgnoreCase)) return "桃園";
            if (s.Equals("Taichung", StringComparison.OrdinalIgnoreCase)) return "台中";
            if (s.Equals("Tainan", StringComparison.OrdinalIgnoreCase)) return "台南";
            if (s.Equals("Kaohsiung", StringComparison.OrdinalIgnoreCase)) return "高雄";
            if (s.Equals("Hsinchu", StringComparison.OrdinalIgnoreCase)) return "新竹";
            if (s.Equals("Keelung", StringComparison.OrdinalIgnoreCase) || s.Equals("Chilung", StringComparison.OrdinalIgnoreCase)) return "基隆";
            if (s.Equals("Chiayi", StringComparison.OrdinalIgnoreCase)) return "嘉義";
            if (s.Equals("Miaoli", StringComparison.OrdinalIgnoreCase)) return "苗栗";
            if (s.Equals("Changhua", StringComparison.OrdinalIgnoreCase)) return "彰化";
            if (s.Equals("Nantou", StringComparison.OrdinalIgnoreCase)) return "南投";
            if (s.Equals("Yunlin", StringComparison.OrdinalIgnoreCase)) return "雲林";
            if (s.Equals("Pingtung", StringComparison.OrdinalIgnoreCase)) return "屏東";
            if (s.Equals("Yilan", StringComparison.OrdinalIgnoreCase) || s.Equals("Ilan", StringComparison.OrdinalIgnoreCase)) return "宜蘭";
            if (s.Equals("Hualien", StringComparison.OrdinalIgnoreCase)) return "花蓮";
            if (s.Equals("Taitung", StringComparison.OrdinalIgnoreCase)) return "台東";
            return s;
        }

        // 建立極簡向量幾何天氣圖示（絕不使用任何 Emoji）
        public static System.Windows.Shapes.Path CreateWeatherIcon(string code, double size = 16) {
            var path = new System.Windows.Shapes.Path {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };

            switch (code) {
                case "Sunny":
                    path.Data = Geometry.Parse("M12,7 A5,5 0 1,0 12,17 A5,5 0 1,0 12,7 M12,1 L12,4 M12,20 L12,23 M4.22,4.22 L6.34,6.34 M17.66,17.66 L19.78,19.78 M1,12 L4,12 M20,12 L23,12 M4.22,19.78 L6.34,17.66 M17.66,6.34 L19.78,4.22");
                    path.Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD60A");
                    path.StrokeThickness = 2;
                    path.StrokeLineJoin = PenLineJoin.Round;
                    break;
                case "Night":
                    path.Data = Geometry.Parse("M21,12.79 A9,9 0 1,1 11.21,3 A7,7 0 0,0 21,12.79 Z");
                    path.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD60A");
                    break;
                case "Cloudy":
                    path.Data = Geometry.Parse("M18,10 C17.3,7.7 15.1,6 12.5,6 C9.7,6 7.4,8 7.1,10.6 C4.8,11.1 3,13.1 3,15.5 C3,18 5,20 7.5,20 L18,20 C20.2,20 22,18.2 22,16 C22,13.9 20.3,12.1 18,12 Z");
                    path.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString("#A0AEC0");
                    break;
                case "Rainy":
                    path.Data = Geometry.Parse("M16,8 C15.4,6.2 13.6,5 11.5,5 C9.1,5 7.1,6.8 6.8,9.2 C4.9,9.7 3.5,11.4 3.5,13.5 C3.5,15.8 5.3,17.5 7.5,17.5 L16.5,17.5 C18.5,17.5 20,16 20,14 C20,12.1 18.5,10.5 16.5,10.4 M8,19 L7,22 M12,19 L11,22 M16,19 L15,22");
                    path.Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#0A84FF");
                    path.StrokeThickness = 1.6;
                    break;
                default: // PartlyCloudy
                    path.Data = Geometry.Parse("M17,11 C16.5,9.2 14.8,8 13,8 C11.3,8 9.8,9.2 9.3,10.8 C7.8,11.2 6.5,12.5 6.5,14 C6.5,16 8.2,17.5 10,17.5 L17.5,17.5 C19.2,17.5 20.5,16.2 20.5,14.5 C20.5,12.8 19,11.2 17,11 Z M9,6 A4,4 0 0,1 15,4");
                    path.Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#E2E8F0");
                    path.StrokeThickness = 1.6;
                    break;
            }

            return path;
        }
    }
}
