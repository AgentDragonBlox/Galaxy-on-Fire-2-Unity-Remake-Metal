// GameAudioIndexBuilder.cs
// GoF2 > Build > Mod Builder Audio Previews: Resources/GoF2ModBuilder/GameAudioIndex (Modding.GameAudioIndex), the game's
// sound effects a mod replaces (the list Resources/GoF2ModBuilder/SoundNames.txt, "folder/name" under Assets/Audio) and its
// music tracks (Assets/Audio/MUSIC, DLC_MUSIC, DLC2_MUSIC), so the Mod Builder's Audio pickers can play them. Run again after
// changing that list or the audio folders.

using System.IO;
using System.Linq;
using GoF2Remake.Modding;
using UnityEditor;
using UnityEngine;

namespace GoF2Remake.EditorTools
{
    public static class GameAudioIndexBuilder
    {
        const string AssetPath = "Assets/Resources/" + GameAudioIndex.ResourcePath + ".asset";
        static readonly string[] MusicFolders = { "Assets/Audio/MUSIC", "Assets/Audio/DLC_MUSIC", "Assets/Audio/DLC2_MUSIC" };

        [MenuItem("GoF2/Build/Mod Builder Audio Previews", priority = 210)]
        public static void Build()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameAudioIndex>(AssetPath);
            if (asset == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                asset = ScriptableObject.CreateInstance<GameAudioIndex>();
                AssetDatabase.CreateAsset(asset, AssetPath);
            }
            asset.names.Clear();
            asset.clips.Clear();
            int missing = 0;
            var list = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/GoF2ModBuilder/SoundNames.txt");
            foreach (var line in (list != null ? list.text : "").Replace("\r", "").Split('\n').Select(l => l.Trim()))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string folder = "Assets/Audio/" + Path.GetDirectoryName(line).Replace('\\', '/'), name = Path.GetFileName(line);
                var clip = AssetDatabase.FindAssets(name + " t:AudioClip", new[] { folder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetFileNameWithoutExtension(p) == name)
                    .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
                    .FirstOrDefault();
                if (clip == null) { missing++; Debug.LogWarning($"Mod Builder audio previews: no clip {line}"); continue; }
                asset.names.Add(name);
                asset.clips.Add(clip);
            }
            foreach (var folder in MusicFolders)
                foreach (var path in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (asset.names.Contains(name)) continue;
                    asset.names.Add(name);
                    asset.clips.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(path));
                }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log($"Mod Builder audio previews: {asset.clips.Count} clips{(missing > 0 ? $", {missing} not found" : "")} in {AssetPath}");
        }
    }
}
