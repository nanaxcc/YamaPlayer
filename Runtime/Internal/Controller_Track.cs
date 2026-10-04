
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Yamadev.YamaStream
{
    public partial class Controller
    {
        [UdonSynced] VideoPlayerType _targetPlayer;
        [UdonSynced] string _title = string.Empty;
        [UdonSynced] VRCUrl _url = VRCUrl.Empty;
        [UdonSynced] string _originalUrl = string.Empty;
        [UdonSynced] int _trackRevision;
        [UdonSynced] int _loadPhase;
        [UdonSynced] int _loadStartedAt;
        int _localLoadPhase;
        int _appliedTrackRevision = -1;
        int _resolveRevision;
        bool _changingTrack;
        bool _pendingVideoRequest;
        VRCUrl _pendingVideoUrl;
        int _pendingResolveRevision;
        float _nextVideoRequestTime;
        public bool IsPreloading => _localLoadPhase == 1;
        public int ResolveRevision => _resolveRevision;
        public bool CanStartPreloaded => IsPreloading && VideoPlayerHandle.IsReady &&
            Networking.GetServerTimeInMilliseconds() - _loadStartedAt >= 5100;
        Track _track;
        UdonEvent _resolveTrack;

        public Track Track
        {
            get
            {
                if (!Utilities.IsValid(_track))
                    _track = Track.Empty();
                return _track;
            }
            set
            {
                _track = value;
                foreach (Listener listener in _listeners) listener.OnTrackUpdated();
            }
        }

        public UdonEvent ResolveTrack
        {
            get
            {
                if (!Utilities.IsValid(_resolveTrack)) 
                    _resolveTrack = UdonEvent.New(this, nameof(Resolve));
                return _resolveTrack;
            }
            set => _resolveTrack = value;
        }

        public void PreloadTrack(Playlist playlist, int index)
        {
            if (playlist == null || index < 0 || index >= playlist.Length) return;
            bool special = playlist == _queue || playlist == _history;
            _activePlaylistIndex = special ? -1 : System.Array.IndexOf(Playlists, playlist);
            _playingTrackIndex = special ? -1 : index;
            BeginTrack(playlist.GetTrack(index), true, false);
        }

        public void PlayTrack(Track track, bool isReload = false)
        {
            if (IsPreloading && !isReload && track.GetUrl() == Track.GetUrl() &&
                track.GetPlayerType() == Track.GetPlayerType())
            {
                RequestPlay();
                return;
            }
            BeginTrack(track, false, isReload);
        }

        void BeginTrack(Track track, bool loadOnly, bool isReload)
        {
            if (!track.GetUrl().IsValidUrl()) return;
            _isReload = isReload;
            if (!isReload)
            {
                _trackRevision++;
                _loadPhase = loadOnly ? 1 : 0;
                _loadStartedAt = Networking.GetServerTimeInMilliseconds();
                _paused = false;
                _repeat = new Vector3(0f, 0f, 999999f);
                ClearSync();
            }
            StartTrackLocal(track, _loadPhase);
            if (Networking.IsOwner(gameObject) && !_isLocal && !isReload) RequestSerialization();
        }

        void StartTrackLocal(Track track, int phase)
        {
            if (!_isReload && !string.IsNullOrEmpty(Track.GetUrl()) && _localLoadPhase != 1)
                _history.AddTrack(Track);
            _changingTrack = true;
            foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Stop();
            _changingTrack = false;
            foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.UseFallbackHandle = false;
            CancelPendingVideo();
            _videoPlayerType = track.GetPlayerType();
            _localLoadPhase = phase;
            _appliedTrackRevision = _trackRevision;
            _stopped = phase == 1;
            Track = track;
            foreach (Listener listener in _listeners) listener.OnPlayerChanged();
            ResolveTrack.Invoke();
            foreach (Listener listener in _listeners) listener.OnUrlChanged();
        }

        public void RequestPlay()
        {
            if (!IsPreloading) { Paused = false; return; }
            // The fixed interval never starts playback automatically.
            if (!CanStartPreloaded) return;
            _loadPhase = 2;
            _localLoadPhase = 2;
            _paused = false;
            _stopped = false;
            SyncTime = 0f;
            VideoPlayerHandle.StartLoaded();
            if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            foreach (Listener listener in _listeners) listener.OnVideoReady();
        }

        void CancelPendingVideo()
        {
            _resolveRevision++;
            _pendingVideoRequest = false;
            _retryTargetUrl = VRCUrl.Empty;
            _errorRetryCount = 0;
        }

        public void Resolve() => ResolveCompleted(Track.GetVRCUrl(), _resolveRevision);

        public void ResolveCompleted(VRCUrl url, int revision, bool waitAfterResolver = false)
        {
            if (revision != _resolveRevision || string.IsNullOrEmpty(Track.GetUrl())) return;
            if (waitAfterResolver) _nextVideoRequestTime = Mathf.Max(_nextVideoRequestTime, Time.time + 5.1f);
            _pendingVideoUrl = url;
            _pendingResolveRevision = revision;
            _pendingVideoRequest = true;
            PumpVideoRequest();
        }

        void PumpVideoRequest()
        {
            if (!_pendingVideoRequest || Time.time < _nextVideoRequestTime) return;
            _pendingVideoRequest = false;
            if (_pendingResolveRevision != _resolveRevision) return;
            _nextVideoRequestTime = Time.time + 5.1f;
            if (_localLoadPhase != 0 && !_isReload) VideoPlayerHandle.LoadUrl(_pendingVideoUrl);
            else VideoPlayerHandle.PlayUrl(_pendingVideoUrl);
        }

        public override void OnPreSerialization()
        {
            _targetPlayer = Track.GetPlayerType();
            _title = Track.GetTitle();
            _url = Track.GetVRCUrl();
            _originalUrl = Track.GetOriginalUrl();
        }
    }
}
