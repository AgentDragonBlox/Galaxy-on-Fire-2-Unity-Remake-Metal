// ModWeapons.cs
// Remake mods: a weapon's own look and sound (items.json "fx" on a new item or an override). The projectile, the muzzle
// flash and the impact are each the base item's (the item's Look), a mod sprite (a PNG on a camera-facing quad, or
// stretched along the flight), a mod model (glTF / GLB, with the fx shader when it names a texture, else its own
// materials), the base item's recoloured (only "color" / "glow": its textures made grey and tinted, FxRecolor), or
// false (none). "shot" / "explosionSound" are the mod's audio files (one at random per shot), "shotLoops"
// whether the shot sound loops while the trigger is held. What the weapon does stays the base item's (a beam stays a
// beam, a mine a mine): only what it looks and sounds like changes. A part may also be another item's ("item": its base's
// part), recoloured or not, and any part without a sprite / model can be resized ("scale"); "shot" / "explosionSound" may
// name an item whose sounds it takes. Turrets: "turret" is the turret's own model (a glTF / GLB with a "pivot" node holding
// the base and a node named *_gun, the barrel along +Z), with "muzzle" / "shotStart" along the gun node (game units).
// Each item gets a WeaponFx of its own (a copy of the base item's, the parts and clips replaced), which WeaponFx.Load hands
// to every gun (the player's, NPCs', turrets, sentry guns, other players' mirrored shots). The parts are built under a
// hidden DontDestroyOnLoad holder (GunRig instantiates them); ModFxPart gives the sprites and models their lifetime,
// growth, fade and camera facing. Loaded in the background with the ships when the mods change (Preload; ModLoading).

using System.Collections.Generic;
using System.Threading.Tasks;
using GoF2Remake.Flight;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoF2Remake.Modding
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModWeapons
    {
        const float M = 0.05f;
        const string File = ModContent.ItemsFile;

        /// <summary>One part of an "fx": a sprite or a model, the base item's recoloured, or removed.</summary>
        public class Part
        {
            /// <summary>copy: no sprite / model, the base item's part (or 'fromItem''s); recolor: with "color" / "glow".</summary>
            public bool remove, copy, recolor;
            /// <summary>Another item's part instead of the base item's (-1 = the base item's), and a size factor.</summary>
            public int fromItem = -1;
            public float scale = 1f;
            public string sprite, model, texture;
            public float size = 200f, glow = -1f, lifetimeMs, grow = 1f, spin;
            public Color color = Color.white;
            public bool additive = true, fade = true, alongFlight;
            public float stretch = 1f;
            public Vector3 rotate;
        }

        public class Def
        {
            public ModInfo mod;
            public int item;
            public string label, where;
            public Part projectile, muzzle, impact;
            public List<string> shots, explosionSounds;
            /// <summary>"shot" / "explosionSound" given as an item number: that item's sounds (-1 = none).</summary>
            public int shotItem = -1, explosionItem = -1;
            public bool? shotLoops;
            /// <summary>"turret": the turret's own model, its muzzle and where its shots start (game units, -1 = the base's).</summary>
            public string turretModel;
            public float turretMuzzle = -1f, turretShot = -1f;
        }

        static readonly Dictionary<int, WeaponFx> made = new Dictionary<int, WeaponFx>();
        static readonly List<Object> owned = new List<Object>();
        static readonly List<GLTFast.GltfImport> imports = new List<GLTFast.GltfImport>();
        static GameObject holder;
        static GLTFast.TimeBudgetPerFrameDeferAgent agent;
        static Mesh quad;
        static int loadingRevision = -1, loadedRevision = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            made.Clear(); owned.Clear(); imports.Clear(); greyed.Clear(); recolorBlit = null;
            holder = null; agent = null; quad = null; loadingRevision = loadedRevision = -1; Count = Done = 0;
        }

        public static int Count { get; private set; }
        public static int Done { get; private set; }

        /// <summary>Every mod weapon's fx is built (or there are none).</summary>
        public static bool Ready
        {
            get
            {
                if (loadedRevision == ModManager.Revision) return true;
                Preload();
                return false;
            }
        }

        /// <summary>A mod item's own fx (null: none, or not built yet: the base item's is used meanwhile).</summary>
        public static WeaponFx Fx(int item)
        {
            if (loadedRevision != ModManager.Revision) { Preload(); return null; }
            return made.TryGetValue(item, out var fx) ? fx : null;
        }

        public static void Preload()
        {
            int rev = ModManager.Revision;
            if (loadingRevision == rev) return;
            loadingRevision = rev;
            _ = LoadAll(rev);
        }

        // ---- the "fx" entries ---------------------------------------------------------------------------------------

        /// <summary>The "fx" of every item of the mods that are on (a later mod's fx for the same item wins).</summary>
        public static List<Def> Defs()
        {
            var byItem = new Dictionary<int, Def>();
            foreach (var mod in ModManager.Active)
                foreach (var d in ModContent.Parse(mod).items)
                {
                    if (!(ModJson.Get(d.json, "fx") is JToken fx)) continue;
                    int index = d.key != null ? ModContent.ItemIndexOf(d.key) : ModContent.TryResolveItem(d.overrideRef, out int t) ? t : -1;
                    if (index < 0) continue;
                    try { byItem[index] = Parse(mod, fx, index, d.localId ?? d.overrideRef); }
                    catch (ModJsonException e) { Warn(mod, e.Message); }
                }
            return new List<Def>(byItem.Values);
        }

        static readonly HashSet<string> FxFields = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "projectile", "muzzle", "impact", "shot", "shotLoops", "explosionSound", "turret" };
        static readonly HashSet<string> PartFields = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "sprite", "model", "texture", "size", "color", "glow", "additive", "lifetime", "grow", "fade", "spin", "alongFlight", "stretch", "rotate",
              "item", "scale" };
        static readonly HashSet<string> CopyFields = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "item", "scale", "color", "glow" };
        static readonly HashSet<string> TurretFields = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "model", "muzzle", "shotStart" };

        static Def Parse(ModInfo mod, JToken token, int item, string label)
        {
            string where = ModJson.Where(token, File);
            if (!(token is JObject o)) throw new ModJsonException($"{where}: \"fx\" must be an object {{ ... }}");
            foreach (var p in o.Properties())
                if (!FxFields.Contains(p.Name)) mod.Warnings.Add($"{ModJson.Where(p, File)}: unknown fx field \"{p.Name}\" (ignored)");
            var d = new Def
            {
                mod = mod, item = item, label = label, where = where,
                projectile = ParsePart(mod, ModJson.Get(o, "projectile"), "projectile"),
                muzzle = ParsePart(mod, ModJson.Get(o, "muzzle"), "muzzle"),
                impact = ParsePart(mod, ModJson.Get(o, "impact"), "impact"),
                shots = Files(o, "shot", out int shotItem),
                explosionSounds = Files(o, "explosionSound", out int explosionItem),
                shotItem = shotItem, explosionItem = explosionItem,
            };
            if (ModJson.Has(o, "shotLoops")) d.shotLoops = ModJson.Bool(o, "shotLoops", false, File);
            foreach (var f in Concat(d.shots, d.explosionSounds))
                if (!mod.Source.Exists(f)) throw new ModJsonException($"{where}: the sound {f} isn't in the mod");
            ParseTurret(mod, d, ModJson.Get(o, "turret"));
            return d;
        }

        /// <summary>"turret": "models/x.glb" or { "model": ..., "muzzle": units, "shotStart": units }.</summary>
        static void ParseTurret(ModInfo mod, Def d, JToken t)
        {
            if (t == null) return;
            string where = ModJson.Where(t, File);
            if (t.Type == JTokenType.String) d.turretModel = (string)t;
            else if (t is JObject o)
            {
                foreach (var p in o.Properties())
                    if (!TurretFields.Contains(p.Name)) mod.Warnings.Add($"{ModJson.Where(p, File)}: unknown field \"{p.Name}\" in \"turret\" (ignored)");
                d.turretModel = ModJson.Str(o, "model");
                d.turretMuzzle = ModJson.Float(o, "muzzle", -1f, File);
                d.turretShot = ModJson.Float(o, "shotStart", -1f, File);
                if (d.turretModel == null) throw new ModJsonException($"{where}: \"turret\" needs \"model\" (a glTF / GLB)");
            }
            else throw new ModJsonException($"{where}: \"turret\" is a model file or {{ \"model\": ..., \"muzzle\": ..., \"shotStart\": ... }}");
            if (!mod.Source.Exists(d.turretModel)) throw new ModJsonException($"{where}: the turret model {d.turretModel} isn't in the mod");
        }

        static IEnumerable<string> Concat(List<string> a, List<string> b)
        {
            if (a != null) foreach (var s in a) yield return s;
            if (b != null) foreach (var s in b) yield return s;
        }

        /// <summary>A sound field: a file, a list of files, or an item's number (its sounds: 'item', the files null).</summary>
        static List<string> Files(JObject o, string key, out int item)
        {
            item = -1;
            var t = ModJson.Get(o, key);
            if (t == null) return null;
            if (t.Type == JTokenType.String) return new List<string> { (string)t };
            if (t.Type == JTokenType.Integer)
            {
                if (!ModContent.TryResolveItem(t.ToString(), out item))
                    throw new ModJsonException($"{ModJson.Where(t, File)}: \"{key}\": there is no item {t}");
                return null;
            }
            var list = ModJson.Strings(o, key);
            if (list == null || list.Count == 0) throw new ModJsonException($"{ModJson.Where(t, File)}: \"{key}\" must be a file, a list of files or an item's number");
            return list;
        }

        static Part ParsePart(ModInfo mod, JToken t, string name)
        {
            if (t == null) return null;
            string where = ModJson.Where(t, File);
            if (t.Type == JTokenType.Boolean)
            {
                if ((bool)t) throw new ModJsonException($"{where}: \"{name}\": true means nothing; give a sprite / model, or false for none");
                return new Part { remove = true };
            }
            if (!(t is JObject o)) throw new ModJsonException($"{where}: \"{name}\" must be an object {{ \"sprite\": ... }} or false");
            foreach (var p in o.Properties())
                if (!PartFields.Contains(p.Name)) mod.Warnings.Add($"{ModJson.Where(p, File)}: unknown field \"{p.Name}\" in \"{name}\" (ignored)");
            var part = new Part
            {
                sprite = ModJson.Str(o, "sprite"), model = ModJson.Str(o, "model"), texture = ModJson.Str(o, "texture"),
                size = ModJson.Float(o, "size", 200f, File),
                glow = ModJson.Float(o, "glow", -1f, File),
                lifetimeMs = ModJson.Float(o, "lifetime", name == "projectile" ? 0f : name == "muzzle" ? 100f : 300f, File),
                grow = ModJson.Float(o, "grow", 1f, File),
                spin = ModJson.Float(o, "spin", 0f, File),
                additive = ModJson.Bool(o, "additive", true, File),
                fade = ModJson.Bool(o, "fade", name != "projectile", File),
                alongFlight = ModJson.Bool(o, "alongFlight", false, File),
                stretch = ModJson.Float(o, "stretch", 1f, File),
            };
            if (part.sprite != null && part.model != null)
                throw new ModJsonException($"{where}: \"{name}\" has both \"sprite\" and \"model\"; give one of them");
            if (part.sprite == null && part.model == null)
            {
                // Neither: the base item's own part (or another item's, "item"), recoloured ("color" / "glow") and / or
                // resized ("scale"). Only those four mean something then.
                part.recolor = ModJson.Has(o, "color") || ModJson.Has(o, "glow");
                if (!part.recolor && !ModJson.Has(o, "item") && !ModJson.Has(o, "scale"))
                    throw new ModJsonException($"{where}: \"{name}\" needs \"sprite\" (a PNG) or \"model\" (a glTF / GLB), or \"color\" / \"item\" / \"scale\" to change the original's");
                part.copy = true;
                if (ModJson.Get(o, "item") is JToken it && (it.Type != JTokenType.Integer || !ModContent.TryResolveItem(it.ToString(), out part.fromItem)))
                    throw new ModJsonException($"{ModJson.Where(it, File)}: \"{name}\": \"item\" is an item's number (there is no item {it})");
                part.scale = ModJson.Float(o, "scale", 1f, File);
                if (part.scale <= 0f) throw new ModJsonException($"{where}: \"{name}\": \"scale\" must be above 0");
                foreach (var p in o.Properties())
                    if (PartFields.Contains(p.Name) && !CopyFields.Contains(p.Name))
                        mod.Warnings.Add($"{ModJson.Where(p, File)}: \"{p.Name}\" in \"{name}\" only applies with a sprite or model; the original's takes \"item\", \"scale\", \"color\" and \"glow\" (ignored)");
            }
            else
                foreach (var p in o.Properties())
                    if (string.Equals(p.Name, "item", System.StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, "scale", System.StringComparison.OrdinalIgnoreCase))
                        mod.Warnings.Add($"{ModJson.Where(p, File)}: \"{p.Name}\" in \"{name}\" only applies without a sprite or model (\"size\" sizes those; ignored)");
            if (part.size <= 0f) throw new ModJsonException($"{where}: \"{name}\": \"size\" must be above 0");
            if (ModJson.Get(o, "color") is JToken c) part.color = ParseColor(c);
            if (ModJson.Get(o, "rotate") is JArray r && r.Count == 3) part.rotate = new Vector3((float)r[0], (float)r[1], (float)r[2]);
            foreach (var f in new[] { part.sprite, part.model, part.texture })
                if (f != null && !mod.Source.Exists(f)) throw new ModJsonException($"{where}: \"{name}\": {f} isn't in the mod");
            return part;
        }

        /// <summary>[r, g, b] / [r, g, b, a] from 0 to 1, or from 0 to 255 when any value is above 1 (what colour pickers
        /// give: [134, 59, 135]), or "#rrggbb" / "#rrggbbaa".</summary>
        static Color ParseColor(JToken t)
        {
            if (t.Type == JTokenType.String && ColorUtility.TryParseHtmlString((string)t, out var hc)) return hc;
            if (t is JArray a && (a.Count == 3 || a.Count == 4))
            {
                var v = new float[4] { 0f, 0f, 0f, 1f };
                bool bytes = false;
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float)
                        throw new ModJsonException($"{ModJson.Where(t, File)}: a color's values are numbers");
                    v[i] = (float)a[i];
                    if (v[i] < 0f) throw new ModJsonException($"{ModJson.Where(t, File)}: a color's values can't be negative");
                    if (v[i] > 1f) bytes = true;
                }
                if (bytes)
                    for (int i = 0; i < a.Count; i++) v[i] = Mathf.Min(v[i], 255f) / 255f;
                return new Color(v[0], v[1], v[2], v[3]);
            }
            throw new ModJsonException($"{ModJson.Where(t, File)}: a color is [r, g, b] (0 to 1, or 0 to 255) or \"#rrggbb\"");
        }

        // ---- loading -------------------------------------------------------------------------------------------------

        static async Task LoadAll(int rev)
        {
            foreach (var o in owned) if (o != null) Object.Destroy(o);
            owned.Clear();
            greyed.Clear();
            made.Clear();
            foreach (var i in imports) i.Dispose();
            imports.Clear();
            List<Def> defs;
            try { defs = Defs(); }
            catch (System.Exception e) { Debug.LogException(e); defs = new List<Def>(); }
            Count = defs.Count;
            Done = 0;
            if (defs.Count > 0)
            {
                if (holder == null)
                {
                    holder = new GameObject("Mod weapon fx");
                    holder.SetActive(false);
                    Object.DontDestroyOnLoad(holder);
                }
                if (agent == null)
                {
                    var go = new GameObject("Mod weapon loader") { hideFlags = HideFlags.HideInHierarchy };
                    Object.DontDestroyOnLoad(go);
                    agent = go.AddComponent<GLTFast.TimeBudgetPerFrameDeferAgent>();
                    agent.SetFrameBudget(0.3f);
                }
                var tasks = new List<Task>();
                foreach (var d in defs) tasks.Add(LoadOne(rev, d));
                await Task.WhenAll(tasks);
            }
            if (rev != ModManager.Revision) return;
            loadedRevision = rev;
        }

        static async Task LoadOne(int rev, Def d)
        {
            try
            {
                // Everything of the item at once: textures, models and sounds.
                var parts = new[] { d.projectile, d.muzzle, d.impact };
                var built = new Task<GameObject>[3];
                for (int i = 0; i < 3; i++) built[i] = parts[i] == null || parts[i].remove || parts[i].copy ? Task.FromResult<GameObject>(null) : BuildPart(rev, d, parts[i], i);
                var shots = LoadClips(d.mod, d.shots);
                var booms = LoadClips(d.mod, d.explosionSounds);
                var turret = d.turretModel != null ? BuildTurret(rev, d) : Task.FromResult<GameObject>(null);
                await Task.WhenAll(built[0], built[1], built[2], shots, booms, turret);
                if (rev != ModManager.Revision) return;
                var baseFx = BaseFx(d.item);
                var fx = baseFx != null ? Object.Instantiate(baseFx) : ScriptableObject.CreateInstance<WeaponFx>();
                fx.name = $"mod fx {d.mod.Id}:{d.label}";
                fx.item = d.item;
                if (d.projectile != null) fx.projectile = d.projectile.copy ? Copy(d, d.projectile, fx.projectile, 0) : built[0].Result;
                if (d.muzzle != null) fx.muzzleFlash = d.muzzle.copy ? Copy(d, d.muzzle, fx.muzzleFlash, 1) : built[1].Result;
                if (d.impact != null) fx.impact = d.impact.copy ? Copy(d, d.impact, fx.impact, 2) : built[2].Result;
                if (d.shotItem >= 0 && BaseFx(d.shotItem) is WeaponFx shotFx)
                {
                    // Another item's shot: its sounds, and whether they loop unless "shotLoops" says so.
                    fx.shot = shotFx.shot; fx.shots = shotFx.shots; fx.shotLoops = shotFx.shotLoops;
                }
                if (d.explosionItem >= 0 && BaseFx(d.explosionItem) is WeaponFx boomFx)
                {
                    fx.explosionSound = boomFx.explosionSound; fx.explosionSounds = boomFx.explosionSounds;
                }
                if (shots.Result != null && shots.Result.Length > 0) { fx.shots = shots.Result; fx.shot = shots.Result[0]; }
                if (booms.Result != null && booms.Result.Length > 0) { fx.explosionSounds = booms.Result; fx.explosionSound = booms.Result[0]; }
                if (d.shotLoops.HasValue) fx.shotLoops = d.shotLoops.Value;
                if (turret.Result != null)
                {
                    fx.turretMounted = turret.Result;
                    fx.modTurret = true;
                    fx.turretMuzzleUnits = d.turretMuzzle;
                    fx.turretShotUnits = d.turretShot;
                }
                owned.Add(fx);
                made[d.item] = fx;
                Debug.Log($"Mods: {d.mod.Id}: weapon fx for \"{d.label}\" (item {d.item})");
            }
            catch (System.Exception e) when (rev == ModManager.Revision) { Warn(d.mod, $"{d.where}: {e.Message}"); if (!ModFileException.IsFileError(e)) Debug.LogException(e); }
            catch (System.Exception) { }   // a stale load (the mods changed meanwhile)
            finally { if (rev == ModManager.Revision) Done++; }
        }

        static async Task<AudioClip[]> LoadClips(ModInfo mod, List<string> files)
        {
            if (files == null || files.Count == 0) return null;
            var tasks = new List<Task<AudioClip>>();
            foreach (var f in files)
            {
                var tcs = new TaskCompletionSource<AudioClip>();
                ModAudio.Load(mod, ModAudio.Find(mod.Source, f) ?? f, c => tcs.TrySetResult(c), false);
                tasks.Add(tcs.Task);
            }
            var clips = await Task.WhenAll(tasks);
            var list = new List<AudioClip>();
            foreach (var c in clips) if (c != null) { list.Add(c); owned.Add(c); }
            return list.ToArray();
        }

        /// <summary>An item's game fx (its base's for a mod item): the source of borrowed parts and sounds.</summary>
        static WeaponFx BaseFx(int item) =>
            Resources.Load<WeaponFx>($"{WeaponFx.ResourcesFolder}/item_{ModContent.ItemLook(item):000}");

        /// <summary>A turret's own model (fx "turret"), as PlayerTurret expects the game's *_ship_mounted prefabs: a "pivot"
        /// node holding the base and a node whose name contains "_gun" (the barrel along +Z). Its size as made: 1 = 1 m.</summary>
        static async Task<GameObject> BuildTurret(int rev, Def d)
        {
            var bytes = await Task.Run(() => d.mod.Source.ReadBytes(d.turretModel));
            if (rev != ModManager.Revision || holder == null) return null;
            if (bytes == null) { Warn(d.mod, $"{d.where}: {d.turretModel} not found"); return null; }
            var import = await ModGltf.Load(d.mod, bytes, agent);
            bytes = null;
            if (import == null) { Warn(d.mod, $"{d.turretModel}: not a glTF model glTFast can read"); return null; }
            imports.Add(import);
            if (rev != ModManager.Revision || holder == null) return null;
            var root = new GameObject($"{d.mod.Id}:{d.label} turret");
            root.transform.SetParent(holder.transform, false);
            owned.Add(root);
            if (!await import.InstantiateMainSceneAsync(root.transform)) { Warn(d.mod, $"{d.turretModel}: no scene to show"); return null; }
            if (rev != ModManager.Revision || holder == null) return null;
            // glTFast may put the scene under a node of its own: PlayerTurret looks for "pivot" right under the root.
            var pivot = FindDeep(root.transform, "pivot");
            Transform gun = null;
            if (pivot != null) foreach (Transform c in pivot) if (c.name.Contains("_gun")) gun = c;
            if (pivot == null || gun == null)
            {
                Warn(d.mod, $"{d.turretModel}: the turret needs a node named \"pivot\" holding the base and a node named *_gun (the barrel); the base item's model is used");
                return null;
            }
            if (pivot.parent != root.transform) pivot.SetParent(root.transform, true);
            return root;
        }

        static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }

        static readonly string[] PartNames = { "projectile", "muzzle", "impact" };

        /// <summary>A part's template: a root (GunRig scales and turns it) holding the sprite / model with its ModFxPart.</summary>
        static async Task<GameObject> BuildPart(int rev, Def d, Part p, int kind)
        {
            string tex = p.sprite ?? p.texture;
            if (tex != null) await ModMaterials.PreloadTexture(d.mod, tex, false);
            GameObject visual = null;
            if (p.model != null)
            {
                var bytes = await Task.Run(() => d.mod.Source.ReadBytes(p.model));
                if (rev != ModManager.Revision || holder == null) return null;
                if (bytes == null) { Warn(d.mod, $"{d.where}: {p.model} not found"); return null; }
                var import = await ModGltf.Load(d.mod, bytes, agent);
                bytes = null;
                if (import == null) { Warn(d.mod, $"{p.model}: not a glTF model glTFast can read"); return null; }
                imports.Add(import);
                if (rev != ModManager.Revision || holder == null) return null;
                visual = new GameObject("model");
                visual.transform.SetParent(holder.transform, false);
                if (!await import.InstantiateMainSceneAsync(visual.transform)) { Object.Destroy(visual); Warn(d.mod, $"{p.model}: no scene to show"); return null; }
                if (rev != ModManager.Revision || holder == null) return null;
            }
            var root = new GameObject($"{d.mod.Id}:{d.label} {PartNames[kind]}");
            root.transform.SetParent(holder.transform, false);
            owned.Add(root);
            Material mat = null;
            if (tex != null)
            {
                var texture = ModMaterials.Texture(d.mod, tex, false);
                var assets = ModAssets.Get();
                var template = assets != null ? (p.additive ? assets.fxAdditive : assets.fxAlpha) : null;
                if (texture != null && template != null)
                {
                    if (p.sprite != null) texture.wrapMode = TextureWrapMode.Clamp;
                    mat = new Material(template) { name = root.name };
                    mat.SetTexture("_MainTex", texture);
                    mat.SetTextureScale("_MainTex", Vector2.one);
                    mat.SetTextureOffset("_MainTex", Vector2.zero);
                    mat.SetColor("_Color", p.color);
                    if (p.glow >= 0f && mat.HasProperty("_Glow")) mat.SetFloat("_Glow", p.glow);
                    owned.Add(mat);
                }
                else if (texture == null) Warn(d.mod, $"{d.where}: {tex} isn't a PNG / JPG the game can read");
            }
            if (p.sprite != null)
            {
                if (mat == null) { Object.Destroy(root); return null; }
                visual = new GameObject("sprite");
                visual.AddComponent<MeshFilter>().sharedMesh = Quad();
                visual.AddComponent<MeshRenderer>().sharedMaterial = mat;
                visual.transform.localScale = new Vector3(p.size, p.size * Mathf.Max(0.01f, p.stretch), p.size) * M;
                visual.transform.SetParent(root.transform, false);
            }
            else
            {
                visual.name = "model";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localRotation = Quaternion.Euler(p.rotate);
                var renderers = visual.GetComponentsInChildren<Renderer>(true);
                if (mat != null)
                    foreach (var r in renderers)
                    {
                        var mats = r.sharedMaterials;
                        for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                        r.sharedMaterials = mats;
                        // The GoF2 Shader Graphs multiply by the vertex colour (every game material has it on): white
                        // where the model has none.
                        var mesh = ModShipBuilder.MeshOf(r);
                        if (mesh != null && mesh.colors32.Length == 0 && mesh.isReadable) mesh.colors32 = White(mesh.vertexCount);
                    }
                // The largest extent to 'size' game units, centred on the part's origin.
                var b = ModShipBuilder.BoundsIn(root.transform, renderers);
                float largest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (largest > 0f) visual.transform.localScale *= p.size * M / largest;
                b = ModShipBuilder.BoundsIn(root.transform, renderers);
                visual.transform.localPosition -= b.center;
            }
            foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            var fxPart = visual.AddComponent<ModFxPart>();
            fxPart.lifetimeMs = p.lifetimeMs;
            fxPart.grow = p.grow;
            fxPart.fade = p.fade && mat != null;
            fxPart.additive = p.additive;
            fxPart.spin = p.spin;
            fxPart.faceCamera = p.sprite != null && !p.alongFlight;
            fxPart.alongFlight = p.sprite != null && p.alongFlight;
            return root;
        }

        /// <summary>A part without a sprite / model: the base item's (or "item"'s) own, recoloured ("color" / "glow") and
        /// resized ("scale"; on a node under a new root, since GunRig sets the root's scale).</summary>
        static GameObject Copy(Def d, Part p, GameObject baseSource, int kind)
        {
            var source = p.fromItem >= 0 ? PartOf(BaseFx(p.fromItem), kind) : baseSource;
            if (source == null)
            {
                Warn(d.mod, $"{d.where}: {(p.fromItem >= 0 ? $"item {p.fromItem}" : "the base item")} has no {PartNames[kind]} to use (give a sprite or model)");
                return null;
            }
            var part = p.recolor ? Recolor(d, p, source, kind) : source;
            if (part == null || Mathf.Approximately(p.scale, 1f)) return part;
            var root = new GameObject($"{d.mod.Id}:{d.label} {PartNames[kind]} (x{p.scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})");
            root.transform.SetParent(holder.transform, false);
            owned.Add(root);
            var scaler = new GameObject("scale").transform;
            scaler.SetParent(root.transform, false);
            scaler.localScale = Vector3.one * p.scale;
            Object.Instantiate(part, scaler, false).name = part.name;
            return root;
        }

        static GameObject PartOf(WeaponFx fx, int kind) =>
            fx == null ? null : kind == 0 ? fx.projectile : kind == 1 ? fx.muzzleFlash : fx.impact;

        // The base items' effect textures made grey (FxRecolor), one per texture whatever the colours asked of it.
        static readonly Dictionary<Texture, Texture> greyed = new Dictionary<Texture, Texture>();
        static Material recolorBlit;

        /// <summary>A part (the base item's or another's) with its effect materials recoloured: their texture the grey
        /// copy (each texel's brightest channel, so the bright core and soft edges stay), _Color the part's colour, _Glow
        /// its glow. Other materials (a missile's hull) keep theirs. PartAnimation's fades start from the copy's _Color.</summary>
        static GameObject Recolor(Def d, Part p, GameObject source, int kind)
        {
            var copy = Object.Instantiate(source, holder.transform, false);
            copy.name = $"{d.mod.Id}:{d.label} {PartNames[kind]} (recoloured)";
            owned.Add(copy);
            var done = new Dictionary<Material, Material>();
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !IsEffect(m)) continue;
                    if (!done.TryGetValue(m, out var tinted))
                    {
                        tinted = new Material(m) { name = m.name + " (recoloured)" };
                        if (m.mainTexture != null) tinted.mainTexture = Grey(m.mainTexture);
                        var baseColor = m.GetColor("_Color");
                        tinted.SetColor("_Color", new Color(p.color.r, p.color.g, p.color.b, baseColor.a * p.color.a));
                        if (p.glow >= 0f && tinted.HasProperty("_Glow")) tinted.SetFloat("_Glow", p.glow);
                        owned.Add(tinted);
                        done[m] = tinted;
                    }
                    mats[i] = tinted;
                }
                r.sharedMaterials = mats;
            }
            return copy;
        }

        static bool IsEffect(Material m) =>
            m.HasProperty("_Color") && (m.shader.name == "GoF2/Additive" || m.shader.name == "GoF2/AlphaBlend");

        static Texture Grey(Texture src)
        {
            if (greyed.TryGetValue(src, out var grey)) return grey;
            if (recolorBlit == null)
            {
                var shader = Resources.Load<Shader>("GoF2Mods/FxRecolor");
                if (shader == null) return src;
                recolorBlit = new Material(shader);
            }
            var rt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = src.name + " (grey)", useMipMap = true, autoGenerateMips = true,
                wrapMode = src.wrapMode, filterMode = src.filterMode, anisoLevel = src.anisoLevel,
            };
            Graphics.Blit(src, rt, recolorBlit);
            owned.Add(rt);
            greyed[src] = rt;
            return rt;
        }

        /// <summary>A 1 x 1 quad facing -Z, both sides drawn (the GoF2 Shader Graphs cull back faces).</summary>
        static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "mod fx quad" };
            var v = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            quad.vertices = new[] { v[0], v[1], v[2], v[3], v[0], v[1], v[2], v[3] };
            quad.uv = new[] { uv[0], uv[1], uv[2], uv[3], uv[0], uv[1], uv[2], uv[3] };
            quad.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 };
            quad.colors32 = White(8);   // the GoF2 Shader Graphs multiply by it
            quad.RecalculateNormals();
            quad.RecalculateBounds();
            Object.DontDestroyOnLoad(quad);
            return quad;
        }

        static Color32[] White(int n)
        {
            var c = new Color32[n];
            for (int i = 0; i < n; i++) c[i] = new Color32(255, 255, 255, 255);
            return c;
        }

        static void Warn(ModInfo mod, string message)
        {
            if (!mod.Warnings.Contains(message)) mod.Warnings.Add(message);
            Debug.LogWarning($"Mods: {mod.Id}: {message}");
        }
    }
}
