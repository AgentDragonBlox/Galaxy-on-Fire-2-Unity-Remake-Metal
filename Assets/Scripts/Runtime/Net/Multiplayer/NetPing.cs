// NetPing.cs
// Remake-only: how far away a listed game is (the server browser's ping and region). An online session runs through one
// Unity Relay allocation, which lives in a region the host publishes with its listing (NetLobby's "region" key, from
// Allocation.Region); every player's traffic goes through that region's relay servers, so the round trip to them is what
// a row's ping shows. The measurement is Unity's QoS service (com.unity.services.qos, which the Multiplayer package
// brings and Multiplay / Matchmaker use for the same thing): it sends a short UDP burst to each region's QoS servers and
// reports the average round trip and the packet loss. One measurement serves every game in that region, so the browser
// measures the regions of the games it lists, at most once a minute each, while it is open; a game from a build before
// the region key has none and its row shows no ping.
// The number is the player's own leg (player -> relay), not the whole path through the host, and the dot reads it as
// Good / Fair / Poor.

using System;
using System.Collections.Generic;
using Unity.Services.Qos;
using UnityEngine;

namespace GoF2Remake.Multiplayer
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class NetPing
    {
        /// <summary>The dot: up to Good green, up to Fair amber, above it red.</summary>
        public const int Good = 80, Fair = 160;
        const float RemeasureSeconds = 60f, AfterFailureSeconds = 300f;
        const string RelayService = "relay";

        struct Reading { public int ms; public float at; }
        static readonly Dictionary<string, Reading> readings = new Dictionary<string, Reading>();
        static bool running;
        static float failedAt = -9999f;

        /// <summary>Goes up whenever a measurement lands: the browser writes its rows' pings again (they are not part of a
        /// row's key, so a new reading never rebuilds the list under the player's focus).</summary>
        public static int Revision { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            readings.Clear();
            running = false;
            failedAt = -9999f;
            Revision = 0;
        }

        /// <summary>The round trip to 'region' in milliseconds; -1 = not measured (yet), or nothing answered there.</summary>
        public static int Of(string region) =>
            !string.IsNullOrEmpty(region) && readings.TryGetValue(region, out var r) ? r.ms : -1;

        /// <summary>0 good, 1 fair, 2 poor, -1 for a ping that isn't known.</summary>
        public static int Quality(int ms) => ms < 0 ? -1 : ms <= Good ? 0 : ms <= Fair ? 1 : 2;

        /// <summary>Measures the regions among 'regions' with no reading yet or a stale one, all in one QoS run; nothing
        /// happens while a run is going, or for a while after one failed (no QoS where the game runs).</summary>
        public static void Measure(IEnumerable<string> regions)
        {
            if (running || regions == null) return;
            float now = Time.realtimeSinceStartup;
            if (now - failedAt < AfterFailureSeconds) return;
            var want = new List<string>();
            foreach (string r in regions)
            {
                if (string.IsNullOrEmpty(r) || want.Contains(r)) continue;
                if (readings.TryGetValue(r, out var had) && now - had.at < RemeasureSeconds) continue;
                want.Add(r);
            }
            if (want.Count == 0) return;
            running = true;
            Run(want);
        }

        static async void Run(List<string> want)
        {
            try
            {
                var qos = QosService.Instance;
                if (qos == null) throw new Exception("the QoS service isn't running");
                var results = await qos.GetSortedQosResultsAsync(RelayService, want);
                float now = Time.realtimeSinceStartup;
                // A region nothing answered from (every packet lost) keeps its place with no number, so it isn't measured
                // again for the next minute either.
                foreach (string r in want) readings[r] = new Reading { ms = -1, at = now };
                if (results != null)
                    foreach (var r in results)
                        if (!string.IsNullOrEmpty(r.Region) && r.PacketLossPercent < 99.9f)
                            readings[r.Region] = new Reading { ms = Mathf.Max(0, r.AverageLatencyMs), at = now };
                Revision++;
            }
            catch (Exception e)
            {
                failedAt = Time.realtimeSinceStartup;
                Debug.LogWarning("NetPing: measuring the regions failed: " + e.Message);
            }
            finally
            {
                running = false;
            }
        }

        // ---- the region's name ------------------------------------------------------------------------------------

        // Relay's region ids read "<area>-<side><number>" ("europe-west4", "asia-southeast1", "us-east4"); the number tells
        // one data centre from another in the same corner of the world, which the ping already says, so the name leaves it
        // out. Place names, like the build version, are not translated.
        static readonly string[,] Areas =
        {
            { "europe", "EU" }, { "us", "US" }, { "northamerica", "N. America" }, { "southamerica", "S. America" },
            { "asia", "Asia" }, { "australia", "Australia" }, { "africa", "Africa" }, { "me", "Middle East" },
        };
        static readonly string[,] Sides =
        {
            { "northeast", "Northeast" }, { "southeast", "Southeast" }, { "northwest", "Northwest" }, { "southwest", "Southwest" },
            { "central", "Central" }, { "north", "North" }, { "south", "South" }, { "east", "East" }, { "west", "West" },
        };

        /// <summary>A Relay region id as a short name: "europe-west4" -> "EU West"; an id of another shape is given back
        /// with its words capitalised.</summary>
        public static string RegionName(string id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0) return "";
            var parts = id.Split('-');
            string area = Lookup(Areas, parts[0]) ?? Capitalise(parts[0]);
            if (parts.Length < 2) return area;
            string word = parts[1].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            string side = Lookup(Sides, word) ?? Capitalise(word);
            return side.Length == 0 ? area : area + " " + side;
        }

        static string Lookup(string[,] table, string key)
        {
            for (int i = 0; i < table.GetLength(0); i++) if (table[i, 0] == key) return table[i, 1];
            return null;
        }

        static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
