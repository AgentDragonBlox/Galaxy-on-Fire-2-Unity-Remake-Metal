// ModOptimizerTests.cs
// Feature: CLAUDE.md "Mods" > Optimize models and textures (the Mod Builder's Optimize window, 2026-10-22): a model's embedded
// textures scaled down to the size picked and stored as JPEG unless they use their alpha; the polygon reduction
// (MeshSimplifier) keeping every vertex where it was and never removing a seam; the GLB written again stays a valid file;
// a project's own image file keeps its format; nothing that wouldn't get smaller is touched; a model giving every triangle
// its own three vertices (the game's .aem files) is joined first, so it is cut like a shared one.
// Expected values: a 2048 px noise texture at 1K (1024 x 1024); a closed 32 x 16 sphere (960 triangles) cut to a quarter.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GoF2Remake.Modding;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    public class ModOptimizerTests
    {
        string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "gof2_optimizer_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "models"));
            Directory.CreateDirectory(Path.Combine(folder, "textures"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        /// <summary>A closed sphere sharing its vertices (no seams): 'rings' x 'segments'.</summary>
        static (Vector3[] pos, int[] idx) Sphere(int segments, int rings)
        {
            var pos = new List<Vector3> { Vector3.up };
            for (int r = 1; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    float th = Mathf.PI * r / rings, ph = 2f * Mathf.PI * s / segments;
                    pos.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)));
                }
            pos.Add(Vector3.down);
            int V(int r, int s) => 1 + (r - 1) * segments + (s % segments);
            int bottom = pos.Count - 1;
            var idx = new List<int>();
            for (int s = 0; s < segments; s++) idx.AddRange(new[] { 0, V(1, s + 1), V(1, s) });
            for (int r = 1; r < rings - 1; r++)
                for (int s = 0; s < segments; s++)
                {
                    idx.AddRange(new[] { V(r, s), V(r, s + 1), V(r + 1, s) });
                    idx.AddRange(new[] { V(r, s + 1), V(r + 1, s + 1), V(r + 1, s) });
                }
            for (int s = 0; s < segments; s++) idx.AddRange(new[] { bottom, V(rings - 1, s), V(rings - 1, s + 1) });
            return (pos.ToArray(), idx.ToArray());
        }

        [Test]
        public void TheSimplifierCutsAClosedMeshToItsShareWithItsOwnVertices()
        {
            var (pos, idx) = Sphere(32, 16);
            Assert.AreEqual(960, idx.Length / 3);
            var cut = MeshSimplifier.Simplify(pos, idx, 0.25f);
            int tris = cut.Length / 3;
            Assert.LessOrEqual(tris, 240 + 4, "about a quarter");
            Assert.Greater(tris, 100, "not more than asked");
            foreach (int i in cut) Assert.IsTrue(i >= 0 && i < pos.Length);
            float Facing(int[] list, int t)
            {
                Vector3 a = pos[list[t * 3]], b = pos[list[t * 3 + 1]], c = pos[list[t * 3 + 2]];
                return Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f);
            }
            float side = Mathf.Sign(Facing(idx, 0));
            for (int t = 0; t < tris; t++)
            {
                Vector3 a = pos[cut[t * 3]], b = pos[cut[t * 3 + 1]], c = pos[cut[t * 3 + 2]];
                Assert.Greater(Vector3.Cross(b - a, c - a).magnitude, 1e-6f, "no flat triangle");
                Assert.Greater(Facing(cut, t) * side, -1e-5f, "facing the way the sphere's triangles do: none turned over");
            }
            CollectionAssert.AreEqual(idx, MeshSimplifier.Simplify(pos, idx, 1f), "a share of 1 changes nothing");
        }

        [Test]
        public void ASeamIsNeverCollapsed()
        {
            var (pos, idx) = Sphere(16, 8);
            // Vertex 5's point given a second copy (a UV seam) that some triangles use instead.
            var withSeam = pos.Concat(new[] { pos[5] }).ToArray();
            int copy = withSeam.Length - 1;
            var idx2 = (int[])idx.Clone();
            int used = 0;
            for (int i = 0; i < idx2.Length && used < 2; i++) if (idx2[i] == 5) { idx2[i] = copy; used++; }
            var cut = MeshSimplifier.Simplify(withSeam, idx2, 0.2f);
            Assert.Contains(5, cut, "the seam vertex stays");
            Assert.Contains(copy, cut, "and its copy");
        }

        [Test]
        public void HalvingAveragesBlocks()
        {
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32((byte)(i * 10), 0, 0, 255);
            int w = 4, h = 4;
            var half = ModOptimizer.Halve(px, ref w, ref h);
            Assert.AreEqual(2, w);
            Assert.AreEqual(2, h);
            Assert.AreEqual((0 + 10 + 40 + 50 + 2) / 4, half[0].r);
            Assert.IsFalse(ModOptimizer.UsesAlpha(half));
            half[3].a = 128;
            Assert.IsTrue(ModOptimizer.UsesAlpha(half));
        }

        static byte[] NoisePng(int size, bool alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var rnd = new System.Random(7);
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), alpha && i % 7 == 0 ? (byte)100 : (byte)255);
            tex.SetPixels32(px);
            tex.Apply();
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>A GLB with the sphere (positions, normals, indices in views of their own) and one embedded PNG; 'split':
        /// every triangle with three vertices of its own.</summary>
        static byte[] Glb(byte[] png, bool split = false)
        {
            var (pos, idx) = Sphere(32, 16);
            if (split) { pos = idx.Select(i => pos[i]).ToArray(); idx = Enumerable.Range(0, idx.Length).ToArray(); }
            var bin = new MemoryStream();
            void Align() { while (bin.Length % 4 != 0) bin.WriteByte(0); }
            var views = new JArray();
            int View(byte[] data, int? target)
            {
                Align();
                var v = new JObject { ["buffer"] = 0, ["byteOffset"] = (int)bin.Length, ["byteLength"] = data.Length };
                if (target != null) v["target"] = target;
                bin.Write(data, 0, data.Length);
                views.Add(v);
                return views.Count - 1;
            }
            byte[] Floats(IEnumerable<Vector3> vs) { var m = new MemoryStream(); foreach (var v in vs) { m.Write(System.BitConverter.GetBytes(v.x), 0, 4); m.Write(System.BitConverter.GetBytes(v.y), 0, 4); m.Write(System.BitConverter.GetBytes(v.z), 0, 4); } return m.ToArray(); }
            int posView = View(Floats(pos), 34962), nrmView = View(Floats(pos.Select(p => p.normalized)), 34962);
            var ib = new MemoryStream();
            foreach (int i in idx) ib.Write(System.BitConverter.GetBytes((ushort)i), 0, 2);
            int idxView = View(ib.ToArray(), 34963);
            int imgView = View(png, null);
            Align();
            var root = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0" },
                ["buffers"] = new JArray(new JObject { ["byteLength"] = (int)bin.Length }),
                ["bufferViews"] = views,
                ["accessors"] = new JArray(
                    new JObject { ["bufferView"] = posView, ["componentType"] = 5126, ["count"] = pos.Length, ["type"] = "VEC3", ["min"] = new JArray(-1, -1, -1), ["max"] = new JArray(1, 1, 1) },
                    new JObject { ["bufferView"] = nrmView, ["componentType"] = 5126, ["count"] = pos.Length, ["type"] = "VEC3" },
                    new JObject { ["bufferView"] = idxView, ["componentType"] = 5123, ["count"] = idx.Length, ["type"] = "SCALAR" }),
                ["meshes"] = new JArray(new JObject { ["primitives"] = new JArray(new JObject { ["attributes"] = new JObject { ["POSITION"] = 0, ["NORMAL"] = 1 }, ["indices"] = 2, ["material"] = 0 }) }),
                ["images"] = new JArray(new JObject { ["bufferView"] = imgView, ["mimeType"] = "image/png" }),
                ["textures"] = new JArray(new JObject { ["source"] = 0 }),
                ["materials"] = new JArray(new JObject { ["pbrMetallicRoughness"] = new JObject { ["baseColorTexture"] = new JObject { ["index"] = 0 } } }),
                ["nodes"] = new JArray(new JObject { ["mesh"] = 0 }),
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
                ["scene"] = 0
            };
            var json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
            int jl = (json.Length + 3) / 4 * 4;
            var o = new MemoryStream();
            void U32(long x) => o.Write(System.BitConverter.GetBytes((uint)x), 0, 4);
            U32(0x46546C67); U32(2); U32(12 + 8 + jl + 8 + bin.Length);
            U32(jl); U32(0x4E4F534A); o.Write(json, 0, json.Length); for (int i = json.Length; i < jl; i++) o.WriteByte(0x20);
            U32(bin.Length); U32(0x004E4942); bin.WriteTo(o);
            return o.ToArray();
        }

        static void Run(ModOptimizer job)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!job.Finished && sw.Elapsed.TotalSeconds < 60) { job.Step(); System.Threading.Thread.Sleep(1); }
            Assert.IsTrue(job.Finished, "done in time");
        }

        /// <summary>The GLB's JSON and its binary chunk (checked to fit the file).</summary>
        static (JObject root, byte[] bin) Read(byte[] glb)
        {
            Assert.AreEqual(glb.Length, (int)System.BitConverter.ToUInt32(glb, 8), "header length");
            int jl = (int)System.BitConverter.ToUInt32(glb, 12);
            var root = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jl));
            int bl = (int)System.BitConverter.ToUInt32(glb, 20 + jl);
            var bin = new byte[bl];
            System.Buffer.BlockCopy(glb, 28 + jl, bin, 0, bl);
            Assert.AreEqual(bl, (int)root["buffers"][0]["byteLength"]);
            foreach (var v in root["bufferViews"]) Assert.LessOrEqual((int)v["byteOffset"] + (int)v["byteLength"], bl);
            return (root, bin);
        }

        [Test]
        public void AModelsTexturesShrinkToJpegAndItsTrianglesToTheirShare()
        {
            File.WriteAllBytes(Path.Combine(folder, "models/ball.glb"), Glb(NoisePng(2048, false)));
            long before = new FileInfo(Path.Combine(folder, "models/ball.glb")).Length;
            CollectionAssert.Contains(ModOptimizer.Candidates(folder), "models/ball.glb");
            Assert.AreEqual(960, ModOptimizer.CountTriangles(Path.Combine(folder, "models/ball.glb")));

            var job = new ModOptimizer(folder, new[] { "models/ball.glb" }, new ModOptimizer.Options { maxSize = 1024, quality = 85, jpeg = true, triangles = 0.5f });
            Run(job);
            var r = job.Results.Single();
            Assert.IsNull(r.error);
            Assert.Less(r.after, before / 2);
            Assert.AreEqual(960, r.trianglesBefore);
            Assert.LessOrEqual(r.trianglesAfter, 490);

            var (root, bin) = Read(File.ReadAllBytes(Path.Combine(folder, "models/ball.glb")));
            Assert.AreEqual("image/jpeg", (string)root["images"][0]["mimeType"]);
            var iv = root["bufferViews"][(int)root["images"][0]["bufferView"]];
            var jpg = new byte[(int)iv["byteLength"]];
            System.Buffer.BlockCopy(bin, (int)iv["byteOffset"], jpg, 0, jpg.Length);
            var tex = new Texture2D(2, 2);
            Assert.IsTrue(tex.LoadImage(jpg));
            Assert.AreEqual(1024, tex.width);
            Object.DestroyImmediate(tex);

            // The vertices no triangle uses are gone from both attributes, every index points at one that is left.
            var acc = (JArray)root["accessors"];
            int verts = (int)acc[0]["count"];
            Assert.AreEqual(verts, (int)acc[1]["count"]);
            Assert.Less(verts, Sphere(32, 16).pos.Length);
            Assert.IsNotNull(acc[0]["min"]);
            var idxView = root["bufferViews"][(int)acc[2]["bufferView"]];
            int idxSize = (int)acc[2]["componentType"] == 5125 ? 4 : 2;
            for (int i = 0; i < (int)acc[2]["count"]; i++)
            {
                int o = (int)idxView["byteOffset"] + i * idxSize;
                int v = idxSize == 4 ? (int)System.BitConverter.ToUInt32(bin, o) : System.BitConverter.ToUInt16(bin, o);
                Assert.Less(v, verts);
            }
            Assert.AreEqual(r.trianglesAfter, ModOptimizer.CountTriangles(Path.Combine(folder, "models/ball.glb")));
        }

        [Test]
        public void AModelWithThreeVerticesPerTriangleIsJoinedAndCut()
        {
            File.WriteAllBytes(Path.Combine(folder, "models/split.glb"), Glb(NoisePng(8, false), split: true));
            var job = new ModOptimizer(folder, new[] { "models/split.glb" }, new ModOptimizer.Options { maxSize = 4096, triangles = 0.25f });
            Run(job);
            var r = job.Results.Single();
            Assert.IsNull(r.error);
            Assert.AreEqual(960, r.trianglesBefore);
            Assert.LessOrEqual(r.trianglesAfter, 260, "cut to about a quarter: no point is a seam once joined");
            var (root, _) = Read(File.ReadAllBytes(Path.Combine(folder, "models/split.glb")));
            Assert.LessOrEqual((int)root["accessors"][0]["count"], Sphere(32, 16).pos.Length);

            // With every triangle kept the joining alone still makes it smaller.
            File.WriteAllBytes(Path.Combine(folder, "models/split.glb"), Glb(NoisePng(8, false), split: true));
            job = new ModOptimizer(folder, new[] { "models/split.glb" }, new ModOptimizer.Options { maxSize = 4096, triangles = 1f });
            Run(job);
            r = job.Results.Single();
            Assert.Less(r.after, r.before);
            Assert.AreEqual(960, r.trianglesAfter);
            (root, _) = Read(File.ReadAllBytes(Path.Combine(folder, "models/split.glb")));
            Assert.AreEqual(Sphere(32, 16).pos.Length, (int)root["accessors"][0]["count"]);
        }

        [Test]
        public void ATransparentTextureStaysPngAndAProjectImageKeepsItsFormat()
        {
            File.WriteAllBytes(Path.Combine(folder, "models/glass.glb"), Glb(NoisePng(2048, true)));
            File.WriteAllBytes(Path.Combine(folder, "textures/hull.png"), NoisePng(2048, false));
            var job = new ModOptimizer(folder, new[] { "models/glass.glb", "textures/hull.png" }, new ModOptimizer.Options { maxSize = 1024 });
            Run(job);
            Assert.IsTrue(job.Results.All(r => r.error == null && r.after < r.before));
            var (root, _) = Read(File.ReadAllBytes(Path.Combine(folder, "models/glass.glb")));
            Assert.AreEqual("image/png", (string)root["images"][0]["mimeType"], "its alpha is used");
            var png = File.ReadAllBytes(Path.Combine(folder, "textures/hull.png"));
            Assert.AreEqual(0x89, png[0], "still a PNG under its name");
        }

        [Test]
        public void AFileThatWouldNotShrinkIsLeftAlone()
        {
            var small = NoisePng(256, false);
            File.WriteAllBytes(Path.Combine(folder, "textures/small.png"), small);
            var job = new ModOptimizer(folder, new[] { "textures/small.png" }, new ModOptimizer.Options { maxSize = 1024 });
            Run(job);
            CollectionAssert.AreEqual(small, File.ReadAllBytes(Path.Combine(folder, "textures/small.png")));
            Assert.AreEqual(job.Results[0].before, job.Results[0].after);
        }
    }
}
