using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VideoToUnity.Editor
{
    [InitializeOnLoad]
    internal static class VideoToolchainSetup
    {
        internal const string DownloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
        internal const string ArchiveSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
        internal const long ArchiveBytes = 114768076;
        private const string VersionFolder = "ffmpeg-9.0.2-win64";
        private const string EncoderPref = "VideoToUnity.FFmpeg", ProbePref = "VideoToUnity.FFprobe";
        private sealed class Pair { internal string Encoder, Probe; }
        private static Task<Pair> task;
        private static CancellationTokenSource cancellation;
        private static volatile string progress;
        private static bool cancelled;
        internal static bool Busy => task != null;
        internal static bool Ready { get; private set; }
        internal static string Ffmpeg { get; private set; } = "";
        internal static string Ffprobe { get; private set; } = "";
        internal static string Message { get; private set; } = "영상 도구를 찾습니다. 없으면 자동 준비 또는 경로 지정을 사용하세요.";
        internal static bool CanInstall => Application.platform == RuntimePlatform.WindowsEditor && Environment.Is64BitOperatingSystem &&
            !string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase);
        internal static event Action Changed;

        static VideoToolchainSetup()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) Cancel(); };
        }

        internal static void Discover()
        {
            if (Busy) return;
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            string savedEncoder = EditorPrefs.GetString(EncoderPref, ""), savedProbe = EditorPrefs.GetString(ProbePref, "");
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Begin("설치된 영상 도구를 찾고 확인하는 중…", token =>
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var candidates = Candidates(savedEncoder, savedProbe, path, local, user, windows);
                    foreach (var pair in candidates.Take(24))
                    {
                        token.ThrowIfCancellationRequested();
                        if (ValidatePair(pair, timeout.Token)) return pair;
                    }
                    return null;
                }
            });
        }

        internal static void UsePaths(string encoder, string probe)
        {
            if (Busy) return;
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Begin("지정한 영상 도구를 확인하는 중…", token =>
            {
                string first = ResolveExecutable(encoder, path, windows);
                string second = ResolveExecutable(probe, path, windows);
                if (first != null && second == null) second = Sibling(first, "ffprobe", windows);
                if (second != null && first == null) first = Sibling(second, "ffmpeg", windows);
                var pair = new Pair { Encoder = first, Probe = second };
                if (!ValidatePair(pair, token)) throw new IOException("FFmpeg와 ffprobe를 함께 확인하지 못했습니다. 두 실행 파일이 있는 폴더를 선택하세요.");
                return pair;
            });
        }

        internal static void Install()
        {
            if (Busy || Ready) return;
            if (!CanInstall) { SetMessage("이 운영체제에서는 설치된 FFmpeg를 자동으로 찾거나 실행 파일 경로를 지정하세요."); return; }
            string cache = CacheRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Begin("영상 도구 다운로드 준비 중 · 약 115 MB", token =>
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                { timeout.CancelAfter(TimeSpan.FromMinutes(20)); return InstallPortable(cache, timeout.Token); }
            });
        }

        private static void Begin(string message, Func<CancellationToken, Pair> work)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { SetMessage("편집 모드에서 영상 도구를 준비하세요."); return; }
            Ready = false; cancelled = false;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            var token = cancellation.Token; progress = message;
            task = Task.Run(() => work(token), token);
            EditorApplication.update += Tick;
            SetMessage(message);
        }

        internal static void Cancel()
        {
            if (!Busy) return;
            cancelled = true; cancellation.Cancel();
            SetMessage("영상 도구 준비를 취소하는 중…");
        }

        private static void Tick()
        {
            if (task == null) return;
            if (!task.IsCompleted) { if (!cancelled) SetMessage(progress); return; }
            var completed = task; task = null; EditorApplication.update -= Tick;
            try
            {
                Pair pair = completed.GetAwaiter().GetResult();
                if (cancelled) throw new OperationCanceledException();
                if (pair == null)
                {
                    SetMessage(CanInstall ? "영상 도구를 찾지 못했습니다. ‘영상 도구 자동 준비’를 한 번 눌러 주세요." :
                        "FFmpeg와 ffprobe가 필요합니다. macOS는 Homebrew의 ffmpeg, Linux는 배포판 ffmpeg 패키지를 설치한 뒤 ‘다시 찾기’를 누르거나 경로를 지정하세요.");
                    return;
                }
                Ffmpeg = pair.Encoder; Ffprobe = pair.Probe; Ready = true;
                EditorPrefs.SetString(EncoderPref, Ffmpeg); EditorPrefs.SetString(ProbePref, Ffprobe);
                SetMessage("영상 도구 준비 완료 · 다음 영상부터도 이 경로를 사용합니다.");
            }
            catch (OperationCanceledException) { SetMessage(cancelled ? "영상 도구 준비를 취소했습니다. 다시 시도할 수 있습니다." : "도구 확인 시간이 초과되었습니다. 실행 파일 경로를 직접 지정해 주세요."); }
            catch (Exception ex) { SetMessage("영상 도구 준비에 실패했습니다: " + ex.Message + "\n다시 시도하거나 경로를 직접 지정할 수 있습니다."); }
            finally { cancellation.Dispose(); cancellation = null; Changed?.Invoke(); }
        }

        private static void SetMessage(string text)
        { if (Message == text) return; Message = text; Changed?.Invoke(); }

        private static IEnumerable<Pair> Candidates(string savedEncoder, string savedProbe, string path, string local, string user, bool windows)
        {
            var seen = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var list = new List<Pair>();
            Action<string, string> add = (a, b) =>
            {
                if (a != null && b != null && File.Exists(a) && File.Exists(b) && seen.Add(a + "\n" + b))
                    list.Add(new Pair { Encoder = Path.GetFullPath(a), Probe = Path.GetFullPath(b) });
            };
            string preferred = ResolveExecutable(savedEncoder, path, windows), probe = ResolveExecutable(savedProbe, path, windows);
            add(preferred, probe); add(preferred, Sibling(preferred, "ffprobe", windows)); add(Sibling(probe, "ffmpeg", windows), probe);
            var directories = new List<string> { Path.Combine(CacheRoot(local), VersionFolder, "bin") };
            directories.AddRange(path.Split(Path.PathSeparator).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().Trim('"')));
            if (windows)
            {
                directories.AddRange(new[] { Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(user, "scoop", "apps", "ffmpeg", "current", "bin"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin"), @"C:\ffmpeg\bin" });
                string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                    foreach (string package in SafeDirectories(packages, "*FFmpeg*").Take(12))
                    {
                        directories.Add(package); directories.Add(Path.Combine(package, "bin"));
                        foreach (string child in SafeDirectories(package, "*").Take(12)) directories.Add(Path.Combine(child, "bin"));
                    }
            }
            else directories.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/opt/local/bin", Path.Combine(user, ".local", "bin") });
            foreach (string directory in directories)
            {
                try { if (Path.IsPathRooted(directory)) add(Path.Combine(directory, windows ? "ffmpeg.exe" : "ffmpeg"), Path.Combine(directory, windows ? "ffprobe.exe" : "ffprobe")); }
                catch (ArgumentException) { }
            }
            return list;
        }

        private static IEnumerable<string> SafeDirectories(string root, string pattern)
        { try { return Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly); } catch (IOException) { return Array.Empty<string>(); } catch (UnauthorizedAccessException) { return Array.Empty<string>(); } }

        internal static string ResolveExecutable(string value, string path, bool windows)
        {
            value = (value ?? "").Trim().Trim('"');
            if (value.Length == 0 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return null;
            try
            {
                if (Path.IsPathRooted(value)) return File.Exists(value) ? Path.GetFullPath(value) : null;
                if (value.Contains("/") || value.Contains("\\")) return null;
                if (windows && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value += ".exe";
                foreach (string directory in (path ?? "").Split(Path.PathSeparator))
                {
                    string folder = directory.Trim().Trim('"');
                    if (!Path.IsPathRooted(folder)) continue;
                    string candidate = Path.Combine(folder, value);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return null;
        }

        private static string Sibling(string executable, string name, bool windows) => executable == null ? null : Path.Combine(Path.GetDirectoryName(executable), name + (windows ? ".exe" : ""));
        private static string CacheRoot(string local) => Path.Combine(local, "VideoToUnity", "Tools");

        private static bool ValidatePair(Pair pair, CancellationToken token)
        {
            if (pair.Encoder == null || pair.Probe == null || !File.Exists(pair.Encoder) || !File.Exists(pair.Probe)) return false;
            try
            {
                if (!RunText(pair.Encoder, new[] { "-version" }, token).Contains("ffmpeg version") ||
                    !RunText(pair.Probe, new[] { "-version" }, token).Contains("ffprobe version")) return false;
                string encoders = RunText(pair.Encoder, new[] { "-v", "error", "-encoders" }, token);
                return encoders.Contains("libx264") && encoders.Contains("libvpx") && encoders.Contains("ffv1");
            }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string RunText(string executable, string[] args, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5)); var text = new StringBuilder();
                BackgroundProcess.Run(executable, args, pipe => BackgroundProcess.Lines(pipe, line =>
                { text.AppendLine(line); if (text.Length > 131072) throw new IOException("도구의 응답이 너무 큽니다."); }, timeout.Token), timeout.Token);
                return text.ToString();
            }
        }

        private static Pair InstallPortable(string cache, CancellationToken token)
        {
            Directory.CreateDirectory(cache); RejectRedirectedAncestors(cache);
            string stage = Path.Combine(cache, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                string archive = Path.Combine(stage, "download.zip");
                Download(archive, token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                VerifyArchive(archive, ArchiveSha256);
                progress = "다운로드 검증 완료 · 영상 도구를 준비하는 중…";
                string unpacked = Path.Combine(stage, "tools"); Directory.CreateDirectory(unpacked);
                ExtractArchive(archive, unpacked, token);
                var pair = new Pair { Encoder = Path.Combine(unpacked, "bin", "ffmpeg.exe"), Probe = Path.Combine(unpacked, "bin", "ffprobe.exe") };
                if (!ValidatePair(pair, token)) throw new IOException("다운로드한 도구의 실행 또는 필수 코덱 확인에 실패했습니다.");
                token.ThrowIfCancellationRequested();
                string final = Path.Combine(cache, VersionFolder);
                if (Directory.Exists(final)) final += "-" + Guid.NewGuid().ToString("N");
                Directory.Move(unpacked, final);
                return new Pair { Encoder = Path.Combine(final, "bin", "ffmpeg.exe"), Probe = Path.Combine(final, "bin", "ffprobe.exe") };
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }

        private static async Task Download(string target, CancellationToken token)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage.RequestUri.Scheme != "https") throw new IOException("HTTPS 다운로드만 허용됩니다.");
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != ArchiveBytes)
                    throw new IOException("다운로드 크기가 고정된 배포 파일과 다릅니다.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        total += count; if (total > ArchiveBytes) throw new IOException("다운로드 크기 제한을 넘었습니다.");
                        await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        progress = "영상 도구 다운로드 중 · " + total / 1048576 + " / " + ArchiveBytes / 1048576 + " MB";
                    }
                    if (total != ArchiveBytes) throw new IOException("다운로드가 끝나기 전에 연결이 끊어졌습니다.");
                }
            }
        }

        internal static void VerifyArchive(string path, string expectedHash)
        {
            using (var input = File.OpenRead(path)) using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("다운로드 파일의 SHA-256 검증에 실패했습니다.");
            }
        }

        internal static bool SafeArchivePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal) || path.Contains(":")) return false;
            return !path.Replace('\\', '/').Split('/').Any(part => part == ".." || part == "." || part.IndexOf('\0') >= 0);
        }

        internal static void ExtractArchive(string path, string destination, CancellationToken token)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.Entries.Count > 5000) throw new IOException("압축 파일에 항목이 너무 많습니다.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!SafeArchivePath(entry.FullName) || (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)) throw new IOException("안전하지 않은 압축 경로입니다.");
                    string normalized = entry.FullName.Replace('\\', '/');
                    string relative = null;
                    if (normalized.EndsWith("/bin/ffmpeg.exe", StringComparison.Ordinal)) relative = "bin/ffmpeg.exe";
                    else if (normalized.EndsWith("/bin/ffprobe.exe", StringComparison.Ordinal)) relative = "bin/ffprobe.exe";
                    else if (entry.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) relative = entry.Name;
                    if (relative == null) continue;
                    if (!names.Add(relative) || entry.Length > 300 * 1024 * 1024 || (total += entry.Length) > 600 * 1024 * 1024)
                        throw new IOException("압축 파일의 중복 항목 또는 크기 제한에 걸렸습니다.");
                    string output = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (var input = entry.Open()) using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[81920]; long written = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        { token.ThrowIfCancellationRequested(); written += count; if (written > entry.Length) throw new IOException("압축 해제 크기가 다릅니다."); target.Write(buffer, 0, count); }
                        if (written != entry.Length) throw new IOException("압축 파일이 잘렸습니다.");
                    }
                }
                if (!names.Contains("bin/ffmpeg.exe") || !names.Contains("bin/ffprobe.exe")) throw new IOException("필요한 두 실행 파일을 찾지 못했습니다.");
            }
        }

        private static void RejectRedirectedAncestors(string path)
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 폴더에는 자동 설치하지 않습니다. 도구 경로를 직접 지정하세요.");
        }
    }
}
