// ModAudio.cs
// Remake mods: loading a mod's audio file (.ogg, .wav, .mp3) into an AudioClip, for ModVoices and ModMusic. Unity can't
// decode compressed audio from bytes, so the file is read with UnityWebRequestMultimedia from disk: a folder mod's own
// file, a zip mod's copied into the cache first. In the background; the
// callback gets the clip, or null when it couldn't be read (logged). (The zip copy is ModInfo.LocalFile, shared with the
// textures' background decoding.)

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GoF2Remake.Modding
{
    public static class ModAudio
    {
        public static readonly string[] Extensions = { ".ogg", ".wav", ".mp3" };

        /// <summary>The AudioType of a file name (Unknown: not one of the Extensions).</summary>
        public static AudioType TypeOf(string file)
        {
            string ext = Path.GetExtension(file ?? "").ToLowerInvariant();
            return ext == ".ogg" ? AudioType.OGGVORBIS : ext == ".wav" ? AudioType.WAV : ext == ".mp3" ? AudioType.MPEG : AudioType.UNKNOWN;
        }

        /// <summary>The AudioType a file's first bytes show (RIFF..WAVE, OggS, an ID3 tag or an MPEG frame sync; Unknown: none of
        /// them). The Mod Builder saved every picked file as .ogg until 2026-10-24 (the pickers' copy is "picked.tmp"), so a
        /// WAV or MP3 under an .ogg name failed to decode.</summary>
        public static AudioType Sniff(byte[] head, int count)
        {
            if (head == null || count < 3) return AudioType.UNKNOWN;
            if (count >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E') return AudioType.WAV;
            if (count >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return AudioType.OGGVORBIS;
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return AudioType.MPEG;
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return AudioType.MPEG;
            return AudioType.UNKNOWN;
        }

        /// <summary>Sniff on a file on disk (Unknown when it can't be read).</summary>
        public static AudioType SniffFile(string path)
        {
            try
            {
                var head = new byte[12];
                using var f = File.OpenRead(path);
                return Sniff(head, f.Read(head, 0, head.Length));
            }
            catch { return AudioType.UNKNOWN; }
        }

        /// <summary>The extension (with the dot) for an AudioType of Sniff, else null.</summary>
        public static string ExtensionOf(AudioType type) =>
            type == AudioType.WAV ? ".wav" : type == AudioType.OGGVORBIS ? ".ogg" : type == AudioType.MPEG ? ".mp3" : null;

        /// <summary>The file's own spelling in the mod (any case; null: no such file).</summary>
        public static string Find(ModSource source, string path)
        {
            foreach (var f in source.Files) if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>Loads 'file' of 'mod' and calls 'done' with the clip or null. 'compressed': kept compressed in memory
        /// (music and voices are long); sound effects are decoded once (no decoding per play).</summary>
        public static async void Load(ModInfo mod, string file, Action<AudioClip> done, bool compressed = true)
        {
            AudioClip clip = null;
            try
            {
                var type = TypeOf(file);
                if (type == AudioType.UNKNOWN) throw new Exception("not an .ogg, .wav or .mp3 file");
                // A zip mod's file is copied out on a worker thread (ModInfo.LocalFile).
                // The content decides over the name (a WAV saved as .ogg, see Sniff).
                AudioType sniffed = AudioType.UNKNOWN;
                string path = await System.Threading.Tasks.Task.Run(() =>
                {
                    string p = mod.LocalFile(file);
                    if (p != null) sniffed = SniffFile(p);
                    return p;
                });
                if (path == null) throw new Exception("can't be read");
                if (sniffed != AudioType.UNKNOWN) type = sniffed;
                using var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
                if (req.downloadHandler is DownloadHandlerAudioClip handler) handler.compressed = compressed;
                var op = req.SendWebRequest();
                while (!op.isDone) await Awaitable.NextFrameAsync();
                if (req.result != UnityWebRequest.Result.Success) throw new Exception(req.error);
                clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip != null) clip.name = Path.GetFileNameWithoutExtension(file);
            }
            catch (Exception e) { Debug.LogWarning($"Mods: {mod.Id}: {file}: {e.Message}"); clip = null; }
            done?.Invoke(clip);
        }
    }
}
