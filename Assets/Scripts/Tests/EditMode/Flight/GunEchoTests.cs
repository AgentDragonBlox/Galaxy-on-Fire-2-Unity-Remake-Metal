// GunEchoTests.cs
// The echo (Flight.GunEcho, items.json stats echoDelayMs / echoPercent / echoRadius / echoExplosion = attrs 127-130):
// what an item's stats make of it, and when each echo goes off. The blast itself (Gun.EchoBlast) needs targets and is
// checked in Play mode.

using System.Collections.Generic;
using GoF2Remake.Data;
using GoF2Remake.Flight;
using NUnit.Framework;
using UnityEngine;

namespace GoF2Remake.Tests
{
    public class GunEchoTests
    {
        /// <summary>An item with the given attribute pairs (attr, value, attr, value...).</summary>
        static ItemData Item(params int[] pairs)
        {
            var keys = new int[pairs.Length / 2];
            var values = new int[pairs.Length / 2];
            for (int i = 0; i < keys.Length; i++) { keys[i] = pairs[2 * i]; values[i] = pairs[2 * i + 1]; }
            return new ItemData { index = 178, categoryId = 25, attrKeys = keys, attrValues = values };
        }

        [Test]
        public void AnItemWithoutADelayHasNoEcho()
        {
            Assert.IsNull(GunEcho.From(Item(9, 30)));
            Assert.IsNull(GunEcho.From(Item(127, 0, 128, 60)));
        }

        [Test]
        public void ADelayAloneMakesAnEchoWithTheDefaults()
        {
            var e = GunEcho.From(Item(127, 500));
            Assert.NotNull(e);
            Assert.AreEqual(500f, e.delayMs, 1e-4f);
            Assert.AreEqual(50f, e.percent, 1e-4f);
            Assert.AreEqual(0f, e.radiusUnits, 1e-4f);   // 0: the gun's own blast radius (attr 14)
            Assert.AreEqual(GunEcho.DefaultExplosion, e.explosion);
        }

        [Test]
        public void TheStatsAreRead()
        {
            var e = GunEcho.From(Item(127, 500, 128, 60, 129, 3000, 130, 10));
            Assert.AreEqual(500f, e.delayMs, 1e-4f);
            Assert.AreEqual(60f, e.percent, 1e-4f);
            Assert.AreEqual(3000f, e.radiusUnits, 1e-4f);
            Assert.AreEqual(10, e.explosion);
            Assert.AreEqual(0.6f, e.Scale, 1e-6f);
        }

        [Test]
        public void TheDelayIsCapped()
        {
            Assert.AreEqual(GunEcho.MaxDelayMs, GunEcho.From(Item(127, 999999)).delayMs, 1e-4f);
        }

        [Test]
        public void ANoneExplosionIsKept()
        {
            Assert.AreEqual(-1, GunEcho.From(Item(127, 100, 130, -1)).explosion);
        }

        [Test]
        public void APercentOfZeroIsTheDefaultAndALargeOneIsCapped()
        {
            Assert.AreEqual(GunEcho.DefaultPercent, GunEcho.From(Item(127, 100, 128, 0)).percent, 1e-4f);
            Assert.AreEqual(1000f, GunEcho.From(Item(127, 100, 128, 5000)).percent, 1e-4f);
        }

        [Test]
        public void TheEchoGoesOffAfterItsDelayAtItsPoint()
        {
            var e = new GunEcho(500f, 60f, 0f, 8);
            var at = new Vector3(1f, 2f, 3f);
            var fired = new List<Vector3>();
            e.Schedule(at);
            e.Tick(300f, fired.Add);
            Assert.AreEqual(0, fired.Count);
            Assert.AreEqual(1, e.Waiting);
            e.Tick(250f, fired.Add);
            Assert.AreEqual(1, fired.Count);
            Assert.AreEqual(at, fired[0]);
            Assert.AreEqual(0, e.Waiting);
        }

        [Test]
        public void EchoesGoOffInTheOrderTheirHitsCame()
        {
            var e = new GunEcho(500f, 60f, 0f, 8);
            var a = new Vector3(1f, 0f, 0f);
            var b = new Vector3(0f, 1f, 0f);
            var fired = new List<Vector3>();
            e.Schedule(a);
            e.Tick(200f, fired.Add);
            e.Schedule(b);
            e.Tick(300f, fired.Add);   // a is due (300 left: 0), b has 200 to go
            Assert.AreEqual(new[] { a }, fired.ToArray());
            e.Tick(200f, fired.Add);
            Assert.AreEqual(new[] { a, b }, fired.ToArray());
        }

        [Test]
        public void AFullBufferDropsTheNewestHit()
        {
            var e = new GunEcho(500f, 60f, 0f, 8);
            for (int i = 0; i < GunEcho.Capacity + 4; i++) e.Schedule(new Vector3(i, 0f, 0f));
            Assert.AreEqual(GunEcho.Capacity, e.Waiting);
        }

        [Test]
        public void ClearDropsTheWaitingEchoes()
        {
            var e = new GunEcho(500f, 60f, 0f, 8);
            e.Schedule(Vector3.zero);
            e.Clear();
            Assert.AreEqual(0, e.Waiting);
        }
    }
}
