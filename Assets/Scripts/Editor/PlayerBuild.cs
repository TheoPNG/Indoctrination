using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Indoctrination.EditorTools
{
    /// <summary>
    /// Builds a standalone player so the game can be played on real machines
    /// over a real network, rather than only in the Editor.
    ///
    /// Driven from the command line by Tools/Build/run.sh, which is how it is
    /// normally used - but it is also on the menu for a build from inside the
    /// Editor.
    /// </summary>
    public static class PlayerBuild
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Indoctrination/Build Player (this platform)")]
        public static void BuildForThisPlatform()
        {
            Build(EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>
        /// Entry point for -executeMethod. Reads -buildTarget from the command
        /// line so one script covers macOS and Windows, and exits non-zero on
        /// failure so a script calling it can tell.
        /// </summary>
        public static void BuildBatch()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;

            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--target");
            if (index >= 0 && index + 1 < args.Length
                && Enum.TryParse<BuildTarget>(args[index + 1], ignoreCase: true, out var requested))
            {
                target = requested;
            }

            EditorApplication.Exit(Build(target) ? 0 : 1);
        }

        private static bool Build(BuildTarget target)
        {
            var output = OutputPathFor(target);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                options = BuildOptions.None
            };

            Debug.Log($"Building {target} to {output}");
            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"BUILD SUCCEEDED  {output}  ({summary.totalSize / (1024 * 1024)} MB)");
                return true;
            }

            // Only the errors are worth printing; a failed build's log is enormous.
            foreach (var step in report.steps)
            {
                foreach (var message in step.messages.Where(
                             m => m.type is LogType.Error or LogType.Exception))
                {
                    Debug.Log($"BUILD ERROR  {message.content}");
                }
            }

            Debug.Log($"BUILD FAILED  {summary.result}, {summary.totalErrors} errors");
            return false;
        }

        private static string OutputPathFor(BuildTarget target) => target switch
        {
            BuildTarget.StandaloneOSX => "Build/macOS/Indoctrination.app",
            BuildTarget.StandaloneWindows64 => "Build/Windows/Indoctrination.exe",
            BuildTarget.StandaloneLinux64 => "Build/Linux/Indoctrination",
            _ => $"Build/{target}/Indoctrination"
        };
    }
}
