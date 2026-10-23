// AndroidGameActivity.cs
// Android builds start the game's own activity, com.unity3d.player.GoF2GameActivity (Assets/Plugins/Android/
// GoF2GameActivity.java: Unity's UnityPlayerGameActivity whose game view keeps the on-screen keyboard's Backspace repeat),
// in place of UnityPlayerGameActivity: the generated Gradle project's unityLibrary manifest gets its name here. A build
// whose manifest has no UnityPlayerGameActivity (the Activity entry point) is left alone, with a warning.

using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace GoF2Remake.EditorTools
{
    public class AndroidGameActivity : IPostGenerateGradleAndroidProject
    {
        const string Unity = "android:name=\"com.unity3d.player.UnityPlayerGameActivity\"";
        const string Own = "android:name=\"com.unity3d.player.GoF2GameActivity\"";

        public int callbackOrder => 1;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // 'path' is the unityLibrary module.
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest)) { Debug.LogWarning("AndroidGameActivity: no manifest at " + manifest); return; }
            string text = File.ReadAllText(manifest);
            if (text.Contains(Own)) return;
            if (!text.Contains(Unity))
            {
                Debug.LogWarning("AndroidGameActivity: no UnityPlayerGameActivity in the manifest: the keyboard's Backspace won't repeat");
                return;
            }
            File.WriteAllText(manifest, text.Replace(Unity, Own));
        }
    }
}
