using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoToUnity.Editor;

internal static class FileTransferTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }

    public static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        string work = Path.GetFullPath(Path.Combine(output, "file-tests-" + Guid.NewGuid().ToString("N")));
        if (!work.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Test directory escaped output.");
        Directory.CreateDirectory(work);
        Environment.SetEnvironmentVariable("TMP", work);
        Environment.SetEnvironmentVariable("TEMP", work);
        try
        {
            string source = Path.Combine(work, "source.bin");
            byte[] bytes = Enumerable.Range(0, 65536).Select(x => (byte)(x % 251)).ToArray();
            File.WriteAllBytes(source, bytes);
            string target = Path.Combine(work, "new.bin");
            await VideoEffectFileTransfer.CopyNewFile(source, target, CancellationToken.None);
            Check(File.ReadAllBytes(target).SequenceEqual(bytes), "new copy is byte-exact");

            File.WriteAllText(target, "existing asset must survive");
            bool refused = false;
            try { await VideoEffectFileTransfer.CopyNewFile(source, target, CancellationToken.None); }
            catch (IOException) { refused = true; }
            Check(refused && File.ReadAllText(target) == "existing asset must survive", "existing destination is not overwritten or deleted");

            using (var token = new CancellationTokenSource())
            {
                token.Cancel();
                bool cancelled = false;
                string before = Path.Combine(work, "cancel-before.bin");
                try { await VideoEffectFileTransfer.CopyNewFile(source, before, token.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !File.Exists(before), "pre-cancellation creates no destination");
                try { await VideoEffectFileTransfer.CopyNewFile(source, target, token.Token); }
                catch (OperationCanceledException) { }
                Check(File.ReadAllText(target) == "existing asset must survive", "cancellation preserves competing existing destination");
            }

            string large = Path.Combine(work, "large.bin");
            using (var file = File.Create(large)) file.SetLength(128 * 1024 * 1024);
            using (var token = new CancellationTokenSource())
            {
                string partial = Path.Combine(work, "partial.bin");
                Task<string> copying = VideoEffectFileTransfer.CopyNewFile(large, partial, token.Token);
                token.Cancel();
                bool cancelled = false;
                try { await copying; } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !File.Exists(partial), "mid-copy cancellation removes only its own partial destination");
                Check(new FileInfo(large).Length == 128 * 1024 * 1024, "cancelled copy leaves source intact");
                Check(Directory.GetFiles(work, ".partial.bin.*.tmp").Length == 0, "cancelled atomic copy leaves no hidden temporary");
            }

            string raced = Path.Combine(work, "raced.bin");
            Task<string> racingCopy = VideoEffectFileTransfer.CopyNewFile(large, raced, CancellationToken.None);
            File.WriteAllText(raced, "competing output created during copy");
            bool collision = false;
            try { await racingCopy; } catch (IOException) { collision = true; }
            Check(collision && File.ReadAllText(raced) == "competing output created during copy", "atomic publication preserves a competing destination");
            Check(Directory.GetFiles(work, ".raced.bin.*.tmp").Length == 0, "publication collision cleans only its hidden temporary");

            bool missing = false;
            string missingTarget = Path.Combine(work, "missing-source.bin");
            try { await VideoEffectFileTransfer.CopyNewFile(Path.Combine(work, "absent.bin"), missingTarget, CancellationToken.None); }
            catch (FileNotFoundException) { missing = true; }
            Check(missing && !File.Exists(missingTarget), "missing source creates no output");

            string temp = Path.Combine(Path.GetTempPath(), "VideoToUnity", "Editor", Guid.NewGuid().ToString("N"));
            Check(Path.GetFullPath(temp).StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "temporary test directory stays inside owned output");
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "owned.tmp"), "temporary");
            VideoEffectFileTransfer.DeleteTemporary(temp);
            Check(!Directory.Exists(temp), "owned GUID temporary directory is cleaned");
            bool rejected = false;
            try { VideoEffectFileTransfer.DeleteTemporary(work); } catch (IOException) { rejected = true; }
            Check(rejected && File.Exists(source), "cleanup rejects paths outside its dedicated GUID directory");
            Console.WriteLine("PASS: " + checks + " file-lifecycle checks; no Unity process or stubs.");
            return 0;
        }
        finally
        {
            // work is the checked, newly created child of the supplied validation output.
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
