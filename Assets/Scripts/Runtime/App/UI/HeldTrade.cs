// HeldTrade.cs
// Remake: how a held sell / buy arrow repeats (the hangar's shop and Kaamo storage, the blueprint ingredients, the carrier's
// shop). The original (HangarWindow::update) repeats after 200 ms, every 30 ms once held 1.5 s, 5 units at a time after 4 s;
// the remake's held arrow is also the Sell all / Buy all (the user's request: the separate buttons went): unit by unit,
// faster and faster, the whole stack (counted at the press) gone after two seconds of holding (an ease-in, t^2), never
// slower than one unit per 120 ms after the first 200 ms, so a small stack doesn't crawl; from the second unit on a bar
// fills the arrow with the share of the stack traded (a click, one unit, shows none).
// Every unit plays the buy / sell button sound (Tick; the original's repeats were silent, only a tap sounded), its pitch
// rising from StartPitch to MaxPitch (capped) as the stack goes, at most one per MinTickMs when several go in a frame.

using UnityEngine;
using UnityEngine.UIElements;

namespace GoF2Remake.UI
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class HeldTrade
    {
        public const float FirstRepeatMs = 200f, RepeatEveryMs = 120f, WholeStackMs = 2000f;
        public const float StartPitch = 1f, MaxPitch = 1.5f, MinTickMs = 30f;

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

        /// <summary>The share of the stack traded, 0..1 (the arrow's fill): 'done' of the 'total' counted at the press. Not the
        /// time held: a small stack, or one the hold's room cuts short, is done well before two seconds. Nothing until the
        /// second unit has gone: a click trades one unit and shows no bar.</summary>
        public static float Progress(int done, int total) => done < 2 || total <= 0 ? 0f : Mathf.Clamp01((float)done / total);

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

        /// <summary>The pitch of a unit's sound once 'done' of 'total' are traded (the press's unit: StartPitch).</summary>
        public static float Pitch(int done, int total) =>
            total <= 1 ? StartPitch : Mathf.Lerp(StartPitch, MaxPitch, Mathf.Clamp01((done - 1f) / (total - 1f)));

        const int Voices = 4;
        static AudioSource[] voices;
        static int nextVoice;
        static float lastTick = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { voices = null; nextVoice = 0; lastTick = -1f; }

        /// <summary>A traded unit's button sound at Pitch(done, total), on voices of its own (a pitched PlayOneShot would bend
        /// every sound of a shared source); 2D, through a pause (the carrier's window pauses the game).</summary>
        public static void Tick(AudioClip clip, int done, int total)
        {
            if (clip == null) return;
            float now = Time.unscaledTime;
            if (lastTick >= 0f && (now - lastTick) * 1000f < MinTickMs) return;
            lastTick = now;
            if (voices == null || voices[0] == null)
            {
                var go = new GameObject("HeldTradeSound") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(go);
                voices = new AudioSource[Voices];
                for (int i = 0; i < Voices; i++)
                {
                    var s = go.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.spatialBlend = 0f;
                    s.ignoreListenerPause = true;
                    voices[i] = s;
                }
            }
            var v = voices[nextVoice];
            nextVoice = (nextVoice + 1) % Voices;
            v.pitch = Pitch(done, total);
            v.PlayOneShot(GoF2Remake.Modding.ModSounds.Get(clip), GoF2Remake.Data.Settings.SfxVolume);
        }
    }
}
