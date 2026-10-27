using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using VideoToUnity.Editor;

namespace VideoToUnity.Tests
{
    public sealed class ToolchainSetupTests
    {
        private static readonly Type Setup = typeof(BackgroundMatte).Assembly.GetType("VideoToUnity.Editor.VideoToolchainSetup", true);
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private string temp;
        [SetUp] public void CreateTemp() { temp = Path.Combine(Path.GetTempPath(), "VideoToUnityToolTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); }
        [TearDown] public void DeleteTemp() { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        private static object Call(string name, params object[] args) => Setup.GetMethod(name, Flags).Invoke(null, args);
        private static void ThrowsIo(Action action) { Assert.IsInstanceOf<IOException>(Assert.Throws<TargetInvocationException>(() => action()).InnerException); }

        [TestCase("root/bin/ffmpeg.exe", true)]
        [TestCase("../ffmpeg.exe", false)]
        [TestCase("root/../../ffmpeg.exe", false)]
        [TestCase("root\\..\\ffmpeg.exe", false)]
        [TestCase("/tmp/ffmpeg.exe", false)]
        [TestCase("C:/ffmpeg.exe", false)]
        [TestCase("root/ffmpeg.exe:payload", false)]
        public void ArchivePathsCannotEscapeOrUseAlternateStreams(string value, bool safe)
        { Assert.AreEqual(safe, Call("SafeArchivePath", value)); }

        [Test] public void ArchiveHashRejectsTampering()
        {
            string file = Path.Combine(temp, "archive"); File.WriteAllText(file, "abc", new UTF8Encoding(false));
            const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            Call("VerifyArchive", file, hash);
            File.AppendAllText(file, "changed"); ThrowsIo(() => Call("VerifyArchive", file, hash));
        }

        [Test] public void ExtractionCopiesOnlyRequiredExecutablesAndNotices()
        {
            string zip = MakeZip("root/bin/ffmpeg.exe", "root/bin/ffprobe.exe", "root/LICENSE", "root/README.txt", "root/bin/ffplay.exe", "root/unknown.ps1");
            string dest = Path.Combine(temp, "result");
            Call("ExtractArchive", zip, dest, CancellationToken.None);
            Assert.IsTrue(File.Exists(Path.Combine(dest, "bin", "ffmpeg.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "bin", "ffprobe.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "LICENSE")));
            Assert.AreEqual(4, Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length);
        }

        [Test] public void ExtractionRejectsTraversalBeforeWritingOutsideDestination()
        {
            string zip = MakeZip("../escaped.exe", "root/bin/ffmpeg.exe", "root/bin/ffprobe.exe");
            ThrowsIo(() => Call("ExtractArchive", zip, Path.Combine(temp, "result"), CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(temp, "escaped.exe")));
        }

        [Test] public void ExtractionRejectsMissingProbeAndDuplicateExecutables()
        {
            string zip = MakeZip("root/bin/ffmpeg.exe");
            ThrowsIo(() => Call("ExtractArchive", zip, Path.Combine(temp, "missing"), CancellationToken.None));
            zip = MakeZip("root/bin/ffmpeg.exe", "other/bin/ffmpeg.exe", "root/bin/ffprobe.exe");
            ThrowsIo(() => Call("ExtractArchive", zip, Path.Combine(temp, "duplicate"), CancellationToken.None));
        }

        [Test] public void CancelledExtractionDoesNotPublishExecutables()
        {
            string zip = MakeZip("root/bin/ffmpeg.exe", "root/bin/ffprobe.exe");
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                var ex = Assert.Throws<TargetInvocationException>(() => Call("ExtractArchive", zip, Path.Combine(temp, "cancelled"), source.Token));
                Assert.IsInstanceOf<OperationCanceledException>(ex.InnerException);
                Assert.IsFalse(Directory.Exists(Path.Combine(temp, "cancelled")));
            }
        }

        [Test] public void ExecutableLookupIgnoresRelativePathEntries()
        { Assert.IsNull(Call("ResolveExecutable", "ffmpeg.exe", ".;relative-bin", true)); Assert.IsNull(Call("ResolveExecutable", "..\\ffmpeg.exe", temp, true)); }

        private string MakeZip(params string[] entries)
        {
            string zip = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string name in entries) using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("test");
            return zip;
        }
    }
}
