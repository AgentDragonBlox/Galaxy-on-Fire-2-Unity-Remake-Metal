// AutoLods.cs
// Remake (the user's request 2026-10-22): "Levels of detail" (Options > Graphics, Settings.Lods; on by default on phones,
// off on PC, Linux and UWP). Added to every assembled object as it wakes (AssembledObject.Awake), so the game's prefabs
// stay as built:
//   - an object with the original's own LOD meshes (the generic ships, the battleship, the freighters, the jumpgates, the
//     asteroids: assemblies.json lodDistances) keeps its LODGroup and switch heights while the option is on; off, the
//     group is cut to LOD 0 alone, culled where the last level was, and the meshes only the lower levels show are kept
//     from drawing (forceRenderingOff). Those LOD meshes are copies of LOD 0 as converted (the same triangles, unreadable:
//     CopiedLevels), so while on each copy is swapped for its hull's made level (GameLods: level k for the original's
//     level k, the deepest there is past it) and put back when off;
//   - any other object gets levels made from its own LOD 0 meshes (LodMeshes): the game's from the prebaked library
//     (GameLods: stations, capital ships, turrets, the add-ons' ships, level objects...), a mod's ship or station from its
//     model at run time (ModLodCache, the hull as the mod ships it: optimized or not). Each level is a renderer of its own
//     under the mesh's renderer ("<name>_lod_gen1": the hull readers' "_lod" filters skip it, HullCollision, the shadow
//     bake; its mesh isn't readable either), in a LODGroup: LOD 1 below Lod1Height of the screen's height, LOD 2
//     below Lod2Height (Quality's LOD bias scales both); a mod ship keeps its cull at 80000 units, an object without a
//     LODGroup is never culled, as before. The level renderers copy their source's materials, property block, layer,
//     enabled and forceRenderingOff every frame, so the skins, cloak, damage and the hiding of the ship reach them.
// Full detail (FullDetail): the player's own ship, the hangar's close-up room, the previews, icons and menu backdrops call
// it where they forced LOD 0 / turned the LODGroup off; such an object gets no made levels.
// Not on a server without graphics (batch mode).

using System.Collections.Generic;
using GoF2Remake.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoF2Remake.Visuals
{
    [DisallowMultipleComponent]
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public sealed class AutoLods : MonoBehaviour
    {
        /// <summary>Screen heights (share of the screen) below which the made levels 1 and 2 show.</summary>
        public const float Lod1Height = 0.15f, Lod2Height = 0.05f;
        /// <summary>The name a made level's renderer carries (after its source's).</summary>
        public const string Marker = "_lod_gen";

        [System.Serializable]
        sealed class Level
        {
            public float height;
            public Renderer[] renderers;
        }

        // Serialized, so a copy (Instantiate) carries them, its references moved to the copy's own parts.
        [SerializeField, HideInInspector] string modId;          // a mod's object: its levels are made at run time
        [SerializeField, HideInInspector] bool fullDetail;
        [SerializeField, HideInInspector] bool ownGroup;         // the LODGroup is this one's (added for the made levels)
        [SerializeField, HideInInspector] bool baseTaken;
        [SerializeField, HideInInspector] List<Level> baseLevels = new List<Level>();   // the object's LODGroup as built
        [SerializeField, HideInInspector] bool swapsTaken;
        [SerializeField, HideInInspector] List<Swap> swaps = new List<Swap>();          // the original's copied LOD meshes

        [System.Serializable]
        sealed class Swap
        {
            public MeshFilter lower;   // the original's LOD mesh renderer
            public Mesh original;      // its mesh as built (a copy of 'hull')
            public Mesh hull;          // the LOD 0 mesh it copies, whose made levels replace it
            public int level;          // the original's level it first shows in (1...)
        }

        sealed class Target
        {
            public MeshFilter filter;
            public MeshRenderer renderer;
            public int slot;
            public Mesh[] meshes;               // its levels; null while a mod's are being made
            public MeshRenderer[] made;         // a renderer per level
        }

        readonly List<Target> targets = new List<Target>();
        readonly List<Renderer> lowerOnly = new List<Renderer>();
        bool started, collapsed, waiting, applied, appliedOn, appliedFull;
        LODGroup group;

        static readonly HashSet<AutoLods> live = new HashSet<AutoLods>();
        static bool? lastSetting;
        static readonly List<Material> matsA = new List<Material>(), matsB = new List<Material>();
        static MaterialPropertyBlock block;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Install()
        {
            live.Clear();
            lastSetting = null;
            Settings.Changed -= OnSettingsChanged;
            Settings.Changed += OnSettingsChanged;
        }

        static void OnSettingsChanged()
        {
            bool now = Settings.Lods;
            if (now == lastSetting) return;
            lastSetting = now;
            foreach (var a in new List<AutoLods>(live)) if (a != null) a.Apply();
        }

        static bool Headless => Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        /// <summary>A mod's ship or station (ModShipBuilder / ModStations): its levels are made from its "hull" at run time.</summary>
        public static void MarkRuntime(GameObject root, string modId)
        {
            if (root == null) return;
            var a = root.GetComponent<AutoLods>();
            if (a == null) a = root.AddComponent<AutoLods>();
            a.modId = string.IsNullOrEmpty(modId) ? "_" : modId;
        }

        /// <summary>'go' and the assembled objects in it always at full detail: no made levels (the callers force the
        /// original's LOD 0 or turn its LODGroup off themselves).</summary>
        public static void FullDetail(GameObject go)
        {
            if (go == null) return;
            foreach (var a in go.GetComponentsInChildren<AutoLods>(true))
            {
                a.fullDetail = true;
                if (a.started) a.Apply();
            }
        }

        void Start()
        {
            if (Headless) { enabled = false; return; }
            started = true;
            lastSetting ??= Settings.Lods;
            live.Add(this);
            Setup();
            Apply();
            if (targets.Count == 0) enabled = false;   // nothing to mirror (the original's levels need no frame)
        }

        void OnDestroy() => live.Remove(this);

        void Setup()
        {
            // A copy of a live object carries its made renderers and its levels: back to the object as built.
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != null && t != transform && t.name.Contains(Marker)) DestroyImmediate(t.gameObject);
            group = GetComponent<LODGroup>();
            if (ownGroup && group != null) { DestroyImmediate(group); group = null; }
            ownGroup = false;
            if (!baseTaken)
            {
                baseTaken = true;
                baseLevels.Clear();
                if (group != null)
                    foreach (var lod in group.GetLODs())
                        baseLevels.Add(new Level { height = lod.screenRelativeTransitionHeight, renderers = lod.renderers });
            }
            foreach (var l in baseLevels) l.renderers = System.Array.FindAll(l.renderers ?? new Renderer[0], r => r != null);
            // A copy of a changed object: its LODGroup back as built. Left alone otherwise (SetLODs could undo a caller's
            // ForceLOD).
            if (group != null && baseLevels.Count > 0 && !SameAs(group.GetLODs(), baseLevels)) group.SetLODs(ToLods(baseLevels));

            if (baseLevels.Count > 1)
            {
                // The original's own levels: what only the lower levels show (their LOD meshes).
                var first = new HashSet<Renderer>(baseLevels[0].renderers);
                for (int i = 1; i < baseLevels.Count; i++)
                    foreach (var r in baseLevels[i].renderers) if (!first.Contains(r) && !lowerOnly.Contains(r)) lowerOnly.Add(r);
                if (!swapsTaken)
                {
                    swapsTaken = true;
                    swaps.Clear();
                    foreach (var (lower, hull, level) in CopiedLevels(transform, ToLods(baseLevels)))
                        if (string.IsNullOrEmpty(modId) && GameLods.Has(hull.sharedMesh))
                            swaps.Add(new Swap { lower = lower, original = lower.sharedMesh, hull = hull.sharedMesh, level = level });
                }
                swaps.RemoveAll(w => w.lower == null || w.original == null);
                return;
            }

            var own = GetComponent<AssembledObject>();
            Transform scope = transform;
            if (modId != null && modId.Length > 0) scope = transform.Find("hull") ?? transform;
            int slot = 0;
            foreach (var mf in scope.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mesh == null || mr == null) continue;
                if (own != null && OwnerOf(mf.transform) != own) continue;   // a nested assembled object minds its own
                if (!string.IsNullOrEmpty(modId))
                {
                    if (!mesh.isReadable || !LodMeshes.Eligible(mesh)) continue;
                }
                else if (!GameLods.Has(mesh)) continue;
                targets.Add(new Target { filter = mf, renderer = mr, slot = slot++ });
            }
        }

        /// <summary>The meshes only the lower levels of 'lods' show that are LOD 0's meshes again (the same vertex, triangle
        /// and submesh counts, bounds and place under 'root'): each with that LOD 0 mesh and the level it first shows in.
        /// The game's converted LOD meshes are all such copies but the deep science station's (GeneratedLodsBuilder too).</summary>
        public static List<(MeshFilter lower, MeshFilter hull, int level)> CopiedLevels(Transform root, LOD[] lods)
        {
            var found = new List<(MeshFilter, MeshFilter, int)>();
            if (root == null || lods == null || lods.Length < 2) return found;
            var first = new HashSet<Renderer>();
            var hulls = new List<MeshFilter>();
            foreach (var r in lods[0].renderers ?? new Renderer[0])
            {
                if (r == null || !first.Add(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) hulls.Add(mf);
            }
            var seen = new HashSet<MeshFilter>();
            for (int k = 1; k < lods.Length; k++)
                foreach (var r in lods[k].renderers ?? new Renderer[0])
                {
                    if (r == null || first.Contains(r)) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !seen.Add(mf)) continue;
                    foreach (var h in hulls)
                        if (SameGeometry(root, mf, h)) { found.Add((mf, h, k)); break; }
                }
            return found;
        }

        static bool SameGeometry(Transform root, MeshFilter a, MeshFilter b)
        {
            Mesh ma = a.sharedMesh, mb = b.sharedMesh;
            if (ma.vertexCount != mb.vertexCount || ma.subMeshCount != mb.subMeshCount || LodMeshes.Triangles(ma) != LodMeshes.Triangles(mb)) return false;
            float tolerance = 1e-3f * (mb.bounds.size.magnitude + 1e-3f);
            if ((ma.bounds.center - mb.bounds.center).magnitude > tolerance || (ma.bounds.size - mb.bounds.size).magnitude > tolerance) return false;
            Matrix4x4 m = root.worldToLocalMatrix * a.transform.localToWorldMatrix, n = root.worldToLocalMatrix * b.transform.localToWorldMatrix;
            for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - n[i]) > 1e-3f * Mathf.Max(1f, Mathf.Abs(n[i]))) return false;
            return true;
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

        static bool SameAs(LOD[] lods, List<Level> levels)
        {
            if (lods.Length != levels.Count) return false;
            for (int i = 0; i < lods.Length; i++)
            {
                var a = lods[i].renderers ?? new Renderer[0];
                var b = levels[i].renderers;
                if (!Mathf.Approximately(lods[i].screenRelativeTransitionHeight, levels[i].height) || a.Length != b.Length) return false;
                for (int j = 0; j < a.Length; j++) if (a[j] != b[j]) return false;
            }
            return true;
        }

        static LOD[] ToLods(List<Level> levels)
        {
            var lods = new LOD[levels.Count];
            for (int i = 0; i < lods.Length; i++) lods[i] = new LOD(levels[i].height, levels[i].renderers);
            return lods;
        }

        bool On => Settings.Lods && !fullDetail;

        /// <summary>The option (or full detail) as it is now.</summary>
        void Apply()
        {
            bool on = On;
            if (applied && on == appliedOn && fullDetail == appliedFull && !waiting) return;
            applied = true;
            appliedOn = on;
            appliedFull = fullDetail;
            if (baseLevels.Count > 1) { ApplyOriginal(on || fullDetail); SwapCopied(on); return; }
            if (targets.Count == 0) return;
            if (on) waiting = !Build();
            else { waiting = false; RemoveMade(); }
        }

        /// <summary>The original's levels ('on') or LOD 0 alone, culled where the last level was.</summary>
        void ApplyOriginal(bool on)
        {
            if (group == null) return;
            if (on)
            {
                if (!collapsed) return;
                group.SetLODs(ToLods(baseLevels));
                foreach (var r in lowerOnly) if (r != null) r.forceRenderingOff = false;
                collapsed = false;
            }
            else
            {
                if (collapsed) return;
                group.SetLODs(new[] { new LOD(baseLevels[baseLevels.Count - 1].height, baseLevels[0].renderers) });
                foreach (var r in lowerOnly) if (r != null) r.forceRenderingOff = true;
                collapsed = true;
            }
        }

        /// <summary>The original's copied LOD meshes: their hull's made levels ('on') or as built.</summary>
        void SwapCopied(bool on)
        {
            foreach (var w in swaps)
            {
                if (w.lower == null) continue;
                var want = w.original;
                if (on)
                {
                    var made = GameLods.Get(w.hull);
                    if (made.Length > 0) want = made[Mathf.Min(w.level, made.Length) - 1];
                }
                if (w.lower.sharedMesh != want) w.lower.sharedMesh = want;
            }
        }

        /// <summary>The made levels in place; false while a mod's are still being made.</summary>
        bool Build()
        {
            int levels = 0;
            foreach (var t in targets)
            {
                if (t.meshes == null)
                    t.meshes = string.IsNullOrEmpty(modId) ? GameLods.Get(t.filter.sharedMesh)
                        : ModLodCache.Get(modId, $"{GetComponent<AssembledObject>()?.ModScope ?? name}_{t.slot}", t.filter.sharedMesh);
                if (t.meshes == null) return false;
                levels = Mathf.Max(levels, t.meshes.Length);
            }
            if (levels == 0) return true;   // nothing worth a level

            // The heights: level k below Heights[k]; the last level down to the cull (a mod ship's 80000 units) or never.
            float cull = baseLevels.Count == 1 ? baseLevels[0].height : 0f;
            var heights = new List<float>();
            float[] wanted = { Lod1Height, Lod2Height };
            for (int k = 0; k < levels; k++)
            {
                if (wanted[Mathf.Min(k, wanted.Length - 1)] <= cull * 1.01f) break;
                heights.Add(wanted[Mathf.Min(k, wanted.Length - 1)]);
            }
            levels = heights.Count;
            if (levels == 0) return true;   // culled before it would ever switch
            heights.Add(cull);

            foreach (var t in targets)
            {
                RemoveMade(t);
                int n = Mathf.Min(levels, t.meshes.Length);
                t.made = new MeshRenderer[n];
                for (int k = 0; k < n; k++) t.made[k] = MakeRenderer(t, k);
            }

            // LOD 0: as built (a mod ship's every renderer, or the meshes with levels); level k: the same with each mesh
            // swapped for its deepest level up to k.
            var lods = new LOD[levels + 1];
            var level0 = baseLevels.Count == 1 ? new List<Renderer>(baseLevels[0].renderers) : new List<Renderer>();
            if (baseLevels.Count != 1) foreach (var t in targets) level0.Add(t.renderer);
            lods[0] = new LOD(heights[0], level0.ToArray());
            for (int k = 1; k <= levels; k++)
            {
                var list = new List<Renderer>(level0);
                foreach (var t in targets)
                {
                    if (t.made == null || t.made.Length == 0) continue;
                    int i = list.IndexOf(t.renderer);
                    var swap = t.made[Mathf.Min(k, t.made.Length) - 1];
                    if (i >= 0) list[i] = swap; else list.Add(swap);
                }
                lods[k] = new LOD(heights[k], list.ToArray());
            }
            if (group == null) { group = gameObject.AddComponent<LODGroup>(); ownGroup = true; }
            group.SetLODs(lods);
            if (ownGroup) group.RecalculateBounds();
            return true;
        }

        MeshRenderer MakeRenderer(Target t, int k)
        {
            var go = new GameObject(t.filter.name + Marker + (k + 1)) { layer = t.filter.gameObject.layer };
            go.transform.SetParent(t.filter.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = t.meshes[k];
            var r = go.AddComponent<MeshRenderer>();
            var src = t.renderer;
            r.sharedMaterials = src.sharedMaterials;
            r.shadowCastingMode = src.shadowCastingMode;
            r.receiveShadows = src.receiveShadows;
            r.lightProbeUsage = src.lightProbeUsage;
            r.reflectionProbeUsage = src.reflectionProbeUsage;
            r.probeAnchor = src.probeAnchor;
            r.motionVectorGenerationMode = src.motionVectorGenerationMode;
            r.renderingLayerMask = src.renderingLayerMask;
            r.sortingLayerID = src.sortingLayerID;
            r.sortingOrder = src.sortingOrder;
            Mirror(src, r);
            return r;
        }

        void RemoveMade()
        {
            foreach (var t in targets) RemoveMade(t);
            if (group == null) return;
            if (ownGroup) { DestroyImmediate(group); group = null; ownGroup = false; }   // at once: on again may add one this frame
            else group.SetLODs(ToLods(baseLevels));
        }

        static void RemoveMade(Target t)
        {
            if (t.made == null) return;
            foreach (var r in t.made) if (r != null) Destroy(r.gameObject);
            t.made = null;
        }

        void LateUpdate()
        {
            if (waiting && On) { waiting = !Build(); if (!waiting) appliedOn = true; }
            foreach (var t in targets)
            {
                if (t.made == null) continue;
                foreach (var r in t.made) if (r != null && t.renderer != null) Mirror(t.renderer, r);
            }
        }

        /// <summary>The level's renderer as its source is now: materials, property block, layer, shown / hidden.</summary>
        static void Mirror(MeshRenderer src, MeshRenderer dst)
        {
            if (dst.gameObject.layer != src.gameObject.layer) dst.gameObject.layer = src.gameObject.layer;
            if (dst.enabled != src.enabled) dst.enabled = src.enabled;
            if (dst.forceRenderingOff != src.forceRenderingOff) dst.forceRenderingOff = src.forceRenderingOff;
            if (dst.shadowCastingMode != src.shadowCastingMode) dst.shadowCastingMode = src.shadowCastingMode;
            src.GetSharedMaterials(matsA);
            dst.GetSharedMaterials(matsB);
            bool same = matsA.Count == matsB.Count;
            for (int i = 0; same && i < matsA.Count; i++) same = matsA[i] == matsB[i];
            if (!same) dst.sharedMaterials = matsA.ToArray();
            if (src.HasPropertyBlock())
            {
                block ??= new MaterialPropertyBlock();
                src.GetPropertyBlock(block);
                dst.SetPropertyBlock(block);
            }
            else if (dst.HasPropertyBlock()) dst.SetPropertyBlock(null);
        }
    }
}
