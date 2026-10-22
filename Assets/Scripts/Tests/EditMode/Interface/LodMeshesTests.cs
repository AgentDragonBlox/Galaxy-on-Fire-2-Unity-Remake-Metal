// LodMeshesTests.cs
// Feature: CLAUDE.md "Options" > Levels of detail (AutoLods, 2026-10-22): a mesh's lower levels are cut by the Mod Builder
// optimizer's simplifier after its identical vertices are joined (a model with three vertices per triangle is cut like a
// shared one), level 1 to about half the triangles and level 2 to about a fifth; a level is a mesh of its own with only
// the vertices it uses; the levels go through a file as indices and come back the same; a changed mesh hashes otherwise;
// light / glow layers and small meshes get none; the option is on by default; a UV seam is cut along itself
// (each side keeping its own copies) and the mesh stays closed; an open panel keeps its outline; the original's LOD meshes
// that are copies of LOD 0 are found by their hull.
// Expected values: a closed 64 x 32 sphere written with three vertices per triangle: 3968 triangles, 11904 vertices.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using GoF2Remake.Data;
using GoF2Remake.Visuals;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    public class LodMeshesTests
    {
        readonly List<Object> made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        /// <summary>A closed sphere, each triangle with its own three vertices (smooth normals, so twins are alike).</summary>
        Mesh Sphere(int segments, int rings, string name = "test_hull")
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
            var verts = idx.Select(i => pos[i]).ToArray();
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.normals = verts.Select(v => v.normalized).ToArray();
            mesh.triangles = Enumerable.Range(0, verts.Length).ToArray();
            mesh.RecalculateBounds();
            made.Add(mesh);
            return mesh;
        }

        /// <summary>A closed sphere sharing its vertices, wrapped in a texture: the meridian at u 0 has a second copy at u 1
        /// (a UV seam, like the game's models have nearly everywhere).</summary>
        Mesh SeamSphere(int segments, int rings)
        {
            var pos = new List<Vector3> { Vector3.up };
            var uv = new List<Vector2> { new Vector2(0.5f, 1f) };
            for (int r = 1; r < rings; r++)
                for (int s = 0; s <= segments; s++)
                {
                    float th = Mathf.PI * r / rings, ph = 2f * Mathf.PI * s / segments;
                    pos.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)));
                    uv.Add(new Vector2((float)s / segments, 1f - (float)r / rings));
                }
            pos.Add(Vector3.down);
            uv.Add(new Vector2(0.5f, 0f));
            // s == segments is the copy column: the same points as s == 0 (by the same sum: bit for bit).
            for (int r = 1; r < rings; r++) pos[1 + (r - 1) * (segments + 1) + segments] = pos[1 + (r - 1) * (segments + 1)];
            int V(int r, int s) => 1 + (r - 1) * (segments + 1) + s;
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
            var mesh = new Mesh { name = "test_seam_hull", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(pos);
            mesh.SetNormals(pos.Select(v => v.normalized).ToList());
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateBounds();
            made.Add(mesh);
            return mesh;
        }

        [Test]
        public void ASeamIsCutAlongItselfAndStaysClosed()
        {
            var mesh = SeamSphere(64, 32);
            Assert.AreEqual(3968, LodMeshes.Triangles(mesh));
            var c = LodMeshes.Capture(mesh);
            var levels = LodMeshes.Simplify(c);
            Assert.AreEqual(2, levels.Length, "level 1 and level 2, the seam no longer holding the mesh");
            Assert.LessOrEqual(levels[1][0].Length / 3, 3968 / 5 + 8, "level 2 about a fifth");
            var uv = mesh.uv;
            var full = mesh.triangles;
            float side = Mathf.Sign(UvAreas(full, uv).Sum());
            float whole = UvAreas(full, uv).Sum(x => Mathf.Abs(x));
            foreach (var level in levels)
            {
                var tris = level[0];
                var edges = new Dictionary<(Vector3, Vector3), int>();
                for (int t = 0; t < tris.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 a = c.positions[tris[t + k]], b = c.positions[tris[t + (k + 1) % 3]];
                        var key = Before(a, b) ? (a, b) : (b, a);
                        edges.TryGetValue(key, out int n);
                        edges[key] = n + 1;
                    }
                Assert.IsTrue(edges.Values.All(n => n == 2), "closed: every edge between two triangles, no crack along the seam");
                // A triangle reaching across the seam (one side's copy with the other side's) would lie turned over in the
                // texture, or cover more of it than the sphere does.
                Assert.IsTrue(UvAreas(tris, uv).All(x => x * side > -1e-7f), "every triangle the right way round in the texture");
                Assert.Less(UvAreas(tris, uv).Sum(x => Mathf.Abs(x)), whole * 1.02f, "and covering no more of it than the sphere");
            }
        }

        [Test]
        public void AnOpenPanelIsCutAlongItsBorderAndKeepsItsOutline()
        {
            const int cells = 40;
            var pos = new List<Vector3>();
            for (int y = 0; y <= cells; y++)
                for (int x = 0; x <= cells; x++)
                    pos.Add(new Vector3(x, 0.02f * Mathf.Sin(x * 0.7f) * Mathf.Sin(y * 0.5f), y));
            var idx = new List<int>();
            int V(int x, int y) => y * (cells + 1) + x;
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    idx.AddRange(new[] { V(x, y), V(x, y + 1), V(x + 1, y) });
                    idx.AddRange(new[] { V(x + 1, y), V(x, y + 1), V(x + 1, y + 1) });
                }
            var mesh = new Mesh { name = "test_panel", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(pos);
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            made.Add(mesh);
            var levels = LodMeshes.Simplify(LodMeshes.Capture(mesh));
            Assert.AreEqual(2, levels.Length, "the open border no longer holds it");
            Assert.LessOrEqual(levels[1][0].Length / 3, 3200 / 5 + 8, "level 2 about a fifth");
            foreach (int corner in new[] { V(0, 0), V(cells, 0), V(0, cells), V(cells, cells) })
                Assert.Contains(corner, levels[1][0], "the panel's corners stay where they are: the outline holds");
        }

        [Test]
        public void TheOriginalsCopiedLodMeshesAreFoundByTheirHull()
        {
            var hullMesh = Sphere(16, 8);
            var copy = Object.Instantiate(hullMesh);
            made.Add(copy);
            var other = Sphere(12, 6, "test_other");
            var root = new GameObject("test_object");
            made.Add(root);
            MeshRenderer Part(string partName, Mesh mesh, Vector3 at)
            {
                var go = new GameObject(partName);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = at;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                return go.AddComponent<MeshRenderer>();
            }
            var hull = Part("hull", hullMesh, Vector3.zero);
            var lod1 = Part("hull_lod_1", copy, Vector3.zero);
            var moved = Part("hull_lod_2", copy, Vector3.right * 5f);   // the same mesh elsewhere: not this hull's copy
            var different = Part("other_lod_2", other, Vector3.zero);
            root.transform.position = new Vector3(100, 20, -50);
            root.transform.rotation = Quaternion.Euler(10, 70, 0);
            var lods = new[]
            {
                new LOD(0.3f, new Renderer[] { hull }),
                new LOD(0.1f, new Renderer[] { lod1 }),
                new LOD(0f, new Renderer[] { lod1, moved, different }),
            };
            var found = AutoLods.CopiedLevels(root.transform, lods);
            Assert.AreEqual(1, found.Count, "only the copy in the hull's own place");
            Assert.AreEqual(lod1.GetComponent<MeshFilter>(), found[0].lower);
            Assert.AreEqual(hull.GetComponent<MeshFilter>(), found[0].hull);
            Assert.AreEqual(1, found[0].level, "the level it first shows in");
        }

        static IEnumerable<float> UvAreas(int[] tris, Vector2[] uv)
        {
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector2 a = uv[tris[t]], b = uv[tris[t + 1]], c = uv[tris[t + 2]];
                yield return ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
            }
        }

        static bool Before(Vector3 a, Vector3 b) => a.x != b.x ? a.x < b.x : a.y != b.y ? a.y < b.y : a.z < b.z;

        [Test]
        public void ASphereWithThreeVerticesPerTriangleGetsTwoLevels()
        {
            var mesh = Sphere(64, 32);
            Assert.AreEqual(3968, LodMeshes.Triangles(mesh));
            Assert.AreEqual(11904, mesh.vertexCount);
            Assert.IsTrue(LodMeshes.Eligible(mesh));
            var c = LodMeshes.Capture(mesh);
            Assert.IsNotNull(c);
            var levels = LodMeshes.Simplify(c);
            Assert.AreEqual(2, levels.Length, "level 1 and level 2: joined, the sphere has no seams");
            int t1 = levels[0][0].Length / 3, t2 = levels[1][0].Length / 3;
            Assert.LessOrEqual(t1, 3968 / 2 + 8, "level 1 about half");
            Assert.Greater(t1, 3968 / 4, "not far below half");
            Assert.LessOrEqual(t2, 3968 / 5 + 8, "level 2 about a fifth");
            foreach (var level in levels) foreach (int i in level[0]) Assert.IsTrue(i >= 0 && i < mesh.vertexCount);
        }

        [Test]
        public void ALevelIsAMeshOfItsOwnWithOnlyItsVertices()
        {
            var mesh = Sphere(64, 32);
            var levels = LodMeshes.Simplify(LodMeshes.Capture(mesh));
            var lod = LodMeshes.Build(mesh, levels[0], mesh.name + "_lod_gen1");
            made.Add(lod);
            Assert.IsNotNull(lod);
            Assert.AreEqual(1, lod.subMeshCount);
            Assert.AreEqual((uint)levels[0][0].Length, lod.GetIndexCount(0));
            Assert.LessOrEqual(lod.vertexCount, levels[0][0].Distinct().Count(), "only the vertices its triangles use");
            Assert.IsFalse(lod.isReadable, "GPU only: HullCollision never takes it");
            Assert.AreEqual(mesh.bounds.size, lod.bounds.size, "the source's bounds");
        }

        [Test]
        public void LevelsGoThroughAFileUnchanged()
        {
            var mesh = Sphere(64, 32);
            var levels = LodMeshes.Simplify(LodMeshes.Capture(mesh));
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) LodMeshes.Write(w, levels);
            ms.Position = 0;
            int[][][] back;
            using (var r = new BinaryReader(ms)) back = LodMeshes.Read(r);
            Assert.AreEqual(levels.Length, back.Length);
            for (int l = 0; l < levels.Length; l++) CollectionAssert.AreEqual(levels[l][0], back[l][0]);
        }

        [Test]
        public void AChangedMeshHashesOtherwise()
        {
            var a = Sphere(64, 32);
            var b = Sphere(64, 32);
            Assert.AreEqual(LodMeshes.Hash(LodMeshes.Capture(a)), LodMeshes.Hash(LodMeshes.Capture(b)), "the same mesh, the same hash");
            var v = b.vertices;
            v[7] += Vector3.up * 0.01f;
            b.vertices = v;
            Assert.AreNotEqual(LodMeshes.Hash(LodMeshes.Capture(a)), LodMeshes.Hash(LodMeshes.Capture(b)), "a moved vertex: another hash (an optimized model's levels are made again)");
        }

        [Test]
        public void LayersAndSmallMeshesGetNoLevels()
        {
            Assert.IsFalse(LodMeshes.Eligible(Sphere(64, 32, "ship_000_midorian_lights_add")), "a light layer sits on the hull");
            Assert.IsFalse(LodMeshes.Eligible(Sphere(64, 32, "sn_carrier_terran_1_emissive")), "an emissive layer too");
            Assert.IsFalse(LodMeshes.Eligible(Sphere(16, 8)), "224 triangles: nothing to win");
            Assert.AreEqual(0, LodMeshes.Simplify(LodMeshes.Capture(Sphere(16, 8))).Length);
        }

        [Test]
        public void TheKeyNamesTheMeshByItsCountsAndSize()
        {
            var a = Sphere(64, 32);
            StringAssert.StartsWith("test_hull|11904|11904|1|", LodMeshes.Key(a));
            Assert.AreEqual(LodMeshes.Key(a), LodMeshes.Key(Sphere(64, 32)));
            Assert.AreNotEqual(LodMeshes.Key(a), LodMeshes.Key(Sphere(64, 32, "other")));
        }

        [Test]
        public void TheOptionIsOnByDefault()
        {
            PlayerPrefs.DeleteKey("gof2.lods");
            Settings.Reset(new[] { "lods" });
            Assert.IsTrue(Settings.Lods, "on by default on every platform");
            CollectionAssert.Contains(Settings.GraphicsKeys, "lods", "Default settings resets it with the Graphics tab");
        }
    }
}
