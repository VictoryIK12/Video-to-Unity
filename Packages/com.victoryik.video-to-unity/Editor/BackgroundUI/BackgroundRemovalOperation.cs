using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace VideoToUnity.Editor
{
    // Unity objects and importer calls stay on the editor update thread. The backend job and
    // the final file copy are asynchronous and are never waited on from the UI thread.
    [InitializeOnLoad]
    internal static class BackgroundRemovalOperation
    {
        internal sealed class Output
        {
            internal VideoEffectAsset Effect;
            internal VideoClip Clip;
            internal string VideoPath;
            internal string EffectPath;
            internal VideoAlphaMode AlphaMode;
            internal Vector2Int Size;
        }

        private static IBackgroundJob job;
        private static BackgroundJobKind? runningKind;
        private static Task<string> copyTask;
        private static CancellationTokenSource copyCancellation;
        private static string temporaryDirectory;
        private static string videoDestination;
        private static string videoAbsoluteDestination;
        private static string effectDestination;
        private static VideoConversionFormat format;
        private static bool cancelled;
        private static bool streaming;
        private static bool reloadLocked;
        private static bool videoImported;
        private static bool effectCreated;
        private static BackgroundAnalysis lastAnalysis;
        private static string analysisSource;
        private static string requestSourceForAnalysis;

        internal static bool Busy => job != null;
        internal static bool Failed { get; private set; }
        internal static string Message { get; private set; } = "MP4 파일을 선택한 뒤 분석하세요.";
        internal static BackgroundJobKind? Kind => runningKind;
        internal static BackgroundResult LastResult { get; private set; }
        internal static string LastResultSource { get; private set; }
        internal static BackgroundAnalysis LastAnalysis { get; private set; }
        internal static string LastAnalysisSource { get; private set; }
        internal static Output LastOutput { get; private set; }
        internal static int Revision { get; private set; }
        internal static event Action Changed;

        static BackgroundRemovalOperation()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Cancel();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.wantsToQuit += WantsToQuit;
            EditorApplication.quitting += Shutdown;
        }

        internal static void StartAnalyze(string source, string ffmpeg, string ffprobe)
        {
            var request = CreateRequest(source, ffmpeg, ffprobe, BackgroundJobKind.Analyze);
            Begin(request, "원본을 분석해 배경색과 신뢰도를 확인하는 중…");
        }

        internal static void StartPreview(string source, string ffmpeg, string ffprobe,
            BackgroundOptions options, double timeSeconds)
        {
            ValidateOptions(options);
            if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds) || timeSeconds < 0)
                throw new ArgumentException("미리보기 시간은 0 이상이어야 합니다.");
            RejectUnreliableAutoDetect(source, options);
            var request = CreateRequest(source, ffmpeg, ffprobe, BackgroundJobKind.Preview);
            request.Options = options.Copy();
            request.PreviewTimeSeconds = timeSeconds;
            request.PreviewMaximumWidth = 640;
            Begin(request, "선택한 시간의 원본과 배경 제거 결과를 만드는 중…");
        }

        internal static void StartConvert(string source, string ffmpeg, string ffprobe,
            BackgroundOptions options, VideoConversionFormat requestedFormat, string videoPath, string effectPath)
        { StartConvertStreaming(source, ffmpeg, ffprobe, options, requestedFormat, videoPath, effectPath, false); }

        internal static void StartConvertStreaming(string source, string ffmpeg, string ffprobe,
            BackgroundOptions options, VideoConversionFormat requestedFormat,
            string videoPath, string effectPath, bool useStreamingAssets)
        {
            ValidateOptions(options);
            if (requestedFormat != VideoConversionFormat.PackedH264 && requestedFormat != VideoConversionFormat.AlphaWebM)
                throw new ArgumentOutOfRangeException(nameof(requestedFormat));
            RejectUnreliableAutoDetect(source, options);

            string extension = requestedFormat == VideoConversionFormat.PackedH264 ? ".mp4" : ".webm";
            string normalizedVideo = VideoEffectEditorPaths.Normalize(videoPath);
            string normalizedEffect = VideoEffectEditorPaths.Normalize(effectPath);
            string absoluteVideo = VideoEffectEditorPaths.NewAssetPath(normalizedVideo, extension, useStreamingAssets);
            string absoluteEffect = VideoEffectEditorPaths.NewAssetPath(normalizedEffect, ".asset", false);
            if (string.Equals(absoluteVideo, absoluteEffect, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("영상과 이펙트 에셋은 서로 다른 새 경로에 저장하세요.");

            var request = CreateRequest(source, ffmpeg, ffprobe, BackgroundJobKind.Convert);
            request.Options = options.Copy();
            request.Format = requestedFormat;
            videoDestination = normalizedVideo;
            videoAbsoluteDestination = absoluteVideo;
            effectDestination = normalizedEffect;
            format = requestedFormat;
            streaming = useStreamingAssets;
            videoImported = false;
            effectCreated = false;
            LastOutput = null;
            Begin(request, "배경 제거 영상과 새 VideoEffectAsset을 만드는 중…");
        }

        internal static void Cancel()
        {
            if (!Busy) return;
            cancelled = true;
            try { job.Cancel(); }
            catch (Exception ex) { Failed = true; SetMessage("취소 요청 중 오류: " + ex.Message); }
            try { copyCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            SetMessage("취소 요청됨 · 자식 프로세스와 임시 파일 정리 중…");
        }

        private static BackgroundRequest CreateRequest(string source, string ffmpeg, string ffprobe,
            BackgroundJobKind kind)
        {
            if (Busy || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("편집 모드에서 현재 작업과 컴파일이 끝난 뒤 시작하세요.");
            if (!QuickWorkflowPolicy.IsVideo(source) || !File.Exists(source))
                throw new ArgumentException("실제로 존재하는 원본 영상 파일을 선택하세요.");
            if (!BackgroundRemovalUiModel.ToolReady(ffmpeg) || !BackgroundRemovalUiModel.ToolReady(ffprobe))
                throw new ArgumentException("FFmpeg와 ffprobe 실행 파일 또는 PATH 명령 이름을 입력하세요.");
            return new BackgroundRequest
            {
                SourcePath = Path.GetFullPath(source),
                Ffmpeg = ffmpeg.Trim(),
                Ffprobe = ffprobe.Trim(),
                Kind = kind,
                Options = new BackgroundOptions()
            };
        }

        private static void Begin(BackgroundRequest request, string message)
        {
            Failed = false;
            cancelled = false;
            copyTask = null;
            copyCancellation = new CancellationTokenSource();
            try
            {
                temporaryDirectory = Path.Combine(Path.GetTempPath(), "VideoToUnity", "Editor", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                request.TemporaryDirectory = temporaryDirectory;
                requestSourceForAnalysis = request.SourcePath;
                runningKind = request.Kind;
                EditorApplication.update += Tick;
                SetMessage(message);
                // Keep script reloads from unloading the callbacks that own process cleanup.
                EditorApplication.LockReloadAssemblies();
                reloadLocked = true;
                // Starting the backend is the last fallible step. If it fails, no job was handed back.
                job = BackgroundVideoProcessor.Start(request);
                if (job == null) throw new InvalidOperationException("배경 처리 작업을 시작하지 못했습니다.");
            }
            catch
            {
                EditorApplication.update -= Tick;
                runningKind = null;
                job = null;
                copyCancellation.Dispose();
                copyCancellation = null;
                TryDeleteTemporary(temporaryDirectory);
                temporaryDirectory = null;
                UnlockReloadIfHeld();
                throw;
            }
        }

        private static void Tick()
        {
            if (!Busy) return;
            if (!job.Completion.IsCompleted)
            {
                if (!cancelled)
                {
                    string status = job.Status;
                    if (!string.IsNullOrWhiteSpace(status)) SetMessage(status);
                }
                return;
            }
            if (copyTask != null && !copyTask.IsCompleted) return;

            bool complete = false;
            try
            {
                if (cancelled) throw new OperationCanceledException();
                BackgroundResult result = job.Completion.GetAwaiter().GetResult();
                if (result == null) throw new InvalidOperationException("배경 처리 결과가 비어 있습니다.");
                ValidateResult(result, runningKind.Value);

                if (runningKind == BackgroundJobKind.Convert && copyTask == null)
                {
                    VideoConverter.Result conversion = result.Conversion;
                    string extension = format == VideoConversionFormat.PackedH264 ? ".mp4" : ".webm";
                    VideoEffectEditorPaths.NewAssetPath(videoDestination, extension, streaming);
                    VideoEffectEditorPaths.NewAssetPath(effectDestination, ".asset", false);
                    string sourceFile = conversion.FilePath;
                    string targetFile = videoAbsoluteDestination;
                    CancellationToken token = copyCancellation.Token;
                    copyTask = Task.Run(() => VideoEffectFileTransfer.CopyNewFile(sourceFile, targetFile, token), token);
                    SetMessage("배경 제거 완료 · 새 영상 파일을 저장하는 중…");
                    return;
                }

                if (runningKind == BackgroundJobKind.Convert)
                {
                    copyTask.GetAwaiter().GetResult();
                    copyCancellation.Token.ThrowIfCancellationRequested();
                    CompleteConversion(result);
                    complete = true;
                }
                else
                {
                    LastResult = result;
                    LastResultSource = requestSourceForAnalysis;
                    if (runningKind == BackgroundJobKind.Analyze)
                    {
                        lastAnalysis = result.Analysis;
                        analysisSource = Path.GetFullPath(requestSourceForAnalysis);
                        LastAnalysis = result.Analysis;
                        LastAnalysisSource = analysisSource;
                    }
                    Revision++;
                    SetMessage(ResultMessage(result, runningKind.Value));
                    complete = true;
                }
            }
            catch (OperationCanceledException)
            {
                string rollback = RollbackOwnedOutputs();
                SetMessage("작업을 취소했습니다. 임시 파일을 정리합니다." + rollback);
                complete = true;
            }
            catch (Exception ex)
            {
                Failed = true;
                string rollback = RollbackOwnedOutputs();
                SetMessage(ex.Message + rollback);
                complete = true;
            }
            finally
            {
                if (complete) Finish();
            }
        }

        private static void ValidateResult(BackgroundResult result, BackgroundJobKind kind)
        {
            if (result.Width <= 0 || result.Height <= 0)
                throw new InvalidDataException("배경 처리 결과의 영상 크기가 올바르지 않습니다.");
            if (kind == BackgroundJobKind.Analyze)
            {
                if (result.Analysis == null) throw new InvalidDataException("분석 결과가 없습니다.");
                if (double.IsNaN(result.Analysis.Confidence) || double.IsInfinity(result.Analysis.Confidence) ||
                    result.Analysis.Confidence < 0 || result.Analysis.Confidence > 1)
                    throw new InvalidDataException("배경색 신뢰도 값이 올바르지 않습니다.");
                return;
            }
            if (kind == BackgroundJobKind.Preview)
            {
                BackgroundPreview preview = result.Preview;
                if (preview == null || preview.Width <= 0 || preview.Height <= 0)
                    throw new InvalidDataException("미리보기 결과가 없습니다.");
                long expected = (long)preview.Width * preview.Height * 4;
                if (preview.OriginalRgba == null || preview.ResultRgba == null ||
                    preview.OriginalRgba.LongLength != expected || preview.ResultRgba.LongLength != expected)
                    throw new InvalidDataException("미리보기 RGBA 버퍼 크기가 영상 크기와 맞지 않습니다.");
                return;
            }
            VideoConverter.Result conversion = result.Conversion;
            if (conversion == null || string.IsNullOrEmpty(conversion.FilePath) || !File.Exists(conversion.FilePath))
                throw new InvalidDataException("배경 제거 영상 파일이 만들어지지 않았습니다.");
            if (conversion.Format != format || conversion.Width != result.Width || conversion.Height != result.Height)
                throw new InvalidDataException("배경 제거 결과의 형식 또는 크기가 요청과 맞지 않습니다.");
        }

        private static void CompleteConversion(BackgroundResult result)
        {
            // The completed copy already owns this path; only the effect asset must still be new.
            string copiedVideo = VideoEffectEditorPaths.AssetPath(videoDestination, streaming);
            if (!string.Equals(copiedVideo, copyTask.Result, StringComparison.OrdinalIgnoreCase) || !File.Exists(copiedVideo))
                throw new IOException("저장한 새 영상 파일을 찾을 수 없습니다.");
            VideoEffectEditorPaths.NewAssetPath(effectDestination, ".asset", false);
            SetMessage("영상 저장 완료 · Unity 임포트 중 (이 단계는 Unity가 처리)…");
            AssetDatabase.ImportAsset(videoDestination, ImportAssetOptions.ForceSynchronousImport);
            videoImported = true;
            VideoAlphaMode mode = format == VideoConversionFormat.PackedH264
                ? VideoAlphaMode.PackedAlpha : VideoAlphaMode.NativeAlpha;
            if (!streaming) VideoEffectImportSettings.Apply(videoDestination, mode);
            var clip = streaming ? null : AssetDatabase.LoadAssetAtPath<VideoClip>(videoDestination);
            if (!streaming && clip == null) throw new InvalidDataException("Unity가 새 영상을 VideoClip으로 가져오지 못했습니다.");

            VideoEffectEditorPaths.NewAssetPath(effectDestination, ".asset", false);
            var effect = ScriptableObject.CreateInstance<VideoEffectAsset>();
            bool assetCreated = false;
            try
            {
                effect.name = Path.GetFileNameWithoutExtension(effectDestination);
                effect.source = streaming ? VideoSourceKind.StreamingAssets : VideoSourceKind.VideoClip;
                effect.clip = clip;
                effect.location = streaming ? videoDestination.Substring("Assets/StreamingAssets/".Length) : "";
                effect.alphaMode = mode;
                effect.premultiplied = false;
                effect.tint = Color.white;
                effect.opacity = 1f;
                effect.keyColor = Color.green;
                effect.keyThreshold = .05f;
                effect.keySoftness = .08f;
                effect.loop = false;
                effect.holdLastFrame = false;
                effect.flipY = false;
                effect.speed = 1f;
                effect.audioEnabled = false;
                effect.contentSize = new Vector2Int(result.Width, result.Height);
                effect.correctPackedAlphaGamma = true;
                if (!effect.Validate(out string reason)) throw new InvalidDataException(reason);
                AssetDatabase.CreateAsset(effect, effectDestination);
                assetCreated = true;
                effectCreated = true;
                AssetDatabase.SaveAssetIfDirty(effect);
                LastOutput = new Output
                {
                    Effect = effect,
                    Clip = clip,
                    VideoPath = videoDestination,
                    EffectPath = effectDestination,
                    AlphaMode = mode,
                    Size = new Vector2Int(result.Width, result.Height)
                };
                Revision++;
                string warning = string.IsNullOrWhiteSpace(result.Warning) ? "" : "\n" + result.Warning;
                SetMessage("새 배경 제거 영상과 이펙트를 만들었습니다.\n" + videoDestination +
                    "\n" + effectDestination + warning);
            }
            finally
            {
                if (!assetCreated && !AssetDatabase.Contains(effect)) UnityEngine.Object.DestroyImmediate(effect);
            }
        }

        private static string ResultMessage(BackgroundResult result, BackgroundJobKind kind)
        {
            string warning = string.IsNullOrWhiteSpace(result.Warning) ? "" : "\n" + result.Warning;
            if (kind == BackgroundJobKind.Analyze)
                return $"분석 완료 · 신뢰도 {result.Analysis.Confidence:P0} · 제안 색 RGB " +
                    $"{result.Analysis.Red}, {result.Analysis.Green}, {result.Analysis.Blue}" +
                    (string.IsNullOrWhiteSpace(result.Analysis.Message) ? "" : "\n" + result.Analysis.Message) + warning;
            return $"미리보기 완료 · {result.Preview.Width} × {result.Preview.Height} · " +
                $"{result.Preview.TimeSeconds:F2}초" + warning;
        }

        private static void ValidateOptions(BackgroundOptions options)
        {
            // Use the backend's exact limits so UI acceptance cannot drift from processing.
            BackgroundMatteSession.Validate(options);
        }

        private static void RejectUnreliableAutoDetect(string source, BackgroundOptions options)
        {
            if (!options.AutoDetect || lastAnalysis == null || string.IsNullOrEmpty(analysisSource) ||
                string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            if (!string.Equals(analysisSource, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return;
            if (!lastAnalysis.Reliable)
                throw new InvalidOperationException("AutoDetect 신뢰도가 낮습니다. AutoDetect를 끄고 제안 색 또는 직접 고른 색으로 다시 시도하세요.");
        }

        private static string RollbackOwnedOutputs()
        {
            string problem = "";
            try
            {
                if (effectCreated && !string.IsNullOrEmpty(effectDestination) &&
                    !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(effectDestination)))
                {
                    if (!AssetDatabase.DeleteAsset(effectDestination))
                        problem += "\n새 이펙트 에셋을 자동으로 지우지 못했습니다: " + effectDestination;
                }
            }
            catch (Exception ex) { problem += "\n새 이펙트 정리 필요: " + effectDestination + "\n" + ex.Message; }
            try
            {
                if (copyTask != null && copyTask.Status == TaskStatus.RanToCompletion)
                {
                    if (!string.IsNullOrEmpty(videoDestination) &&
                        !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(videoDestination)))
                    {
                        if (!AssetDatabase.DeleteAsset(videoDestination))
                            problem += "\n새 영상을 자동으로 지우지 못했습니다: " + videoDestination;
                    }
                    else if (!string.IsNullOrEmpty(videoAbsoluteDestination) && File.Exists(videoAbsoluteDestination))
                    {
                        VideoEffectFileTransfer.DeleteCopiedFile(videoAbsoluteDestination);
                    }
                }
            }
            catch (Exception ex) { problem += "\n새 영상 정리 필요: " + videoDestination + "\n" + ex.Message; }
            return problem;
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            var finishedJob = job;
            var finishedCopy = copyTask;
            var finishedCancellation = copyCancellation;
            string finishedTemporary = temporaryDirectory;
            job = null;
            copyTask = null;
            runningKind = null;
            videoDestination = null;
            videoAbsoluteDestination = null;
            effectDestination = null;
            videoImported = false;
            effectCreated = false;
            requestSourceForAnalysis = null;
            temporaryDirectory = null;
            copyCancellation = null;
            try
            {
                try { finishedJob?.Dispose(); }
                catch (Exception ex) { Failed = true; Message += "\n배경 처리기 정리 실패: " + ex.Message; }
                try { VideoEffectFileTransfer.DeleteTemporary(finishedTemporary); }
                catch (Exception ex) { Failed = true; Message += "\n임시 폴더 정리 필요: " + finishedTemporary + "\n" + ex.Message; }
                finishedCancellation?.Dispose();
                if (finishedCopy != null && finishedCopy.IsFaulted) { _ = finishedCopy.Exception; }
            }
            finally
            {
                UnlockReloadIfHeld();
                Changed?.Invoke();
            }
        }

        private static bool WantsToQuit()
        {
            if (!Busy) return true;
            Cancel();
            SetMessage("Unity 종료를 잠시 보류했습니다. 취소와 파일 정리가 끝난 뒤 다시 종료하세요.");
            return false;
        }

        private static void Shutdown()
        {
            if (!Busy) return;
            cancelled = true;
            try { job.Cancel(); }
            catch (Exception) { }
            try { copyCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            EditorApplication.update -= Tick;
            var finishedJob = job;
            var finishedCopy = copyTask;
            var cancellation = copyCancellation;
            string temporary = temporaryDirectory;
            bool imported = videoImported;
            job = null;
            copyTask = null;
            runningKind = null;
            copyCancellation = null;
            temporaryDirectory = null;
            videoDestination = null;
            videoAbsoluteDestination = null;
            effectDestination = null;
            effectCreated = false;
            requestSourceForAnalysis = null;
            // The editor domain can disappear immediately after this callback. Keep cleanup on the
            // backend/copy tasks themselves, and only release or delete anything after both settle.
            Task completion = finishedJob?.Completion ?? Task.CompletedTask;
            if (finishedCopy != null) completion = Task.WhenAll(completion, finishedCopy);
            completion.ContinueWith(done =>
            {
                _ = done.Exception;
                try
                {
                    if (!imported && finishedCopy != null && finishedCopy.Status == TaskStatus.RanToCompletion)
                        VideoEffectFileTransfer.DeleteCopiedFile(finishedCopy.Result);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // Let the completed job release its own files before removing their parent.
                try { finishedJob?.Dispose(); }
                catch (Exception) { }
                try { VideoEffectFileTransfer.DeleteTemporary(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                cancellation?.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Forced shutdown cannot wait for a later update callback; normal reload cleanup stays
            // in Tick/Finish while the lock is held.
            UnlockReloadIfHeld();
        }

        private static void UnlockReloadIfHeld()
        {
            if (!reloadLocked) return;
            reloadLocked = false;
            EditorApplication.UnlockReloadAssemblies();
        }

        private static void TryDeleteTemporary(string directory)
        {
            try { VideoEffectFileTransfer.DeleteTemporary(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void SetMessage(string message)
        {
            if (Message == message) return;
            Message = message;
            Changed?.Invoke();
        }
    }
}
