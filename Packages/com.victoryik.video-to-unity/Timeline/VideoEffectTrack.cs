using System.ComponentModel;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace VideoToUnity.Timeline
{
    [TrackColor(0.18f, 0.64f, 0.76f)]
    [DisplayName("Video to Unity/Video Effect Track")]
    [TrackClipType(typeof(VideoEffectClip))]
    [TrackBindingType(typeof(VideoEffectPlayer))]
    public sealed class VideoEffectTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            var playable = ScriptPlayable<VideoEffectMixerBehaviour>.Create(graph, inputCount);
            playable.GetBehaviour().Initialize(go != null ? go.GetComponent<PlayableDirector>() : null, this);
            return playable;
        }

        public override void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            // Timeline preview must not persist sampled scene fields when preview ends.
            var player = director != null ? director.GetGenericBinding(this) as VideoEffectPlayer : null;
            if (player != null)
            {
                driver.AddFromName(player, nameof(VideoEffectPlayer.effect));
                driver.AddFromName(player, nameof(VideoEffectPlayer.opacity));
            }
            base.GatherProperties(director, driver);
        }
    }
}
