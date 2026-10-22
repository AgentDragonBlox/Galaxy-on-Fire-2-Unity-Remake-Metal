// ModSounds.cs
// Remake mods: sound effects. A mod that is on ships files in its sounds folder (sounds/<name>.ogg | .wav | .mp3) named like
// one of the game's clips (the file names in Assets/Audio/<bank>/, e.g. Jumpgate_3b, Explosion_Small_01, Menu_Click_01; the
// FMOD event list in Reference/research/fmod_event_ids.txt names them per event): each replaces that clip wherever the
// game plays it (Get, at every place that plays a clip: Sfx, ShotVoices, the engines, the loops, the UI's PlayOneShot...).
// Voices and weapon shots are clips like any other. Music is ModMusic's (music/). A later mod in the load order wins.
// A file of any other name is a new sound: the quests' Sound step and /sound play it by name (Named, Has).
// Loaded in the background when the mods change (Preload; the main menu's loading screen waits, ModLoading), decoded once
// (sound effects are short and play often).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GoF2Remake.Modding
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModSounds
    {
        public const string Folder = "sounds";

        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The game's clips already looked up (-> the replacement, or null): AudioClip.name allocates.
        static readonly Dictionary<AudioClip, AudioClip> memo = new Dictionary<AudioClip, AudioClip>();
        static int revision = -1, loading;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { clips.Clear(); names.Clear(); memo.Clear(); revision = -1; loading = 0; }

        public static int Count => names.Count;
        public static int Loading => loading;

        /// <summary>Every sound of the mods that are on is loaded (or failed).</summary>
        public static bool Ready { get { Preload(); return loading == 0; } }

        /// <summary>Loads the mods' sounds again when the mods changed (no-op otherwise).</summary>
        public static void Preload()
        {
            if (revision == ModManager.Revision) return;
            revision = ModManager.Revision;
            clips.Clear();
            names.Clear();
            memo.Clear();
            var files = new Dictionary<string, (ModInfo mod, string file)>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in ModManager.Active)
                foreach (string f in mod.Source.FilesIn(Folder, ModAudio.Extensions))
                    files[Path.GetFileNameWithoutExtension(f)] = (mod, f);
            int started = revision;
            foreach (var kv in files)
            {
                string name = kv.Key;
                names.Add(name);
                loading++;
                ModAudio.Load(kv.Value.mod, kv.Value.file, clip =>
                {
                    if (started != revision) return;
                    loading--;
                    if (clip == null) return;
                    clip.name = name;
                    clips[name] = clip;
                    memo.Clear();
                }, false);
            }
            if (files.Count > 0) Debug.Log($"Mods: {files.Count} sound effect(s) from the mods");
        }

        /// <summary>A mod's sound by its file name (a new one, or a replacement played by name), once loaded; else null.</summary>
        public static AudioClip Named(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (revision != ModManager.Revision) Preload();
            return clips.TryGetValue(name, out var c) ? c : null;
        }

        /// <summary>A mod that is on has a sound of that name (loaded or still loading).</summary>
        public static bool Has(string name)
        {
            if (revision != ModManager.Revision) Preload();
            return !string.IsNullOrEmpty(name) && names.Contains(name);
        }

        /// <summary>The names of the mods' sounds.</summary>
        public static IEnumerable<string> Names { get { if (revision != ModManager.Revision) Preload(); return names; } }

        /// <summary>The game's clip, or a mod's replacement of the same name. Cheap when no mod has sounds.</summary>
        public static AudioClip Get(AudioClip clip)
        {
            if (clip == null) return null;
            if (revision != ModManager.Revision) Preload();
            if (clips.Count == 0) return clip;
            if (!memo.TryGetValue(clip, out var r))
            {
                r = clips.TryGetValue(clip.name, out var c) && c != clip ? c : null;
                memo[clip] = r;
            }
            return r != null ? r : clip;
        }
    }
}
