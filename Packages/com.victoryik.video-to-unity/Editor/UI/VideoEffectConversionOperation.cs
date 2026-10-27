using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace VideoToUnity.Editor
{
    // The editor update owns Unity APIs. Conversion and file transfer only perform background work.
    [InitializeOnLoad]
    internal static class VideoEffectConversionOperation
    {
        internal sealed class Output
        {
            internal VideoClip Clip;
            internal string AssetPath;
            internal string Location;
            internal VideoSourceKind Source;
            internal VideoAlphaMode AlphaMode;
            internal Vector2Int Size;
        }

        private static VideoConverter converter;
        private static Task<string> copyTask;
        private static CancellationTokenSource cancellation;
        private static string temporaryDirectory;
        private static string destination;
        private static string absoluteDestination;
        private static bool streaming;
        private static VideoConversionFormat format;
        private static bool cancelled;
        private static bool reloadLocked;
        private static Vector2Int size;
        private static string evidence;
        internal static bool Busy => converter != null;
        internal static string Message { get; private set; } = "알파 채널이 있는 원본을 선택하세요.";
        internal static bool Failed { get; private set; }
        internal static Output LastOutput { get; private set; }
        internal static int Revision { get; private set; }
        internal static event Action Changed;

        static VideoEffectConversionOperation()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Cancel();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.wantsToQuit += WantsToQuit;
            EditorApplication.quitting += Shutdown;
        }

        internal static void Start(string source, string target, string ffmpeg, string ffprobe,
            VideoConversionFormat requestedFormat, bool useStreamingAssets)
        {
            if (Busy || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("편집 모드에서 다른 변환과 컴파일이 끝난 뒤 시작하세요.");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new ArgumentException("실제로 존재하는 알파 원본 파일을 선택하세요.");
            if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe))
                throw new ArgumentException("FFmpeg와 ffprobe 경로 또는 PATH 명령 이름을 입력하세요.");
            if (useStreamingAssets && requestedFormat != VideoConversionFormat.PackedH264)
                throw new ArgumentException("StreamingAssets 변환은 Packed H.264를 선택하세요. Linux의 Native Alpha는 VideoClip 경로를 사용하세요.");
            string extension = requestedFormat == VideoConversionFormat.PackedH264 ? ".mp4" : ".webm";
            absoluteDestination = VideoEffectEditorPaths.NewAssetPath(target, extension, useStreamingAssets);
            destination = VideoEffectEditorPaths.Normalize(target);
            streaming = useStreamingAssets;
            format = requestedFormat;
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "VideoToUnity", "Editor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            cancelled = false;
            Failed = false;
            copyTask = null;
            cancellation = new CancellationTokenSource();
            try
            {
                EditorApplication.LockReloadAssemblies();
                reloadLocked = true;
                converter = VideoConverter.Start(Path.GetFullPath(source), temporaryDirectory, ffmpeg.Trim(), ffprobe.Trim(), format);
                EditorApplication.update += Tick;
                SetMessage("원본 알파 확인 및 변환 시작…");
            }
            catch
            {
                Finish();
                throw;
            }
        }

        internal static void Cancel()
        {
            if (!Busy) return;
            cancelled = true;
            cancellation?.Cancel();
            converter.Cancel();
            SetMessage("취소 요청됨 · 프로세스 종료와 임시 파일 정리 중…");
        }

        private static void Tick()
        {
            if (!Busy) return;
            if (!converter.Completion.IsCompleted)
            {
                if (!cancelled) SetMessage(converter.Status);
                return;
            }
            if (copyTask != null && !copyTask.IsCompleted) return;
            bool complete = false;
            try
            {
                if (cancelled) throw new OperationCanceledException();
                if (copyTask == null)
                {
                    var result = converter.Completion.GetAwaiter().GetResult();
                    if (result.Format != format || result.Width <= 0 || result.Height <= 0 || !File.Exists(result.FilePath))
                        throw new InvalidOperationException("변환 결과 형식 또는 크기가 올바르지 않습니다.");
                    VideoEffectEditorPaths.NewAssetPath(destination, format == VideoConversionFormat.PackedH264 ? ".mp4" : ".webm", streaming);
                    size = new Vector2Int(result.Width, result.Height);
                    evidence = $"{result.Width} × {result.Height} · {result.Frames}프레임 · 알파 {result.MinimumAlpha}–{result.MaximumAlpha}";
                    string file = result.FilePath;
                    string target = absoluteDestination;
                    CancellationToken token = cancellation.Token;
                    copyTask = Task.Run(() => VideoEffectFileTransfer.CopyNewFile(file, target, token), token);
                    SetMessage("변환 완료 · 새 영상 파일 저장 중…");
                    return;
                }
                copyTask.GetAwaiter().GetResult();
                // No importer runs for StreamingAssets: Web builds reference the relative URL only.
                VideoClip clip = null;
                VideoAlphaMode mode = format == VideoConversionFormat.PackedH264 ? VideoAlphaMode.PackedAlpha : VideoAlphaMode.NativeAlpha;
                if (streaming)
                {
                    AssetDatabase.Refresh();
                }
                else
                {
                    SetMessage("영상 저장 완료 · Unity 임포트 중 (이 단계는 Unity에서 처리)…");
                    AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
                    VideoEffectImportSettings.Apply(destination, mode);
                    clip = AssetDatabase.LoadAssetAtPath<VideoClip>(destination);
                    if (clip == null) throw new InvalidOperationException("Unity가 저장된 영상을 VideoClip으로 가져오지 못했습니다.");
                }
                LastOutput = new Output
                {
                    Clip = clip, AssetPath = destination, Source = streaming ? VideoSourceKind.StreamingAssets : VideoSourceKind.VideoClip,
                    Location = streaming ? destination.Substring("Assets/StreamingAssets/".Length) : "",
                    AlphaMode = mode, Size = size
                };
                Revision++;
                SetMessage("변환 완료: " + evidence + "\n" + destination + "\n아래 설정에 연결했습니다. 이펙트 에셋을 저장하세요.");
                complete = true;
            }
            catch (OperationCanceledException)
            {
                // The copy task owns only a newly created file. Never remove a competing pre-existing destination.
                string cleanup = "";
                if (copyTask != null && copyTask.Status == TaskStatus.RanToCompletion)
                {
                    try { VideoEffectFileTransfer.DeleteCopiedFile(copyTask.Result); }
                    catch (Exception ex) { Failed = true; cleanup = "\n저장 파일 정리 필요: " + copyTask.Result + "\n" + ex.Message; }
                }
                SetMessage("변환을 취소했습니다. 임시 파일을 정리합니다." + cleanup);
                complete = true;
            }
            catch (Exception ex)
            {
                Failed = true;
                string saved = copyTask != null && copyTask.Status == TaskStatus.RanToCompletion
                    ? "\n새 영상은 저장되어 있습니다: " + destination + "\n임포트 설정을 확인하거나 다른 새 경로로 다시 시도하세요."
                    : "";
                SetMessage(ex.Message + saved);
                complete = true;
            }
            finally
            {
                if (complete) Finish();
            }
        }

        private static bool WantsToQuit()
        {
            if (!Busy) return true;
            Cancel();
            SetMessage("Unity 종료를 잠시 보류했습니다. 변환 취소와 파일 정리가 끝나면 종료를 다시 선택하세요.");
            return false;
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            if (converter != null && converter.Completion.IsFaulted) { var observed = converter.Completion.Exception; }
            if (copyTask != null && copyTask.IsFaulted) { var observed = copyTask.Exception; }
            try { converter?.Dispose(); }
            catch (Exception ex) { Failed = true; Message += "\n변환기 정리 실패: " + ex.Message; }
            finally
            {
                converter = null;
                copyTask = null;
                cancellation?.Dispose();
                cancellation = null;
                try { VideoEffectFileTransfer.DeleteTemporary(temporaryDirectory); }
                catch (Exception ex) { Failed = true; Message += "\n임시 폴더 정리 필요: " + temporaryDirectory + "\n" + ex.Message; }
                temporaryDirectory = null;
                if (reloadLocked) { reloadLocked = false; EditorApplication.UnlockReloadAssemblies(); }
                Changed?.Invoke();
            }
        }

        private static void Shutdown()
        {
            if (!Busy) return;
            Cancel();
            EditorApplication.update -= Tick;
            var job = converter;
            var transfer = copyTask;
            var cancellationSource = cancellation;
            string temporary = temporaryDirectory;
            converter = null;
            copyTask = null;
            cancellation = null;
            temporaryDirectory = null;
            try { job.Dispose(); }
            finally
            {
                // Observe errors and delete only after every process/file handle has finished.
                Task completion = transfer != null ? Task.WhenAll(job.Completion, transfer) : job.Completion;
                completion.ContinueWith(done =>
                {
                    var observed = done.Exception;
                    try
                    {
                        if (transfer != null && transfer.Status == TaskStatus.RanToCompletion) VideoEffectFileTransfer.DeleteCopiedFile(transfer.Result);
                        VideoEffectFileTransfer.DeleteTemporary(temporary);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    cancellationSource?.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (reloadLocked) { reloadLocked = false; EditorApplication.UnlockReloadAssemblies(); }
            }
        }

        private static void SetMessage(string message)
        {
            if (Message == message) return;
            Message = message;
            Changed?.Invoke();
        }
    }
}
