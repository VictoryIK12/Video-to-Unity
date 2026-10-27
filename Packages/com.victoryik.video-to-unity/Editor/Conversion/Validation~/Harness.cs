using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VideoToUnity.Editor;

// Standalone test host. Unity ignores this entire Validation~ folder.
internal static class Harness
{
    private static int Main(string[] args)
    {
        try
        {
            if (args[0] == "verify") return VerifyCorruptedOutput(args);
            if (args[0] == "verify-metadata" || args[0] == "verify-bitstream") return VerifyBadMetadata(args);
            var watch = Stopwatch.StartNew();
            using (var job = VideoConverter.Start(args[1], args[2], args[3], args[4],
                args[5] == "packed" ? VideoConversionFormat.PackedH264 : VideoConversionFormat.AlphaWebM))
            {
                if (args[0] == "cancel" || args[0] == "dispose")
                {
                    string stage = args.Length > 6 ? args[6] : "";
                    while (!job.Completion.IsCompleted && !job.Status.StartsWith(stage, StringComparison.Ordinal))
                    {
                        if (watch.Elapsed.TotalSeconds > 90) throw new Exception("Did not reach cancellation stage: " + stage + "; " + job.Status);
                        Thread.Sleep(1);
                    }
                    if (job.Completion.IsCompleted) { job.Completion.GetAwaiter().GetResult(); throw new Exception("Job finished before requested cancellation stage."); }
                    var cancelTime = Stopwatch.StartNew();
                    if (args[0] == "dispose") job.Dispose(); else job.Cancel();
                    if (cancelTime.ElapsedMilliseconds > 100) throw new Exception("Cancellation blocked caller.");
                    try { job.Completion.GetAwaiter().GetResult(); throw new Exception("Cancellation was not observed."); }
                    catch (OperationCanceledException) { }
                    if (!job.Completion.IsCanceled) throw new Exception("Cancellation did not cancel Completion.");
                    Console.WriteLine("CANCELLED " + cancelTime.ElapsedMilliseconds + "ms; " + job.Status);
                }
                else if (args[0] == "parallel")
                {
                    using (var second = VideoConverter.Start(args[1], args[2], args[3], args[4]))
                    {
                        Task.WhenAll(job.Completion, second.Completion).GetAwaiter().GetResult();
                        if (job.Completion.Result.FilePath == second.Completion.Result.FilePath) throw new Exception("Jobs shared an output path.");
                        Console.WriteLine("PASS concurrent jobs with unique outputs");
                    }
                }
                else
                {
                    var result = job.Completion.GetAwaiter().GetResult();
                    if (!File.Exists(result.FilePath)) throw new Exception("Missing result.");
                    File.Copy(result.FilePath, args[6], false);
                    Console.WriteLine("PASS " + result.Format + " " + result.Width + "x" + result.Height + " frames=" + result.Frames +
                        " alpha=" + result.MinimumAlpha + ".." + result.MaximumAlpha + " " + watch.ElapsedMilliseconds + "ms");
                }
            }
            if (Directory.GetFileSystemEntries(args[2]).Length != 0) throw new Exception("Temporary files remain after disposal.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    // Exercise the SAME private pixel/timing verifier with deliberately corrupt real outputs.
    // Reflection is test-only; production API stays the agreed contract.
    private static int VerifyCorruptedOutput(string[] args)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(VideoConverter);
        var job = type.GetConstructors(flags)[0].Invoke(new object[] { args[1], args[2], args[3], args[4], VideoConversionFormat.PackedH264 });
        object Invoke(string method, params object[] parameters) { return type.GetMethod(method, flags).Invoke(job, parameters); }
        var candidate = Invoke("Probe", args[1]);
        int width = int.Parse(args[7]), height = int.Parse(args[8]);
        using (var reference = File.OpenRead(args[6]))
        {
            bool alpha = args[5] == "alpha";
            long bytesPerFrame = (long)width * height * (alpha ? 1 : 3);
            string filter = alpha ? "scale=in_range=limited:out_range=full,format=rgb24,crop=" + width + ":" + height + ":" + width + ":0:exact=1,extractplanes=r"
                : "scale=out_range=full,format=rgb24" + (args[5] == "color-webm" ? "" : ",crop=" + width + ":" + height + ":0:0:exact=1");
            try
            {
                Invoke("ScanRaw", args[1], candidate, filter, alpha ? "gray" : "rgb24", reference.Length, null, reference, alpha, bytesPerFrame);
                throw new Exception("Corrupt output was accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
            {
                Console.WriteLine("REJECTED CORRUPTION " + ex.InnerException.Message);
                return 0;
            }
        }
    }

    private static int VerifyBadMetadata(string[] args)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(VideoConverter);
        var format = args[5] == "packed" ? VideoConversionFormat.PackedH264 : VideoConversionFormat.AlphaWebM;
        var job = type.GetConstructors(flags)[0].Invoke(new object[] { args[1], args[2], args[3], args[4], format });
        object Invoke(string method, params object[] parameters) { return type.GetMethod(method, flags).Invoke(job, parameters); }
        var input = Invoke("Probe", args[1]);
        var candidate = Invoke("Probe", args[6]);
        type.GetField("workingDirectory", flags).SetValue(job, args[2]);
        type.GetField("outputPath", flags).SetValue(job, args[6]);
        // An initial container failure must FAIL this test, not be mistaken for the desired
        // independent SPS rejection. Python has also checked these container values.
        if (args[0] == "verify-bitstream") Invoke("ValidateColorMetadata", candidate, input);
        try
        {
            if (args[0] == "verify-bitstream")
            {
                Invoke("ValidatePackedBitstream", input);
            }
            else Invoke("ValidateColorMetadata", candidate, input);
            throw new Exception("Incorrect metadata was accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        { Console.WriteLine("REJECTED METADATA " + ex.InnerException.Message); return 0; }
        finally { Invoke("Cleanup", false); }
    }
}
