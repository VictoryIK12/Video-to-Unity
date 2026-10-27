using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace VideoToUnity.Timeline
{
    /// <summary>A reference to a reusable effect; decoding belongs to the bound player.</summary>
    [Serializable]
    public sealed class VideoEffectClip : PlayableAsset, ITimelineClipAsset
    {
        // A public asset reference also enables Timeline's built-in asset drag-and-drop.
        [Tooltip("재생할 Video Effect Asset을 여기에 넣으세요.")]
        public VideoEffectAsset effect;

        [Range(0f, 1f), Tooltip("이 클립에만 적용할 불투명도입니다.")]
        public float opacity = 1f;

        // One decoder cannot composite two different video sources.
        public ClipCaps clipCaps => ClipCaps.ClipIn | ClipCaps.SpeedMultiplier;

        public override double duration => effect != null && effect.clip != null &&
            effect.source == VideoSourceKind.VideoClip && effect.clip.length > 0
                ? effect.clip.length : 5.0;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<VideoEffectBehaviour>.Create(graph);
            playable.GetBehaviour().Clip = this;
            return playable;
        }
    }
}
