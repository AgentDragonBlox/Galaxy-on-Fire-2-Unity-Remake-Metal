// UiShots.cs
// Remake (Editor): screenshots of the screen on show at every size and device class the UI must work at (docs/UI-DESIGN.md
// "Checking a screen"): the Game view is set to each resolution in turn (PlayModeWindow's custom rendering resolution),
// UiLayout's device class forced through PlayerPrefs "debug_ui_device", and a PNG written per step into
// Logs/UiShots/<tag>/ (git-ignored), plus a contact sheet of them all made by Reference/tools/ui/contact_sheet.py.
// Play mode only. GoF2 > Tools > UI Screenshots, or from a script / the unity CLI: UiShots.Run("station"), then poll
// UiShots.Status until it reads "done".

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GoF2Remake.EditorTools
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class UiShots
    {
        /// <summary>One step: a screen size and the device class (UiLayout.Device + 1, 0 = detect).</summary>
        public struct Shot
        {
            public string name;
            public int width, height, device;
            public Shot(string name, int width, int height, int device) { this.name = name; this.width = width; this.height = height; this.device = device; }
        }

        /// <summary>The sizes the UI is checked at (docs/UI-DESIGN.md "Supported screens").</summary>
        public static readonly Shot[] Standard =
        {
            new Shot("desktop-1080p", 1920, 1080, 1),
            new Shot("desktop-720p", 1280, 720, 1),
            new Shot("desktop-4x3", 1440, 1080, 1),
            new Shot("desktop-1x1", 1080, 1080, 1),
            new Shot("desktop-21x9", 2560, 1080, 1),
            new Shot("desktop-32x9", 3840, 1080, 1),
            new Shot("deck-1280x800", 1280, 800, 2),
            new Shot("tv-1080p", 1920, 1080, 4),
            new Shot("phone-20x9", 2400, 1080, 3),
            new Shot("phone-16x9", 1920, 1080, 3),
        };

        /// <summary>How long each size is left to settle before its screenshot.</summary>
        public static double SettleSeconds = 1.5;

        public static string Status { get; private set; } = "idle";
        public static string Folder { get; private set; }

        static readonly Queue<Shot> queue = new Queue<Shot>();
        static Shot current;
        static double due;
        static bool captured;
        static uint oldW, oldH;
        static int oldDevice;

        [MenuItem("GoF2/Tools/UI Screenshots")]
        static void Menu() => Run("screen");

        /// <summary>Starts the series (Standard unless 'shots' is given) for the screen on show.</summary>
        public static void Run(string tag, Shot[] shots = null)
        {
            if (!EditorApplication.isPlaying) { Status = "error: Play mode only"; Debug.LogWarning("UiShots: Play mode only."); return; }
            if (queue.Count > 0) { Status = "error: busy"; return; }
            Folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "UiShots", tag));
            Directory.CreateDirectory(Folder);
            foreach (var f in Directory.GetFiles(Folder, "*.png")) File.Delete(f);
            PlayModeWindow.GetRenderingResolution(out oldW, out oldH);
            oldDevice = PlayerPrefs.GetInt("debug_ui_device", 0);
            foreach (var s in shots ?? Standard) queue.Enqueue(s);
            Status = "running";
            Next();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Next()
        {
            current = queue.Dequeue();
            PlayerPrefs.SetInt("debug_ui_device", current.device);
            PlayModeWindow.SetCustomRenderingResolution((uint)current.width, (uint)current.height, "UiShot");
            // The UI settles first: the size change, the screens' UpdateLayout and safe-area pass, and their transitions
            // (fades, the menus flying in) run out.
            due = EditorApplication.timeSinceStartup + SettleSeconds;
            captured = false;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { Finish("error: Play mode ended"); return; }
            if (EditorApplication.timeSinceStartup < due) return;
            string path = Path.Combine(Folder, current.name + ".png");
            if (!captured)
            {
                ScreenCapture.CaptureScreenshot(path);
                captured = true;
                due = EditorApplication.timeSinceStartup + 0.5;   // written at the end of a later frame
                return;
            }
            if (queue.Count > 0) { Next(); return; }
            Finish("done");
        }

        static void Finish(string status)
        {
            EditorApplication.update -= Tick;
            queue.Clear();
            PlayerPrefs.SetInt("debug_ui_device", oldDevice);
            if (oldW > 0 && oldH > 0) PlayModeWindow.SetCustomRenderingResolution(oldW, oldH, "UiShot");
            Status = status;
            Debug.Log($"UiShots: {status}, {Folder}");
        }
    }
}
