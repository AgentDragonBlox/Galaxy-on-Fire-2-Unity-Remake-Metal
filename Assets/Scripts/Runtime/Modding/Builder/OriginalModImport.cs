// OriginalModImport.cs
// Remake (the user's request, 2026-10-22): a mod made for the original game (a zip of the game's own files: .aem models and
// .aei textures, usually in MESHES/ and TEXTURES/ folders, named like the files they replace) becomes a Mod Builder project.
//   A game ship's hull ("ship_016_nivelian.aem", the name of its assembly: Database.ShipAssembly) becomes an override of
//   that ship with the model converted (AemImport) at its own length, so it keeps the size and the hardpoints fit as they
//   did; its "<name>_diffuse" / "_normal_specular" .aei become the hull textures: the diffuse as it is, the normal map's
//   RGB as the normal map and its alpha (the game's specular) as the smoothness (Reference/tools/asset_conversion/
//   split_spec.py, the remake's own *_metallic_smoothness). The ship's additive layers become add parts (ships.json
//   "addParts", ModShipBuilder: drawn with the game's material for their role, like the game's own ships): _lights_add,
//   _engine_glow_add (the player's engine glow) and _engine_add (the NPCs'); copies of the hull (the mods often fill those
//   slots and _lod_N with it, which the original drew as glowing ghosts) and the _lod_N models aren't used.
//   The first release's names work too (2026-10-23, the user's request with its Betty): "ship_00.aem" (two digits, the same
//   numbering) with "_lights" and "_add" (its one engine glow), and its race's texture atlas ("tex_mido_ships.aei", the
//   hull's and its parts' texture: its glow sprites are in it) as the hull's diffuse and the parts' texture.
//   Any other .aei is a skin: textures/<its name>.png replaces the game's texture of that name (ModTextures); a
//   "_normal_specular" one also gets its "_metallic_smoothness". A texture-only mod is a skin project that way.
//   Other .aem files (stations, weapons...) aren't taken yet: the result names them.
// Plain C# but for the Database it is given (read only); run on a worker thread (ModBuilderProjects).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GoF2Remake.Data;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public static class OriginalModImport
    {
        /// <summary>What became of the mod's files, for the player.</summary>
        public sealed class Result
        {
            public ModProject project;
            public string error;
            public readonly List<(int ship, string file, int triangles, bool textured)> hulls = new List<(int, string, int, bool)>();
            public readonly List<string> skins = new List<string>();
            public readonly List<string> unused = new List<string>();
            /// <summary>The ships' additive layers kept as add parts: the ship, the file, the role (CustomAddPart).</summary>
            public readonly List<(int ship, string file, string role)> parts = new List<(int, string, string)>();
            public int hullCopies;
        }

        /// <summary>A zip of the game's own files (an .aem or .aei inside) without a mod.json: an original-game mod.</summary>
        public static bool Looks(string zipPath)
        {
            try
            {
                using (var zip = Open(zipPath))
                {
                    bool any = false;
                    foreach (var e in zip.Entries)
                    {
                        if (e.Name.Equals(ModManifest.FileName, StringComparison.OrdinalIgnoreCase)) return false;
                        string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                        if (ext == ".aem" || ext == ".aei") any = true;
                    }
                    return any;
                }
            }
            catch (Exception) { return false; }
        }

        static ZipArchive Open(string path) =>
            new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ZipArchiveMode.Read);

        /// <summary>The game's ships by the name of their assembly (the hull's file name), from 'db'. Main thread.</summary>
        public static Dictionary<string, int> ShipsByModel(Database db)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < GameNames.OriginalShips; i++)
            {
                if (i == 13 || i == 14 || i == 15) continue;   // the freighters / battleship: not flyable, several models
                var asm = db.ShipAssembly(i, original: true);
                if (asm != null && !string.IsNullOrEmpty(asm.name) && !map.ContainsKey(asm.name)) map[asm.name] = i;
            }
            return map;
        }

        /// <summary>Fills 'project' (new and empty, made on the main thread: ModProject.Create) from the zip at 'zipPath'
        /// (ships by ShipsByModel; 'db' for the overrides' stats and hardpoints, read only). On a failure the project's
        /// folder is deleted again.</summary>
        public static Result Import(string zipPath, ModProject project, Database db, Dictionary<string, int> ships)
        {
            var result = new Result();
            string temp = Path.Combine(Path.GetTempPath(), "gof2_ogmod_" + Guid.NewGuid().ToString("N"));
            try
            {
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);   // by base name (no folder, no extension)
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var zip = Open(zipPath))
                    foreach (var e in zip.Entries)
                    {
                        string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                        if (ext != ".aem" && ext != ".aei") continue;
                        string key = Path.GetFileNameWithoutExtension(e.Name) + ext;
                        using (var s = e.Open())
                        using (var m = new MemoryStream())
                        {
                            s.CopyTo(m);
                            files[key] = m.ToArray();
                        }
                        names[key] = e.FullName;
                    }
                if (files.Count == 0) throw new InvalidDataException("no .aem or .aei inside");
                Directory.CreateDirectory(temp);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // The ships whose hull the mod replaces: by the HD versions' names ("ship_000_midorian", ShipsByModel) or the
                // first release's ("ship_00").
                var atlases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // an atlas .aei -> its PNG in the project
                foreach (var (model, ship, firstRelease) in HullNames(ships))
                {
                    string hullKey = model + ".aem";
                    if (!files.TryGetValue(hullKey, out var hullBytes) || used.Contains(hullKey)) continue;
                    var mesh = AemImport.Read(hullBytes, out string error);
                    if (mesh == null) { result.unused.Add($"{names[hullKey]} ({error})"); used.Add(hullKey); continue; }
                    string glb = Path.Combine(temp, model + ".glb");
                    File.WriteAllBytes(glb, AemImport.ToGlb(mesh, model));
                    var s = project.AddOverride(db, ship);
                    s.Model = project.AddModel(glb, "ship_" + ship);
                    s.ModelLength = mesh.Length;
                    used.Add(hullKey);
                    // The first release's textures: one atlas per race ("tex_mido_ships"), the hull's and its parts' alike.
                    string atlas = firstRelease ? RaceAtlas(files.Keys, ship, atlases, temp, project, files) : null;
                    if (atlas != null) used.Add(atlas);
                    string atlasPath = atlas != null ? atlases[atlas] : null;
                    // The ship's other parts: its additive layers (lights, engine glows) as add parts; copies of the hull (the
                    // community's mods often fill the glow and LOD slots with it, which the original drew as glowing ghosts)
                    // and the LOD models (the remake makes its own) left out.
                    foreach (var other in files.Keys.Where(k => k.EndsWith(".aem", StringComparison.OrdinalIgnoreCase)
                                                             && k.StartsWith(model + "_", StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        used.Add(other);
                        var part = AemImport.Read(files[other], out string partError);
                        if (files[other].AsSpan().SequenceEqual(hullBytes) || part != null && SameShape(part, mesh)) { result.hullCopies++; continue; }
                        string role = PartRole(Path.GetFileNameWithoutExtension(other).Substring(model.Length), firstRelease);
                        if (role == null || part == null) { result.unused.Add(part == null ? $"{names[other]} ({partError})" : names[other]); continue; }
                        string partGlb = Path.Combine(temp, Path.GetFileNameWithoutExtension(other) + ".glb");
                        File.WriteAllBytes(partGlb, AemImport.ToGlb(part, Path.GetFileNameWithoutExtension(other)));
                        s.AddPart(role, project.AddModel(partGlb, $"ship_{ship}_{role}"), atlasPath);
                        result.parts.Add((ship, names[other], role));
                    }
                    bool textured = false;
                    if (atlasPath != null) { s.SetHullTexture("diffuse", atlasPath); textured = true; }
                    if (files.TryGetValue(model + "_diffuse.aei", out var diffuse) && WritePng(diffuse, Path.Combine(temp, "d.png"), Channels.All))
                    {
                        s.SetHullTexture("diffuse", project.AddTexture(Path.Combine(temp, "d.png"), $"ship_{ship}_diffuse"));
                        used.Add(model + "_diffuse.aei");
                        textured = true;
                    }
                    if (files.TryGetValue(model + "_normal_specular.aei", out var normal))
                    {
                        if (WritePng(normal, Path.Combine(temp, "n.png"), Channels.Normal))
                            s.SetHullTexture("normal", project.AddTexture(Path.Combine(temp, "n.png"), $"ship_{ship}_normal"));
                        if (WritePng(normal, Path.Combine(temp, "s.png"), Channels.Smoothness))
                            s.SetHullTexture("metallicSmoothness", project.AddTexture(Path.Combine(temp, "s.png"), $"ship_{ship}_metallic_smoothness"));
                        used.Add(model + "_normal_specular.aei");
                    }
                    result.hulls.Add((ship, names[hullKey], mesh.Triangles, textured));
                }

                // Every other texture: a skin of the game's texture of that name.
                foreach (var key in files.Keys.Where(k => k.EndsWith(".aei", StringComparison.OrdinalIgnoreCase) && !used.Contains(k)).ToList())
                {
                    string baseName = Path.GetFileNameWithoutExtension(key);
                    if (!WritePng(files[key], Path.Combine(temp, "t.png"), Channels.All)) { result.unused.Add(names[key]); continue; }
                    project.AddTexture(Path.Combine(temp, "t.png"), baseName);
                    result.skins.Add(baseName);
                    if (baseName.EndsWith("_normal_specular", StringComparison.OrdinalIgnoreCase)
                        && WritePng(files[key], Path.Combine(temp, "s.png"), Channels.Smoothness))
                        project.AddTexture(Path.Combine(temp, "s.png"), baseName.Substring(0, baseName.Length - "_normal_specular".Length) + "_metallic_smoothness");
                    used.Add(key);
                }
                foreach (var key in files.Keys.Where(k => !used.Contains(k))) result.unused.Add(names[key]);

                project.Save();
                result.project = project;
                return result;
            }
            catch (Exception e)
            {
                result.error = e.Message;
                if (project != null) { try { Directory.Delete(project.Folder, true); } catch { } }
                return result;
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }

        /// <summary>The hull file names to look for: each game ship's HD name ("ship_000_midorian", from ShipsByModel) and its
        /// first release's ("ship_00": two digits, the same numbering).</summary>
        static IEnumerable<(string model, int ship, bool firstRelease)> HullNames(Dictionary<string, int> ships)
        {
            foreach (var pair in ships) yield return (pair.Key, pair.Value, false);
            foreach (int ship in ships.Values.Distinct().OrderBy(i => i))
                if (ship < 100) yield return ($"ship_{ship:00}", ship, true);
        }

        /// <summary>An add part's role by the end of its file name after the hull's ("_lights_add" ...), null = not one (a
        /// "_lod_N", or something else). The HD versions: _lights_add, _engine_glow_add (the player's), _engine_add (the
        /// NPCs'); the first release: _lights, _add (its one engine glow).</summary>
        internal static string PartRole(string ending, bool firstRelease)
        {
            ending = ending.ToLowerInvariant();
            if (firstRelease) return ending == "_lights" ? CustomAddPart.Lights : ending == "_add" ? CustomAddPart.EngineGlow : null;
            return ending == "_lights_add" ? CustomAddPart.Lights : ending == "_engine_glow_add" ? CustomAddPart.EngineGlow
                 : ending == "_engine_add" ? CustomAddPart.Engine : null;
        }

        /// <summary>A part that is the hull again (as many triangles and the same size within a game unit): a copy.</summary>
        internal static bool SameShape(AemImport.Model a, AemImport.Model b)
        {
            if (a.Triangles != b.Triangles) return false;
            Bounds x = a.Bounds, y = b.Bounds;
            return (x.center - y.center).sqrMagnitude <= 1f && (x.size - y.size).sqrMagnitude <= 1f;
        }

        static readonly string[] RaceWords = { "terran", "vossk", "nivelian", "midorian", null, null, null, null, "pirates", "void" };

        /// <summary>The first release's texture atlas of the ship's race in the zip ("tex_mido_ships": "tex_" + a start of the
        /// race's name + "_ships"), written into the project once (its PNG in 'atlases'); its file key, or null.</summary>
        static string RaceAtlas(IEnumerable<string> keys, int ship, Dictionary<string, string> atlases, string temp, ModProject project,
                                Dictionary<string, byte[]> files)
        {
            int race = Shop.RaceOfShip(ship);
            string word = race >= 0 && race < RaceWords.Length ? RaceWords[race] : null;
            if (word == null) return null;
            foreach (var key in keys)
            {
                string k = key.ToLowerInvariant();
                if (!k.StartsWith("tex_") || !k.EndsWith("_ships.aei")) continue;
                string middle = k.Substring(4, k.Length - 4 - "_ships.aei".Length);
                if (middle.Length < 3 || !word.StartsWith(middle.Length > word.Length ? middle.Substring(0, word.Length) : middle)) continue;
                if (!atlases.ContainsKey(key))
                {
                    string png = Path.Combine(temp, "atlas.png");
                    if (!WritePng(files[key], png, Channels.All)) return null;
                    atlases[key] = project.AddTexture(png, Path.GetFileNameWithoutExtension(key));
                }
                return key;
            }
            return null;
        }

        enum Channels { All, Normal, Smoothness }

        /// <summary>An .aei as a PNG: as it is, its RGB as a normal map (alpha 255), or its alpha as a metallic / smoothness
        /// map (metallic 0, the smoothness in alpha). Worker-thread safe (ImageImport.DecodePixels, EncodeArrayToPNG).</summary>
        static bool WritePng(byte[] aei, string path, Channels channels)
        {
            if (!ImageImport.DecodePixels(aei, "texture.aei", out int w, out int h, out Color32[] px, out _)) return false;
            if (channels != Channels.All)
            {
                var copy = new Color32[px.Length];
                for (int i = 0; i < px.Length; i++)
                    copy[i] = channels == Channels.Normal ? new Color32(px[i].r, px[i].g, px[i].b, 255) : new Color32(0, 0, 0, px[i].a);
                px = copy;
            }
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(px, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h));
            return true;
        }
    }
}
