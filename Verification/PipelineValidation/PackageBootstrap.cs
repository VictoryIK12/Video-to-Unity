using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace VideoToUnity.PipelineValidation
{
    public static class PackageBootstrap
    {
        private static AddAndRemoveRequest request;
        private static double deadline;

        public static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing argument " + key);
            return args[index + 1];
        }

        public static void Install()
        {
            string path = Path.GetFullPath(Argument("-vtuPackage")).Replace('\\', '/');
            request = Client.AddAndRemove(new[] { "file:" + path }, Array.Empty<string>());
            deadline = EditorApplication.timeSinceStartup + 600;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > deadline) Finish(2, "Package request timed out.");
                return;
            }
            if (request.Status != StatusCode.Success) { Finish(1, request.Error.message); return; }
            Finish(0, string.Join("\n", request.Result.Select(package => package.name + "@" + package.version)));
        }

        private static void Finish(int code, string message)
        {
            EditorApplication.update -= Poll;
            File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "package-install-result.txt"), "exit=" + code + "\n" + message);
            Debug.Log("VTU package installation: " + message);
            EditorApplication.Exit(code);
        }
    }
}
