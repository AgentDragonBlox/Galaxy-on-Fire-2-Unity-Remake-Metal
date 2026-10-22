// TargetGrid.cs
// Remake (performance, 2026-10-22): the guns' broad phase. Every shot tested every target of its gun's list (Gun.TestHits:
// in 20 fleet battles ~3400 shots in flight against ~430 enemies each, 1.4 million sphere tests a frame, 3 ms). Once a frame
// the hittable targets (Target.All) are filed by their broad sphere (Target.BroadSphere, the one MayContain tests) into hash
// grids, so a shot only looks at the targets whose sphere can hold it. The grids find exactly the targets MayContain would
// pass: each target sits in every cell its sphere's box overlaps, and a point inside the sphere is in one of those cells.

using System.Collections.Generic;
using UnityEngine;

namespace GoF2Remake.Flight
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class TargetGrid
    {
        /// <summary>Cell sizes (metres) of the grids, finest first: a target goes into the finest grid whose cell is at least its
        /// sphere's radius (at most 8 cells), else into the list every shot looks at (the biggest hulls).</summary>
        static readonly float[] CellSizes = { 250f, 2500f };

        static readonly Dictionary<long, List<Target>>[] grids = { new Dictionary<long, List<Target>>(), new Dictionary<long, List<Target>>() };
        static readonly List<Target> huge = new List<Target>();
        static readonly List<Target> spheres = new List<Target>();
        static readonly Stack<List<Target>> pool = new Stack<List<Target>>();
        static int builtFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            foreach (var g in grids) g.Clear();
            huge.Clear();
            spheres.Clear();
            pool.Clear();
            builtFrame = -1;
        }

        /// <summary>Adds to 'into' every target whose broad sphere may hold 'point' this 'frame' (and some others near it), in no
        /// particular order, each once.</summary>
        public static void Near(Vector3 point, int frame, List<Target> into)
        {
            Build(frame);
            for (int g = 0; g < grids.Length; g++)
                if (grids[g].TryGetValue(Key(point, CellSizes[g]), out var cell)) into.AddRange(cell);
            into.AddRange(huge);
        }

        /// <summary>The targets with a hit sphere (Target.hitSphereRadius, the players' collision probes) this 'frame'.</summary>
        public static List<Target> Spheres(int frame)
        {
            Build(frame);
            return spheres;
        }

        static void Build(int frame)
        {
            if (builtFrame == frame) return;
            builtFrame = frame;
            foreach (var g in grids)
            {
                foreach (var l in g.Values) { l.Clear(); pool.Push(l); }
                g.Clear();
            }
            huge.Clear();
            spheres.Clear();
            var all = Target.All;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if ((object)t == null) continue;
                float r = t.BroadSphere(frame, out var c);
                if (r < 0f) continue;
                if (t.hitSphereRadius > 0f) spheres.Add(t);
                int level = 0;
                while (level < CellSizes.Length && r > CellSizes[level]) level++;
                if (level == CellSizes.Length) { huge.Add(t); continue; }
                float size = CellSizes[level];
                var grid = grids[level];
                int x0 = Cell(c.x - r, size), x1 = Cell(c.x + r, size);
                int y0 = Cell(c.y - r, size), y1 = Cell(c.y + r, size);
                int z0 = Cell(c.z - r, size), z1 = Cell(c.z + r, size);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long key = Pack(x, y, z);
                            if (!grid.TryGetValue(key, out var list))
                            {
                                list = pool.Count > 0 ? pool.Pop() : new List<Target>(4);
                                grid[key] = list;
                            }
                            list.Add(t);
                        }
            }
        }

        static int Cell(float v, float size) => Mathf.FloorToInt(v / size);

        static long Key(Vector3 p, float size) => Pack(Cell(p.x, size), Cell(p.y, size), Cell(p.z, size));

        /// <summary>21 bits a coordinate: +-1 million cells, far past any orbit (positions are checked to 1e6 m).</summary>
        static long Pack(int x, int y, int z) =>
            ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }
}
