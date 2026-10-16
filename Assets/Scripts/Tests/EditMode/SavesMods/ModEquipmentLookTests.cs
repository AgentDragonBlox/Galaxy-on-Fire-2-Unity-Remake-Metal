// ModEquipmentLookTests.cs
// Feature: CLAUDE.md "Mods (remake-only)" > The look, and "NPCs and combat" > the tractor beam (CombatRadar): a mod's item
// based on an original sounds and looks like it. The boost sound goes by the booster's look (PlayerEgo::PlayerEgo +0xd4:
// boosters 71-74 -> sounds 0-3 = events 38-41, 195 -> 4 = 1102; any other booster like 71); a mod booster based on 73
// sounds like 73 (it was silent). A tractor beam's "tractorTargets" (attr 122) is how many crates it pulls at once
// (1..8, 1 without it), "tractorRange" (attr 123) how far its extra beams reach.
// Expected values: the original's table (PlayerEngine's header), the remake-only attribute numbers (ItemStats).

using GoF2Remake.Data;
using GoF2Remake.Flight;
using GoF2Remake.Modding;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class ModEquipmentLookTests : SavesModsTest
    {
        Database ModOn()
        {
            WriteMod("eq", "t_eq", "1.0", "", ("items.json", @"[
                { 'id': 'boost', 'base': 73, 'name': 'Boost' },
                { 'id': 'pull', 'base': 69, 'name': 'Pull', 'stats': { 'tractorTargets': 4, 'tractorRange': 50000 } },
                { 'id': 'many', 'base': 68, 'name': 'Many', 'stats': { 'tractorTargets': 99 } }
            ]"));
            ScanAndEnable("t_eq");
            Assert.IsFalse(Installed("t_eq").Broken, AllMessages(Installed("t_eq")));
            return Database.Load();
        }

        [Test]
        public void TheOriginalBoostersSoundsAreKept()
        {
            var db = Database.Load();
            Assert.AreEqual(-1, PlayerEngine.BoostSound(null));
            Assert.AreEqual(0, PlayerEngine.BoostSound(db.Item(71)));
            Assert.AreEqual(3, PlayerEngine.BoostSound(db.Item(74)));
            Assert.AreEqual(4, PlayerEngine.BoostSound(db.Item(195)));
        }

        [Test]
        public void AModBoosterSoundsLikeItsBase()
        {
            var db = ModOn();
            var boost = db.Item(ModContent.ItemIndexOf("t_eq:boost"));
            Assert.AreEqual(73, boost.Look);
            Assert.AreEqual(2, PlayerEngine.BoostSound(boost));
        }

        [Test]
        public void TractorTargetsAndRange()
        {
            var db = ModOn();
            Assert.AreEqual(CombatRadar.TractorTargetsAttr, ItemStats.IdOf("tractorTargets"));
            Assert.AreEqual(CombatRadar.TractorRangeAttr, ItemStats.IdOf("tractorRange"));
            Assert.AreEqual(1, CombatRadar.TractorTargets(db.Item(68)), "the original's one beam");
            Assert.AreEqual(0, CombatRadar.TractorTargets(null));
            var pull = db.Item(ModContent.ItemIndexOf("t_eq:pull"));
            Assert.AreEqual(4, CombatRadar.TractorTargets(pull));
            Assert.AreEqual(50000, pull.Attr(CombatRadar.TractorRangeAttr));
            Assert.AreEqual(69, pull.Look, "its beam is the base's");
            Assert.AreEqual(CombatRadar.MaxTractorTargets, CombatRadar.TractorTargets(db.Item(ModContent.ItemIndexOf("t_eq:many"))), "at most 8");
        }
    }
}
