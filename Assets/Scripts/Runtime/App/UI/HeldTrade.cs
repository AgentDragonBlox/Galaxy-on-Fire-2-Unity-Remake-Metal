// HeldTrade.cs
// Remake: how a held sell / buy arrow repeats (the hangar's shop and Kaamo storage, the blueprint ingredients, the carrier's
// shop). The original (HangarWindow::update) repeats after 200 ms, every 30 ms once held 1.5 s, 5 units at a time after 4 s;
// the remake's held arrow is also the Sell all / Buy all (the user's request: the separate buttons went): unit by unit,
// faster and faster, the whole stack (counted at the press) gone after two seconds of holding (an ease-in, t^2), never
// slower than one unit per 120 ms after the first 200 ms, so a small stack doesn't crawl; a bar fills the arrow meanwhile.

using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    public static class HeldTrade
    {
        public const float FirstRepeatMs = 200f, RepeatEveryMs = 120f, WholeStackMs = 2000f;

        /// <summary>The units traded by 'heldMs' of holding, the press's own unit included, of a stack of 'total'.</summary>
        public static int Due(float heldMs, int total)
        {
            if (total <= 1) return Mathf.Max(0, total);
            float t = Mathf.Clamp01(heldMs / WholeStackMs);
            int eased = 1 + Mathf.FloorToInt((total - 1) * t * t + 1e-4f);
            int steady = heldMs < FirstRepeatMs ? 1 : 2 + Mathf.FloorToInt((heldMs - FirstRepeatMs) / RepeatEveryMs);
            return Mathf.Min(total, Mathf.Max(eased, steady));
        }

        /// <summary>Advances a hold by 'dtMs': the units to trade now. 'heldMs' (0) and 'done' (1, the press traded one) are
        /// the caller's state; 'total' the stack counted at the press.</summary>
        public static int Step(ref float heldMs, ref int done, int total, float dtMs)
        {
            heldMs += dtMs;
            int due = Due(heldMs, total);
            if (due <= done) return 0;
            int n = due - done;
            done = due;
            return n;
        }

        /// <summary>How far the hold is toward the whole stack, 0..1 (the arrow's fill).</summary>
        public static float Progress(float heldMs) => Mathf.Clamp01(heldMs / WholeStackMs);

        /// <summary>The arrow's fill bar (HangarWindow.uss .trade-arrow-fill), behind its label.</summary>
        public static VisualElement AddFill(VisualElement arrow)
        {
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("trade-arrow-fill");
            arrow.Insert(0, fill);
            return fill;
        }

        public static void SetFill(VisualElement fill, float progress)
        {
            if (fill != null) fill.style.width = Length.Percent(progress * 100f);
        }
    }
}
