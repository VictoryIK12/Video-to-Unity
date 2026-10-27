using UnityEngine.Playables;

namespace VideoToUnity.Timeline
{
    /// <summary>Clip-local state only. The mixer is the sole writer to the bound player.</summary>
    public sealed class VideoEffectBehaviour : PlayableBehaviour
    {
        internal VideoEffectClip Clip;
        internal float EffectiveSpeed = 1f;

        public override void PrepareFrame(Playable playable, FrameData info)
        {
            // Includes TimelineClip.timeScale and the parent graph's accumulated speed.
            EffectiveSpeed = info.effectiveSpeed;
        }
    }
}
