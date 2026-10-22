// ModAudioNamesTests.cs
// Feature: CLAUDE.md "Mods" > Sound effects / Voice-over (2026-10-22): a sounds/ file of any name is a mod sound /sound
// and the quests play by name (ModSounds.Has); a voices/ file named like one of the game's lines replaces it
// (ModVoices.PreloadReplacements, StoryAssets.IsVoice), in every language (voices/) or one (voices/<language>/, any of the
// game's text languages); a voices/ file of any other name is a mod line, not a replacement.
// The audio files' contents don't matter here: what is counted is decided from the names. The Mod Builder's pickers can play
// every game sound and track they list (Modding.GameAudioIndex, GoF2 > Build > Mod Builder Audio Previews).

using GoF2Remake.Data;
using GoF2Remake.Modding;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class ModAudioNamesTests : SavesModsTest
    {
        [Test]
        public void ANewSoundIsPlayedByNameAndAGameLineIsReplacedInAnyLanguage()
        {
            var assets = StoryAssets.Load();
            Assert.IsNotNull(assets);
            var lines = assets.VoiceNames();
            Assert.Greater(lines.Count, 100, "the game's voice lines by event name");
            string line = lines[0];
            Assert.IsTrue(assets.IsVoice(line));
            Assert.IsFalse(assets.IsVoice("t_audio_not_a_line"));
            CollectionAssert.DoesNotContain(lines, lines.Find(n => n.EndsWith("_Alt2")), "re-recorded waves by their event's name");

            WriteMod("aud", "t_audio", "1.0", "",
                ("sounds/t_klaxon.ogg", "x"),
                ("voices/" + line + ".ogg", "x"),
                ("voices/fr/" + line + ".ogg", "x"),
                ("voices/zh/" + line + ".ogg", "x"),
                ("voices/t_own_line.ogg", "x"));
            ScanAndEnable("t_audio");

            Assert.IsTrue(ModSounds.Has("t_klaxon"));
            Assert.IsFalse(ModSounds.Has("t_missing"));
            CollectionAssert.Contains(ModSounds.Names, "t_klaxon");
            Assert.AreEqual(3, ModVoices.ReplacementCount, "the game's line in every language, French and Chinese; not the mod's own line");
        }

        [Test]
        public void EverySoundAndTrackTheBuilderListsCanBePlayed()
        {
            var list = UnityEngine.Resources.Load<UnityEngine.TextAsset>("GoF2ModBuilder/SoundNames");
            Assert.IsNotNull(list);
            foreach (var line in list.text.Replace("\r", "").Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                string name = l.Substring(l.LastIndexOf('/') + 1);
                Assert.IsNotNull(GameAudioIndex.Clip(name), l + ": run GoF2 > Build > Mod Builder Audio Previews");
            }
            foreach (var track in ModRefs.GameMusic) Assert.IsNotNull(GameAudioIndex.Clip(track), track);
        }
    }
}
