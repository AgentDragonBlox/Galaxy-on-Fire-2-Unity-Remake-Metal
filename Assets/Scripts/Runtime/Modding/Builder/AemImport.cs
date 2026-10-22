// AemImport.cs
// Remake (the user's request, 2026-10-22: the community's GoF2 mods are the game's own .aem models and .aei textures): reads
// an .aem (the game's "AEMesh") and writes it as a .glb the Mod Builder and the mods' loaders take. The reader is the C# of
// Reference/tools/asset_conversion/aem.py (AEMesh = 16-bit positions, V2 / V3 32-bit, V4 / V5 floats; flags 0x10 indexed,
// 2 UVs, 4 normals, 8 colours; V3+ a pivot, an animation block and children per part). Every part's vertices are already in
// the model's own space (aem2fbx.py leaves the pivots to the animations), so the parts are joined into one mesh in their
// rest pose; animations are dropped. Axes: the engine's (x, y, z) is the glTF's as it is (glTFast turns glTF into Unity by
// x mirrored, the same as the remake's ship-relative (-x, y, z)), in metres (x 0.05); V flipped (the game samples memory row
// 0 at v = 0, glTF's v = 0 is the top). Plain C#, any thread.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public static class AemImport
    {
        public const float MetresPerUnit = 0.05f;

        /// <summary>The model joined into one triangle list (game units).</summary>
        public sealed class Model
        {
            public readonly List<Vector3> positions = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> indices = new List<int>();
            public bool hasNormals = true, hasUvs = true;
            public int parts;
            public Bounds Bounds
            {
                get
                {
                    if (positions.Count == 0) return default;
                    var b = new Bounds(positions[0], Vector3.zero);
                    foreach (var p in positions) b.Encapsulate(p);
                    return b;
                }
            }
            /// <summary>Its length along the nose (z), game units: a ship's modelLength.</summary>
            public float Length => Bounds.size.z;
            public int Triangles => indices.Count / 3;

            /// <summary>A copy with its identical vertices (position, normal and UV alike) joined: the game's files often
            /// give every triangle its own three, which made the .glb five times as large and left nothing for the
            /// Mod Builder's polygon reduction to remove.</summary>
            public Model Welded()
            {
                var o = new Model { hasNormals = hasNormals, hasUvs = hasUvs, parts = parts };
                var first = new Dictionary<(Vector3, Vector3, Vector2), int>(positions.Count);
                var map = new int[positions.Count];
                for (int i = 0; i < positions.Count; i++)
                {
                    var key = (positions[i], hasNormals ? normals[i] : default, hasUvs ? uvs[i] : default);
                    if (!first.TryGetValue(key, out int f))
                    {
                        f = o.positions.Count;
                        first[key] = f;
                        o.positions.Add(positions[i]);
                        if (hasNormals) o.normals.Add(normals[i]);
                        if (hasUvs) o.uvs.Add(uvs[i]);
                    }
                    map[i] = f;
                }
                foreach (int i in indices) o.indices.Add(map[i]);
                return o;
            }
        }

        /// <summary>The file starts like an .aem.</summary>
        public static bool Is(byte[] head) =>
            head != null && head.Length >= 6 && head[0] == 'A' && head[1] == 'E' && head[2] == 'M' && head[3] == 'e' && head[4] == 's' && head[5] == 'h'
            || head != null && head.Length >= 7 && head[0] == 'V' && head[2] == 'A' && head[3] == 'E' && head[4] == 'M' && head[5] == 'e' && head[6] == 's';

        const int V2 = 1, V3 = 2, AE = 4, V4 = 8, V5 = 16;

        /// <summary>Reads an .aem; null with 'error' when it isn't one or ends early.</summary>
        public static Model Read(byte[] b, out string error)
        {
            error = null;
            try
            {
                var r = new Reader(b);
                int ver = Version(b);
                if (ver == 0) { error = "Not an .aem model."; return null; }
                r.Skip(7);
                if ((ver & (V2 | V3 | V4 | V5)) != 0) r.Skip(2);
                int flags = r.U8();
                var model = new Model();
                int top = (ver & (V3 | V4 | V5)) != 0 ? Math.Max(1, (int)r.U16()) : 1;
                for (int i = 0; i < top; i++)
                {
                    try { Part(r, ver, flags, model); }
                    catch (EndOfStreamException) { if (model.positions.Count == 0) throw; break; }   // a truncated file: what was read
                }
                if (model.indices.Count == 0) { error = "The model has no triangles."; return null; }
                return model;
            }
            catch (EndOfStreamException) { error = "The .aem ends early (damaged or not a model)."; return null; }
            catch (Exception e) { error = e.Message; return null; }
        }

        static int Version(byte[] b)
        {
            if (b.Length < 7) return 0;
            string h = Encoding.ASCII.GetString(b, 0, 7);
            if (h == "V2AEMes") return V2;
            if (h == "V3AEMes") return V3;
            if (h == "V4AEMes") return V4;
            if (h == "V5AEMes") return V5;
            return h.StartsWith("AEMesh", StringComparison.Ordinal) ? AE : 0;
        }

        static void Part(Reader r, int ver, int flags, Model m)
        {
            bool enhanced = (ver & (V3 | V4 | V5)) != 0;
            bool small = (ver & (AE | V2 | V3)) != 0;
            if (enhanced) r.Skip(12);   // the pivot (the animations')
            int[] idx = null;
            if ((flags & 0x10) != 0)
            {
                int ni = r.U16();
                idx = new int[ni];
                for (int i = 0; i < ni; i++) idx[i] = r.U16();
            }
            int nv = r.U16();
            var pos = new Vector3[nv];
            for (int i = 0; i < nv; i++)
                pos[i] = (ver & AE) != 0 ? new Vector3(r.I16(), r.I16(), r.I16())
                       : (ver & (V2 | V3)) != 0 ? new Vector3(r.I32(), r.I32(), r.I32())
                       : new Vector3(r.F32(), r.F32(), r.F32());
            Vector2[] uv = null;
            if ((flags & 2) != 0)
            {
                uv = new Vector2[nv];
                for (int i = 0; i < nv; i++) uv[i] = small ? new Vector2(r.I16() / 4096f, r.I16() / 4096f) : new Vector2(r.F32(), r.F32());
            }
            Vector3[] nrm = null;
            if ((flags & 4) != 0)
            {
                nrm = new Vector3[nv];
                for (int i = 0; i < nv; i++)
                {
                    var n = small ? new Vector3(r.I16(), r.I16(), r.I16()) : new Vector3(r.F32(), r.F32(), r.F32());
                    nrm[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
                }
            }
            if ((flags & 8) != 0) r.Skip(nv * (small ? 4 : 16));   // vertex colours: not used by the ships' materials

            // Joined: this part's triangles (sequential when it isn't indexed; out-of-range triangles dropped).
            int baseVertex = m.positions.Count;
            m.positions.AddRange(pos);
            if (uv != null) m.uvs.AddRange(uv); else { m.hasUvs = false; for (int i = 0; i < nv; i++) m.uvs.Add(Vector2.zero); }
            if (nrm != null) m.normals.AddRange(nrm); else { m.hasNormals = false; for (int i = 0; i < nv; i++) m.normals.Add(Vector3.up); }
            int count = idx != null ? idx.Length : nv;
            for (int t = 0; t + 2 < count; t += 3)
            {
                int a = idx != null ? idx[t] : t, b = idx != null ? idx[t + 1] : t + 1, c = idx != null ? idx[t + 2] : t + 2;
                if (a >= nv || b >= nv || c >= nv) continue;
                m.indices.Add(baseVertex + a); m.indices.Add(baseVertex + b); m.indices.Add(baseVertex + c);
            }
            m.parts++;

            if (!enhanced) return;
            SkipAnimation(r, ver);
            int children = r.U16();
            for (int i = 0; i < children; i++) Part(r, ver, flags, m);
        }

        static void SkipAnimation(Reader r, int ver)
        {
            r.Skip(16);   // the bounding sphere
            for (int k = 0; k < 3; k++)   // position, rotation, scale
            {
                int mode = r.I16();
                if (mode == 1) { int n = r.U16(); r.Skip(n * 16); }
                else if (mode == 0) for (int c = 0; c < 3; c++) { int n = r.U16(); r.Skip(n * 8); }
            }
            if ((ver & (V4 | V5)) != 0 && r.I16() == 2) { int n = r.U16(); r.Skip(n * 8); }
            if ((ver & V5) != 0)
            {
                int n = r.U16();
                if (n != 0) for (int c = 0; c < 7; c++) { int k = r.I16(); if (k > 0) r.Skip(k * 8); }
            }
        }

        sealed class Reader
        {
            readonly byte[] b;
            int o;
            public Reader(byte[] bytes) { b = bytes; }
            void Need(int n) { if (o + n > b.Length) throw new EndOfStreamException(); }
            public void Skip(int n) { Need(n); o += n; }
            public int U8() { Need(1); return b[o++]; }
            public int U16() { Need(2); int v = b[o] | b[o + 1] << 8; o += 2; return v; }
            public int I16() { Need(2); int v = (short)(b[o] | b[o + 1] << 8); o += 2; return v; }
            public int I32() { Need(4); int v = BitConverter.ToInt32(b, o); o += 4; return v; }
            public float F32() { Need(4); float v = BitConverter.ToSingle(b, o); o += 4; return v; }
        }

        // ---- .glb ---------------------------------------------------------------------------------------------

        /// <summary>The model as a .glb: one mesh, one material ("hull", no textures: the ship's hull textures give them),
        /// positions in metres, normals and UVs when the .aem has them, 32-bit indices.</summary>
        public static byte[] ToGlb(Model m, string name)
        {
            m = m.Welded();
            int nv = m.positions.Count;
            var bin = new MemoryStream();
            var w = new BinaryWriter(bin);
            var views = new JArray();
            var accessors = new JArray();
            int View(int start, int target)
            {
                int length = (int)bin.Length - start;
                views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = length, ["target"] = target });
                while (bin.Length % 4 != 0) w.Write((byte)0);
                return views.Count - 1;
            }
            int Accessor(int view, int componentType, int count, string type, JArray min = null, JArray max = null)
            {
                var a = new JObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
                if (min != null) { a["min"] = min; a["max"] = max; }
                accessors.Add(a);
                return accessors.Count - 1;
            }

            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            int start = (int)bin.Length;
            foreach (var p in m.positions)
            {
                var q = p * MetresPerUnit;
                lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
                w.Write(q.x); w.Write(q.y); w.Write(q.z);
            }
            var attributes = new JObject
            {
                ["POSITION"] = Accessor(View(start, 34962), 5126, nv, "VEC3", new JArray(lo.x, lo.y, lo.z), new JArray(hi.x, hi.y, hi.z)),
            };
            if (m.hasNormals)
            {
                start = (int)bin.Length;
                foreach (var n in m.normals) { w.Write(n.x); w.Write(n.y); w.Write(n.z); }
                attributes["NORMAL"] = Accessor(View(start, 34962), 5126, nv, "VEC3");
            }
            if (m.hasUvs)
            {
                start = (int)bin.Length;
                foreach (var t in m.uvs) { w.Write(t.x); w.Write(1f - t.y); }
                attributes["TEXCOORD_0"] = Accessor(View(start, 34962), 5126, nv, "VEC2");
            }
            start = (int)bin.Length;
            foreach (int i in m.indices) w.Write((uint)i);
            int indices = Accessor(View(start, 34963), 5125, m.indices.Count, "SCALAR");

            var gltf = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "GoF2 Remake Mod Builder (.aem import)" },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
                ["nodes"] = new JArray(new JObject { ["name"] = name, ["mesh"] = 0 }),
                ["meshes"] = new JArray(new JObject
                {
                    ["name"] = name,
                    ["primitives"] = new JArray(new JObject { ["attributes"] = attributes, ["indices"] = indices, ["material"] = 0 }),
                }),
                ["materials"] = new JArray(new JObject
                {
                    ["name"] = "hull",
                    ["pbrMetallicRoughness"] = new JObject { ["baseColorFactor"] = new JArray(1, 1, 1, 1), ["metallicFactor"] = 0, ["roughnessFactor"] = 0.6 },
                }),
                ["accessors"] = accessors,
                ["bufferViews"] = views,
                ["buffers"] = new JArray(new JObject { ["byteLength"] = bin.Length }),
            };
            return Pack(gltf.ToString(Formatting.None), bin.ToArray());
        }

        static byte[] Pack(string json, byte[] bin)
        {
            var js = Encoding.UTF8.GetBytes(json);
            int jsLen = (js.Length + 3) & ~3, binLen = (bin.Length + 3) & ~3;
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsLen + 8 + binLen));
            w.Write((uint)jsLen); w.Write(0x4E4F534Au); w.Write(js); for (int i = js.Length; i < jsLen; i++) w.Write((byte)' ');
            w.Write((uint)binLen); w.Write(0x004E4942u); w.Write(bin); for (int i = bin.Length; i < binLen; i++) w.Write((byte)0);
            return o.ToArray();
        }
    }
}
