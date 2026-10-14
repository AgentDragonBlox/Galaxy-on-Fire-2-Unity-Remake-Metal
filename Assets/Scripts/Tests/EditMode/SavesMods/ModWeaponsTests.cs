// ModWeaponsTests.cs
// Feature: Modding/README.md "Weapon effects" (ModWeapons.Defs, the items.json "fx" entries): a turret's own model ("turret":
// a file, or { "model", "muzzle", "shotStart", "scale" }), a part borrowed from another item ("item") and resized ("scale"), a sound
// taken from another item ("shot": a number), and their refusals. Only the parsing: the models aren't loaded here.

using System.Linq;
using GoF2Remake.Modding;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class ModWeaponsTests : SavesModsTest
    {
        const string Mod = "t_fx";

        ModWeapons.Def DefOf(string fxJson, params (string path, string text)[] files)
        {
            var all = files.Append(("items.json", "[ { 'id': 'mat', 'base': 182, 'name': 'Missile turret', 'fx': " + fxJson + " } ]")).ToArray();
            WriteMod("fx", Mod, "1.0", "", all);
            ScanAndEnable(Mod);
            var defs = ModWeapons.Defs();
            return defs.Count == 1 ? defs[0] : null;
        }

        [Test]
        public void ATurretModelWithItsMuzzleAndShotStart()
        {
            var d = DefOf("{ 'turret': { 'model': 'models/t.glb', 'muzzle': 28, 'shotStart': 32 } }", ("models/t.glb", "glb"));
            Assert.IsNotNull(d, AllMessages(Installed(Mod)));
            Assert.AreEqual("models/t.glb", d.turretModel);
            Assert.AreEqual(28f, d.turretMuzzle);
            Assert.AreEqual(32f, d.turretShot);
        }

        [Test]
        public void ATurretModelAsAFileKeepsTheBaseOffsets()
        {
            var d = DefOf("{ 'turret': 'models/t.glb' }", ("models/t.glb", "glb"));
            Assert.AreEqual("models/t.glb", d.turretModel);
            Assert.AreEqual(-1f, d.turretMuzzle);
            Assert.AreEqual(-1f, d.turretShot);
        }

        [Test]
        public void ATurretModelWithItsScale()
        {
            var d = DefOf("{ 'turret': { 'model': 'models/t.glb', 'scale': 2.5 } }", ("models/t.glb", "glb"));
            Assert.AreEqual(2.5f, d.turretScale);
            Assert.AreEqual(1f, DefOf("{ 'turret': 'models/t.glb' }", ("models/t.glb", "glb")).turretScale, "as made");
        }

        [Test]
        public void ATurretModelMovedOffItsMount()
        {
            var d = DefOf("{ 'turret': { 'model': 'models/t.glb', 'offset': [10, -20, 30] } }", ("models/t.glb", "glb"));
            Assert.AreEqual(new UnityEngine.Vector3(10f, -20f, 30f), d.turretOffset);
            Assert.IsTrue(d.TurretReshaped);
        }

        [Test]
        public void TheBaseTurretResizedWithoutAModel()
        {
            var d = DefOf("{ 'turret': { 'scale': 1.5, 'offset': [0, 40, 0] } }");
            Assert.IsNotNull(d, AllMessages(Installed(Mod)));
            Assert.IsNull(d.turretModel, "the base item's model");
            Assert.AreEqual(1.5f, d.turretScale);
            Assert.IsTrue(d.TurretReshaped);
        }

        [Test]
        public void AnEmptyTurretIsRefused()
        {
            Assert.IsNull(DefOf("{ 'turret': { } }"));
            StringAssert.Contains("needs \"model\"", AllMessages(Installed(Mod)));
            Assert.IsNull(DefOf("{ 'turret': { 'model': 'models/t.glb', 'offset': [1, 2] } }", ("models/t.glb", "glb")));
            StringAssert.Contains("\"offset\" is [x, y, z]", AllMessages(Installed(Mod)));
        }

        [Test]
        public void ATurretScaleOfZeroIsRefused()
        {
            Assert.IsNull(DefOf("{ 'turret': { 'model': 'models/t.glb', 'scale': 0 } }", ("models/t.glb", "glb")));
            StringAssert.Contains("\"scale\" must be above 0", AllMessages(Installed(Mod)));
        }

        [Test]
        public void AMissingTurretModelIsRefused()
        {
            Assert.IsNull(DefOf("{ 'turret': 'models/none.glb' }"));
            StringAssert.Contains("isn't in the mod", AllMessages(Installed(Mod)));
        }

        [Test]
        public void APartFromAnotherItemResized()
        {
            var d = DefOf("{ 'projectile': { 'item': 46, 'scale': 0.35 }, 'impact': { 'scale': 2 } }");
            Assert.IsTrue(d.projectile.copy);
            Assert.IsFalse(d.projectile.recolor);
            Assert.AreEqual(46, d.projectile.fromItem);
            Assert.AreEqual(0.35f, d.projectile.scale, 1e-6f);
            Assert.IsTrue(d.impact.copy, "the base item's own impact, twice the size");
            Assert.AreEqual(-1, d.impact.fromItem);
            Assert.AreEqual(2f, d.impact.scale);
            Assert.IsEmpty(Installed(Mod).Warnings, AllMessages(Installed(Mod)));
        }

        [Test]
        public void ARecolourIsStillACopy()
        {
            var d = DefOf("{ 'projectile': { 'color': '#ff0000' } }");
            Assert.IsTrue(d.projectile.copy);
            Assert.IsTrue(d.projectile.recolor);
            Assert.AreEqual(1f, d.projectile.scale);
        }

        [Test]
        public void AnUnknownItemIsRefused()
        {
            Assert.IsNull(DefOf("{ 'projectile': { 'item': 99999 } }"));
            StringAssert.Contains("there is no item 99999", AllMessages(Installed(Mod)));
        }

        [Test]
        public void ScaleOnASpriteIsIgnoredWithAWarning()
        {
            var d = DefOf("{ 'projectile': { 'sprite': 'fx/b.png', 'scale': 2 } }", ("fx/b.png", "png"));
            Assert.IsFalse(d.projectile.copy);
            StringAssert.Contains("only applies without a sprite or model", AllMessages(Installed(Mod)));
        }

        [Test]
        public void ASoundFromAnotherItem()
        {
            var d = DefOf("{ 'shot': 46, 'explosionSound': 7, 'shotLoops': false }");
            Assert.AreEqual(46, d.shotItem);
            Assert.IsNull(d.shots);
            Assert.AreEqual(7, d.explosionItem);
            Assert.AreEqual(false, d.shotLoops);
        }
    }
}
