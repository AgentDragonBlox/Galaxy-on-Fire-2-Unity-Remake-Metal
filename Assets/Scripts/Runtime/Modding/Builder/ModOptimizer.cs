// ModOptimizer.cs
// Mod Builder (remake, 2026-10-22, the user's request): makes a project's models and textures smaller. Models are mostly their
// embedded textures: SR2_Normandy.glb (61.7 MB) carries nine 4096 px PNGs and 1.5 MB of geometry. Each embedded PNG / JPG is
// scaled down to the largest size picked (1K / 2K / 4K, halved with a box filter, so it never grows) and stored as a JPEG
// unless it uses its alpha (or the JPEG choice is off: then a PNG). Polygons (optional): every triangle primitive cut to a
// share of its triangles by MeshSimplifier (the same vertices, so the other attributes stay valid), the vertices no triangle
// uses any more dropped when the primitive owns its attributes. The GLB is written again, its bufferViews packed in order
// (ModGlbImages' layout rules: one embedded buffer, no meshopt / Draco / quantized positions for the geometry). The project's
// own image files are scaled down in their own format (a file's name is what the mod's JSON names). A file whose result isn't
// smaller is left as it was.
// The decode is Unity's (Texture2D.LoadImage, main thread: one image per Step); scaling, encoding
// (ImageConversion.EncodeArrayTo*, thread safe), the polygons and the files run on worker threads, two images at most in flight.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GoF2Remake.Modding
{
    public sealed class ModOptimizer
    {
        /// <summary>The texture sizes offered (1K, 2K, 4K).</summary>
        public static readonly int[] Sizes = { 1024, 2048, 4096 };
        /// <summary>The JPEG qualities offered (high, normal, small).</summary>
        public static readonly int[] Qualities = { 92, 85, 70 };
        /// <summary>The shares of the triangles offered (all of them, 75 %, 50 %, 25 %).</summary>
        public static readonly float[] TriangleShares = { 1f, 0.75f, 0.5f, 0.25f };

        public sealed class Options
        {
            public int maxSize = 1024;
            public int quality = 85;
            public bool jpeg = true;          // a GLB's opaque images as JPEG
            public float triangles = 1f;      // the share of the triangles kept
        }

        public sealed class FileResult
        {
            public string path;               // relative to the project
            public long before, after;
            public int trianglesBefore, trianglesAfter;
            public string error;
        }

        const uint Magic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;
        const int InFlight = 2;
        const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

        /// <summary>The project's files the optimizer can make smaller (relative paths, '/' separated): its .glb models and
        /// its image files, not the saved originals (templates/, never exported) or the Mods screen's picture.</summary>
        public static List<string> Candidates(string folder)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(folder.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase) || rel.Equals("preview.png", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".glb" || ImageExtensions.Contains(ext)) list.Add(rel);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>A GLB's triangle count from its JSON chunk alone (the dialog's list), -1 when it can't tell.</summary>
        public static int CountTriangles(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var head = new byte[20];
                    if (f.Read(head, 0, 20) != 20 || BitConverter.ToUInt32(head, 0) != Magic || BitConverter.ToUInt32(head, 16) != ChunkJson) return -1;
                    int len = (int)BitConverter.ToUInt32(head, 12);
                    if (len <= 0 || len > 64 << 20) return -1;
                    var json = new byte[len];
                    if (f.Read(json, 0, len) != len) return -1;
                    return Triangles(JObject.Parse(Encoding.UTF8.GetString(json)));
                }
            }
            catch (Exception) { return -1; }
        }

        static int Triangles(JObject root)
        {
            int count = 0;
            var accessors = root["accessors"] as JArray;
            if (root["meshes"] is JArray meshes)
                foreach (var m in meshes)
                    if (m?["primitives"] is JArray prims)
                        foreach (var p in prims)
                        {
                            if (((int?)p["mode"] ?? 4) != 4) continue;
                            int? acc = (int?)p["indices"] ?? (int?)p["attributes"]?["POSITION"];
                            if (acc != null && accessors != null && acc < accessors.Count) count += ((int?)accessors[acc.Value]?["count"] ?? 0) / 3;
                        }
            return count;
        }

        enum Phase { Loading, Decoding, Writing }

        sealed class Item
        {
            public byte[] src;
            public bool keepFormat, jpgFile;   // a loose image keeps its format
            public Color32[] px;
            public int w, h;
            public byte[] result;              // null: keep the original
            public bool decoded;
            public Task task;
        }

        sealed class Work
        {
            public string rel, path;
            public bool glb;
            public Phase phase;
            public Task task;
            public string error;
            public byte[] bytes;
            public JObject root;
            public int binStart, binLength;
            public readonly Dictionary<int, Item> byView = new Dictionary<int, Item>();   // the images, by their bufferView
            public Item single;                                                         // a loose image file
            public readonly Dictionary<int, byte[]> viewData = new Dictionary<int, byte[]>();   // replaced bufferViews
            public int trianglesBefore, trianglesAfter;
            public long after = -1;
        }

        readonly string folder;
        readonly Options options;
        readonly Queue<string> pending;
        Work work;

        public readonly List<FileResult> Results = new List<FileResult>();
        public int Total { get; }
        public bool Finished { get; private set; }
        /// <summary>The file in progress (relative path), for the status line.</summary>
        public string Current => work?.rel;

        public long Before => Results.Sum(r => r.before);
        public long After => Results.Sum(r => r.after);

        public ModOptimizer(string folder, IEnumerable<string> files, Options options)
        {
            this.folder = folder;
            this.options = options ?? new Options();
            pending = new Queue<string>(files ?? Enumerable.Empty<string>());
            Total = pending.Count;
            if (Total == 0) Finished = true;
        }

        /// <summary>Main thread, once a frame until Finished: starts the next file, decodes one of its images, writes a
        /// file whose images are done.</summary>
        public void Step()
        {
            if (Finished) return;
            if (work == null)
            {
                if (pending.Count == 0) { Finished = true; return; }
                string rel = pending.Dequeue();
                var w = new Work { rel = rel, path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)), phase = Phase.Loading };
                w.glb = rel.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
                w.task = Task.Run(() => Load(w));
                work = w;
                return;
            }
            var wk = work;
            switch (wk.phase)
            {
                case Phase.Loading:
                    if (!wk.task.IsCompleted) return;
                    if (wk.task.IsFaulted) wk.error = Message(wk.task);
                    if (wk.error != null) { Done(wk); return; }
                    wk.phase = Phase.Decoding;
                    return;
                case Phase.Decoding:
                {
                    var items = Items(wk).ToList();
                    if (items.Count(i => i.decoded && !i.task.IsCompleted) < InFlight && items.FirstOrDefault(i => !i.decoded) is Item next)
                    {
                        Decode(next);
                        next.task = next.px != null ? Task.Run(() => Process(next)) : Task.CompletedTask;
                        return;
                    }
                    if (!items.All(i => i.decoded && i.task.IsCompleted)) return;
                    var failed = items.FirstOrDefault(i => i.task.IsFaulted);
                    if (failed != null) { wk.error = Message(failed.task); Done(wk); return; }
                    wk.phase = Phase.Writing;
                    wk.task = Task.Run(() => Write(wk));
                    return;
                }
                default:
                    if (!wk.task.IsCompleted) return;
                    if (wk.task.IsFaulted) wk.error = Message(wk.task);
                    Done(wk);
                    return;
            }
        }

        static string Message(Task t) => t.Exception?.GetBaseException().Message ?? "failed";

        static IEnumerable<Item> Items(Work w)
        {
            if (w.single != null) yield return w.single;
            foreach (var i in w.byView.Values) yield return i;
        }

        void Done(Work w)
        {
            long before = w.bytes?.LongLength ?? 0;
            Results.Add(new FileResult
            {
                path = w.rel, before = before, after = w.error != null || w.after < 0 ? before : w.after, error = w.error,
                trianglesBefore = w.trianglesBefore, trianglesAfter = w.error != null || w.after < 0 ? w.trianglesBefore : w.trianglesAfter
            });
            work = null;
            if (pending.Count == 0) Finished = true;
        }

        // ---- worker steps ---------------------------------------------------------------------------------------

        void Load(Work w)
        {
            w.bytes = File.ReadAllBytes(w.path);
            if (!w.glb)
            {
                string ext = Path.GetExtension(w.path).ToLowerInvariant();
                w.single = new Item { src = w.bytes, keepFormat = true, jpgFile = ext != ".png" };
                return;
            }
            if (!ReadGlb(w)) w.error = "not a model this can optimize (one embedded buffer needed)";
            else w.trianglesBefore = w.trianglesAfter = Triangles(w.root);
        }

        bool ReadGlb(Work w)
        {
            var glb = w.bytes;
            if (glb.Length < 28 || BitConverter.ToUInt32(glb, 0) != Magic || BitConverter.ToUInt32(glb, 4) != 2) return false;
            int end = (int)Math.Min(BitConverter.ToUInt32(glb, 8), (uint)glb.Length);
            int pos = 12, jsonStart = -1, jsonLength = 0;
            w.binStart = -1;
            while (pos + 8 <= end)
            {
                int length = (int)BitConverter.ToUInt32(glb, pos);
                uint type = BitConverter.ToUInt32(glb, pos + 4);
                if (length < 0 || pos + 8 + length > end) return false;
                if (type == ChunkJson && jsonStart < 0) { jsonStart = pos + 8; jsonLength = length; }
                else if (type == ChunkBin && w.binStart < 0) { w.binStart = pos + 8; w.binLength = length; }
                pos += 8 + length;
            }
            if (jsonStart < 0 || w.binStart < 0) return false;
            var root = JObject.Parse(Encoding.UTF8.GetString(glb, jsonStart, jsonLength));
            var views = root["bufferViews"] as JArray;
            var buffers = root["buffers"] as JArray;
            if (views == null || buffers == null || buffers.Count != 1 || buffers[0]["uri"] != null) return false;
            if (Mentions(root, "meshopt")) return false;
            w.root = root;
            if (!(root["images"] is JArray images)) return true;
            // A bufferView anything but an image reads (accessors, Draco...) is never replaced by an image.
            var used = new HashSet<int>();
            foreach (var p in root.Properties()) if (p.Name != "images") CollectViews(p.Value, used);
            for (int i = 0; i < images.Count; i++)
            {
                if (!(images[i] is JObject img) || img["uri"] != null || !(img["bufferView"] is JValue bv) || bv.Type != JTokenType.Integer) continue;
                int v = (int)bv;
                if (v < 0 || v >= views.Count || used.Contains(v) || w.byView.ContainsKey(v) || !(views[v] is JObject view) || (int?)view["buffer"] != 0) continue;
                int off = (int?)view["byteOffset"] ?? 0, len = (int?)view["byteLength"] ?? 0;
                if (off < 0 || len <= 8 || off + len > w.binLength) continue;
                int at = w.binStart + off;
                bool png = glb[at] == 0x89 && glb[at + 1] == (byte)'P' && glb[at + 2] == (byte)'N' && glb[at + 3] == (byte)'G';
                bool jpg = glb[at] == 0xFF && glb[at + 1] == 0xD8;
                if (!png && !jpg) continue;   // KTX, WebP...: left as they are
                var src = new byte[len];
                Buffer.BlockCopy(glb, at, src, 0, len);
                w.byView[v] = new Item { src = src };
            }
            return true;
        }

        /// <summary>Main thread: the image's pixels (Unity's decoder).</summary>
        static void Decode(Item item)
        {
            item.decoded = true;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(item.src, false)) return;
                item.px = tex.GetPixels32();
                item.w = tex.width;
                item.h = tex.height;
            }
            finally
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                else UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        void Process(Item item)
        {
            var px = item.px;
            int w = item.w, h = item.h;
            while (w > options.maxSize || h > options.maxSize) px = Halve(px, ref w, ref h);
            item.px = null;
            bool jpeg = item.keepFormat ? item.jpgFile : options.jpeg && !UsesAlpha(px);
            var bytes = jpeg
                ? ImageConversion.EncodeArrayToJPG(px, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, options.quality)
                : ImageConversion.EncodeArrayToPNG(px, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
            item.result = bytes != null && bytes.Length > 0 && bytes.Length < item.src.Length ? bytes : null;
        }

        void Write(Work w)
        {
            byte[] output;
            if (!w.glb) output = w.single?.result;
            else
            {
                bool changed = false;
                foreach (var kv in w.byView)
                    if (kv.Value.result != null) { w.viewData[kv.Key] = kv.Value.result; changed = true; }
                if (ReduceGeometry(w)) changed = true;
                output = changed ? Rebuild(w) : null;
            }
            if (output == null || output.Length >= w.bytes.Length) return;   // nothing smaller: the file stays
            string tmp = w.path + ".optimizing";
            File.WriteAllBytes(tmp, output);
            File.Delete(w.path);
            File.Move(tmp, w.path);
            w.after = output.LongLength;
        }

        // ---- polygons ---------------------------------------------------------------------------------------------

        sealed class Accessor
        {
            public JObject json;
            public int index, view, offset, count, comps, compSize, compType, stride;
            public int ElementSize => comps * compSize;
        }

        Accessor ReadAccessor(Work w, int index)
        {
            var accessors = w.root["accessors"] as JArray;
            var views = (JArray)w.root["bufferViews"];
            if (accessors == null || index < 0 || index >= accessors.Count || !(accessors[index] is JObject a)) return null;
            if (a["sparse"] != null || !(a["bufferView"] is JValue bv) || bv.Type != JTokenType.Integer) return null;
            int v = (int)bv;
            if (v < 0 || v >= views.Count || (int?)views[v]?["buffer"] != 0) return null;
            int compType = (int?)a["componentType"] ?? 0;
            int compSize = compType == 5126 || compType == 5125 ? 4 : compType == 5123 || compType == 5122 ? 2 : compType == 5121 || compType == 5120 ? 1 : 0;
            int comps = Comps((string)a["type"]);
            if (compSize == 0 || comps == 0) return null;
            var acc = new Accessor
            {
                json = a, index = index, view = v, compType = compType, compSize = compSize, comps = comps, count = (int?)a["count"] ?? 0,
                offset = w.binStart + ((int?)views[v]["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0)
            };
            acc.stride = (int?)views[v]["byteStride"] ?? acc.ElementSize;
            if (acc.count <= 0 || acc.offset + (long)(acc.count - 1) * acc.stride + acc.ElementSize > w.binStart + w.binLength) return null;
            return acc;
        }

        static int Comps(string type) =>
            type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : type == "MAT2" ? 4 : type == "MAT3" ? 9 : type == "MAT4" ? 16 : 0;

        /// <summary>Every indexed triangle primitive with float positions: its identical vertices joined (Weld) and cut to the
        /// options' share of its triangles; its unused vertices dropped when it owns its attributes. False when nothing
        /// changed.</summary>
        bool ReduceGeometry(Work w)
        {
            if (Mentions(w.root, "draco") || Mentions(w.root, "quantization")) return false;
            if (!(w.root["meshes"] is JArray meshes)) return false;
            // How many primitives read each accessor: a shared one is never compacted.
            var users = new Dictionary<int, int>();
            void Use(JToken t) { if (t is JValue v && v.Type == JTokenType.Integer) { users.TryGetValue((int)v, out int c); users[(int)v] = c + 1; } }
            foreach (var m in meshes)
                if (m?["primitives"] is JArray prims)
                    foreach (var p in prims)
                    {
                        if (p["attributes"] is JObject attrs) foreach (var a in attrs.Properties()) Use(a.Value);
                        if (p["targets"] is JArray targets) foreach (var t in targets.OfType<JObject>()) foreach (var a in t.Properties()) Use(a.Value);
                        Use(p["indices"]);
                    }
            bool changed = false;
            int total = 0;
            foreach (var m in meshes)
                if (m?["primitives"] is JArray prims)
                    foreach (var p in prims.OfType<JObject>())
                    {
                        int before = 0, after = ReducePrimitive(w, p, users, ref before);
                        if (after >= 0) changed = true;   // -1: left alone; else welded and / or cut
                        total += after >= 0 ? after : before;
                    }
            if (!changed) return false;
            w.trianglesAfter = total;
            DropUnusedViews(w);
            return true;
        }

        /// <summary>The primitive's triangle count after reducing it (its count before in 'before'), -1 when it is left alone.</summary>
        int ReducePrimitive(Work w, JObject prim, Dictionary<int, int> users, ref int before)
        {
            if (((int?)prim["mode"] ?? 4) != 4 || prim["targets"] != null || !(prim["attributes"] is JObject attrs)) return -1;
            var posAcc = ReadAccessor(w, (int?)attrs["POSITION"] ?? -1);
            var idxAcc = ReadAccessor(w, (int?)prim["indices"] ?? -1);
            if (posAcc == null || idxAcc == null || posAcc.compType != 5126 || posAcc.comps != 3 || idxAcc.comps != 1) return -1;
            before = idxAcc.count / 3;
            if (before < 64) return -1;
            var bytes = w.bytes;
            var positions = new Vector3[posAcc.count];
            for (int i = 0; i < posAcc.count; i++)
            {
                int o = posAcc.offset + i * posAcc.stride;
                positions[i] = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            }
            var indices = new int[idxAcc.count];
            for (int i = 0; i < idxAcc.count; i++)
            {
                int o = idxAcc.offset + i * idxAcc.stride;
                indices[i] = idxAcc.compType == 5125 ? (int)BitConverter.ToUInt32(bytes, o) : idxAcc.compType == 5123 ? BitConverter.ToUInt16(bytes, o) : bytes[o];
            }
            var attrAccs = new List<(string name, Accessor acc)>();
            bool own = true, readable = true;
            foreach (var a in attrs.Properties())
            {
                var acc = ReadAccessor(w, (int?)a.Value ?? -1);
                if (acc == null || acc.count != posAcc.count) { own = false; readable = false; break; }
                users.TryGetValue(acc.index, out int u);
                if (u != 1) own = false;
                attrAccs.Add((a.Name, acc));
            }
            // Vertices alike in every attribute joined first: a model written with three vertices per triangle (the
            // game's .aem files, many exports) is otherwise all seams to the simplifier, which then removes nothing.
            bool welded = readable && Weld(bytes, attrAccs.Select(a => a.acc).ToList(), indices);
            var reduced = options.triangles < 1f ? MeshSimplifier.Simplify(positions, indices, options.triangles) : (int[])indices.Clone();
            if (reduced.Length >= idxAcc.count && !(welded && own)) return -1;

            // The vertices still used, in their old order, when every attribute is this primitive's alone.
            int[] remap = null;
            int kept = posAcc.count;
            if (own)
            {
                remap = new int[posAcc.count];
                for (int i = 0; i < remap.Length; i++) remap[i] = -1;
                kept = 0;
                var order = reduced.Distinct().OrderBy(i => i);
                foreach (int i in order) remap[i] = kept++;
                for (int i = 0; i < reduced.Length; i++) reduced[i] = remap[reduced[i]];
                foreach (var (name, acc) in attrAccs)
                {
                    int es = acc.ElementSize;
                    var data = new byte[kept * es];
                    for (int i = 0; i < remap.Length; i++)
                        if (remap[i] >= 0) Buffer.BlockCopy(bytes, acc.offset + i * acc.stride, data, remap[i] * es, es);
                    PutAccessor(w, acc, data, kept, ArrayBuffer);
                    if (name == "POSITION") SetBounds(acc.json, data, kept);
                    else { acc.json.Remove("min"); acc.json.Remove("max"); }
                }
            }
            int max = reduced.Length == 0 ? 0 : reduced.Max();
            bool wide = max > 65534;
            var idx = new byte[reduced.Length * (wide ? 4 : 2)];
            for (int i = 0; i < reduced.Length; i++)
                if (wide) Buffer.BlockCopy(BitConverter.GetBytes((uint)reduced[i]), 0, idx, i * 4, 4);
                else Buffer.BlockCopy(BitConverter.GetBytes((ushort)reduced[i]), 0, idx, i * 2, 2);
            idxAcc.json["componentType"] = wide ? 5125 : 5123;
            idxAcc.json.Remove("min");
            idxAcc.json.Remove("max");
            PutAccessor(w, idxAcc, idx, reduced.Length, ElementArrayBuffer, users.TryGetValue(idxAcc.index, out int iu) && iu == 1);
            return reduced.Length / 3;
        }

        /// <summary>Points every index at the first vertex whose every attribute has the same bytes; true when any moved.</summary>
        static bool Weld(byte[] bytes, List<Accessor> accs, int[] indices)
        {
            int count = accs[0].count, size = accs.Sum(a => a.ElementSize);
            var first = new Dictionary<VertexKey, int>(count);
            var canon = new int[count];
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                var key = new byte[size];
                int k = 0;
                foreach (var a in accs) { Buffer.BlockCopy(bytes, a.offset + i * a.stride, key, k, a.ElementSize); k += a.ElementSize; }
                var vk = new VertexKey(key);
                if (first.TryGetValue(vk, out int f)) { canon[i] = f; any = true; }
                else { first[vk] = i; canon[i] = i; }
            }
            if (!any) return false;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] >= 0 && indices[i] < count) indices[i] = canon[indices[i]];
            return true;
        }

        readonly struct VertexKey : IEquatable<VertexKey>
        {
            readonly byte[] data;
            readonly int hash;
            public VertexKey(byte[] data)
            {
                this.data = data;
                unchecked { int h = (int)2166136261; foreach (byte b in data) h = (h ^ b) * 16777619; hash = h; }
            }
            public bool Equals(VertexKey o) => hash == o.hash && data.AsSpan().SequenceEqual(o.data);
            public override bool Equals(object o) => o is VertexKey v && Equals(v);
            public override int GetHashCode() => hash;
        }

        /// <summary>The accessor's new data in a bufferView: its own view rewritten when nothing else reads that view, else a new
        /// view at the end.</summary>
        void PutAccessor(Work w, Accessor acc, byte[] data, int count, int target, bool own = true)
        {
            var views = (JArray)w.root["bufferViews"];
            int readers = 0;
            if (w.root["accessors"] is JArray accessors)
                foreach (var a in accessors) if ((int?)a?["bufferView"] == acc.view) readers++;
            if (own && readers == 1 && !w.byView.ContainsKey(acc.view))
            {
                var view = (JObject)views[acc.view];
                view["byteLength"] = data.Length;
                view.Remove("byteStride");
                w.viewData[acc.view] = data;
            }
            else
            {
                var view = new JObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = data.Length, ["target"] = target };
                views.Add(view);
                acc.json["bufferView"] = views.Count - 1;
                w.viewData[views.Count - 1] = data;
            }
            acc.json.Remove("byteOffset");
            acc.json["count"] = count;
        }

        static void SetBounds(JObject acc, byte[] data, int count)
        {
            if (count == 0) return;
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 3; c++)
                {
                    float v = BitConverter.ToSingle(data, i * 12 + c * 4);
                    if (v < min[c]) min[c] = v;
                    if (v > max[c]) max[c] = v;
                }
            acc["min"] = new JArray(min[0], min[1], min[2]);
            acc["max"] = new JArray(max[0], max[1], max[2]);
        }

        /// <summary>Views no accessor, image or extension reads any more (an accessor moved to a new one) shrink to 4 bytes.</summary>
        static void DropUnusedViews(Work w)
        {
            var used = new HashSet<int>();
            foreach (var p in w.root.Properties()) if (p.Name != "bufferViews") CollectViews(p.Value, used);
            var views = (JArray)w.root["bufferViews"];
            for (int v = 0; v < views.Count; v++)
                if (!used.Contains(v) && views[v] is JObject view && (int?)view["buffer"] == 0)
                {
                    view["byteLength"] = 4;
                    view.Remove("byteStride");
                    w.viewData[v] = new byte[4];
                }
        }

        /// <summary>The GLB with its replaced bufferViews: every view packed in order (16-byte aligned), an image's mimeType
        /// following its new bytes.</summary>
        static byte[] Rebuild(Work w)
        {
            var root = w.root;
            var views = (JArray)root["bufferViews"];
            var bin = new MemoryStream(w.binLength / 2 + 64);
            for (int v = 0; v < views.Count; v++)
            {
                if (!(views[v] is JObject view) || (int?)view["buffer"] != 0) continue;
                int off = (int?)view["byteOffset"] ?? 0, len = (int?)view["byteLength"] ?? 0;
                Pad(bin, 16, 0);
                view["byteOffset"] = (int)bin.Length;
                if (w.viewData.TryGetValue(v, out var data))
                {
                    bin.Write(data, 0, data.Length);
                    view["byteLength"] = data.Length;
                }
                else bin.Write(w.bytes, w.binStart + off, len);
            }
            Pad(bin, 4, 0);
            root["buffers"][0]["byteLength"] = (int)bin.Length;
            if (root["images"] is JArray images)
                foreach (var img in images.OfType<JObject>())
                    if (img["bufferView"] is JValue bv && bv.Type == JTokenType.Integer && w.byView.TryGetValue((int)bv, out var it) && it.result != null)
                        img["mimeType"] = it.result[0] == 0xFF ? "image/jpeg" : "image/png";

            var json = new MemoryStream();
            var text = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            json.Write(text, 0, text.Length);
            Pad(json, 4, 0x20);
            var o = new MemoryStream(28 + (int)json.Length + (int)bin.Length);
            void U32(long x) { var b = BitConverter.GetBytes((uint)x); o.Write(b, 0, 4); }
            U32(Magic); U32(2); U32(12 + 8 + json.Length + 8 + bin.Length);
            U32(json.Length); U32(ChunkJson); json.WriteTo(o);
            U32(bin.Length); U32(ChunkBin); bin.WriteTo(o);
            return o.ToArray();
        }

        // ---- pixels -----------------------------------------------------------------------------------------------

        /// <summary>Half the size each way (an odd last row / column folded into its neighbour), each pixel the mean of its
        /// 2 x 2 block.</summary>
        public static Color32[] Halve(Color32[] src, ref int w, ref int h)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var dst = new Color32[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    Color32 a = src[y0 * w + x0], b = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                    dst[y * nw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                                                  (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                }
            }
            w = nw;
            h = nh;
            return dst;
        }

        public static bool UsesAlpha(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++) if (px[i].a < 255) return true;
            return false;
        }

        static void CollectViews(JToken t, HashSet<int> into)
        {
            if (t is JObject o)
                foreach (var p in o.Properties())
                {
                    if (p.Name == "bufferView" && p.Value is JValue v && v.Type == JTokenType.Integer) into.Add((int)v);
                    else CollectViews(p.Value, into);
                }
            else if (t is JArray a)
                foreach (var e in a) CollectViews(e, into);
        }

        static bool Mentions(JObject root, string text)
        {
            foreach (var key in new[] { "extensionsUsed", "extensionsRequired" })
                if (root[key] is JArray a)
                    foreach (var e in a)
                        if (((string)e)?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void Pad(MemoryStream s, int to, byte with)
        {
            while (s.Length % to != 0) s.WriteByte(with);
        }
    }
}
