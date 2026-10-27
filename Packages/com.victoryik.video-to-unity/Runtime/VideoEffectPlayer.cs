using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Video;

namespace VideoToUnity
{
    [Serializable] public sealed class VideoErrorEvent : UnityEvent<string> { }

    /// <summary>Owns one native decoder. Prepare ahead; Play restarts, Resume continues.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Video to Unity/Video Effect Player")]
    public sealed class VideoEffectPlayer : MonoBehaviour
    {
        public VideoEffectAsset effect;
        public bool playOnEnable;
        [Range(0, 1)] public float opacity = 1;
        [Min(1)] public float prepareTimeout = 30;
        public UnityEvent onPrepared = new UnityEvent();
        public UnityEvent onStarted = new UnityEvent();
        public UnityEvent onCompleted = new UnityEvent();
        public UnityEvent onLooped = new UnityEvent();
        public UnityEvent onStopped = new UnityEvent();
        public VideoErrorEvent onError = new VideoErrorEvent();

        private VideoPlayer decoder;
        private AudioSource audioSource;
        private GameObject ownedObject;
        private VideoEffectAsset loaded, failedEffect, beforeTimeline;
        private bool ready, preparing, visible, hasFrame, wantPlay, started, completed;
        private bool timeline, timelinePlaying, seekPending, seekQueued;
        private float timelineWeight = 1, timelineSpeed = 1;
        private double desiredTime, preparedAt, lastSeekAt, issuedSeekTime, queuedSeekTime, lastTimelineTime = double.NaN;
        private int generation;
        private VideoPlayer.EventHandler preparedHandler;
        private VideoPlayer.ErrorEventHandler errorHandler;
        private VideoPlayer.FrameReadyEventHandler frameHandler;
        private VideoPlayer.EventHandler endHandler, seekHandler;

        public VideoPlayer Decoder { get { EnsureDecoder(); return decoder; } }
        public Texture Texture { get { return visible && ready && hasFrame && decoder != null ? decoder.texture : null; } }
        public bool IsReady { get { return ready && decoder != null && decoder.isPrepared; } }
        public bool IsPlaying { get { return decoder != null && decoder.isPlaying && wantPlay; } }
        public string LastError { get; private set; }
        public double Duration { get { return decoder != null && ready ? decoder.length : effect != null && effect.clip != null ? effect.clip.length : 0; } }
        public double CurrentTime { get { return decoder != null && ready ? decoder.time : 0; } }
        public float EffectiveOpacity { get { return Mathf.Clamp01(opacity) * (timeline ? Mathf.Clamp01(timelineWeight) : 1); } }

        private void OnEnable() { if (Application.IsPlaying(gameObject) && playOnEnable) Play(); }
        private void OnDisable() { ReleaseTimeline(); StopInternal(false); }
        private void OnDestroy()
        {
            StopInternal(false);
            if (ownedObject != null)
            {
                if (Application.isPlaying) Destroy(ownedObject); else DestroyImmediate(ownedObject);
            }
        }

        private void Update()
        {
            if (preparing && Time.realtimeSinceStartupAsDouble - preparedAt > Mathf.Max(1, prepareTimeout))
                Fail("Preparing the video timed out. Check the codec, URL and target platform.");
            if (seekPending && Time.realtimeSinceStartupAsDouble - lastSeekAt > Mathf.Max(1, prepareTimeout))
                Fail("Seeking the video timed out. Check the source and target platform.");
            UpdateAudio();
        }

        private void EnsureDecoder()
        {
            if (decoder != null) return;
            ownedObject = new GameObject("VideoToUnity Decoder");
            ownedObject.hideFlags = HideFlags.HideAndDontSave;
            ownedObject.transform.SetParent(transform, false);
            decoder = ownedObject.AddComponent<VideoPlayer>();
            decoder.playOnAwake = false;
            decoder.renderMode = VideoRenderMode.APIOnly;
            decoder.audioOutputMode = VideoAudioOutputMode.None;
            decoder.waitForFirstFrame = true;
            decoder.skipOnDrop = true;
            decoder.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            audioSource = ownedObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0;
        }

        public void Prepare(VideoEffectAsset asset)
        {
            if (!isActiveAndEnabled) return;
            timeline = false; failedEffect = null; effect = asset; wantPlay = false; visible = false;
            desiredTime = 0;
            Configure(asset);
        }

        public void Play() { Play(effect); }
        public void Play(VideoEffectAsset asset)
        {
            if (!isActiveAndEnabled) return;
            timeline = false; failedEffect = null; effect = asset;
            wantPlay = true; visible = true; started = false; completed = false; desiredTime = 0;
            if (loaded == asset && IsReady && decoder.canSetTime)
            {
                hasFrame = false;
                RequestSeek(0);
                decoder.Play(); return;
            }
            Configure(asset);
        }

        public void Pause()
        {
            wantPlay = false;
            if (decoder != null && ready) decoder.Pause();
        }
        public void Resume()
        {
            if (!isActiveAndEnabled) return;
            if (!ready) { Play(); return; }
            wantPlay = true; visible = true; completed = false;
            decoder.Play();
        }
        public void Stop() { timeline = false; failedEffect = null; StopInternal(true); }

        public void Seek(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            desiredTime = Math.Max(0, seconds);
            if (IsReady) RequestSeek(desiredTime);
        }

        private void Configure(VideoEffectAsset asset)
        {
            // Keep the caller's playback intent while replacing only our own decoder state.
            Detach(); generation++; EnsureDecoder(); decoder.Stop();
            ready = preparing = hasFrame = seekPending = seekQueued = false;
            loaded = asset; LastError = null; completed = false; lastTimelineTime = double.NaN;
            string reason = "Assign a VideoEffectAsset.";
            if (asset == null || !asset.Validate(out reason)) { Fail(reason); return; }
            decoder.source = asset.source == VideoSourceKind.VideoClip ? VideoSource.VideoClip : VideoSource.Url;
            if (decoder.source == VideoSource.VideoClip) { decoder.url = ""; decoder.clip = asset.clip; }
            else { decoder.clip = null; decoder.url = asset.ResolveUrl(); }
            decoder.isLooping = asset.loop && !timeline;
            decoder.audioOutputMode = asset.audioEnabled ? VideoAudioOutputMode.AudioSource : VideoAudioOutputMode.None;
            if (asset.audioEnabled)
            {
                decoder.controlledAudioTrackCount = 1;
                decoder.EnableAudioTrack(0, true);
                decoder.SetTargetAudioSource(0, audioSource);
            }
            int request = generation;
            preparedHandler = p =>
            {
                if (request != generation || loaded != asset || !isActiveAndEnabled) return;
                ready = true; preparing = false;
                SetSpeed();
                if (desiredTime > 0 && p.canSetTime) RequestSeek(desiredTime);
                onPrepared.Invoke();
                if (request != generation || !isActiveAndEnabled) return; // callbacks may Stop or replace the clip
                if (wantPlay || timeline) p.Play();
            };
            frameHandler = (p, index) =>
            {
                if (request != generation || !ready) return;
                hasFrame = true;
                p.sendFrameReadyEvents = false;
                if (wantPlay && !started)
                {
                    started = true; onStarted.Invoke();
                    if (request != generation) return;
                }
                if (timeline && !timelinePlaying) p.Pause();
            };
            seekHandler = p =>
            {
                if (request != generation) return;
                seekPending = false;
                if (seekQueued)
                {
                    // Consume the newest request even when no further Timeline evaluation follows.
                    // Compare requested targets, not quantized frame timestamps: non-frame-aligned
                    // times must not cause an endless seek/retry loop.
                    double latest = queuedSeekTime;
                    seekQueued = false;
                    RequestSeek(latest);
                    return;
                }
                if ((timeline && !timelinePlaying) || !wantPlay) p.Pause();
            };
            endHandler = p =>
            {
                if (request != generation || !ready) return;
                if (asset.loop && !timeline) { onLooped.Invoke(); return; }
                if (timeline) { p.Pause(); return; }
                if (completed) return;
                completed = true; wantPlay = false; visible = asset.holdLastFrame;
                onCompleted.Invoke();
            };
            errorHandler = (p, message) => { if (request == generation) Fail(message); };
            decoder.prepareCompleted += preparedHandler;
            decoder.frameReady += frameHandler;
            decoder.loopPointReached += endHandler;
            decoder.seekCompleted += seekHandler;
            decoder.errorReceived += errorHandler;
            decoder.sendFrameReadyEvents = true;
            preparing = true; preparedAt = Time.realtimeSinceStartupAsDouble;
            try { decoder.Prepare(); }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private void SetSpeed()
        {
            if (decoder != null && loaded != null && decoder.canSetPlaybackSpeed)
                decoder.playbackSpeed = Mathf.Clamp(timeline ? timelineSpeed : loaded.speed, 0.05f, 4);
        }

        private double ClampTime(double seconds)
        {
            double duration = Duration;
            double frame = decoder != null && decoder.frameRate > 0 ? 1.0 / decoder.frameRate : 1.0 / 30;
            return duration > 0 ? Math.Max(0, Math.Min(seconds, Math.Max(0, duration - frame))) : Math.Max(0, seconds);
        }

        private void RequestSeek(double seconds)
        {
            if (!IsReady || !decoder.canSetTime) return;
            seconds = ClampTime(seconds);
            if (seekPending)
            {
                queuedSeekTime = seconds;
                seekQueued = Math.Abs(seconds - issuedSeekTime) > 0.000001;
                return;
            }
            seekPending = true; seekQueued = false;
            lastSeekAt = Time.realtimeSinceStartupAsDouble; issuedSeekTime = seconds;
            decoder.sendFrameReadyEvents = true;
            decoder.time = seconds;
        }

        /// <summary>Timeline evaluates time; the native decoder runs normally between discontinuities.</summary>
        public void SampleTimeline(VideoEffectAsset asset, double time, float weight, bool playing, float speed = 1)
        {
            if (!isActiveAndEnabled || asset == null || double.IsNaN(time) || double.IsInfinity(time)) return;
            if (!timeline) { beforeTimeline = effect; timeline = true; }
            timelineWeight = Mathf.Clamp01(weight);
            timelinePlaying = playing; timelineSpeed = speed;
            effect = asset; desiredTime = Math.Max(0, time); wantPlay = playing; visible = true;
            if (failedEffect == asset) return;
            if (loaded != asset || (!ready && !preparing))
            {
                started = false; Configure(asset); return;
            }
            if (!ready) return;
            decoder.isLooping = false;
            SetSpeed();
            double target = ClampTime(desiredTime);
            double tolerance = Math.Max(0.12, decoder.frameRate > 0 ? 3 / decoder.frameRate : 0.12);
            bool moved = double.IsNaN(lastTimelineTime) || Math.Abs(target - lastTimelineTime) > 0.0005;
            if ((!playing && moved) || (playing && Math.Abs(decoder.time - target) > tolerance))
            {
                if (seekPending || !playing || Time.realtimeSinceStartupAsDouble - lastSeekAt > 0.08)
                { RequestSeek(target); lastTimelineTime = target; }
            }
            else if (!seekPending) lastTimelineTime = target;
            if (playing) { if (!decoder.isPlaying) decoder.Play(); }
            else if (hasFrame) decoder.Pause();
            else if (!decoder.isPlaying) decoder.Play(); // Decode first frame, then frameReady pauses.
            UpdateAudio();
        }

        public void ReleaseTimeline()
        {
            if (!timeline) return;
            timeline = false; timelineWeight = 1;
            StopInternal(false); effect = beforeTimeline; beforeTimeline = null; failedEffect = null;
        }

        private void UpdateAudio()
        {
            if (audioSource != null)
                audioSource.volume = loaded != null && visible ? Mathf.Clamp01(loaded.opacity) * EffectiveOpacity : 0;
        }

        private void Fail(string message)
        {
            failedEffect = loaded;
            LastError = string.IsNullOrWhiteSpace(message) ? "Video playback failed." : message;
            Detach(); generation++;
            if (decoder != null) decoder.Stop();
            ready = preparing = hasFrame = wantPlay = visible = seekPending = seekQueued = false;
            onError.Invoke(LastError);
        }

        private void StopInternal(bool notify)
        {
            bool active = preparing || ready || wantPlay;
            Detach(); generation++;
            if (decoder != null) decoder.Stop();
            if (audioSource != null) audioSource.Stop();
            loaded = null; ready = preparing = hasFrame = visible = wantPlay = seekPending = seekQueued = false;
            started = completed = false; timelineWeight = 1;
            if (notify && active) onStopped.Invoke();
        }

        private void Detach()
        {
            if (decoder == null) return;
            if (preparedHandler != null) decoder.prepareCompleted -= preparedHandler;
            if (frameHandler != null) decoder.frameReady -= frameHandler;
            if (endHandler != null) decoder.loopPointReached -= endHandler;
            if (errorHandler != null) decoder.errorReceived -= errorHandler;
            if (seekHandler != null) decoder.seekCompleted -= seekHandler;
            preparedHandler = endHandler = seekHandler = null; frameHandler = null; errorHandler = null;
        }
    }
}
