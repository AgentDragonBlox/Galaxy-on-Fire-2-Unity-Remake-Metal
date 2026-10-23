// OriginalModImportTests.cs
// Feature: CLAUDE.md "Mods" > Original-game mods in the builder (2026-10-22): the game's .aem read (Modding.AemImport: the
// layout of Reference/tools/asset_conversion/aem.py; parts and their children joined, positions in game units) and written as
// a valid .glb (metres, V flipped); an original-game mod's zip made into a project (OriginalModImport): a ship's hull as an
// override at its own length with the mod's textures as its hull textures (the normal map's RGB, its alpha the smoothness),
// copies of the hull left out, other textures as skins (a normal map with its metallic / smoothness), the rest named unused;
// the hull's additive layers kept as add parts by role, and the first release's names ("ship_00", "_lights", "_add") with its
// race atlas ("tex_mido_ships") as the hull's and the parts' texture (2026-10-23).
// Expected values from the files built here: a quad (2 triangles) with a child triangle; a box 4 x 2 x 10 game units; a quad
// of two triangles with three vertices each written with four (identical vertices joined).

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GoF2Remake.Data;
using GoF2Remake.Modding;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    public class OriginalModImportTests : SavesModsTest
    {
        /// <summary>A V5 .aem (floats, indexed, UVs and normals: flags 0x17) of one part per entry, the later ones children
        /// of the first.</summary>
        static byte[] Aem(params (Vector3[] pos, Vector2[] uv, int[] idx)[] parts)
        {
            var m = new MemoryStream();
            var w = new BinaryWriter(m);
            w.Write(Encoding.ASCII.GetBytes("V5AEMesh"));
            w.Write((byte)0);
            w.Write((byte)0x17);
            w.Write((ushort)1);   // one top part
            void Part(int i)
            {
                var (pos, uv, idx) = parts[i];
                w.Write(0f); w.Write(0f); w.Write(0f);   // pivot
                w.Write((ushort)idx.Length); foreach (int k in idx) w.Write((ushort)k);
                w.Write((ushort)pos.Length);
                foreach (var p in pos) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                foreach (var t in uv) { w.Write(t.x); w.Write(t.y); }
                foreach (var _ in pos) { w.Write(0f); w.Write(1f); w.Write(0f); }
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);   // bounding sphere
                for (int k = 0; k < 3; k++) w.Write((short)-1);     // no position / rotation / scale keys
                w.Write((short)0);                                   // no extra channel
                w.Write((ushort)0);                                  // no V5 channels
                if (i == 0) { w.Write((ushort)(parts.Length - 1)); for (int c = 1; c < parts.Length; c++) Part(c); }
                else w.Write((ushort)0);
            }
            Part(0);
            return m.ToArray();
        }

        static (Vector3[], Vector2[], int[]) Quad() =>
            (new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0), new Vector3(20, 0, 40), new Vector3(0, 0, 40) },
             new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 0.25f), new Vector2(0, 0.25f) },
             new[] { 0, 1, 2, 0, 2, 3 });

        static (Vector3[], Vector2[], int[]) Box(float x, float y, float z)
        {
            var pos = new List<Vector3>();
            for (int i = 0; i < 8; i++) pos.Add(new Vector3((i & 1) != 0 ? x : 0, (i & 2) != 0 ? y : 0, (i & 4) != 0 ? z : 0));
            var idx = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
            return (pos.ToArray(), pos.Select(p => new Vector2(p.x / x, p.z / z)).ToArray(), idx);
        }

        /// <summary>A raw RGBA .aei ("AEimage", type 3) of one colour.</summary>
        static byte[] Aei(int size, Color32 c)
        {
            var m = new MemoryStream();
            var w = new BinaryWriter(m);
            w.Write(Encoding.ASCII.GetBytes("AEimage"));
            w.Write((byte)0);
            w.Write((byte)3);
            w.Write((ushort)size); w.Write((ushort)size); w.Write((ushort)0);
            for (int i = 0; i < size * size; i++) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
            return m.ToArray();
        }

        static Color32 PixelOf(string png)
        {
            var t = new Texture2D(2, 2);
            try { Assert.IsTrue(t.LoadImage(File.ReadAllBytes(png))); return t.GetPixel(0, 0); }
            finally { Object.DestroyImmediate(t); }
        }

        [Test]
        public void AnAemIsReadWithItsChildrenAndWrittenAsAValidGlb()
        {
            var tri = (new[] { new Vector3(0, 10, 0), new Vector3(5, 10, 0), new Vector3(0, 10, 5) }, new Vector2[3], new[] { 0, 1, 2 });
            var bytes = Aem(Quad(), tri);
            Assert.IsTrue(AemImport.Is(bytes));
            var m = AemImport.Read(bytes, out string error);
            Assert.IsNull(error);
            Assert.AreEqual(2, m.parts);
            Assert.AreEqual(7, m.positions.Count);
            Assert.AreEqual(3, m.Triangles);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 }, m.indices, "the child's triangle after the parent's vertices");
            Assert.AreEqual(40f, m.Length, 1e-4f);

            var glb = AemImport.ToGlb(m, "test");
            Assert.AreEqual("glTF", Encoding.ASCII.GetString(glb, 0, 4));
            int jsonLength = System.BitConverter.ToInt32(glb, 12);
            var json = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
            var prim = json["meshes"][0]["primitives"][0];
            var position = json["accessors"][(int)prim["attributes"]["POSITION"]];
            Assert.AreEqual(7, (int)position["count"]);
            Assert.AreEqual(40 * AemImport.MetresPerUnit, (float)position["max"][2], 1e-5f, "metres");
            Assert.AreEqual(9, (int)json["accessors"][(int)prim["indices"]]["count"]);
            Assert.AreEqual(5125, (int)json["accessors"][(int)prim["indices"]]["componentType"]);
            // V flipped: the quad's third vertex (v 0.25) is v 0.75 in the file.
            var uvAccessor = json["accessors"][(int)prim["attributes"]["TEXCOORD_0"]];
            var view = json["bufferViews"][(int)uvAccessor["bufferView"]];
            int binStart = 20 + jsonLength + 8;
            float v2 = System.BitConverter.ToSingle(glb, binStart + (int)view["byteOffset"] + 2 * 8 + 4);
            Assert.AreEqual(0.75f, v2, 1e-6f);
            // A quad given as two triangles of three vertices each is written with its four.
            var split = (new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0), new Vector3(20, 0, 40), new Vector3(0, 0, 0), new Vector3(20, 0, 40), new Vector3(0, 0, 40) },
                         new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1) },
                         new[] { 0, 1, 2, 3, 4, 5 });
            var splitGlb = AemImport.ToGlb(AemImport.Read(Aem(split), out _), "split");
            var splitJson = JObject.Parse(Encoding.UTF8.GetString(splitGlb, 20, System.BitConverter.ToInt32(splitGlb, 12)));
            Assert.AreEqual(4, (int)splitJson["accessors"][(int)splitJson["meshes"][0]["primitives"][0]["attributes"]["POSITION"]]["count"]);
            Assert.IsNull(AemImport.Read(Encoding.ASCII.GetBytes("not a model at all"), out string bad));
            Assert.IsNotNull(bad);
        }

        [Test]
        public void AnOriginalGameModBecomesAProject()
        {
            string zip = Path.Combine(GamePaths.Data, "og_mod.zip");
            var hull = Aem(Box(4, 2, 10));
            using (var a = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
            {
                void Put(string name, byte[] data) { using (var s = a.CreateEntry(name).Open()) s.Write(data, 0, data.Length); }
                Put("MESHES/ship_016_nivelian.aem", hull);
                Put("MESHES/ship_016_nivelian_lod_1.aem", hull);
                Put("MESHES/ship_016_nivelian_lights_add.aem", Aem(Quad()));
                Put("MESHES/station_x.aem", Aem(Quad()));
                Put("TEXTURES/ship_016_nivelian_diffuse.aei", Aei(4, new Color32(200, 10, 10, 255)));
                Put("TEXTURES/ship_016_nivelian_normal_specular.aei", Aei(4, new Color32(128, 128, 255, 77)));
                Put("TEXTURES/ship_028_terran_normal_specular.aei", Aei(4, new Color32(128, 128, 255, 40)));
                Put("readme.txt", Encoding.UTF8.GetBytes("hi"));
            }
            Assert.IsTrue(OriginalModImport.Looks(zip));

            var project = ModProject.Create("Narn test", "");
            var r = OriginalModImport.Import(zip, project, Database.Shared, new Dictionary<string, int> { ["ship_016_nivelian"] = 16 });
            Assert.IsNull(r.error);
            Assert.AreEqual(16, r.hulls.Single().ship);
            Assert.IsTrue(r.hulls[0].textured);
            Assert.AreEqual(1, r.hullCopies, "the _lod_1 copy of the hull");
            CollectionAssert.AreEquivalent(new[] { "MESHES/station_x.aem" }, r.unused);
            Assert.AreEqual((16, "MESHES/ship_016_nivelian_lights_add.aem", CustomAddPart.Lights), r.parts.Single(), "a real lights layer is kept");
            CollectionAssert.AreEqual(new[] { "ship_028_terran_normal_specular" }, r.skins);

            var back = ModProject.Load(project.Folder);
            var ship = back.Ships.Single();
            Assert.IsTrue(ship.IsOverride);
            Assert.AreEqual(16, ship.OverrideIndex);
            Assert.AreEqual(10f, ship.ModelLength, 1e-3f, "its own length, so it keeps the size it had in the game");
            Assert.IsTrue(File.Exists(Path.Combine(back.Folder, ship.Model)));
            Assert.Greater(ship.Mounts.Count, 0, "the game ship's hardpoints to start from");
            Assert.AreEqual(new Color32(200, 10, 10, 255), PixelOf(Path.Combine(back.Folder, ship.HullTexture("diffuse"))));
            Assert.AreEqual(new Color32(128, 128, 255, 255), PixelOf(Path.Combine(back.Folder, ship.HullTexture("normal"))));
            Assert.AreEqual(77, PixelOf(Path.Combine(back.Folder, ship.HullTexture("metallicSmoothness"))).a, "the game's specular as smoothness");
            Assert.AreEqual(40, PixelOf(Path.Combine(back.Folder, "textures", "ship_028_terran_metallic_smoothness.png")).a);
            Assert.IsTrue(File.Exists(Path.Combine(back.Folder, "textures", "ship_028_terran_normal_specular.png")));
            var part = ship.AddParts.Single();
            Assert.AreEqual(CustomAddPart.Lights, part.role);
            Assert.IsNull(part.texture, "the game's material for the role");
            Assert.IsTrue(File.Exists(Path.Combine(back.Folder, part.model)));
        }

        [Test]
        public void TheFirstReleasesShipsKeepTheirPartsAndTheRaceAtlas()
        {
            // The first release's names: ship_NN with _lights and _add (its engine glow); a part that is the hull again
            // (same triangles and size, other bytes) is a copy; the race's atlas is the hull's and the parts' texture.
            string zip = Path.Combine(GamePaths.Data, "first_release.zip");
            var box = Box(4, 2, 10);
            var moved = (box.Item1.Select(p => p + new Vector3(0.2f, 0, 0)).ToArray(), box.Item2, box.Item3);
            using (var a = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
            {
                void Put(string name, byte[] data) { using (var s = a.CreateEntry(name).Open()) s.Write(data, 0, data.Length); }
                Put("ship_00.aem", Aem(box));
                Put("ship_00_lights.aem", Aem(Quad()));
                Put("ship_00_add.aem", Aem(Quad(), Quad()));
                Put("ship_00_lod_1.aem", Aem(moved));
                Put("tex_mido_ships.aei", Aei(4, new Color32(10, 200, 10, 255)));
            }
            var project = ModProject.Create("First release test", "");
            var r = OriginalModImport.Import(zip, project, Database.Shared, new Dictionary<string, int> { ["ship_000_midorian"] = 0 });
            Assert.IsNull(r.error);
            Assert.AreEqual(0, r.hulls.Single().ship);
            Assert.IsTrue(r.hulls[0].textured, "the race atlas");
            Assert.AreEqual(1, r.hullCopies, "the lod copy, moved by less than a unit");
            CollectionAssert.IsEmpty(r.unused);
            CollectionAssert.IsEmpty(r.skins, "the atlas is the ship's texture, not a skin");
            CollectionAssert.AreEquivalent(new[] { CustomAddPart.Lights, CustomAddPart.EngineGlow }, r.parts.Select(p => p.role));

            var ship = ModProject.Load(project.Folder).Ships.Single();
            Assert.AreEqual(0, ship.OverrideIndex);
            string atlas = ship.HullTexture("diffuse");
            Assert.AreEqual(new Color32(10, 200, 10, 255), PixelOf(Path.Combine(project.Folder, atlas)));
            Assert.IsTrue(ship.AddParts.All(p => p.texture == atlas), "the parts drawn from the same atlas");
        }

        [Test]
        public void PartRolesByTheirNames()
        {
            Assert.AreEqual(CustomAddPart.Lights, OriginalModImport.PartRole("_lights_add", false));
            Assert.AreEqual(CustomAddPart.EngineGlow, OriginalModImport.PartRole("_engine_glow_add", false));
            Assert.AreEqual(CustomAddPart.Engine, OriginalModImport.PartRole("_ENGINE_ADD", false));
            Assert.IsNull(OriginalModImport.PartRole("_lod_1", false));
            Assert.IsNull(OriginalModImport.PartRole("_add", false), "the HD names have no plain _add");
            Assert.AreEqual(CustomAddPart.Lights, OriginalModImport.PartRole("_lights", true));
            Assert.AreEqual(CustomAddPart.EngineGlow, OriginalModImport.PartRole("_add", true));
            Assert.IsNull(OriginalModImport.PartRole("_engine_add", true));
            Assert.AreEqual("_engine_glow_add", CustomAddPart.Suffix(CustomAddPart.EngineGlow));
        }

        [Test]
        public void ARemakeModIsNoOriginalGameMod()
        {
            string zip = Path.Combine(GamePaths.Data, "remake_mod.zip");
            using (var a = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
            {
                using (var s = new StreamWriter(a.CreateEntry("mod.json").Open())) s.Write("{\"id\":\"x\"}");
                using (var s = a.CreateEntry("textures/ship_016_nivelian_diffuse.aei").Open()) { var b = Aei(2, new Color32(1, 2, 3, 4)); s.Write(b, 0, b.Length); }
            }
            Assert.IsFalse(OriginalModImport.Looks(zip));
            Assert.IsFalse(OriginalModImport.Looks(Path.Combine(GamePaths.Data, "missing.zip")));
        }
    }
}
