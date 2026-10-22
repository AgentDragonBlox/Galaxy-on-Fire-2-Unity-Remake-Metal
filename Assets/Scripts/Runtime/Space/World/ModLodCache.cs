// ModLodCache.cs
// Remake (AutoLods): the lower levels of detail of the mods' models (ships, stations), made at run time from the model as
// the mod ships it: an optimized model (the Mod Builder's Optimize window) is level 0 as it is, an untouched one too. The
// cutting runs on a worker thread (LodMeshes.Simplify); an object shows level 0 alone until its levels are ready.
// Kept on disk for later plays: persistentDataPath/ModCache/Lods/<mod id>/<assembly>_<n>.lods, the levels of the n-th
// eligible mesh of that assembly, headed by the mesh's hash (LodMeshes.Hash: its vertices and triangles). A model that
// changed (optimized again, an undo, a new version) no longer matches: its file is deleted and the levels made again from
// the new mesh. The Optimize window also drops the mod's whole folder at once (Forget); ModManager.Scan deletes the folders
// of the mods no longer installed (PruneCache, through GameHooks.ModsScanned).

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace GoF2Remake.Visuals
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModLodCache
    {
        const int Magic = 0x444F4C4D;   // "MLOD"
        const int Version = 1;

        sealed class Job
        {
            public Task<int[][][]> task;
            public Mesh[] meshes;   // set once the task is done and the meshes are made
        }

        static readonly Dictionary<Mesh, Job> jobs = new Dictionary<Mesh, Job>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => jobs.Clear();

        /// <summary>persistentDataPath/ModCache/Lods (main thread only: persistentDataPath).</summary>
        static string Root => Path.Combine(GoF2Remake.GamePaths.Data, "ModCache", "Lods");

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var chars = s.ToCharArray();
            var bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++) if (System.Array.IndexOf(bad, chars[i]) >= 0 || chars[i] == '#') chars[i] = '_';
            return new string(chars);
        }

        /// <summary>The levels of a mod mesh (level 1, level 2...): null while they are being made (ask again next frame),
        /// empty when it gets none. 'modId' / 'slot' name its cache file.</summary>
        public static Mesh[] Get(string modId, string slot, Mesh mesh)
        {
            if (mesh == null) return System.Array.Empty<Mesh>();
            if (!jobs.TryGetValue(mesh, out var job))
            {
                Prune();
                job = new Job();
                jobs[mesh] = job;
                var captured = LodMeshes.Capture(mesh);
                if (captured == null) { job.meshes = System.Array.Empty<Mesh>(); return job.meshes; }
                string file = null;
                try { file = Path.Combine(Root, Safe(modId), Safe(slot) + ".lods"); }
                catch (System.Exception e) { Debug.LogWarning($"Mod LODs ({modId}): {e.Message}"); }
                job.task = Task.Run(() => LoadOrMake(captured, file, modId));
            }
            if (job.meshes != null) return job.meshes;
            if (!job.task.IsCompleted) return null;
            var list = new List<Mesh>();
            if (job.task.Status == TaskStatus.RanToCompletion && job.task.Result != null)
                for (int l = 0; l < job.task.Result.Length; l++)
                {
                    var m = LodMeshes.Build(mesh, job.task.Result[l], $"{mesh.name}_lod_gen{l + 1}");
                    if (m == null) break;
                    list.Add(m);
                }
            else if (job.task.Exception != null) Debug.LogWarning($"Mod LODs ({modId}, {mesh.name}): {job.task.Exception.GetBaseException().Message}");
            job.meshes = list.ToArray();
            job.task = null;
            return job.meshes;
        }

        /// <summary>The levels from the cache file when it was made from this very mesh, else made and written there (the old
        /// file of a changed model deleted first). A worker thread.</summary>
        static int[][][] LoadOrMake(LodMeshes.Captured c, string file, string modId)
        {
            ulong hash = LodMeshes.Hash(c);
            if (file != null && File.Exists(file))
            {
                try
                {
                    using (var r = new BinaryReader(File.OpenRead(file)))
                        if (r.ReadInt32() == Magic && r.ReadInt32() == Version && r.ReadUInt64() == hash)
                        {
                            var levels = LodMeshes.Read(r);
                            if (Valid(levels, c)) return levels;
                        }
                }
                catch (System.Exception e) { Debug.LogWarning($"Mod LODs ({modId}): {e.Message}"); }
                try { File.Delete(file); }   // made from another mesh (the model changed) or damaged
                catch (System.Exception e) { Debug.LogWarning($"Mod LODs ({modId}): {e.Message}"); }
            }
            var made = LodMeshes.Simplify(c);
            if (file != null)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    string tmp = file + ".tmp";
                    using (var w = new BinaryWriter(File.Create(tmp)))
                    {
                        w.Write(Magic); w.Write(Version); w.Write(hash);
                        LodMeshes.Write(w, made);
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
                catch (System.Exception e) { Debug.LogWarning($"Mod LODs ({modId}): {e.Message}"); }
            return made;
        }

        static bool Valid(int[][][] levels, LodMeshes.Captured c)
        {
            foreach (var level in levels)
            {
                if (level.Length != c.submeshes.Length) return false;
                foreach (var s in level) foreach (int i in s) if (i < 0 || i >= c.vertexCount) return false;
            }
            return true;
        }

        /// <summary>The levels of meshes that are gone (a mod reloaded or switched off) are freed.</summary>
        static void Prune()
        {
            List<Mesh> gone = null;
            foreach (var kv in jobs) if (kv.Key == null) (gone ??= new List<Mesh>()).Add(kv.Key);
            if (gone == null) return;
            foreach (var key in gone)
            {
                if (jobs[key].meshes != null) foreach (var m in jobs[key].meshes) if (m != null) Object.Destroy(m);
                jobs.Remove(key);
            }
        }

        /// <summary>Drops a mod's cached levels (the Mod Builder's Optimize window, before the game reads the project again):
        /// they are made again from the optimized models.</summary>
        public static void Forget(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;
            try
            {
                string dir = Path.Combine(Root, Safe(modId));
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (System.Exception e) { Debug.LogWarning($"Mod LODs ({modId}): {e.Message}"); }
        }

        /// <summary>Deletes the cached levels of the mods no longer installed (ModManager.Scan: 'installedIds' every mod id
        /// found, on or off).</summary>
        public static void PruneCache(IEnumerable<string> installedIds)
        {
            try
            {
                string root = Root;
                if (!Directory.Exists(root)) return;
                var keep = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var id in installedIds) keep.Add(Safe(id));
                foreach (var dir in Directory.GetDirectories(root))
                    if (!keep.Contains(Path.GetFileName(dir)))
                    {
                        Directory.Delete(dir, true);
                        Debug.Log($"Mod LODs: removed the cached levels of {Path.GetFileName(dir)} (no longer installed)");
                    }
            }
            catch (System.Exception e) { Debug.LogWarning($"Mod LODs cache: {e.Message}"); }
        }
    }
}
