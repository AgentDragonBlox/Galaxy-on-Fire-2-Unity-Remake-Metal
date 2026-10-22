// ModVoices.cs
// Remake mods: voice-over for the event graphs' dialogue and radio lines. A page's text may hold "[voice <clip>]" (taken
// out of the text, Take): the clip is one of the original voice lines by its event name (StoryAssets.Voice, e.g.
// MSG_MISSION_24_NO_EQUIPMENT_INSTALLED) or an audio file a mod that is on ships: voices/<language>/<clip>.ogg | .wav |
// .mp3 in any of the game's text languages (Languages: with the voice language option on "as text" the text language
// first, then the game's own voice bank, de or en; fixed on English / German that one), else voices/<clip>.<ext>; a later
// mod in the load order wins.
// The files load in the background (Preload, when the page arrives; ModAudio); StoryAssets.Voice then finds them (Get), so
// the dialogue window and the radio box play them like the story's voices (EventScreen holds a dialogue up to 3 s for its
// clips). Every game resolves the clip from its own mods (a session's are the same for all).
// A file named like one of the game's own lines (voices/<clip> for every language, voices/<language>/ for one) replaces
// that line wherever it is spoken, the story's included (Replacement, which StoryAssets.Voice asks first); those load with
// the mods (PreloadReplacements, ModLoading).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GoF2Remake.Data;
using UnityEngine;

namespace GoF2Remake.Modding
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModVoices
    {
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static int revision = -1;
        // The game's lines a mod replaces, keyed "en/<clip>", "de/<clip>" or "*/<clip>" (both languages).
        static readonly Dictionary<string, AudioClip> replacements = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static int replacementsRevision = -1, replacing;

        static readonly Regex Token = new Regex(@"\[voice\s+([A-Za-z0-9_\-./]+)\s*\]", RegexOptions.IgnoreCase);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { clips.Clear(); loading.Clear(); missing.Clear(); revision = -1; replacements.Clear(); replacementsRevision = -1; replacing = 0; }

        /// <summary>How many of the game's lines the mods that are on replace (loaded or loading).</summary>
        public static int ReplacementCount { get { PreloadReplacements(); return replacementCount; } }
        static int replacementCount;

        /// <summary>Every replacement of a game line is loaded (or failed).</summary>
        public static bool ReplacementsReady { get { PreloadReplacements(); return replacing == 0; } }

        /// <summary>Loads the mods' replacements of the game's lines again when the mods changed (no-op otherwise).</summary>
        public static void PreloadReplacements()
        {
            if (replacementsRevision == ModManager.Revision) return;
            var assets = StoryAssets.Load();
            if (assets == null) return;
            replacementsRevision = ModManager.Revision;
            replacements.Clear();
            replacing = 0;
            var files = new Dictionary<string, (ModInfo mod, string file)>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<(string lang, string folder)> { ("*", "voices") };
            foreach (var (code, _) in LanguageTables.Offered) folders.Add((code, "voices/" + code));
            foreach (var mod in ModManager.Active)
                foreach (var (lang, folder) in folders)
                    foreach (string f in mod.Source.FilesIn(folder, ModAudio.Extensions))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (assets.IsVoice(name)) files[lang + "/" + name] = (mod, f);   // a later mod wins
                    }
            replacementCount = files.Count;
            int started = replacementsRevision;
            foreach (var kv in files)
            {
                string key = kv.Key;
                replacing++;
                ModAudio.Load(kv.Value.mod, kv.Value.file, clip =>
                {
                    if (started != replacementsRevision) return;
                    replacing--;
                    if (clip != null) replacements[key] = clip;
                });
            }
            if (files.Count > 0) Debug.Log($"Mods: {files.Count} of the game's voice lines replaced");
        }

        /// <summary>A mod's replacement of the game's line in the voice language (else one for both languages), once loaded;
        /// null: the game's own.</summary>
        public static AudioClip Replacement(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            PreloadReplacements();
            if (replacements.Count == 0) return null;
            foreach (string lang in Languages)
                if (replacements.TryGetValue(lang + "/" + name, out var c)) return c;
            return replacements.TryGetValue("*/" + name, out var any) ? any : null;
        }

        /// <summary>The "[voice &lt;clip&gt;]" of a page's text (null: none); the text without it.</summary>
        public static string Take(ref string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("[voice", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var m = Token.Match(text);
            if (!m.Success) return null;
            text = (text.Substring(0, m.Index) + text.Substring(m.Index + m.Length)).Replace("  ", " ").Trim();
            return m.Groups[1].Value;
        }

        /// <summary>The languages a mod's voice line is looked for in, best first: with the voice language option on "as text"
        /// the text language, then the game's own voice bank (de / en); else the one the option names.</summary>
        static IEnumerable<string> Languages
        {
            get
            {
                string bank = Settings.GermanVoices ? "de" : "en";
                if (Settings.VoiceLanguage == "auto" && Settings.Language != bank) yield return Settings.Language;
                yield return bank;
            }
        }

        static string Key(string name) => string.Join(",", Languages) + "/" + name;

        static void CheckMods()
        {
            if (revision == ModManager.Revision) return;
            revision = ModManager.Revision;
            clips.Clear();
            missing.Clear();
        }

        /// <summary>A mod's clip once loaded (null: none, or still loading).</summary>
        public static AudioClip Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            CheckMods();
            return clips.TryGetValue(Key(name), out var c) ? c : null;
        }

        /// <summary>The clip is still loading.</summary>
        public static bool Pending(string name) => !string.IsNullOrEmpty(name) && loading.Contains(Key(name));

        /// <summary>Starts loading a mod's clip (nothing for an original voice line, one loaded or one no mod has).</summary>
        public static void Preload(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Contains("..")) return;
            CheckMods();
            string key = Key(name);
            if (clips.ContainsKey(key) || loading.Contains(key) || missing.Contains(key)) return;
            var assets = StoryAssets.Load();
            if (assets != null && assets.Voice(name) != null) return;   // an original line
            var mods = ModManager.Active;
            for (int m = mods.Count - 1; m >= 0; m--)
                foreach (string folder in Folders())
                    foreach (string ext in ModAudio.Extensions)
                    {
                        string file = ModAudio.Find(mods[m].Source, folder + name + ext);
                        if (file == null) continue;
                        int started = revision;
                        loading.Add(key);
                        ModAudio.Load(mods[m], file, clip =>
                        {
                            loading.Remove(key);
                            if (started != revision) return;   // the mods changed meanwhile
                            if (clip != null) clips[key] = clip; else missing.Add(key);
                        });
                        return;
                    }
            missing.Add(key);
            Debug.LogWarning($"Mods: no voice clip \"{name}\" (voices/<language>/{name}.ogg or voices/{name}.ogg in a mod that is on)");
        }

        static IEnumerable<string> Folders()
        {
            foreach (string lang in Languages) yield return "voices/" + lang + "/";
            yield return "voices/";
        }
    }
}
