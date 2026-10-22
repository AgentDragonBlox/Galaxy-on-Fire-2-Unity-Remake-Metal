// TargetGridTests.cs
// Feature: CLAUDE.md "NPCs and combat" > Performance, the guns' broad phase (Flight.TargetGrid): once a frame the hittable
// targets are filed by their broad sphere (Target.BroadSphere) into hash grids, and a shot (Gun.TestHits) looks only at the
// targets of its cell. It must find every target Target.MayContain passes, whatever its size: small ships in the fine
// grid, big hulls in the coarse one, the biggest in the list every shot sees.

using System.Collections.Generic;
using GoF2Remake.Flight;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    public class TargetGridTests
    {
        readonly List<GameObject> made = new List<GameObject>();
        readonly List<Target> added = new List<Target>();

        [TearDown]
        public void CleanUp()
        {
            foreach (var t in added) Target.All.Remove(t);
            added.Clear();
            foreach (var go in made) if (go != null) Object.DestroyImmediate(go);
            made.Clear();
        }

        Target NewTarget(Vector3 at, float radius, Bounds[] boxes = null)
        {
            var go = new GameObject("target");
            made.Add(go);
            go.transform.position = at;
            var t = go.AddComponent<Target>();
            t.radius = radius;
            t.boxes = boxes;
            if (!Target.All.Contains(t)) Target.All.Add(t);   // OnEnable doesn't run in edit mode
            added.Add(t);
            return t;
        }

        [Test]
        public void EveryTargetThatMayHoldAPointIsNearIt()
        {
            var rng = new System.Random(7);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var targets = new List<Target>();
            for (int i = 0; i < 80; i++)
                targets.Add(NewTarget(new Vector3(R(-6000, 6000), R(-3000, 3000), R(-6000, 6000)), R(5f, 120f)));
            targets.Add(NewTarget(new Vector3(100, 0, 100), 400f));     // an asteroid: the coarse grid
            targets.Add(NewTarget(new Vector3(-2000, 50, 900), 50f,     // a capital ship's boxes, 3 km: the list every shot sees
                new[] { new Bounds(new Vector3(0, 0, 1500f), new Vector3(300, 200, 3000)) }));
            const int frame = 424242;
            var near = new List<Target>();
            int checkedHits = 0;
            for (int k = 0; k < 4000; k++)
            {
                // Half the points close to a target, so many fall inside its sphere.
                Vector3 p = k % 2 == 0 ? targets[rng.Next(targets.Count)].transform.position + new Vector3(R(-150, 150), R(-150, 150), R(-150, 150))
                                       : new Vector3(R(-7000, 7000), R(-3500, 3500), R(-7000, 7000));
                near.Clear();
                TargetGrid.Near(p, frame, near);
                Assert.AreEqual(near.Count, new HashSet<Target>(near).Count, "a target listed twice");
                foreach (var t in targets)
                    if (t.MayContain(p, frame))
                    {
                        checkedHits++;
                        Assert.IsTrue(near.Contains(t), $"target at {t.transform.position} (radius {t.radius}) missed at {p}");
                    }
            }
            Assert.Greater(checkedHits, 500, "the points should fall inside many spheres");
        }

        [Test]
        public void OnlyTheTargetsWithAHitSphereAreListedForIt()
        {
            var a = NewTarget(Vector3.zero, 20f);
            var b = NewTarget(new Vector3(500, 0, 0), 20f);
            b.hitSphereRadius = 30f;
            var spheres = TargetGrid.Spheres(515151);
            Assert.That(spheres, Has.Member(b));
            Assert.That(spheres, Has.No.Member(a));
        }
    }
}
