// ModWorld.cs
// Mods' star systems and stations (systems.json / stations.json), merged into Database.Systems / Stations after the
// original 34 systems and 135 stations (ModContent.Apply calls Apply). See Modding/README.md.
//
// systems.json: { "id", "name", "race" (0 Terran, 1 Vossk, 2 Nivelian, 3 Midorian), "security" (0-3), "visible",
//   "position": [x, y, z] (the star map, the originals 15-94 / 2-96 / 10-90), "sky" (the nebula / sun set, 0-18 like
//   the originals' textureIndex), "gates": ["mod:id" | number, ...] (jumpgate routes, both ways: the other system gets
//   the route back), "gateStation" (the station orbit with the jumpgate; default its first station) } or
//   { "override": n | "mod:id", name, security, visible, gates (added) }; both may name "spaceMusic" / "stationMusic" (a
//   mod's track, ModMusic: the calm space music in the system's orbits, the music docked at its stations).
//   Both may name "available": a condition (ModUnlocks, the event graphs' expressions): while it holds the system is on
//   the map, while it doesn't it is hidden, revealed or not (GalaxyMap.Visibility); a player in it is taken out.
// stations.json may name "music" too (that station's docked music).
// Looks (new or override): a station's own 3D model ("model": a glTF / GLB, "modelSize" its largest extent in game units
// (default 40000 = 2 km; the originals are 1-5 km), "modelYaw" degrees, "modelCentre" (default true: the bounds' centre at
// the station's origin), "materials" like ships.json's; its collision "collision": "box" (default: the model's bounds),
// "sphere", "none", or "volumes": [{ "box": [x, y, z, hx, hy, hz] } | { "sphere": [x, y, z, r] }] in game units around the
// station; built by ModStations, "looksLike" stands in while it loads or when it fails), "interior" (the hangar and bar:
// terran / vossk / nivelian / midorian or 0-3; default the system's race), "planetTexture" (a PNG with transparency: the
// station's planet, seen from its own orbit and the system's others), "mapTexture" (the star map's planet: an image wrapped
// round the map's sphere, a 2:1 one like a world map; without it a "planetTexture" faces the map's camera as a disc, else the
// game's planet for "planet"), "mapRing" (true: a ring round the map's planet, like the planet ring orbits'), "mapPlace"
// ({ "angle": degrees, "distance": map units }: the planet's place in the star map's system view, else the seeded one), "orbit"
// (the orbit level's layout over the seeded one, OrbitSpec: station shown / without docking / none, the visible jumpgate and
// its position, where travel arrives, the asteroid field (none, count, centre, spread, kind, ores), fog, the sun's and the
// orbit planet's direction and size); systems: "sunTexture" (a PNG), "sunColor" [r, g, b]
// 0..1 (the sun's light; default from "sky").
// stations.json: { "id", "system": "mod:id" | number, "name", "techLevel" (0-10), "planet" (its planet texture: 0-22 or
//   24-26 like the originals), "looksLike": a station number (its model and collision; default the first original
//   station of the system's race) } or { "override": n | "mod:id", name, techLevel }.
// A system takes at most 7 stations (OrbitLayout places 7 planet slots around the sun). Numbering as ModContent: the
// registry in single player (a mod turned off leaves placeholders: a hidden system without stations or routes, a
// station in no system; ModSaves moves a save docked there to Var Hastra), afresh in a session.

using System;
using System.Collections.Generic;
using System.Linq;
using GoF2Remake.Data;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoF2Remake.Modding
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModWorld
    {
        public const string SystemsFile = "systems.json", StationsFile = "stations.json";
        public const int MaxStationsPerSystem = 7;
        public const int RefugeStation = 78;   // Var Hastra: where a save at a station that is gone docks

        public class Def
        {
            public ModInfo mod;
            public JObject json;
            public string where, localId, key, overrideRef, file;
            public Dictionary<string, string> name;
        }

        static int originalSystems = -1, originalStations = -1, mappedRevision = -1;
        static readonly Dictionary<string, int> systemIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly Dictionary<string, int> stationIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly Dictionary<int, Def> systemAt = new Dictionary<int, Def>(), stationAt = new Dictionary<int, Def>();
        static readonly Dictionary<int, string> missingSystems = new Dictionary<int, string>(), missingStations = new Dictionary<int, string>();
        static readonly Dictionary<int, int> stationLook = new Dictionary<int, int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { originalSystems = originalStations = mappedRevision = -1; }

        public static int OriginalSystemCount => originalSystems;
        public static int OriginalStationCount => originalStations;

        // ---- parsing -------------------------------------------------------------------------------------------------

        static readonly HashSet<string> SystemFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "id", "override", "name", "race", "security", "visible", "position", "sky", "gates", "gateStation", "spaceMusic", "stationMusic",
              "sunTexture", "sunColor", "skybox", "available" };
        static readonly HashSet<string> StationFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "id", "override", "name", "system", "techLevel", "planet", "looksLike", "music", "model", "modelSize", "modelYaw", "modelCentre",
              "materials", "collision", "volumes", "interior", "planetTexture", "mapTexture", "mapRing", "mapPlace", "orbit", "hangar", "bar" };

        /// <summary>ModContent.Parse: a mod's systems.json and stations.json into 'systems' / 'stations'.</summary>
        public static void Parse(ModInfo mod, List<Def> systems, List<Def> stations)
        {
            Read(mod, SystemsFile, SystemFields, systems, (o, d) =>
            {
                ModJson.Int(o, "race", 0, d.file); ModJson.Int(o, "security", 0, d.file); ModJson.Int(o, "sky", 0, d.file);
                if (ModJson.Has(o, "sunColor") && !(ModJson.Get(o, "sunColor") is JArray c && c.Count == 3))
                    throw new ModJsonException($"{d.where}: \"sunColor\" must be [r, g, b] (0..1)");
                if (ModJson.Get(o, "skybox") is JToken sb) ParseSkybox(mod, sb, d.where);
                if (d.localId != null)
                {
                    if (!(ModJson.Get(o, "position") is JArray p) || p.Count != 3)
                        throw new ModJsonException($"{d.where}: a new system needs \"position\": [x, y, z] on the star map");
                    int race = ModJson.Int(o, "race", 0, d.file);
                    if (race < 0 || race > 3) throw new ModJsonException($"{d.where}: \"race\" must be 0-3 (Terran, Vossk, Nivelian, Midorian)");
                }
            });
            Read(mod, StationsFile, StationFields, stations, (o, d) =>
            {
                ModJson.Int(o, "techLevel", 0, d.file); ModJson.Int(o, "planet", 0, d.file); ModJson.Int(o, "looksLike", -1, d.file);
                if (d.localId != null && !ModJson.Has(o, "system")) throw new ModJsonException($"{d.where}: a new station needs \"system\"");
                int planet = ModJson.Int(o, "planet", 0, d.file);
                if (planet < 0 || planet > 26 || planet == 23) throw new ModJsonException($"{d.where}: \"planet\" must be 0-22 or 24-26 (the game's planet textures)");
                if (ModJson.Has(o, "interior") && InteriorOf(o) < 0)
                    throw new ModJsonException($"{d.where}: \"interior\" must be terran, vossk, nivelian, midorian or 0-3");
                string collision = ModJson.Str(o, "collision", "box");
                if (collision != "box" && collision != "sphere" && collision != "none")
                    throw new ModJsonException($"{d.where}: \"collision\" must be box, sphere or none");
                ReadVolumes(o, d);   // checks the list
                if (ModJson.Has(o, "materials") && !(ModJson.Get(o, "materials") is JArray))
                    throw new ModJsonException($"{d.where}: \"materials\" must be a list like ships.json's");
                foreach (string image in new[] { "planetTexture", "mapTexture" })
                    if (ModJson.Str(o, image) is string file && !mod.Source.Exists(file))
                        throw new ModJsonException($"{d.where}: the {image} {file} isn't in the mod");
                ModJson.Bool(o, "mapRing", false, d.file);
                if (ModJson.Get(o, "orbit") is JToken orbitToken) ParseOrbit(mod, orbitToken, d.where, null);
                if (ModJson.Get(o, "mapPlace") is JToken placeToken) ParseMapPlace(placeToken, d.where);
            });

        }

        static void Read(ModInfo mod, string file, HashSet<string> fields, List<Def> into, Action<JObject, Def> check)
        {
            var token = ModJson.Read(mod.Source, file);
            if (token == null) return;
            if (!(token is JArray list)) throw new ModJsonException($"{file}: must be a list [ {{ ... }}, ... ]");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in list)
            {
                string where = ModJson.Where(t, file);
                if (!(t is JObject o)) throw new ModJsonException($"{where}: each entry must be an object {{ ... }}");
                var d = new Def { mod = mod, json = o, where = where, file = file, localId = ModJson.Str(o, "id"),
                                  overrideRef = ModJson.Str(o, "override"), name = ModJson.Text(o, "name") };
                if ((d.localId == null) == (d.overrideRef == null))
                    throw new ModJsonException($"{where}: an entry needs either \"id\" (a new one) or \"override\" (change an existing one)");
                if (d.localId != null)
                {
                    if (!ModManifest.ValidId(d.localId)) throw new ModJsonException($"{where}: the id \"{d.localId}\" may only use a-z, 0-9, _ and -");
                    if (!ids.Add(d.localId)) throw new ModJsonException($"{where}: the id \"{d.localId}\" is used twice");
                    if (d.name == null) throw new ModJsonException($"{where}: a new entry needs \"name\"");
                    d.key = mod.Id + ":" + d.localId;
                }
                foreach (var prop in o.Properties())
                    if (!fields.Contains(prop.Name)) mod.Warnings.Add($"{ModJson.Where(prop, file)}: unknown field \"{prop.Name}\" (ignored)");
                check(o, d);
                into.Add(d);
            }
        }

        // ---- numbering -----------------------------------------------------------------------------------------------

        public static void NoteOriginalCounts(Database db)
        {
            if (originalSystems < 0) originalSystems = db.Systems.Count;
            if (originalStations < 0) originalStations = db.Stations.Count;
        }

        static void EnsureMapping()
        {
            if (mappedRevision == ModManager.Revision && originalSystems >= 0) return;
            if (originalSystems < 0) { Database.Load(); if (originalSystems < 0) return; }
            mappedRevision = ModManager.Revision;
            systemIndex.Clear(); stationIndex.Clear(); systemAt.Clear(); stationAt.Clear(); missingSystems.Clear(); missingStations.Clear(); stationLook.Clear();
            bool session = ModManager.InSession;
            int nextSystem = originalSystems, nextStation = originalStations;
            foreach (var mod in ModManager.Active)
            {
                var p = ModContent.Parse(mod);
                foreach (var d in p.systems)
                {
                    if (d.key == null) continue;
                    int i = session ? nextSystem++ : ModRegistry.Assign(ModRegistry.Kind.System, d.key, originalSystems);
                    systemIndex[d.key] = i; systemAt[i] = d;
                }
                foreach (var d in p.stations)
                {
                    if (d.key == null) continue;
                    int i = session ? nextStation++ : ModRegistry.Assign(ModRegistry.Kind.Station, d.key, originalStations);
                    stationIndex[d.key] = i; stationAt[i] = d;
                }
            }
            if (!session)
            {
                foreach (var kv in ModRegistry.All(ModRegistry.Kind.System))
                    if (!systemIndex.ContainsKey(kv.Key) && kv.Value >= originalSystems) missingSystems[kv.Value] = kv.Key;
                foreach (var kv in ModRegistry.All(ModRegistry.Kind.Station))
                    if (!stationIndex.ContainsKey(kv.Key) && kv.Value >= originalStations) missingStations[kv.Value] = kv.Key;
            }
        }

        static bool Resolve(string reference, Dictionary<string, int> keys, int original, Dictionary<int, Def> mods, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(reference)) return false;
            if (int.TryParse(reference, out index)) return index >= 0 && (index < original || mods.ContainsKey(index));
            return keys.TryGetValue(reference, out index);
        }

        public static bool TryResolveSystem(string reference, out int index) { EnsureMapping(); return Resolve(reference, systemIndex, originalSystems, systemAt, out index); }
        public static bool TryResolveStation(string reference, out int index) { EnsureMapping(); return Resolve(reference, stationIndex, originalStations, stationAt, out index); }

        public static bool IsModSystem(int i) { EnsureMapping(); return systemAt.ContainsKey(i); }

        /// <summary>The mod that adds the system (null: an original one, or a placeholder).</summary>
        public static ModInfo SystemOwner(int i) { EnsureMapping(); return systemAt.TryGetValue(i, out var d) ? d.mod : null; }
        public static bool IsModStation(int i) { EnsureMapping(); return stationAt.ContainsKey(i); }
        /// <summary>A placeholder system (Apply): past the original table and no mod that is on adds it, whether the registry
        /// names a mod that is off or gone, or it only fills a gap in the numbering.</summary>
        public static bool IsMissingSystem(int i) { EnsureMapping(); return originalSystems >= 0 && i >= originalSystems && !systemAt.ContainsKey(i); }
        public static bool IsMissingStation(int i) { EnsureMapping(); return missingStations.ContainsKey(i); }
        public static int SystemIndexOf(string key) { EnsureMapping(); return systemIndex.TryGetValue(key, out int i) ? i : -1; }
        /// <summary>A mod's system's key ("mod_id:id"), null for an original one.</summary>
        public static string SystemKey(int i) { EnsureMapping(); return systemAt.TryGetValue(i, out var d) ? d.key : null; }
        public static int StationIndexOf(string key) { EnsureMapping(); return stationIndex.TryGetValue(key, out int i) ? i : -1; }
        public static IEnumerable<KeyValuePair<int, string>> ActiveSystemKeys() { EnsureMapping(); return systemAt.Select(kv => new KeyValuePair<int, string>(kv.Key, kv.Value.key)).ToList(); }
        public static IEnumerable<KeyValuePair<int, string>> ActiveStationKeys() { EnsureMapping(); return stationAt.Select(kv => new KeyValuePair<int, string>(kv.Key, kv.Value.key)).ToList(); }

        /// <summary>The original station whose model and collision a station uses (a mod's: its "looksLike"; others itself).</summary>
        public static int StationLook(int station) { EnsureMapping(); return stationLook.TryGetValue(station, out int l) ? l : station; }

        /// <summary>A station's own 3D model (stations.json "model"), built by ModStations.</summary>
        public class StationModel
        {
            public ModInfo mod;
            public int station;
            public string model, collision = "box", label;
            public float size = 40000f, yaw;
            public bool centre = true;
            public List<CustomShipMaterial> materials = new List<CustomShipMaterial>();
            public List<(bool sphere, Vector3 centre, Vector3 half, float radius)> volumes;   // game units; null = from "collision"
            public string Assembly => $"modstation_{station:000}";
        }

        static readonly Dictionary<int, StationModel> stationModels = new Dictionary<int, StationModel>();
        static readonly Dictionary<int, int> stationInterior = new Dictionary<int, int>();
        static readonly Dictionary<int, string> stationHangar = new Dictionary<int, string>(), stationBar = new Dictionary<int, string>();

        /// <summary>A station's custom hangar / bar (stations.json "hangar" / "bar": an interiors.json key; ModInteriors), null: none.</summary>
        public static string HangarKey(int station) => stationHangar.TryGetValue(station, out var k) ? k : null;
        public static string BarKey(int station) => stationBar.TryGetValue(station, out var k) ? k : null;
        static readonly Dictionary<int, string> planetTextures = new Dictionary<int, string>(), sunTextures = new Dictionary<int, string>(),
                                                mapTextures = new Dictionary<int, string>();
        static readonly HashSet<int> mapRings = new HashSet<int>();
        static readonly Dictionary<int, Color> sunColors = new Dictionary<int, Color>();

        /// <summary>The station's own model (null: an original's, "looksLike").</summary>
        public static StationModel ModelOf(int station) => stationModels.TryGetValue(station, out var m) ? m : null;

        /// <summary>Every station with its own model (ModStations loads them).</summary>
        public static IEnumerable<StationModel> Models() { Database.Load(); return stationModels.Values.ToList(); }

        /// <summary>The race whose hangar and bar a station uses (stations.json "interior"; -1: the system's).</summary>
        public static int InteriorRace(int station) => stationInterior.TryGetValue(station, out int r) ? r : -1;

        /// <summary>A station's own planet texture (Backdrop's "mod:" name, ModBackdrop), null: the game's.</summary>
        public static string PlanetTexture(int station) => planetTextures.TryGetValue(station, out var t) ? t : null;

        /// <summary>A station's star map planet image (stations.json "mapTexture", wrapped round the map's sphere; Backdrop's
        /// "mod:" name), null: none (StarMap then shows its planetTexture as a disc, else the game's planet).</summary>
        public static string MapTexture(int station) => mapTextures.TryGetValue(station, out var t) ? t : null;

        /// <summary>The station's planet has a ring on the star map (stations.json "mapRing").</summary>
        public static bool MapRing(int station) => mapRings.Contains(station);

        /// <summary>A station's orbit level laid out by its mod (stations.json "orbit"), applied by OrbitLayout.Build over the
        /// seeded layout; null fields keep the game's. Positions in game units around the station (the orbit's centre).</summary>
        public sealed class OrbitSpec
        {
            /// <summary>-1 the game's; 0 no station (an empty orbit); 1 a station to dock at; 2 its model without docking.</summary>
            public int station = -1;
            public bool? jumpgate;
            public Vector3? jumpgatePosition, arrival;
            /// <summary>false: no asteroid field.</summary>
            public bool? asteroids;
            public int? asteroidCount;
            public Vector3? asteroidCentre;
            /// <summary>The field's size (game units: the small asteroids' cube side; the big ones in 0.6 of it). Default 100000.</summary>
            public float? asteroidSpread;
            /// <summary>-1 the game's; 0 rock, 1 Void crystal, 2 ice, 3 magma.</summary>
            public int asteroidType = -1;
            /// <summary>The ores (item, chance weight), null: the system's by distance like the game's.</summary>
            public List<(int item, int chance)> ores;
            public bool? fog;
            public Color? fogColor;
            public float? fogEnd;
            /// <summary>The sun's direction: yaw (degrees, 0 straight ahead along the launch, + to the right) and pitch (+ up).</summary>
            public Vector2? sun;
            /// <summary>The orbit's own planet: its size (x the game's), yaw / pitch (degrees, 0 = straight ahead as the game's).</summary>
            public float? planetSize, planetYaw, planetPitch;
        }

        /// <summary>A planet's place in the star map's system view (stations.json "mapPlace").</summary>
        public sealed class MapPlace
        {
            /// <summary>Degrees round the sun (0..360), null: the seeded slot's.</summary>
            public float? angle;
            /// <summary>Map units from the sun (the game's planets lie about 8000 to 45000 out), null: the seeded one.</summary>
            public float? distance;
        }

        static readonly Dictionary<int, OrbitSpec> orbits = new Dictionary<int, OrbitSpec>();
        static readonly Dictionary<int, MapPlace> mapPlaces = new Dictionary<int, MapPlace>();

        /// <summary>The station's orbit layout from its mod, null: the game's.</summary>
        public static OrbitSpec OrbitOf(int station) => orbits.TryGetValue(station, out var o) ? o : null;

        /// <summary>The station's planet place on the star map, null: the game's.</summary>
        public static MapPlace MapPlaceOf(int station) => mapPlaces.TryGetValue(station, out var p) ? p : null;

        static readonly HashSet<string> OrbitFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "station", "jumpgate", "jumpgatePosition", "arrival", "asteroids", "fog", "sun", "planet" };
        static readonly HashSet<string> AsteroidFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "count", "centre", "center", "spread", "type", "ores" };
        static readonly string[] AsteroidTypes = { "rock", "void", "ice", "magma" };

        static Vector3? Xyz(JObject o, string key, string where)
        {
            var t = ModJson.Get(o, key);
            if (t == null) return null;
            if (!(t is JArray a) || a.Count != 3 || a.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                throw new ModJsonException($"{where}: \"{key}\" must be [x, y, z] (game units)");
            var p = new Vector3((float)a[0], (float)a[1], (float)a[2]);
            if (Mathf.Abs(p.x) > 1e6f || Mathf.Abs(p.y) > 1e6f || Mathf.Abs(p.z) > 1e6f) throw new ModJsonException($"{where}: \"{key}\" lies too far out (at most 1000000 units)");
            return p;
        }

        /// <summary>stations.json "orbit" (see OrbitSpec). 'mod' reports the ores it can't resolve when 'resolve' is set
        /// (Apply; Parse only checks the shape).</summary>
        static OrbitSpec ParseOrbit(ModInfo mod, JToken t, string where, Func<string, int> resolve)
        {
            if (!(t is JObject o)) throw new ModJsonException($"{where}: \"orbit\" must be an object {{ \"station\": ..., \"asteroids\": ... }}");
            foreach (var p in o.Properties())
                if (!OrbitFields.Contains(p.Name)) Report(mod, $"{where}: unknown orbit field \"{p.Name}\" (ignored)");
            var s = new OrbitSpec();
            if (ModJson.Get(o, "station") is JToken st)
            {
                if (st.Type == JTokenType.Boolean) s.station = (bool)st ? 1 : 0;
                else if (st.Type == JTokenType.String && ((string)st).Equals("nodock", StringComparison.OrdinalIgnoreCase)) s.station = 2;
                else throw new ModJsonException($"{where}: orbit \"station\" is true, false (an empty orbit) or \"nodock\" (its model, no docking)");
            }
            if (ModJson.Get(o, "jumpgate") is JToken jg)
            {
                if (jg.Type != JTokenType.Boolean) throw new ModJsonException($"{where}: orbit \"jumpgate\" is true or false");
                s.jumpgate = (bool)jg;
            }
            s.jumpgatePosition = Xyz(o, "jumpgatePosition", where);
            s.arrival = Xyz(o, "arrival", where);
            var ast = ModJson.Get(o, "asteroids");
            if (ast != null && ast.Type == JTokenType.Boolean) s.asteroids = (bool)ast;
            else if (ast is JObject a)
            {
                foreach (var p in a.Properties())
                    if (!AsteroidFields.Contains(p.Name)) Report(mod, $"{where}: unknown asteroids field \"{p.Name}\" (ignored)");
                s.asteroids = true;
                if (ModJson.Has(a, "count")) s.asteroidCount = Mathf.Clamp(ModJson.Int(a, "count", 60, StationsFile), 0, 300);
                s.asteroidCentre = Xyz(a, "centre", where) ?? Xyz(a, "center", where);
                if (ModJson.Has(a, "spread")) s.asteroidSpread = Mathf.Clamp(ModJson.Float(a, "spread", 100000f, StationsFile), 5000f, 500000f);
                if (ModJson.Str(a, "type") is string type)
                {
                    s.asteroidType = Array.FindIndex(AsteroidTypes, x => x.Equals(type, StringComparison.OrdinalIgnoreCase));
                    if (s.asteroidType < 0) throw new ModJsonException($"{where}: asteroids \"type\" is rock, void, ice or magma");
                }
                if (ModJson.Get(a, "ores") is JToken ores)
                {
                    if (!(ores is JArray list) || list.Count == 0) throw new ModJsonException($"{where}: asteroids \"ores\" is a list of items (numbers or \"mod_id:id\"), or {{ \"item\", \"chance\" }}");
                    s.ores = new List<(int, int)>();
                    foreach (var e in list)
                    {
                        string item = e is JObject eo ? ModJson.Get(eo, "item")?.ToString() : e.ToString();
                        int chance = e is JObject eo2 ? Mathf.Max(1, ModJson.Int(eo2, "chance", 1, StationsFile)) : 1;
                        if (string.IsNullOrEmpty(item)) throw new ModJsonException($"{where}: an ore without \"item\"");
                        if (resolve == null) continue;
                        int n = resolve(item);
                        if (n < 0) { Report(mod, $"{where}: ore \"{item}\" is no item (left out)"); continue; }
                        s.ores.Add((n, chance));
                    }
                    if (resolve != null && s.ores.Count == 0) s.ores = null;
                }
            }
            else if (ast != null) throw new ModJsonException($"{where}: orbit \"asteroids\" is false or {{ \"count\", \"centre\", \"spread\", \"type\", \"ores\" }}");
            var fog = ModJson.Get(o, "fog");
            if (fog != null && fog.Type == JTokenType.Boolean) s.fog = (bool)fog;
            else if (fog is JObject f)
            {
                s.fog = true;
                if (ModJson.Get(f, "color") is JToken c)
                {
                    if (!(c is JArray ca) || ca.Count != 3) throw new ModJsonException($"{where}: fog \"color\" must be [r, g, b] (0..1)");
                    s.fogColor = new Color(Mathf.Clamp01((float)ca[0]), Mathf.Clamp01((float)ca[1]), Mathf.Clamp01((float)ca[2]));
                }
                if (ModJson.Has(f, "end")) s.fogEnd = Mathf.Clamp(ModJson.Float(f, "end", 100000f, StationsFile), 5000f, 1000000f);
            }
            else if (fog != null) throw new ModJsonException($"{where}: orbit \"fog\" is false or {{ \"color\": [r, g, b], \"end\": units }}");
            if (ModJson.Get(o, "sun") is JToken sun)
            {
                if (!(sun is JArray sa) || sa.Count != 2) throw new ModJsonException($"{where}: orbit \"sun\" must be [yaw, pitch] (degrees)");
                s.sun = new Vector2((float)sa[0], Mathf.Clamp((float)sa[1], -89f, 89f));
            }
            if (ModJson.Get(o, "planet") is JToken pl)
            {
                if (!(pl is JObject po)) throw new ModJsonException($"{where}: orbit \"planet\" must be {{ \"size\", \"yaw\", \"pitch\" }}");
                if (ModJson.Has(po, "size")) s.planetSize = Mathf.Clamp(ModJson.Float(po, "size", 1f, StationsFile), 0.05f, 5f);
                if (ModJson.Has(po, "yaw")) s.planetYaw = ModJson.Float(po, "yaw", 0f, StationsFile);
                if (ModJson.Has(po, "pitch")) s.planetPitch = Mathf.Clamp(ModJson.Float(po, "pitch", 0f, StationsFile), -89f, 89f);
            }
            return s;
        }

        /// <summary>stations.json "mapPlace": { "angle": degrees, "distance": map units }.</summary>
        static MapPlace ParseMapPlace(JToken t, string where)
        {
            if (!(t is JObject o)) throw new ModJsonException($"{where}: \"mapPlace\" must be {{ \"angle\": degrees, \"distance\": map units }}");
            var p = new MapPlace();
            if (ModJson.Has(o, "angle")) p.angle = Mathf.Repeat(ModJson.Float(o, "angle", 0f, StationsFile), 360f);
            if (ModJson.Has(o, "distance")) p.distance = Mathf.Clamp(ModJson.Float(o, "distance", 20000f, StationsFile), 3000f, 120000f);
            return p;
        }

        /// <summary>A system's own sun texture (Backdrop's "mod:" name), null: the game's.</summary>
        public static string SunTexture(int system) => sunTextures.TryGetValue(system, out var t) ? t : null;

        /// <summary>A system's own sun light colour.</summary>
        public static bool SunColor(int system, out Color c) => sunColors.TryGetValue(system, out c);

        /// <summary>Every mod planet / sun / sky texture (ModStations decodes them in the background with the models).</summary>
        public static IEnumerable<string> BackdropTextures()
        {
            Database.Load();
            var sky = skyboxes.Values.SelectMany(s => new[] { s.nebula, s.stars }).Where(n => n != null);
            return planetTextures.Values.Concat(sunTextures.Values).Concat(mapTextures.Values).Concat(sky).Distinct().ToList();
        }

        /// <summary>A system's own sky (systems.json "skybox"; OrbitBuilder.SetupSky, GoF2/SpaceSky's mod images).</summary>
        public sealed class Skybox
        {
            /// <summary>Backdrop "mod:" names of the images (null: the game's layer); a 2:1 image is a panorama, a 6:1 one a strip
            /// of cube faces.</summary>
            public string nebula, stars;
            /// <summary>The game's star layer 0-2 instead (-1: the system's own, -2: none).</summary>
            public int starsLayer = -1;
            public float nebulaBrightness = 1f, starsBrightness = 1f;
            /// <summary>A fixed turn (Unity degrees); null: the game's own per-station turn, like the originals. With sunAligned
            /// it turns the image in its own frame first (its +Y stays toward the sun).</summary>
            public Vector3? rotation;
            /// <summary>The sky's +Y toward each orbit's sun (OrbitBuilder.SunAlignedSky, the supernova system's rule), so a
            /// painted sky's bright side lies behind the sun whatever the orbit.</summary>
            public bool sunAligned;
        }

        static readonly Dictionary<int, Skybox> skyboxes = new Dictionary<int, Skybox>();

        public static Skybox SkyOf(int system) => skyboxes.TryGetValue(system, out var s) ? s : null;

        static readonly HashSet<string> SkyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "nebula", "stars", "nebulaBrightness", "starsBrightness", "rotation", "sunAligned" };

        /// <summary>"skybox": { "nebula": "sky/x.png", "stars": "sky/s.png" | 0-2 | "none", "nebulaBrightness", "starsBrightness",
        /// "rotation": [x, y, z], "sunAligned": true }.</summary>
        static Skybox ParseSkybox(ModInfo mod, JToken t, string where)
        {
            if (!(t is JObject o)) throw new ModJsonException($"{where}: \"skybox\" must be an object {{ \"nebula\": \"sky/....png\", ... }}");
            foreach (var p in o.Properties())
                if (!SkyFields.Contains(p.Name)) Report(mod, $"{where}: unknown skybox field \"{p.Name}\" (ignored)");
            var s = new Skybox
            {
                nebulaBrightness = Mathf.Max(0f, ModJson.Float(o, "nebulaBrightness", 1f, SystemsFile)),
                starsBrightness = Mathf.Max(0f, ModJson.Float(o, "starsBrightness", 1f, SystemsFile)),
                sunAligned = ModJson.Bool(o, "sunAligned", false, SystemsFile),
            };
            string nebula = ModJson.Str(o, "nebula");
            if (nebula != null)
            {
                if (!mod.Source.Exists(nebula)) throw new ModJsonException($"{where}: the skybox image {nebula} isn't in the mod");
                s.nebula = BackdropName(mod, nebula);
            }
            var stars = ModJson.Get(o, "stars");
            if (stars != null)
            {
                if (stars.Type == JTokenType.Integer) s.starsLayer = Mathf.Clamp((int)stars, 0, 2);
                else if (stars.Type == JTokenType.String && ((string)stars).Equals("none", StringComparison.OrdinalIgnoreCase)) s.starsLayer = -2;
                else if (stars.Type == JTokenType.String)
                {
                    if (!mod.Source.Exists((string)stars)) throw new ModJsonException($"{where}: the skybox image {(string)stars} isn't in the mod");
                    s.stars = BackdropName(mod, (string)stars);
                }
                else throw new ModJsonException($"{where}: skybox \"stars\" is an image, a star layer 0-2 or \"none\"");
            }
            if (ModJson.Get(o, "rotation") is JToken r)
            {
                if (!(r is JArray a) || a.Count != 3) throw new ModJsonException($"{where}: skybox \"rotation\" must be [x, y, z] degrees");
                s.rotation = new Vector3((float)a[0], (float)a[1], (float)a[2]);
            }
            return s;
        }

        static int InteriorOf(JObject o)
        {
            var t = ModJson.Get(o, "interior");
            if (t == null) return -1;
            if (t.Type == JTokenType.Integer) { int v = (int)t; return v >= 0 && v <= 3 ? v : -1; }
            switch (((string)t ?? "").Trim().ToLowerInvariant())
            {
                case "terran": return 0;
                case "vossk": return 1;
                case "nivelian": return 2;
                case "midorian": return 3;
            }
            return -1;
        }

        static List<(bool, Vector3, Vector3, float)> ReadVolumes(JObject o, Def d)
        {
            if (!ModJson.Has(o, "volumes")) return null;
            if (!(ModJson.Get(o, "volumes") is JArray list)) throw new ModJsonException($"{d.where}: \"volumes\" must be a list");
            var r = new List<(bool, Vector3, Vector3, float)>();
            foreach (var t in list)
            {
                string where = ModJson.Where(t, d.file);
                float[] F(JToken a, int n)
                {
                    if (!(a is JArray arr) || arr.Count != n) return null;
                    var v = new float[n];
                    for (int i = 0; i < n; i++) v[i] = (float)arr[i];
                    return v;
                }
                if (t is JObject vo && vo["box"] != null && F(vo["box"], 6) is float[] b)
                    r.Add((false, new Vector3(b[0], b[1], b[2]), new Vector3(Mathf.Abs(b[3]), Mathf.Abs(b[4]), Mathf.Abs(b[5])), 0f));
                else if (t is JObject so && so["sphere"] != null && F(so["sphere"], 4) is float[] s)
                    r.Add((true, new Vector3(s[0], s[1], s[2]), Vector3.zero, Mathf.Abs(s[3])));
                else throw new ModJsonException($"{where}: a volume is {{ \"box\": [x, y, z, hx, hy, hz] }} or {{ \"sphere\": [x, y, z, r] }} (game units)");
            }
            return r;
        }

        /// <summary>A mod's PNG as Backdrop's texture name.</summary>
        static string BackdropName(ModInfo mod, string path) => "mod:" + mod.Id + "|" + path;

        static readonly Dictionary<int, string> systemSpaceMusic = new Dictionary<int, string>(), systemStationMusic = new Dictionary<int, string>(),
                                                stationMusic = new Dictionary<int, string>();

        /// <summary>The calm space music a mod gave the station's system (null: the game's).</summary>
        public static AudioClip SpaceMusic(int station)
        {
            if (systemSpaceMusic.Count == 0) return null;   // every music change asks: no table lookup without mod music
            var st = Database.Shared.Stations.Find(s => s.index == station);
            return st != null && systemSpaceMusic.TryGetValue(st.system, out string name) ? ModMusic.Track(name) : null;
        }

        /// <summary>The docked music a mod gave the station or its system (null: the game's).</summary>
        public static AudioClip StationMusic(int station)
        {
            if (stationMusic.TryGetValue(station, out string name)) return ModMusic.Track(name);
            if (systemStationMusic.Count == 0) return null;
            var st = Database.Shared.Stations.Find(s => s.index == station);
            return st != null && systemStationMusic.TryGetValue(st.system, out name) ? ModMusic.Track(name) : null;
        }

        // ---- merging -------------------------------------------------------------------------------------------------

        /// <summary>ModContent.Apply: the active mods' systems and stations into the freshly read tables.</summary>
        public static void Apply(Database db)
        {
            NoteOriginalCounts(db);
            EnsureMapping();
            systemSpaceMusic.Clear(); systemStationMusic.Clear(); stationMusic.Clear();
            stationModels.Clear(); stationInterior.Clear(); planetTextures.Clear(); sunTextures.Clear(); sunColors.Clear(); skyboxes.Clear();
            mapTextures.Clear(); mapRings.Clear(); orbits.Clear(); mapPlaces.Clear();
            stationHangar.Clear(); stationBar.Clear();
            if (systemAt.Count == 0 && stationAt.Count == 0 && missingSystems.Count == 0 && missingStations.Count == 0
                && !ModManager.Active.Any(m => ModContent.Parse(m).systems.Count + ModContent.Parse(m).stations.Count > 0)) return;
            var gates = new List<(int a, int b)>();
            var gateStations = new Dictionary<int, string>();
            foreach (var mod in ModManager.Active)
            {
                var p = ModContent.Parse(mod);
                foreach (var d in p.systems)
                {
                    try
                    {
                        var o = d.json;
                        SystemData s;
                        if (d.overrideRef != null)
                        {
                            if (!TryResolveSystem(d.overrideRef, out int t) || (s = db.Systems.Find(x => x.index == t)) == null)
                            { Report(mod, $"{d.where}: \"override\": {d.overrideRef} is no system"); continue; }
                        }
                        else
                        {
                            int index = systemIndex[d.key];
                            var pos = (JArray)ModJson.Get(o, "position");
                            int race = ModJson.Int(o, "race", 0, d.file);
                            var template = db.Systems.Find(x => x.index < originalSystems && x.raceId == race) ?? db.Systems[0];
                            s = new SystemData
                            {
                                index = index, raceId = race, race = template.race, initiallyVisible = true, jumpgateStation = -1,
                                mapPosition = new MapPos { x = (int)pos[0], y = (int)pos[1], z = (int)pos[2] },
                                textureIndex = template.textureIndex,
                                stations = new List<int>(), jumpRoutesTo = new List<int>(),
                                unknownTriple = new List<int>(template.unknownTriple ?? new List<int>()),
                                forbiddenGoodsOrUnknown = new List<int>(template.forbiddenGoodsOrUnknown ?? new List<int>()),
                            };
                            db.Systems.Add(s);
                        }
                        if (d.name != null) s.name = Text(d, "systems");
                        s.securityLevel = Mathf.Clamp(ModJson.Int(o, "security", s.securityLevel, d.file), 0, 3);
                        s.initiallyVisible = ModJson.Bool(o, "visible", s.initiallyVisible, d.file);
                        s.textureIndex = Mathf.Clamp(ModJson.Int(o, "sky", s.textureIndex, d.file), 0, 18);
                        foreach (var g in ModJson.Strings(o, "gates"))
                        {
                            if (TryResolveSystem(g, out int other) && other != s.index) gates.Add((s.index, other));
                            else Report(mod, $"{d.where}: gate to \"{g}\": no such system");
                        }
                        if (ModJson.Str(o, "gateStation") is string gs) gateStations[s.index] = gs;
                        if (ModJson.Str(o, "spaceMusic") is string sm) systemSpaceMusic[s.index] = sm;
                        if (ModJson.Str(o, "stationMusic") is string stm) systemStationMusic[s.index] = stm;
                        if (ModJson.Str(o, "sunTexture") is string sun) sunTextures[s.index] = BackdropName(mod, sun);
                        if (ModJson.Get(o, "skybox") is JToken skyToken) skyboxes[s.index] = ParseSkybox(mod, skyToken, d.where);
                        if (ModJson.Get(o, "sunColor") is JArray sc && sc.Count == 3)
                            sunColors[s.index] = new Color(Mathf.Clamp01((float)sc[0]), Mathf.Clamp01((float)sc[1]), Mathf.Clamp01((float)sc[2]));
                    }
                    catch (ModJsonException e) { Report(mod, e.Message); }
                }
            }
            foreach (var mod in ModManager.Active)
            {
                foreach (var d in ModContent.Parse(mod).stations)
                {
                    try
                    {
                        var o = d.json;
                        StationData st;
                        if (d.overrideRef != null)
                        {
                            if (!TryResolveStation(d.overrideRef, out int t) || (st = db.Stations.Find(x => x.index == t)) == null)
                            { Report(mod, $"{d.where}: \"override\": {d.overrideRef} is no station"); continue; }
                        }
                        else
                        {
                            int index = stationIndex[d.key];
                            if (!TryResolveSystem(ModJson.Str(o, "system"), out int sys) || db.Systems.Find(x => x.index == sys) is not SystemData sd)
                            { Report(mod, $"{d.where}: \"system\": {ModJson.Str(o, "system")} is no system"); continue; }
                            if (sd.stations.Count >= MaxStationsPerSystem)
                            { Report(mod, $"{d.where}: the system {sd.name} has {MaxStationsPerSystem} stations already (the most a system can hold)"); continue; }
                            st = new StationData { index = index, system = sys, systemName = sd.name, techLevel = 1, textureIndex = 0 };
                            db.Stations.Add(st);
                            sd.stations.Add(index);
                            int look = ModJson.Int(o, "looksLike", -1, d.file);
                            if (look < 0 || look >= originalStations)
                                look = db.Stations.Find(x => x.index < originalStations && db.Systems.Find(y => y.index == x.system)?.raceId == sd.raceId)?.index ?? 78;
                            stationLook[index] = look;
                        }
                        if (d.name != null) st.name = Text(d, "stations");
                        st.techLevel = Mathf.Clamp(ModJson.Int(o, "techLevel", st.techLevel, d.file), 0, 10);
                        st.textureIndex = ModJson.Int(o, "planet", st.textureIndex, d.file);
                        if (ModJson.Str(o, "music") is string mu) stationMusic[st.index] = mu;
                        int interior = InteriorOf(o);
                        if (interior >= 0) stationInterior[st.index] = interior;
                        if (ModJson.Str(o, "planetTexture") is string pt) planetTextures[st.index] = BackdropName(mod, pt);
                        if (ModJson.Str(o, "mapTexture") is string mt) mapTextures[st.index] = BackdropName(mod, mt);
                        if (ModJson.Bool(o, "mapRing", false, d.file)) mapRings.Add(st.index); else if (ModJson.Has(o, "mapRing")) mapRings.Remove(st.index);
                        if (ModJson.Get(o, "orbit") is JToken orbitToken)
                            orbits[st.index] = ParseOrbit(mod, orbitToken, d.where, r => ModContent.TryResolveItem(r, out int item) && db.Item(item) != null ? item : -1);
                        if (ModJson.Get(o, "mapPlace") is JToken placeToken) mapPlaces[st.index] = ParseMapPlace(placeToken, d.where);
                        // A custom room: "mod_id:interior_id", or this mod's own id.
                        if (ModJson.Str(o, "hangar") is string hk) stationHangar[st.index] = hk.Contains(":") ? hk : mod.Id + ":" + hk;
                        if (ModJson.Str(o, "bar") is string bk) stationBar[st.index] = bk.Contains(":") ? bk : mod.Id + ":" + bk;
                        if (ModJson.Str(o, "model") is string model)
                        {
                            var sm = new StationModel
                            {
                                mod = mod, station = st.index, model = model, label = st.name,
                                // A game model ("gof2:", GameModels) keeps its own size unless modelSize is given.
                                size = GameModels.Is(model) && !ModJson.Has(o, "modelSize") ? 0f : Mathf.Max(1000f, ModJson.Float(o, "modelSize", 40000f, d.file)),
                                yaw = ModJson.Float(o, "modelYaw", 0f, d.file),
                                centre = ModJson.Bool(o, "modelCentre", true, d.file),
                                collision = ModJson.Str(o, "collision", "box"),
                                volumes = ReadVolumes(o, d),
                            };
                            if (ModJson.Get(o, "materials") is JArray mats)
                                foreach (var m in mats) sm.materials.Add(JsonUtility.FromJson<CustomShipMaterial>(m.ToString()));
                            stationModels[st.index] = sm;
                            if (db.Assemblies.Find(a => a.name == sm.Assembly) == null)
                                db.Assemblies.Add(new AssemblyData { name = sm.Assembly, pack = ModContent.Pack, category = "stations", origin = "mod " + mod.Id });
                        }
                    }
                    catch (ModJsonException e) { Report(mod, e.Message); }
                }
            }
            // Gates both ways; a system with routes needs a gate station (its first station unless the entry names one).
            foreach (var (a, b) in gates)
            {
                var sa = db.Systems.Find(x => x.index == a);
                var sb = db.Systems.Find(x => x.index == b);
                if (sa == null || sb == null) continue;
                if (!sa.jumpRoutesTo.Contains(b)) sa.jumpRoutesTo.Add(b);
                if (!sb.jumpRoutesTo.Contains(a)) sb.jumpRoutesTo.Add(a);
            }
            foreach (var s in db.Systems)
            {
                if (gateStations.TryGetValue(s.index, out var gsRef) && TryResolveStation(gsRef, out int gst) && s.stations.Contains(gst)) s.jumpgateStation = gst;
                if (s.jumpRoutesTo.Count > 0 && (s.jumpgateStation < 0 || !s.stations.Contains(s.jumpgateStation)) && s.stations.Count > 0) s.jumpgateStation = s.stations[0];
            }
            // Station names carry their system's (StationData.systemName).
            foreach (var st in db.Stations)
                if (st.index >= originalStations || IsModSystem(st.system)) st.systemName = db.Systems.Find(x => x.index == st.system)?.name ?? st.systemName;
            foreach (var kv in missingSystems)
                if (db.Systems.Find(x => x.index == kv.Key) == null) db.Systems.Add(PlaceholderSystem(kv.Key, kv.Value));
            int maxSystem = db.Systems.Max(s => s.index);
            for (int i = originalSystems; i <= maxSystem; i++) if (db.Systems.Find(x => x.index == i) == null) db.Systems.Add(PlaceholderSystem(i, "?:" + i));
            foreach (var kv in missingStations)
                if (db.Stations.Find(x => x.index == kv.Key) == null) db.Stations.Add(PlaceholderStation(kv.Key, kv.Value));
            int maxStation = db.Stations.Max(s => s.index);
            for (int i = originalStations; i <= maxStation; i++) if (db.Stations.Find(x => x.index == i) == null) db.Stations.Add(PlaceholderStation(i, "?:" + i));
            db.Systems.Sort((a, c) => a.index.CompareTo(c.index));
            db.Stations.Sort((a, c) => a.index.CompareTo(c.index));
        }

        static string Text(Def d, string kind)
        {
            string lang = Localization.Language;
            var text = ModContent.Parse(d.mod).text;
            string key = $"{kind}.{d.localId ?? d.overrideRef}.name";
            if (d.name != null && d.name.TryGetValue(lang, out var s)) return s;
            if (text.TryGetValue(lang, out var table) && table.TryGetValue(key, out s)) return s;
            if (d.name != null && (d.name.TryGetValue("en", out s) || d.name.TryGetValue("", out s))) return s;
            return d.name?.Values.FirstOrDefault() ?? d.key;
        }

        static SystemData PlaceholderSystem(int index, string key) => new SystemData
        {
            index = index, name = "?", initiallyVisible = false, jumpgateStation = -1, raceId = 0, race = "Terran",
            mapPosition = new MapPos { x = -1000, y = -1000, z = -1000 }, stations = new List<int>(), jumpRoutesTo = new List<int>(),
            unknownTriple = new List<int>(), forbiddenGoodsOrUnknown = new List<int>(),
        };

        static StationData PlaceholderStation(int index, string key) =>
            new StationData { index = index, name = $"Missing station ({key})", system = -1, systemName = "", techLevel = 0 };

        static void Report(ModInfo mod, string message)
        {
            if (mod.Warnings.Contains(message)) return;
            mod.Warnings.Add(message);
            Debug.LogWarning($"Mods: {mod.Id}: {message}");
        }
    }
}
