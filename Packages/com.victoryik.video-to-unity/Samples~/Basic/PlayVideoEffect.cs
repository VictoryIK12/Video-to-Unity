using UnityEngine;
using VideoToUnity;

namespace VideoToUnity.Samples
{
    /// <summary>Wire Trigger/Stop to a button, event or your gameplay code.</summary>
    public sealed class PlayVideoEffect : MonoBehaviour
    {
        public VideoEffectPlayer player;
        public VideoEffectAsset effect;
        public void WarmUp() { if (player != null) player.Prepare(effect); }
        public void Trigger() { if (player != null) player.Play(effect); }
        public void Stop() { if (player != null) player.Stop(); }
    }
}
