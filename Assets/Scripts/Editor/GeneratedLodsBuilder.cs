// GeneratedLodsBuilder.cs  (Editor only)
// GoF2 > Build > Generated LODs: Resources/GoF2Data/generated_lods.bytes (Visuals.GameLods), the lower levels of detail of
// the game's own models for the "Levels of detail" option (Visuals.AutoLods). Every assembled object a level places
// (ships, stations, jumpgates, turrets, asteroids and the level objects under misc: the capital ships, the carrier, the
// battlestations...) that has no LOD meshes of the original's: each of its LOD 0 hull meshes (readable at run time,
// ImportPostprocessor.IsHullModel; not an additive / light / glow / emissive layer, LodMeshes.Eligible: 1000 triangles or
// more) cut by LodMeshes.Simplify (the Mod Builder optimizer's MeshSimplifier) to level 1 (50 %) and level 2 (20 %),
// where the seams allow. An object with the original's LOD meshes (the generic ships, freighters, battleship, jumpgates,
// asteroids) has them as copies of LOD 0 (AutoLods.CopiedLevels): the LOD 0 meshes they copy are cut the same way (from
// LodMeshes.MinTrianglesCopied, 500: the asteroids), and AutoLods swaps the copies for them. Stored as indices only, per mesh (LodMeshes.Key); the game makes the meshes when an object needs
// them. Interiors (hangars, bars), the star map, shop previews and effects are left out: always seen up close or flat.
// Run again after changing a model; the mods' models get theirs at run time (ModLodCache).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GoF2Remake.Data;
using GoF2Remake.Visuals;
using UnityEditor;
using UnityEngine;

namespace GoF2Remake.EditorTools
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class GeneratedLodsBuilder
    {
        const string OutPath = "Assets/Resources/" + GameLods.ResourcePath + ".bytes";

        /// <summary>The assemblies.json categories whose objects get made levels.</summary>
        static readonly HashSet<string> Categories = new HashSet<string> { "ships", "stations", "jumpgates", "turrets", "misc", "asteroids" };

        [MenuItem("GoF2/Build/Generated LODs", priority = 209)]
        public static void Build()
        {
            var db = Database.Load();
            var meshes = new SortedDictionary<string, Mesh>(System.StringComparer.Ordinal);
            var minimum = new Dictionary<string, int>();
            int objects = 0, original = 0, copied = 0;
            void Add(Mesh mesh, int min)
            {
                string key = LodMeshes.Key(mesh);
                meshes[key] = mesh;
                minimum[key] = minimum.TryGetValue(key, out int m) ? Mathf.Min(m, min) : min;
            }
            try
            {
                int n = 0;
                foreach (var a in db.Assemblies)
                {
                    n++;
                    if (a.pack == Modding.ModShips.Pack || !Categories.Contains(a.category)) continue;
                    if (n % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("GoF2", "Generated LODs: " + a.name, 0.2f * n / db.Assemblies.Count)) return;
                    var prefab = AssembledObject.LoadPrefab(a);
                    if (prefab == null) continue;
                    var group = prefab.GetComponent<LODGroup>();
                    if (group != null && group.lodCount > 1)
                    {
                        // The original's own LOD meshes, copies of LOD 0: their LOD 0 meshes cut for them.
                        original++;
                        bool cut = false;
                        foreach (var (_, hull, _) in AutoLods.CopiedLevels(prefab.transform, group.GetLODs()))
                        {
                            if (!hull.sharedMesh.isReadable || !LodMeshes.Eligible(hull.sharedMesh, LodMeshes.MinTrianglesCopied)) continue;
                            Add(hull.sharedMesh, LodMeshes.MinTrianglesCopied);
                            cut = true;
                        }
                        if (cut) copied++;
                        continue;
                    }
                    var own = prefab.GetComponent<AssembledObject>();
                    bool any = false;
                    foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.GetComponent<MeshRenderer>() == null || OwnerOf(mf.transform) != own) continue;
                        var mesh = mf.sharedMesh;
                        if (!LodMeshes.Eligible(mesh)) continue;
                        if (!ImportPostprocessor.IsHullModel(AssetDatabase.GetAssetPath(mesh))) continue;   // unreadable in a build
                        Add(mesh, LodMeshes.MinTriangles);
                        any = true;
                    }
                    if (any) objects++;
                }

                // Read on the main thread, cut on worker threads.
                var keys = meshes.Keys.ToList();
                var captured = new LodMeshes.Captured[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("GoF2", "Generated LODs: reading " + meshes[keys[i]].name, 0.2f + 0.2f * i / keys.Count)) return;
                    captured[i] = LodMeshes.Capture(meshes[keys[i]]);
                }
                var tasks = captured.Select((c, i) => { int min = minimum[keys[i]]; return Task.Run(() => LodMeshes.Simplify(c, min)); }).ToArray();
                while (!Task.WaitAll(tasks, 250))
                {
                    int done = tasks.Count(t => t.IsCompleted);
                    if (EditorUtility.DisplayCancelableProgressBar("GoF2", $"Generated LODs: cutting {done} / {tasks.Length}", 0.4f + 0.6f * done / Mathf.Max(1, tasks.Length)))
                    {
                        Task.WaitAll(tasks);
                        return;
                    }
                }

                long before = 0, level1 = 0, level2 = 0;
                int written = 0, noGain = 0;
                var lines = new List<string>();
                using (var w = new BinaryWriter(File.Create(OutPath)))
                {
                    w.Write(GameLods.Magic);
                    w.Write(GameLods.Version);
                    long countAt = w.BaseStream.Position;
                    w.Write(0);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var levels = tasks[i].Result;
                        int tris = LodMeshes.Triangles(meshes[keys[i]]);
                        if (levels.Length == 0) { noGain++; lines.Add($"  {meshes[keys[i]].name}: {tris} triangles, no level (the seams hold it)"); continue; }
                        w.Write(keys[i]);
                        using var payload = new MemoryStream();
                        using (var pw = new BinaryWriter(payload, System.Text.Encoding.UTF8, true)) LodMeshes.Write(pw, levels);
                        w.Write((int)payload.Length);
                        w.Write(payload.GetBuffer(), 0, (int)payload.Length);
                        written++;
                        int t1 = levels[0].Sum(s => s.Length) / 3, t2 = levels.Length > 1 ? levels[1].Sum(s => s.Length) / 3 : t1;
                        before += tris; level1 += t1; level2 += t2;
                        lines.Add($"  {meshes[keys[i]].name}: {tris} -> {t1}" + (levels.Length > 1 ? $" -> {t2}" : ""));
                    }
                    w.BaseStream.Position = countAt;
                    w.Write(written);
                }
                AssetDatabase.ImportAsset(OutPath);
                Debug.Log($"GoF2: Generated LODs: {written} meshes of {objects} objects with levels in {OutPath} ({new FileInfo(OutPath).Length / 1024} KB); "
                          + $"{original} objects have the original's LOD meshes ({copied} of them copies of LOD 0, cut here), {noGain} meshes gained too little. Triangles: {before} -> {level1} (level 1) -> {level2} (level 2).\n"
                          + string.Join("\n", lines));
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static AssembledObject OwnerOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var a = t.GetComponent<AssembledObject>();
                if (a != null) return a;
            }
            return null;
        }
    }
}
