using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace TokenIsland {
    public class MediaTrackInfo {
        public string Title = "未播放媒體";
        public string Artist = "Ready to play";
        public string AppName = "";
        public bool IsPlaying = false;
        public BitmapSource Thumbnail = null;
        public bool HasMedia = false;
    }

    public class MediaManager {
        private static MediaManager instance;
        public static MediaManager Instance {
            get {
                if (instance == null) instance = new MediaManager();
                return instance;
            }
        }

        public event Action<MediaTrackInfo> TrackChanged;
        public MediaTrackInfo CurrentTrack { get; private set; }

        private GlobalSystemMediaTransportControlsSessionManager sessionManager;
        private DispatcherTimer pollTimer;
        private string lastTitle = "";
        private string lastArtist = "";
        private bool lastPlaying = false;
        private string lastAppId = "";
        private bool lastHasThumbnail;
        private bool refreshing;

        internal bool IsCurrentTrack(string title, string artist, bool playing, string appId, bool hasThumbnail) {
            return CurrentTrack.HasMedia && title == lastTitle && artist == lastArtist
                && playing == lastPlaying && appId == lastAppId && hasThumbnail == lastHasThumbnail;
        }

        public MediaManager() {
            CurrentTrack = new MediaTrackInfo();
            Initialize();
        }

        private async void Initialize() {
            try {
                sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                await RefreshTrackInfoAsync();
            } catch { }

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            pollTimer.Tick += async delegate {
                await RefreshTrackInfoAsync();
            };
            pollTimer.Start();
        }

        public async Task RefreshTrackInfoAsync() {
            if (refreshing) return;
            refreshing = true;
            try {
                if (sessionManager == null) {
                    sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                }
                var session = sessionManager != null ? sessionManager.GetCurrentSession() : null;
                if (session == null) {
                    if (CurrentTrack.HasMedia || CurrentTrack.Title != "暫無媒體播放") {
                        CurrentTrack = new MediaTrackInfo {
                            Title = "暫無媒體播放",
                            Artist = "NotchNook Ready",
                            AppName = "",
                            IsPlaying = false,
                            Thumbnail = null,
                            HasMedia = false
                        };
                        lastTitle = "";
                        lastArtist = "";
                        lastPlaying = false;
                        RaiseTrackChanged();
                    }
                    return;
                }

                string appId = session.SourceAppUserModelId ?? "";
                string displayAppName = CleanAppName(appId);

                var playbackInfo = session.GetPlaybackInfo();
                bool isPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var props = await session.TryGetMediaPropertiesAsync();
                string title = (props != null && !string.IsNullOrEmpty(props.Title)) ? props.Title : "未知曲目";
                string artist = (props != null && !string.IsNullOrEmpty(props.Artist)) ? props.Artist : displayAppName;

                // 檢查是否有變動
                bool hasThumbnail = props != null && props.Thumbnail != null;
                if (IsCurrentTrack(title, artist, isPlaying, appId, hasThumbnail)) {
                    return;
                }

                BitmapSource thumb = null;
                if (props != null && props.Thumbnail != null) {
                    try {
                        using (var stream = await props.Thumbnail.OpenReadAsync()) {
                            using (var netStream = stream.AsStreamForRead()) {
                                var img = new BitmapImage();
                                img.BeginInit();
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.StreamSource = netStream;
                                img.EndInit();
                                img.Freeze();
                                thumb = img;
                            }
                        }
                    } catch { }
                }

                lastTitle = title;
                lastArtist = artist;
                lastPlaying = isPlaying;
                lastAppId = appId;
                lastHasThumbnail = hasThumbnail;

                CurrentTrack = new MediaTrackInfo {
                    Title = title,
                    Artist = artist,
                    AppName = displayAppName,
                    IsPlaying = isPlaying,
                    Thumbnail = thumb,
                    HasMedia = true
                };
                RaiseTrackChanged();
            } catch {
                if (CurrentTrack.HasMedia) {
                    CurrentTrack = new MediaTrackInfo {
                        Title = "暫無媒體播放",
                        Artist = "NotchNook Ready",
                        AppName = "",
                        IsPlaying = false,
                        Thumbnail = null,
                        HasMedia = false
                    };
                    RaiseTrackChanged();
                }
            } finally {
                refreshing = false;
            }
        }

        private void RaiseTrackChanged() {
            try {
                if (TrackChanged != null) {
                    TrackChanged(CurrentTrack);
                }
            } catch { }
        }

        private string CleanAppName(string raw) {
            if (string.IsNullOrEmpty(raw)) return "系統播放器";
            string lower = raw.ToLowerInvariant();
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("applemusic") || lower.Contains("apple music")) return "Apple Music";
            if (lower.Contains("chrome")) return "Chrome";
            if (lower.Contains("msedge") || lower.Contains("edge")) return "Edge";
            if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "網易雲音樂";
            if (lower.Contains("qqmusic")) return "QQ音樂";
            if (lower.Contains("foobar")) return "foobar2000";
            if (lower.Contains("vlc")) return "VLC";
            if (lower.Contains("music")) return "音樂播放器";
            int lastDot = raw.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < raw.Length - 1) return raw.Substring(lastDot + 1);
            return raw;
        }

        public async Task<bool> PlayPauseAsync() {
            try {
                if (sessionManager != null) {
                    var session = sessionManager.GetCurrentSession();
                    if (session != null) {
                        bool res = await session.TryTogglePlayPauseAsync();
                        await Task.Delay(100);
                        await RefreshTrackInfoAsync();
                        return res;
                    }
                }
            } catch { }
            return false;
        }

        public async Task<bool> NextAsync() {
            try {
                if (sessionManager != null) {
                    var session = sessionManager.GetCurrentSession();
                    if (session != null) {
                        bool res = await session.TrySkipNextAsync();
                        await Task.Delay(100);
                        await RefreshTrackInfoAsync();
                        return res;
                    }
                }
            } catch { }
            return false;
        }

        public async Task<bool> PreviousAsync() {
            try {
                if (sessionManager != null) {
                    var session = sessionManager.GetCurrentSession();
                    if (session != null) {
                        bool res = await session.TrySkipPreviousAsync();
                        await Task.Delay(100);
                        await RefreshTrackInfoAsync();
                        return res;
                    }
                }
            } catch { }
            return false;
        }
    }
}
