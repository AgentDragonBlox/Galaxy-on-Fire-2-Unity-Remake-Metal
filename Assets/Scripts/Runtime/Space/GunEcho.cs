// GunEcho.cs
// The echo: a weapon whose shot hits (a bullet's hit, a scatter burst, a homing missile at its goal) sets off a second,
// weaker blast at the same point a little later (remake, for mods: items.json stats echoDelayMs / echoPercent /
// echoRadius / echoExplosion, Modding/README.md "Echo blasts"). Plain C#: Gun schedules the blast and fires it when it is
// due; WeaponSystem draws the explosion.

using GoF2Remake.Data;
using UnityEngine;

namespace GoF2Remake.Flight
{
    public sealed class GunEcho
    {
        /// <summary>Attr 127 (ms): the echo goes off this long after the hit. 0 = no echo.</summary>
        public const int DelayAttr = 127, PercentAttr = 128, RadiusAttr = 129, ExplosionAttr = 130;
        /// <summary>Attr 130: the Explosion type the echo shows (0 ship, 7 EMP, 8-10 scatter, 11 shock blast, 13 fireworks).</summary>
        public const int DefaultExplosion = 8;
        public const float DefaultPercent = 50f, MaxDelayMs = 30000f;
        /// <summary>Blasts still waiting at once; a burst of more than this drops the newest echoes.</summary>
        public const int Capacity = 16;

        public readonly float delayMs, percent, radiusUnits;
        public readonly int explosion;

        struct Pending { public float leftMs; public Vector3 point; }
        readonly Pending[] pending = new Pending[Capacity];
        int count;

        /// <summary>The echo's settings from an item's stats (ItemData.Attr); null when it has no delay.</summary>
        public static GunEcho From(ItemData item)
        {
            float delay = Mathf.Clamp(item.Attr(DelayAttr), 0f, MaxDelayMs);
            return delay > 0f ? new GunEcho(delay, item.Attr(PercentAttr, (int)DefaultPercent), item.Attr(RadiusAttr),
                                            item.Attr(ExplosionAttr, DefaultExplosion)) : null;
        }

        public GunEcho(float delayMs, float percent, float radiusUnits, int explosion)
        {
            this.delayMs = Mathf.Clamp(delayMs, 0f, MaxDelayMs);
            this.percent = percent <= 0f ? DefaultPercent : Mathf.Clamp(percent, 1f, 1000f);
            this.radiusUnits = Mathf.Max(0f, radiusUnits);
            this.explosion = explosion;
        }

        /// <summary>The echo's damage and size as a factor of the shot's (percent / 100).</summary>
        public float Scale => percent / 100f;

        /// <summary>Blasts waiting to go off.</summary>
        public int Waiting => count;

        /// <summary>A shot hit at 'point': its echo goes off delayMs from now.</summary>
        public void Schedule(Vector3 point)
        {
            if (count >= Capacity) return;
            pending[count++] = new Pending { leftMs = delayMs, point = point };
        }

        /// <summary>Advances the waiting echoes by dt; 'due' is called with each point that goes off now, in order.</summary>
        public void Tick(float dtMs, System.Action<Vector3> due)
        {
            for (int i = 0; i < count;)
            {
                pending[i].leftMs -= dtMs;
                if (pending[i].leftMs > 0f) { i++; continue; }
                var at = pending[i].point;
                // Remove it by moving the last one into its place (order does not matter for blasts).
                pending[i] = pending[--count];
                due(at);
            }
        }

        public void Clear() => count = 0;
    }
}
