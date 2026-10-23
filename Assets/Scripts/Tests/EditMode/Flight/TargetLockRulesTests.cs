// TargetLockRulesTests.cs
// The remake's ship lock (CLAUDE.md "NPCs and combat", Radar; TargetLockRules, CombatRadar): the Smart lock's priority
// (hostile first, then the nearest to the crosshair), its hold on an enemy (Settings.KeepEnemyLock, its own option after a
// player's suggestion), the ship let go by Unlock target, which stays out until it leaves the crosshair box, and the
// cargo steal from another game's EMP-disabled ship in a session.

using System.Linq;
using GoF2Remake.Flight;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class TargetLockRulesTests
    {
        struct Ship { public string name; public float dx, dy; public bool hostile; }

        /// <summary>CombatRadar's pick with the Smart lock: the eligible ship with the lowest score, or null.</summary>
        static string Pick(Ship[] ships, bool keepEnemyLock, bool lockedHostileAlive, string released = null)
        {
            bool holds = TargetLockRules.HoldsEnemy(true, keepEnemyLock, lockedHostileAlive);
            return ships.Where(s => TargetLockRules.Eligible(holds, s.hostile, s.name == released))
                        .OrderBy(s => TargetLockRules.Score(s.dx, s.dy, s.hostile))
                        .Select(s => s.name).FirstOrDefault();
        }

        static readonly Ship Enemy = new Ship { name = "enemy", dx = 60, dy = 40, hostile = true };
        static readonly Ship Trader = new Ship { name = "trader", dx = 2, dy = 1, hostile = false };
        static readonly Ship Raider = new Ship { name = "raider", dx = 90, dy = 0, hostile = true };

        [Test]
        public void HostileShipsComeFirstThenTheNearest()
        {
            Assert.AreEqual("enemy", Pick(new[] { Trader, Raider, Enemy }, true, false), "hostile first, the nearer of the two");
            Assert.AreEqual("trader", Pick(new[] { Trader }, true, false), "no hostile ship in the box: the neutral one");
            Assert.Less(TargetLockRules.Score(1000, 1000, true), TargetLockRules.Score(0, 0, false), "any hostile beats any neutral");
        }

        [Test]
        public void TheHoldKeepsNeutralShipsOffWhileAnEnemyIsLocked()
        {
            Assert.IsNull(Pick(new[] { Trader }, true, true), "an enemy locked: the trader can't take it");
            Assert.AreEqual("raider", Pick(new[] { Trader, Raider }, true, true), "another hostile ship still can");
            Assert.AreEqual("trader", Pick(new[] { Trader }, true, false), "the locked enemy dead or neutral: no hold");
        }

        [Test]
        public void WithoutTheHoldANeutralShipCanBeLockedDuringABattle()
        {
            Assert.AreEqual("trader", Pick(new[] { Trader }, false, true), "an enemy locked, the hold off: the trader locks");
            Assert.AreEqual("enemy", Pick(new[] { Trader, Enemy }, false, true), "the priority stays: a hostile ship in the box first");
        }

        [Test]
        public void TheHoldIsOnlyPartOfTheSmartLock()
        {
            Assert.IsFalse(TargetLockRules.HoldsEnemy(false, true, true), "the Original lock never holds");
            Assert.IsTrue(TargetLockRules.HoldsEnemy(true, true, true));
            Assert.IsFalse(TargetLockRules.HoldsEnemy(true, false, true));
        }

        [Test]
        public void AnUnlockedShipStaysOutUntilItLeavesTheBox()
        {
            // Unlocked: the enemy still in the box isn't picked straight back, the trader beside it is (no lock, no hold).
            Assert.AreEqual("trader", Pick(new[] { Trader, Enemy }, true, false, released: "enemy"));
            Assert.AreEqual("raider", Pick(new[] { Trader, Enemy, Raider }, true, false, released: "enemy"), "another enemy still first");
            Assert.IsFalse(TargetLockRules.Eligible(false, true, true), "released: never a candidate");
            Assert.IsTrue(TargetLockRules.StillReleased(true, true), "alive and in the box: still released");
            Assert.IsFalse(TargetLockRules.StillReleased(true, false), "out of the box: lockable again");
            Assert.IsFalse(TargetLockRules.StillReleased(false, true), "dead: forgotten");
        }

        // Discord report 2026-10-21: "the tractor beam can't take loot from EMP-disabled ships". The radar only looked for a
        // steal among its own traffic, so in a session the players whose game didn't run the orbit's NPCs saw an EMP'd NPC
        // (another game's NetProxy) as a plain ship lock and could never steal from it.
        [Test]
        public void AnotherGamesDisabledShipWithCargoCanBeStolenFrom()
        {
            Assert.IsTrue(TargetLockRules.RemoteStealable(true, true, true, 6, 12), "disabled, flying, 12 t aboard: a steal");
            Assert.IsFalse(TargetLockRules.RemoteStealable(false, true, true, 6, 12), "not disabled: a ship lock");
            Assert.IsFalse(TargetLockRules.RemoteStealable(true, false, true, 6, 12), "dying or dead: its own crate, no steal");
            Assert.IsFalse(TargetLockRules.RemoteStealable(true, true, true, -1, 0), "no cargo: a ship lock");
            Assert.IsFalse(TargetLockRules.RemoteStealable(true, true, false, 6, 12), "a mission ship's cargo: the team's only");

            var go = new UnityEngine.GameObject("proxy");
            try
            {
                var t = go.AddComponent<Target>();
                t.netStealItem = 6;
                t.netStealAmount = 12;
                Assert.IsFalse(t.NetStealable, "only a copy of another game's ship (RemoteSteal) is stolen from this way");
                int stolen = 0;
                t.RemoteSteal = (item, amount) => stolen += amount;
                Assert.IsTrue(t.NetStealable, "the radar takes it as a steal candidate");
                t.RemoteSteal(6, 4);
                Assert.AreEqual(4, stolen, "the capture's units go to the owner's ship");
                t.netStealAmount = 0;
                Assert.IsFalse(t.NetStealable, "emptied: a ship lock again");

                var crate = go.AddComponent<Crate>();
                Assert.IsFalse(crate.Stolen);
                crate.stolenFromRemote = t;
                Assert.IsTrue(crate.Stolen, "a remote steal's crate is the stealer's: never shared (NetOrbit) or auto-salvaged");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
