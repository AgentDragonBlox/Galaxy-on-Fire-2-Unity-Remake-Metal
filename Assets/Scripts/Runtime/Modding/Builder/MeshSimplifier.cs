// MeshSimplifier.cs
// The Mod Builder's polygon reduction (ModOptimizer, the user's request 2026-10-22: very high-poly models uploaded as mods):
// quadric error edge collapse (Garland & Heckbert 1997) that only ever collapses a vertex onto a neighbour that already
// exists, so the result is a new triangle list over the same vertices and every other attribute (UVs, normals, tangents,
// colours, skin weights) stays valid as it is. The error quadrics are summed per position, so the copies of one point that a
// UV or normal seam splits share theirs; a vertex with such copies (a seam) and a vertex on an open edge or a non-manifold
// edge is never removed, so textures don't tear and the outline holds. A collapse that would turn a triangle by more than
// 60 degrees (over, or up on its edge) or squash it flat is refused. Plain C#, any thread.
// With 'seams' (the levels of detail, LodMeshes: the game's models split 93-97 % of their points by hard edges and UV
// seams, so locked seams left them uncut) a seam point may go too, all its copies at once and only along the seam: each
// copy onto a copy of the neighbouring point that shares a triangle with it (on its own side), so both sides of the seam
// keep their own UVs and normals and the seam stays closed; a point whose copies don't all have such a partner stays.
// With 'borders' (the levels of detail too: the game's models are open panels and fins, 17-41 % of their points on an open
// edge) a point on one open border (two open edges) may go along that border onto its neighbour there, never inward; each
// open edge adds the plane through it upright on its triangle to its points' quadrics, so the outline costs to move. A
// point where borders meet, or on a non-manifold edge (more than two triangles), stays.
// With 'loose' as well (the last level of detail, a few percent of the screen high) a seam copy without such a partner
// goes onto the neighbouring point's copy nearest it in the texture ('uvs'; none within LooseUvReach: it stays): its
// triangles may take a neighbouring face's normal and a slightly shifted UV, which only a close look would show; flat-shaded
// parts (a copy per face) can be cut at all only this way.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public static class MeshSimplifier
    {
        /// <summary>'indices' (triangles over 'positions') cut to about 'ratio' of the triangles where the seams and edges
        /// allow; returns the new triangle list (the same vertex numbers). Degenerate triangles of the input are dropped.
        /// 'seams': seam points may be collapsed along their seam; 'borders': border points along their border (see the
        /// header); 'loose': seam copies without a partner along the edge too, by 'uvs' (the vertices' texture coordinates;
        /// null: any copy).</summary>
        public static int[] Simplify(Vector3[] positions, int[] indices, float ratio, bool seams = false, bool borders = false, bool loose = false,
            Vector2[] uvs = null)
        {
            int triCount = indices.Length / 3;
            int target = Mathf.Max(1, Mathf.RoundToInt(triCount * Mathf.Clamp01(ratio)));
            if (ratio >= 1f || triCount < 4 || positions.Length == 0) return (int[])indices.Clone();

            int n = positions.Length;
            // The position groups: copies of one point (a seam) share a group and its quadric. Only the vertices a triangle
            // uses count: copies nothing points at (a model whose identical vertices were joined) are no seam.
            var used = new bool[n];
            foreach (int i in indices) if (i >= 0 && i < n) used[i] = true;
            var group = new int[n];
            var groupOf = new Dictionary<Vector3, int>();
            var groupSize = new List<int>();
            var copies = new List<List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!groupOf.TryGetValue(positions[i], out int g)) { g = groupSize.Count; groupOf[positions[i]] = g; groupSize.Add(0); copies.Add(new List<int>(1)); }
                group[i] = g;
                if (used[i]) { groupSize[g]++; copies[g].Add(i); }
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

            // Locked groups: a seam (several vertices at one point; not with 'seams'), or an edge with other than two triangles
            // (with 'borders' only a non-manifold one, or where open edges other than two meet).
            var locked = new bool[groupSize.Count];
            for (int g = 0; g < locked.Length; g++) locked[g] = !seams && groupSize[g] > 1;
            var border = new bool[groupSize.Count];
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
            var openEdges = new int[groupSize.Count];
            foreach (var kv in edgeUse)
            {
                int ga = (int)(kv.Key >> 32), gb = (int)(kv.Key & 0xffffffff);
                if (kv.Value == 2) continue;
                if (borders && kv.Value == 1) { openEdges[ga]++; openEdges[gb]++; continue; }
                locked[ga] = true;
                locked[gb] = true;
            }
            if (borders)
            {
                for (int g = 0; g < border.Length; g++)
                {
                    border[g] = openEdges[g] > 0;
                    if (openEdges[g] != 0 && openEdges[g] != 2) locked[g] = true;
                }
                // The outline's planes: through each open edge, upright on its triangle.
                for (int t = 0; t < triCount; t++)
                {
                    if (!alive[t]) continue;
                    Vector3 p0 = positions[tri[t * 3]], p1 = positions[tri[t * 3 + 1]], p2 = positions[tri[t * 3 + 2]];
                    Vector3 nrm = Vector3.Cross(p1 - p0, p2 - p0);
                    for (int k = 0; k < 3; k++)
                    {
                        int ga = group[tri[t * 3 + k]], gb = group[tri[t * 3 + (k + 1) % 3]];
                        if (edgeUse[EdgeKey(ga, gb)] != 1) continue;
                        Vector3 a = positions[tri[t * 3 + k]], b = positions[tri[t * 3 + (k + 1) % 3]];
                        var q = Quadric.Plane(Vector3.Cross(b - a, nrm), a, BorderWeight * (b - a).sqrMagnitude);
                        quadric[ga].Add(q);
                        quadric[gb].Add(q);
                    }
                }
            }

            var removed = new bool[n];
            var version = new int[n];
            var heap = new MinHeap();
            var pairs = new List<(int u, int v)>();
            var scratch = new List<(int u, int v)>();
            // The live copy of the point g nearest 'a' in the texture (within LooseUvReach), any live one without UVs.
            int NearestLive(int g, int a)
            {
                int best = -1;
                float bestDistance = LooseUvReach * LooseUvReach;
                foreach (int b in copies[g])
                {
                    if (removed[b]) continue;
                    bool live = false;
                    foreach (int t in vtris[b]) if (alive[t]) { live = true; break; }
                    if (!live) continue;
                    if (uvs == null || uvs.Length != n) return b;
                    float d = (uvs[b] - uvs[a]).sqrMagnitude;
                    if (d <= bestDistance) { bestDistance = d; best = b; }
                }
                return best;
            }
            // Is the edge between the points gu and gv open now: one live triangle on it?
            bool OpenEdge(int gu, int gv)
            {
                int on = 0;
                foreach (int a in copies[gu])
                    foreach (int t in vtris[a])
                        if (alive[t] && (group[tri[t * 3]] == gv || group[tri[t * 3 + 1]] == gv || group[tri[t * 3 + 2]] == gv) && ++on > 1)
                            return false;
                return on == 1;
            }
            // The copies of u's point each paired with a copy of v's that shares a live triangle with it (copies without
            // live triangles left out); false when one has none ('loose': any live copy of v's point). A point of one copy
            // pairs with v itself.
            bool Pair(int u, int v, List<(int u, int v)> into)
            {
                into.Clear();
                int gu = group[u], gv = group[v];
                if (border[gu] && !OpenEdge(gu, gv)) return false;   // a border point only along its border
                if (copies[gu].Count <= 1) { into.Add((u, v)); return true; }
                foreach (int a in copies[gu])
                {
                    if (removed[a]) continue;
                    int partner = -1;
                    bool live = false;
                    foreach (int t in vtris[a])
                    {
                        if (!alive[t]) continue;
                        live = true;
                        for (int k = 0; k < 3 && partner < 0; k++) { int b = tri[t * 3 + k]; if (group[b] == gv && !removed[b]) partner = b; }
                        if (partner >= 0) break;
                    }
                    if (!live) continue;
                    if (partner < 0 && loose) partner = NearestLive(gv, a);
                    if (partner < 0) return false;
                    into.Add((a, partner));
                }
                return into.Count > 0;
            }
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
                        if (cost < bestCost && (copies[group[u]].Count > 1 || border[group[u]]) && !Pair(u, v, scratch)) continue;
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
                if (!StillNeighbours(u, v, vtris, tri, alive) || !Pair(u, v, pairs)) { version[u]++; continue; }
                bool refused = false;
                foreach (var (a, b) in pairs) if (Flips(a, b, positions, vtris, tri, alive)) { refused = true; break; }
                if (refused) { version[u]++; continue; }
                // Each copy goes: its triangles touching v's point vanish, the others take its partner in its place.
                int gv = group[v];
                foreach (var (a, b) in pairs)
                {
                    foreach (int t in vtris[a])
                    {
                        if (!alive[t]) continue;
                        bool hasV = group[tri[t * 3]] == gv || group[tri[t * 3 + 1]] == gv || group[tri[t * 3 + 2]] == gv;
                        if (hasV) { alive[t] = false; live--; continue; }
                        for (int k = 0; k < 3; k++) if (tri[t * 3 + k] == a) tri[t * 3 + k] = b;
                        vtris[b].Add(t);
                    }
                    removed[a] = true;
                    vtris[a].Clear();
                }
                if (group[u] != gv) quadric[gv].Add(quadric[group[u]]);
                // The neighbours' best collapses changed.
                var seen = new HashSet<int>();
                foreach (var (_, b) in pairs)
                    foreach (int t in vtris[b])
                        if (alive[t])
                            for (int k = 0; k < 3; k++)
                                if (seen.Add(tri[t * 3 + k])) Consider(tri[t * 3 + k]);
            }

            var outList = new List<int>(live * 3);
            for (int t = 0; t < triCount; t++)
                if (alive[t]) { outList.Add(tri[t * 3]); outList.Add(tri[t * 3 + 1]); outList.Add(tri[t * 3 + 2]); }
            return outList.ToArray();
        }

        /// <summary>How far in the texture (UV units) a loose seam copy may land from where it was.</summary>
        const float LooseUvReach = 0.05f;

        /// <summary>The weight of an open edge's plane against the triangles' (which weigh by their area).</summary>
        const double BorderWeight = 4.0;

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

            /// <summary>The plane through 'p' with the normal 'n' (any length), weighted by 'w'.</summary>
            public static Quadric Plane(Vector3 n, Vector3 p, double w)
            {
                double len = n.magnitude;
                if (len <= 0 || w <= 0) return default;
                double a = n.x / len, b = n.y / len, c = n.z / len, d = -(a * p.x + b * p.y + c * p.z);
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
