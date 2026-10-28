# Keeper of the Lake audit

## Boundary and purpose — 2026-10-28

This audit prevents the older handler's partial coverage from becoming the baseline for
four-role certification. Stone Vigil and the six preceding duties are complete; no Keeper
role has started or qualified. This is source/data/historical-evidence review, not a claim
that a new handler has been implemented or tested. No Keeper source/profile edits yet.

Domestic approved retaining existing authored objective layouts. Preserve the profile's
barriers, keys, lifts, target weights and completion guard. DutyMechanic must own boss
mechanics with SideStep suspended there and restored between encounters. Combat actions
and cooldown decisions remain with the routine. Native avoidance owns geometric movement;
only confirmed positive positioning, such as Akh Morn participation, requires a semantic
request through the same movement channel. Never use an NPC location as a generic safe proxy.

## Sources and verified identity

- Current upstream reference modules:
  [Einhander](https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod.Modules/RealmReborn/Dungeon/D29TheKeeperoftheLake/D291Einhander.cs),
  [Magitek Gunship](https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod.Modules/RealmReborn/Dungeon/D29TheKeeperoftheLake/D292MagitekGunship.cs),
  [Midgardsormr](https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod.Modules/RealmReborn/Dungeon/D29TheKeeperoftheLake/D293Midgardsormr.cs).
  These corroborate actors, actions and component overlap; their comments can disagree
  with their executable geometry and are not automatically authoritative.
- [Thonky encounter guide](https://www.thonky.com/final-fantasy-xiv/duties/the-keeper-of-the-lake)
  corroborates proximity falloff, persistent flamethrower danger, staggered phantom lines,
  ordered concentric rings and staying together through repeated stack damage.
- [Community dungeon guide](https://ffxiv.consolegameswiki.com/wiki/The_Keeper_of_the_Lake)
  corroborates the four objectives, post-6.2 add phase and mitigation/stack semantics.
- Installed Global client 2026.10.16, read through the detached Lumina probe:
  InstanceContent32/CFC32, territory1063, level/sync50. Objective text rows7500–7503:
  Agrius hull, Garlean barriers, ceruleum spill, Defeat ???. ContentTodo0 contains no useful
  index mapping; confirm director indices live instead of inventing six template stages.
- Existing `Dungeons/KeeperOfTheLake.cs` and canonical
  `DWsWebserver/Dungeons/2.0 ARR/50. Keeper of the Lake.xml`, read in full.
- Historical RB capture: installation `Reborn - Crucible-Arelia`, log
  `43288 2026-10-26 23.32.txt`, Keeper window about04:50–05:05UTC on October27.
  It contains shared CAST_START records including hidden helpers. Another independent
  capture is `Reborn - Crucible-Darling/Logs/108900 2026-10-22 21.54.txt` around02:07UTC.
  These are mechanic evidence, not Kember's current qualification. Older `Reborn Clean`
  log `14528 2026-08-05 09.20.txt` records repeated Mini Cannon no-path failures.

BNpcName rows verified:3369 Einhander;3371 auxiliary ceruleum tank;3373 gunship;
3374 Midgardsormr;3375 mirage dragon;3357 vanguard;3358 colossus;3359 signifer;
3361 eques;3362 laquearius;3363 centurion;3379 field inductor. The profile's first-route
IDs3349/3352–3355 match their authored names. Level5589380 places an arena marker at
(-40.75,641.463,-78.3), corroborating the final room center but not its wall radius.

## Complete mechanic map

Dimensions below are unpadded reference/data dimensions. Candidate damaging avoids need
0.5y clearance unless a documented constraint requires otherwise. Live acceptance for
every implemented row includes safe resolved impact, normal combat execution and owned
input cleanup, not merely an early inAvoid=false sample. Current ownership is mostly
SideStep plus the older partial DutyMechanic handler; the profile also clears all avoids.

| Boss / phase | Actions and owner | Expected response / geometry | Existing state | Evidence / confidence / acceptance |
| --- | --- | --- | --- | --- |
| Einhander room | NPC3369/base0x3927 | Circular floor near(18.85,-17), radius20 | Existing DM radius19 boundary; review inset | Reference + live boss positions; strong. Confirm physical edge and safe egress. |
| Raidwide |29273 Aero Blast, boss | Request mitigation; no escape circle | Missing DM mitigation | Sheet type2/range40 and live04:50:48, guide; confirmed semantics. Routine remains active. |
| Tankbuster |29620 Heavy Swing, boss | Request tankbuster mitigation | Missing | Sheet type1, live04:51:35; confirmed. No manual cooldown planning. |
| Tank deployment |29270 Screech, boss;3371/base0x3929 tanks | Choreography only; wait for actual damaging casts | No explicit lifecycle | Live04:50:57 and later tanks; confirmed. Do not avoid every dormant tank. |
| Line ignition |29271 Quick-firing Cannon, boss |40x4 forward line | Missing DM geometry | Type12/axis4, live04:51:06; confirmed. Avoid line while retaining room floor. |
| Proximity ignition |29272 Mini Cannon, boss cast destination | Move toward a safe outer region; proximity damage falls off | Missing; generic radius31 can erase arena | Type2/range31/omen152 gl_shipimpact1f; live center(18.814,-16.953), reference uses17 and guide calls proximity. Strong semantics, capture damage falloff. Never equate31 with a binary unsafe radius. |
| Tank explosions |29275, untargetable3371 tanks |12y caster-centered circles, successive groups | Missing DM geometry |11 live helper casts, range12; confirmed. Only current damaging group, preserve next group for later resolution. |
| Gunship room |3373/base0xE68 | Circle near(8.5,-150), radius19 | DM circle19 | Reference + current route; strong. Verify inset/room wall during dodge. |
| Flamethrower cast |3389 boss |120deg cone,12+6 reach=18y | Existing40y/125deg overestimate | Type3/omen5 fan120, live04:56:54; confirmed. Match actual cone with clearance. |
| Flamethrower continuation |3390 helper/base0xE7B | Persist the same cone through five subsequent ticks | Missing lifetime after cast | Reference event count, guide duration; instant effect not in CAST_START. Capture tick timing/owner before claiming exact expiry. |
| Carpet Bomb |3391 boss announcement;3392 helper |5y circle at helper CastLocation | Missing DM geometry | Live04:57:23 cast location differs from helper position; confirmed. Announcement adds no duplicate circle. |
| Garlean Fire |3411 instant helper;event object2005194 |8y persistent ground pools | DM8.5y visible-object avoid | Reference + existing object registration; strong. Capture visibility/target lifecycle and crossing. |
| Blitz |3393 (4790 is another same-named row) | Targeted knockback, avoid inventing a giant floor region | Not explicit | Installed3393 type8/range0 and guide; capture actual gunship action/impact and pool overlap. |
| Gunship adds |Cohort signifer/eques/laquearius/secutor/vanguard | OrderBot targets eligible adds; return to boss | Profile explicitly lists only boss in room | Reference AddsMulti and guide; verify ordinary aggro targeting before adding a new provider. |
| Vanguard line |1433, NPC3357/base0xE69 |30+2.8 reach by5 line | Missing DM geometry | Type4, live04:58:11; confirmed. Same native hazard union as pools. |
| Vanguard cone |1435, same add |8+2.8 reach,120deg | Missing DM geometry | Type3/omen5 and live04:58:20; confirmed. Preserve normal add combat. |
| Midgardsormr room |3374/base0x392A | Circle centered(-41,-78), radius19.5 | DM18y boundary | Reference + installed marker; strong. Boss stands near(-40.786,-98.25), not room center. |
| Main line |29889 Admonishment |40x12 from boss | Missing DM | Type12/axis12 and live05:01:35; confirmed. |
| Main close attack |29888 Inner Turmoil |22y circle centered on boss at southern edge | Missing DM | Sheet and live05:01:43; confirmed. Do not center on arena marker. |
| Raidwide |29891 Disgust announcement /29892 helper damage | Request mitigation; no dodge | Missing | Live05:02:25 parent, sheet helper range20; confirmed semantics; capture effect attribution. |
| Phantom preparation |29277 Kin;mirages bases0x392B/0x392C | Pre-position for future mirror lines when identifiable | No explicit state | Live05:01:55 and guide/reference; strong. Observe hidden helper positions, no NPC following. |
| Phantom main line |29280 boss |40x12 line, then mirror lines | Missing DM | Live05:02:11.141,6700ms; confirmed sequence. |
| Phantom close attack |29278 boss |22y circle, then mirror lines | Missing DM | Live05:02:38.467,6700ms; confirmed sequence. |
| Phantom distant attack |29279 boss |22–40 donut sector,180deg forward | DM full donut with inner19; wrong geometry | Sheet type13/range39/omen70; reference actual shape contradicts its cone-only comment. Live05:04:57.696. Strong, inspect omen/impact; don't expand unsafe floor behind boss. |
| Mirror lines |29290, untargetable3375 bases0x392B/0x392C |40x12 lines from opposite sides,1700ms visible casts | Missing DM; late cast may be insufficient |8 live helpers: start05:02:18.137 after main line,05:02:45.562 after inner. Confirm earlier actor/timeline cue and common-safe staging. |
| Add phase |3375 bases0x392D/0x392E, boss untargetable | Kill dragons before gauge fills, retain boss ownership | Profile weight100 dragons, boss1 | Reference and live untargetable parent05:03:11; matches authored priority. Verify both dragons and return. |
| Add-phase ultimate |29281 Animadversion | Mitigate; damage depends on add/gauge phase | Missing mitigation | Live05:03:32.168,6700ms and guides; confirmed semantics. |
| Expanding rings |29285 parent;29286/29287/29288 base0x233C helpers |6y circle,6–12 ring,12–20 ring; move inward after first effect | Missing DM sequence | Live starts05:03:11.777/13.753/15.764, each3700ms; repeats twice. Confirm effect grace; do not union all future rings into a full arena wall. |
| Multi-hit stack |29283 initial +29284 three follow-ups; boss targets party actor |6y stack, keep participants through all hits | NPC FollowDodge: wrong semantics | Live target0x400057DC, reference icon305 and guides. Resolve actual target; self-target needs living party participation. Capture follow-up timestamps, never release at first cast end. |
| Tankbuster |29602 Condescension | Request tankbuster mitigation | Missing | Type1, live05:04:27; confirmed. |

Ordinary autoattacks, Thunder968, visual30226/29646 and non-damaging announcements remain
routine/lifecycle responsibilities. No tower, gaze or spread is corroborated for this
duty. Do not create handlers for those categories without new evidence.

## Required overlaps and ordering

All reference phases activate their component sets concurrently. The table below is a
required coverage checklist; component activation is not proof that every pair resolves
simultaneously. Capture exact effects when timing or a shared destination is uncertain.

| Window | Resolution order and movement owner | Required verification |
| --- | --- | --- |
| Einhander line/proximity plus tanks | Cannon resolves first; tank casts begin later. Native avoidance owns current hazards; prefer floor also safe from imminent tanks when possible. | Live line04:51:06 → first tanks04:51:11.852 → second group04:51:20.275. Proximity cast destination is arena center, not boss. Avoid premature union of future explosions. |
| Successive tank groups | Earliest effect group before later chain group; retain owned room boundary throughout. | Living tanks are not automatically active hazards. Damage/HP and post-escape movement must agree. |
| Gunship cone continuation / pools / carpet / add attacks | Current persistent hazards and cast geometry share one native union; routine/OrderBot retain add combat. | Confirm true cone expiration, pool gaps, add attacks, and no input drift while boss relocates. Blitz is a capture target, not an invented staging route. |
| Phantom main attack → mirror lines | Main impact first; choose common-safe floor during main cast where observed mirror layout permits. Otherwise retain first-stage safety through effect then move for mirrors. | Main line05:02:11.141+6.7s precedes two mirror casts05:02:18.137+1.7s. Inner05:02:38.467+6.7s precedes mirror05:02:45.562. Outer05:04:57.696+6.7s precedes mirror05:05:04.764. Do not let independent registrations choose oscillating destinations. |
| Add phase + Antipathy | Profile targets dragons while the earliest resolving ring owns native egress. Later rings must not exclude safe floor before their turn. | Helper origin(-40.786,641.040,-78.250) differs by20y from boss. After center clears, move inside before middle ring; retain routine attacks. |
| Antipathy progression | Circle → middle → outer, about2s apart. One sequence owner selects the applicable stage. | Historical current-sheet omen mismatch for29288 (live148/current404) does not justify changing ring semantics; verify current helper data and effects. |
| Stack with any remaining hazard / add phase transition | Earlier harmful impact retains egress priority. Stack request may move only along safe reachable floor, then hold participation through follow-ups while scheduling combat. | Self-target and other-target cases, four-player coverage where available, no generic NPC proxy, no move/stop competition with native escape. No simultaneous stack/ring impact is established by this capture; retain safe arbitration regardless. |
| Tankbuster/raidwide with geometry | Routine mitigation requests do not acquire a competing mover or suppress healing. | Verify requests and routine resumption, including sustained stack hold. |

## Profile/template comparison before edits

Verified constants remain duty32/zone1063 and bosses3369/3373/3374. Preserve authored
four-objective flow, existing intermediate coordinates, both lift crossings, key item
2001567, object IDs2004844/2004975/2004829/2004834, enemy allowlists and weights, and
IsDutyEnded completion. These inherited departures from six arrival/defeat pairs have
Domestic's explicit blanket approval; no new exception request is needed.

Proposed modernization: Duty Support local QueueType1; inline existing GrindAreas beside
their Grind tags; remove the global ClearAvoidsOutOfCombat hook/chunk once handler lifecycle
owns registration; guard nullable interaction objects using existing supported predicates.
Do not add a new KillMobs chunk merely to match the template: preserve the existing explicit
Grind target lists and diagnose any actually skipped enemy. Preserve the legacy shortcut
until its interaction location is verified; the installed Level rows do not identify it.
No new waypoints, custom chunks, helpers or objective-progress state are proposed.

## Implementation and first-run gates

1. Build one encounter-local state owner for geometry sequencing, stack requests, native
   input lifetime and cleanup. Reuse existing movement-channel principles; do not refactor
   frozen/shared encounter behavior. Gate actor selectors as well as avoid conditions so
   inactive registrations cannot suppress SideStep between bosses.
2. Resolve known missing/wrong semantics above before a qualifying candidate. Mini Cannon
   is proximity, Akh Morn is a multi-hit stack, Antipathy is ordered rings, and phantom
   follow-ups require explicit stage handling. Remaining uncertain timing needs bounded
   capture and honest qualification gaps, not guessed future hazards.
3. Capture actor positions/base IDs, active casts and resolved damage, stack target and party
   participation, flame continuation ticks, mirage appearance/timeline, native commands,
   director/lift transitions and normal exit. Use read-only observers; do not issue rescue
   movement from diagnostics. A capture-only failed attempt cannot count as a role pass.
4. Compile with deployment disabled and run detached geometry/lifecycle/overlap checks.
   Use approved updater and protected profile publication; verify exact runtime agreement.
   Revalidate Kember/process/install, use LLGoToInn LimsaLominsa and verify inn177 before
   Paladin queue through Panda Farmer. Actively verify first two qualifying roles.

No run was launched during this audit boundary. Runtime control and temporary settings
remain as recorded in DutyLevelingPassiveMonitoring.md. Ratings remain unchanged.

## Candidate1 preparation override — 2026-10-28 18:25 UTC

The preceding map describes the audited baseline. Candidate1 now replaces NPC following
with actual Akh Morn target/participant handling, supplies the corroborated damage shapes,
orders earliest ring/tank waves, stages visible phantom-only lanes when common-safe floor
exists, and retains flame/stack follow-ups. One IPlayerMover adapter forwards normal/native
commands and owns semantic request cleanup; stale callbacks and foreign successor movers
cannot stop or overwrite current input. Keeper's plugin pulse/reset wiring handles yielding
profile hooks, death, loading, cutscene, explicit stop and replacement. Frozen handlers and
unrelated Thornmarch work were not changed.

Timing remains an explicit verification gate: flame and stack currently persist until the
next distinct boss cast with a conservative12s post-cast fallback. This prevents premature
release after the initial cast, but does not prove exact last-hit timing. Capture the five
flame ticks and three Akh Morn repeats before claiming coverage. Phantom forecasting uses
visible, untargetable bases0x392B/C during the main cast; confirm this early cue on all three
layouts. Actual29290 casts replace forecasts. No player/NPC safe-position proxy is used.

Full deployment-disabled compile passed with13 prior unrelated warnings. Temp/
KeeperMechanicChecks links the real three handler files against detached command-recording
fakes:59 checks passed across lifecycle interruptions, stale/successor input, forced motion,
self/missing-target stacks, routine scheduling while holding, ring ordering/geometry,
proximity origin, rotated mirage lines, concurrent tank groups, phantom common-floor layout
and fixed flame continuation origin. These are implementation checks, not gameplay clears.

Canonical profile modernization published as b2f7a6e4b3991b4185ae7731d74260331146236c.
It preserves all authored MoveTo, Hotspot, TargetMob weights, UseObject, UseTransport and
LLChangeZone attributes. Six GrindAreas are inline; default Duty Support, three missing-object
guards and removal of ClearAvoidsOutOfCombat are the intended behavior changes. No new chunk,
helper, waypoint or objective layout was added. XML parsed and route/target comparisons passed;
line endings were normalized to the original CRLF. Durable rationale reviewed in every changed
source/profile file. Ratings remain unchanged.

Approved updater RestartOnly17028→70576/game57744 succeeded, stopped state preserved, no
standard Stop fallback, plugin directory replacement, rollback or automatic profile resume.
The coordination package is installed PandaTripleTriad2.2.8.2, stage SHA
973259FDDC818065469FE88F2DF6DA4BB56C520EF51720B7EF8898A299C0CDAA.
Kember verified alive/stopped Paladin in inn177; game/executable/listener ownership verified.
Existing gearset11 activated; temporary Platypus/Market hooks verifiedfalse. Observer/setup
completed through normal UI dispatcher; read-only observer captures audited casts, action
announcements/damage, helper visibility, party/player deaths, objectives and input positions.

Exact candidate hashes:
- KeeperOfTheLake.cs:0C9E4FF7118834730685D7D0089285449AB1B50FC8F9299A11507A1A1601BCC3
- KeeperOfTheLake.Geometry.cs:1785291BBC2948A2198A35E89360E6734219454BD8E9C0A647A8E8FD1FE6F470
- KeeperOfTheLake.Movement.cs:4ED7F2BCB5AE7D7511C9BF9230410B422956E88C4374174C5A23038B092264FD
- LoadedDutyMechanic_402469575.dll:B7F4C573B05BA25446733B12C276ED6CC65F6D0A858E7C2861ECAF7731759BEB
- Canonical XML:57F318AD166C9FE3D8E20824F77A665D928B74CEC357CC57ACF855274A99CFCD
- Existing-loader wrapper:80DCE4AA71BFD7DB8B82C4B8052C49C1ADA4E6BDB83FD6457EC665EA9A71D4C6

No queue: protected preload, then supported bridge ClearCache/refetch, returned old
ECC0900388DA2F46503B5D99C9C7B431.xml, SHA
BD3300A803B76CE0DF3B3AF70AE373DC50E98D9DE2A3F061AE4AECB7C5B7DF1A.
It still clears global avoids and defaults QueueType0; expanded XML differs. Preserve the
cache, wait for normal publication propagation, refresh through the existing API and require
agreement before launch. This boundary preserves the current stopped state; it is neither a
failed attempt nor a user pause. No attempt ledger/completion watcher yet. Prepared helper
paths and exact next steps are in DutyLevelingPassiveMonitoring.md.

## First Paladin Einhander boundary — 2026-10-28 18:35 UTC

Protected XML agreement resolved18:28: new622C3DAE9E9C37626E2C31A55AAF23CD.xml SHA
3F87476EDF48D942D4BA08277FBFD1F8EEE2300E82B8DA746D21BA255218B85F matches expanded
canonical b2f7a6e. First Paladin launched18:29 via Panda Farmer from verified inn177;
ledger Temp/Keeper-PLD-Start.json, RB70576/game57744, log offset29506, watcher61072.
Entry1063 confirmed and director advertises0:(0,1),1:(0,2),2:(0,1),3:(0,1), corroborating
preserved four-objective flow. No role credit yet.

Einhander acquired ownership18:32:52.689 and completed18:34:44.149, objective0 advanced,
SideStep restored. No player death/vulnerability marker in reviewed range. First tank
waves escaped to northern safe floor, then later groups to cleared floor. Native cleanup
released input without a wall run. Mini Cannon produced repeated tiny boundary corrections
(about0.01y after arrival)18:34:08–10; these are not a large positional oscillation, but
check routine behavior on other roles before accepting execution quality.

Measured Mini Cannon action29272: estimated finish18:34:09.974, cast disappearance10.226,
use/damage11.066. Kember took901 versus party1531–1973, consistent with intended falloff;
no vulnerability. This reveals an approximately1.09s effect delay beyond cast finish,
longer than candidate1's0.5s geometry tail. Preserve this evidence for a bounded effect-tail
correction after the diagnostic run; do not credit final-behavior coverage until reviewed.
The observed next tank group started11.133; avoid early release into still-resolving cannon.
Run remains active through authored key/lift route, with later bosses still to capture.

## Candidate1 diagnostic completion — 2026-10-28 18:46 UTC

Paladin completed18:46:05.270, left normally18:46:11.912 and the completion watcher
requested its authorized stop18:46:14.822. Identity recheck: Kember, RB70576/game57744,
expected executable paths, alive/stopped Paladin in inn177, original SlideMover restored.
The protected XML/source/assembly for this attempt are the candidate1 hashes above;
source-only correction below was not loaded during gameplay. Ledger Temp/Keeper-PLD-Start.json
is DIAGNOSTIC_UNQUALIFIED, not a qualifying clear. No recorded player deaths, boss
vulnerability gains or AntiStuck entries. Three ownership acquisition/release pairs cover
all bosses. Authored key/barrier/lift flow and all four director objectives completed.

Akh Morn exposed an API-contract error: CapabilityManager.Update rejects the combined
Movement|GapCloser value despite its flags enum. There were63 coroutine exception records;
normal execution recovered on the next mechanic without manual rescue. Initial hit51.293
and follow-ups53.300/54.403/55.470 at18:44 corroborate four hits, but this does not qualify
stack positioning. Source now updates/clears each named capability separately. The detached
fake now rejects combinations and tracks each handle/capability pair; it reproduced the
failure before the fix and all59 checks pass afterward. Full runtime-reference compile
passes with13 existing unrelated warnings. Movement source SHA now
D787F149030C5E274238AEEB76F785763D83011468DBBB11E07D217857732D20; not yet loaded/published.

Gunship ownership18:38:10.538–18:39:39.745. Burns occurred on three fire passes. The first
pool appeared18:38:45.277 at(-7.330,-149.814), only6.66y from Kember; Burns followed45.413.
The next spawned46.520 at(3.128,-149.814) beneath the first escape endpoint. This is a
spawn-warning/route anticipation question, not evidence that a visible pool was ignored.
The boss had moved from center westward starting38.696, reached the edge around41.116,
then laid eastward pools about10.5y apart. Later passes were diagonal and northward.
Do not invent a generic center-to-edge avoid or use NPC destinations: review the captured
relocation/heading semantics before changing this mechanic. Existing object avoids escaped
the new pools and did not retain a wall-running command. Capture all pool instances and
boss heading/movement in the next observer if the current evidence cannot distinguish the cue.

Midgardsormr acquired18:42:30.312 and released18:46:05.270. Phantom line, inner and outer
branches and two ring sequences were observed without recorded vulnerability gain. The
last mirage pair was interrupted by boss death, so do not claim its full damage resolution.
Ring HP reductions around18:44:16–19 were logged dragon autoattacks, not proven ring hits.
Damage announcement latency is not itself proof of server snapshot timing: the first ring
was safely reentered before its Antipathy announcement. Therefore do not mechanically
extend every0.5s hazard tail based on announcement delay. Mini Cannon's falloff and late
announcement remain a conservative timing review item; no timing edit has been made.

Next: finish this focused fire/timing review, keep the exact failed evidence/settings,
then approved updater restart and same-Paladin retry from verified inn177. No role advance,
ratings or campaign completion. This is the first diagnostic attempt, below the help threshold.

## Candidate2 restart and first-boss boundary — 2026-10-28 18:59 UTC

Approved RestartOnly70576→6352/game57744 completed without standard-stop fallback,
plugin replacement, rollback or profile resume. Stopped state and inn177 were verified;
temporary hooks were disabled again. Both separate named capability calls passed against
the live runtime at the stopped boundary and were immediately cleared. Loaded assembly
DutyMechanic_1175501364.dll SHA294F2375646B8E9945A2CC810FF4B95CED88FE912F44E4FEFB742BB51902E409.
Only movement capability calls changed from candidate1; geometry/main/profile hashes match.
No fire forecast was added: a reliable warning cue is still uncertain. The read-only
observer now captures boss headings and per-instance visibility transitions to resolve it.
The instantaneous fire action and persistent pool semantics remain consistent with the
linked independent reference; a cast-bar trigger cannot be assumed.

Panda Farmer retry launched18:54 from verified inn177. Ledger Temp/Keeper-PLD-Retry1-Start.json,
log6352 2026-10-28 14.52.txt offset24925, watcher21840. Previous failed ledger retained.
Einhander ownership18:57:48.867–18:59:31.473; normal objective0 completion and route/SideStep
handoff. No player death, vulnerability gain, coroutine error, no-path or AntiStuck in the
reviewed retry range through18:59:54. Mini Cannon finish18:59:06.148, observed damage07.288
716 after block; no vulnerability. This corroborates acceptable falloff, without proving
the game's snapshot boundary from delayed chat. No broad hazard-tail edit is justified.
Run remains active toward the barrier/key route. Gunship fire anticipation and corrected
Akh Morn participation still require live review; no role credit yet. Incremental check
Temp/Keeper-Retry1-Check.ps1 now also reports failure counts so a truncated marker tail
cannot silently hide an earlier exception or death in the new range.

## Candidate2 Gunship warning evidence — 2026-10-28 19:05 UTC

Gunship ownership19:03:21.366–19:05:00.264; objective2 completed, original route/SideStep
handoff continued. No deaths/vulnerability/path errors, but Burns again preceded visible
pool avoidance. Capture now distinguishes initial damage from object presentation:

| Pass | Stable edge position X/Z and heading | Stable by | First pool visible | Interpretation |
| --- | --- | --- | --- | --- |
| West to east | -8.560/-149.828,1.5701 |19:03:39.915 |19:03:43.435 | About3.5s preparation; Burns43.234 before object first-seen43.300 and visible43.435. |
| Northwest to southeast | -3.586/-137.804,2.3562 |19:04:01.398 |19:04:04.315 | Roughly3s warning, diagonal trail corroborates heading. |
| North to south | 8.438/-132.830,3.1416 |19:04:33.341 |19:04:36.835 | Roughly3.5s preparation; source-object handling alone starts too late. |
| Immediate southwest follow-up | 8.438/-166.827,5.4979 |19:04:41.390 |19:04:44.918 | New heading at the endpoint predicts a chord, not a diameter through center. |

Pools become invisible about1.6–1.8s after visible, rather than remaining indefinitely.
The boss's edge pause and stable inward heading are stronger warning evidence than
the helper: helper relocation precedes initial damage by only about0.4s. Existing guide
corroborates edge-to-edge fire trails and random follow-up directions (linked above).
This supports a bounded scalar lane forecast from the observed edge pose, but it is not
implemented yet. Require preceding outward travel plus a stable noncasting edge pause;
do not trigger for every normal boss movement or infer a lane through room center.
Retain visible pools for recent floor, drop an old forecast as that pass ends/turns,
and hand off to the next independently observed pose. Native avoidance must choose the
escape; preserve the existing input owner. Replay all four captured directions, normal
center combat, turn handoff and stop/death/exit cleanup before another live correction.

The current unchanged retry continues to Midgardsormr for Akh Morn API/participation
verification. Do not silently call fire anticipation solved or credit final behavior.

## Candidate2 completion and verified stack correction — 2026-10-28 19:12 UTC

Normal completion19:11:27.814, exit19:11:34.406, watcher stop19:11:37.103. Full-log review:
zero player deaths, boss vulnerability gains, coroutine exceptions, no-path or AntiStuck;
three ownership pairs. Exact source/profile/assembly hashes still agree with the ledger.
Identity rechecked: RB6352/game57744 and expected executable paths, Kember alive/stopped
Paladin inn177, original SlideMover and SideStep enabled. No manual rescue or requeue.

Akh Morn now acquired05.864 and released17.220 at19:10, target1073802179 (not self).
Four hits11.301/13.369/14.408/15.482 dealt445/454/436/421 to Kember. Routine attacks
continued06.664,09.966,11.435,14.072 and17.356, corroborating shared participation and
no scheduling starvation. No capability exception. This verifies the API correction and
non-self stationary-stack branch; a self-marked/actual-travel branch still needs live coverage.
The final outer-phantom pair resolved before boss completion this time. Ring and phantom
branches remain clean in this capture.

Ledger Temp/Keeper-PLD-Retry1-Start.json is DIAGNOSTIC_PENDING_FIRE_QUALIFICATION. It meets
the numerical death/vulnerability limits but is not final-behavior certification while fire
anticipation is unfinished. Keep its settings/evidence and retry Paladin after that local
correction; do not advance WHM. Fire has had discovery/capture runs, not two unsuccessful
correction cycles. The new edge-pause cue supplies a concrete evidence-backed next approach.

## Candidate3 fire preparation — 2026-10-28 19:19 UTC

Implemented encounter-local scalar fire-trail prediction in KeeperOfTheLake.FireTrail.cs.
The actual observed heading defines the lane, including a non-diameter follow-up chord.
Gate: sampled fast outward travel (at least6.5y/s, radius at least12y, increasing radius),
then a noncasting15.5–18.5y edge pose stable for0.5s and facing inward. Five-second arming
window rejects unrelated stationary poses;100ms sampling avoids duplicate lifecycle/run
pulses. Fixed17y width is8y damage radius plus0.5y clearance each side. Extend8.5y behind
the initial pool and47y forward, clipped by existing arena bounds. Native avoidance still
owns routing, with the existing input adapter responsible for cleanup.

A turn/lateral departure after10y progress releases the old lane; existing pool circles
retain recent floor. Distinct casts and ownership loss reset preparation and geometry;
9s expiry bounds missed endpoint observations. No cast-tail, phantom, ring, stack, profile,
shared handler or frozen-mechanic behavior changed. Per-pass acquisition/release diagnostics
make early-warning timing reviewable in the next run.

Temp/KeeperMechanicChecks replays the actual candidate2 quarter-second trajectory from
GunshipCapture.csv. Four acquisitions, each more than2s before observed pool visibility,
four releases, scalar origin stability, all four direction geometries, stationary/ordinary
negative cases, expiry/cast interruption and stop/death/loading/exit/zone reset passed.
Captured flame-tail overlap and previous endpoint-pool overlap retain safe floor. All94
checks pass; full runtime-reference compilation passes with13 existing unrelated warnings.
These are detached checks, not a gameplay qualification. Source rationale reviewed in each
changed file. Approved restart plan is being prepared; no candidate3 queue yet.

Candidate3 loaded and queued19:22:20UTC after approved RestartOnly6352→57512/game57744.
No standard-stop fallback, plugin replacement, rollback or automatic profile resume.
Revalidated identity, executable paths/listener and alive Paladin inn177; temporary hooks
disabled and read-only observer/preload/setup completed. Exact hashes and current tools
are in DutyLevelingPassiveMonitoring.md's candidate3 override and Temp/Keeper-PLD-Retry2-Start.json.
Protected XML still matches canonical; new assembly exposes UpdateFireTrail. First live
fire-correction trial launched through Panda Farmer, watcher56836; no qualification yet.

## Candidate3 live fire verification — 2026-10-28 19:34 UTC

Gunship completed19:33:21.218 with ownership/SideStep restored and objective2 complete.
The four forecast acquisitions were19:31:55.504,19:32:18.413,19:32:50.993 and19:32:58.887.
Corresponding first visible pools appeared19:31:58.525,19:32:21.437,19:32:54.014 and
19:33:02.069: about3.0–3.2s of advance warning. These are visibility intervals, not claims
about damage snapshot timestamps. Fixed scalar lanes handled west/east, diagonal,
north/south and the immediate follow-up diagonal. Native escape cleanup was observed
at19:31:56.142,19:32:19.556 and19:32:52.104; the fourth pass already left Kember on safe
floor. No player Burns or vulnerability gain, no death, no-path or AntiStuck recorded.
The conjurer's Burns19:33:02.638 is explicitly NPC damage and is not attributed to Kember.
First two forecasts released at their turns; later phase/cast reset and encounter cleanup
removed the remaining state. Ordinary route movement resumed without manual rescue.

This is marked improvement over candidate2's initial-pool hit and verifies the warning
on this Paladin trajectory. Continue the same active attempt through Midgardsormr and
normal exit before role credit; other-role positioning and live stack branches remain open.

## Candidate3 Paladin qualified — 2026-10-28 19:41 UTC

Normal completion19:40:03.008, exit19:40:09.666, authorized watcher stop19:40:12.635.
Full attempt reviewed: zero player deaths, boss vulnerability gains, coroutine exceptions,
no-path or AntiStuck; three ownership pairs. Source, geometry, fire, movement, loaded DLL
and protected XML hashes match the ledger. Kember alive/stopped Paladin in inn177,
RB57512/game57744 and expected executable paths; SlideMover and SideStep restored.
No manual rescue. Ledger Temp/Keeper-PLD-Retry2-Start.json is PASS.

Four fire warnings and clean fire behavior are detailed above. Inner/line phantom overlaps
and ordered rings remained clean. Akh Morn acquired19:38:31.987, moved about2.1y toward
another marked participant, shared four hits37.373/39.475/40.543/41.677, then released43.387.
Routine attacks continued33.916,35.425,38.073,41.343 and42.816. This adds actual movement
handoff coverage; self-marked stack remains open for later roles. Prior candidate2's clean
outer phantom is retained as unchanged-geometry branch evidence, not a candidate2 role pass.
Two paralysis gains at19:34:35 and19:35:20 came from trash Levinfang between bosses under
SideStep; retain this distinction rather than attributing them to boss or fire behavior.

Advance to White Mage under active second-role verification from Limsa inn177. Keep failed
candidate ledgers/settings and all existing approvals. No rating or campaign completion.

White Mage candidate3 launched19:42:03UTC through Panda Farmer from verified inn177 after
native gearset15 selection. Fresh observer installation and protected prefetch completed.
Same exact candidate3 source/assembly/profile; ledger Temp/Keeper-WHM-Start.json records
log offset3181669 and watcher29132. Entry1063 confirmed19:42:08, alive/running WHM. Continue
active second-role verification; no White Mage credit yet. This role tests healer positioning
without changing the verified fire geometry or movement owner.

## White Mage first encounter boundary — 2026-10-28 19:47 UTC

Einhander completed19:47:24.554, objective0 and ownership release recorded together. Both
tank groups, line and proximity movement retained safe floor; native cleanup was followed
by normal Regen/Stone II casts. Player damage was Aero Blast1841/1876 and proximity1347
from the arena edge, with no recorded line/tank explosion hit, vulnerability or death.
No coroutine/no-path/AntiStuck markers through cursor4055337. Authored lift transition and
next barrier route resumed autonomously. This documents a healer movement/casting handoff;
Gunship, stack/self-target coverage and full completion are still pending on the active run.

## White Mage Gunship boundary — 2026-10-28 19:53 UTC

Gunship defeated19:53:11.869, objective2 complete and SideStep restored; authored upper
lift transition began17.708. No player damage or Burns recorded within the encounter,
no deaths/boss vulnerability/coroutine errors/no-path/AntiStuck through cursor4546936.
Four scalar warnings acquired19:51:54.698,19:52:16.145,19:52:50.022,19:52:58.077. First
visible pools58.092,19.733,52.910,19:53:00.933 give2.86–3.59s warning. All four lanes
released at observed pass ends. The fourth northbound chord from(20.46,-161.85) differs
from Paladin and corroborates heading-based geometry rather than a through-center guess.
Healer was already outside the fire lanes; do not claim those warnings forced a dodge.
Carpet Bomb did produce escape cleanup19:52:10.612 and32.042, followed by Stone II/Regen.
Add line/cone and flame continuation coexisted without route failures or suppressed casts.

This verifies ranged-position safety and live direction variation, not full-role credit.
Continue the same run through Midgardsormr stack/phantom/ring coverage and normal exit.

## Missing authored cleanup and White Mage navigation failure

Domestic identified that the removed ClearAvoidsOutOfCombat hook was an existing workaround
for post-avoid stalls. Candidate3 WHM stopped progressing at19:55:42.014 after trash Carpet
Bomb3482. SideStep removed the final avoid19:55:44.761; later snapshots showed zero avoids,
four dormant Keeper definitions, no combat and SlideMover, while repeated blocked-avoid
messages persisted at(-28.440243,636.2647,-18.056412). The monitor missed this message family;
prior healthy-path assertions are superseded. The ledger is DIAGNOSTIC_UNQUALIFIED_NAVIGATION.

Restored the authored profile hook unchanged (predicate and RemoveAllAvoids body), published
DWsWebserver6427cb3. Keeper's new owned-registration list reacquires after a global clear and
releases only its four definitions between fights. Without this compatibility change the
restored hook could delete the one-time registrations and leave later bosses unprotected.
100 checks and runtime-reference compile passed; no geometry or native input-owner changes.
Source/profile rationale reviewed. Between-fight workaround is not yet live-verified.

The inspected RB RemoveAllAvoids body does not directly reset the cached path; internal
ResetPath is a separate operation. Preserve this distinction for any upstream report.
No arbitrary reflection reset or new profile helper was added. Safely stopped and exited
failed attempt under existing recovery approval, leaving Kember alive WHM inn177. Continue
approved restart, exact runtime/protected agreement and same-role retry. Preserve all boss
successes as evidence, but never count the spoiled full run as a clear.

Recovery runtime is loaded after approved57512→67096 restart against game57744. Kember is
alive/stopped WHM inn177, setup/observer complete. Exact hashes are in the handoff. Protected
prefetch still returns old622C3D... XML after normal bridge cache refresh, so no retry queued.
Wait for canonical6427cb335ec9d4f716584d5c396a63f81cba9141 to materialize, then retry same role.
This preserves the user's workaround and requires proof of both between-fight hook execution
and subsequent boss registration reacquisition; compile success alone is not qualification.

Candidate4 same-WHM retry launched20:13:39UTC after restored protected XML became available.
Materialized1DA68006CC17963D969E045B5FAA1FDA.xml SHA D5D15BD15722E38870B8E931A8DBB30A12F5E0B4ACB981D68F35E810410451FA
matches canonical6427cb3 expanded XML exactly. Corrected DLL remains loaded; Kember verified
WHM15 inn177 before Panda launch, entry1063 at20:13:56. New ledger Temp/Keeper-WHM-Retry1-Start.json
records offset26729/watcher66724. Active lifecycle/navigation qualification pending; preserve
failed candidate3 and distinguish transient live-hazard waits from stale blocked paths.

Candidate4 Einhander completed20:19:02.565. Runtime read-only snapshot confirmed4/4 owned
AvoidInfo handles during combat, then0/0 with SideStep restored on the next trash section.
Damage review shows only Aero Blast1783/1848, no player death/vulnerability or tank/line hit.
Brief blocked-avoid messages during active hazards resolved autonomously; preserve their
context rather than treating every wait as the prior stale-path failure. Cursor843072.
Hook installation is verified, actual clearing execution and later reacquisition still
require live coverage. Continue active same-WHM verification; this is not a full clear.

## Candidate4 navigation verified; new fire overlap — 2026-10-28 20:32 UTC

Normal completion20:31:19.881 and exit/watcher stop20:31:26–29, alive WHM inn177.
Full log has zero player deaths/vulnerability gains/coroutine exceptions/AntiStuck.
Authored ClearAvoidsOutOfCombat executed20:26:45–20:27:17; previously stalled upper
route passed and final boss reacquired4/4 owned registrations. Phantom/ring sequences
and ally-target Akh Morn completed without recorded mechanic hits; self-target still open.
Keep this evidence, but ledger is DIAGNOSTIC_UNQUALIFIED_FIRE_OVERLAP, not a WHM clear.

Gunship's acquired follow-up warning31.053 was deleted when its living actor became
invisible32.394–34.006. Player re-entered upcoming pool floor and received Burns35.617,
1223damage35.684, before pool visibility35.752. Source-only correction preserves an
acquired scalar warning through that gap up to its unchanged9s expiry, excludes hidden
poses from acquisition/history, and retains visible-turn/death/cast/lifecycle cleanup.
110 detached checks and full compile pass. Source hash4386EE1A91D272DC24F0D42A24FA65A794010DC1D3ACA9EEB5422634B435F777;
loaded DLL remains candidate4 CC2FCF..., so this correction is not live-verified.

A separate overlap remains:1433 Drill Cannons heading4.069953 at near-end31.488,
remaining292ms, origin(15.457,-141.741). Fire escape moved from just outside its6y
rectangle at(0.011,-157.337) through the line at(0.180,-155.246), then2451damage32.998.
Geometry already refreshes heading each pulse. Review forecast urgency versus the
resolving line and native escape path; do not infer snapshot timing from damage display
or add another movement owner. No line/timing change implemented yet. Resolve that plan
before approved restart/exact agreement/same-WHM retry; preserve Paladin credit and all
failed evidence/settings. The handoff records exact log/identity/hash details.

## Candidate5 overlap correction prepared — 2026-10-28

The native RB escape cost map treats every High-priority hazard alike and finds a
path to any safe endpoint; the captured early fire escape crossed a still-resolving
cannon line. Keeper now temporarily replaces that earlier line with its unsafe-side
half-plane only when Kember starts outside the line but inside a not-yet-due fire
forecast. This preserves the current cannon-safe component while native avoidance
continues to select the actual path/input. It does not defer fire handling or choose
a manual destination. The plane spans the arena using origin-to-center distance plus
radius and1y clearance. Cannon expiry, forecast due time, escape completion and normal
lifecycle end the restriction. If already in the cannon, the cannon resolves later,
or no common-safe floor exists, retain the original native hazard union.

The captured opposing-side endpoint is excluded, eastward common-safe floor remains,
and mirrored-side/already-inside/no-floor/later-cannon/due-fire/expiry cases pass.
Together with visibility-gap replay and prior lifecycle/stack/ring tests:120 checks
and full runtime-reference compile passed. No profile, cast-tail, ring geometry,
movement controller or NPC-follow change. Candidate5 is awaiting approved restart
and exact runtime agreement, then same-WHM live proof. Candidate4 remains diagnostic.

Candidate5 Gunship boundary20:51:57.525UTC: objective2 complete, SideStep restored,
upper lift route resumed. No player damage/Burns in the complete20:50:18–20:51:57
encounter. Four forecast acquisitions20:50:27.993,20:51:01.313,22.825,30.809 released
at actual pass turns34.539,08.053,29.505,36.365. Native cleanup followed successful
fire/Carpet Bomb egress and routine casts. The1433 cannon this run began35.865 after
the last warning's preparation, so the new same-side branch did not execute. Keep
that branch open for later roles rather than falsely claiming this clean fight tested
it. Same active WHM attempt continues through upper route and final boss; no full clear yet.

## White Mage candidate5 qualified — 2026-10-28 20:59 UTC

PASS_WHM recorded before changing roles. Full exact-revision attempt completed20:58:45.880,
normal exit20:58:52.476 and watcher stop20:58:55.414. Kember alive/stopped WHM inn177;
source/profile/loaded assembly hashes still match ledger. Zero player deaths, boss
vulnerability gains, Burns, coroutine exceptions, AntiStuck or path-generation failures.
Gunship produced zero player damage over four fire forecasts. Upper-route authored
cleanup ran, final boss reacquired4/4 registrations, rings/phantoms and ally-target
Akh Morn completed cleanly. Original SlideMover restored, SideStep true, avoids/infos0.
Transient blocked waits during hazards/trash ended with continued movement/progress.

This clear did not trigger the rare early Drill/fire same-side branch or temporary
hidden-gunship branch; neither is claimed live-qualified. Self-marked stack remains
open too. Carry these into Viper/Black Mage evidence. Paladin+WHM now qualify for passive
recording per Domestic; review the next exact run after completion, return to active
diagnosis if any issue appears, and do not finish the dungeon without required coverage.

## Viper candidate5 qualified — 2026-10-28 21:22 UTC

PASS_VPR recorded before next role: completion21:17:04.127, normal exit21:17:10.708,
watcher stop21:17:13.381. Exact source/assembly/profile hashes agree. Kember alive/stopped
Viper inn177 RB77300/game57744, paths verified. Zero deaths, vulnerability gains, Burns,
coroutine exceptions, AntiStuck or path-generation failures. Three boss ownership pairs;
upper route passed and original SlideMover/SideStep restored with avoids/infos0.
Gunship had zero player damage across three warnings; final boss damage was raidwide/stack,
with clean rings/phantoms and all four Akh Morn hits shared on an ally-targeted stack.
Transient blocked waits resolved with later progress. Early Drill/fire side restriction
and self-marked stack did not trigger; retain open coverage for Black Mage.

## Final-behavior Paladin confirmation override — 2026-10-28 21:45 UTC

Black Mage candidate5 PASS_BLM recorded before changing roles: completion21:40:44.146,
normal exit21:40:50.739, watcher stop21:40:53.249. Exact source/profile/loaded DLL hashes
agreed. Zero player deaths/Burns/coroutine/AntiStuck/path-generation failures. One final
boss vulnerability1789 gain at21:39:48.295 meets the agreed maximum1 per boss; it is not
silently treated as either zero or a failed clear. Gunship produced no player damage
over four forecasts. Three ownership pairs, upper route, ally-target stack and normal
exit verified; original SlideMover/SideStep restored, avoids/infos0. Early Drill/fire,
already-acquired warning through invisibility and self-marked stack remain open.

Contract correction: earlier handoffs incorrectly carried candidate3 Paladin forward
through candidate4/5 behavior changes. DutyLevelingVerification.md requires all four
roles on final behavior. Preserve that historical clean evidence, but it does not
qualify the current build. No Keeper waiver was granted. WHM/VPR/BLM candidate5 qualify;
run Paladin candidate5 before certification, also capturing outstanding rare branches.
Do not advance Sohm Al or claim Keeper complete until final-role and coverage review.

Verified Kember alive/stopped in Limsa inn177, RB77300/game57744 and both executable
paths before gearset11; confirmed Paladin, reinstalled read-only observer, checked all
source/assembly/profile/wrapper hashes unchanged, then launched existing Panda wrapper.
Ledger Temp/Keeper-PLD-Final-Start.json, offset8836981 in77300 2026-10-28 16.39.txt;
watcher58388, helper Temp/Keeper-PLD-Final-Check.ps1 and matching watcher output files.
This confirmation preserves previous attempts/settings and uses the same movement owner.
Passive recording is appropriate after three qualifying candidate5 roles. Check once
per wake while healthy; review normal exit and rare branches promptly before credit.
Resume active diagnosis on issues under the agreed help threshold. Ratings unchanged.

## Four final-behavior role passes; coverage capture — 2026-10-28 22:09 UTC

Candidate5 Paladin PASS_PLD recorded: completion22:03:36.063, exit22:03:42.650,
watcher stop22:03:45.294. Exact source/assembly/profile/wrapper hashes agree. Kember
alive/stopped Paladin inn177, original SlideMover/SideStep restored, avoids0. Zero
player deaths/vulnerability gains/Burns, coroutine/AntiStuck/path-generation failures.
The one Burns record belongs to Scion conjurer. Gunship-phase308damage21:55:27.127
belongs to add Thunder; ordinary tank combat is not a fire failure. Three boss
ownership releases, upper route, ring/phantom progression and ally stack verified.

Acquired-warning invisibility coverage is now captured: warning acquired21:55:33.836,
living gunship base3688 invisible in samples38.713 through41.124, forecast released
only after visible return at41.257, before its original42.836 expiry. Kember remained
outside the fire lane and suffered no player Burns. This differs from Viper's hidden
samples after forecast release; retain the distinction. Early Drill/fire same-side
constraint and self-marked Akh Morn still did not trigger. All four final-behavior roles
now pass, but required branch coverage prevents final certification/publication closure.

Additional unchanged-candidate5 WHM coverage run selected because the earlier live
Drill/fire overlap was captured on WHM. This is a capture opportunity, not a guarantee
of a random branch, and does not erase existing role credit. Do not silently count
unobserved branches or loop indefinitely if the encounter never selects the player.
Review target selection evidence if this capture again only targets the support NPC.

Revalidated RB77300/game57744 and executable paths, alive/stopped inn177, changed to
gearset15 and confirmed WhiteMage. Reinstalled passive observer and launched existing
Panda wrapper. Ledger Temp/Keeper-WHM-Coverage1-Start.json; offset11825515 in77300
2026-10-28 16.39.txt; watcher69488; matching Check.ps1 and Watcher.out.txt/.err.txt.
Continue passive one-check-per-wake while healthy; diagnose actively if an issue arises.
Preserve temporary settings until campaign boundary, all failed evidence, authored hook
and unchanged ratings. Do not advance Sohm Al before required coverage is resolved.


## Coverage review and acceptance question — 2026-10-28 22:31 UTC

WHM-Coverage1 completed cleanly22:27:14.111, exit22:27:20.710, watcher standard stop
22:27:23.360 (bot thread ended23.438). Ledger CLEAN_CAPTURE_BRANCHES_UNOBSERVED.
Exact hashes agree; Kember alive/stopped WHM inn177 RB77300/game57744, original
SlideMover/SideStep restored, avoids0. Zero player deaths/vulnerability gains/Burns,
coroutine/AntiStuck/path-generation failures. The sole Burns line belongs to Scion
thaumaturge. Three ownership releases, upper-route cleanup and final progression passed.

Neither remaining branch triggered. Drill1433 started22:19:53.558 and resolved55.758,
after forecast49.171's preparation due51.671; Kember was outside both, so this was not
the earlier-cannon escape case. Akh Morn29283 at22:25:39.759 targeted NPC1073816322,
not Kember. All captured final-build roles targeted an NPC; this does NOT prove the
Duty Support encounter can never select the player. The upstream BossMod module
https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod.Modules/RealmReborn/Dungeon/D29TheKeeperoftheLake/D293Midgardsormr.cs
models icon305 and29283/29284 player stacks, but supplies no Duty Support target-selection
rule that would justify declaring the self-target branch unreachable. Detached tests
cover self-carrier participation, missing target, follow-up leases/native escape handoff,
and captured Drill/fire geometry/negative cases; these are not live branch captures.

Asked Domestic whether to accept the two explicitly documented live-test gaps after
four qualifying final-build roles plus this clean capture, or keep certification open.
This is a change to the accepted live-branch contract, not permission for ordinary
control. Await that answer; no blind requeue, certification, source-publication closure,
Sohm Al launch or rating change meanwhile. Keep all four role passes and failed evidence.
The normal-exit stop is authorized, not an explicit user stop. Settings remain staged
pending that short decision; restore appropriately if Domestic parks/stops the campaign.

## Accepted completion and publication scope — 2026-10-28 22:35 UTC

Domestic explicitly answered "Accept with documented gaps" to the two named exceptions:
early Drill/fire same-side correction and self-targeted Akh Morn. Keeper is COMPLETE
with four qualifying final-behavior roles and these two accepted live-test gaps. They
retain detached coverage; neither is represented as observed or impossible in gameplay.
The extra clean WHM capture and Paladin's hidden-gunship evidence remain recorded above.
This waiver applies only to these Keeper branches, not future dungeons or role standards.

Final source cleanup changes comments only: replace obsolete first-run instructions
with the observed lifecycle rationale and remove candidate-number shorthand. Geometry,
movement, timing and accepted gameplay behavior remain unchanged. Publication includes
only Keeper partials and its plugin lifecycle wiring; the published diagnostic switch
is false while the local developer build may retain true for subsequent captures.
The restored authored profile was already published as6427cb3 and needs no new edits.
Every published file's reason is documented beside its behavior or in this audit.
Ratings remain unchanged until the complete campaign passes. Next audit: Sohm Al.
