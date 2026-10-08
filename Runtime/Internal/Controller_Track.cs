
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
        [UdonSynced] bool _showMode;
        [UdonSynced] bool _hasStart;
        [UdonSynced] float _startTime;
        [UdonSynced] bool _hasEnd;
        [UdonSynced] float _endTime;
        [UdonSynced] int _videoGeneration;
        [UdonSynced] int _songIndex = -1;
        [UdonSynced] int _selectionRevision;
        [UdonSynced] int _runId;
        [UdonSynced] int _endNoticeSequence;
        [UdonSynced] int _endGeneration;
        [UdonSynced] int _endRunId;
        [UdonSynced] int _endKind;
        [UdonSynced] int _endReason;
        [UdonSynced] float _endPosition;
        int _localLoadPhase;
        int _appliedTrackRevision = -1;
        int _resolveRevision;
        bool _changingTrack;
        bool _pendingVideoRequest;
        bool _creatingShowTrack;
        bool _authorizedShowStart;
        int _localLoadAttempt;
        bool _intervalValidated;
        bool _intervalPrepared;
        bool _intervalSeekPending;
        float _intervalSeekStartedAt;
        bool _pendingNetworkStart;
        int _playbackNoticeSequence;
        int _pendingStopReason;
        bool _endRecordWritten;
        bool _intervalValidationFailed;
        bool _hasSavedShowSettings;
        bool _savedLoop;
        Vector3 _savedRepeat;
        float _savedForwardInterval;
        float _lastValidVideoTime;
        bool _hasLastValidVideoTime;
        VRCUrl _pendingVideoUrl;
        int _pendingResolveRevision;
        float _nextVideoRequestTime;
        public bool IsPreloading => _localLoadPhase == 1;
        public bool ShowMode => _showMode;
        public int VideoGeneration => _videoGeneration;
        public int SongIndex => _songIndex;
        public int SelectionRevision => _selectionRevision;
        public int RunId => _runId;
        public int EndNoticeSequence => _endNoticeSequence;
        public int EndGeneration => _endGeneration;
        public int EndRunId => _endRunId;
        public int EndKind => _endKind;
        public int EndReason => _endReason;
        public float EndPosition => _endPosition;
        public int ResolveRevision => _resolveRevision;
        public bool CanStartPreloaded => IsPreloading && VideoPlayerHandle.IsReady &&
            (!_showMode || _intervalPrepared) && ServerDelayElapsed(_loadStartedAt, 5100);
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

        public void PreloadTrack(Track track)
        {
            if (!Utilities.IsValid(track) || !Utilities.IsValid(track.GetVRCUrl()) ||
                !track.GetUrl().IsValidUrl()) return;
            _activePlaylistIndex = -1;
            _playingTrackIndex = -1;
            BeginTrack(track, true, false);
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
            if (!_creatingShowTrack && _showMode)
            {
                NotifyPlaybackNotice(NoticeSourceChanged, ReasonSourceChanged);
                _showMode = false;
                _intervalPrepared = false;
                _intervalValidated = false;
                _pendingNetworkStart = false;
                RestoreShowSettings();
            }
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
            _intervalValidated = false;
            _intervalValidationFailed = false;
            _intervalPrepared = false;
            _intervalSeekPending = false;
            _appliedTrackRevision = _trackRevision;
            _stopped = phase == 1;
            Track = track;
            foreach (Listener listener in _listeners) listener.OnPlayerChanged();
            ResolveTrack.Invoke();
            foreach (Listener listener in _listeners) listener.OnUrlChanged();
        }

        public void RequestPlay()
        {
            if (_showMode && (!_authorizedShowStart || !Networking.IsOwner(gameObject))) return;
            if (!IsPreloading) { Paused = false; return; }
            // The fixed interval never starts playback automatically.
            if (!CanStartPreloaded) return;
            _loadPhase = 2;
            _localLoadPhase = 2;
            _paused = false;
            _stopped = false;
            SyncTime = _showMode && _hasStart ? _startTime : 0f;
            VideoPlayerHandle.StartLoaded();
            if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            if (!_showMode)
                foreach (Listener listener in _listeners) listener.OnVideoReady();
        }

        // SongClock-compatible notice kinds and reasons (kept numerically aligned).
        public const int NoticeReady = 1, NoticePlaybackStarted = 2, NoticeEnded = 3,
            NoticeStopped = 4, NoticeError = 5, NoticeSourceChanged = 6;
        public const int ReasonUnknown = 0, ReasonNaturalEnd = 1, ReasonIntervalEnd = 2,
            ReasonOperatorStop = 3, ReasonSourceChanged = 4, ReasonCleanup = 5,
            ReasonReset = 6, ReasonStartUnavailable = 7;
        public const int ErrorReasonLoad = 100, ErrorReasonSeek = 101,
            ErrorReasonPlayback = 102, ErrorReasonMaxRetry = 103,
            ErrorReasonResolverRejected = 104, ErrorReasonInvalidInterval = 105;

        public void PreloadIntervalTrack(Track track, int songIndex, int selectionRevision,
            bool hasStart, float startTime, bool hasEnd, float endTime)
        {
            if (!Networking.IsOwner(gameObject) || !Utilities.IsValid(track) ||
                !Utilities.IsValid(track.GetVRCUrl()) || !track.GetUrl().IsValidUrl()) return;
            if (!IsFinite(startTime) || !IsFinite(endTime) ||
                (hasStart && startTime < 0f) || (hasEnd && endTime < 0f))
            {
                NotifyPlaybackNotice(NoticeError, ErrorReasonInvalidInterval);
                return;
            }
            CaptureShowSettings();
            _showMode = true;
            _hasStart = hasStart;
            _startTime = hasStart ? startTime : 0f;
            _hasEnd = hasEnd;
            _endTime = endTime;
            _videoGeneration++;
            _songIndex = songIndex;
            _selectionRevision = selectionRevision;
            _runId = 0;
            _pendingNetworkStart = false;
            _endRecordWritten = false;
            _hasLastValidVideoTime = false;
            _lastValidVideoTime = 0f;
            _intervalValidationFailed = false;
            _endNoticeSequence = 0;
            _endGeneration = _videoGeneration;
            _endRunId = 0;
            _endKind = 0;
            _endReason = ReasonUnknown;
            _endPosition = 0f;
            _creatingShowTrack = true;
            _loop = false;
            _repeat = new Vector3(0f, 0f, 999999f);
            _forwardInterval = -1f;
            foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Loop = false;
            foreach (Listener listener in _listeners)
            {
                if (Utilities.IsValid(listener))
                {
                    listener.OnLoopChanged();
                    listener.OnRepeatChanged();
                }
            }
            _activePlaylistIndex = -1;
            _playingTrackIndex = -1;
            BeginTrack(track, true, false);
            _creatingShowTrack = false;
            if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
        }

        public bool TryRequestPlayback(int expectedGeneration, int runId)
        {
            if (!_showMode || expectedGeneration != _videoGeneration ||
                !Networking.IsOwner(gameObject) || !CanStartPreloaded) return false;
            _runId = runId;
            _pendingStopReason = ReasonUnknown;
            SyncTime = _hasStart ? _startTime : 0f;
            _authorizedShowStart = true;
            RequestPlay();
            _authorizedShowStart = false;
            return _localLoadPhase == 2;
        }

        public bool BindPlaybackRun(int expectedGeneration, int runId)
        {
            if (!_showMode || expectedGeneration != _videoGeneration || runId < 0) return false;
            _runId = runId;
            if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            return true;
        }

        public void RequestStopWithReason(int reason, int expectedGeneration, int runId)
        {
            if (!_showMode || expectedGeneration != _videoGeneration || runId != _runId ||
                !Networking.IsOwner(gameObject)) return;
            _pendingStopReason = reason;
            Stopped = true;
        }

        public bool IsStoppedForRun(int runId) => _stopped && _showMode && _runId == runId &&
            _endRunId == runId && _endNoticeSequence > 0;

        public bool TrySampleVideoTime(out double time)
        {
            time = 0d;
            if (!_showMode || !_intervalValidated || !_intervalPrepared ||
                _localLoadPhase == 0 || !VideoPlayerHandle.IsReady) return false;
            float value = VideoPlayerHandle.VideoTime;
            if (!IsFinite(value) || value < 0f || Duration <= 0f || IsLive) return false;
            time = value;
            _lastValidVideoTime = value;
            _hasLastValidVideoTime = true;
            return true;
        }

        bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        bool ServerDelayElapsed(int startedAt, int delayMilliseconds)
        {
            int elapsed = Networking.GetServerTimeInMilliseconds() - startedAt;
            return elapsed >= delayMilliseconds;
        }

        void NotifyPlaybackNotice(int kind, int reason)
        {
            _playbackNoticeSequence++;
            NotifyPlaybackNoticeWithSequence(kind, reason, _playbackNoticeSequence);
        }

        void NotifyPlaybackNoticeWithSequence(int kind, int reason, int sequence)
        {
            foreach (Listener listener in _listeners)
                if (Utilities.IsValid(listener)) listener.OnPlaybackNotice(kind, reason,
                    _videoGeneration, _songIndex, _selectionRevision, _runId, sequence);
        }

        void CaptureShowSettings()
        {
            if (_hasSavedShowSettings) return;
            _savedLoop = _loop;
            _savedRepeat = _repeat;
            _savedForwardInterval = _forwardInterval;
            _hasSavedShowSettings = true;
        }

        void RestoreShowSettings()
        {
            if (!_hasSavedShowSettings) return;
            if (_loop != _savedLoop) Loop = _savedLoop;
            _repeat = _savedRepeat;
            _forwardInterval = _savedForwardInterval;
            _hasSavedShowSettings = false;
        }

        void SaveEndRecord(int kind, int reason)
        {
            if (_endRecordWritten || !_showMode || (!_isLocal && !Networking.IsOwner(gameObject))) return;
            float position = _hasLastValidVideoTime ? _lastValidVideoTime : VideoPlayerHandle.VideoTime;
            if (!_stopped && IsFinite(VideoPlayerHandle.VideoTime)) position = VideoPlayerHandle.VideoTime;
            if (!IsFinite(position)) position = _hasStart ? _startTime : 0f;
            _endNoticeSequence = ++_playbackNoticeSequence;
            _endGeneration = _videoGeneration;
            _endRunId = _runId;
            _endKind = kind;
            _endReason = reason;
            _endPosition = position;
            _endRecordWritten = true;
            if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            NotifyPlaybackNoticeWithSequence(kind, reason, _endNoticeSequence);
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
            _localLoadAttempt++;
            foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.SetLoadAttempt(_localLoadAttempt);
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
