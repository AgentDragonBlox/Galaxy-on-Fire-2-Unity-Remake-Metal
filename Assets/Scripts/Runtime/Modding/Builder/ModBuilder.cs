// ModBuilder.cs
// Remake-only: the in-game Mod Builder's session (main menu > Mods > Mod Builder). Opening a project (Begin) makes a
// throwaway free-play game docked at Dis (70, a Terran hangar) with the project as the only active mod
// (ModManager.SetBuilderProject), and loads the Station scene, where UI.ModBuilderView replaces the station menu: the
// project's ship sits on the turntable, its hardpoints are edited with World.ModBuilderGizmo, its test loadout chosen.
// Test fly (StartTestFlight) is the normal take-off into Dis' orbit with World.ModBuilderRange there: no traffic, a few
// pirates that keep coming, no jumps; docking (or the pause menu's "Back to Mod Builder", or a death) lands back in the
// builder. Nothing of this game is ever saved (SaveGame refuses while Active). End returns to the main menu's projects.
// Plain C# (static: the session outlives the scene changes).

using System.Collections.Generic;
using System.Linq;
using GoF2Remake.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoF2Remake.Modding
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class ModBuilder
    {
        /// <summary>The station whose hangar hosts the builder and whose orbit the test flights use (Dis, Terran).</summary>
        public const int Station = 70;
        /// <summary>The ship shown while the project has none (the Phantom).</summary>
        public const int PlaceholderShip = 10;
        public const string ProjectsPanel = "modBuilderPanel";

        /// <summary>A project is open (the builder's game runs).</summary>
        public static bool Active { get; private set; }
        /// <summary>The open project.</summary>
        public static ModProject Project { get; private set; }
        /// <summary>The ship being worked on (null = the project has none yet).</summary>
        public static ProjectShip Ship { get; set; }
        /// <summary>A test flight runs (between Test fly and landing back in the builder).</summary>
        public static bool TestFlying { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Active = TestFlying = false; Project = null; Ship = null; }

        /// <summary>Opens 'project': the builder's game; the caller loads the Station scene (after the mods have loaded).</summary>
        public static void Begin(ModProject project)
        {
            Session.ResetNewGame();
            Session.UseCompletedWorld();   // free play: every original ship and item exists, no story
            Session.Difficulty = Session.DifficultyNormal;
            Session.Economy = Economy.Android;
            Session.StationIndex = Station;
            Session.LaunchedFromStation = false;
            Session.DockedFromSpace = false;
            Session.Credits = 0;
            Cheats.ClearGranted();
            Project = project;
            Active = true;
            TestFlying = false;
            ModManager.SetBuilderProject(project.Folder);
            Ship = project.Ships.FirstOrDefault(s => s.Ref == project.LastShip) ?? project.Ships.FirstOrDefault();
            ApplyShip();
            Debug.Log($"Mod Builder: opened \"{project.Name}\" ({project.Folder}), {project.Ships.Count} ship(s)");
        }

        /// <summary>Closes the project and returns to the main menu's project list.</summary>
        public static void End()
        {
            if (Project != null) Project.Save();
            Active = TestFlying = false;
            Project = null;
            Ship = null;
            Cheats.ClearGranted();
            ModManager.SetBuilderProject(null);
            Session.ResetNewGame();
            UI.MainMenu.OpenPanelOnStart = ProjectsPanel;
            SceneManager.LoadScene("MainMenu");
        }

        /// <summary>The game's ship number of a project ship: an override's original, a new ship's mod number (-1 while the
        /// project can't be read).</summary>
        public static int IndexOf(ProjectShip s)
        {
            if (s == null || Project == null) return -1;
            if (s.IsOverride) return s.OverrideIndex;
            return ModContent.ShipIndexOf(Project.Id + ":" + s.LocalId);
        }

        /// <summary>The ship number on the turntable: the current ship's, else the placeholder.</summary>
        public static int CurrentIndex
        {
            get
            {
                int i = IndexOf(Ship);
                return i >= 0 && Database.Load().Ship(i) != null ? i : PlaceholderShip;
            }
        }

        /// <summary>The project's files changed: the game reads them again (the ship models rebuild in the background).</summary>
        public static void Reload()
        {
            if (Project == null) return;
            Project.Save();
            ModManager.SetBuilderProject(Project.Folder);
        }

        /// <summary>The current ship and its test loadout into the game's state (the flown ship, its equipment).</summary>
        public static void ApplyShip()
        {
            var db = Database.Load();
            Session.ShipIndex = CurrentIndex;
            var loadout = Ship != null ? Project.Loadout(Ship) : null;
            if (loadout == null && Ship != null) { loadout = DefaultLoadout(db, Session.ShipIndex); Project.SetLoadout(Ship, loadout); }
            Session.Equipment = (loadout ?? new List<ItemStack>()).Where(i => db.Item(i.item) != null).Select(i => i.Clone()).ToList();
            Session.Cargo = new List<ItemStack>();
            Hangar.FitToSlots(db);
            Session.Cargo.Clear();   // what didn't fit isn't wanted in the hold
            Session.SelectedSecondary = -1;
            Session.PlayerHull = Session.PlayerArmor = -1;
            Session.PlayerShield = -1f;
        }

        // ---- loadouts -------------------------------------------------------------------------------------------

        /// <summary>Secondary weapons are mounted with this much ammo.</summary>
        public const int Ammo = 50;

        /// <summary>Items a slot kind (0 primary, 1 secondary, 2 turret, 3 equipment) can take, the original ones, sorted by
        /// category and price.</summary>
        public static List<ItemData> ItemsFor(Database db, int kind) =>
            db.Items.Where(i => i.TypeId == kind && !Session.Unsaleable.Contains(i.index) && !ModContent.IsMissingItem(i.index) && i.maxPrice > 0)
                    .OrderBy(i => i.categoryId).ThenBy(i => i.maxPrice).ToList();

        /// <summary>A loadout to start from: the dearest primary, secondary and turret in every slot, and a shield, armor,
        /// booster and scanner while the equipment slots last.</summary>
        public static List<ItemStack> DefaultLoadout(Database db, int ship)
        {
            var l = new List<ItemStack>();
            var s = db.Ship(ship)?.slots;
            if (s == null) return l;
            ItemData Best(int kind, string category = null) =>
                ItemsFor(db, kind).Where(i => category == null || i.category == category).OrderByDescending(i => i.maxPrice).FirstOrDefault();
            var primary = ItemsFor(db, 0).Where(i => i.category == "Laser" || i.category == "Blaster").OrderByDescending(i => i.maxPrice).FirstOrDefault() ?? Best(0);
            for (int i = 0; i < s.primary && primary != null; i++) l.Add(new ItemStack(primary.index, 1));
            var secondary = Best(1, "Missile") ?? Best(1);
            for (int i = 0; i < s.secondary && secondary != null; i++) l.Add(new ItemStack(secondary.index, Ammo));
            var turret = Best(2, "Turret");
            for (int i = 0; i < s.turret && turret != null; i++) l.Add(new ItemStack(turret.index, 1));
            int eq = 0;
            foreach (var cat in new[] { "Shield", "Armor", "Booster", "Scanner", "Tractor beam", "Steering nozzle" })
            {
                if (eq >= s.equipment) break;
                var e = Best(3, cat);
                if (e != null) { l.Add(new ItemStack(e.index, 1)); eq++; }
            }
            return l;
        }

        // ---- test flights ---------------------------------------------------------------------------------------

        /// <summary>God mode for the test flights (builder.json).</summary>
        public static bool TestGodMode { get => Project?.Flag("godMode", true) ?? true; set => Project?.SetFlag("godMode", value); }
        /// <summary>Infinite ammo for the test flights (builder.json).</summary>
        public static bool TestInfiniteAmmo { get => Project?.Flag("infiniteAmmo", true) ?? true; set => Project?.SetFlag("infiniteAmmo", value); }

        /// <summary>Test fly: the ship and its loadout set, the cheats granted; the caller takes off (StationLevel.Launch),
        /// which loads Dis' orbit.</summary>
        public static void StartTestFlight()
        {
            Project.Save();
            ApplyShip();
            Session.StationIndex = Station;
            Cheats.ClearGranted();
            Cheats.Grant("godMode", TestGodMode);
            Cheats.Grant("infiniteAmmo", TestInfiniteAmmo);
            TestFlying = true;
            Debug.Log($"Mod Builder: test flight in ship {Session.ShipIndex}");
        }

        /// <summary>Back in the builder's hangar from a test flight ('flyIn': the ship flies in through the forcefield).</summary>
        public static void EndTestFlight(bool flyIn)
        {
            TestFlying = false;
            Cheats.ClearGranted();
            Session.StationIndex = Station;
            Session.DockedFromSpace = flyIn;
            SceneManager.LoadScene("Station");
        }

        /// <summary>The scene the builder was left in has landed: whatever the flight did is undone (ammo spent, damage).</summary>
        public static void OnDocked()
        {
            TestFlying = false;
            Cheats.ClearGranted();
            ApplyShip();
        }
    }
}
