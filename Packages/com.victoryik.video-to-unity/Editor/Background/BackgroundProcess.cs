using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    /// <summary>
    /// One directly started FFmpeg/FFprobe child. No shell is involved; arguments are quoted for
    /// CreateProcess, stderr is drained concurrently into a bounded tail, and Kill() terminates
    /// only this exact child process. Children run below normal priority so their worker threads
    /// never starve the Editor's interactive threads. No UnityEngine dependency.
    /// </summary>
    internal sealed class BackgroundProcess : IDisposable
    {
        internal const int DiagnosticLimit = 16384;
        /// <summary>Per-stage FFmpeg thread cap: bounded CPU and memory on many-core machines.</summary>
        internal static readonly string Threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        private readonly Process process;
        private readonly Task<string> errors;
        private readonly string name;
        private int disposed;

        private BackgroundProcess(Process process, string name)
        {
            this.process = process; this.name = name;
            errors = Drain(process.StandardError);
        }

        public Stream Output { get { return process.StandardOutput.BaseStream; } }
        public Stream Input { get { return process.StandardInput.BaseStream; } }

        public static BackgroundProcess Start(string executable, IEnumerable<string> arguments, bool writeInput)
        {
            var quoted = new List<string>();
            foreach (var argument in arguments) quoted.Add(Quote(argument));
            var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = string.Join(" ", quoted), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8 } };
            try
            {
                if (!process.Start()) throw new IOException("Could not start video tool: " + executable);
            }
            catch (Win32Exception ex)
            {
                process.Dispose();
                throw new IOException("Could not start video tool '" + executable + "': " + StartFailure(ex), ex);
            }
            LowerPriority(process);
            if (!writeInput) process.StandardInput.Close();
            return new BackgroundProcess(process, Path.GetFileName(executable));
        }

        /// <summary>
        /// The OS reason only. The framework message also names the current working directory, which
        /// says nothing about the tool and must not end up in user-visible status or shared logs.
        /// </summary>
        internal static string StartFailure(Win32Exception ex)
        {
            string reason = new Win32Exception(ex.NativeErrorCode).Message;
            return string.IsNullOrWhiteSpace(reason) ? "system error " + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : reason;
        }

        /// <summary>Best effort; a platform that refuses the change keeps normal priority.</summary>
        internal static void LowerPriority(Process process)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
        }

        /// <summary>Run to completion while a background task consumes stdout.</summary>
        public static void Run(string executable, IEnumerable<string> arguments, Action<Stream> consume, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var child = Start(executable, arguments, false))
            {
                Task output = Task.Run(() => consume(child.Output));
                try
                {
                    while (!child.process.WaitForExit(100))
                        if (token.IsCancellationRequested || output.IsFaulted) child.Kill();
                    token.ThrowIfCancellationRequested();
                    // Keep a validation/pipe exception rather than replacing it with broken-pipe stderr.
                    output.GetAwaiter().GetResult();
                    child.Finish(token);
                }
                finally
                {
                    child.Dispose();
                    try { output.GetAwaiter().GetResult(); } catch { }
                }
            }
        }

        /// <summary>Wait (on the worker thread) for exit; any exit code or error-level diagnostic fails.</summary>
        public void Finish(CancellationToken token)
        {
            while (!process.WaitForExit(100)) if (token.IsCancellationRequested) Kill();
            string tail = errors.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(tail))
                throw new IOException(name + " failed (exit " + process.ExitCode + "): " + tail.Trim());
        }

        /// <summary>Best-effort diagnostics after a broken pipe. Kills the child if it does not exit promptly.</summary>
        public string Diagnostics()
        {
            if (!process.WaitForExit(2000)) Kill();
            while (!process.WaitForExit(100)) Kill();
            try { return errors.GetAwaiter().GetResult().Trim(); } catch (Exception ex) { return ex.Message; }
        }

        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(); } // Direct child only; never by name or tree.
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (NotSupportedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                // Called on the worker thread only. Files are never deleted while a writer is alive.
                while (!process.WaitForExit(100)) Kill();
                try { process.StandardInput.Close(); } catch { }
                try { errors.GetAwaiter().GetResult(); } catch { }
            }
            finally { process.Dispose(); }
        }

        private static async Task<string> Drain(StreamReader reader)
        {
            var tail = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > DiagnosticLimit) tail.Remove(0, tail.Length - DiagnosticLimit);
            }
            return tail.ToString();
        }

        /// <summary>Read lines from a tool's stdout with a bounded line length.</summary>
        public static void Lines(Stream pipe, Action<string> consume, CancellationToken token)
        {
            using (var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, true))
            {
                var line = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < count; i++)
                    {
                        char c = buffer[i];
                        if (c == '\n') { consume(line.ToString().TrimEnd('\r')); line.Clear(); }
                        else { line.Append(c); if (line.Length > DiagnosticLimit) throw new InvalidDataException("Video tool response line is too long."); }
                    }
                }
                if (line.Length != 0) consume(line.ToString().TrimEnd('\r'));
            }
        }

        /// <summary>Fill the buffer completely. Returns false only at a clean end of stream.</summary>
        public static bool ReadFrame(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new InvalidDataException("Decoded video ended inside a frame (" + offset + " of " + buffer.Length + " bytes); the source is truncated or damaged.");
                }
                offset += read;
            }
            return true;
        }

        public static string Executable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
                throw new ArgumentException("Specify the FFmpeg/FFprobe executable, without command arguments.");
            if (path.IndexOfAny(new[] { '/', '\\' }) >= 0 || Path.IsPathRooted(path))
            { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("Video tool not found.", path); }
            return path;
        }

        // ProcessStartInfo.Arguments quoting (embedded quotes, trailing backslashes). With
        // UseShellExecute=false, metacharacters and non-ASCII names remain literal arguments.
        internal static string Quote(string argument)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}

