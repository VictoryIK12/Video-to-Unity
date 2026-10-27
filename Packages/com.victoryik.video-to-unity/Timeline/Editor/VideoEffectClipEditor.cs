using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace VideoToUnity.Timeline.Editor
{
    [CustomTimelineEditor(typeof(VideoEffectClip))]
    public sealed class VideoEffectClipEditor : ClipEditor
    {
        public override void OnCreate(TimelineClip clip, TrackAsset track, TimelineClip clonedFrom)
        {
            if (clonedFrom != null)
                return;
            var asset = clip.asset as VideoEffectClip;
            if (asset == null || asset.effect == null)
                return;
            clip.displayName = asset.effect.name;
            clip.duration = asset.duration;
        }

        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);
            var asset = clip.asset as VideoEffectClip;
            options.tooltip = "Video Effect Asset 재생. Clip In과 Speed Multiplier를 지원하며 영상 간 블렌딩은 지원하지 않습니다.";
            if (asset == null || asset.effect == null)
                options.errorText = "클립의 Effect 필드에 Video Effect Asset을 넣으세요.";
            else if (!asset.effect.Validate(out var reason))
                options.errorText = reason;
            else if (clip.timeScale < 0.05 || clip.timeScale > 4)
                options.errorText = "Speed Multiplier는 0.05~4 범위로 설정하세요. 기기별 지원 범위는 더 좁을 수 있습니다.";
            else if (HasOverlap(clip))
                options.errorText = "한 플레이어에서 두 영상을 블렌딩할 수 없습니다. 클립 겹침을 제거하세요.";
            return options;
        }

        private static bool HasOverlap(TimelineClip clip)
        {
            var track = clip.GetParentTrack();
            if (track == null)
                return false;
            foreach (var other in track.GetClips())
            {
                if (other != clip && other.start < clip.end && clip.start < other.end)
                    return true;
            }
            return false;
        }
    }
}
