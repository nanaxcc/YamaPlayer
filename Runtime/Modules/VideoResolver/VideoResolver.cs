
using UdonSharp;
using UnityEngine;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
#if WEB_UNIT_INCLUDED
using Yamadev.YamachanWebUnit;
#endif

namespace Yamadev.YamaStream.Modules
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
#if WEB_UNIT_INCLUDED
    public class VideoResolver : Receiver
#else
    public class VideoResolver : UdonSharpBehaviour
#endif
    {
#if WEB_UNIT_INCLUDED
        [SerializeField] Client _client;
#else
        [SerializeField] UdonSharpBehaviour _client;
#endif
        [SerializeField] Controller _controller;
        [SerializeField] VRCUrl _callbackYoutubeUrl;
        [SerializeField] VRCUrl _callbackNiconicoUrl;
        VRCUrl _callbackUrl;
        int _requestRevision;
        bool _requestInFlight;
        bool _resolveQueued;
#if UNITY_ANDROID
        bool _isQuest = true;
#else
        bool _isQuest = false;
#endif

        void Start()
        {
#if WEB_UNIT_INCLUDED
            SendCustomEventDelayedFrames(nameof(SetHooks), 1);
#endif
        }

        public void SetHooks()
        {
            _controller.ResolveTrack = UdonEvent.New(this, nameof(ResolveTrack));
        }

        public void ResolveTrack()
        {
#if WEB_UNIT_INCLUDED
            if (_requestInFlight || _client.IsLoading)
            {
                _resolveQueued = true;
                SendCustomEventDelayedFrames(nameof(ContinueResolve), 1);
                return;
            }
#endif
            _resolveQueued = false;
            Track track = _controller.Track;
            if (!_isQuest)
            {
                if (track.GetUrl().StartsWith("https://www.nicovideo.jp") || track.GetUrl().StartsWith("https://nicovideo.jp"))
                {
                    PlayNicoVideo(track.GetUrl());
                    return;
                }
                string originalUrl = track.GetOriginalUrl();
                if (track.GetVRCUrl().Equals(VRCUrl.Empty) && originalUrl != string.Empty)
                {
                    if (originalUrl.StartsWith("https://www.youtube.com") || originalUrl.StartsWith("https://youtube.com")) PlayYoutubeVideo(originalUrl);
                    return;
                }
            }
            _controller.Resolve();
        }

#if WEB_UNIT_INCLUDED
        public override void OnRequestSuccess(IVRCStringDownload result)
        {
            if (!_requestInFlight) return;
            _requestInFlight = false;
            _controller.ResolveCompleted(_callbackUrl, _requestRevision);
            if (_resolveQueued) SendCustomEventDelayedFrames(nameof(ContinueResolve), 1);
        }

        public override void OnRequestError()
        {
            if (!_requestInFlight) return;
            _requestInFlight = false;
            if (_requestRevision == _controller.ResolveRevision)
                _controller.OnVideoError(VRC.SDK3.Components.Video.VideoError.PlayerError);
            if (_resolveQueued) SendCustomEventDelayedFrames(nameof(ContinueResolve), 1);
        }
#endif

        public void ContinueResolve()
        {
#if WEB_UNIT_INCLUDED
            if (_requestInFlight || !_resolveQueued) return;
            if (_client.IsLoading) { SendCustomEventDelayedFrames(nameof(ContinueResolve), 1); return; }
            if (string.IsNullOrEmpty(_controller.Track.GetUrl())) { _resolveQueued = false; return; }
            ResolveTrack();
#endif
        }

        public void PlayYoutubeVideo(string url)
        {
#if WEB_UNIT_INCLUDED
            Debug.Log($"[<color=#ff70ab>YamaStream</color>] Resolve youtube url: {url}");
            string id = url.Replace("https://youtube.com/watch?v=", "").Replace("https://www.youtube.com/watch?v=", "").Split('&')[0];
            _callbackUrl = _callbackYoutubeUrl;
            _requestRevision = _controller.ResolveRevision;
            _requestInFlight = true;
            if (!_client.Request(VRCUrl.Empty, id, this)) _requestInFlight = false;
#endif
        }

        public void PlayNicoVideo(string url)
        {
#if WEB_UNIT_INCLUDED
            Debug.Log($"[<color=#ff70ab>YamaStream</color>] Resolve niconico url: {url}");
            string id = url.Replace("https://nicovideo.jp/watch/", "").Replace("https://www.nicovideo.jp/watch/", "").Split('?')[0];
            _callbackUrl = _callbackNiconicoUrl;
            _requestRevision = _controller.ResolveRevision;
            _requestInFlight = true;
            if (!_client.Request(VRCUrl.Empty, id, this)) _requestInFlight = false;
#endif
        }
    }
}
