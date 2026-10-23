// EventWormhole.cs
// Remake events: an event's / mission's wormhole on this player's game (/wormhole, the mission step Wormhole, the event
// graphs' Open / Close Wormhole; NetAdmin.Order.Wormhole): in that station's orbit a wormhole like the Void's opens at the
// given game coordinates (SpaceLevel.OpenEventWormhole: it grows in 3 s, pulls within 2 km, marked "Wormhole"), and flying
// in takes the player to its destination (any station, or the Void's home orbit), a stream-out arrival with a wormhole
// closing behind. It stays open until "/wormhole close", its seconds run out (game time: it waits while the game is
// paused), the event's end or the session's; applied again whenever this player enters that orbit. One per player: a new
// one replaces it.

using System.Globalization;
using GoF2Remake.World;
using UnityEngine;

namespace GoF2Remake.Events
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public sealed class EventWormhole : MonoBehaviour
    {
        static EventWormhole instance;
        int station, destination;
        bool open;
        Vector3 point;          // game units
        float leftMs = -1f;     // < 0: until closed
        SpaceLevel appliedTo;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => instance = null;

        /// <summary>This game's open event wormhole: its orbit (-2: none; -1 is the Void's).</summary>
        public static int OpenAt => instance != null && instance.open ? instance.station : -2;

        /// <summary>The server's order: "station x y z destination seconds" (game units; seconds 0 = until closed), or "close".</summary>
        internal static void Apply(string text)
        {
            if (instance == null)
            {
                var go = new GameObject("EventWormhole");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<EventWormhole>();
            }
            instance.Close();
            if (TryParse(text, out int s, out var p, out int to, out float seconds))
            {
                instance.open = true;
                instance.station = s;
                instance.point = p;
                instance.destination = to;
                instance.leftMs = seconds > 0f ? seconds * 1000f : -1f;
            }
        }

        /// <summary>The order's text (see Apply).</summary>
        public static bool TryParse(string text, out int station, out Vector3 point, out int destination, out float seconds)
        {
            station = destination = 0;
            point = Vector3.zero;
            seconds = 0f;
            var f = (text ?? "").Split(' ');
            if (f.Length != 6 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out station)
                || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out destination)) return false;
            if (!Num(f[1], out float x) || !Num(f[2], out float y) || !Num(f[3], out float z) || !Num(f[5], out seconds)) return false;
            point = new Vector3(x, y, z);
            return true;
        }

        static bool Num(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) < 1e7f;

        void Close()
        {
            if (appliedTo != null) appliedTo.CloseEventWormhole();
            appliedTo = null;
            open = false;
        }

        void Update()
        {
            if (!EventHost.ScreensActive) { if (open || appliedTo != null) Close(); return; }
            if (!open) return;
            if (leftMs >= 0f && (leftMs -= Time.deltaTime * 1000f) <= 0f) { Close(); return; }
            var level = appliedTo != null ? appliedTo : FindAnyObjectByType<SpaceLevel>();
            if (level == null || level.Player == null || level.Layout == null) { appliedTo = null; return; }
            if (level.Layout.stationIndex != station || level.Leaving) return;
            if (appliedTo != level)
            {
                level.OpenEventWormhole(point, destination);
                appliedTo = level;
            }
        }
    }
}
