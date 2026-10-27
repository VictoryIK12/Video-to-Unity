using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    internal static class VideoEffectFileTransfer
    {
        internal static async Task<string> CopyNewFile(string source, string target, CancellationToken token)
        {
            bool ownsTarget = false;
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target)),
                ".vtu-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Output already exists: " + target);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    ownsTemporary = true;
                    await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                // Publish a complete file on the same volume. Unity ignores the dot-prefixed temporary.
                File.Move(temporary, target);
                ownsTemporary = false;
                ownsTarget = true;
                token.ThrowIfCancellationRequested();
                return target;
            }
            catch
            {
                if (ownsTarget) DeleteCopiedFile(target);
                if (ownsTemporary) DeleteCopiedFile(temporary);
                throw;
            }
        }

        internal static void DeleteCopiedFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        internal static void DeleteTemporary(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VideoToUnity", "Editor")) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
                throw new IOException("임시 폴더 범위 확인에 실패했습니다.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
