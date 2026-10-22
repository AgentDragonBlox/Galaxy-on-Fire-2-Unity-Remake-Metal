// LodMeshesTests.cs
// Feature: CLAUDE.md "Options" > Levels of detail (AutoLods, 2026-10-22): a mesh's lower levels are cut by the Mod Builder
// optimizer's simplifier after its identical vertices are joined (a model with three vertices per triangle is cut like a
// shared one), level 1 to about half the triangles and level 2 to about a fifth; a level is a mesh of its own with only
// the vertices it uses; the levels go through a file as indices and come back the same; a changed mesh hashes otherwise;
// light / glow layers and small meshes get none; the option is on by default on phones only.
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
        public void TheOptionIsOnByDefaultOnPhonesOnly()
        {
            PlayerPrefs.DeleteKey("gof2.lods");
            Settings.Reset(new[] { "lods" });
            Assert.AreEqual(Application.isMobilePlatform, Settings.Lods);
            CollectionAssert.Contains(Settings.GraphicsKeys, "lods", "Default settings resets it with the Graphics tab");
        }
    }
}
