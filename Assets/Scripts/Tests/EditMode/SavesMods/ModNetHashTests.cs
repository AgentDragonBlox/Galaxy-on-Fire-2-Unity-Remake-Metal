// ModNetHashTests.cs
// ModInfo.NetHash (CLAUDE.md "Mods", Multiplayer: what a session compares, NetMods.InstalledList in every game's connection
// data): a mod file that can't be read (a damaged zip) is the mod's warning and makes a hash no intact copy has, instead of
// an exception (a Discord report, "I am unable to play multiplayer at all": hosting stopped on the fade, joining on
// "Connecting..."). Hash (the saves' signature) already did so.

using System.Collections.Generic;
using System.IO;
using GoF2Remake.Modding;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class ModNetHashTests
    {
        sealed class FakeSource : ModSource
        {
            readonly Dictionary<string, byte[]> files;
            readonly string damaged;
            public FakeSource(Dictionary<string, byte[]> files, string damaged = null) { this.files = files; this.damaged = damaged; }
            public override IReadOnlyList<string> Files => new List<string>(files.Keys);
            public override byte[] ReadBytes(string path)
            {
                if (path == damaged) throw new ModFileException($"{path} is damaged in test.zip");
                return files.TryGetValue(path, out var b) ? b : null;
            }
        }

        static Dictionary<string, byte[]> Files() => new Dictionary<string, byte[]>
        {
            { "items.json", new byte[] { 1, 2, 3 } },
            { "models/ship.glb", new byte[] { 4, 5, 6 } },
        };

        static ModInfo Mod(ModSource source) => new ModInfo(new ModManifest { id = "test" }, source);

        [Test]
        public void ADamagedFileIsAWarningNotAnException()
        {
            var mod = Mod(new FakeSource(Files(), "models/ship.glb"));
            string hash = null;
            Assert.DoesNotThrow(() => hash = mod.NetHash);
            Assert.IsNotEmpty(hash);
            Assert.That(mod.Warnings, Has.Some.Contains("damaged"));
        }

        [Test]
        public void ADamagedCopyDiffersFromAnIntactOne()
        {
            Assert.AreNotEqual(Mod(new FakeSource(Files())).NetHash, Mod(new FakeSource(Files(), "models/ship.glb")).NetHash);
            Assert.AreEqual(Mod(new FakeSource(Files())).NetHash, Mod(new FakeSource(Files())).NetHash);
        }
    }
}
