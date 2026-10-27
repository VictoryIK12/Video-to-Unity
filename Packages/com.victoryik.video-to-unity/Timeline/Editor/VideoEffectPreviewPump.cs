using System.Collections.Generic;
using UnityEditor;

namespace VideoToUnity.Timeline.Editor
{
    /// <summary>Give asynchronous preparation/seeking time to update edit-mode views.</summary>
    [InitializeOnLoad]
    internal static class VideoEffectPreviewPump
    {
        private const double UpdateInterval = 1.0 / 30.0;
        private static readonly HashSet<VideoEffectMixerBehaviour> Active = new HashSet<VideoEffectMixerBehaviour>();
        private static readonly List<VideoEffectMixerBehaviour> Expired = new List<VideoEffectMixerBehaviour>();
        private static double nextUpdate;

        static VideoEffectPreviewPump()
        {
            VideoEffectMixerBehaviour.PreviewRequested += Request;
            VideoEffectMixerBehaviour.PreviewReleased += Forget;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
            EditorApplication.quitting += ReleaseAll;
            EditorApplication.playModeStateChanged += _ => ReleaseAll();
        }

        private static void Request(VideoEffectMixerBehaviour mixer)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                Active.Add(mixer);
        }

        private static void Forget(VideoEffectMixerBehaviour mixer) => Active.Remove(mixer);

        private static void Update()
        {
            if (Active.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            double now = EditorApplication.timeSinceStartup;
            if (now < nextUpdate)
                return;
            nextUpdate = now + UpdateInterval;
            Expired.Clear();
            foreach (var mixer in Active)
            {
                if (!mixer.HasPreview)
                    Expired.Add(mixer);
            }
            foreach (var mixer in Expired)
                Active.Remove(mixer);
            if (Active.Count == 0)
                return;

            // Never call Director.Evaluate, VideoPlayer.Play or Seek from this pump.
            // Keep the native decoder and ExecuteAlways View updating for the entire preview;
            // the player itself intentionally need not have ExecuteAlways.
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private static void ReleaseAll()
        {
            var mixers = new List<VideoEffectMixerBehaviour>(Active);
            Active.Clear();
            foreach (var mixer in mixers)
                mixer.Release();
        }
    }
}
