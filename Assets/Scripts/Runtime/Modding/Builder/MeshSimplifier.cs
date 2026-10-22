// MeshSimplifier.cs
// The Mod Builder's polygon reduction (ModOptimizer, the user's request 2026-10-22: very high-poly models uploaded as mods):
// quadric error edge collapse (Garland & Heckbert 1997) that only ever collapses a vertex onto a neighbour that already
// exists, so the result is a new triangle list over the same vertices and every other attribute (UVs, normals, tangents,
// colours, skin weights) stays valid as it is. The error quadrics are summed per position, so the copies of one point that a
// UV or normal seam splits share theirs; a vertex with such copies (a seam) and a vertex on an open edge or a non-manifold
// edge is never removed, so textures don't tear and the outline holds. A collapse that would turn a triangle by more than
// 60 degrees (over, or up on its edge) or squash it flat is refused. Plain C#, any thread.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public static class MeshSimplifier
    {
        /// <summary>'indices' (triangles over 'positions') cut to about 'ratio' of the triangles where the seams and edges
        /// allow; returns the new triangle list (the same vertex numbers). Degenerate triangles of the input are dropped.</summary>
        public static int[] Simplify(Vector3[] positions, int[] indices, float ratio)
        {
            int triCount = indices.Length / 3;
            int target = Mathf.Max(1, Mathf.RoundToInt(triCount * Mathf.Clamp01(ratio)));
            if (ratio >= 1f || triCount < 4 || positions.Length == 0) return (int[])indices.Clone();

            int n = positions.Length;
            // The position groups: copies of one point (a seam) share a group and its quadric.
            var group = new int[n];
            var groupOf = new Dictionary<Vector3, int>();
            var groupSize = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!groupOf.TryGetValue(positions[i], out int g)) { g = groupSize.Count; groupOf[positions[i]] = g; groupSize.Add(0); }
                group[i] = g;
                groupSize[g]++;
            }

            var tri = new int[triCount * 3];
            var alive = new bool[triCount];
            int live = 0;
            for (int t = 0; t < triCount; t++)
            {
                int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
                if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;
                tri[t * 3] = a; tri[t * 3 + 1] = b; tri[t * 3 + 2] = c;
                alive[t] = group[a] != group[b] && group[b] != group[c] && group[a] != group[c];
                if (alive[t]) live++;
            }

            // Quadrics per group, triangle lists per vertex.
            var quadric = new Quadric[groupSize.Count];
            var vtris = new List<int>[n];
            for (int i = 0; i < n; i++) vtris[i] = new List<int>(6);
            for (int t = 0; t < triCount; t++)
            {
                if (!alive[t]) continue;
                Vector3 p0 = positions[tri[t * 3]], p1 = positions[tri[t * 3 + 1]], p2 = positions[tri[t * 3 + 2]];
                var q = Quadric.Of(p0, p1, p2);
                for (int k = 0; k < 3; k++) { quadric[group[tri[t * 3 + k]]].Add(q); vtris[tri[t * 3 + k]].Add(t); }
            }

            // Locked groups: a seam (several vertices at one point), or an edge with other than two triangles.
            var locked = new bool[groupSize.Count];
            for (int g = 0; g < locked.Length; g++) locked[g] = groupSize[g] > 1;
            var edgeUse = new Dictionary<long, int>();
            for (int t = 0; t < triCount; t++)
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    long key = EdgeKey(group[tri[t * 3 + k]], group[tri[t * 3 + (k + 1) % 3]]);
                    edgeUse.TryGetValue(key, out int c);
                    edgeUse[key] = c + 1;
                }
            }
            foreach (var kv in edgeUse)
                if (kv.Value != 2) { locked[(int)(kv.Key >> 32)] = true; locked[(int)(kv.Key & 0xffffffff)] = true; }

            var removed = new bool[n];
            var version = new int[n];
            var heap = new MinHeap();
            void Consider(int u)
            {
                version[u]++;
                if (removed[u] || locked[group[u]]) return;
                if (Best(u, out int v, out double cost)) heap.Push(cost, u, v, version[u]);
            }
            bool Best(int u, out int best, out double bestCost)
            {
                best = -1;
                bestCost = double.MaxValue;
                var q = quadric[group[u]];
                foreach (int t in vtris[u])
                {
                    if (!alive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = tri[t * 3 + k];
                        if (v == u || removed[v]) continue;
                        double cost = q.Error(positions[v]);
                        if (cost < bestCost) { bestCost = cost; best = v; }
                    }
                }
                return best >= 0;
            }
            for (int i = 0; i < n; i++) Consider(i);

            while (live > target && heap.Count > 0)
            {
                var (u, v, ver) = heap.Pop();
                if (ver != version[u] || removed[u] || removed[v] || locked[group[u]]) continue;
                if (!StillNeighbours(u, v, vtris, tri, alive) || Flips(u, v, positions, vtris, tri, alive)) { version[u]++; continue; }
                // u goes: its triangles with v vanish, the others take v in its place.
                foreach (int t in vtris[u])
                {
                    if (!alive[t]) continue;
                    bool hasV = tri[t * 3] == v || tri[t * 3 + 1] == v || tri[t * 3 + 2] == v;
                    if (hasV) { alive[t] = false; live--; continue; }
                    for (int k = 0; k < 3; k++) if (tri[t * 3 + k] == u) tri[t * 3 + k] = v;
                    vtris[v].Add(t);
                }
                removed[u] = true;
                vtris[u].Clear();
                if (group[u] != group[v]) quadric[group[v]].Add(quadric[group[u]]);
                // The neighbours' best collapses changed.
                var seen = new HashSet<int>();
                foreach (int t in vtris[v])
                    if (alive[t])
                        for (int k = 0; k < 3; k++)
                            if (seen.Add(tri[t * 3 + k])) Consider(tri[t * 3 + k]);
            }

            var outList = new List<int>(live * 3);
            for (int t = 0; t < triCount; t++)
                if (alive[t]) { outList.Add(tri[t * 3]); outList.Add(tri[t * 3 + 1]); outList.Add(tri[t * 3 + 2]); }
            return outList.ToArray();
        }

        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        static bool StillNeighbours(int u, int v, List<int>[] vtris, int[] tri, bool[] alive)
        {
            foreach (int t in vtris[u])
                if (alive[t] && (tri[t * 3] == v || tri[t * 3 + 1] == v || tri[t * 3 + 2] == v)) return true;
            return false;
        }

        /// <summary>Would moving u onto v turn one of u's other triangles by more than 60 degrees (over, or up on its edge as a
        /// sliver) or squash it to nothing?</summary>
        static bool Flips(int u, int v, Vector3[] p, List<int>[] vtris, int[] tri, bool[] alive)
        {
            foreach (int t in vtris[u])
            {
                if (!alive[t]) continue;
                int a = tri[t * 3], b = tri[t * 3 + 1], c = tri[t * 3 + 2];
                if (a == v || b == v || c == v) continue;
                Vector3 before = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
                Vector3 pa = a == u ? p[v] : p[a], pb = b == u ? p[v] : p[b], pc = c == u ? p[v] : p[c];
                Vector3 after = Vector3.Cross(pb - pa, pc - pa);
                float bl = before.magnitude, al = after.magnitude;
                if (al <= bl * 1e-4f) return true;
                if (Vector3.Dot(before, after) < 0.5f * bl * al) return true;   // turned by more than 60 degrees
            }
            return false;
        }

        /// <summary>A plane-distance quadric (the 10 numbers of the symmetric 4 x 4 matrix), in doubles.</summary>
        struct Quadric
        {
            double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2;

            public static Quadric Of(Vector3 p0, Vector3 p1, Vector3 p2)
            {
                Vector3 nrm = Vector3.Cross(p1 - p0, p2 - p0);
                double area = nrm.magnitude;
                if (area <= 0) return default;
                double a = nrm.x / area, b = nrm.y / area, c = nrm.z / area, d = -(a * p0.x + b * p0.y + c * p0.z);
                double w = area;   // weighted by the triangle's size
                return new Quadric { a2 = w * a * a, ab = w * a * b, ac = w * a * c, ad = w * a * d, b2 = w * b * b, bc = w * b * c, bd = w * b * d, c2 = w * c * c, cd = w * c * d, d2 = w * d * d };
            }

            public void Add(Quadric q)
            {
                a2 += q.a2; ab += q.ab; ac += q.ac; ad += q.ad; b2 += q.b2; bc += q.bc; bd += q.bd; c2 += q.c2; cd += q.cd; d2 += q.d2;
            }

            public double Error(Vector3 p)
            {
                double x = p.x, y = p.y, z = p.z;
                return a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x + b2 * y * y + 2 * bc * y * z + 2 * bd * y + c2 * z * z + 2 * cd * z + d2;
            }
        }

        /// <summary>A binary min-heap of collapse candidates (cost, u, v, u's version when pushed).</summary>
        sealed class MinHeap
        {
            readonly List<(double cost, int u, int v, int ver)> items = new List<(double, int, int, int)>();
            public int Count => items.Count;

            public void Push(double cost, int u, int v, int ver)
            {
                items.Add((cost, u, v, ver));
                int i = items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].cost <= items[i].cost) break;
                    (items[parent], items[i]) = (items[i], items[parent]);
                    i = parent;
                }
            }

            public (int u, int v, int ver) Pop()
            {
                var top = items[0];
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < items.Count && items[l].cost < items[m].cost) m = l;
                    if (r < items.Count && items[r].cost < items[m].cost) m = r;
                    if (m == i) break;
                    (items[m], items[i]) = (items[i], items[m]);
                    i = m;
                }
                return (top.u, top.v, top.ver);
            }
        }
    }
}
