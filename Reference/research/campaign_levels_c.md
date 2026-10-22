# Campaign levels C: campaign missions 111-162 (0x6f-0xa2)

In-space levels of the late campaign, reconstructed from the decompiled Android HD build. Private research for the remake,
do not publish. Game units, milliseconds, Ghidra addresses (image base 0x10000). Game -> Unity: `(x, y, -z) * 0.05`.
Text IDs refer to `Assets/Localization/text_en.json` (only IDs and short paraphrases here, no quotes).

Sources: `Level::createCampaignMission` 0xc3370, `Level::createRadioMessages` 0xd0574, `Level::createStaticObjects` 0xcadb0,
`Level::createStaticObject` 0xcda54, `LevelScript::LevelScript` 0x15e650, `LevelScript::process` 0x160d50,
`Status::nextCampaignMission` 0xb6c98, `Status::departStation`, `Status::missionCompleted`, `MGame::successCheck`,
`MGame::gameOverCheck`, `MGame::OnTouchEnd` / `OnTouchBegin` / `OnUpdate` / `OnInitialize`, `ModStation::OnInitialize` /
`OnUpdate`, `RadioMessage::triggered` 0x17c5d8, `Radio::update` / `draw`, `Objective::achieved` 0xa36fc,
`DialogueWindow::init` / `loadContent`, `MenuTouchWindow::startValkyrie` / `startSupernova` / `startSupernovaChallenge`.
Float constants lost in the decompile were read from the Thumb disassembly (movw/movt pairs, literal pools).

## 0. Which story these indices are (correction)

The campaign index is `Status+0x1e8` (`getCurrentCampaignMission`). The story boundaries are fixed by two predicates:

| Predicate | Code | Meaning |
|---|---|---|
| `Status::gameWon` 0xb6832 | `campaign > 0x2c` | main story = indices 0..44; its last step is case 0x2c -> index 45 |
| `Status::dlc1Won` 0xb8eda | `campaign > 0x53` | Valkyrie DLC = indices 45..83 |
| end of Supernova | case 0xa1 -> index 0xa2 with an empty `Mission()` | Supernova DLC = indices 84..161, 162 = finished |

So **every index >= 111 belongs to the second half of the Supernova DLC.** The main story's ending (index 44/45) and the
Valkyrie DLC (45-83, deep science station 100, battlestation 101, first Void visits) are in the ranges covered by the other
two agents. The Valkyrie station, the Void and Alice do reappear here, as Supernova story: Valkyrie is stranded in the Void
(missions 0x93, 0x98, 0x9a), then becomes the plasma-array platform at Var Lupra (0x9d). Section 5 summarises the main
ending / DLC unlock logic as far as it touches these missions.

## 1. Mechanisms used below

The shared mechanisms are documented in detail in `campaign_levels_a.md`; this is what this file relies on.

### 1.1 Mission objects and activation

- `nextCampaignMission`: `case N` sets the index to N+1 and creates the `Mission` for **N+1**. `Mission(type, reward,
  station)`: `+0x8` type, `+0x24` reward, `+0x30` target station (-1 = the Void), `+0x5c` statusValue, production goods
  (index, amount) via `setProductionGoods`. The Void is `Status+0x78`, a default `Station()` with index -1
  (`Station::Station()` 0xb37d4), so `Mission(..., -1)` means "in the Void"; `Status::inAlienOrbit` = current station ==
  that object.
- Activation (`Status::departStation`, called whenever a space level for a station is entered): `Status+400` (the level's
  mission) = the campaign mission if its target station == the entered station and its type is not in the excluded set
  `{0x96,0x97,0x99,0x9b,0xa1..0xa4,0xa6,0xa7,0xa9,0xad, 8, 0xe}`. Types 4, 0xa5, 0xa8, 0xaa, 0xab, 0xac, 0xae, 0xb (campaign)
  therefore activate in their target orbit. `Level::init`: level type 3 + active campaign mission -> `createCampaignMission`
  (instead of the random `createMission`); after `gameWon` with neither DLC owned (`options[0x35]`, `options[0x37]`) the
  campaign branch is skipped.
- Completion (`Status::missionCompleted` 0xb924c, polled by `MGame::successCheck`), types used in this range:

| Type | Completes when | Used for |
|---|---|---|
| 4 | only through the level's win objective (`Level+0x28`) | in-space missions |
| 0xb | docked at the target station | "go to station X" |
| 8 | docked at target with the production goods in cargo; campaign 0x8f special: in space at target when `Status+0x1c` holds item 0xd2 (a finished blueprint product) | deliveries |
| 0xa4 | 10 000 ms in space, not docked | 0x9b |
| 0xa5 | in space (not docked) at the target station | 0x98 (arrive in the Void) |
| 0xa8 | `Status+0x174 >= statusValue` (cargo units transferred at a docking-type-2 object, 1 unit per 1500 ms docked, `PlayerEgo::update`) | 0x8b |
| 0xaa | statusValue == 1 (set by the LevelScript cutscene) | cutscenes |
| 0xab | docked at target + a second flag (bar talk) | station story steps |
| 0xac | docked at target with the production goods in cargo | 0x70 |
| 0xae | statusValue >= production amount (goods unloaded at a docking-type-1 object) | 0x87 mining plant |

- `MGame::successCheck` 0x1b0620 runs every ~5 s (it only proceeds when the `MGame+0x48` counter reached 5001 ms, which
  `gameOverCheck` resets) except for type 0xaa (immediate). On success: if `DialogueWindow::hasSuccessDialogue(index)`
  (count table 0x25a158 > 0) the success dialogue opens and advances the campaign when closed; with no dialogue
  (0x78, 0x7b, 0x83, 0x89, 0x90, 0x9d) and index > 0x2d it calls `nextCampaignMission` directly, clears the objectives and
  sets the level mission to `Mission::empty` (the level keeps running).
- Dialogue content: briefing counts 0x259ed0 / pairs 0x25a3e0, success counts 0x25a158 / pairs 0x25a5c0; per mission
  the start offset is the running sum of the counts (`DialogueWindow::init`), entries are (speaker image, text ID) pairs,
  speaker name = text `0x63d + image`.

### 1.2 Level arrays and objectives

- `Level+0xf8` "enemies" array: every scripted object (hostile, friendly or static) in creation order; the indices below
  ("[3]") are the indices used by radio triggers and objectives. `createStaticObjects` appends after
  `createCampaignMission`. `Level+0x108` player route (waypoint marker), `+0x10c` / `+0x110` spare routes, `+0x114` radio
  messages, `+0x18c` scratch vector.
- `Level+0x28` = win objective (`checkObjective`), `+0x2c` = fail objective (`checkGameOver`). `Objective(type, p[, q])`:

| Type | Achieved when |
|---|---|
| 0 | no enemies left | 
| 1 | enemies[p] dead |
| 2 | last waypoint of the player route reached |
| 3 | elapsed > p ms (a "survival"/time objective; as a fail objective = time limit) |
| 4 | radio message p is over |
| 7 | enemies[0..p) dead |
| 0x12 | enemies[p..q) all dead |
| 0x16 | the last radio message is over |

- `MGame::gameOverCheck`: player hull <= 0 -> emergency system, else explosion and "Game Over" (text 0x13f), reload the last
  record. Exception: **campaign 0x9e reloads space (module 2) instead of a savegame.** A LevelScript time limit
  (`LevelScript+0`) fails the mission when the win objective exists and is not type 3. Three failures in a row of the same
  campaign mission (`Globals::lastCampaignMissionFailCount > 2`) scale every hostile NPC gun's damage by 0.7
  (`Level::assignGuns`).
- NPC stats in this whole range: `gameWon` is true, so NPC hull = `180 + 14*L + 20` (not `4*campaign`, see
  `npc_traffic_ai.md`) and NPC gun reload = `600 - 2*45` = 510 ms. Harval's ship (ship 0x31) in 0x9d / 0x9e gets an extra
  rocket gun (item 0xd6, type 0x28, speed 8, lifetime 10 000, reload 3000, damage x4).

### 1.3 Radio messages (`RadioMessage(text, image, trigger, param[, count])`)

`Radio::update`: only evaluated while no message is shown; the first triggered message becomes current, appears 2000 ms
after triggering and stays `lines*2000 + 1500` ms; its voice comes from `Globals::getDialogueSoundId(text)` (voice events
unmapped). Speaker name = text `0x63d + image` (0 Keith T. Maxwell, 1 Brent Snocom, 2 Gunant Breh, 6 Carla, 10 Pirate,
0x11 "Story" caption, 0x16 Nivelian, 0x1a Alice, 0x26 Moonsprocket, 0x27 Trunt Harval, 0x31 Miner, 0x32 Vossk, 0x37 Hans).
Trigger codes used here:

| Code | Fires when |
|---|---|
| 1 | enemies[param] dead |
| 5 | level time >= param ms |
| 6 | message[param] has triggered (effectively "after message param", because of the one-at-a-time queue) |
| 0xc | enemies[param] hull < 50 % |
| 0x13 | enemies[param] hull < 25 % |
| 0x1b | `LevelScript+0x1c` (the script "event"/state) == param |
| 0x1f | enemies[param] hull < 75 % |

### 1.4 LevelScript fields

`+0x1c` event/state (also read by trigger 0x1b), `+0x90` and `+0x98` 64-bit ms timers, `+8` level time, `+0` time limit,
`+0x11` cinematic flag, `+0x21` launch camera flag, `Hud+1` / `Radar+0x48` HUD visibility, `+0xcc` helper geometry the
camera follows, `+0xbc/+0xc0/+0xc4` effect geometries, `+200` an `Explosion`. KIPlayer `+0x70` = hidden from the radar
(`Radar::draw` skips it), `+0x124` = the fighter's attack/wake box half-size (default 50 000), vtable `+0xc` wake up,
`+0x18` revive, `+0x1c` set speed, `+0x44`/`+0x48` set position.

### 1.5 Teleports between cutscenes and missions

Several steps move the player without flight (`departStation` + module switch; module 2 = space `MGame`, 5 = station):

| Where | New index | Effect |
|---|---|---|
| `ModStation` after a station dialogue | 0x77 | space at Thynome (10), stream-out position |
| | 0x85 | space at Katashun (120) |
| | 0x90 | space at Var Lupra (112), orbit start |
| | 0xa0 | space at Thynome (10), orbit start |
| | 0x80 | activates the storyline Wanted target (Pal Tyyrt) |
| `MGame::OnTouchEnd` after a space success dialogue | 0x78 | docked at Bak S'ondorr (126) (back from the 0x77 cutscene) |
| | 0x7e | space at Katashun (120) (0x7d done -> 0x7e cutscene) |
| | 0x7f | space at Alioth (98), stream-out |
| | 0x86 | docked at Var Lupra (112) (back from the 0x85 cutscene) |
| | 0x90 | space at Var Lupra (112) (0x8f done -> 0x90 cutscene) |
| | 0x9b | space at `Status+0x84` = the last non-Void orbit (set in `MGame::OnInitialize`), stream-out |
| | 0xa1 | space at Maissa (93) (0xa1 cutscene) |
| | 0xa2 | docked at Maissa (93): end of the Supernova story |
| `LevelScript` itself | 0x91 | 0x90 cutscene end: same orbit reloaded (the attack) |
| | 0x9e | 0x9d finale end: space at Luur (111), stream-out; hull/shield/armor/gamma and the secondary weapon kept |

## 2. Mission table 111-162

Stations: 10 Thynome (Wolf-Reiser), 22 Dekato, 38 Nepis (Aquila), 40 Valadon (Buntta), 55 Kappa (Union), 58 B'akrram
(K'ontrr), 78 Var Hastra, 79 Kernstal (Mido), 82 Plural Z / 83 Marktesh (Prospero), 93 Maissa (Pescal Inartu), 96 Kalun
Amir / 98 Alioth / 99 Damarque I (Augmenta), 103 Coromesk (Skavac), 111 Luur / 112 Var Lupra (Ginoya, the supernova system
27), 120 Katashun / 121 Navan (Paraah), 126 Bak S'ondorr (Me'enkk), 131 Bra'Murr (Wah'norr).

| Index | Type / station | In space? | Content (success dialogue text range) |
|---|---|---|---|
| 0x6f 111 | 0xab @38 | no | bar (0xa87-0xa8b); new chapter: Carla radio 0xc62/0xc63 (1.6) |
| 0x70 112 | 0xac @38, goods item 146 Magnetar Juice x1 | no | deliver (0xa8c-0xa8e) |
| 0x71 113 | 0xab @82 | no | bar (0xa8f-0xa93) |
| **0x72 114** | 4 @83 Marktesh | **yes** | loan-shark pirates in the asteroid field (3.1) |
| 0x73 115 | 0xab @82 | no | (0xa98-0xa9b) |
| 0x74 116 | 0xab @93 | no | search Pescal Inartu bars, star map marks stations 90-94 not yet visited (statusValue bits) (0xaa0-0xab1) |
| 0x75 117 | 0xb @126 | no | system 30 made visible (0xab2-0xab7) |
| 0x76 118 | 8 @126, goods item 209 Mutagen x1 | no | buy mutagen (0xab8) |
| **0x77 119** | 0xaa @10 | cutscene | "meanwhile at Thynome" (3.2) |
| **0x78 120** | 4 @40 Valadon | **yes** | 2 Specter ambush (3.3) |
| 0x79 121 | 8 @93, goods item 209 | no | deliver mutagen (0xac6-0xace) |
| 0x7a 122 | 0xb @10 | no | (0xacf-0xae0); passenger complaint radio while flying (1.6) |
| **0x7b 123** | 4 @121 Navan | **yes** | security check radio (3.4) |
| 0x7c 124 | 0xb @121 | no | Dr. Gum'Skratch (0xae5-0xaf3) |
| **0x7d 125** | 4 @55 Kappa | **yes** | freighter black box search, secure containers (3.5) |
| **0x7e 126** | 0xaa @120 | cutscene | Harval and the refugees (0xb0d-0xb12) |
| 0x7f 127 | 0xb @98 | no | Brent (0xb13-0xb1d) |
| 0x80 128 | empty | wanted | storyline Wanted #0 Pal Tyyrt (0xb1e-0xb21) (3.6) |
| 0x81 | never reached | | case 0x80 skips it |
| 0x82 130 | empty | wanted | storyline Wanted #1 Kehnor (0xb22-0xb2a) |
| **0x83 131** | 4 @112 Var Lupra | **yes** | fly-by of the plasma array platform (3.7) |
| 0x84 132 | 0xb @112 | no | (0xb2c-0xb32) |
| **0x85 133** | 0xaa @120 | cutscene | Harval's contingency plans (0xb34-0xb36) |
| 0x86 134 | 0xb @22 | no | industrialist (0xb37-0xb3e) |
| **0x87 135** | 0xae @103 Coromesk, goods item 155 Titanium Ore x140 | **yes** | mining-plant delivery under pirate waves (3.8) |
| 0x88 136 | 0xb @112 | no | (0xb49-0xb4e) |
| **0x89 137** | 4 @58 B'akrram | **yes** | Vossk hail radio (3.9) |
| 0x8a 138 | 0xb @58 | no | Mekant Orskk (0xb54-0xb6b); system 31 made visible |
| **0x8b 139** | 0xa8 @131 Bra'Murr, statusValue 10 | **yes** | hack two Vossk battleships for the prism (3.10) |
| 0x8c 140 | 0xb @112 | no | (0xb72-0xb7d) |
| 0x8d 141 | 0xb @78 | no | blueprint 0xd2 (Chromo Plasma) unlocked with 4 plasma ingredients (0xb7e-0xb87) |
| **0x8e 142** | 4 @79 Kernstal | **yes** | plasma-gathering tutorial with Gunant (3.11); cargo given: 15x item 197, items 196 and 198 |
| 0x8f 143 | 8 @112, goods item 210 Chromo Plasma x1 | in space | completes in the Var Lupra orbit when the blueprint product is ready (0xb92-0xb97); new chapter (Carla 0xc64/0xc65) |
| **0x90 144** | 0xaa @112 | cutscene | Harval's ultimatum (3.12) |
| **0x91 145** | 4 @112 | **yes** | the plasma array is destroyed (3.13) |
| 0x92 146 | 0xb @112 | no | plan: use Valkyrie as platform (0xba0-0xbab) |
| **0x93 147** | 4 @Void | **yes** | hail Alice (3.14) |
| 0x94-0x97 148-151 | 0xab @96 (four steps) | no | real-estate broker for Alice (0xbb6-0xbcb) |
| 0x98 152 | 0xa5 @Void | arrival | completes on entering the Void (0xbcc-0xbd3) |
| 0x99 153 | 0xb @98 | no | Brent, Hans, energy cells (0xbd4-0xbde) |
| **0x9a 154** | 4 @Void | **yes** | Alice's betrayal, board Valkyrie in 91 s (3.15) |
| 0x9b 155 | 0xa4 (station 0) | in space | 10 s in space anywhere (0xbfb-0xbfe) (3.16) |
| 0x9c 156 | 0xb @99 | no | deliver Alice (0xbff-0xc03) |
| **0x9d 157** | 4 @112 | **yes** | final battle, Alice takes Valkyrie and fires the plasma array (3.17) |
| **0x9e 158** | 4 @111 Luur | **yes** | duel with Trunt Harval (3.18) |
| 0x9f 159 | 0xb @10 | no | victory party (0xc26-0xc30), station music 144 OutroSong |
| **0xa0 160** | 0xaa @10 | cutscene | "the next morning" (0xc32-0xc34) |
| **0xa1 161** | 0xaa @93 | cutscene | "meanwhile on Maissa" (0xc36-0xc3b) |
| 0xa2 162 | empty | | Supernova finished (5) |

Notes: `Status+0x178` is set when the new index is 0x5d, 0x6f or 0x8f (table 0x252b00), see 1.6.
`startSupernovaChallenge` (menu) is a separate mode: index forced to 0x98, freelance mission type 0xb7 at Luur (111),
fixed ship 0xb8/4 = 46 and equipment; it is not the campaign.

### 1.6 Ambient story radio in this range (`MGame::OnUpdate`, `Level::createRadioMessage`)

- Chapter hails: when `Status+0x178` is set, 12 000 ms of playing time into a (non-Void) space visit, radio type 0x1b with
  index = count of `{111, 143}` <= campaign (0 below 111): Carla (image 6) 0xc60+2k then Keith 0xc61+2k. For this range:
  index 1 (0xc62/0xc63) from 0x6f, index 2 (0xc64/0xc65) from 0x8f. Shown once.
- Campaign 0x7a-0x7c with no active mission, not mining, and >= 3 600 000 ms of playing time since the last one: radio type
  0x1c, a passenger complaint 0xc5c/0xc5d (0x7a) or 0xc5e/0xc5f (0x7b, 0x7c), random of two.
- Campaign 0x7d decoy orbits (3.5): radio type 0x13.

## 3. The in-space missions

Positions are game units; "faces the station" = direction toward the origin (the station of the orbit is at the origin).

### 3.1 0x72 (114) Marktesh: the loan sharks

- Player start (`LevelScript` ctor, current station == target): (0, 0, -120000), facing the station.
- Asteroid field centre overridden to (30000, 0, 80000) (`Level::createAsteroids`; count 40 + rnd(40) seeded by station).
- Spawns: 6 pirates (race 8, `getRandomEnemyFighter(8)`), each at asteroid `[i + n/2]` position + (0, 2000, 0), asleep,
  attack box `+0x124` = 7000, hull x2 (max and current).
- Script: state 0: as soon as any pirate is active (woken by the player within +-7000), all pirates get box 50 000 and wake
  -> state 1.
- Radio: 0 0xa94 Keith at 8000 ms (they must be hiding in the field); 1 0xa95 Pirate (image 10) on event 1 (you blow our
  cover); 2 0xa96 Keith after 1.
- Win: `Objective(0x12, 0, 6)` all 6 dead. Success dialogue 0xa97 (one line). No fail condition beyond death.

### 3.2 Cutscene missions 0x77, 0x7e, 0x85, 0xa0, 0xa1 (type 0xaa)

All five use the same setup (`LevelScript` ctor group, process block for the same list; 0x5f, 99, 0x6d share it):

- HUD and radar off, player invisible, invulnerable, computer controlled, at (0, 0, 0), engine sound stopped, look-at
  camera at (-15000, 800, 75000) aimed at the player (i.e. at the station), LOD forced.
- Process: camera translates every frame by (+1, ?, -2) units per ms (the middle component is lost in the decompile).
  State 0 -> 1 after 2000 ms; event 1 triggers the caption; state 2 when it is over; 2000 ms later
  `campaign mission statusValue = 1` -> `missionCompleted` (type 0xaa) -> the success dialogue plays the scene's
  conversation -> `nextCampaignMission` -> teleport (1.5).
- Captions (image 0x11 "Story", trigger 0x1b event 1): 0x77 0xab9 "meanwhile, Thynome"; 0x7e 0xb0c and 0x85 0xb33
  "meanwhile, Nivelian sector"; 0xa0 0xc31 "next morning"; 0xa1 0xc35 "meanwhile, Maissa".
- Conversations: 0x77 Carla/Khador 0xaba-0xac4; 0x7e Harval/Nivelian 0xb0d-0xb12; 0x85 0xb34-0xb36; 0xa0 Carla's letter
  0xc32-0xc34; 0xa1 the Flabbergaster drinks 0xc36-0xc3b.
- Music: at Katashun (120) during 0x7e / 0x85 the space music is FMOD event 0x8bf (2239, outside the 0-213 table in
  `fmod_event_ids.txt`).

### 3.3 0x78 (120) Valadon: Specter ambush

- No special start (normal arrival / launch).
- Spawns (relative to the player at level creation): 2 Specters (race 10, ship 44), always hostile, at
  player + right*50000 + dir*(+4000 for the first, -4000 for the second); route `+0x110` = [(0, 0, -80000)].
- Radio: 0 0xac5 Keith at 1500 ms (stealth fighters, great).
- Win: `Objective(4, 0)` = that message over. No success dialogue, so `nextCampaignMission` runs directly (next: deliver the
  mutagen at Maissa); the two Specters stay and must be fought or escaped. The star map marks Maissa (93), not Valadon,
  as the story destination during 0x78 (`StarMap::drawOnScreenInfo`), so Valadon is met on the way there.

### 3.4 0x7b (123) Navan: security check

- Player start: (0, 0, 180000), facing the station. No spawns.
- Radio: 0 0xae1 Nivelian (image 0x16) at 8000 ms (state your intent); 1 0xae2 Moonsprocket after 0; 2 0xae3 Nivelian after
  1 (cleared); 3 0xae4 Keith after 2.
- Win: `Objective(4, 3)`. No success dialogue -> next (0x7c, dock at Navan).

### 3.5 0x7d (125) Kappa: the freighter's black box

Decoy phase (`MGame::OnInitialize`): while 0x7d is current, entering the orbit of any "freighter mission station" (15, 30,
40, 45, 60, 70, 80, 85, 95; `Status::isFreighterMissionStation`) sets that station's bit in the mission statusValue
(`getFreighterMissionStationBit`) and plays radio type 0x13: Keith 0xaf4 + rnd(4) (scanning) at 1500 ms, then 0xafa + rnd(4)
(nothing here). The star map marks the not-yet-scanned ones. The real target is Kappa (55).

Kappa level (`createCampaignMission` 0x7d), enemies array (15):

| Index | Object | Position | Notes |
|---|---|---|---|
| 0, 1 | pirates (race 8) | route `+0x110` wp0 = (0, 0, 0) | awake, always hostile |
| 2..7 | pirates (race 8) | wp1 = (7000000, 7000000, 7000000) | asleep, parked far away |
| 8 | secure container `sn_secure_container_nivelian` 0x4961 (+ 0x498f) | (-80000, 0, -160000) | not moving, docking type 3 (hackable), friendly, radar-hidden, HP 9999999 |
| 9 | secure container | (-72000, 0, -190000) | same |
| 10 | secure container | (-66000, 53000, -170000) | same |
| 11..14 | junk field `sn_junk_field` 0x4962 | x = -72000 + 4000k, y = rnd(1500) - 200k, z = -145000 - 4000k (k = 0..3) | rotation (k+11 - 5.7596, ?, k+11 - 4.3197), radar-hidden |

Script (process 0x7d) and radio:

| State | Trigger | Action |
|---|---|---|
| 0 | radio 1 (0xafe, signal found) has triggered | player route = [(-70000, 0, -130000)] (the black box) -> 1 |
| 1 | player reaches that waypoint | containers renamed "Secure Container 1/2/3" (text 0xc8c + number), activated, shown on radar -> 2 (radio 2 0xaff: dock and hack them) |
| 2 | radio 2 over | pirates 2..5 teleported to player + (+-(2000k + 2000), 2000 + rnd(1000), -57000 -+ rnd(2000)) (k = 0..3), AI off, targeting the player; cinematic: player hidden and frozen, HUD off, look-at camera on pirate 4 from its dir*4000, up*-500 -> 3 (radio 3 0xb00 Pirate: another treasure hunter) |
| 3 | 7000 ms | pirates 2..5 fly in at 2 u/ms; then AI on, player back, camera behind the player -> 4 (radio 4 0xb01 Keith: claiming salvage) |
| 4, 5, 6 | player docked to a container and wins the hacking game | container docking type 0, unnamed, radar-hidden, hacking game removed, state + 1 (radio 5 0xb02 black box but no prism; 6 0xb04 one left; 7 0xb05 prism found). Entering state 6 also teleports pirates 6, 7 to player + (25000 + 1000k, 2300 + 50k, 25000) |

- Radio 0: 0xaf4 Keith at 1500 ms; 1: 0xafe after 0.
- Win: `Objective(4, 7)` = radio 7 (0xb05) over. Success 0xb06-0xb0b (call Brent) -> teleport to Katashun for the 0x7e
  cutscene.

### 3.6 0x80 / 0x82: storyline Wanted targets

Index 0x80 and 0x82 carry an empty `Mission()`, so their levels are free flight (`createMission`). Wanted #0 Pal Tyyrt
(Vossk, ship 9, HP 5000, `requiredMission` 128) and #1 Kehnor (pirate, ship 42 Vol Noor, HP 6500, `requiredMission` 130) are
activated on the Terran board (`ModStation` at 0x80: `activateNewWanted`), highlighted in `WantedWindow::draw`, and the
star map hides the normal story marker. Their success dialogues are 0xb1e-0xb21 and 0xb22-0xb2a. The Wanted encounter
itself is the generic wanted system (not documented here); where the kill advances the index was not traced.

### 3.7 0x83 (131) Var Lupra: plasma array fly-by

- Start (ctor, station 112): cinematic, player at (-65000, 0, 95000) facing the station, computer controlled, no collision,
  HUD off; camera at player - dir*2400 + up*500 - right*600, then fixed and carried along
  (+2.01 dt forward, +0.03 dt right, +0.01 dt up per frame). Autopilot to a programmed station is set up (unless instant
  jump). State 1.
- Process: state 1 for 3000 ms -> 2 (radio 0 0xb2b Keith admires the platform, event 2); when it is over: control back,
  vulnerable, HUD on -> 3.
- Win: `Objective(4, 0)`. No success dialogue -> next.
- The platform is the orbit's plasma-array object (4.1), stage 2 at this point.

### 3.8 0x87 (135) Coromesk: titanium for the mining plant

- Planet-jump gate (`MGame::OnTouchBegin`): needs a mining drill (sort 0x13), else text 0xc8d.
- Start: (70000, 0, 100000), facing the station (ctor rule for station 103 when campaign > 0x54 or == 0x87).
- Briefing dialogue (0x87 has one): Miner 0xb3f-0xb42 (140 t of titanium needed).
- Spawns: [0] Mining Plant (0x4a88 `test_dock` + parts 0x4a89-0x4a90 `sn_station_mining_plant_*`) at (0, 0, 0), not
  moving, docking type 1, friendly, named 0xc8a, invulnerable; [1], [2] pirates (race 8) parked at (7000000, 7000000,
  7000000), asleep, hostile. All asteroids of the orbit get ore index 155 (Titanium Ore).
- Delivery: unloading titanium at the plant's docking point raises statusValue (type 0xae); done at 140.
- Script: state 0: docked to a docking point or level time > 60 000 ms -> pirates 1, 2 teleported to player +
  (25000 + 1000*slot, 5000, 25000), woken, visible, targeting the player -> 1. From then a timer: state 1 -> 2 when
  statusValue >= 70 (half); every 75 000 ms dead pirates are revived at the same offsets (state 2 -> 3 on the first wave
  after half).
- Radio: 0 0xb43 Miner (image 0x31) on event 1 (pirate attack); 1 0xb44 Keith after 0; 2 0xb45 Keith on event 2 (halfway);
  3 0xb46 Keith on event 3 (more company).
- Win: mission type 0xae (no level objective). Success 0xb47-0xb48 (prism handed over).
- Later visits (campaign > 0x54, except 0x87): `createStaticObjects` always places the same Mining Plant at the origin.

### 3.9 0x89 (137) B'akrram: Vossk hail

- Start: (20000, 0, 150000), facing the station. No spawns.
- Radio: 0 0xb4f Vossk (image 0x32) at 1500 ms; 1 0xb50 Keith; 2 0xb51 Vossk; 3 0xb52 Keith; 4 0xb53 Vossk (each after the
  previous).
- Win: `Objective(4, 4)`. No success dialogue -> next (0x8a, Mekant Orskk in the station).

### 3.10 0x8b (139) Bra'Murr: two Vossk battleships

- Planet-jump gate: ship 42 (Vol Noor) or a Vossk ship (`DAT_0025d3d0[ship] == 1`) with item 190 Vossk Signature, else
  text 0xc8f. `Status+0x174` reset to 0 at creation.
- Start: (20000, 0, 190000), facing Battleship 2.
- Spawns (22):

| Index | Object | Position |
|---|---|---|
| 0 | `sn_battleship_vossk` 0x4a6b (+0x4a6c, 0x4a6d) "Battleship" (text 0x683) | (-50000, -1500, 70000) |
| 1 | same, "Battleship 2" | (-200000, -1500, 30000) |
| 2..6 | turret_003 0x1a76, HP 1000, around battleship 0 | battleship + offset |
| 7..11 | same around battleship 1 | |
| 12..16 | Vossk fighters (race 1), start at battleship 0, looping patrol (-70000, 0, 80000) <-> (-30000, 0, 60000) | |
| 17..21 | Vossk fighters, start at battleship 1, loop (-220000, 0, 40000) <-> (-180000, 0, 20000) | |

  Battleships: not moving, docking type 3 (hackable), HP 9999999, bounding volume removed. Turret offsets (table 0x2536ac,
  rotation 0): (10283, -1123, 26039), (-10171, -1083, 25907), (-15624, -787, -7569), (15624, -787, -7569), (0, 4901, 3084).
  The Vossk are not flagged hostile at first (the player is disguised).
- Script: state 0: the first battleship the player docks to and hacks (docked + `hackingWon`) is renamed "" and set to
  docking type 0 -> event 1 (radio 1 0xb6d: no prism, cover blown). Event 1 also turns every non-wingman Vossk (fighters and
  turrets) hostile (`Player::turnEnemy`) once radio 1 has triggered. State 1: hacking the other battleship (the one still
  docking type 3) sets it to docking type 2 and the player's docking state to 2, `Status+0x174` = 0 -> event 2 (radio 4
  0xb70: cargo bot transfer). The prism is therefore always on the second ship hacked.
- Transfer: docked to the docking-type-2 ship, 1 unit per 1500 ms into `Status+0x174`; mission type 0xa8 completes at 10
  (15 s). The HUD shows an "N / M" counter in this orbit (`Hud::draw`).
- Radio: 0 0xb6c Keith at 8000 ms (two battleships!); 1 0xb6d on event 1; 2 0xb6e Vossk after 1 (intrusion detected); 3
  0xb6f Vossk after 2; 4 0xb70 on event 2; 5 0xb71 on event 3 (not set by the script; the same line is the success dialogue).
- Win: type 0xa8. Success 0xb71.

### 3.11 0x8e (142) Kernstal: plasma gathering with Gunant

- Planet-jump gate: a spectral filter (sort 0x21), 15x item 197 Ion Lambda Mk1 ionizing missiles, a plasma collector (sort
  0x23), else 0xc90; and at least 1 t free cargo, else 0xc92. (Case 0x8d gives items 197 x15, 196, 198.)
- Start: (60000, 0, 120000) facing the player-route waypoint; player route `+0x108` = [(90000, 0, 42000)].
- Spawns: [0] Gunant Breh (race 3, ship 30 Berger CrossXT, named 0x63f), friendly, HP 9999999, never boosts, same route,
  placed at player + right*2100 + dir*2000 facing the player's direction. Gas clouds: the normal count + 3, the first at
  (92000, 0, 36000); type = the orbit's most likely plasma (green here).
- Script: while state < 2 Gunant stops (speed 0) within 3000 of his waypoint. State 0 -> 1 when the player reaches the
  waypoint (radio 1 0xb89, event 1). State 1 -> 2 when any gas cloud has sparks (ionized by a detonated missile). State 2 ->
  3 when the cargo holds any of items 201-205 (the code checks 0xc9..0xcd, i.e. the four plasmas and, probably by mistake,
  Gamma Shield I); Gunant's speed 2.1 (radio 4 0xb8c, event 3).
- Radio: 0 0xb88 Gunant at 1500 ms (follow me, stop your engine); 1 0xb89 Gunant on event 1; 2 0xb8a Keith after 1; 3
  0xb8b Gunant after 2 (fire, detonate, collect in turret view); 4 0xb8c Gunant on event 3.
- Win: `Objective(4, 4)`. Success 0xb8d-0xb91 (where the other plasma colours are).

### 3.12 0x90 (144) Var Lupra: Harval's ultimatum (cutscene)

- Spawns: [0] Trunt Harval (race 2, ship 49 Scimitar, name 0x664), hostile, AI off, no cloak, on route
  (60000, 10000, 100000) -> (-50000, 0, 50000) (wp0 reached; he starts at wp0, facing along the route with y ignored);
  [1..12] 12 Specters (race 10, ship 44), hostile, AI off, same route, in formation around Harval (offsets in his frame,
  +-30 jitter): (-5000, 300, 200), (-3500, 300, 200), (5000, 300, 200), (3500, 300, 200), (-6500, 500, -5000),
  (-3250, 500, -5000), (0, -300, -5000), (3250, 500, -5000), (6500, 500, -5000), (-6500, -400, -5000), (0, -400, -5000),
  (6500, -400, -5000).
- Ctor: helper at (30000, 10000, 160000) moving along (1, 0, -1) at 5 u/ms, player hidden/frozen/inactive, HUD off, camera
  at Harval + (-4600, 1000, -4300) following the helper; process moves the camera by (0, 0.05, 0.28) dt per frame
  (`TargetFollowCamera::translate` at 0x162efa, r1 = 0; the decompile shows it as (0.28 dt, ?, 0.28)).
- Process: state 1 -> 2 at once; 2 -> 3 when radio 3 is over; state 3: each Specter has a 10 % chance per frame to cloak
  (20 000 ms), Specters accelerate (move t*0.05 per frame), Harval follows after 4000 ms; at 7001 ms:
  `nextCampaignMission`, same station re-entered, module 2 (the 0x91 attack starts).
- Radio: 0 0xb98 Harval at 7000 ms; 1 0xb99 Harval; 2 0xb9a Keith; 3 0xb9b Harval (chained).

### 3.13 0x91 (145) Var Lupra: the plasma array is destroyed

- Asteroid field centre (50000, 0, 70000).
- Spawns (15): [0] Harval (as above) at (30000, 0, 80000), route [(-45000, 0, 60000)], AI off, facing (-50000, 0, 50000);
  [1..12] Specters at (23000 + 3000(i-1), (rnd(1000)-500)*i + rnd(1000), 70000 + 2000i + rnd(2500)), AI off, facing the
  array; [13] the explosion animation `sn_plasma_array_midorian_explosion_anim` 0x4a6a at (-50000, 0, 50000), hidden,
  inactive, named 0xc87, HP 9999999, radar-hidden; [14] the plasma array stage 5 (+glow 0x494d only in 0x91), appended by
  `createStaticObjects`.
- Process: every frame all 13 ships fly forward at 2 u/ms; the Specters fire while state >= 2 (not in state 4 after 1 s).
  - State 0: all 13 target the array; an Explosion is prepared -> 1.
  - State 1: radio 0 over -> cinematic (player hidden, frozen, invulnerable, HUD off), camera at Harval + dir*18000 +
    right*3000 looking at him -> 2.
  - State 2: after 1000 ms Harval fires; a helper rides his shot (projectile speed) and the camera follows it -> 3.
  - State 3: 5000 ms later the level particle system starts at the array and the explosion begins -> 4.
  - State 4: 324 ms later the array [14] is hidden and the explosion animation [13] replaces it -> 5.
  - State 5: explosion animation at 0.3 speed; the ships veer right between 2000 and 5000 ms and accelerate away
    (move t*dt*0.001); at 10 000 ms all 13 are hidden and parked at (50000, 50000, 50000), control back -> 6 (event 5).
- Radio: 0 0xb9c Keith at 7000 ms (defend the array); 1 0xb9d Harval after 0; 2 0xb9e Harval on event 5.
- Win: `Objective(4, 2)`. Success 0xb9f (Carla: come back). Nothing can prevent the destruction; the fight is scripted.
- Afterwards (`createStaticObjects`, index > 0x91) the array no longer exists.

### 3.14 0x93 (147) the Void: hail Alice

- The Void between 0x54 and 0x99 holds a station named after Valkyrie (station 101's name; `Level::createSpace`,
  `Status::inEmptyOrbit`), Alice's stranded battlestation. The Void's random Void-fighter respawn (`updateAlienAttackers`)
  is off in 0x93 and 0x9a. Music: 145 Space_NoCombat_Void for 0x93-0x99 (136 Space_Combat_Void otherwise).
- No spawns. Radio: 0 0xbac Keith at 7000 ms; 1 0xbad Keith after 0.
- Win: `Objective(4, 1)`. Success 0xbae-0xbb5 (Alice's terms).

### 3.15 0x9a (154) the Void: Alice's betrayal

Spawns (22): [0] Hans' freighter (race 0, ship 51 Rhino), friendly, invulnerable, placed at player + right*2600 + dir*4000
facing the player's direction (ctor), route (-10000, 1000, 15000) -> dock at enemies[1] -> (-10000, 1000, 15000)
(`Route(int*, Array*, int*, 9)`; the middle waypoint (-1, -1, -1) refers to the target object; per-waypoint parameter 10000
at the dock, probably a wait time), its enemy = Valkyrie; [1] Valkyrie `v_station_battlestation_anim` 0x4220 static
object at the origin, docking type 3 (hackable), named "Valkyrie" (text 0x4d), friendly, radar-hidden; [2..21] 20 Void
fighters (race 9, `getRandomEnemyFighter(9)`), inactive, hostile, positioned (0, 0, 40000) + i*(sx*(900..999), -200..-1,
-200..199) for i = 2..10 and + i*(sx*(1600..1699), -200..-101, 1000..1499) for i = 11..21, sx = +1 for even i, -1 for odd
(a wide V ahead of the player), facing the player. Race-9 hull x2 in this mission (`createShip`). Asteroid centre
(-70000, 0, 30000).

- Start: (0, 0, 140000); the race-9 fighters' enemy lists cleared.
- Script (camera cuts; each "then" waits for the named message to be over plus 1000-1500 ms):

| State | Trigger | Action |
|---|---|---|
| 0 | radio 0 (0xbdf, 9000 ms) triggers | cinematic, player frozen, invulnerable; camera on fighter [2] from + (300, 300, 5800); fighters AI off, facing the player -> 1 |
| 1 | radio 1 (Alice) over + 1500 | fighters hidden; camera on the player at + (-350, 400, -1500) -> 2 |
| 2 | radio 2 (Keith) over + 1000 | camera on Valkyrie from + (0, 4000, 26000) -> 3 |
| 3 | radio 3 (Alice) over + 1000 | camera on the player -> 4 |
| 4 | radio 4 (Keith) over + 1000 | camera on Valkyrie from + (0, 4000, 42000) -> 5 |
| 5 | radio 5 (Alice) over + 1000 | camera on the player + (150, 400, -1500); freighter put at (-10000, 0, 22000), AI on; fighters active, vulnerable -> 6 |
| 6 | radio 6 (Keith sends the freighter) over + 1000 | camera on the freighter + (200, 300, 1800) -> 7 |
| 7 | freighter docked at Valkyrie | -> 8 |
| 8 | 2000 ms | -> 9 (radio 7 0xbe6 Alice: terminate him) |
| 9 | radio 7 over + 2000 | fighters active, visible, AI on; every third attacks the freighter, the others the player; Valkyrie becomes race 8, hostile, vulnerable, shown on radar; freighter enemy list cleared; control back -> 10 |
| 10 | radio 8 (0xbe7, ~90 s) over | time limit 91 000 ms and fail objective `Objective(3, 91000)` (time counted from here) |
| 10 | player docked to Valkyrie and wins the hacking game | fail objective removed, time limit 0, fighters' enemy lists cleared -> 11 (radio 11 0xbea) |

- Radio: 0 0xbdf Keith 9000 ms; 1 0xbe0 Alice (image 0x1a); 2 0xbe1 Keith; 3 0xbe2 Alice; 4 0xbe3 Keith; 5 0xbe4 Alice;
  6 0xbe5 Keith (chained 1-6); 7 0xbe6 Alice on event 9; 8 0xbe7 Keith on event 10; 9 0xbe8 Hans (image 0x37) after 8;
  10 0xbe9 Keith after 9; 11 0xbea Keith on event 11.
- Win: `Objective(4, 11)`. Fail: 91 s pass (dialogue mode 2) or death. Success 0xbeb-0xbfa (Corny, arrest of Alice), then
  back to the orbit the Void was entered from.

### 3.16 0x9b (155)

Type 0xa4 is excluded from activation, so no level is built for it: it completes after 10 s in space anywhere (success
0xbfb-0xbfe, bring Alice to Damarque). The `createCampaignMission` case (`Objective(4, 2)`) is dead code (0x9b has no radio
messages).

### 3.17 0x9d (157) Var Lupra: the final battle

Layout differs on weak devices (`Globals::isRunningHDonWeakDevice`); normal / weak indices:

| Index normal (weak) | Object | Position / notes |
|---|---|---|
| 0..9 (0..4) | Terran fighters (race 0, `getRandomEnemyFighter(0)`), friendly | route (70000, 0, 20000) -> (30000, 10000, 60000) (wp0 reached) |
| 10 (-) | Terran carrier `sn_carrier_terran_1` 0x4974, friendly, not moving | (30000, -15000, -50000) |
| 11..20 (5..9) | Specters (race 10, ship 44), hostile, hidden, inactive, AI off, no cloak | enemy route start (-110000, 10000, 170000) + offset k: (-1000 + 2000k, 0, 5000) k<2; (3000k - 9000, 0, 7000) k 2-4; (3000k - 20000, 0, 9000) k 5-8; (0, 0, 11000) k>=9; +-100 jitter; route (-110000, 10000, 170000) -> (-110000, 0, 20000) |
| 21 (10) | Trunt Harval (race 2, ship 49), hostile, hidden, inactive, AI off, speed 5 | enemy route start |
| 22 (11) | Alice (race 3, ship 20 Cicero, named 0x657), AI off, hidden | parked (50000, 50000, 50000) |
| 23 (12) | Valkyrie as a `PlayerStation` of station 101 with the plasma gun `sn_plasma_gun_valkyrie` 0x4950/0x4952/0x4951 (`PlayerStation::PlayerStation` for 0x9d at 112), race 3 | (-120000, 0, 20000) |

- Start: (100000, 0, 20000) facing normalize(-1, -0.2, -0.5). No level objective: the script ends the mission.
- Ctor loads the effects: plasma beam 0x4a75 with cone 0x4a76 and star 0x4a77 (`+0xbc`), burning-Valkyrie stage 1 fire/smoke
  0x4a97/0x4a98 (`+0xc0`) and stage 2 0x4a99/0x4a9a (`+0xc4`), all hidden.
- Script:

| State | Trigger | Action |
|---|---|---|
| 0 | | helper at (-120000, 0, 5000) facing +z -> 1 |
| 1 | radio 0 (0xc05, 7000 ms) over | cinematic on the helper, player frozen and invulnerable -> 2 |
| 2 | 16 000 ms | helper +3 u/ms z, camera +1 u/ms z; then Harval and Specters visible/active, helper at Valkyrie, camera at Harval + (-1500, -5000, 2500) -> 3 |
| 3 | 8000 ms | camera drifts (slowing over 15 s) -> 4 |
| 4 | radio 3 (0xc08 Harval) over and 18 000 ms | enemies fly in (t*0.02); then control back, enemies AI on; Specters (may cloak) given the point (120000, rnd(12000)-6000, rnd(30000)-87000), Harval (20000, rnd(6000)-3000, rnd(6000)-7000) -> event 5 (radio 4 Brent: focus Harval) |
| 5 | (>= 9 of the counted enemy ships dead and level time > 200 000 ms) or Harval hull < 25 % | every 25 000 ms Harval's cloak flag `+0x13c` toggles. Then: cinematic; camera at Valkyrie + (45000, 2000, 11000); Alice appears at camera + (15000, -200, 1000) flying to Valkyrie; Harval invulnerable -> 6 (radio 8 0xc0d Carla: what is that?) |
| 6 | radio 10 (0xc0f) triggered | Alice (3 u/ms toward Valkyrie) hidden; camera on the Valkyrie station -> 7 |
| 7 | radio 10 over | Valkyrie's station mesh backs away accelerating (-10 dt * t/10000), 3D sound 0x8cb; when its coordinate passes 100 000: Harval at camera + (4000, -600, -2000) aimed at Valkyrie + (0, 0, 40000), player placed at camera + (5000, 300, -14000), same heading -> 8 (radio 12 0xc11 Harval) |
| 8 | 12 000 ms and radio 12 over | Harval chases at 4 u/ms firing; camera at Valkyrie + (35000, 0, 70000); Harval hidden -> 9 (radio 13 0xc12 Keith) |
| 9 | radio 14 (0xc13 Alice) over | Harval re-appears after 9000 ms, fires at Valkyrie; after radio 13: explosion at Valkyrie + (-2000, 1000, -8000) (sound 0x8c4), 300 ms later burning stage 1 shown; every 6000 ms another explosion at + (-6000, 2000, -10000). Then camera at Valkyrie + (50000, 0, 70000), Harval and player hidden, burning stage 2 -> 10 |
| 10 | radio 15 (0xc14 Keith) over + 3000 ms | explosions at Valkyrie minus the light direction (sound 0x8c3), camera rumble; plasma beam placed at Valkyrie facing -z -> 11 |
| 11 | 10 000 ms | at 6000 ms sound 0x8c7, beam visible and animating; then helper at Valkyrie facing the sun, camera follows it, Harval removed -> 12 |
| 12 | 6500 ms | camera pulls back; helper races to the sun at 35 u/ms from 5000 ms; at 5000 ms `Level::switchSkyboxForSupernovaReversal` (normal system skybox `0x4588 + texture`, supernova flares removed); at 5500 ms explosion at Valkyrie + (0, 0, -12000); at 5700 ms beam and Valkyrie hidden, fade to white, sound 0x8c8; the sun billboard rescales between 5000 and 6000 ms; at 6500 ms `nextCampaignMission`, station = Luur (111), module 2 |

- Radio (17): 0 0xc05 Keith 7000 ms; 1 0xc06 Moonsprocket; 2 0xc07 Keith; 3 0xc08 Harval (chained); 4 0xc09 Brent on event
  5; 5 0xc0a Keith after 4; 6 0xc0b Carla when enemies[21] < 50 % hull (hard-coded index 21 = Harval in the normal layout);
  7 0xc0c Keith after 6; 8 0xc0d Carla on event 6; 9 0xc0e Alice; 10 0xc0f Carla; 11 0xc10 Brent (chained); 12 0xc11 Harval
  on event 8; 13 0xc12 Keith on event 9; 14 0xc13 Alice; 15 0xc14 Keith; 16 0xc15 Alice (chained).
- Sounds 0x8c3-0x8cb (2243-2251) are events outside the mapped 0-213 range (Supernova bank).

### 3.18 0x9e (158) Luur: duel with Trunt Harval

- Luur's station object (`PlayerStation::PlayerStation`) is, for campaign > 0x5e, the wrecked Midorian station
  `sn_station_midorian_wrecked` 0x4953 (+0x4954; 0x4955/0x4956 have no model), like stations 109/110; the burning-station
  parts (0x495d/0x495e on the station 76 model) are only used up to 0x5d. The orbit is an "empty orbit" that still gets
  its station object (`Level::createSpace`). From 0x9e on system 27 is no longer a
  supernova system: normal lighting, no gamma damage (except 1.0/s at station 109), normal skybox.
- `createCampaignMission` 0x9e: player at (-70000, 0, -30000) facing (1, 0, 1) (overridden by the ctor); [0] Harval
  (race 10 here, ship 49, name 0x664) at (9000, 0, -13000) facing normalize(-1, 0, 3), hostile, speed 5.3, EMP points x3,
  no cloak, AI off; [1..3] sentry guns `sn_sentry_gun_003` 0x49c2, radius 800, hostile, hull 100, parked
  (50000, 50000, 50000), inactive. `MGame::OnInitialize` cloaks Harval at start.
- Ctor: player at (-50000, 0, 15000) facing the sun (light direction), frozen, invulnerable, HUD off, engine silent; camera at
  player + (-28000, 500, 8500); helper = camera + lightDir*50000; the player is moved to the helper, turned toward
  (camera - helper) + (20000, 0, 0), pushed forward 30000, speed 0.1 (drifting, "blown off course"); fade in over 8000 ms;
  Harval's and the sentries' enemy = the player, sentry gun damage x0.3; Harval hidden.
- Script: state 1: camera drifts; helper turns toward (9000, 0, -13000) and flies 2.5 u/ms until 30 000 ms; Harval is shown
  after 25 000 ms and rides the helper, camera on him; the player rotates slowly; Harval's exhaust off from 20 000 to
  34 000 ms -> 2. State 2: Harval coasts; after 12 000 ms and radio 2 over: control back, Harval may cloak, AI on -> 3.
  State 3: every 4000 ms, if Harval is within 25 000 of the player and rnd(100) > 60, one inactive sentry gun is revived at
  Harval's position and turned hostile; every 25 000 ms Harval's cloak flag toggles.
- Radio (11): 0 0xc16 Keith 10 000 ms (the sun is normal again); 1 0xc17 Keith; 2 0xc18 Harval; 3 0xc19 Keith; 4 0xc1a
  Harval (confession; chained); 5 0xc1b Harval at < 75 % hull; 6 0xc1c Keith after 5; 7 0xc1d Harval at < 50 %; 8 0xc1e
  Keith after 7; 9 0xc1f Harval at < 25 %; 10 0xc20 Keith when Harval is dead.
- Win: `Objective(0x16, 0)` = last message (Harval dead) over. Fail: death reloads the level (MGame special), no savegame.
  Success 0xc21-0xc25 (Brent).
- No wingmen (`createWingmen` and the HUD wingmen button skip 0x9e).

## 4. Orbit state driven by these indices

### 4.1 Plasma array platform at Var Lupra (112)

`Level::createStaticObjects`: for campaign 0x80..0x91 a static object at (-50000, 0, 50000) facing the sun, named
"Plasma Array" (0xc87), friendly, invulnerable (HP 9999999), not moving, added to the enemies array:

| Campaign | Mesh |
|---|---|
| 0x80-0x82 | `sn_plasma_array_midorian_stage_001` 18750 (+ `_alpha` +2, `_anim_add` +1) |
| 0x83-0x86 | stage_002 18753 |
| 0x87-0x89 | stage_003 18756 |
| 0x8a-0x8d | stage_004 18759 |
| 0x8e-0x91 | stage_005 18762 (+ `_glow_anim_add` 18765 in 0x91) |
| > 0x91 | none (destroyed in 0x91) |

(`PlayerFixedObject::update` does not animate stage 5 during 0x91.)

### 4.2 Other orbit variants

- Coromesk (103): the Mining Plant (3.8) for campaign > 0x54.
- Valkyrie: in the Void as the Void station for 0x54..0x99, a static object in the Void in 0x9a, at Var Lupra in 0x9d
  (PlayerStation of 101 with the plasma gun). Battlestation 101 is an empty orbit from 0x54 on.
- Supernova system 27 (stations 109-113) while campaign < 0x9e: supernova skybox `sn_skybox_015` (+ flares 17824/17825;
  texture 0x2765 from 0x6a), gamma damage per second 3.0 / 2.0 / 1.0 / 0.5 / 0.3 for stations 109-113 (0.7 / 0.4 / 0.4 /
  0.3 / 0.2 before 0x6a), supernova lighting, no wingmen. From 0x9e: normal.
- Luur (111): station 76 model + burning-station parts up to 0x5d, wrecked Midorian station from 0x5f (`PlayerStation`),
  so throughout this range it is the wreck.
- Free-flight Specter raids (`createMission`): for 100 < campaign < 0x91 a (campaign/144*15 + 5) % chance per visit of 2-4
  Specters (see `npc_traffic_ai.md`).

## 5. Endings and DLC unlock / start

- Main story end (index 0x2c -> 0x2d, other agents' range): empty invisible mission, wormhole disabled
  (`Status+0x7c/+0x80 = -10`), +40 000 credits. `gameWon()` = campaign > 44 from then on: NPC hull/gun scaling capped
  (180 / 45), Thynome stocks 50 Void Crystals (Khador drive blueprint ingredient) if the player has no Khador drive,
  stations 10/100/101 restock energy cells, a one-time hint 0x6e (buy Valkyrie) when docking without the add-on.
- Valkyrie start: new game `startValkyrie` (resetGame + 45x `nextCampaignMission`, station 91, fixed ship and gear), or in a
  won save: `options[0x35]` owned, no mission, not autopilot/mining -> dialogue (success list of index 0x2e) and
  `nextCampaignMission` twice (`MGame::OnUpdate`).
- Valkyrie end (0x53 -> 0x54): empty mission, the mounted jump drive (sort 0x12) or a Khador Drive in cargo made
  saleable again, one Khador Drive (item 85) added to cargo; hint 0x6f (buy Supernova) when docking without the add-on.
- Supernova start: `startSupernova` (resetGame + 84x, station 70, ship 30, race 3, gear, 8 energy cells), or in a save with
  `dlc1Won` and `options[0x37]`: dialogue (index 0x55) and `nextCampaignMission` twice.
- Supernova end (0xa1 -> 0xa2): empty mission; after the Maissa cutscene the player is docked at Maissa (93); hint 0x265
  (Vossk, Nivelian and Midorian wanted boards unlocked): `WantedWindow::init` lists non-Terran wanted targets only when
  campaign >= 0xa2 (Terran ones from 0x80). `DialogueWindow` tables end at 0xa1.

## 6. Uncertainties

- Camera tracks with lost float components (cutscene group middle axis, 0x9d states 1, 3, 10, 12 exact paths) are only
  described qualitatively.
- 0x9a freighter route: the meaning of the per-waypoint parameter 10000 (assumed dock wait) and exact dock behaviour are
  not traced.
- 0x9d on weak devices: the dead-count loop (start 5, indices 6..11) and radio 6's hard-coded index 21 look inconsistent
  with the weak layout; only the normal layout is reliable.
- 0x8b radio 5 (event 3) is never triggered by the script.
- 0x80 / 0x82: how killing the storyline Wanted target advances the index was not traced.
- vtable `+0x20` (the "point" given to the 0x9d enemies when the fight starts) is assumed to be a fly-to target.
- Where exactly `PlayerStation` for the Void station picks its model (Valkyrie) was not checked.
