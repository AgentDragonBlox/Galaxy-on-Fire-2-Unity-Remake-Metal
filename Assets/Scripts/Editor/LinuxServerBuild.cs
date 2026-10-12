// LinuxServerBuild.cs
// GoF2 > Build > Linux Dedicated Server: a headless Linux server build without the game's graphics and sound. Unity's
// Dedicated Server build (Linux Standalone, subtarget Server; the Unity Hub module "Linux Dedicated Server Build Support")
// with the dedicated server optimizations on: textures, audio and shader data are left out (the server draws and plays
// nothing), meshes keep only what isn't drawing data. Only the first scene goes in (the main menu, which the server
// switches off as it wakes: DedicatedServer.ShutOff); Resources/GoF2Data, the text tables, the web admin page, the events
// and the network prefabs stay, so the server runs exactly as the full build's -server mode. It starts as a server with no
// -server flag (UNITY_SERVER: DedicatedServer.Enabled), and the post-build step writes start-server.sh next to it
// (DedicatedServerLaunchers). Same code = same multiplayer fingerprint, so it serves the normal builds.
// Output: Build/LinuxServer/GoF2Server.x86_64. The active build target is switched to it for the build (BuildTargetGuard
// and URP want the build's own target active) and put back afterwards, the standalone subtarget too.

using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GoF2Remake.EditorTools
{
    public static class LinuxServerBuild
    {
        const string Output = "Build/LinuxServer/GoF2Server.x86_64";
        const string FirstScene = "Assets/Scenes/MainMenu.unity";

        [MenuItem("GoF2/Build/Linux Dedicated Server", priority = 230)]
        static void Build() => Run();

        /// <summary>The command line (docker/build-server.sh): -executeMethod GoF2Remake.EditorTools.LinuxServerBuild.Batch;
        /// exits 1 when the build fails (the menu item only logs it).</summary>
        public static void Batch() => EditorApplication.Exit(Run() ? 0 : 1);

        static bool Run()
        {
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);
            var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server);
            bool previousOptimizations = ServerOptimizations;
            try
            {
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
                {
                    EditorUtility.DisplayDialog("Linux Dedicated Server",
                        "Couldn't switch to the Linux server target. Install the Unity Hub module \"Linux Dedicated Server Build Support\" " +
                        "for this Editor version, then try again.", "OK");
                    return false;
                }
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.IL2CPP);   // like the Linux player
                ServerOptimizations = true;

                Directory.CreateDirectory(Path.GetDirectoryName(Output));
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { FirstScene },
                    locationPathName = Output,
                    target = BuildTarget.StandaloneLinux64,
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    options = BuildOptions.None,
                };
                var report = BuildPipeline.BuildPlayer(options);
                var s = report.summary;
                if (s.result == BuildResult.Succeeded)
                    Debug.Log($"GoF2: Linux dedicated server built: {Path.GetFullPath(Output)} ({s.totalSize / (1024 * 1024)} MB, {s.totalTime:mm\\:ss}).");
                else
                    Debug.LogError($"GoF2: the Linux dedicated server build {s.result} ({s.totalErrors} errors; see the Console).");
                return s.result == BuildResult.Succeeded;
            }
            finally
            {
                ServerOptimizations = previousOptimizations;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, previousBackend);
                EditorUserBuildSettings.standaloneBuildSubtarget = previousSubtarget;
                if (EditorUserBuildSettings.activeBuildTarget != previousTarget)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(previousGroup, previousTarget);
            }
        }

        /// <summary>Player Settings > Dedicated Server > "Enable Dedicated Server optimizations"
        /// (PlayerSettings.dedicatedServerOptimizations), read and written by reflection so an Editor without it still
        /// compiles (it then just builds without them).</summary>
        static bool ServerOptimizations
        {
            get
            {
                var p = typeof(PlayerSettings).GetProperty("dedicatedServerOptimizations");
                return p != null && p.GetValue(null) is bool b && b;
            }
            set
            {
                var p = typeof(PlayerSettings).GetProperty("dedicatedServerOptimizations");
                if (p != null && p.CanWrite) p.SetValue(null, value);
                else if (value) Debug.LogWarning("GoF2: this Editor has no dedicated server optimizations setting; the server build keeps the textures and audio.");
            }
        }
    }
}
