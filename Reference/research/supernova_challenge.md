# Supernova Challenge (mission type 0xb7)

The "Supernova Challenge" arcade mode of the Supernova add-on, reconstructed from the decompiled Android HD build 2.0.16.
Private research for the remake, do not publish. Game units, milliseconds, Ghidra addresses (image base 0x10000).
Game -> Unity: `(x, y, -z) * 0.05`. Text IDs refer to `Assets/Localization/text_en.json` (IDs and short paraphrases
only).

Sources: `MenuTouchWindow::startSupernovaChallenge` 0x154238, `Level::createMission` 0xbda70 (the 0xb7 branch at 0xbed0a),
`Level::updateMissionOrbit` 0xd4bd4, `Level::assignGuns` 0xcb638, `Level::createShip` 0xcf83c, `Level::createStaticObject`
0xcda54, `Level::enemyDied` 0xd3f98, `Objective::achieved` 0xa36fc, `MGame::successCheck` 0x1b0620, `MGame::gameOverCheck`
0x1b0d04, `MGame::OnTouchEnd` 0x1a98d8, `MGame::OnTouchBegin`, `MGame::OnUpdate`, `MGame::OnInitialize`, `MGame::reset`,
`MGame::dialogueEvent` 0x1b0498, `DialogueWindow::loadContent` 0x194cd0, `Hud::draw` 0x190d08, `Hud::drawChallengeModeScore`
0x1908bc, `MenuTouchWindow::MenuTouchWindow` 0x14816c, `PlayerFighter::handleCloaking` 0xf0b00, `PlayerFighter::reset`
0xf0644, `Status::getGammaRayDamagePerSecond` 0xba160, `Status::checkForLevelUp` 0xb9978,
`Globals::playMusicAndFadeOutCurrent` 0xf9dc0, `Globals::reportSupernovaChallengeScore` 0xf920c, `BuildResourceList`.
Literal values the decompile lost (the player's start position, the Hud's score / combo maths) were read from the Thumb
disassembly (movw/movt pairs, VFP code).

## 0. Summary

- **Unreachable on Android.** Nothing in the main menu starts it; the only caller of `startSupernovaChallenge` is a
  `MGame::OnTouchEnd` ChoiceWindow branch guarded by `MGame+0x1dc`, which no code ever sets (only cleared in `MGame::MGame`
  / `MGame::reset`, checked in the binary). Its texts 293-300 are referenced by no code.
- **The Android build has the level, but not the score.** The score (`Status+0x180`), combo counter (`Status+0x188`) and
  combo timer (`Status+0x17c`) are only reset (`MGame::reset`) and read (`Hud::drawChallengeModeScore`); nothing writes
  them, and `drawChallengeModeScore` itself has no caller (its sprite `Hud+0x4cc` and image `Hud+0x4d0` are never even
  created). `Globals::reportSupernovaChallengeScore` (Game Center) is an empty stub. The SV_Mode sounds are registered
  as sound resources but never played. The scoring rules therefore have to be invented; only the HUD layout and the
  combo-bonus display formula survive (section 6).
- What does work: a timed survival round of **181 000 ms** in Luur's orbit (station 111) at campaign index 0x98, fixed ship
  (Blue Fyre) and loadout, **8 Specters** respawning in waves every 7.5 s, scenery evacuation traffic, the HUD countdown,
  and on time-out a success page "Kills: N" after which the round **restarts** with the secondary missiles refilled.
- Because of `assignGuns` (damage forced to 1 for type 0xb7, then the Specters' x0.7 truncated to an int), the Specters'
  shots do **0 damage** in this build. Only asteroid ramming (20 per hit) and the gamma drain (0.4 / s with the Gamma
  Shield II, not enough to kill in 181 s) can hurt the player. Almost certainly not what the designers intended (see 4.3).

## 1. How it is offered

### 1.1 Main menu

No menu state, card or button of `ModMainMenu` / `MenuTouchWindow` (mode 0) calls `startSupernovaChallenge`
(`mainmenu_notes.md`: the campaign select shows only the GoF2 / Valkyrie / Supernova cards). The menu texts exist:

| Text | English (paraphrase) |
|---|---|
| 293 (0x125) | "Supernova Challenge" |
| 294 (0x126) | "Highscore" |
| 295 (0x127) | "New Highscore!" |
| 296 (0x128) | "Your score" |
| 297 (0x129) | description: recurring waves of enemies, get the highest score within the time limit, shoot ships down consecutively for a combo bonus, an insight into the Supernova add-on; with iOS 6.0 or higher you can challenge friends after each try; ends "Start Supernova Challenge?" |
| 298 (0x12a) | the same without the iOS sentence ("biggest score") |
| 299 (0x12b) | "Challenge a friend" |
| 300 (0x12c) | "Play again?" |

None of 293-300 is passed to `GameText::getText` anywhere in the Android code (checked for hex and decimal literals).
297 vs 298 suggests an iOS-only mode (Game Center friend challenges arrived with iOS 6), with 298 as the other platforms'
variant. Images 6000-6010 (texture 27341 `gof2_challenge_interface`, iphone4 / ipad / ipad_large variants in
`Assets/Textures/textures/`): the atlas holds a "COMBOx" label and the ten digits 0-9 in a grey metal style, i.e. the
challenge HUD font; no code creates these images.

### 1.2 The (dead) end-of-round ChoiceWindow

`MGame::OnTouchEnd` 0x1a98d8, ChoiceWindow branch (`MGame+0xca` set, `+0xc6` clear) when `MGame+0x1dc` != 0:

| ChoiceWindow result | Action |
|---|---|
| 0 | create the pause MenuTouchWindow(1) if missing, then `MenuTouchWindow::startSupernovaChallenge()` (a fresh challenge) |
| 1 | `playMusicAndFadeOutCurrent(2)` (0x91 Space_NoCombat_Void, the menu theme), `MGame+0x54 = 0`, application module 1 (main menu), `Status::resetGame` |
| 2 | nothing (falls to the function's end) |

With `+0x1dc` never set, this is unreachable. The shape matches an end screen "Play again?" (300) with three answers
(play again / back to menu / "Challenge a friend" 299, whose iOS action is gone); inferred, not in the code.

## 2. Starting a round (`MenuTouchWindow::startSupernovaChallenge` 0x154238)

In order:

1. `Status::resetGame()`.
2. `setCurrentCampaignMission(0x98)` (152, the Supernova story's "arrive in the Void" step; only the index is set, no
   `nextCampaignMission` side effects, the campaign mission object stays resetGame's). Consequences: `Status::gameWon`
   (index > 0x2c) is true, so NPC stats use the won-game values; `inSupernovaSystem` holds for system 27 (< 0x9e).
3. Ship `Ship::makeShip(ships[0xb8/4 = 46], race -1)` = **46 Blue Fyre** (hull 270, cargo 125, slots 3 primary / 3
   secondary / 0 turret / 13 equipment, handling 116).
4. Equipment (`Ship::setEquipment(item, slot)`, the slot within the item's kind):

| Kind / slot | Item | Key stats (Android tables) |
|---|---|---|
| primary 0 | 231 Mimung Blaster | damage 16, reload 230 ms, lifetime 1000 ms, speed 20 |
| primary 1 | 230 Mass Driver MD 12 | damage 7, reload 100 ms, lifetime 1500 ms, speed 30 |
| primary 2 | 231 Mimung Blaster | as above |
| equipment 0 | 73 Synchrotron Boost | boost 160, recharge 12 000, duration 5600 |
| equipment 1 | 54 Fluxed Matter Shield | 220, full regen 60 000 ms |
| equipment 2 | 59 T'yol (armor) | 250 |
| equipment 3 | 83 Hiroto Proscan | lock 1800 ms, cargo readout (attr 31 = 1) |
| equipment 4 | 206 Gamma Shield II | attr 52 = 60 (% gamma reduction) |
| equipment 5 | 75 Ketar Repair Bot | |
| equipment 6 | 76 Static Thrust | agility 20 |
| secondary 0 | 216 Patala x5 | cluster missile: damage 90, reload 3000, lifetime 10 000, speed 10 |

5. `Status::setStation(Galaxy::getStation(0x6f))` = **111 Luur** (system 27 Ginoya, the supernova system).
6. `Mission(0xb7, "Client", ImageFactory::createChar(true, 0), 0, reward 0, target station 111, difficulty 1)` (the
   7-argument constructor 0x187ba8: `+0x50` campaign flag 0, `+0x60` visible 1, `+0x48` instant action 0). The client name
   is the literal string "Client" (not a text id). Set as both `Status::setMission` (the level mission, Status+400) and
   `setFreelanceMission`.
7. Hints marked as already shown (`Globals::hints[...] = 1`): 0x08, 0x09, 0x0a, 0x0d, 0x0e, 0x0f, 0x13, 0x15 (wingmen,
   637), 0x17 (secondary weapons, 596), 0x1c (standing, 648), 0x20 (planet jump, 584), 0x21 (jump drive, 590), 0x22 (cloak,
   593), 0x29 (gamma, 600). (Texts per `FlightHints.cs`; 8-0x13 are station / menu hints not mapped here.)
8. `Status::setKills(500)` then `setKills(220)`: kills = 220. With resetGame's zeroes this gives XP 220 + 152 (campaign
   index) = 372 (+ visited stations), **rank 9** by `checkForLevelUp`'s table 0x252b0c (315 <= XP < 385), applied by
   `MGame::OnInitialize`. Rank matters for the Specters' EMP pool, the Rhinos' and the carrier's hull (below), not for the
   Specters' hull (fixed 250).
9. `FModSound::stop(current)`, `switch_to_target_setting = -1` (OnInitialize then keeps the music), `enterSpaceLounge = 0`,
   `playMusicAndFadeOutCurrent(1)`: in the supernova system with the mission's target station = the current station and
   index >= 0x6a this is **0x8c2 = 2242 `GOF2_Mission_102_CutScene_Loop_2`** (DLC2_MUSIC).
10. `Status+0x5c / +0x60 / +0x64 = -1` (shield / armor / hull "keep" values: the player starts full). Gamma (`+0x68`) is
    100 from resetGame.
11. `ApplicationManager::SetCurrentApplicationModule(2)` = the flight level (MGame), no docking or station first.

The difficulty (`options+0x2c`) is left as it was (whatever the last new game chose).

## 3. The orbit (`Level::createMission`, 0xb7 branch at 0xbed0a)

`Level::init` (level type 3, non-campaign mission) calls `createMission`, then asteroids / gas clouds as usual for the
orbit (`createAsteroids`, `createGasClouds`), `createStaticObjects`, sentry guns, fighter turrets, wingmen (none),
`assignGuns`, `connectPlayers`. The 0xb7 branch replaces the enemies array `Level+0xf8` with a 14-entry array; there is
no other traffic. Luur at index 0x98 is an empty orbit in the remake's terms (no docking, no station target;
`OrbitLayout.IsEmptyOrbit`: 111 above 0x5d), and the level mission refuses docking / gate / planet jumps / Khador with 525
like any mission orbit.

### 3.1 Array layout

| Index | Object | Setup |
|---|---|---|
| 0-7 | 8 Specters | `createShip(race 10, kind 0 = fighter, ship 0x2c = 44 Specter, waypoint, true, false)`, `setAlwaysEnemy(true)`, `setMaxHitpoints(250)` (sets max and current hull to 250, overriding createShip's formula) |
| 8 (+0x20) | "Station Platform" | `createStaticObject(null waypoint, 0x495d, false)`: the burning Luur platform `sn_burning_station` (meshes 0x495d.. with children 0x5254 / 0x5574 / 0x563c / 0x495e-0x4960, its own Player(1000, hull 9 999 999)); not moving, docking type 2 (pickup), name text 3208 "Station Platform", always friend, rotated (0, -2.35619, 0) = yaw -135 deg (`AEGeometry::rotate`), **`+0x70 = 1`, `+0x24 = 10`**, loot list `+0x4c` deleted. Position: the null waypoint, i.e. the orbit origin (where the station would be). |
| 9 (+0x24) | Terran carrier | `createStaticObject(Waypoint(-70000, -5000, -35000), 0x4974, true)` (`sn_carrier_terran_1` + children 0x4975-0x497a), moved there (vtable +0x44), not moving, `+0x6c = 0`, docking type 1 (drop-off), always friend, `setHitpoints(9 999 999)`, `+0x70 = 1`, `+0x24 = 10`, loot list deleted |
| 10-13 | 4 Rhinos | `createShip(race 0 Terran, fighter, ship 0x33 = 51 Rhino, route waypoint 0, true, false)`, each on a clone of the shuttle route (3.3), always friend, `+0x139` (halfword) = 0; positioned at (20000, -3000, -50000), (10000, -7000, -30000), (25000, 0, -36000), (12000, 6000, -45000) in that order |

- `KIPlayer+0x70` = "skip on the radar": `Radar::draw` skips such objects (no marker, no lock, no docking target) and
  `RocketGun::seekEnemy` ignores them. So the platform and the carrier are pure scenery: never lockable, never docked at.
- `+0x24` is the race field; setting the two objects to race 10 (the Specters' race) puts them on the Specters' side, so
  the Specters never target them (same-race rule). Interpretation; the field is the one `handleCloaking` reads as race.
- The Specters are **not given a route** (`Level+0x110` holds a 2-point route (-55000, 0, -35000) -> (-30000, 0, -50000),
  used only as the spawn waypoint; nothing else reads `Level+0x110` after creation). They spawn at waypoint 0 plus
  `createShip`'s scatter `rnd(40000) - 20000` per axis, i.e. inside the box x -75000..-35000, y -20000..20000,
  z -55000..-15000, and attack by the always-enemy AI.

### 3.2 The player

After building the array (0xbf102): `PlayerEgo::setPosition(-30000, 5000, -100000)` (literals 0xc6ea6000 / 0x459c4000 /
0xc7c35000), the start position copied to `Level+0xc8`. Facing is the level default (not set here). The LevelScript
constructor has no 0xb7 case (generic start sequence; whether that is the 7 s launch camera or the arrival camera for this
direct module-2 start was not traced).

Distances at start: Specter spawn centre ~69 800 units (3.5 km; 65 000 in z, 25 000 in x); carrier ~77 000 units
(3.9 km); platform (origin) ~104 500 units (5.2 km).

### 3.3 The shuttle route (scenery, like mission 94 "Evacuate Luur")

`Route(points, objects, spread (20000, 0, 20000), 9)` with points `[-1,-1,-1] (object 9: carrier), (30000, 1000, 40000),
[-1,-1,-1] (object 8: platform)`, looping. -1 = "the object's position". The same docking-route mechanism as type 0xb8
(`npc_traffic_ai.md`: 0xf / 0xb8 / 0xb7 docking routes, states 7-9): the Rhinos shuttle between the carrier and the burning
platform. They are unarmed (`createShip` clears `PlayerFighter+0x21` for ship 0x33) and carry no loot (+0x4c deleted);
hull `(180 + 14 * min(rank, 20) + 20) * 1.7` x the difficulty factor (`x + x * (difficulty - 0.5)`), = (180 + 126 + 20) *
1.7 = 554 at rank 9 on Normal. Never respawned (3.5).

### 3.4 Win objective and time limit

`Level+0x130 = 181000` (the level's time limit, copied by the LevelScript constructor to `LevelScript+0`) and the win
objective `Level+0x28 = Objective(3, 181000)` ("elapsed > 181 000 ms", a survival objective). No fail objective
(`Level+0x2c` stays null).

- Mission time = `LevelScript+8/+0xc` (64-bit), advanced in `MGame::OnUpdate` by the frame's dt (time-extender / fast-
  forward scaled), from the level's start; `MGame::dialogueEvent` doesn't reset it for 0xb7 (no briefing, below).
- `MGame::gameOverCheck`: the time limit would fail a mission whose win objective is not survival; 0xb7's is, so the
  limit only drives the HUD countdown.
- `MGame::successCheck` runs only when the `MGame+0x48` counter has reached 5001 ms (reset by `gameOverCheck`), so the win is
  noticed at the first check after 181 s: **181-186 s** of mission time.
- `Status::missionCompleted` lists 0xb7 among the types that never complete there (`case 0xb7: break`), so only the
  objective wins it.

So the round is **3:01** long (the HUD shows the remaining time, 3:01 counting down; the texts only say "the given time
limit"). The request's "2 minutes" isn't in this build.

### 3.5 Waves (`Level::updateMissionOrbit` 0xd4bd4, every frame from `Level::update`)

While a mission of type 0xb7 is set: `Level+0x174 += dt`; when it exceeds **7500 ms** (0x1d4c) it is reset to 0 and every
entry of the enemies array that is `KIPlayer::isDead` == 1, **inactive** (`Player::isActive` == 0, i.e. its death tumble
and explosion are over) and **not ship 0x33** (the Rhinos) is revived:

1. vtable +0x18 = `PlayerFighter::reset` (KIPlayer::reset: hull back to max = 250, state 0, cloak cleared and
   `cloakingPossible = 1`),
2. position = the **player's current position** + (s1 * (rnd(30000) + 50000), s2 * (rnd(10000) + 10000), s3 *
   (rnd(30000) + 50000)), each sign s random ±1, each ship drawn separately (vtable +0x48 setPosition),
3. `+0x4c = 0` (no loot list).

So a dead Specter comes back 2.5-4 km (50 000-80 000 units) from the player on x and z, 500-1000 m above or below, within
7.5 s of its explosion; at most 8 are ever alive. The fixed objects never die (9 999 999 hull) and the Rhinos are
excluded, so in practice only the Specters respawn. (The timer `Level+0x174` is the same field as type 0xf's 50 s
respawn timer; only one branch applies per mission.)

## 4. The Specters

### 4.1 Stats

- Ship 44 Specter, race 10, `createShip` hull formula (overridden): `(180 + 14 * rank + 20) * 2.25` for ship 0x2c, then
  `setMaxHitpoints(250)`: **250 hull**, no shield / armor (NPCs have hull only).
- Hit cube ±1000 (±650 on Extreme, `Status::hardCoreMode`).
- EMP pool `setEmpData(rank * 5 + 40, 15000)` = 85 at rank 9.
- No cargo list (ship 0x2c's loot list is deleted in `createShip`, and again on every respawn).

### 4.2 Cloaking (`PlayerFighter::handleCloaking` 0xf0b00, called from `update` states 1 / 7)

Generic race-10 behaviour, nothing challenge-specific (`cloakingPossible` +0x2d0 = 1 from the constructor and every
reset): while not cloaked, if the ship is "panicking" (`+0x1d8`) and `rnd(100) < 50` it cloaks at once; otherwise a timer
`+0x2cc` accumulates dt and every 8000 ms there is a 30 % chance (`rnd(100) < 30`). A cloak lasts `rnd(5000) + 9000` ms
in all: 2000 ms fade-in (material switched to shader 0xe, `MeshChangeShaderAnimValue(t / 2000)`), cloaked (exhaust off,
`+0x70 = 1` = off the radar) until 2000 ms before the end, 2000 ms fade-out. (`PlayerFighter::cloak(ms, instant)` is the
scripted variant: ms <= 0 -> rnd(5000) + 5000, + 4000.)

### 4.3 Guns (`Level::assignGuns` 0xcb638)

For every armed ship: the damage is first computed by the usual rules, then **`if mission type == 0xb7: damage = 1`**,
then `Gun(slot 0, damage, 4 bullets, ammo -1, lifetime 3000, reload 600 - 2 * 45 = 510, ...)` (gameWon -> index 45),
`setFriendGun(true)`, then the per-race switch on `KIPlayer+0x24`: race 10 -> `Gun::setIndex(0xe5)` (229, the Specter
blaster), projectile mesh 0x4a93 (`sn_projectile_229_anim_alpha`), and **`damage = (int)(damage * 0.7)`**. With damage 1
that is `(int)0.7 = 0`: the Specters' bullets do no damage in the Android build (as decompiled; the order is the code's,
damage override before the gun is built, race factor after). The hit feedback (shake, hit sounds, arcs) still plays.

For the remake: a faithful port makes the mode harmless. A designer's guess would be to apply "damage 1" after the race
factor (1 per bullet, 4 bullets per burst, 510 ms reload: 8 Specters ~60 damage / s against 270 + 250 + 220), or to keep
the normal Specter damage (`rank + 2..3` x 0.7). Flag this as a remake choice.

Shot sound: the Specter shot event (2276, per the remake's sound audit in CLAUDE.md "Sound"; not re-checked here).

### 4.4 Targeting

Always-enemy fighters with no route: they attack the player (and, through `connectPlayers`' race rules, any non-race-10
ship, which here means the unarmed Rhinos; with 0 damage nothing dies). The two fixed objects are race 10 and
radar-hidden. No sleeping (`createShip`'s 5th argument true = active).

## 5. Player-side rules in the challenge

| Where | 0xb7 behaviour |
|---|---|
| `MGame::dialogueEvent` 0x1b0498 | no briefing page (type 0xb7 excluded like 0, 8, 0xa6) |
| `MGame::OnTouchEnd` (autopilot button, ~0x1ab9f8) | the autopilot menu (`Hud::initHudMenu(3)`) is not opened: the button does nothing unless an autopilot / approach is running (it still switches those off) |
| `Hud::draw` (~0x1926f6) | the autopilot touch button image (`Hud+0x2ac/0x2b0` at `+0x394`) isn't drawn |
| `MenuTouchWindow(1)` (pause menu) 0x14816c | no "Missions" (129) and no "Cargo hold" (166) buttons; Options (31), Back to Main Menu (522), Action Freeze (59) remain |
| `Hud::draw` readout | the level timer: remaining = `LevelScript+0 - mission time` while > 0 (image `Hud+0x2c0` at `+0x3d4`, `Globals::longToTimeString`); the kill counter is type 0xc's only |
| `Level::assignGuns` | NPC damage 1 (4.3) |
| Gamma (`Level::update`, `Status::getGammaRayDamagePerSecond`) | station 111, index 0x98 (>= 0x6a, < 0x9e): table 0x252b80[2] = **1.0 gamma / s**, x (100 - attr 52) / 100 = x 0.4 with the Gamma Shield II -> **0.4 / s**: 72.4 of the 100 in a round, never lethal; the "gamma shield low" HUD event (0x2c) only below 15. Each round starts at 100 again (`MGame::OnInitialize` writes Status+0x68 = 100) |
| Docking, gate, planet jump, Khador | refused by the level mission (525); the Blue Fyre has no Khador Drive anyway; Luur has no dockable station at this index |

Nothing else (Radar, LevelScript, MGame::OnUpdate) checks type 0xb7: a full search for `0xb7` / 183 against
`Mission::getType` finds only the places in this file (plus unrelated item 0xb7 = 183 Disruptor Laser and text 0xb7 =
183 "Ship").

Kills: `Level::enemyDied(i, byNpc = false)` -> `Status::incKills` and **`Level+0x24` += 1** (the round's player kills; NPC
kills go to `Level+0x20`). Kills raise the XP, so the rank (and the Rhinos' / carrier's hull) can rise from one round to the
next.

### 5.1 Sky, sun, music

- Station 111 Luur in system 27 Ginoya at index 0x98: the supernova system before its end (`inSupernovaSystem`). As the
  remake's OrbitBuilder already does for that index: the supernova sun (scale 1.37329 from 0x6a), the supernova flare sky
  layer (mission != 89 and < 158, the "nasty" texture from 106, x1.5 speed), the storms (mission >= 90 in system 27), gamma.
- Music: 2242 `GOF2_Mission_102_CutScene_Loop_2` (DLC2_MUSIC) as the calm track; with hostiles on the scanner the
  `Radar::draw` battle switch plays the Specter tracks 149 `SN_Battle_StealthFighter_Low` / 150 `..._High`
  (`Traffic.CalmClip` / `Traffic.UpdateMusic` rules).

## 6. Score and combo (only the HUD survives)

### 6.1 Fields

| Field | Reset (`MGame::reset`) | Read | Meaning (inferred) |
|---|---|---|---|
| `Status+0x180` | 0 | `drawChallengeModeScore` | score (int, shown as 7 digits) |
| `Status+0x184` | 1 | nothing | unknown (a multiplier?) |
| `Status+0x188` | 1 | `drawChallengeModeScore` | combo count |
| `Status+0x17c` | never written at all (zero from construction) | `drawChallengeModeScore` | combo timer, ms remaining |

No other code writes `+0x17c / +0x180 / +0x188` (scanned every function's Thumb code for stores through the
`Globals::status` GOT slot 0x265810). There is no score per kill, no combo increment, no combo timeout in this build.

### 6.2 `Hud::drawChallengeModeScore` 0x1908bc (never called)

- Colour 0x77CCFFFF (`PaintCanvas::SetColor`), the score `Status+0x180` as a decimal string left-padded with "0" to 7
  digits, drawn with the digit sprite `Hud+0x4cc` (frame = digit), each digit `frameWidth - pad` apart (pad =
  `layout+0x2c`), centred: x0 = w/2 - 7 * step / 2, y = pad.
- If `combo timer > 0` and `combo > 1` (colour white):
  - combo line y = pad + digitHeight + pad.
  - while the timer is <= 3000 ms it **blinks**: nothing of the combo is drawn when `timer % 100 < 50`; in the visible half
    a **bonus number** `(int)(combo * 1000 * (1 + combo * 0.05))` is drawn centred one line further down (y = comboY +
    digitHeight + pad).
  - the "COMBOx" image `Hud+0x4d0` at (x0 + pad, comboY), then the combo count's digits right after it, scaled by
    `1 + max(0, (timer - 7000) * 0.01)` (a pop when the timer is above 7000 ms, so a fresh combo presumably starts the timer
    somewhat above 7000, e.g. 7100-7500, and it then runs ~7 s; inferred).
- The digit sprite and the COMBOx image are the `gof2_challenge_interface` atlas (images 6000-6010).

Reading of the design (not in the code): each kill adds points and increments the combo; the combo timer restarts on
every kill; a combo left to run out pays `combo * 1000 * (1 + 0.05 * combo)` (combo 2 -> 2200, 5 -> 6250, 10 -> 15 000),
announced during its last 3 s. The 10 SV_Mode sounds fit a rising combo jingle (2.3).

### 6.3 Sounds (group SURVIVALMODE, bank FMOD_GOF2_SVMODE)

`BuildResourceList` registers events 0x8ea-0x8f4 as sound resources (0x8f4 = 2292 is the highest event id
`FModSound::play` accepts). Events 2283-2292 (0x8eb-0x8f4) `SV_Mode_01` .. `SV_Mode_10` (`Assets/Audio/SVMODE/`): simple
2D one-shots, event volume 0.42658, max_playbacks 1; 01-09 are 0.27-0.34 s blips whose pitch rises roughly from ~430 to
~620 Hz (zero-crossing estimate), 10 is a 0.91 s longer, louder one. No code plays any of them (no literal, no computed
`0x8eb + n` call). Most likely combo steps 1-9 and a 10th for a long combo or the bonus; inferred. (0x8ea itself is
2282 `CONTAINER`, a transport sound of level 91, not part of this set.)

## 7. End of a round

### 7.1 Time out (the only "win")

1. `MGame::successCheck`: `Level::checkObjective(mission time)` true -> not campaign -> `Status::incMissionCount()`
   (missions completed +1, +2 XP) -> `DialogueWindow::set(mission, 1, -1)`, game paused.
2. `DialogueWindow::loadContent` 0xb7 branch: one page, the client "Client" with its random portrait; text = a random
   one of 373-377 (table 0x25c6c4: generic "thanks, nice job" lines) + "\n\n" + text 176 "Kills" + ": " + **`Level+0x24`**
   (the round's kills, read as `*(DialogueWindow+0x50 Level)+0x24`).
3. Closing it (`MGame::OnTouchEnd`, mission won, not instant action, not campaign): `setFreelanceMission(empty)`,
   `Layout::showMissionRewardMessage` with reward + bonus = 0, `changeCredits(0)`, the objectives removed and the level
   mission set to empty, then the 0xb7 restart:
   - `MGame+0x1d8 = Level+0x24` (the kills; written, never read: presumably for the lost score / highscore code),
   - `Ship::setEquipment(Patala x **10**, secondary slot 0)` (the first round has 5),
   - a new `Mission(0xb7, "Client", new random portrait, ..., current station)` set as mission and freelance mission,
   - `MGame+0x54 = 0`, application module 2: the level is rebuilt (new Specters, new start position as in 3.2, mission time
     0, full hull / shield / armor and gamma 100 because `MGame::OnInitialize` stored the maxima, music unchanged since
     `switch_to_target_setting` stays -1).

So on Android the "challenge" is an endless loop of 181 s rounds with a kills tally after each; no score, no highscore.

### 7.2 Death

No 0xb7 special case: `MGame::gameOverCheck` -> emergency system if any (none mounted), else the explosion, "Game Over"
(319), sound 0x25 `game_over`, and `RecordHandler::recordStoreRead(Globals::lastRecordWritten)`; after 4000 ms a tap loads
that record (the player's own last save, docked, module 5) or, without one, goes to the main menu (music 2, module 1).
The challenge never saves (no docking), so the player's saves are untouched. (With 0-damage Specters only asteroid
collisions can cause this.)

### 7.3 Leaving

Pause menu -> Back to Main Menu (522, confirm 523): the normal path; the in-memory game is the reset challenge state, the
main menu's Resume reloads `lastRecordWritten`.

## 8. Uncertain / not determined

- The scoring rules (points per kill, combo increment, the timer's start value and length, when the bonus is paid, what
  `Status+0x184` is) are not in the Android code. Only the display formulas in 6.2 are.
- Whether the iOS build had a different round length (181 000 ms here; the request mentioned 2 minutes) or Specter damage.
- The texts and actions of the end-screen ChoiceWindow (1.2): only the three result codes are known.
- The start sequence the LevelScript plays for this direct start (launch vs arrival camera) and the player's initial
  heading were not traced.
- `KIPlayer+0x70` = radar-hidden and `+0x24` = race for the two fixed objects is read from their uses elsewhere
  (`Radar::draw`, `RocketGun::seekEnemy`, `handleCloaking`), not from a named setter.
- Hint indices 8, 9, 0xa, 0xd, 0xe, 0xf, 0x13 were not mapped to their texts.

## 9. Remake notes

- Content already in the remake: the Blue Fyre, every item above, Specters with `NpcCloak`, `ShipSmoke`, the Luur
  supernova sky (`OrbitBuilder` at world index 0x98), the burning platform and the Terran carrier models (as for level
  94), the Rhino shuttle routes (`SupernovaLevels` 91 / 94), gamma (`PlayerHealth`), the HUD level timer (`HudReadout`),
  the success dialogue machinery, the SV_Mode clips and the `gof2_challenge_interface` atlas (digits + COMBOx).
- To add it, a remake needs its own entry point (a main-menu card or a Missions / Extras button using texts 293 / 297-298),
  a scoring design (6.2 gives the HUD and a bonus formula to match), a decision on the Specters' damage (4.3), and an end
  screen (294-296, 300). The original's success-then-restart loop (7.1) can serve as "Play again?".
