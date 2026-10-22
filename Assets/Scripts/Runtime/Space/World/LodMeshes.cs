// LodMeshes.cs
// Remake (the user's request 2026-10-22, "Levels of detail" in Options > Graphics, AutoLods): the lower levels of detail of a
// mesh, made by the Mod Builder optimizer's polygon reduction (MeshSimplifier: quadric edge collapse over the mesh's own
// vertices; seams and open borders collapsed only along themselves, each side keeping its own vertices: the game's models
// split nearly every point by hard edges and UV seams and are built of open panels, and with those locked 142 of their 147
// hull meshes got no level). One mesh in, its levels out:
//   Capture   (main thread) the mesh's vertices as raw bytes (every stream), its positions and its triangles per submesh;
//   Simplify  (any thread) vertices alike in every byte joined first (a model with three vertices per triangle is otherwise
//             all seams to the simplifier), then level 1 at Ratios[0] of the triangles, level 2 cut from level 1 to Ratios[1];
//             a level that keeps more than MinGain of the one above isn't worth a switch: the chain stops there;
//   Build     (main thread) a level as a mesh of its own: only the vertices its triangles use, copied byte for byte from
//             the source's streams (every attribute: UVs, normals, tangents, colours), 16-bit indices when they fit; the
//             CPU copy is dropped (UploadMeshData), so HullCollision (readable LOD 0 meshes only) never takes it.
// Levels are kept as triangle lists over the SOURCE mesh's vertex numbers, so a file of them (GameLods' prebaked library,
// ModLodCache's per-mod cache) is only indices: Write / Read. Key names a game mesh without its asset path (name, counts,
// bounds): the same at bake time in the Editor and at run time.

using System;
using System.Collections.Generic;
using System.IO;
using GoF2Remake.Modding;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoF2Remake.Visuals
{
    public static class LodMeshes
    {
        /// <summary>The share of LOD 0's triangles each lower level keeps (level 1, level 2).</summary>
        public static readonly float[] Ratios = { 0.5f, 0.2f };
        /// <summary>A level keeping more than this share of the level above is dropped (and the levels below it).</summary>
        public const float MinGain = 0.85f;
        /// <summary>Meshes with fewer triangles get no levels: nothing to win.</summary>
        public const int MinTriangles = 1000;
        /// <summary>The same for the hulls whose levels replace the original's copied LOD meshes (AutoLods.CopiedLevels):
        /// the asteroids (538-1369 triangles) are many to a field.</summary>
        public const int MinTrianglesCopied = 500;
        /// <summary>Bumped when Ratios / MinGain / the simplifier change: the mods' cached levels are made again.</summary>
        public const int ParamsVersion = 4;

        /// <summary>A mesh read for the simplifier (plain data, any thread).</summary>
        public sealed class Captured
        {
            public string name;
            public int vertexCount;
            public Vector3[] positions;
            public Vector2[] uvs;      // texture coordinates 0 (null without): the last level's loose seam cut picks by them
            public byte[] vertices;    // every vertex's bytes from every stream, 'stride' each
            public int stride;
            public int[][] submeshes;  // triangles, absolute vertex numbers
        }

        /// <summary>The triangles of 'mesh' (every submesh), from the index counts (no read access needed).</summary>
        public static int Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long n = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) n += mesh.GetIndexCount(s);
            return (int)(n / 3);
        }

        /// <summary>A mesh worth levels: big enough, not an additive / light / glow / emissive layer or a lower LOD of the
        /// game's (those sit on the hull: cut apart from it they would float off or sink in), triangles only, no blend shapes.</summary>
        public static bool Eligible(Mesh mesh, int minTriangles = MinTriangles)
        {
            if (mesh == null || mesh.vertexCount == 0 || mesh.blendShapeCount > 0) return false;
            string n = mesh.name.ToLowerInvariant();
            if (n.Contains("_add") || n.Contains("_lights") || n.Contains("_lod_") || n.Contains("glow") || n.Contains("emissive")) return false;
            for (int s = 0; s < mesh.subMeshCount; s++) if (mesh.GetTopology(s) != MeshTopology.Triangles) return false;
            return Triangles(mesh) >= minTriangles;
        }

        /// <summary>A game mesh's name in the prebaked library: its name, vertex and index counts, submeshes and size (cm).</summary>
        public static string Key(Mesh mesh)
        {
            var s = mesh.bounds.size;
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return $"{mesh.name}|{mesh.vertexCount}|{indices}|{mesh.subMeshCount}|{Mathf.RoundToInt(s.x * 100f)}|{Mathf.RoundToInt(s.y * 100f)}|{Mathf.RoundToInt(s.z * 100f)}";
        }

        /// <summary>Reads 'mesh' (main thread). Null when it can't be read (a player build's non-readable mesh), isn't all
        /// triangles or is empty.</summary>
        public static Captured Capture(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0) return null;
            if (!mesh.isReadable && !Application.isEditor) return null;   // the Editor reads every mesh through MeshData
            for (int s = 0; s < mesh.subMeshCount; s++) if (mesh.GetTopology(s) != MeshTopology.Triangles) return null;
            using var data = Mesh.AcquireReadOnlyMeshData(mesh);
            var d = data[0];
            int n = d.vertexCount;
            var c = new Captured { name = mesh.name, vertexCount = n, positions = new Vector3[n], submeshes = new int[d.subMeshCount][] };
            using (var p = new NativeArray<Vector3>(n, Allocator.Temp)) { d.GetVertices(p); p.CopyTo(c.positions); }
            if (d.HasVertexAttribute(VertexAttribute.TexCoord0) && d.GetVertexAttributeDimension(VertexAttribute.TexCoord0) >= 2)
                using (var uv = new NativeArray<Vector2>(n, Allocator.Temp)) { d.GetUVs(0, uv); c.uvs = uv.ToArray(); }
            int streams = d.vertexBufferCount;
            var strides = new int[streams];
            for (int s = 0; s < streams; s++) { strides[s] = d.GetVertexBufferStride(s); c.stride += strides[s]; }
            c.vertices = new byte[n * c.stride];
            int offset = 0;
            for (int s = 0; s < streams; s++)
            {
                int st = strides[s];
                if (st == 0) continue;
                var bytes = d.GetVertexData<byte>(s).ToArray();
                for (int v = 0; v < n; v++) Buffer.BlockCopy(bytes, v * st, c.vertices, v * c.stride + offset, st);
                offset += st;
            }
            for (int s = 0; s < d.subMeshCount; s++)
            {
                var sd = d.GetSubMesh(s);
                using var idx = new NativeArray<int>(sd.indexCount, Allocator.Temp);
                d.GetIndices(idx, s, true);
                c.submeshes[s] = idx.ToArray();
            }
            return c;
        }

        /// <summary>The lower levels of a captured mesh (any thread): [level][submesh] triangle lists over its vertex numbers;
        /// empty when no level is worth it.</summary>
        public static int[][][] Simplify(Captured c, int minTriangles = MinTriangles)
        {
            if (c == null || c.submeshes == null) return Array.Empty<int[][]>();
            var canonical = Weld(c);
            var cur = new int[c.submeshes.Length][];
            for (int s = 0; s < cur.Length; s++)
            {
                var src = c.submeshes[s];
                var dst = new int[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = canonical[src[i]];
                cur[s] = dst;
            }
            int curTris = Count(cur);
            var levels = new List<int[][]>();
            if (curTris < minTriangles) return levels.ToArray();
            float prev = 1f;
            for (int l = 0; l < Ratios.Length; l++)
            {
                float ratio = Ratios[l];
                bool last = l == Ratios.Length - 1;   // a few percent of the screen high: seams may smear (MeshSimplifier loose)
                var next = new int[cur.Length][];
                for (int s = 0; s < cur.Length; s++)
                    next[s] = cur[s].Length >= 12 ? MeshSimplifier.Simplify(c.positions, cur[s], ratio / prev, seams: true, borders: true, loose: last, uvs: c.uvs) : (int[])cur[s].Clone();
                int tris = Count(next);
                if (tris > curTris * MinGain) break;   // the seams and edges held it: not worth a level
                levels.Add(next);
                cur = next;
                curTris = tris;
                prev = ratio;
            }
            return levels.ToArray();
        }

        static int Count(int[][] subs) { int n = 0; foreach (var s in subs) n += s.Length / 3; return n; }

        /// <summary>Each vertex's first twin alike in every byte (itself when it has none).</summary>
        static int[] Weld(Captured c)
        {
            var canonical = new int[c.vertexCount];
            var first = new Dictionary<int, int>(c.vertexCount, new SameBytes(c.vertices, c.stride));
            for (int v = 0; v < c.vertexCount; v++)
            {
                if (first.TryGetValue(v, out int f)) canonical[v] = f;
                else { first[v] = v; canonical[v] = v; }
            }
            return canonical;
        }

        sealed class SameBytes : IEqualityComparer<int>
        {
            readonly byte[] data;
            readonly int stride;
            public SameBytes(byte[] data, int stride) { this.data = data; this.stride = stride; }
            public bool Equals(int a, int b) =>
                a == b || new ReadOnlySpan<byte>(data, a * stride, stride).SequenceEqual(new ReadOnlySpan<byte>(data, b * stride, stride));
            public int GetHashCode(int v)
            {
                unchecked
                {
                    uint h = 2166136261;
                    for (int i = v * stride, end = i + stride; i < end; i++) h = (h ^ data[i]) * 16777619;
                    return (int)h;
                }
            }
        }

        /// <summary>The captured mesh's identity (FNV-1a 64 over its vertex bytes, triangles and ParamsVersion): a changed
        /// model (a mod optimized again, undone, replaced) is another hash.</summary>
        public static ulong Hash(Captured c)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(byte b) { h = (h ^ b) * 1099511628211UL; }
                void MixInt(int x) { Mix((byte)x); Mix((byte)(x >> 8)); Mix((byte)(x >> 16)); Mix((byte)(x >> 24)); }
                MixInt(ParamsVersion);
                MixInt(c.vertexCount);
                MixInt(c.stride);
                foreach (byte b in c.vertices) Mix(b);
                foreach (var s in c.submeshes) { MixInt(s.Length); foreach (int i in s) MixInt(i); }
                return h;
            }
        }

        /// <summary>One level as a mesh of its own (main thread): the vertices its triangles use, in their old order, every
        /// stream copied byte for byte. Null when it can't be made (the source unreadable, no triangles).</summary>
        public static Mesh Build(Mesh src, int[][] submeshes, string name)
        {
            if (src == null || submeshes == null || submeshes.Length != src.subMeshCount) return null;
            if (!src.isReadable && !Application.isEditor) return null;
            int n = src.vertexCount;
            var used = new bool[n];
            int total = 0;
            foreach (var s in submeshes)
            {
                total += s.Length;
                foreach (int i in s)
                {
                    if (i < 0 || i >= n) return null;
                    used[i] = true;
                }
            }
            if (total == 0) return null;
            var remap = new int[n];   // the used vertices numbered 0..kept-1 in their old order, -1 the others
            int kept = 0;
            for (int i = 0; i < n; i++) remap[i] = used[i] ? kept++ : -1;

            var attributes = src.GetVertexAttributes();
            using var srcData = Mesh.AcquireReadOnlyMeshData(src);
            var d = srcData[0];
            var outData = Mesh.AllocateWritableMeshData(1);
            bool applied = false;
            try
            {
                var w = outData[0];
                w.SetVertexBufferParams(kept, attributes);
                for (int s = 0; s < d.vertexBufferCount; s++)
                {
                    int st = d.GetVertexBufferStride(s);
                    if (st == 0) continue;
                    if (w.GetVertexBufferStride(s) != st) return null;   // another layout: never expected, never guessed
                    var sv = d.GetVertexData<byte>(s);
                    var dv = w.GetVertexData<byte>(s);
                    for (int i = 0; i < n; i++)
                        if (remap[i] >= 0) NativeArray<byte>.Copy(sv, i * st, dv, remap[i] * st, st);
                }
                bool wide = kept > 65535;
                w.SetIndexBufferParams(total, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
                int o = 0;
                if (wide)
                {
                    var ib = w.GetIndexData<int>();
                    foreach (var s in submeshes) foreach (int i in s) ib[o++] = remap[i];
                }
                else
                {
                    var ib = w.GetIndexData<ushort>();
                    foreach (var s in submeshes) foreach (int i in s) ib[o++] = (ushort)remap[i];
                }
                w.subMeshCount = submeshes.Length;
                int start = 0;
                for (int s = 0; s < submeshes.Length; s++)
                {
                    w.SetSubMesh(s, new SubMeshDescriptor(start, submeshes[s].Length, MeshTopology.Triangles));
                    start += submeshes[s].Length;
                }
                var mesh = new Mesh { name = name };
                Mesh.ApplyAndDisposeWritableMeshData(outData, mesh);
                applied = true;
                mesh.bounds = src.bounds;
                mesh.UploadMeshData(true);   // GPU only: no CPU copy kept, and HullCollision never takes it
                return mesh;
            }
            finally
            {
                if (!applied) outData.Dispose();
            }
        }

        // ---- files of levels (indices only) --------------------------------------------------------------------------

        /// <summary>Levels as [count][per level: submeshes, per submesh: count, wide, the indices (16 or 32 bit)].</summary>
        public static void Write(BinaryWriter w, int[][][] levels)
        {
            w.Write((byte)levels.Length);
            foreach (var level in levels)
            {
                w.Write(level.Length);
                foreach (var s in level)
                {
                    int max = 0;
                    foreach (int i in s) if (i > max) max = i;
                    bool wide = max > 65535;
                    w.Write(s.Length);
                    w.Write(wide);
                    if (wide) foreach (int i in s) w.Write(i);
                    else foreach (int i in s) w.Write((ushort)i);
                }
            }
        }

        public static int[][][] Read(BinaryReader r)
        {
            int count = r.ReadByte();
            var levels = new int[count][][];
            for (int l = 0; l < count; l++)
            {
                int subs = r.ReadInt32();
                if (subs < 0 || subs > 4096) throw new InvalidDataException("bad submesh count");
                levels[l] = new int[subs][];
                for (int s = 0; s < subs; s++)
                {
                    int n = r.ReadInt32();
                    bool wide = r.ReadBoolean();
                    if (n < 0 || n % 3 != 0 || n > (r.BaseStream.Length - r.BaseStream.Position) / (wide ? 4 : 2)) throw new InvalidDataException("bad index count");
                    var idx = new int[n];
                    if (wide) for (int i = 0; i < n; i++) idx[i] = r.ReadInt32();
                    else for (int i = 0; i < n; i++) idx[i] = r.ReadUInt16();
                    levels[l][s] = idx;
                }
            }
            return levels;
        }
    }
}
