// HeldTradeTests.cs
// HeldTrade (CLAUDE.md "Shop", Sell all / Buy all by holding the arrow): a held sell / buy arrow trades unit by unit, faster
// and faster (an ease-in), the whole stack counted at the press after two seconds, and never slower than one unit per
// 120 ms after the first 200 ms, so a small stack doesn't crawl.

using GoF2Remake.UI;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class HeldTradeTests
    {
        static int[] Run(int total, int frames, float dtMs = 10f)
        {
            float held = 0f;
            int done = 1;
            var perFrame = new int[frames];
            for (int i = 0; i < frames; i++) perFrame[i] = HeldTrade.Step(ref held, ref done, total, dtMs);
            return perFrame;
        }

        static int Sum(int[] a, int upTo)
        {
            int s = 1;   // the press's own unit
            for (int i = 0; i < upTo; i++) s += a[i];
            return s;
        }

        [Test]
        public void ALargeStackGoesInTwoSecondsSpeedingUp()
        {
            var steps = Run(500, 250);
            Assert.AreEqual(500, Sum(steps, 200), "the whole stack at 2 s");
            Assert.Less(Sum(steps, 200), 501);
            Assert.AreEqual(500, Sum(steps, 250), "nothing past it");
            int firstHalf = Sum(steps, 100) - 1, secondHalf = Sum(steps, 200) - Sum(steps, 100);
            Assert.Less(firstHalf * 2, secondHalf, "faster over time");
            foreach (int n in steps) Assert.GreaterOrEqual(n, 0);
        }

        [Test]
        public void ASmallStackKeepsTheSteadyRepeat()
        {
            // 4 units: the press, then 200 ms, 320 ms, 440 ms (the ease alone would take until ~1.2 s for the second).
            var steps = Run(4, 100);
            Assert.AreEqual(1, Sum(steps, 19), "only the press before 200 ms");
            Assert.AreEqual(2, Sum(steps, 20));
            Assert.AreEqual(4, Sum(steps, 44));
            Assert.AreEqual(4, Sum(steps, 100));
        }

        [Test]
        public void ThePitchRisesWithTheStackAndIsCapped()
        {
            Assert.AreEqual(HeldTrade.StartPitch, HeldTrade.Pitch(1, 100), 1e-5f, "the press's unit at the start pitch");
            Assert.AreEqual(1f, HeldTrade.Pitch(1, 100), 1e-5f, "the normal pitch");
            Assert.Less(HeldTrade.Pitch(30, 100), HeldTrade.Pitch(70, 100));
            Assert.AreEqual(HeldTrade.MaxPitch, HeldTrade.Pitch(100, 100), 1e-5f);
            Assert.AreEqual(HeldTrade.MaxPitch, HeldTrade.Pitch(500, 100), 1e-5f, "capped");
            Assert.AreEqual(HeldTrade.StartPitch, HeldTrade.Pitch(1, 1), 1e-5f, "a single unit");
            Assert.LessOrEqual(HeldTrade.MaxPitch, 1.5f);
        }

        [Test]
        public void ATapTradesOne()
        {
            Assert.AreEqual(1, HeldTrade.Due(50f, 300));
            Assert.AreEqual(1, HeldTrade.Due(5000f, 1));
            Assert.AreEqual(0, HeldTrade.Due(5000f, 0));
            Assert.AreEqual(0f, HeldTrade.Progress(1, 4), "one unit (a click) shows no fill");
            Assert.AreEqual(0.5f, HeldTrade.Progress(2, 4), 1e-5f, "from the second unit, the share of the stack traded");
            Assert.AreEqual(1f, HeldTrade.Progress(4, 4));
            Assert.AreEqual(1f, HeldTrade.Progress(9, 4));
            Assert.AreEqual(0f, HeldTrade.Progress(0, 0));
        }
    }
}
