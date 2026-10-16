// EngineMaterialGlow.cs
// Remake mods: the materials a mod ship marks "engine" (ships.json / shipkits.json "materials": the nozzles' own glowing
// surfaces, EVERSPACE 2's nozzle rings and grilles) follow the engines: dark at idle, full with the thrust (EngineDrive: the
// player's throttle, else the ship's measured speed), as EVERSPACE 2's do (the user's shots of a Raptor at rest: no glow;
// they kept 18 % at idle before). On a
// ship a station spawns (StationLevel.SpawnShip sets followEngine) they also follow the engine glow part
// (AssembledObject.playerVariantParts' engine_glow): dark on the turntable and the pads, lit through a hangar flight.
// Materials marked "boost" (boost vents) glow only while boosting, with the boost (EngineDrive above 1). Changes ease over
// a quarter second. The materials are this ship's own copies (renderer.materials), so other ships of the model keep theirs.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoF2Remake.Modding
{
    public class EngineMaterialGlow : MonoBehaviour
    {
        public string[] materials = new string[0];   // material names (contains, case-insensitive)
        public string[] boostMaterials = new string[0];   // ... glowing only while boosting
        public bool followEngine;

        const float FadePerSecond = 4f;
        readonly List<(Material mat, Color emission)> glowing = new List<(Material, Color)>();
        readonly List<(Material mat, Color emission)> boosting = new List<(Material, Color)>();
        float boostLevel = -1f;
        GameObject engineGlow;
        Visuals.EngineDrive drive;
        float level = -1f;
        bool ready;

        /// <summary>Adds the component to a ship template for the named materials (none: nothing added).</summary>
        public static void Add(GameObject ship, IEnumerable<string> names, IEnumerable<string> boostNames = null)
        {
            var list = names?.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray() ?? new string[0];
            var blist = boostNames?.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray() ?? new string[0];
            if (ship == null || list.Length + blist.Length == 0) return;
            var c = ship.GetComponent<EngineMaterialGlow>();
            if (c == null) c = ship.AddComponent<EngineMaterialGlow>();
            c.materials = c.materials.Concat(list).Distinct().ToArray();
            c.boostMaterials = c.boostMaterials.Concat(blist).Distinct().ToArray();
        }

        void Setup()
        {
            ready = true;
            var asm = GetComponent<Visuals.AssembledObject>();
            if (asm != null && asm.playerVariantParts != null)
            {
                engineGlow = asm.playerVariantParts.FirstOrDefault(p => p != null && p.name.Contains("engine_glow"));
                if (engineGlow == null) engineGlow = asm.playerVariantParts.FirstOrDefault(p => p != null);
            }
            Collect(materials, glowing);
            Collect(boostMaterials, boosting);
        }

        void Collect(string[] names, List<(Material, Color)> into)
        {
            var wanted = names.Select(m => m.ToLowerInvariant()).ToArray();
            if (wanted.Length == 0) return;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                if (!shared.Any(m => m != null && Matches(m.name, wanted))) continue;
                var own = r.materials;   // this ship's copies
                for (int i = 0; i < own.Length; i++)
                    if (own[i] != null && i < shared.Length && shared[i] != null && Matches(shared[i].name, wanted) && own[i].HasProperty("_EmissionColor"))
                        into.Add((own[i], own[i].GetColor("_EmissionColor")));
            }
        }

        static bool Matches(string name, string[] wanted)
        {
            string n = name.ToLowerInvariant();
            return wanted.Any(w => n.Contains(w));
        }

        void Update()
        {
            if (!ready) { Setup(); drive = new Visuals.EngineDrive(transform); }
            if (glowing.Count == 0 && boosting.Count == 0) return;
            float d = drive.Sample(Time.deltaTime, followEngine ? engineGlow : null);
            bool off = (followEngine && !On) || drive.Off;   // free flight with no thrust: dark like a parked ship
            Ease(ref level, off ? 0f : Mathf.Clamp01(d), glowing);
            Ease(ref boostLevel, off ? 0f : Mathf.Clamp01(d - 1f), boosting);
        }

        static void Ease(ref float current, float target, List<(Material mat, Color emission)> mats)
        {
            if (mats.Count == 0) return;
            if (current < 0f) current = target;
            else if (Mathf.Approximately(current, target)) return;
            else current = Mathf.MoveTowards(current, target, Time.unscaledDeltaTime * FadePerSecond);
            foreach (var (mat, emission) in mats) if (mat != null) mat.SetColor("_EmissionColor", emission * current);
        }

        bool On => engineGlow == null || engineGlow.activeInHierarchy;

        void OnDestroy()
        {
            foreach (var (mat, _) in glowing) if (mat != null) Destroy(mat);
            foreach (var (mat, _) in boosting) if (mat != null) Destroy(mat);
        }
    }
}
