
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDKBase;

#if AUDIOLINK_V1
using AudioLink;
#endif

namespace Yamadev.YamaStream
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public partial class Controller : Listener
    {
        [SerializeField] Animator _videoPlayerAnimator;
        [SerializeField] VideoPlayerHandle[] _videoPlayerHandles;
        [SerializeField] Permission _permission;
        [SerializeField] float _retryAfterSeconds = 5.1f;
        [SerializeField] int _maxErrorRetry = 5;
        [SerializeField] string _timeFormat = @"hh\:mm\:ss";
        [SerializeField] bool _isLocal;
        [SerializeField] string _version;
        [SerializeField] bool _useFallbackHandler;
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(VideoPlayerType))] VideoPlayerType _videoPlayerType;
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(Loop))] bool _loop;
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(SlideMode))] bool _slideMode;
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(SlideSeconds))] int _slideSeconds = 1;
        [UdonSynced, FieldChangeCallback(nameof(Paused))] bool _paused;
        [UdonSynced, FieldChangeCallback(nameof(Stopped))] bool _stopped = true;
        [UdonSynced, FieldChangeCallback(nameof(Speed))] float _speed = 1f;
        [UdonSynced, FieldChangeCallback(nameof(Repeat))] Vector3 _repeat = new Vector3(0f, 0f, 999999f);
        Listener[] _listeners = { };
        int _errorRetryCount = 0;
        VRCUrl _retryTargetUrl = VRCUrl.Empty;
        float _retryNotBefore;
        bool _isReload;
        float _lastSetTime = 0f;
        float _repeatCooling = 0.6f;
        bool _initialized;
        bool _allowShowSeek;
        bool _pendingPlaybackStartedNotice;
        float _nextIntervalSeekCheck;
        int _observedEndNoticeSequence;

        void Start() => initialize();

        void Update()
        {
            PumpVideoRequest();
            UpdateShowPlayback();
            if (_showMode)
            {
                double observedTime;
                TrySampleVideoTime(out observedTime);
            }
            if (_showMode && Networking.IsOwner(gameObject) && _hasEnd && IsPlaying &&
                _videoGeneration == _endGeneration && TrySampleVideoTime(out double intervalTime) &&
                intervalTime >= _endTime)
                RequestStopWithReason(ReasonIntervalEnd, _videoGeneration, _runId);
            if (!_showMode && OutOfRepeat(VideoTime) && Time.time - _lastSetTime > _repeatCooling)
                SetTime(Repeat.ToRepeatStatus().GetStartTime());
            if (IsPlaying && Time.time - _syncFrequency > _lastSync) DoSync();
        }

        public Permission Permission => _permission;

        public string Version => _version;

        public PlayerPermission PlayerPermission => _permission == null ? PlayerPermission.Editor : _permission.PlayerPermission;

        void initialize()
        {
            if (_initialized) return;
            Loop = _loop;
            _videoPlayerAnimator.Rebind();
            initializeScreen();
            UpdateAudio();
            UpdateAudioLink();
            foreach (VideoPlayerHandle handle in _videoPlayerHandles)
                handle.Listener = this;
            _initialized = true;
        }

        public void AddListener(Listener listener)
        {
            if (Array.IndexOf(_listeners, listener) >= 0) return;
            _listeners = _listeners.Add(listener);
        }

        public bool IsLocal => _isLocal;

        public VideoPlayerType VideoPlayerType
        {
            get => _videoPlayerType;
            set
            {
                if (_videoPlayerType == value) return;
                if (!_isLocal && !Networking.IsOwner(gameObject))
                { _videoPlayerType = value; return; }
                if (!_changingTrack)
                {
                    CancelPendingVideo();
                    _loadPhase = 0;
                    _trackRevision++;
                }
                if (_showMode) _pendingStopReason = ReasonSourceChanged;
                VideoPlayerHandle.Stop();
                if (!string.IsNullOrEmpty(Track.GetUrl())) OnVideoStop();
                if (_showMode)
                {
                    _showMode = false;
                    _intervalPrepared = false;
                    _intervalValidated = false;
                    _pendingNetworkStart = false;
                    RestoreShowSettings();
                }
                _localLoadPhase = 0;
                _videoPlayerType = value;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnPlayerChanged();
                PrintLog($"Video player change to {_videoPlayerType}.");
            }
        }

        public VideoPlayerHandle VideoPlayerHandle
        {
            get
            {
                foreach (VideoPlayerHandle handle in _videoPlayerHandles)
                    if (handle.VideoPlayerType == _videoPlayerType) return handle;
                return null;
            }
        }

        public bool Paused
        {
            get => _paused;
            set
            {
                if (!_isLocal && !Networking.IsOwner(gameObject))
                { _paused = value; return; }
                if (IsPreloading) { _paused = value; return; }
                _paused = value;
                if (_paused) VideoPlayerHandle.Pause();
                else
                {
                    if (_localLoadPhase == 2) VideoPlayerHandle.StartLoaded();
                    VideoPlayerHandle.Play();
                }
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaPlaying(_paused ? MediaPlaying.Paused : IsLive ? MediaPlaying.Streaming : MediaPlaying.Playing);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    SyncTime = VideoTime - VideoStandardDelay;
                    RequestSerialization();
                }
            }
        }

        public bool Stopped
        {
            get => _stopped;
            set
            {
                _stopped = value;
                _isReload = false;
                if (!_isLocal && !Networking.IsOwner(gameObject)) return;
                if (_stopped)
                {
                    if (_showMode && _pendingStopReason == ReasonUnknown)
                        _pendingStopReason = ReasonOperatorStop;
                    CancelPendingVideo();
                    VideoPlayerHandle.Stop();
                    if (!string.IsNullOrEmpty(Track.GetUrl())) OnVideoStop();
                }
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            }
        }

        public bool SlideMode
        {
            get => _slideMode;
            set
            {
                _slideMode = value;
                if (!_paused) Paused = true;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnSlideModeChanged();
                PrintLog($"Slide mode changed {_slideMode}.");
            }
        }

        public int SlideSeconds
        {
            get => _slideSeconds;
            set
            {
                _slideSeconds = value;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnSlideModeChanged();
                PrintLog($"Slide seconds changed to {_slideSeconds}.");
            }
        }

        public int SlidePage => _slideMode && !_stopped ? Mathf.FloorToInt(VideoTime) / _slideSeconds + 1 : 0;

        public int SlidePageCount => _slideMode ? Mathf.FloorToInt(Duration) / _slideSeconds : 0;

        public bool Loop
        {
            get => _loop;
            set
            {
                if (_showMode && value) value = false;
                _loop = value;
                foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Loop = _loop;
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaLoop(_loop ? MediaLoop.LoopOne : MediaLoop.None);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnLoopChanged();
                PrintLog($"Loop changed {_loop}.");
            }
        }

        public void UpdateSpeed()
        {
            _videoPlayerAnimator.SetFloat("Speed", _speed);
            _videoPlayerAnimator.Update(0f);
            if (!_stopped && _videoPlayerType == VideoPlayerType.AVProVideoPlayer && !VideoPlayerHandle.UseFallbackHandle)
                SendCustomEventDelayedFrames(nameof(Reload), 1);
            UpdateAudio();
        }

        public float Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                UpdateSpeed();
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    SyncTime = VideoTime - VideoStandardDelay;
                    RequestSerialization();
                }
                foreach (Listener listener in _listeners) listener.OnSpeedChanged();
                PrintLog($"Speed changed {_speed:F2}x.");
            }
        }

        public bool OutOfRepeat(float targetTime)
        {
            if (!IsPlaying || !Repeat.ToRepeatStatus().IsOn()) return false;
            return targetTime > Repeat.ToRepeatStatus().GetEndTime() || targetTime < Repeat.ToRepeatStatus().GetStartTime();

        }

        public Vector3 Repeat
        {
            get => _repeat;
            set
            {
                if (_showMode && value.ToRepeatStatus().IsOn())
                    value = new Vector3(0f, 0f, 999999f);
                _repeat = value;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnRepeatChanged();
                RepeatStatus status = _repeat.ToRepeatStatus();
                if (status.IsOn()) PrintLog($"Repeat on, start: {status.GetStartTime()}, end: {status.GetEndTime()}.");
                else PrintLog($"Repeat off.");
            }
        }

        public float LastLoaded => VideoPlayerHandle.LastLoaded;
        public bool IsPlaying => VideoPlayerHandle.IsPlaying;
        public float Duration => VideoPlayerHandle.Duration;
        public float VideoTime => VideoPlayerHandle.VideoTime;
        public bool IsLoading => _pendingVideoRequest || VideoPlayerHandle.IsLoading;
        public bool IsReload => _isReload;
        public bool IsLive => float.IsInfinity(Duration);

        public void Reload()
        {
            if (!IsPreloading && !Stopped && !IsLoading) PlayTrack(Track, true);
        }

        public void ErrorRetry()
        {
            if (Time.time < _retryNotBefore) return;
            var currentUrl = Track.GetVRCUrl();

            if (VRCUrl.IsNullOrEmpty(_retryTargetUrl) || _retryTargetUrl != currentUrl)
            {
                _errorRetryCount = 0;
                _retryTargetUrl = VRCUrl.Empty;
                PrintLog("Retry cancelled: track has changed.");
                return;
            }

            if (IsPlaying || !currentUrl.Get().IsValidUrl())
            {
                _retryTargetUrl = VRCUrl.Empty;
                return;
            }

            ResolveTrack.Invoke();
            foreach (Listener listener in _listeners) listener.OnVideoRetry();
        }

        void HandleErrorRetry(VideoError videoError)
        {
            switch (videoError)
            {
                case VideoError.AccessDenied:
                    PrintLog("Access denied - no retry will be attempted");
                    _errorRetryCount = 0;
                    _retryTargetUrl = VRCUrl.Empty;
                    return;
                case VideoError.InvalidURL:
                    PrintLog("Invalid URL - no retry will be attempted");
                    _errorRetryCount = 0;
                    _retryTargetUrl = VRCUrl.Empty;
                    return;
                case VideoError.PlayerError:
                    if (_errorRetryCount == 0)
                    {
                        if (_useFallbackHandler && Utilities.IsValid(VideoPlayerHandle.FallbackHandle))
                        {
                            VideoPlayerHandle.UseFallbackHandle = true;
                            PrintLog($"Switching to fallback handler: {VideoPlayerHandle.FallbackHandle.VideoPlayerType}");
                        }
                    }
                    else
                    {
                        VideoPlayerHandle.UseFallbackHandle = false;
                    }
                    break;
            }

            if (_errorRetryCount < _maxErrorRetry)
            {
                _errorRetryCount++;
                _retryNotBefore = Time.time + _retryAfterSeconds;
                _retryTargetUrl = Track.GetVRCUrl();
                PrintLog($"Scheduling retry {_errorRetryCount}/{_maxErrorRetry} in {_retryAfterSeconds} seconds");
                SendCustomEventDelayedSeconds(nameof(ErrorRetry), _retryAfterSeconds);
            }
            else
            {
                _errorRetryCount = 0;
                _retryTargetUrl = VRCUrl.Empty;
                PrintLog($"Maximum retry count ({_maxErrorRetry}) reached. Stopping retry attempts.");
                if (_showMode) NotifyPlaybackNotice(NoticeError, ErrorReasonMaxRetry);
            }
        }

        public void OnResolverRequestRejected(int resolveRevision)
        {
            if (resolveRevision != ResolveRevision || !_showMode) return;
            NotifyPlaybackNotice(NoticeError, ErrorReasonResolverRejected);
        }

        public void SetPage(int page)
        {
            if (!_slideMode || page < 1 || page > SlidePageCount) return;
            SetTime(page * _slideSeconds - 0.5f);
        }

        public void SetTime(float time)
        {
            if (IsLive || OutOfRepeat(time) || (_showMode && !_intervalPrepared && !_allowShowSeek)) return;
            VideoPlayerHandle.VideoTime = time;
            _lastSetTime = Time.time;
            if (Networking.IsOwner(gameObject) && !_isLocal)
            {
                SyncTime = time - VideoStandardDelay;
                RequestSerialization();
            }
            foreach (Listener listener in _listeners) listener.OnSetTime(time);
            PrintLog($"{_videoPlayerType}: Set video time: {time}.");
        }

        public void SendCustomVideoEvent(string eventName)
        {
            foreach (Listener listener in _listeners)
                if (Utilities.IsValid(listener)) listener.SendCustomEvent(eventName);
        }

        public override void OnDeserialization()
        {
            initialize();
            if (_showMode)
            {
                CaptureShowSettings();
                _loop = false;
                _repeat = new Vector3(0f, 0f, 999999f);
                _forwardInterval = -1f;
                foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Loop = false;
                foreach (Listener listener in _listeners) listener.OnLoopChanged();
                foreach (Listener listener in _listeners) listener.OnRepeatChanged();
            }
            else RestoreShowSettings();
            Track track = Track.New(_targetPlayer, _title, _url, _originalUrl);
            foreach (Listener listener in _listeners) listener.OnTrackSynced(track.GetUrl());
            bool stopped = _stopped;
            bool paused = _paused;
            if (_appliedTrackRevision != _trackRevision || track.GetUrl() != Track.GetUrl())
            {
                if (!string.IsNullOrEmpty(track.GetUrl())) StartTrackLocal(track, _loadPhase);
                else
                {
                    _changingTrack = true;
                    foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Stop();
                    _changingTrack = false;
                    CancelPendingVideo();
                    _localLoadPhase = 0;
                    _appliedTrackRevision = _trackRevision;
                    Track = track;
                }
            }
            _localLoadPhase = _loadPhase;
            _stopped = stopped;
            _paused = paused;
            if (_showMode && _localLoadPhase == 2)
            {
                _pendingNetworkStart = true;
                _pendingPlaybackStartedNotice = false;
            }
            else if (_localLoadPhase == 2)
            {
                if (paused) VideoPlayerHandle.Pause();
                else { VideoPlayerHandle.StartLoaded(); VideoPlayerHandle.Play(); }
            }
            else if (_localLoadPhase == 0)
            {
                if (stopped) VideoPlayerHandle.Stop();
                else if (paused) VideoPlayerHandle.Pause();
                else VideoPlayerHandle.Play();
            }
            if (!_showMode)
                foreach (Listener listener in _listeners) listener.OnVideoReady();
            if (_endNoticeSequence > 0 && _endNoticeSequence != _observedEndNoticeSequence &&
                _endGeneration == _videoGeneration)
            {
                _observedEndNoticeSequence = _endNoticeSequence;
                NotifyPlaybackNoticeWithSequence(_endKind, _endReason, _endNoticeSequence);
            }
            if (!_showMode) DoSync(true);
            GenerateDynamicPlaylists();
        }

        #region Video Event
        public override void OnVideoReady()
        {
            if (_showMode)
            {
                BeginIntervalPreparation();
                return;
            }
            if (_localLoadPhase == 2 && !_paused) VideoPlayerHandle.StartLoaded();
            if (!_showMode)
                foreach (Listener listener in _listeners) listener.OnVideoReady();
            PrintLog($"{_videoPlayerType}: Video ready.");
        }

        public override void OnVideoStart()
        {
            if (IsPreloading) { VideoPlayerHandle.Pause(); return; }
            _errorRetryCount = 0;
            _retryTargetUrl = VRCUrl.Empty;
            _stopped = false;
            if (_paused || _slideMode) VideoPlayerHandle.Pause();
            else VideoPlayerHandle.Play();
            UpdateAudio();
#if AUDIOLINK_V1
            if (_audioLink != null && _useAudioLink)
                _audioLink.SetMediaPlaying(IsLive ? MediaPlaying.Streaming : MediaPlaying.Playing);
#endif
            if (Networking.IsOwner(gameObject) && !_isLocal && !_isReload && _localLoadPhase != 2)
            {
                SyncTime = 0f;
                RequestSerialization();
            }
            else DoSync();
            if (KaraokeMode != KaraokeMode.None) SendCustomEventDelayedSeconds(nameof(ForceSync), 1f);
            foreach (Listener listener in _listeners) listener.OnVideoStart();
            if (_showMode)
            {
                if (TrySampleVideoTime(out double startedAt) &&
                    (!_hasStart || startedAt >= _startTime - 0.5d))
                    NotifyPlaybackNotice(NoticePlaybackStarted, ReasonUnknown);
                else _pendingPlaybackStartedNotice = true;
            }
            PrintLog($"{_videoPlayerType}: Video start.");
            _isReload = false;
        }

        public override void OnVideoPlay()
        {
            _paused = false;
            if (KaraokeMode != KaraokeMode.None) SendCustomEventDelayedSeconds(nameof(ForceSync), 1f);
            foreach (Listener listener in _listeners) listener.OnVideoPlay();
            PrintLog($"{_videoPlayerType}: Video play.");
        }

        public override void OnVideoPause()
        {
            _paused = true;
            foreach (Listener listener in _listeners) listener.OnVideoPause();
            PrintLog($"{_videoPlayerType}: Video pause.");
        }

        public override void OnVideoStop()
        {
            if (_changingTrack) return;
            if (_showMode)
            {
                int reason = _pendingStopReason == ReasonUnknown ? ReasonOperatorStop : _pendingStopReason;
                int kind = reason == ReasonNaturalEnd || reason == ReasonIntervalEnd ? NoticeEnded :
                    reason == ReasonSourceChanged ? NoticeSourceChanged : NoticeStopped;
                SaveEndRecord(kind, reason);
                _pendingStopReason = ReasonUnknown;
            }
            bool wasPreloading = IsPreloading;
            CancelPendingVideo();
            _loadPhase = 0;
            _localLoadPhase = 0;
            if (Networking.IsOwner(gameObject)) _trackRevision++;
            if (!_isReload)
            {
                _paused = false;
                _stopped = true;
                _errorRetryCount = 0;
                _retryTargetUrl = VRCUrl.Empty;
                _repeat = new Vector3(0f, 0f, 999999f);
                VideoPlayerHandle.UseFallbackHandle = false;
                if (!wasPreloading && !string.IsNullOrEmpty(Track.GetUrl())) _history.AddTrack(Track);
                Track = Track.New(_videoPlayerType, string.Empty, VRCUrl.Empty);
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaPlaying(MediaPlaying.Stopped);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    ClearSync();
                    RequestSerialization();
                }
            }
            foreach (Listener listener in _listeners) listener.OnVideoStop();
            PrintLog($"{_videoPlayerType}: Video stop.");
        }

        public override void OnVideoLoop()
        {
            if (Networking.IsOwner(gameObject) && !_isLocal)
            {
                SyncTime = 0f;
                RequestSerialization();
            }
            foreach (Listener listener in _listeners) listener.OnVideoLoop();
            PrintLog($"{_videoPlayerType}: Video loop.");
        }

        public override void OnVideoEnd()
        {
            if (_showMode)
            {
                if (Networking.IsOwner(gameObject) || _isLocal)
                {
                    _pendingStopReason = ReasonNaturalEnd;
                    SaveEndRecord(NoticeEnded, ReasonNaturalEnd);
                }
                else NotifyPlaybackNotice(NoticeEnded, ReasonNaturalEnd);
            }
            if (!_showMode && Networking.IsOwner(gameObject) && !_isLocal && _forwardInterval >= 0)
            {
                _forwardScheduledGeneration = _videoGeneration;
                SendCustomEventDelayedSeconds(nameof(RunForward), _forwardInterval);
            }
            foreach (Listener listener in _listeners) listener.OnVideoEnd();
            PrintLog($"{_videoPlayerType}: Video end.");
        }

        public override void OnVideoError(VideoError videoError)
        {
            PrintLog($"{_videoPlayerType}: Video error {videoError}.");
#if AUDIOLINK_V1
            if (_audioLink != null && _useAudioLink)
                _audioLink.SetMediaPlaying(MediaPlaying.Error);
#endif
            HandleErrorRetry(videoError);
            foreach (Listener listener in _listeners) listener.OnVideoError(videoError);
            if (_showMode) NotifyPlaybackNotice(NoticeError,
                _localLoadPhase == 1 ? ErrorReasonLoad : ErrorReasonPlayback);
        }

        public override bool IsPlaybackHandleEventCurrent(int playerType, int loadAttempt)
        {
            if (loadAttempt != _localLoadAttempt) return false;
            VideoPlayerHandle active = VideoPlayerHandle;
            if (active != null && active.UseFallbackHandle)
                return active.FallbackHandle != null && playerType == (int)active.FallbackHandle.VideoPlayerType;
            return playerType == (int)_videoPlayerType;
        }

        void BeginIntervalPreparation()
        {
            if (!_showMode || !VideoPlayerHandle.IsReady) return;
            float duration = Duration;
            if (IsLive)
            {
                _intervalValidationFailed = true;
                NotifyPlaybackNotice(NoticeError, ErrorReasonInvalidInterval);
                return;
            }
            if (!IsFinite(duration) || duration <= 0f) return;
            float start = _hasStart ? _startTime : 0f;
            if (!IsFinite(start) || start < 0f || start >= duration ||
                (_hasEnd && (!IsFinite(_endTime) || _endTime <= start || _endTime > duration)))
            {
                _intervalValidated = false;
                _intervalPrepared = false;
                _intervalValidationFailed = true;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                NotifyPlaybackNotice(NoticeError, ErrorReasonInvalidInterval);
                return;
            }
            _intervalValidated = true;
            _intervalPrepared = false;
            _intervalSeekPending = true;
            _intervalSeekStartedAt = Time.time;
            _nextIntervalSeekCheck = Time.time;
            _allowShowSeek = true;
            VideoPlayerHandle.VideoTime = start;
            _allowShowSeek = false;
        }

        void UpdateShowPlayback()
        {
            if (!_showMode) return;
            if (!_intervalSeekPending && VideoPlayerHandle.IsReady && !_intervalValidated)
            {
                BeginIntervalPreparation();
                return;
            }
            if (_pendingNetworkStart && _intervalPrepared && _localLoadPhase == 2)
            {
                DoSync(true);
                VideoPlayerHandle.StartLoaded();
                _pendingNetworkStart = false;
            }
            if (_pendingPlaybackStartedNotice && IsPlaying &&
                TrySampleVideoTime(out double playbackTime) &&
                (!_hasStart || playbackTime >= _startTime - 0.5d))
            {
                _pendingPlaybackStartedNotice = false;
                NotifyPlaybackNotice(NoticePlaybackStarted, ReasonUnknown);
            }
            if (!_intervalSeekPending || Time.time < _nextIntervalSeekCheck) return;
            _nextIntervalSeekCheck = Time.time + 0.1f;
            float start = _hasStart ? _startTime : 0f;
            float time = VideoPlayerHandle.VideoTime;
            if (IsFinite(time) && Mathf.Abs(time - start) <= 0.5f)
            {
                _intervalSeekPending = false;
                _intervalPrepared = true;
                NotifyPlaybackNotice(NoticeReady, ReasonUnknown);
                foreach (Listener listener in _listeners)
                    if (Utilities.IsValid(listener)) listener.OnVideoReady();
                if (_pendingNetworkStart && _localLoadPhase == 2)
                {
                    DoSync(true);
                    VideoPlayerHandle.StartLoaded();
                    _pendingNetworkStart = false;
                }
                return;
            }
            if (Time.time - _intervalSeekStartedAt >= 5f)
            {
                _intervalSeekPending = false;
                _intervalPrepared = false;
                NotifyPlaybackNotice(NoticeError, ErrorReasonSeek);
            }
        }
        #endregion
    }
}
