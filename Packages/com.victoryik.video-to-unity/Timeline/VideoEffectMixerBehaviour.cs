using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace VideoToUnity.Timeline
{
    /// <summary>Selects one clip per player, forwards media time, and owns Timeline cleanup.</summary>
    public sealed class VideoEffectMixerBehaviour : PlayableBehaviour
    {
        private static readonly Dictionary<VideoEffectPlayer, VideoEffectMixerBehaviour> Owners =
            new Dictionary<VideoEffectPlayer, VideoEffectMixerBehaviour>();

        internal static event Action<VideoEffectMixerBehaviour> PreviewRequested;
        internal static event Action<VideoEffectMixerBehaviour> PreviewReleased;

        private PlayableDirector director;
        private VideoEffectTrack track;
        private VideoEffectPlayer player;
        private VideoEffectAsset sampledAsset;
        private double sampledTime;
        private float sampledWeight;
        private float sampledSpeed;
        private bool sampledPlaying;
        private bool hasSample;
        private bool warnedOverlap;
        private bool warnedBinding;
        private bool warnedReverse;

        internal bool HasPreview => player != null && hasSample;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOwners() => Owners.Clear();

        internal void Initialize(PlayableDirector owner, VideoEffectTrack sourceTrack)
        {
            director = owner;
            track = sourceTrack;
            if (director == null)
                return;
            director.paused += OnDirectorPaused;
            director.stopped += OnDirectorStopped;
        }

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var binding = playerData as VideoEffectPlayer;
            if (player != binding)
                Release();
            if (binding == null || !binding.isActiveAndEnabled)
            {
                Release();
                return;
            }

            int selected = -1;
            int activeCount = 0;
            float selectedWeight = 0f;
            for (int i = 0; i < playable.GetInputCount(); i++)
            {
                float weight = playable.GetInputWeight(i);
                var input = playable.GetInput(i);
                if (!IsFinite(weight) || weight <= 0 || !input.IsValid())
                    continue;
                var clip = ((ScriptPlayable<VideoEffectBehaviour>)input).GetBehaviour().Clip;
                if (clip == null || clip.effect == null)
                    continue;
                activeCount++;
                // Highest input weight wins. Equal weights keep the lower mixer input index.
                if (weight > selectedWeight)
                {
                    selected = i;
                    selectedWeight = weight;
                }
            }

            if (activeCount > 1 && !warnedOverlap)
            {
                Debug.LogWarning("Video to Unity: 한 플레이어에서는 영상 클립을 블렌딩할 수 없습니다. " +
                    "가중치가 가장 큰 클립 하나만 표시하며, 같으면 트랙의 앞선 입력을 사용합니다. " +
                    "겹침을 제거하거나 각각 다른 플레이어에 연결한 트랙을 사용하세요.", track);
                warnedOverlap = true;
            }

            if (selected < 0 || !IsFinite(info.effectiveWeight) || info.effectiveWeight <= 0)
            {
                Release();
                return;
            }

            if (Owners.TryGetValue(binding, out var owner) && owner != this)
            {
                if (!warnedBinding)
                {
                    Debug.LogWarning("Video to Unity: 이미 다른 트랙이 이 플레이어를 제어하고 있습니다. " +
                        "트랙마다 별도 VideoEffectPlayer를 연결하세요. 먼저 제어한 트랙을 유지합니다.", track);
                    warnedBinding = true;
                }
                return;
            }

            var selectedInput = (ScriptPlayable<VideoEffectBehaviour>)playable.GetInput(selected);
            var behaviour = selectedInput.GetBehaviour();
            double time = selectedInput.GetTime();
            float speed = behaviour.EffectiveSpeed;
            if (double.IsNaN(time) || double.IsInfinity(time) || !IsFinite(speed))
            {
                Release();
                return;
            }

            bool playing = info.evaluationType == FrameData.EvaluationType.Playback &&
                info.effectivePlayState == PlayState.Playing && !info.timeHeld && speed > 0f;
            if (speed < 0f && !warnedReverse)
            {
                Debug.LogWarning("Video to Unity: 역방향 영상 재생은 지원하지 않습니다. " +
                    "역방향 Timeline은 정지 화면 탐색으로 처리합니다.", track);
                warnedReverse = true;
            }

            player = binding;
            Owners[player] = this;
            float opacity = IsFinite(behaviour.Clip.opacity) ? Mathf.Clamp01(behaviour.Clip.opacity) : 0f;
            float weightValue = Mathf.Clamp01(selectedWeight * info.effectiveWeight) * opacity;
            // GetTime already equals (directorTime - clip.start) * clip.timeScale + clip.clipIn.
            // Do NOT multiply by clip.timeScale a second time. Asset.speed is for standalone playback.
            Sample(behaviour.Clip.effect, Math.Max(0.0, time), weightValue, playing, Math.Abs(speed));
        }

        private void Sample(VideoEffectAsset asset, double time, float weight, bool playing, float speed)
        {
            bool changed = !hasSample || playing != sampledPlaying || asset != sampledAsset ||
                Math.Abs(time - sampledTime) >= 0.000001 || weight != sampledWeight || speed != sampledSpeed;

            sampledAsset = asset;
            sampledTime = time;
            sampledWeight = weight;
            sampledSpeed = speed;
            sampledPlaying = playing;
            hasSample = true;
            player.SampleTimeline(asset, time, weight, playing, speed);
            // Runtime coalesces asynchronous seeks. Forward repeated evaluations so a pending
            // seek can consume the newest target. The editor pump only updates/repaints the loop.
            if (!Application.isPlaying && (changed || playing))
                PreviewRequested?.Invoke(this);
        }

        private void PauseOwned()
        {
            if (player == null || !hasSample || !sampledPlaying ||
                !Owners.TryGetValue(player, out var owner) || owner != this)
                return;
            // Do not stop a new standalone source that replaced this Timeline's source.
            if (player.effect != sampledAsset)
            {
                Release();
                return;
            }
            Sample(sampledAsset, sampledTime, sampledWeight, false, sampledSpeed);
        }

        internal void Release()
        {
            // ReferenceEquals also lets us remove a destroyed Unity object from the dictionary.
            if (!ReferenceEquals(player, null) && Owners.TryGetValue(player, out var owner) && owner == this)
            {
                Owners.Remove(player);
                // Runtime's idempotent ReleaseTimeline also handles restored preview fields;
                // standalone Play/Prepare relinquish Timeline ownership in Runtime itself.
                if (player != null)
                    player.ReleaseTimeline();
            }
            player = null;
            sampledAsset = null;
            hasSample = false;
            sampledPlaying = false;
            PreviewReleased?.Invoke(this);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void OnDirectorPaused(PlayableDirector _) => PauseOwned();
        private void OnDirectorStopped(PlayableDirector _) => Release();

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            if (info.effectiveWeight <= 0)
                Release();
            else
                PauseOwned();
        }

        // GraphStop also occurs on pause: keep the displayed frame until Stop/destroy or a gap.
        public override void OnGraphStop(Playable playable) => PauseOwned();

        public override void OnPlayableDestroy(Playable playable)
        {
            Release();
            if (director != null)
            {
                director.paused -= OnDirectorPaused;
                director.stopped -= OnDirectorStopped;
            }
            director = null;
        }
    }
}
