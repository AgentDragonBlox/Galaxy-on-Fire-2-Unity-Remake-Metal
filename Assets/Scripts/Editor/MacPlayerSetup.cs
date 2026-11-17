// MacPlayerSetup.cs  (Editor only, Apple fork)
// The macOS app's Info.plist keys, written after the build like IosPlayerSetup's (Unity has no Player setting for them):
//   LSApplicationCategoryType  action games (as on iOS): macOS takes the app for a game
//   LSSupportsGameMode (macOS 26+) + GCSupportsGameMode (macOS 14 / 15)   Game Mode while full screen: the CPU and GPU
//                        first, background work held back, twice the Bluetooth sampling rate for controllers and AirPods
//   GCSupportsControllerUserInteraction   the game takes a controller's input as its own (MacGameControllers)
// Edited with plutil (part of every macOS). The edit changes the bundle: sign it afterwards (codesign --force --deep -s -).

#if UNITY_EDITOR_OSX
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace GoF2Remake.EditorTools
{
    public static class MacPlayerSetup
    {
        [PostProcessBuild(100)]
        static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.StandaloneOSX) return;
            string plist = Path.Combine(path, "Contents", "Info.plist");
            if (!File.Exists(plist))
            {
                UnityEngine.Debug.LogWarning($"MacPlayerSetup: no Info.plist at {plist} (an Xcode project build?): keys not written.");
                return;
            }
            bool ok = Plutil(plist, "LSApplicationCategoryType", "-string", "public.app-category.action-games")
                & Plutil(plist, "LSSupportsGameMode", "-bool", "YES")
                & Plutil(plist, "GCSupportsGameMode", "-bool", "YES")
                & Plutil(plist, "GCSupportsControllerUserInteraction", "-bool", "YES");
            if (ok) UnityEngine.Debug.Log("MacPlayerSetup: Info.plist keys written (category, Game Mode, controllers).");
        }

        static bool Plutil(string plist, string key, string type, string value)
        {
            var info = new ProcessStartInfo("/usr/bin/plutil", $"-replace {key} {type} {value} \"{plist}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0) return true;
            UnityEngine.Debug.LogWarning($"MacPlayerSetup: plutil {key} failed: {error}");
            return false;
        }
    }
}
#endif
