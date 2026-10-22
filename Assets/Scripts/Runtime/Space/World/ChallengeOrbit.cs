// ChallengeOrbit.cs
// The Supernova Challenge's orbit (Reference/research/supernova_challenge.md 3-7; the rules and score in
// Data.SupernovaChallenge): Level::createMission's 0xb7 branch 0xbed0a and Level::updateMissionOrbit 0xd4bd4 in Luur's orbit.
//   [0-7]   8 Specters (createShip(10, 0, 0x2c) at (-55000, 0, -35000) +- 20000 per axis, always enemy, hull 250 (setMaxHitpoints),
//           no loot, no route: they go for the player)
//   [8]     the burning "Station Platform" (0x495d, text 3208) at the origin, turned (0, -2.35619, 0)
//   [9]     a Terran carrier (0x4974) at (-70000, -5000, -35000)
//           both radar-hidden (KIPlayer+0x70), race 10 (the Specters leave them alone), 9 999 999 hull: scenery
//   [10-13] 4 unarmed Rhinos (race 0, ship 0x33) shuttling carrier -> (30000, 1000, 40000) -> platform (Route(..., 9), like 102)
// Every 7500 ms each Specter whose explosion is over comes back at full hull 50 000-80 000 units from the player on x and z,
// 10 000-20 000 above or below (updateMissionOrbit). The player starts at (-30000, 5000, -100000) (0xbf102). The round ends
// after 181 000 ms; the original's success page then restarts it (MGame::OnTouchEnd). Remake: the end asks "Play again?" (300)
// with the score, the kills and the highscore (294 / 295 / 296; the original's dead ChoiceWindow 1.2 had that shape), also
// after the player's death; the Specters fire their ordinary guns (assignGuns' damage 1 then the Specters' x0.7 made 0 in the
// Android build: harmless). Remake pick: the player faces the Specters' spawn point (the original's heading wasn't traced).

using System.Collections.Generic;
using GoF2Remake.Data;
using GoF2Remake.Flight;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoF2Remake.World
{
    public class ChallengeOrbit : MonoBehaviour
    {
        const float M = 0.05f;
        const float WaveMs = 7500f;        // Level+0x174's period (0x1d4c)
        const float EndDelayMs = 1500f;    // remake: the question after the time-out (the original's successCheck ran every ~5 s)
        const float DeathDelayMs = 7000f;  // the game over's "Tap to ..." moment (FlightHud)
        const float SoundVolume = 0.42658f;   // the SV_Mode events' volume (supernova_challenge.md 6.3)

        static readonly Vector3 SpecterPoint = new Vector3(-55000, 0, -35000);
        static readonly Vector3 CarrierPoint = new Vector3(-70000, -5000, -35000);
        static readonly Vector3 PlayerStart = new Vector3(-30000, 5000, -100000);
        static readonly Vector3 ShuttlePoint = new Vector3(30000, 1000, 40000);
        static readonly Vector3[] RhinoStarts =
        {
            new Vector3(20000, -3000, -50000), new Vector3(10000, -7000, -30000), new Vector3(25000, 0, -36000), new Vector3(12000, 6000, -45000),
        };

        SpaceLevel level;
        Traffic traffic;
        readonly List<NpcShip> specters = new List<NpcShip>();
        readonly ShuttleRoutes shuttles = new ShuttleRoutes();
        AudioSource sfx;
        AudioClip[] sounds;
        float waveMs, endMs;
        bool asked;

        /// <summary>The round is over and its question is up or coming (FlightHud leaves the game over's tap alone).</summary>
        public bool Ending => SupernovaChallenge.Over;

        static Vector3 ToUnity(Vector3 game) => new Vector3(game.x, game.y, -game.z) * M;
        static Vector3 ToGame(Vector3 unity) => new Vector3(unity.x, unity.y, -unity.z) / M;
        static Vector3 Jitter() => new Vector3(Random.Range(0, 40000) - 20000, Random.Range(0, 40000) - 20000, Random.Range(0, 40000) - 20000);
        static float Sign() => Random.Range(0, 2) == 0 ? 1f : -1f;

        public void Setup(SpaceLevel spaceLevel, Traffic npcTraffic)
        {
            level = spaceLevel;
            traffic = npcTraffic;
            sounds = SupernovaAssets.Load()?.challengeSounds;
            sfx = gameObject.AddComponent<AudioSource>();
            sfx.playOnAwake = false;
            sfx.spatialBlend = 0f;
            for (int i = 0; i < 8; i++)
            {
                var s = traffic.SpawnShip(new SpawnSpec { group = NpcGroup.Raider, race = Standing.Specter, ship = 44, position = SpecterPoint + Jitter(),
                                                          alwaysEnemy = true, hitpoints = 250, noLoot = true });
                if (s != null) specters.Add(s);
            }
            var platform = Scenery("sn_burning_station_mission_object", Vector3.zero, new Vector3(0f, -2.35619f, 0f), 3208, 111);
            var carrier = Scenery("sn_carrier_terran_1", CarrierPoint, Vector3.zero, -1, 2005);
            for (int i = 0; i < 4; i++)
            {
                var s = traffic.SpawnShip(new SpawnSpec { group = NpcGroup.Raider, race = 0, ship = 51, position = RhinoStarts[i], alwaysFriend = true, noLoot = true });
                if (s == null) continue;
                s.SetOnlyEnemy(null);
                shuttles.Add(new ShuttleRoutes.Shuttle { ship = s, points = new[] { CarrierPoint, ShuttlePoint, Vector3.zero }, dockMs = new[] { 20000f, 0f, 20000f },
                                                         dropOff = -1, leg = i % 3, rhino = true,
                                                         dockAt = new[] { carrier?.transform, null, platform?.transform }, dockSets = new[] { 5, 0, 6 } });
            }
            traffic.ConnectPlayers();
            traffic.ShipDied += OnShipDied;
            var start = ToUnity(PlayerStart);
            level.MovePlayer(start, Quaternion.LookRotation((ToUnity(SpecterPoint) - start).normalized, Vector3.up));
            Debug.Log($"ChallengeOrbit: round {SupernovaChallenge.Round}, {specters.Count} Specters, {shuttles.All.Count} Rhinos");
        }

        /// <summary>Level::createStaticObject with the challenge's settings: never moving, 9 999 999 hull, race 10, off the radar
        /// (+0x70 = 1: no marker, lock or docking), its collision volume.</summary>
        NpcShip Scenery(string assembly, Vector3 at, Vector3 rotation, int nameText, int collision)
        {
            var s = traffic.SpawnShip(new SpawnSpec
            {
                group = NpcGroup.Special, race = Standing.Specter, ship = -1, position = at, fixedObject = assembly, rotation = rotation,
                nameText = nameText, alwaysFriend = true, hitpoints = 9999999, noLoot = true, stationary = true, hitRadius = 6000f, collisionId = collision,
            });
            if (s != null) s.Target.untargetable = true;
            return s;
        }

        void OnDestroy()
        {
            if (traffic != null) traffic.ShipDied -= OnShipDied;
        }

        /// <summary>Level::enemyDied(i, byNpc = false): the round's kills (Level+0x24); the score and combo (remake).</summary>
        void OnShipDied(NpcShip ship, bool byPlayer)
        {
            if (byPlayer && specters.Contains(ship)) SupernovaChallenge.OnKill();
        }

        void Update()
        {
            float dtMs = Time.deltaTime * 1000f;
            bool dead = level.Health != null && level.Health.Dead;
            if (SupernovaChallenge.Over)
            {
                endMs += Time.unscaledDeltaTime * 1000f;
                if (!asked && endMs >= (dead ? DeathDelayMs : EndDelayMs)) Ask();
                return;
            }
            if (dead) { End(); return; }
            if (dtMs <= 0f) return;
            SupernovaChallenge.Tick(dtMs);
            PlaySound(SupernovaChallenge.TakeSound());
            shuttles.Update(dtMs);
            UpdateWaves(dtMs);
            if (SupernovaChallenge.TimeUp) End();
        }

        /// <summary>Level::updateMissionOrbit: every 7500 ms the Specters whose death is over come back around the player.</summary>
        void UpdateWaves(float dtMs)
        {
            waveMs += dtMs;
            if (waveMs <= WaveMs) return;
            waveMs = 0f;
            var player = ToGame(level.Player.transform.position);
            foreach (var s in specters)
            {
                if (s == null || !s.Gone) continue;
                var at = player + new Vector3(Sign() * (Random.Range(0, 30000) + 50000), Sign() * (Random.Range(0, 10000) + 10000), Sign() * (Random.Range(0, 30000) + 50000));
                s.Revive(ToUnity(at));
            }
        }

        /// <summary>The round is over: its score is final (a running combo paid), the player can't be hurt any more.</summary>
        void End()
        {
            SupernovaChallenge.Finish();
            PlaySound(SupernovaChallenge.TakeSound());
            endMs = 0f;
            if (level.Health != null && level.Health.Target != null && !level.Health.Dead) level.Health.Target.invulnerable = true;
            if (level.Weapons != null) level.Weapons.Blocked = true;
        }

        /// <summary>The end of a round: the score, the kills and the highscore, "Play again?" (300): Yes restarts with Patala
        /// x10 (MGame::OnTouchEnd's 0xb7 restart), No goes to the main menu (the dead ChoiceWindow's answer 1).</summary>
        void Ask()
        {
            asked = true;
            string text = $"{Localization.Get(296)}: {SupernovaChallenge.Digits(SupernovaChallenge.Score)}\n"
                        + $"{Localization.Get(176)}: {SupernovaChallenge.Kills}\n"
                        + (SupernovaChallenge.NewHighscore ? Localization.Get(295) : $"{Localization.Get(294)}: {SupernovaChallenge.Digits(SupernovaChallenge.Highscore)}")
                        + "\n\n" + Localization.Get(300);
            traffic.Ask(text, Localization.Get(134), Localization.Get(135),
                        () => SceneManager.LoadScene(SupernovaChallenge.Start(true)),
                        () => SceneManager.LoadScene("MainMenu"));
        }

        void PlaySound(int n)
        {
            if (n <= 0 || sounds == null || n > sounds.Length || sounds[n - 1] == null) return;
            sfx.PlayOneShot(sounds[n - 1], SoundVolume * Sfx.EventGain * Settings.SfxVolume);
        }
    }
}
