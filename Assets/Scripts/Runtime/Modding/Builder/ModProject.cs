// ModProject.cs
// Remake-only: a Mod Builder project (ModBuilder). A project is a mod's folder under Application.persistentDataPath/
// ModProjects/<id>, written in the mods' own format (mod.json, ships.json, models/*.glb; Modding/README.md), so the game
// reads it like any installed mod while it is being built (ModManager.SetBuilderProject) and Export only zips it into
// the Mods folder. Beside the mod's files it keeps builder.json (never exported): each ship's test-flight loadout, the
// test cheats and the ship last worked on.
// The ships are ProjectShip: their ships.json entry as a JObject (fields the builder doesn't know, from a hand edit, are
// kept), with typed accessors for what the builder edits and the hardpoints as WeaponMounts. Only ships for now; the
// other kinds of content come later (the menu shows them as "Coming soon").
// Plain C#.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GoF2Remake.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public class ModProject
    {
        public const string BuilderFile = "builder.json";
        public const string ModelsFolder = "models";

        /// <summary>Where the projects live (created on first use).</summary>
        public static string Root => Path.Combine(Application.persistentDataPath, "ModProjects");

        public string Folder { get; private set; }
        public string Id { get; private set; }
        public string Name { get => Str(manifest, "name") ?? Id; set => manifest["name"] = value; }
        public string Description { get => Str(manifest, "description") ?? ""; set => manifest["description"] = value; }
        public string Author { get => Str(manifest, "author") ?? ""; set => manifest["author"] = value; }
        public string Version { get => Str(manifest, "version") ?? "1.0"; set => manifest["version"] = value; }
        public DateTime Modified { get; private set; }

        public readonly List<ProjectShip> Ships = new List<ProjectShip>();
        JObject manifest = new JObject();
        JObject builder = new JObject();
        /// <summary>A content file couldn't be read (a hand edit gone wrong): shown, and the project isn't saved over it.</summary>
        public string LoadError { get; private set; }

        static string Str(JObject o, string key) => o[key] is JValue v && v.Type == JTokenType.String ? (string)v : null;

        // ---- the list ----------------------------------------------------------------------------------------------

        /// <summary>Every project, the most recently changed first.</summary>
        public static List<ModProject> List()
        {
            var l = new List<ModProject>();
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    if (!File.Exists(Path.Combine(dir, ModManifest.FileName))) continue;
                    var p = Load(dir);
                    if (p != null) l.Add(p);
                }
            }
            catch (Exception e) { Debug.LogWarning($"Mod Builder: can't read {Root}: {e.Message}"); }
            l.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return l;
        }

        /// <summary>A project's id from its name: lower case a-z 0-9 _, made unique among the projects and installed mods.</summary>
        public static string IdFor(string name)
        {
            var chars = (name ?? "").Trim().ToLowerInvariant().Select(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ? c : '_').ToArray();
            string id = new string(chars).Trim('_');
            while (id.Contains("__")) id = id.Replace("__", "_");
            if (id.Length == 0) id = "my_mod";
            if (id.Length > ModManifest.MaxIdLength - 4) id = id.Substring(0, ModManifest.MaxIdLength - 4);
            string unique = id;
            for (int i = 2; Directory.Exists(Path.Combine(Root, unique)) || ModManager.Find(unique) != null; i++) unique = $"{id}_{i}";
            return unique;
        }

        /// <summary>A new, empty project named 'name'.</summary>
        public static ModProject Create(string name, string author)
        {
            name = string.IsNullOrWhiteSpace(name) ? "My mod" : name.Trim();
            string id = IdFor(name);
            var p = new ModProject { Folder = Path.Combine(Root, id), Id = id };
            p.manifest = new JObject
            {
                ["id"] = id, ["name"] = name, ["version"] = "1.0",
                ["author"] = string.IsNullOrWhiteSpace(author) ? "" : author.Trim(),
                ["description"] = "",
            };
            Directory.CreateDirectory(p.Folder);
            p.Save();
            Debug.Log($"Mod Builder: new project \"{name}\" ({p.Folder})");
            return p;
        }

        /// <summary>Reads a project folder (null when its mod.json can't be read).</summary>
        public static ModProject Load(string folder)
        {
            try
            {
                var p = new ModProject { Folder = folder };
                p.manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, ModManifest.FileName)));
                p.Id = Str(p.manifest, "id") ?? Path.GetFileName(folder);
                p.Modified = Directory.GetLastWriteTimeUtc(folder);
                foreach (var f in new[] { ModManifest.FileName, ModContent.ShipsFile, BuilderFile })
                {
                    string path = Path.Combine(folder, f);
                    if (File.Exists(path)) { var t = File.GetLastWriteTimeUtc(path); if (t > p.Modified) p.Modified = t; }
                }
                string ships = Path.Combine(folder, ModContent.ShipsFile);
                if (File.Exists(ships))
                {
                    try
                    {
                        if (JToken.Parse(File.ReadAllText(ships)) is JArray list)
                            foreach (var t in list.OfType<JObject>()) p.Ships.Add(new ProjectShip(t));
                    }
                    catch (Exception e) { p.LoadError = $"{ModContent.ShipsFile}: {e.Message}"; }
                }
                string b = Path.Combine(folder, BuilderFile);
                if (File.Exists(b)) { try { p.builder = JObject.Parse(File.ReadAllText(b)); } catch { p.builder = new JObject(); } }
                return p;
            }
            catch (Exception e) { Debug.LogWarning($"Mod Builder: can't read the project {folder}: {e.Message}"); return null; }
        }

        /// <summary>Writes mod.json, ships.json and builder.json (each through a .tmp file).</summary>
        public void Save()
        {
            if (LoadError != null) { Debug.LogWarning($"Mod Builder: {Id} not saved: {LoadError}"); return; }
            Directory.CreateDirectory(Folder);
            Write(ModManifest.FileName, manifest.ToString(Formatting.Indented));
            var list = new JArray();
            foreach (var s in Ships) list.Add(s.ToJson());
            Write(ModContent.ShipsFile, list.ToString(Formatting.Indented));
            Write(BuilderFile, builder.ToString(Formatting.Indented));
            Modified = DateTime.UtcNow;
        }

        void Write(string file, string text)
        {
            string path = Path.Combine(Folder, file), tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Deletes the project's folder.</summary>
        public bool Delete(out string error)
        {
            error = null;
            try { Directory.Delete(Folder, true); Debug.Log($"Mod Builder: deleted the project {Folder}"); return true; }
            catch (Exception e) { error = e.Message; return false; }
        }

        // ---- export ---------------------------------------------------------------------------------------------

        /// <summary>An installed mod (not this project) already has the project's id: the export replaces it.</summary>
        public ModInfo InstalledMod => ModManager.Installed.FirstOrDefault(m => string.Equals(m.Id, Id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Zips the mod's files (not builder.json) into the Mods folder as "<id>.zip", replacing an installed mod with
        /// that id in its own folder (it keeps its on / off and load order). Returns the zip's path, or null with the error.</summary>
        public string Export(out string error)
        {
            error = null;
            Save();
            try
            {
                var old = InstalledMod;
                string folder = old != null ? ModManager.FolderOf(old) : ModManager.MainFolder;
                if (old != null && !ModImport.Delete(old, out error, keepEnabled: true)) return null;
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, Id + ".zip"), tmp = target + ".tmp";
                using (var zip = new ZipArchive(File.Create(tmp), ZipArchiveMode.Create))
                    foreach (var f in Directory.GetFiles(Folder, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(Folder.Length).TrimStart('/', '\\').Replace('\\', '/');
                        if (rel == BuilderFile || rel.EndsWith(".tmp")) continue;
                        var e = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                        using var dst = e.Open();
                        using var src = File.OpenRead(f);
                        src.CopyTo(dst);
                    }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                Debug.Log($"Mod Builder: exported \"{Id}\" to {target}");
                ModManager.Scan();
                return target;
            }
            catch (Exception e) { error = e.Message; Debug.LogWarning($"Mod Builder: export of {Id} failed: {e.Message}"); return null; }
        }

        // ---- ships ------------------------------------------------------------------------------------------------

        /// <summary>A new ship id (a-z 0-9 _) from a name, unique in the project.</summary>
        public string ShipIdFor(string name)
        {
            var chars = (name ?? "").Trim().ToLowerInvariant().Select(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ? c : '_').ToArray();
            string id = new string(chars).Trim('_');
            while (id.Contains("__")) id = id.Replace("__", "_");
            if (id.Length == 0) id = "ship";
            if (id.Length > 32) id = id.Substring(0, 32);
            string unique = id;
            for (int i = 2; Ships.Any(s => s.LocalId == unique); i++) unique = $"{id}_{i}";
            return unique;
        }

        /// <summary>Copies a GLB into the project's models folder; its path inside the project ("models/<name>.glb").</summary>
        public string AddModel(string sourceFile, string baseName)
        {
            string dir = Path.Combine(Folder, ModelsFolder);
            Directory.CreateDirectory(dir);
            string name = baseName + ".glb";
            for (int i = 2; File.Exists(Path.Combine(dir, name)); i++) name = $"{baseName}_{i}.glb";
            File.Copy(sourceFile, Path.Combine(dir, name), true);
            return ModelsFolder + "/" + name;
        }

        /// <summary>Copies a PNG into the project's textures folder; its path inside the project ("textures/<name>.png").</summary>
        public string AddTexture(string sourceFile, string baseName)
        {
            string dir = Path.Combine(Folder, "textures");
            Directory.CreateDirectory(dir);
            string name = baseName + ".png";
            File.Copy(sourceFile, Path.Combine(dir, name), true);
            return "textures/" + name;
        }

        /// <summary>A new ship from a model file in the project: sensible stats, no hardpoints yet (the builder places
        /// defaults once the model is built).</summary>
        public ProjectShip AddNewShip(string id, string name, string modelPath)
        {
            var s = new ProjectShip(new JObject
            {
                ["id"] = id, ["name"] = name, ["description"] = "",
                ["race"] = 0, ["armor"] = 300, ["cargo"] = 60, ["handling"] = 110,
                ["price"] = 400000,
                ["slots"] = new JObject { ["primary"] = 2, ["secondary"] = 1, ["turret"] = 0, ["equipment"] = 8 },
                ["model"] = modelPath, ["modelLength"] = 1000, ["modelYaw"] = 0, ["hangarHeight"] = 250,
                ["mounts"] = new JArray(),
            }) { NeedsLayout = true };
            Ships.Add(s);
            return s;
        }

        /// <summary>An override of an original ship: its stats and hardpoints copied, so they can be changed.</summary>
        public ProjectShip AddOverride(Database db, int ship)
        {
            var d = db.Ship(ship);
            var o = new JObject { ["override"] = ship };
            if (d != null)
            {
                o["armor"] = d.armor; o["cargo"] = d.cargo; o["handling"] = Mathf.RoundToInt(d.handling); o["price"] = d.price;
                o["slots"] = new JObject { ["primary"] = d.slots?.primary ?? 0, ["secondary"] = d.slots?.secondary ?? 0, ["turret"] = d.slots?.turret ?? 0, ["equipment"] = d.slots?.equipment ?? 0 };
            }
            var s = new ProjectShip(o);
            s.Mounts.AddRange((db.WeaponMounts.Find(w => w.ship == ship)?.mounts ?? new List<WeaponMount>()).Select(ProjectShip.Copy));
            Ships.Add(s);
            return s;
        }

        // ---- builder.json --------------------------------------------------------------------------------------

        JObject Section(string key)
        {
            if (!(builder[key] is JObject o)) builder[key] = o = new JObject();
            return o;
        }

        /// <summary>A ship's test loadout (item, amount), null = none chosen yet.</summary>
        public List<ItemStack> Loadout(ProjectShip s)
        {
            if (!(Section("loadouts")[s.Ref] is JArray a)) return null;
            var l = new List<ItemStack>();
            foreach (var e in a.OfType<JArray>()) if (e.Count >= 2) l.Add(new ItemStack((int)e[0], (int)e[1]));
            return l;
        }

        public void SetLoadout(ProjectShip s, List<ItemStack> items)
        {
            var a = new JArray();
            foreach (var i in items) a.Add(new JArray(i.item, i.amount));
            Section("loadouts")[s.Ref] = a;
        }

        /// <summary>The icon the builder made for a ship (ShipIconMaker): its file and the look it was made from, null = none.
        /// A ships.json "icon" that isn't this file is the modder's own and is never replaced.</summary>
        public (string file, string key)? GeneratedIcon(ProjectShip s) =>
            Section("icons")[s.Ref] is JObject o ? ((string)o["file"], (string)o["key"]) : ((string, string)?)null;

        /// <summary>Writes a generated icon to icons/&lt;id&gt;.png and makes it the ship's icon.</summary>
        public void SetGeneratedIcon(ProjectShip s, byte[] png, string key)
        {
            string dir = Path.Combine(Folder, "icons");
            Directory.CreateDirectory(dir);
            string file = "icons/" + (s.LocalId ?? "ship") + ".png";
            File.WriteAllBytes(Path.Combine(Folder, file), png);
            s.Icon = file;
            Section("icons")[s.Ref] = new JObject { ["file"] = file, ["key"] = key };
        }

        public bool Flag(string key, bool fallback) => builder[key] is JValue v && v.Type == JTokenType.Boolean ? (bool)v : fallback;
        public void SetFlag(string key, bool on) => builder[key] = on;

        /// <summary>The ship last worked on (its Ref), null = none.</summary>
        public string LastShip { get => Str(builder, "lastShip"); set => builder["lastShip"] = value; }

        /// <summary>A loadout key moves with a renamed ship.</summary>
        public void RenameRef(string from, string to)
        {
            var l = Section("loadouts");
            if (l[from] != null) { l[to] = l[from]; l.Remove(from); }
            var ic = Section("icons");
            if (ic[from] != null) { ic[to] = ic[from]; ic.Remove(from); }
            if (LastShip == from) LastShip = to;
        }
    }

    /// <summary>One ships.json entry of a project: a new ship ("id") or an override of an original ship ("override").</summary>
    public class ProjectShip
    {
        readonly JObject json;
        /// <summary>The hardpoints (ships.json "mounts"): slotType 0 primary, 1 secondary, 2 turret, 3 engine exhaust.</summary>
        public readonly List<WeaponMount> Mounts = new List<WeaponMount>();
        /// <summary>A new ship whose default hardpoints haven't been placed yet (done once its model is built).</summary>
        public bool NeedsLayout;

        public ProjectShip(JObject o)
        {
            json = o;
            if (o["mounts"] is JArray a)
                foreach (var m in a.OfType<JObject>())
                {
                    try { Mounts.Add(JsonUtility.FromJson<WeaponMount>(m.ToString())); } catch { }
                }
        }

        public static WeaponMount Copy(WeaponMount m) => new WeaponMount
        {
            slotType = m.slotType, upsideDown = m.upsideDown,
            position_engine = m.position_engine != null ? (int[])m.position_engine.Clone() : new int[3],
            turretAngles = m.turretAngles != null ? (float[])m.turretAngles.Clone() : null,
            glowColor = m.glowColor != null ? (float[])m.glowColor.Clone() : null,
            glowSize = m.glowSize != null ? (float[])m.glowSize.Clone() : null,
        };

        public bool IsOverride => json["override"] != null;
        public int OverrideIndex => IsOverride && int.TryParse(json["override"].ToString(), out int i) ? i : -1;
        public string LocalId => json["id"] is JValue v ? (string)v : null;
        /// <summary>The key builder.json files it under: "id:<id>" / "override:<n>".</summary>
        public string Ref => IsOverride ? "override:" + OverrideIndex : "id:" + LocalId;

        string S(string k) => json[k] is JValue v && v.Type == JTokenType.String ? (string)v : null;
        int I(string k, int d) => json[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? Mathf.RoundToInt((float)v) : d;
        float F(string k, float d) => json[k] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (float)v : d;

        public string Name { get => S("name"); set { if (string.IsNullOrEmpty(value)) json.Remove("name"); else json["name"] = value; } }
        public string Description { get => S("description") ?? ""; set => json["description"] = value ?? ""; }
        public string Model { get => S("model"); set { if (string.IsNullOrEmpty(value)) json.Remove("model"); else json["model"] = value; } }
        /// <summary>The shop icon (a 180 x 88 PNG in the project), null = none.</summary>
        public string Icon { get => S("icon"); set { if (string.IsNullOrEmpty(value)) json.Remove("icon"); else json["icon"] = value; } }
        public int Race { get => I("race", 0); set => json["race"] = value; }
        public int Armor { get => I("armor", 100); set => json["armor"] = value; }
        public int Cargo { get => I("cargo", 0); set => json["cargo"] = value; }
        public int Handling { get => I("handling", 100); set => json["handling"] = value; }
        public int Price { get => I("price", 0); set => json["price"] = value; }
        public int HangarHeight { get => I("hangarHeight", 250); set => json["hangarHeight"] = value; }
        public float ModelLength { get => F("modelLength", 1000f); set => json["modelLength"] = Mathf.Round(value); }
        public float ModelYaw { get => F("modelYaw", 0f); set => json["modelYaw"] = Mathf.Round(value); }
        public float ModelPitch { get => F("modelPitch", 0f); set => SetOrRemove("modelPitch", Mathf.Round(value)); }
        public float ModelRoll { get => F("modelRoll", 0f); set => SetOrRemove("modelRoll", Mathf.Round(value)); }

        void SetOrRemove(string key, float v) { if (v == 0f) json.Remove(key); else json[key] = v; }

        /// <summary>modelOffset (game units, x y z in the ship's frame); zeros = none.</summary>
        public Vector3Int ModelOffset
        {
            get => json["modelOffset"] is JArray a && a.Count >= 3
                ? new Vector3Int(Mathf.RoundToInt((float)a[0]), Mathf.RoundToInt((float)a[1]), Mathf.RoundToInt((float)a[2])) : Vector3Int.zero;
            set { if (value == Vector3Int.zero) json.Remove("modelOffset"); else json["modelOffset"] = new JArray(value.x, value.y, value.z); }
        }

        JObject Slots
        {
            get
            {
                if (!(json["slots"] is JObject o)) json["slots"] = o = new JObject();
                return o;
            }
        }

        /// <summary>Slot count by kind: 0 primary, 1 secondary, 2 turret, 3 equipment.</summary>
        public int SlotCount(int kind)
        {
            string k = SlotKey(kind);
            return Slots[k] is JValue v && v.Type == JTokenType.Integer ? (int)v : 0;
        }

        public void SetSlotCount(int kind, int count) => Slots[SlotKey(kind)] = Mathf.Clamp(count, 0, 99);

        /// <summary>The builder's hull textures: one "materials" entry for every mesh (ships.json; ModMaterials.FromSpec), its
        /// 'role' "diffuse", "normal" or "metallicSmoothness" (null = none). False when the entry list is more than the builder
        /// manages (several entries, or one limited to some meshes: a hand edit), which it then leaves alone.</summary>
        public bool HullTexturesEditable => !(json["materials"] is JArray a) || a.Count == 0
                                            || a.Count == 1 && a[0] is JObject o && string.IsNullOrEmpty((string)o["mesh"]) && o["submesh"] == null;

        public string HullTexture(string role) =>
            json["materials"] is JArray a && a.Count == 1 && a[0] is JObject o && o[role] is JValue v ? (string)v : null;

        public void SetHullTexture(string role, string path)
        {
            if (!HullTexturesEditable) return;
            var o = json["materials"] is JArray a && a.Count == 1 && a[0] is JObject e ? e : new JObject { ["smoothness"] = 1 };
            if (string.IsNullOrEmpty(path)) o.Remove(role); else o[role] = path;
            bool any = o["diffuse"] != null || o["normal"] != null || o["metallicSmoothness"] != null;
            if (any) json["materials"] = new JArray(o); else json.Remove("materials");
        }

        static string SlotKey(int kind) => kind == 0 ? "primary" : kind == 1 ? "secondary" : kind == 2 ? "turret" : "equipment";

        public int MountCount(int slotType) => Mounts.Count(m => m.slotType == slotType);

        /// <summary>The entry as written to ships.json (the hardpoints from Mounts).</summary>
        public JObject ToJson()
        {
            var o = (JObject)json.DeepClone();
            var a = new JArray();
            foreach (var m in Mounts)
            {
                var e = new JObject { ["slotType"] = m.slotType, ["position_engine"] = new JArray(m.position_engine ?? new int[3]) };
                if (m.turretAngles != null && m.turretAngles.Length > 0) e["turretAngles"] = new JArray(m.turretAngles.Select(f => (float)Math.Round(f, 3)));
                if (m.upsideDown) e["upsideDown"] = true;
                if (m.glowColor != null && m.glowColor.Length >= 3) e["glowColor"] = new JArray(m.glowColor.Select(f => (float)Math.Round(f, 3)));
                if (m.glowSize != null && m.glowSize.Length >= 3) e["glowSize"] = new JArray(m.glowSize.Select(f => (float)Math.Round(f, 1)));
                a.Add(e);
            }
            o["mounts"] = a;
            return o;
        }
    }
}
